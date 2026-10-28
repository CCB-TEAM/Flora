using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.Framework.BlueprintAgent;

/// <summary>A content block of the model conversation, independent of the provider's wire format.</summary>
public sealed class ChatBlock
{
    public const string TextType = "text", ThinkingType = "thinking", RedactedThinkingType = "redacted_thinking", ToolUseType = "tool_use", ToolResultType = "tool_result";

    public string Type { get; set; } = TextType;
    public string Text { get; set; }

    /// <summary>Thinking: the signature that must come back unchanged. Redacted thinking: the opaque data.</summary>
    public string Signature { get; set; }
    public string Data { get; set; }

    /// <summary>Tool use / tool result: the call id.</summary>
    public string Id { get; set; }
    public string Name { get; set; }
    public JObject Input { get; set; }
    public bool IsError { get; set; }

    public static ChatBlock FromText(string text) => new() { Type = TextType, Text = text };
    public static ChatBlock ToolResult(string id, string content, bool isError) => new() { Type = ToolResultType, Id = id, Text = content, IsError = isError };
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "user";
    public List<ChatBlock> Blocks { get; set; } = [];

    public static ChatMessage User(params ChatBlock[] blocks) => new() { Role = "user", Blocks = [.. blocks] };
}

/// <summary>One line of the transcript the agent panel shows (and keeps with the conversation).</summary>
public sealed class TranscriptEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>user, assistant, tool, approval, compaction, info, error.</summary>
    public string Kind { get; set; } = "info";
    public string Text { get; set; } = "";
    public string Thinking { get; set; }
    public string Name { get; set; }
    public string Input { get; set; }
    public string Output { get; set; }

    /// <summary>tool: running / ok / error. approval: pending / allowed / denied.</summary>
    public string Status { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>A conversation with the agent: the model history (provider neutral), the transcript and its bookkeeping. Saved as JSON.</summary>
public sealed class AgentConversation
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
    public string Title { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public string Model { get; set; } = "";

    public List<ChatMessage> History { get; set; } = [];
    public List<TranscriptEntry> Transcript { get; set; } = [];

    /// <summary>Context carried over by a compaction that happened at the end of a turn; it starts the next user message.</summary>
    public string PendingContext { get; set; }

    /// <summary>Prompt tokens of the last request (the size of the context).</summary>
    public int ContextTokens { get; set; }
    public int Compactions { get; set; }

    /// <summary>The context size right after the last compaction (system prompt, tools and the summary): the part compaction can't shrink.</summary>
    public int BaselineTokens { get; set; }
    public bool SentFloraContext { get; set; }
}

/// <summary>Saved conversations in %AppData%\FModel\Agent\Sessions.</summary>
public static class AgentStore
{
    public static string Directory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel", "Agent", "Sessions");

    private static readonly JsonSerializerSettings Settings = new() { NullValueHandling = NullValueHandling.Ignore, Formatting = Formatting.None };

    public sealed record Summary(string Id, string Title, DateTime Updated, string Model);

    public static List<Summary> List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];
        var result = new List<Summary>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                // only the header fields are needed for the list
                using var reader = new JsonTextReader(new StreamReader(file));
                var json = JObject.Load(reader);
                result.Add(new Summary(json.Value<string>("Id") ?? Path.GetFileNameWithoutExtension(file), json.Value<string>("Title") ?? "",
                    json.Value<DateTime?>("Updated") ?? File.GetLastWriteTime(file), json.Value<string>("Model") ?? ""));
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Unreadable agent session {File}", file);
            }
        }
        return result.OrderByDescending(o => o.Updated).ToList();
    }

    public static AgentConversation Load(string id)
    {
        var file = PathOf(id);
        return File.Exists(file) ? JsonConvert.DeserializeObject<AgentConversation>(File.ReadAllText(file), Settings) : null;
    }

    public static void Save(AgentConversation conversation)
    {
        if (conversation.Transcript.Count == 0)
            return;
        System.IO.Directory.CreateDirectory(Directory);
        conversation.Updated = DateTime.Now;
        var file = PathOf(conversation.Id);
        File.WriteAllText(file + ".tmp", JsonConvert.SerializeObject(conversation, Settings));
        File.Move(file + ".tmp", file, true);
    }

    public static void Delete(string id)
    {
        var file = PathOf(id);
        if (File.Exists(file))
            File.Delete(file);
    }

    private static string PathOf(string id) => Path.Combine(Directory, string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')) + ".json");
}
