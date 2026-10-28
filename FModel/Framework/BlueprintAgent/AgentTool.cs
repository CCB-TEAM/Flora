using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace FModel.Framework.BlueprintAgent;

/// <summary>What a tool may do; the agent panel and the MCP server use it to gate calls.</summary>
public enum AgentToolAccess
{
    /// <summary>Only reads the loaded game and the open blueprints.</summary>
    Read,

    /// <summary>Records pending edits (reviewable, undoable, nothing is written).</summary>
    Edit,

    /// <summary>Bytecode object edits: only offered when the setting allows them.</summary>
    ObjectEdit,

    /// <summary>Writes files (an exported package, a pak, a project). The built-in agent asks the user first; MCP calls run directly.</summary>
    Write,
}

/// <summary>A tool offered to the language model, the same for the built-in agent and the MCP server.</summary>
public sealed class AgentTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JObject InputSchema { get; init; }
    public AgentToolAccess Access { get; init; }

    /// <summary>The tool touches nothing of the application and runs on the calling thread instead of the UI thread.</summary>
    public bool OffUiThread { get; init; }

    /// <summary>Runs the tool on the UI thread; returns text for the model (an <see cref="AgentToolException"/> is a failed call).</summary>
    public required Func<JObject, CancellationToken, Task<string>> Run { get; init; }
}

/// <summary>A tool call that failed in a way the model can fix (bad arguments, an edit the bytecode can't express...).</summary>
public sealed class AgentToolException(string message) : Exception(message);

/// <summary>Builds JSON schemas for tool inputs.</summary>
public static class Schema
{
    public static JObject Object(params (string Name, JObject Schema, bool Required)[] properties)
    {
        var props = new JObject();
        foreach (var (name, schema, _) in properties)
            props[name] = schema;
        var schemaObject = new JObject { ["type"] = "object", ["properties"] = props };
        var required = properties.Where(o => o.Required).Select(o => o.Name).ToArray();
        if (required.Length > 0)
            schemaObject["required"] = new JArray(required.Cast<object>().ToArray());
        return schemaObject;
    }

    public static JObject String(string description) => new() { ["type"] = "string", ["description"] = description };
    public static JObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };
    public static JObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };
    public static JObject Any(string description) => new() { ["description"] = description };

    public static JObject Enum(string description, params string[] values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JArray(values.Cast<object>().ToArray()) };

    public static JObject Array(string description, JObject items) => new() { ["type"] = "array", ["description"] = description, ["items"] = items };
}

public static class ArgumentExtensions
{
    public static string Str(this JObject args, string name, bool required = true)
    {
        var value = args[name];
        if (value == null || value.Type == JTokenType.Null)
            return required ? throw new AgentToolException($"'{name}' is required.") : null;
        return value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Newtonsoft.Json.Formatting.None);
    }

    public static int Int(this JObject args, string name, int fallback)
    {
        var value = args[name];
        if (value == null || value.Type == JTokenType.Null)
            return fallback;
        return value.Type switch
        {
            JTokenType.Integer => value.Value<int>(),
            JTokenType.String when int.TryParse(value.Value<string>(), out var parsed) => parsed,
            _ => throw new AgentToolException($"'{name}' must be an integer."),
        };
    }

    public static uint UInt(this JObject args, string name)
    {
        var value = args.Int(name, -1);
        return value >= 0 ? (uint) value : throw new AgentToolException($"'{name}' is required (a bytecode offset).");
    }

    public static bool Bool(this JObject args, string name, bool fallback)
    {
        var value = args[name];
        if (value == null || value.Type == JTokenType.Null)
            return fallback;
        return value.Type == JTokenType.Boolean ? value.Value<bool>() : bool.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
    }

    /// <summary>A JSON argument: the token itself, or JSON text the model sent as a string.</summary>
    public static JToken Json(this JObject args, string name)
    {
        var value = args[name] ?? throw new AgentToolException($"'{name}' is required.");
        if (value.Type != JTokenType.String)
            return value;
        try
        {
            return JToken.Parse(value.Value<string>()!);
        }
        catch (Exception e)
        {
            throw new AgentToolException($"'{name}' is not valid JSON: {e.Message}");
        }
    }

    public static IEnumerable<string> Strings(this JObject args, string name) =>
        args[name] is JArray array ? array.Select(o => o.ToString()) : args[name] is { Type: JTokenType.String } single ? [single.Value<string>()] : [];
}
