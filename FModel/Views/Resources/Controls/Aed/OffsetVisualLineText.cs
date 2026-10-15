using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Rendering;

namespace FModel.Views.Resources.Controls;

public class OffsetVisualLineText : VisualLineText
{
    public delegate void OffsetOnClick(string offset);

    public event OffsetOnClick OnOffsetClicked;
    private readonly string _offset;

    public OffsetVisualLineText(string offset, VisualLine parentVisualLine, int length) : base(parentVisualLine, length)
    {
        _offset = offset;
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

    private bool OffsetIsClickable() => !string.IsNullOrEmpty(_offset) && Keyboard.Modifiers == ModifierKeys.None;

    protected override void OnQueryCursor(QueryCursorEventArgs e)
    {
        if (!OffsetIsClickable())
            return;
        e.Handled = true;
        e.Cursor = Cursors.Hand;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !OffsetIsClickable())
            return;
        if (e.Handled || OnOffsetClicked == null)
            return;

        OnOffsetClicked(_offset);
        e.Handled = true;
    }

    protected override VisualLineText CreateInstance(int length)
    {
        var instance = new OffsetVisualLineText(_offset, ParentVisualLine, length);
        instance.OnOffsetClicked += offsetText =>
        {
            if (!int.TryParse(offsetText, out var targetOffset))
                return;

            var doc = instance.ParentVisualLine.Document;
            if (doc == null || doc.LineCount <= 0)
                return;

            var currentLine = instance.ParentVisualLine.FirstDocumentLine.LineNumber;
            var marker = $"\"offset\": {targetOffset}";
            var candidates = new List<int>();

            for (var lineNumber = 1; lineNumber <= doc.LineCount; lineNumber++)
            {
                var line = doc.GetLineByNumber(lineNumber);
                var text = doc.GetText(line.Offset, line.Length);
                if (text.Contains(marker, StringComparison.Ordinal))
                    candidates.Add(lineNumber);
            }

            if (candidates.Count == 0)
                return;

            var destination = candidates.OrderBy(line => Math.Abs(line - currentLine)).First();
            var targetLine = doc.GetLineByNumber(destination);
            AvalonEditor.YesWeEditor.Select(targetLine.Offset, targetLine.Length);
            AvalonEditor.YesWeEditor.ScrollToLine(destination);
            AvalonEditor.YesWeEditor.TextArea.Caret.BringCaretToView();
        };

        return instance;
    }
}
