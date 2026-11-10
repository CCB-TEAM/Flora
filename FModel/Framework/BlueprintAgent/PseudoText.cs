using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FModel.Framework.BlueprintAgent;

/// <summary>Navigates the pseudo C++ of a blueprint class: its functions, and the places the decompiler could not handle.</summary>
public static class PseudoText
{
    public sealed record Block(string Name, string Signature, int Start, int End, int FailedStatements)
    {
        public int Length => End - Start;
    }

    private static readonly Regex SignatureLine = new(@"^[ \t]*(public|private|protected)\s+[^\r\n(]*?\b(?<name>[A-Za-z_]\w*)\((?<args>[^\r\n]*)\)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.Compiled);

    public const string FailureMarker = "DECOMPILE FAILED";

    /// <summary>Every function of the text with its character range (leading comment lines included).</summary>
    public static List<Block> Functions(string text)
    {
        var blocks = new List<Block>();
        foreach (Match match in SignatureLine.Matches(text))
        {
            // the block opens on the next line that contains the brace
            var open = text.IndexOf('{', match.Index + match.Length);
            if (open < 0 || text[(match.Index + match.Length)..open].Trim().Length > 0)
                continue;

            var depth = 0;
            var end = open;
            for (; end < text.Length; end++)
            {
                if (text[end] == '{') depth++;
                else if (text[end] == '}' && --depth == 0) { end++; break; }
            }
            if (depth != 0)
                end = text.Length;

            var start = match.Index;
            // comment lines right above belong to the function (flags, category, tooltip)
            while (start > 0)
            {
                var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 2)) + 1;
                if (!text[lineStart..start].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    break;
                start = lineStart;
            }

            var body = text[start..end];
            blocks.Add(new Block(match.Groups["name"].Value, match.Value.Trim(), start, end, Regex.Matches(body, FailureMarker).Count));
        }
        return blocks;
    }

    /// <summary>The text of one function (the first of that name, case-insensitive), or null.</summary>
    public static string Extract(string text, string name)
    {
        var blocks = Functions(text);
        var block = blocks.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        if (block == null)
            return null;
        var code = text[block.Start..block.End];

        // An event is only a stub that jumps into the shared ubergraph: ExecuteUbergraph_X(<entry>). Append the ubergraph from that entry label
        // (the ubergraph is one flat list of labels and gotos, so the code after the label is followed by other events' code; read until its return).
        var stub = EventStub.Match(code);
        if (stub.Success)
        {
            var graph = blocks.FirstOrDefault(o => string.Equals(o.Name, stub.Groups["graph"].Value, StringComparison.OrdinalIgnoreCase));
            if (graph != null)
            {
                var body = text[graph.Start..graph.End];
                var entry = stub.Groups["entry"].Value;
                // labels are named by the statement offset: the entry is the offset of the first statement, which the ubergraph shows as Label_<n> or as an inline statement
                var label = Regex.Match(body, @"^[ \t]*Label_" + entry + @":[ \t]*\r?$", RegexOptions.Multiline);
                if (label.Success)
                {
                    var rest = body[label.Index..];
                    var limit = Math.Min(rest.Length, EventBodyLimit);
                    code += $"\n    // ---- ubergraph code from Label_{entry} (flat listing, follow the gotos; {rest.Length} chars follow, showing {limit}) ----\n" + rest[..limit];
                }
                else
                {
                    code += $"\n    // (entry {entry} is not a labelled statement in {graph.Name}; use get_graph for this event)";
                }
            }
        }
        return code;
    }

    private const int EventBodyLimit = 12000;

    private static readonly Regex EventStub = new(@"(?<graph>ExecuteUbergraph_\w+)\((?<entry>\d+)\)\s*;", RegexOptions.Compiled);

    /// <summary>Lines matching <paramref name="term"/> with <paramref name="context"/> lines around them, each group tagged with the function it is in.</summary>
    public static string Search(string text, string term, int context, int maxMatches, bool regex)
    {
        var matcher = regex ? new Regex(term, RegexOptions.IgnoreCase | RegexOptions.Compiled) : null;
        var lines = text.Split('\n');
        var starts = new int[lines.Length];
        for (int i = 0, at = 0; i < lines.Length; at += lines[i].Length + 1, i++)
            starts[i] = at;
        var blocks = Functions(text);

        var hits = new List<int>();
        for (var i = 0; i < lines.Length; i++)
            if (matcher != null ? matcher.IsMatch(lines[i]) : lines[i].Contains(term, StringComparison.OrdinalIgnoreCase))
                hits.Add(i);
        if (hits.Count == 0)
            return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{hits.Count} matching line(s)" + (hits.Count > maxMatches ? $", showing the first {maxMatches}" : "") + ":");
        var lastShown = -1;
        foreach (var hit in hits.Take(maxMatches))
        {
            var from = Math.Max(lastShown + 1, hit - context);
            var to = Math.Min(lines.Length - 1, hit + context);
            if (from > to)
                continue;
            var function = blocks.FirstOrDefault(o => starts[hit] >= o.Start && starts[hit] < o.End)?.Name ?? "(class level)";
            sb.AppendLine($"--- in {function}, line {hit + 1}");
            for (var i = from; i <= to; i++)
                sb.AppendLine((i == hit ? ">> " : "   ") + lines[i].TrimEnd('\r'));
            lastShown = to;
        }
        return sb.ToString();
    }

    /// <summary>How many statements (and classes) the decompiler gave up on, to say so before the text.</summary>
    public static (int Statements, int Classes) FailureCounts(string text) =>
        (Regex.Matches(text, FailureMarker + " in ").Count, Regex.Matches(text, "CLASS " + FailureMarker).Count);
}
