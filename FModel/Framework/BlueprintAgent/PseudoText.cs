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

    private static readonly Regex SignatureLine = new(@"^[ \t]*(public|private|protected)\s+[^\r\n(]*?\b(?<name>[A-Za-z_]\w*)\((?<args>[^\r\n]*)\)[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);

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
        var block = Functions(text).FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        return block == null ? null : text[block.Start..block.End];
    }

    /// <summary>How many statements (and classes) the decompiler gave up on, to say so before the text.</summary>
    public static (int Statements, int Classes) FailureCounts(string text) =>
        (Regex.Matches(text, FailureMarker + " in ").Count, Regex.Matches(text, "CLASS " + FailureMarker).Count);
}
