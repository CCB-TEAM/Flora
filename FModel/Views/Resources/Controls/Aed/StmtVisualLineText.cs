using System;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using CUE4Parse.Utils;
using FModel.Framework;
using ICSharpCode.AvalonEdit.Rendering;

namespace FModel.Views.Resources.Controls;

public class StmtVisualLineText : VisualLineText
{
    private readonly string _statementIndexText;

    public StmtVisualLineText(string statementIndexText, VisualLine parentVisualLine, int length) : base(parentVisualLine, length)
    {
        _statementIndexText = statementIndexText;
    }

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));

        var relativeOffset = startVisualColumn - VisualColumn;
        var text = context.GetText(
            context.VisualLine.FirstDocumentLine.Offset + RelativeTextOffset + relativeOffset,
            DocumentLength - relativeOffset);
        TextRunProperties.SetForegroundBrush(Brushes.LightSkyBlue);
        return new TextCharacters(text.Text, text.Offset, text.Count, TextRunProperties);
    }

    private bool IsClickable() => !string.IsNullOrEmpty(_statementIndexText) && Keyboard.Modifiers == ModifierKeys.None;

    protected override void OnQueryCursor(QueryCursorEventArgs e)
    {
        if (!IsClickable())
            return;
        e.Handled = true;
        e.Cursor = Cursors.Hand;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !IsClickable())
            return;
        if (e.Handled)
            return;

        if (!int.TryParse(_statementIndexText, out var statementIndex))
            return;

        var targetEditor = AvalonEditor.YesWeEditor;
        var targetDocument = targetEditor?.Document;
        if (targetDocument == null || targetDocument.TextLength <= 0)
            return;

        var match = Match.Empty;
        var pseudoDocument = ParentVisualLine.Document;
        var pseudoLine = ParentVisualLine.FirstDocumentLine.LineNumber;
        var functionName = TryFindEnclosingFunctionName(pseudoDocument, pseudoLine);
        if (!string.IsNullOrEmpty(functionName) &&
            TryFindStatementIndexWithinFunction(targetDocument.Text, functionName, statementIndex, out var functionScopedMatch))
        {
            match = functionScopedMatch;
        }

        if (!match.Success)
        {
            match = Regex.Match(
                targetDocument.Text,
                $"\"StatementIndex\"\\s*:\\s*{statementIndex}\\b",
                RegexOptions.CultureInvariant);
        }

        if (!match.Success)
        {
            var hasAnyStatementIndex = targetDocument.Text.Contains("\"StatementIndex\"", StringComparison.Ordinal);
            functionName ??= "<unknown>";
            FLogger.Append(ELog.Warning, () => FLogger.Text(
                $"Stmt click target not found in current JSON: StatementIndex={statementIndex}, Function={functionName}, HasAnyStatementIndex={hasAnyStatementIndex}",
                Constants.WHITE,
                true));
            return;
        }

        var line = targetDocument.GetLineByOffset(match.Index);
        targetEditor.Select(line.Offset, line.Length);
        targetEditor.ScrollToLine(line.LineNumber);
        targetEditor.TextArea.Caret.BringCaretToView();
        targetEditor.Focus();
        e.Handled = true;
    }

    protected override VisualLineText CreateInstance(int length)
    {
        return new StmtVisualLineText(_statementIndexText, ParentVisualLine, length);
    }

    private static string TryFindEnclosingFunctionName(ICSharpCode.AvalonEdit.Document.TextDocument doc, int startLine)
    {
        if (doc == null || doc.LineCount <= 0)
            return null;

        var signatureRegex = new Regex(@"^\s*(?:[\w:<>,~\[\]\*&]+\s+)+(?<name>[A-Za-z_]\w*)\s*\([^;]*\)\s*$",
            RegexOptions.CultureInvariant);

        for (var lineNumber = Math.Min(startLine, doc.LineCount); lineNumber >= 1; lineNumber--)
        {
            var line = doc.GetLineByNumber(lineNumber);
            var text = doc.GetText(line.Offset, line.Length).TrimEnd();
            if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal))
                continue;

            var match = signatureRegex.Match(text);
            if (match.Success && match.Groups.TryGetValue("name", out var group))
                return group.Value;
        }

        return null;
    }

    private static bool TryFindStatementIndexWithinFunction(string jsonText, string functionName, int statementIndex, out Match match)
    {
        match = Match.Empty;
        if (string.IsNullOrEmpty(jsonText) || string.IsNullOrEmpty(functionName))
            return false;

        var functionObjectMarker = $"\"ObjectName\": \"{functionName}\"";
        var statementMarkerRegex = new Regex($"\"StatementIndex\"\\s*:\\s*{statementIndex}\\b", RegexOptions.CultureInvariant);
        var searchStart = 0;

        while (true)
        {
            var functionPos = jsonText.IndexOf(functionObjectMarker, searchStart, StringComparison.Ordinal);
            if (functionPos < 0)
                return false;

            var nextFunctionPos = jsonText.IndexOf("\"ObjectName\": \"", functionPos + functionObjectMarker.Length, StringComparison.Ordinal);
            var segmentEnd = nextFunctionPos >= 0 ? nextFunctionPos : jsonText.Length;
            var segmentLength = segmentEnd - functionPos;
            if (segmentLength > 0)
            {
                var segment = jsonText.Substring(functionPos, segmentLength);
                var localMatch = statementMarkerRegex.Match(segment);
                if (localMatch.Success)
                {
                    match = statementMarkerRegex.Match(jsonText, functionPos + localMatch.Index);
                    return match.Success;
                }
            }

            searchStart = functionPos + functionObjectMarker.Length;
        }
    }
}
