using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static FModel.Framework.BlueprintAgent.Schema;

namespace FModel.Framework.BlueprintAgent;

/// <summary>
/// The flora-blueprint-bytecode skill shipped inside Flora: the built-in agent's system prompt, and what MCP clients read with
/// <c>read_guide</c> or as resources (clients other than Claude Code in the Flora repository can't load the skill itself).
/// </summary>
public static class AgentGuide
{
    private const string Prefix = "FModel.Agent.";

    private static readonly Lazy<Dictionary<string, string>> Documents = new(() =>
    {
        var assembly = typeof(AgentGuide).Assembly;
        var documents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames().Where(o => o.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var text = new StreamReader(stream).ReadToEnd();
            var key = name == Prefix + "SKILL.md" ? "skill" : Path.GetFileNameWithoutExtension(name[(Prefix + "reference.").Length..]);
            documents[key] = key == "skill" ? StripFrontMatter(text) : text;
        }
        return documents;
    });

    /// <summary>skill, then the references (edit-rules, kismet-objects, packing).</summary>
    public static IReadOnlyList<string> Topics => [.. Documents.Value.Keys.OrderBy(o => o == "skill" ? "" : o, StringComparer.Ordinal)];

    public static string Read(string topic) => Documents.Value.GetValueOrDefault(topic) ?? "";

    public static string Skill => Read("skill");

    /// <summary>The skill followed by every reference, as one text.</summary>
    public static string Full => Skill + string.Concat(Topics.Where(o => o != "skill").Select(o => $"\n\n<reference file=\"{o}.md\">\n{Read(o)}\n</reference>"));

    private static string StripFrontMatter(string text)
    {
        if (text.StartsWith("---", StringComparison.Ordinal) && text.IndexOf("\n---", 3, StringComparison.Ordinal) is var end and > 0)
            return text[(end + 4)..].TrimStart();
        return text;
    }

    public static AgentTool Tool => new()
    {
        Name = "read_guide",
        OffUiThread = true,
        Description = "Flora's guide for editing blueprints with these tools: the workflow, what each edit can and can't do, the bytecode object JSON format, and packing/localization. " +
                      "Read 'skill' before the first edit, and the reference that matches what you are about to do (kismet-objects before any kismet_* edit).",
        InputSchema = Object(("topic", Enum("Which document (default skill)", "skill", "edit-rules", "kismet-objects", "packing", "all"), false)),
        Run = (args, _) =>
        {
            var topic = args.Str("topic", false) ?? "skill";
            if (topic == "all")
                return System.Threading.Tasks.Task.FromResult(Full);
            var text = Read(topic);
            return System.Threading.Tasks.Task.FromResult(text.Length > 0 ? text : $"No topic '{topic}'. Topics: {string.Join(", ", Topics)}, all.");
        },
    };
}
