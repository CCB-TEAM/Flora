using System;
using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit.Rendering;

namespace FModel.Views.Resources.Controls;

public class StmtElementGenerator : VisualLineElementGenerator
{
    private readonly Regex _stmtRegex = new(
        @"\bstmt\s*[:=]\s*(?'target'\d+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private Match FindMatch(int startOffset)
    {
        var line = CurrentContext.VisualLine.FirstDocumentLine;
        var lineStart = line.Offset;
        var lineText = CurrentContext.Document.GetText(lineStart, line.Length);
        var relativeOffset = Math.Max(0, startOffset - lineStart);
        return _stmtRegex.Match(lineText, relativeOffset);
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var m = FindMatch(startOffset);
        if (!m.Success)
            return -1;

        var lineStart = CurrentContext.VisualLine.FirstDocumentLine.Offset;
        return lineStart + m.Index;
    }

    public override VisualLineElement ConstructElement(int offset)
    {
        var m = FindMatch(offset);
        if (!m.Success || !m.Groups.TryGetValue("target", out var group))
            return null;

        var lineStart = CurrentContext.VisualLine.FirstDocumentLine.Offset;
        if (lineStart + m.Index != offset)
            return null;

        return new StmtVisualLineText(group.Value, CurrentContext.VisualLine, m.Length);
    }
}
