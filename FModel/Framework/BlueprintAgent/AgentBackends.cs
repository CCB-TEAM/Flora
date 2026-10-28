using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Newtonsoft.Json.Linq;

namespace FModel.Framework.BlueprintAgent;

public enum EAgentProvider
{
    /// <summary>Claude through the Anthropic API (official SDK).</summary>
    Anthropic,

    /// <summary>Any OpenAI-compatible chat completions endpoint (DeepSeek, Qwen, Ollama, LM Studio...).</summary>
    OpenAICompatible,
}

/// <summary>Receives a reply while it streams.</summary>
public interface IAgentStreamSink
{
    void Text(string chunk);
    void Thinking(string chunk);
    void ToolUseStarted(string id, string name);
}

/// <summary>A finished model reply: the assistant message to append to the history, why it stopped, and the token usage.</summary>
public sealed record AgentReply(ChatMessage Message, string StopReason, int ContextTokens, int OutputTokens, string Usage)
{
    public IEnumerable<ChatBlock> ToolCalls => Message.Blocks.Where(o => o.Type == ChatBlock.ToolUseType);
    public string Text => string.Concat(Message.Blocks.Where(o => o.Type == ChatBlock.TextType).Select(o => o.Text));
}

/// <summary>A model endpoint. Backends are stateless: the conversation owns the history and passes it on every request.</summary>
public interface IAgentBackend
{
    string Describe { get; }

    /// <param name="useTools">false: the tools stay declared (the history may reference them) but the model can't call them.</param>
    Task<AgentReply> StreamAsync(string system, IReadOnlyList<ChatMessage> history, IReadOnlyList<AgentTool> tools, bool useTools,
        IAgentStreamSink sink, CancellationToken cancellation);
}

/// <summary>The API key, encrypted for the current Windows user.</summary>
public static class SecretStore
{
    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret))
            return string.Empty;
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
            return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            return string.Empty; // another user's or machine's settings
        }
    }
}

/// <summary>Claude through the official Anthropic SDK (beta endpoint, streaming, for the server-side refusal fallback).</summary>
public sealed class AnthropicAgentBackend : IAgentBackend
{
    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly string _effort;
    private readonly bool _officialEndpoint;

    public AnthropicAgentBackend(string apiKey, string baseUrl, string model, string effort)
    {
        _client = string.IsNullOrWhiteSpace(baseUrl)
            ? new AnthropicClient { ApiKey = apiKey, Timeout = TimeSpan.FromMinutes(15) }
            : new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl.Trim(), Timeout = TimeSpan.FromMinutes(15) };
        _model = string.IsNullOrWhiteSpace(model) ? "claude-opus-5-5" : model.Trim();
        _effort = string.IsNullOrWhiteSpace(effort) ? "high" : effort.Trim().ToLowerInvariant();
        // proxies and other platforms may not know the fallback beta
        _officialEndpoint = string.IsNullOrWhiteSpace(baseUrl);
    }

    public string Describe => _model;

    public async Task<AgentReply> StreamAsync(string system, IReadOnlyList<ChatMessage> history, IReadOnlyList<AgentTool> tools, bool useTools,
        IAgentStreamSink sink, CancellationToken cancellation)
    {
        var toolParams = tools.Select((tool, index) =>
        {
            var schema = tool.InputSchema;
            var properties = (schema["properties"] as JObject ?? new JObject()).Properties()
                .ToDictionary(o => o.Name, o => JsonSerializer.Deserialize<JsonElement>(o.Value.ToString(Newtonsoft.Json.Formatting.None)));
            return (BetaToolUnion) new BetaTool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = new() { Properties = properties, Required = (schema["required"] as JArray)?.Select(o => o.ToString()).ToList() ?? [] },
                // large inputs (bytecode JSON) stream as they are written; the input is validated before the tool runs
                EagerInputStreaming = true,
                // the tool list is fixed for the conversation: cache it with the system prompt
                CacheControl = index == tools.Count - 1 ? new BetaCacheControlEphemeral() : null,
            };
        }).ToList();

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 64000,
            System = new List<BetaTextBlockParam> { new() { Text = system, CacheControl = new BetaCacheControlEphemeral() } },
            Tools = toolParams,
            ToolChoice = useTools ? new BetaToolChoiceAuto() : new BetaToolChoiceNone(),
            Messages = Render(history),
            Thinking = new BetaThinkingConfigAdaptive { Display = Display.Summarized },
            OutputConfig = new BetaOutputConfig { Effort = Enum.TryParse<Effort>(_effort, true, out var effort) ? effort : Effort.High },
        };
        if (_officialEndpoint)
        {
            // a policy decline is retried on Anthropic's recommended fallback model
            parameters = parameters with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
        }

        var blocks = new SortedDictionary<long, ChatBlock>();
        var partialJson = new Dictionary<long, StringBuilder>();
        var stopReason = "";
        long input = 0, cacheRead = 0, cacheWrite = 0, output = 0;

        await foreach (var streamEvent in _client.Beta.Messages.CreateStreaming(parameters, cancellation))
        {
            if (streamEvent.TryPickStart(out var start))
            {
                input = start.Message.Usage.InputTokens;
                cacheRead = start.Message.Usage.CacheReadInputTokens ?? 0;
                cacheWrite = start.Message.Usage.CacheCreationInputTokens ?? 0;
            }
            else if (streamEvent.TryPickContentBlockStart(out var blockStart))
            {
                var content = blockStart.ContentBlock;
                ChatBlock block = null;
                if (content.TryPickBetaText(out var text))
                    block = ChatBlock.FromText(text.Text ?? "");
                else if (content.TryPickBetaThinking(out var thinking))
                    block = new ChatBlock { Type = ChatBlock.ThinkingType, Text = thinking.Thinking ?? "", Signature = thinking.Signature ?? "" };
                else if (content.TryPickBetaRedactedThinking(out var redacted))
                    block = new ChatBlock { Type = ChatBlock.RedactedThinkingType, Data = redacted.Data };
                else if (content.TryPickBetaToolUse(out var toolUse))
                {
                    block = new ChatBlock { Type = ChatBlock.ToolUseType, Id = toolUse.ID, Name = toolUse.Name, Input = new JObject() };
                    partialJson[blockStart.Index] = new StringBuilder();
                    sink.ToolUseStarted(toolUse.ID, toolUse.Name);
                }
                if (block != null)
                    blocks[blockStart.Index] = block;
            }
            else if (streamEvent.TryPickContentBlockDelta(out var blockDelta) && blocks.TryGetValue(blockDelta.Index, out var current))
            {
                var delta = blockDelta.Delta;
                if (delta.TryPickText(out var text))
                {
                    current.Text += text.Text;
                    sink.Text(text.Text);
                }
                else if (delta.TryPickThinking(out var thinking))
                {
                    current.Text += thinking.Thinking;
                    sink.Thinking(thinking.Thinking);
                }
                else if (delta.TryPickSignature(out var signature))
                    current.Signature += signature.Signature;
                else if (delta.TryPickInputJson(out var json) && partialJson.TryGetValue(blockDelta.Index, out var builder))
                    builder.Append(json.PartialJson);
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                stopReason = messageDelta.Delta.StopReason?.ToString() ?? stopReason;
                output = messageDelta.Usage.OutputTokens;
            }
        }

        foreach (var (index, builder) in partialJson)
        {
            var block = blocks[index];
            var text = builder.ToString();
            try
            {
                block.Input = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
            }
            catch (Exception)
            {
                // eager input streaming doesn't validate the JSON: the tool reports it instead of running
                block.Input = new JObject { [AgentSession.InvalidInputKey] = text };
            }
        }

        var message = new ChatMessage { Role = "assistant", Blocks = blocks.Values.ToList() };
        var context = (int) (input + cacheRead + cacheWrite);
        return new AgentReply(message, stopReason, context, (int) output, $"in {context} (cache read {cacheRead}), out {output}");
    }

    private static List<BetaMessageParam> Render(IReadOnlyList<ChatMessage> history)
    {
        var messages = new List<BetaMessageParam>();
        for (var m = 0; m < history.Count; m++)
        {
            var message = history[m];
            // the last block of the conversation is a cache breakpoint: the next request reads the whole history from the cache
            var cacheLast = m == history.Count - 1;
            var blocks = new List<BetaContentBlockParam>();
            for (var b = 0; b < message.Blocks.Count; b++)
            {
                var block = message.Blocks[b];
                var cache = cacheLast && b == message.Blocks.Count - 1 ? new BetaCacheControlEphemeral() : null;
                switch (block.Type)
                {
                    case ChatBlock.TextType when !string.IsNullOrEmpty(block.Text):
                        blocks.Add(new BetaTextBlockParam { Text = block.Text, CacheControl = cache });
                        break;
                    case ChatBlock.ThinkingType:
                        blocks.Add(new BetaThinkingBlockParam { Thinking = block.Text ?? "", Signature = block.Signature ?? "" });
                        break;
                    case ChatBlock.RedactedThinkingType:
                        blocks.Add(new BetaRedactedThinkingBlockParam { Data = block.Data });
                        break;
                    case ChatBlock.ToolUseType:
                        blocks.Add(new BetaToolUseBlockParam
                        {
                            ID = block.Id, Name = block.Name,
                            Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>((block.Input ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None))!,
                        });
                        break;
                    case ChatBlock.ToolResultType:
                        blocks.Add(new BetaToolResultBlockParam { ToolUseID = block.Id, Content = block.Text ?? "", IsError = block.IsError, CacheControl = cache });
                        break;
                }
            }
            if (blocks.Count == 0)
                continue;
            messages.Add(message.Role == "assistant" ? new BetaAssistantMessageParam { Content = blocks } : new BetaUserMessageParam { Content = blocks });
        }
        return messages;
    }
}

/// <summary>An OpenAI-compatible <c>/chat/completions</c> endpoint with function calling, streamed (server-sent events).</summary>
public sealed class OpenAICompatibleAgentBackend : IAgentBackend
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _model;

    public OpenAICompatibleAgentBackend(string apiKey, string baseUrl, string model)
    {
        _apiKey = apiKey;
        var root = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim().TrimEnd('/');
        _endpoint = root.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? root : root + "/chat/completions";
        _model = model?.Trim() ?? "";
    }

    public string Describe => _model;

    public async Task<AgentReply> StreamAsync(string system, IReadOnlyList<ChatMessage> history, IReadOnlyList<AgentTool> tools, bool useTools,
        IAgentStreamSink sink, CancellationToken cancellation)
    {
        var body = new JObject
        {
            ["model"] = _model,
            ["stream"] = true,
            ["stream_options"] = new JObject { ["include_usage"] = true },
            ["messages"] = Render(system, history),
        };
        if (tools.Count > 0)
        {
            body["tools"] = new JArray(tools.Select(o => new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject { ["name"] = o.Name, ["description"] = o.Description, ["parameters"] = o.InputSchema },
            }));
            body["tool_choice"] = useTools ? "auto" : "none";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellation);
            throw new InvalidOperationException($"HTTP {(int) response.StatusCode}: {error[..Math.Min(error.Length, 600)]}");
        }

        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
        var finishReason = "";
        int prompt = 0, completion = 0;

        using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var data = line[5..].Trim();
            if (data == "[DONE]")
                break;
            JObject chunk;
            try { chunk = JObject.Parse(data); }
            catch (Newtonsoft.Json.JsonException) { continue; }

            if (chunk["usage"] is JObject usage && usage.HasValues)
            {
                prompt = usage.Value<int?>("prompt_tokens") ?? prompt;
                completion = usage.Value<int?>("completion_tokens") ?? completion;
            }
            // the last chunk carries only the usage, with no choices
            if (chunk["choices"] is not JArray { Count: > 0 } choices || choices[0] is not JObject choice)
                continue;
            if (choice.Value<string>("finish_reason") is { } finish)
                finishReason = finish;
            if (choice["delta"] is not JObject delta)
                continue;

            if (delta.Value<string>("reasoning_content") is { Length: > 0 } thought)
            {
                reasoning.Append(thought);
                sink.Thinking(thought);
            }
            if (delta.Value<string>("content") is { Length: > 0 } content)
            {
                text.Append(content);
                sink.Text(content);
            }
            foreach (var call in delta["tool_calls"] as JArray ?? [])
            {
                var index = call.Value<int?>("index") ?? 0;
                if (!calls.TryGetValue(index, out var entry))
                {
                    entry = (call.Value<string>("id") ?? Guid.NewGuid().ToString("N"), "", new StringBuilder());
                    calls[index] = entry;
                }
                if (call["function"]?.Value<string>("name") is { Length: > 0 } name)
                {
                    entry.Name += name;
                    calls[index] = entry;
                    sink.ToolUseStarted(entry.Id, entry.Name);
                }
                if (call["function"]?.Value<string>("arguments") is { } arguments)
                    entry.Arguments.Append(arguments);
            }
        }

        var message = new ChatMessage { Role = "assistant" };
        if (reasoning.Length > 0)
            message.Blocks.Add(new ChatBlock { Type = ChatBlock.ThinkingType, Text = reasoning.ToString() });
        if (text.Length > 0)
            message.Blocks.Add(ChatBlock.FromText(text.ToString()));
        foreach (var (_, call) in calls)
        {
            JObject input;
            var arguments = call.Arguments.ToString();
            try { input = string.IsNullOrWhiteSpace(arguments) ? new JObject() : JObject.Parse(arguments); }
            catch (Exception) { input = new JObject { [AgentSession.InvalidInputKey] = arguments }; }
            message.Blocks.Add(new ChatBlock { Type = ChatBlock.ToolUseType, Id = call.Id, Name = call.Name, Input = input });
        }
        return new AgentReply(message, finishReason, prompt, completion, $"in {prompt}, out {completion}");
    }

    private static JArray Render(string system, IReadOnlyList<ChatMessage> history)
    {
        var messages = new JArray { new JObject { ["role"] = "system", ["content"] = system } };
        // reasoning goes back only within the current turn (after the last user text), as reasoning models with tools expect
        var lastUserText = -1;
        for (var i = 0; i < history.Count; i++)
            if (history[i].Role == "user" && history[i].Blocks.Any(o => o.Type == ChatBlock.TextType))
                lastUserText = i;

        for (var i = 0; i < history.Count; i++)
        {
            var message = history[i];
            if (message.Role == "assistant")
            {
                var assistant = new JObject { ["role"] = "assistant", ["content"] = string.Concat(message.Blocks.Where(o => o.Type == ChatBlock.TextType).Select(o => o.Text)) };
                if (i > lastUserText && message.Blocks.FirstOrDefault(o => o.Type == ChatBlock.ThinkingType) is { } thinking)
                    assistant["reasoning_content"] = thinking.Text;
                var calls = message.Blocks.Where(o => o.Type == ChatBlock.ToolUseType).ToList();
                if (calls.Count > 0)
                    assistant["tool_calls"] = new JArray(calls.Select(o => new JObject
                    {
                        ["id"] = o.Id, ["type"] = "function",
                        ["function"] = new JObject { ["name"] = o.Name, ["arguments"] = (o.Input ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None) },
                    }));
                messages.Add(assistant);
                continue;
            }

            foreach (var result in message.Blocks.Where(o => o.Type == ChatBlock.ToolResultType))
                messages.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = result.Id, ["content"] = result.IsError ? "ERROR: " + result.Text : result.Text });
            var userText = string.Join("\n\n", message.Blocks.Where(o => o.Type == ChatBlock.TextType).Select(o => o.Text));
            if (userText.Length > 0)
                messages.Add(new JObject { ["role"] = "user", ["content"] = userText });
        }
        return messages;
    }
}
