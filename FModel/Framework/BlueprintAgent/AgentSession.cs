using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FModel.Settings;
using Newtonsoft.Json.Linq;

namespace FModel.Framework.BlueprintAgent;

/// <summary>
/// The built-in agent: a conversation with the configured model that drives the agent tools, streams its replies,
/// asks the user before tools write files, saves itself after every step and compacts its own context when it grows too large
/// (the model writes a summary of the conversation, Flora adds the current state of the blueprints and the pak, and the summary replaces the history).
/// </summary>
public sealed class AgentSession
{
    public const string InvalidInputKey = "__invalid_json";
    private const int MaxTurns = 60;

    private readonly Dictionary<string, TaskCompletionSource<bool>> _approvals = new();

    public AgentConversation Conversation { get; }
    public bool IsRunning { get; private set; }

    /// <summary>(kind, payload): "entry" a new transcript entry, "patch" changed fields of one, "delta" streamed text, "status".</summary>
    public event Action<string, JObject> Changed;

    public AgentSession(AgentConversation conversation = null)
    {
        Conversation = conversation ?? new AgentConversation();
    }

    public static int CompactThreshold => UserSettings.Default.AgentCompactAtTokens;

    private static IAgentBackend CreateBackend()
    {
        var settings = UserSettings.Default;
        var key = settings.AgentApiKey;
        return settings.AgentProvider switch
        {
            EAgentProvider.OpenAICompatible => new OpenAICompatibleAgentBackend(key, settings.AgentBaseUrl, settings.AgentModel),
            _ => string.IsNullOrEmpty(key)
                ? throw new InvalidOperationException("No API key is set (agent settings).")
                : new AnthropicAgentBackend(key, settings.AgentBaseUrl, settings.AgentModel, settings.AgentEffort),
        };
    }

    #region running

    /// <summary>Sends a user message and runs tool calls until the model answers without one.</summary>
    public async Task SendAsync(string text, CancellationToken cancellation)
    {
        if (IsRunning)
            throw new InvalidOperationException("The agent is already running.");
        SetRunning(true);
        try
        {
            var backend = CreateBackend();
            Conversation.Model = backend.Describe;
            Add(new TranscriptEntry { Kind = "user", Text = text });
            if (string.IsNullOrEmpty(Conversation.Title))
                Conversation.Title = text.Length > 48 ? text[..48].Trim() + "…" : text.Trim();

            var prefix = new StringBuilder();
            if (Conversation.PendingContext is { } carried)
            {
                prefix.AppendLine(carried).AppendLine();
                Conversation.PendingContext = null;
            }
            if (!Conversation.SentFloraContext)
            {
                // volatile context goes in the conversation, not the cached system prompt
                prefix.AppendLine(FloraContextLine()).AppendLine();
                Conversation.SentFloraContext = true;
            }
            Conversation.History.Add(ChatMessage.User(ChatBlock.FromText(prefix + text)));
            await RunLoopAsync(backend, cancellation);
        }
        catch (OperationCanceledException)
        {
            Add(new TranscriptEntry { Kind = "info", Text = "Stopped." });
            RepairHistory();
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Agent request failed");
            Add(new TranscriptEntry { Kind = "error", Text = e.Message });
            RepairHistory();
        }
        finally
        {
            CancelApprovals();
            SetRunning(false);
            AgentStore.Save(Conversation);
        }
    }

    private async Task RunLoopAsync(IAgentBackend backend, CancellationToken cancellation)
    {
        for (var turn = 0; turn < MaxTurns; turn++)
        {
            cancellation.ThrowIfCancellationRequested();
            var reply = await StreamTurnAsync(backend, cancellation);
            Conversation.History.Add(reply.Message);
            Conversation.ContextTokens = reply.ContextTokens;
            if (Conversation.BaselineTokens == 0)
                Conversation.BaselineTokens = reply.ContextTokens;
            RaiseStatus();

            var calls = reply.ToolCalls.ToList();
            if (calls.Count == 0)
            {
                if (reply.StopReason.Contains("refusal", StringComparison.OrdinalIgnoreCase))
                    Add(new TranscriptEntry { Kind = "error", Text = "The model declined this request." });
                Add(new TranscriptEntry { Kind = "info", Text = $"{reply.StopReason}  ·  {reply.Usage}" });
                if (ShouldCompact(0))
                    await CompactAsync(backend, false, cancellation);
                return;
            }

            var truncated = reply.StopReason.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) || reply.StopReason == "length";
            var results = new List<ChatBlock>();
            foreach (var call in calls)
                results.Add(await RunToolAsync(call, truncated, cancellation));
            Conversation.History.Add(new ChatMessage { Role = "user", Blocks = results });
            AgentStore.Save(Conversation);

            // the tool results are about to be sent: compact first when they would overflow the budget
            if (ShouldCompact(results.Sum(o => o.Text?.Length ?? 0) / 3))
                await CompactAsync(backend, true, cancellation);
        }
        Add(new TranscriptEntry { Kind = "error", Text = $"Stopped after {MaxTurns} model turns; send a message to continue." });
    }

    /// <summary>Streams one model reply into a new assistant entry of the transcript.</summary>
    private async Task<AgentReply> StreamTurnAsync(IAgentBackend backend, CancellationToken cancellation, bool useTools = true, TranscriptEntry entry = null)
    {
        entry ??= Add(new TranscriptEntry { Kind = "assistant", Status = "streaming" });
        var sink = new Sink(this, entry);
        var reply = await backend.StreamAsync(SystemPrompt.Value, Conversation.History, BlueprintAgentTools.Available().ToList(), useTools, sink, cancellation);
        entry.Text = reply.Text.Trim();
        entry.Thinking = string.Concat(reply.Message.Blocks.Where(o => o.Type == ChatBlock.ThinkingType).Select(o => o.Text));
        entry.Status = "done";
        Patch(entry, new JObject { ["text"] = entry.Text, ["thinking"] = entry.Thinking, ["status"] = entry.Status });
        return reply;
    }

    private async Task<ChatBlock> RunToolAsync(ChatBlock call, bool truncated, CancellationToken cancellation)
    {
        var input = call.Input ?? new JObject();
        var entry = Add(new TranscriptEntry { Kind = "tool", Name = call.Name, Input = input.ToString(Newtonsoft.Json.Formatting.Indented), Status = "running" });

        string output;
        bool isError;
        if (input[InvalidInputKey] is { } broken)
        {
            (output, isError) = ($"INVALID_JSON: the arguments of this call are not valid JSON{(truncated ? " (the reply was cut off by the output limit; send smaller inputs)" : "")}. " +
                                 $"Call the tool again. Received: {broken.ToString()[..Math.Min(broken.ToString().Length, 300)]}", true);
        }
        else
        {
            // tool calls of this session ask for approval in the panel instead of a message box
            BlueprintAgentHost.ConfirmOverride.Value = message => RequestApprovalAsync(call.Name, message, cancellation);
            try
            {
                (output, isError) = await BlueprintAgentTools.RunAsync(call.Name, input, cancellation);
            }
            finally
            {
                BlueprintAgentHost.ConfirmOverride.Value = null;
            }
        }

        entry.Output = output;
        entry.Status = isError ? "error" : "ok";
        Patch(entry, new JObject { ["output"] = output, ["status"] = entry.Status });
        return ChatBlock.ToolResult(call.Id, output, isError);
    }

    /// <summary>After a failure the history may end with tool calls that have no results; answer them so the next request is valid.</summary>
    private void RepairHistory()
    {
        if (Conversation.History.LastOrDefault() is { Role: "assistant" } last && last.Blocks.Any(o => o.Type == ChatBlock.ToolUseType))
        {
            Conversation.History.Add(new ChatMessage
            {
                Role = "user",
                Blocks = last.Blocks.Where(o => o.Type == ChatBlock.ToolUseType).Select(o => ChatBlock.ToolResult(o.Id, "Not run: the user stopped the agent.", true)).ToList(),
            });
        }
        // an empty or failed assistant turn is dropped
        if (Conversation.History.LastOrDefault() is { Role: "assistant" } empty && empty.Blocks.Count == 0)
            Conversation.History.RemoveAt(Conversation.History.Count - 1);
    }

    #endregion

    #region approvals

    private async Task<bool> RequestApprovalAsync(string tool, string message, CancellationToken cancellation)
    {
        var entry = Add(new TranscriptEntry { Kind = "approval", Name = tool, Text = message, Status = "pending" });
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_approvals)
            _approvals[entry.Id] = completion;
        bool allowed;
        await using (cancellation.Register(() => completion.TrySetResult(false)))
            allowed = await completion.Task;
        lock (_approvals)
            _approvals.Remove(entry.Id);
        entry.Status = allowed ? "allowed" : "denied";
        Patch(entry, new JObject { ["status"] = entry.Status });
        return allowed;
    }

    public void ResolveApproval(string id, bool allow)
    {
        TaskCompletionSource<bool> completion;
        lock (_approvals)
            _approvals.TryGetValue(id, out completion);
        completion?.TrySetResult(allow);
    }

    private void CancelApprovals()
    {
        lock (_approvals)
            foreach (var completion in _approvals.Values)
                completion.TrySetResult(false);
    }

    #endregion

    #region compaction

    /// <summary>
    /// Whether the context should be compacted before the next request. It must have grown well past what a compaction leaves
    /// (system prompt, tools, summary) and have new turns in it, or a low threshold would compact after every turn.
    /// </summary>
    private bool ShouldCompact(int pendingTokens)
    {
        var size = Conversation.ContextTokens + pendingTokens;
        return CompactThreshold > 0 && size >= CompactThreshold && Conversation.History.Count >= 4 &&
               size - Conversation.BaselineTokens >= CompactThreshold / 4;
    }

    /// <summary>Compacts the context now (the /compact command).</summary>
    public async Task CompactNowAsync(CancellationToken cancellation)
    {
        if (IsRunning)
            throw new InvalidOperationException("The agent is running.");
        if (Conversation.History.Count == 0)
            return;
        SetRunning(true);
        try
        {
            await CompactAsync(CreateBackend(), false, cancellation);
        }
        catch (OperationCanceledException)
        {
            Add(new TranscriptEntry { Kind = "info", Text = "Compaction stopped." });
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Compaction failed");
            Add(new TranscriptEntry { Kind = "error", Text = "Compaction failed: " + e.Message });
        }
        finally
        {
            SetRunning(false);
            AgentStore.Save(Conversation);
        }
    }

    /// <summary>
    /// The model summarizes the conversation for itself; Flora appends the current state from its tools; the result replaces the history.
    /// <paramref name="continueTask"/>: the agent is in the middle of a task and goes on right after.
    /// </summary>
    private async Task CompactAsync(IAgentBackend backend, bool continueTask, CancellationToken cancellation)
    {
        var before = Conversation.ContextTokens;
        var entry = Add(new TranscriptEntry { Kind = "compaction", Status = "streaming", Text = "" });

        // the instruction joins the last user message (tool results or text): roles keep alternating
        var request = Conversation.History.Select(o => new ChatMessage { Role = o.Role, Blocks = [.. o.Blocks] }).ToList();
        if (request.Count > 0 && request[^1].Role == "user")
            request[^1].Blocks.Add(ChatBlock.FromText(CompactionPrompt));
        else
            request.Add(ChatMessage.User(ChatBlock.FromText(CompactionPrompt)));

        var sink = new Sink(this, entry);
        var reply = await backend.StreamAsync(SystemPrompt.Value, request, BlueprintAgentTools.Available().ToList(), false, sink, cancellation);
        var summary = reply.Text.Trim();
        if (summary.Length < 40)
            throw new InvalidOperationException("The model returned no usable summary; the context was left as it was.");

        var state = await FloraStateAsync(cancellation);
        var carried = $"[This conversation was compacted to save context. The summary below was written by you from the earlier conversation; " +
                      $"Flora's current state is appended from its tools and is authoritative.]\n\n<summary>\n{summary}\n</summary>\n\n<flora-state>\n{state}\n</flora-state>";

        Conversation.History.Clear();
        Conversation.Compactions++;
        if (continueTask)
            Conversation.History.Add(ChatMessage.User(ChatBlock.FromText(carried + "\n\nContinue the task from where you left off. Don't redo finished steps; re-read graphs before editing if node ids are needed.")));
        else
            Conversation.PendingContext = carried;
        Conversation.SentFloraContext = true;
        Conversation.ContextTokens = carried.Length / 3;
        // measured again by the next request
        Conversation.BaselineTokens = 0;

        entry.Text = summary;
        entry.Status = "done";
        entry.Output = $"{before} → ~{Conversation.ContextTokens} tokens";
        Patch(entry, new JObject { ["text"] = summary, ["status"] = "done", ["output"] = entry.Output });
        RaiseStatus();
    }

    private const string CompactionPrompt =
        "[Flora: context compaction] The conversation is about to be replaced by your summary because the context is getting large. " +
        "Do not call tools. Write the summary you will need to continue this work without the conversation above. Use these sections:\n" +
        "1. User requests - every request and constraint, quoting the user's own words for instructions and preferences.\n" +
        "2. Current task - what is being done right now and the exact next step.\n" +
        "3. Facts - blueprint/package paths, functions, node and pin ids, bytecode offsets and tokens, class defaults, .locres paths and keys, pak paths, jmap signatures: only what is still relevant, with exact names and values.\n" +
        "4. Edits - each edit made (pending or saved) with old and new values, and validation results.\n" +
        "5. Problems - errors, refused edits and why, approaches already tried.\n" +
        "6. Next steps.\n" +
        "Be precise and dense; lists, not prose. Write in the language the user writes in.";

    /// <summary>What Flora knows for sure right now: open blueprints, their pending edits and the pak project.</summary>
    private static async Task<string> FloraStateAsync(CancellationToken cancellation)
    {
        var sb = new StringBuilder();
        async Task Append(string tool, JObject args)
        {
            var (text, isError) = await BlueprintAgentTools.RunAsync(tool, args, cancellation);
            if (!isError)
                sb.AppendLine($"## {tool}{(args.HasValues ? " " + args.ToString(Newtonsoft.Json.Formatting.None) : "")}").AppendLine(text.Trim()).AppendLine();
        }

        await Append("list_blueprints", new JObject());
        foreach (var viewer in BlueprintAgentHost.Open)
            await Append("list_pending_edits", new JObject { ["blueprint"] = viewer.Entry.Path });
        if (Docking.DockWorkspace.IsOpen(Docking.DockPanels.PackWorkspaceId))
            await Append("pack_status", new JObject { ["limit"] = 60 });
        return sb.ToString().Trim();
    }

    #endregion

    #region transcript

    private sealed class Sink(AgentSession session, TranscriptEntry entry) : IAgentStreamSink
    {
        public void Text(string chunk) => session.Delta(entry, "text", chunk);
        public void Thinking(string chunk) => session.Delta(entry, "thinking", chunk);
        public void ToolUseStarted(string id, string name) { }
    }

    private TranscriptEntry Add(TranscriptEntry entry)
    {
        Conversation.Transcript.Add(entry);
        Changed?.Invoke("entry", JObject.FromObject(entry, AgentJson.Serializer));
        return entry;
    }

    private void Patch(TranscriptEntry entry, JObject fields)
    {
        fields["id"] = entry.Id;
        Changed?.Invoke("patch", fields);
    }

    private void Delta(TranscriptEntry entry, string field, string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;
        Changed?.Invoke("delta", new JObject { ["id"] = entry.Id, ["field"] = field, ["text"] = chunk });
    }

    private void SetRunning(bool running)
    {
        IsRunning = running;
        RaiseStatus();
    }

    public void RaiseStatus() => Changed?.Invoke("status", Status());

    public JObject Status() => new()
    {
        ["running"] = IsRunning,
        ["contextTokens"] = Conversation.ContextTokens,
        ["compactAt"] = CompactThreshold,
        ["compactions"] = Conversation.Compactions,
        ["model"] = Conversation.Model,
        ["title"] = Conversation.Title,
        ["id"] = Conversation.Id,
    };

    #endregion

    private static string FloraContextLine()
    {
        var open = BlueprintAgentHost.Open.Select(o => o.Entry.Path).ToList();
        string game = null;
        var jmap = false;
        try
        {
            var cue4Parse = Services.ApplicationService.ApplicationView.CUE4Parse;
            game = cue4Parse.Provider?.ProjectName;
            jmap = Mappings.BlueprintGraphContextFactory.GetJmaps(cue4Parse.JmapPath).Count > 0;
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "No game state for the agent context");
        }
        return $"[Flora: game {(string.IsNullOrEmpty(game) ? "not loaded" : game)}; open blueprints: {(open.Count == 0 ? "none" : string.Join(", ", open))}; " +
               $"bytecode object tools {(UserSettings.Default.AgentAllowBytecodeObjects ? "enabled" : "disabled")}; jmap {(jmap ? "loaded" : "not loaded")}]";
    }

    /// <summary>The skill without its front matter, followed by its references.</summary>
    public static readonly Lazy<string> SystemPrompt = new(() =>
    {
        var assembly = typeof(AgentSession).Assembly;
        string Read(string name)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            return stream == null ? "" : new StreamReader(stream).ReadToEnd();
        }

        return "You are the blueprint agent built into Flora, an Unreal Engine archive explorer. You edit compiled blueprints, game text and pak files " +
               "through the tools below; the guide and its references are included here, so read_guide is not needed. " +
               "Reply in the language the user writes in.\n\n" + AgentGuide.Full;
    });
}

/// <summary>JSON settings for what goes to the agent panel (camelCase, like the web code expects).</summary>
public static class AgentJson
{
    public static readonly Newtonsoft.Json.JsonSerializer Serializer = Newtonsoft.Json.JsonSerializer.Create(new Newtonsoft.Json.JsonSerializerSettings
    {
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
    });
}
