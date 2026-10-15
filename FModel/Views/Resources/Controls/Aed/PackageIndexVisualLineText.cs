using System;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace FModel.Views.Resources.Controls;

public class PackageIndexVisualLineText : VisualLineText
{
    public delegate void PackageIndexOnClick(string packageIndex);

    public event PackageIndexOnClick OnPackageIndexClicked;
    private readonly string _packageIndex;

    public PackageIndexVisualLineText(string packageIndex, VisualLine parentVisualLine, int length) : base(parentVisualLine, length)
    {
        _packageIndex = packageIndex;
    }

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));

        var relativeOffset = startVisualColumn - VisualColumn;
        var text = context.GetText(
            context.VisualLine.FirstDocumentLine.Offset + RelativeTextOffset + relativeOffset,
            DocumentLength - relativeOffset);
        TextRunProperties.SetForegroundBrush(Brushes.LightGreen);
        return new TextCharacters(text.Text, text.Offset, text.Count, TextRunProperties);
    }

    private bool IndexIsClickable() => !string.IsNullOrEmpty(_packageIndex) && Keyboard.Modifiers == ModifierKeys.None;

    protected override void OnQueryCursor(QueryCursorEventArgs e)
    {
        if (!IndexIsClickable())
            return;
        e.Handled = true;
        e.Cursor = Cursors.Hand;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !IndexIsClickable())
            return;
        if (e.Handled || OnPackageIndexClicked == null)
            return;

        OnPackageIndexClicked(_packageIndex);
        e.Handled = true;
    }

    protected override VisualLineText CreateInstance(int length)
    {
        var instance = new PackageIndexVisualLineText(_packageIndex, ParentVisualLine, length);
        instance.OnPackageIndexClicked += packageIndexText =>
        {
            if (!int.TryParse(packageIndexText, out var packageIndex) || packageIndex == 0)
                return;

            var doc = instance.ParentVisualLine.Document;
            if (doc == null || doc.LineCount <= 0)
                return;

            var lineNumber = FindPackageIndexTargetLine(doc, packageIndex);
            if (lineNumber <= 0)
                return;

            var line = doc.GetLineByNumber(lineNumber);
            AvalonEditor.YesWeEditor.Select(line.Offset, line.Length);
            AvalonEditor.YesWeEditor.ScrollToLine(lineNumber);
            AvalonEditor.YesWeEditor.TextArea.Caret.BringCaretToView();
        };
        return instance;
    }

    private static int FindPackageIndexTargetLine(TextDocument doc, int packageIndex)
    {
        var isExport = packageIndex > 0;
        var targetArray = isExport ? "Exports" : "Imports";
        var targetEntryIndex = Math.Abs(packageIndex) - 1;
        if (targetEntryIndex < 0)
            return -1;

        var arrayStart = FindArrayStartLine(doc, targetArray);
        if (arrayStart < 0)
            return -1;

        var entryStart = FindObjectEntryStartLine(doc, arrayStart, targetEntryIndex);
        if (entryStart < 0)
            return -1;

        var objectNameLine = FindObjectNameLine(doc, entryStart);
        return objectNameLine > 0 ? objectNameLine : entryStart;
    }

    private static int FindArrayStartLine(TextDocument doc, string arrayName)
    {
        var marker = $"\"{arrayName}\": [";
        for (var lineNumber = 1; lineNumber <= doc.LineCount; lineNumber++)
        {
            var line = doc.GetLineByNumber(lineNumber);
            var text = doc.GetText(line.Offset, line.Length);
            if (text.Contains(marker, StringComparison.Ordinal))
                return lineNumber;
        }

        return -1;
    }

    private static int FindObjectEntryStartLine(TextDocument doc, int arrayStartLine, int targetEntryIndex)
    {
        var entryIndex = -1;
        for (var lineNumber = arrayStartLine + 1; lineNumber <= doc.LineCount; lineNumber++)
        {
            var line = doc.GetLineByNumber(lineNumber);
            var text = doc.GetText(line.Offset, line.Length);
            var trimmed = text.Trim();
            if (trimmed == "]" || trimmed == "],")
                break;

            if (text == "    {")
            {
                entryIndex++;
                if (entryIndex == targetEntryIndex)
                    return lineNumber;
            }
        }

        return -1;
    }

    private static int FindObjectNameLine(TextDocument doc, int objectStartLine)
    {
        for (var lineNumber = objectStartLine; lineNumber <= doc.LineCount; lineNumber++)
        {
            var line = doc.GetLineByNumber(lineNumber);
            var text = doc.GetText(line.Offset, line.Length);
            var trimmed = text.Trim();
            if (trimmed.StartsWith("\"ObjectName\":", StringComparison.Ordinal))
                return lineNumber;
            if (text == "    }" || text == "    },")
                return -1;
        }

        return -1;
    }
}
