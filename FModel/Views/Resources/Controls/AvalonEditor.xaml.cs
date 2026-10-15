using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CUE4Parse.Utils;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.ViewModels;
using ICSharpCode.AvalonEdit;
using SkiaSharp;

namespace FModel.Views.Resources.Controls;

/// <summary>
/// Logique d'interaction pour AvalonEditor.xaml
/// </summary>
public partial class AvalonEditor
{
    public static TextEditor YesWeEditor;
    public static System.Windows.Controls.TextBox YesWeSearch;
    private static readonly Regex JsonStatementRegex = new("\"StatementIndex\"\\s*:\\s*(?<idx>\\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PseudoStatementRegex = new("\\bstmt\\s*[:=]\\s*(?<idx>\\d+)\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int RichRenderMaxTextLength = 250_000;
    private const int RichRenderMaxLineCount = 5_000;
    private const int LinkOnlyMaxTextLength = 900_000;
    private const int LinkOnlyMaxLineCount = 18_000;
    private readonly Regex _hexColorRegex = new("\"Hex\": \"(?'target'[0-9A-Fa-f]{3,8})\"$",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly System.Windows.Controls.ToolTip _toolTip = new();
    private readonly Dictionary<string, NavigationList<int>> _savedCarets = new();
    private NavigationList<int> _caretsOffsets
    {
        get => MyAvalonEditor.Document != null && MyAvalonEditor.Document.FileName != null
            ? _savedCarets.GetOrAdd(MyAvalonEditor.Document.FileName, () => new NavigationList<int>())
            : new NavigationList<int>();
    }
    private bool _ignoreCaret = true;
    private RenderMode _renderMode = RenderMode.Full;
    private bool _hasLoggedLargeDocumentMode;
    private DateTime _lastScrollDiagAt = DateTime.MinValue;
    private double _lastScrollDiagOffset;
    private TabItem _boundTabItem;

    private enum RenderMode
    {
        Full,
        LinkOnly,
        Minimal
    }

    public AvalonEditor()
    {
        CommandBindings.Add(new CommandBinding(NavigationCommands.Search, (_, e) =>
        {
            if (DataContext is TabItem tab)
                tab.HasSearchOpen = true;
            e.Handled = true;
        }));
        InitializeComponent();

        YesWeEditor = MyAvalonEditor;
        YesWeSearch = WpfSuckMyDick;
        MyAvalonEditor.TextArea.TextView.LinkTextBackgroundBrush = null;
        MyAvalonEditor.TextArea.TextView.LinkTextForegroundBrush = Brushes.Cornsilk;
        PseudoPreviewEditor.TextArea.TextView.LinkTextBackgroundBrush = null;
        PseudoPreviewEditor.TextArea.TextView.LinkTextForegroundBrush = Brushes.Cornsilk;
        PseudoPreviewEditor.TextArea.TextView.ElementGenerators.Add(new StmtElementGenerator());
        ConfigureElementGenerators(RenderMode.Full);
        MyAvalonEditor.TextArea.TextView.ScrollOffsetChanged += OnScrollOffsetChanged;
        MyAvalonEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        DataContextChanged += OnDataContextChanged;

        ApplicationService.ApplicationView.CUE4Parse.TabControl.OnTabRemove += OnTabClose;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundTabItem != null)
            _boundTabItem.PropertyChanged -= OnBoundTabPropertyChanged;

        _boundTabItem = DataContext as TabItem;
        if (_boundTabItem != null)
            _boundTabItem.PropertyChanged += OnBoundTabPropertyChanged;

        UpdatePreviewLayout(_boundTabItem?.ShowPseudoPreviewPane == true);
    }

    private void OnBoundTabPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(TabItem.TitleExtra), StringComparison.Ordinal) &&
            !string.Equals(e.PropertyName, nameof(TabItem.ShowPseudoPreviewPane), StringComparison.Ordinal))
            return;

        Dispatcher.Invoke(() => UpdatePreviewLayout(_boundTabItem?.ShowPseudoPreviewPane == true));
    }

    private void UpdatePreviewLayout(bool showPreview)
    {
        LeftColumn.Width = new GridLength(1, GridUnitType.Star);
        SplitterColumn.Width = showPreview ? GridLength.Auto : new GridLength(0);
        RightColumn.Width = showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                ((TabItem) DataContext).HasSearchOpen = false;
                break;
            case Key.Enter when !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && ((TabItem) DataContext).HasSearchOpen:
                FindNext();
                break;
            case Key.Enter when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && ((TabItem) DataContext).HasSearchOpen:
                var dc = (TabItem) DataContext;
                var old = dc.SearchUp;
                dc.SearchUp = true;
                FindNext();
                dc.SearchUp = old;
                break;
            case Key.System: // Alt
                if (Keyboard.IsKeyDown(Key.Left))
                {
                    if (_caretsOffsets.Count == 0) return;
                    MyAvalonEditor.CaretOffset = _caretsOffsets.MovePrevious;
                    MyAvalonEditor.TextArea.Caret.BringCaretToView();
                }
                else if (Keyboard.IsKeyDown(Key.Right))
                {
                    if (_caretsOffsets.Count == 0) return;
                    MyAvalonEditor.CaretOffset = _caretsOffsets.MoveNext;
                    MyAvalonEditor.TextArea.Caret.BringCaretToView();
                }

                break;
        }
    }

    private void OnMouseHover(object sender, MouseEventArgs e)
    {
        var pos = MyAvalonEditor.GetPositionFromPoint(e.GetPosition(MyAvalonEditor));
        if (pos == null) return;

        var line = MyAvalonEditor.Document.GetLineByNumber(pos.Value.Line);
        var m = _hexColorRegex.Match(MyAvalonEditor.Document.GetText(line.Offset, line.Length));
        if (!m.Success || !m.Groups.TryGetValue("target", out var g)) return;

        var color = SKColor.Parse(g.Value);
        _toolTip.PlacementTarget = this; // required for property inheritance
        _toolTip.Background = new SolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
        _toolTip.Foreground = _toolTip.BorderBrush = PerceivedBrightness(color) > 130 ? Brushes.Black : Brushes.White;
        _toolTip.Content = $"#{g.Value}";
        _toolTip.IsOpen = true;
        e.Handled = true;
    }

    private void OnMouseHoverStopped(object sender, MouseEventArgs e)
    {
        _toolTip.IsOpen = false;
    }

    private int PerceivedBrightness(SKColor c)
    {
        return (int) Math.Sqrt(
            c.Red * c.Red * .299 +
            c.Green * c.Green * .587 +
            c.Blue * c.Blue * .114);
    }

    private void OnTextChanged(object sender, EventArgs e)
    {
        if (sender is not TextEditor avalonEditor || DataContext is not TabItem tabItem ||
            avalonEditor.Document == null || string.IsNullOrEmpty(avalonEditor.Document.Text))
            return;
        avalonEditor.Document.FileName = tabItem.Entry.PathWithoutExtension;
        UpdateLargeDocumentMode();

        if (!_savedCarets.ContainsKey(avalonEditor.Document.FileName))
            _ignoreCaret = true;

        if (!tabItem.ShouldScroll) return;

        var lineNumber = avalonEditor.Document.Text.GetNameLineNumber(tabItem.ScrollTrigger);
        if (lineNumber == -1) lineNumber = 1;

        var line = avalonEditor.Document.GetLineByNumber(lineNumber);
        avalonEditor.Select(line.Offset, line.Length);
        avalonEditor.ScrollToLine(lineNumber);

        if (!tabItem.IsReadOnly && string.Equals(tabItem.TitleExtra, "Blueprint JSON", StringComparison.Ordinal))
        {
            ApplicationService.ApplicationView?.CUE4Parse?.QueueLivePseudoPreviewFromJson(tabItem);
            SyncPseudoPreviewSelectionFromJsonCaret();
        }
    }

    private void OnCaretPositionChanged(object sender, EventArgs e)
    {
        SyncPseudoPreviewSelectionFromJsonCaret();
    }

    private void UpdateLargeDocumentMode()
    {
        var doc = MyAvalonEditor.Document;
        if (doc == null) return;

        var newMode = ResolveRenderMode(doc);
        if (newMode == _renderMode)
            return;

        _renderMode = newMode;
        ConfigureElementGenerators(_renderMode);

        if (_renderMode != RenderMode.Full && !_hasLoggedLargeDocumentMode)
        {
            _hasLoggedLargeDocumentMode = true;
            FLogger.Append(ELog.Warning, () => FLogger.Text(
                "Large JSON detected: reduced editor decorations to keep scrolling stable.",
                Constants.WHITE,
                true));
        }

        global::FModel.App.AppendCrashDiagnostic(
            $"EditorRenderModeChanged mode={_renderMode} textLen={doc.TextLength} lines={doc.LineCount} file={doc.FileName}");
    }

    private RenderMode ResolveRenderMode(ICSharpCode.AvalonEdit.Document.TextDocument doc)
    {
        var textLength = doc.TextLength;
        var lineCount = doc.LineCount;
        if (textLength > LinkOnlyMaxTextLength || lineCount > LinkOnlyMaxLineCount)
            return RenderMode.Minimal;
        if (textLength > RichRenderMaxTextLength || lineCount > RichRenderMaxLineCount)
            return RenderMode.LinkOnly;
        return RenderMode.Full;
    }

    private void ConfigureElementGenerators(RenderMode mode)
    {
        var generators = MyAvalonEditor.TextArea.TextView.ElementGenerators;
        generators.Clear();
        generators.Add(new PackageIndexElementGenerator());
        if (mode == RenderMode.Minimal)
        {
            MyAvalonEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Text);
            return;
        }
        if (mode == RenderMode.Full)
        {
            generators.Add(new GamePathElementGenerator());
            generators.Add(new JumpElementGenerator());
            generators.Add(new OffsetElementGenerator());
            generators.Add(new HexColorElementGenerator());
        }

        MyAvalonEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Text);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not TabItem tabItem || Keyboard.Modifiers != ModifierKeys.Control)
            return;

        var fontSize = tabItem.FontSize + e.Delta / 50.0;
        tabItem.FontSize = fontSize switch
        {
            < 6 => 6,
            > 200 => 200,
            _ => fontSize
        };
    }

    private void OnDeleteSearchClick(object sender, RoutedEventArgs e)
    {
        ((TabItem) DataContext).TextToFind = string.Empty;
    }

    private void FindNext(bool invertLeftRight = false)
    {
        var viewModel = (TabItem) DataContext;
        if (viewModel.Document == null || string.IsNullOrEmpty(viewModel.TextToFind))
            return;

        if (!viewModel.UseRegEx)
        {
            var comparison = viewModel.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var text = MyAvalonEditor.Text;
            if (string.IsNullOrEmpty(text))
                return;

            var start = viewModel.SearchUp
                ? Math.Max(0, MyAvalonEditor.SelectionStart - 1)
                : MyAvalonEditor.SelectionStart + MyAvalonEditor.SelectionLength;
            if (start >= text.Length)
                start = text.Length - 1;

            int index;
            if (viewModel.SearchUp)
            {
                index = FindPlainText(text, viewModel.TextToFind, start, comparison, rightToLeft: true, wholeWord: viewModel.WholeWord);
                if (index < 0)
                    index = FindPlainText(text, viewModel.TextToFind, text.Length - 1, comparison, rightToLeft: true, wholeWord: viewModel.WholeWord);
            }
            else
            {
                index = FindPlainText(text, viewModel.TextToFind, Math.Max(0, start), comparison, rightToLeft: false, wholeWord: viewModel.WholeWord);
                if (index < 0)
                    index = FindPlainText(text, viewModel.TextToFind, 0, comparison, rightToLeft: false, wholeWord: viewModel.WholeWord);
            }

            if (index >= 0)
            {
                MyAvalonEditor.Select(index, viewModel.TextToFind.Length);
                MyAvalonEditor.TextArea.Caret.BringCaretToView();
            }
            return;
        }

        Regex r;
        if (invertLeftRight)
        {
            viewModel.SearchUp = !viewModel.SearchUp;
            r = GetRegEx();
            viewModel.SearchUp = !viewModel.SearchUp;
        }
        else r = GetRegEx();

        try
        {
            var rightToLeft = r.Options.HasFlag(RegexOptions.RightToLeft);
            var m = r.Match(MyAvalonEditor.Text, rightToLeft ? MyAvalonEditor.SelectionStart : MyAvalonEditor.SelectionStart + MyAvalonEditor.SelectionLength);
            if (m.Success)
            {
                MyAvalonEditor.Select(m.Index, m.Length);
                MyAvalonEditor.TextArea.Caret.BringCaretToView();
            }
            else
            {
                // we have reached the end of the document
                // start again from the beginning/end,
                var oldEditor = MyAvalonEditor;
                do
                {
                    m = rightToLeft ? r.Match(MyAvalonEditor.Text, MyAvalonEditor.Text.Length - 1) : r.Match(MyAvalonEditor.Text, 0);
                    if (!m.Success) continue;
                    MyAvalonEditor.Select(m.Index, m.Length);
                    MyAvalonEditor.TextArea.Caret.BringCaretToView();
                    break;
                } while (MyAvalonEditor != oldEditor);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text(
                "Search regex timed out on large text. Please narrow pattern or disable Regex mode.",
                Constants.WHITE,
                true));
        }
    }

    private Regex GetRegEx(bool forceLeftToRight = false)
    {
        Regex r;
        var o = RegexOptions.None;
        var viewModel = (TabItem) DataContext;

        if (viewModel.SearchUp && !forceLeftToRight)
            o |= RegexOptions.RightToLeft;
        if (!viewModel.CaseSensitive)
            o |= RegexOptions.IgnoreCase;

        if (viewModel.UseRegEx)
        {
            r = new Regex(viewModel.TextToFind, o, TimeSpan.FromMilliseconds(500));
        }
        else
        {
            var s = Regex.Escape(viewModel.TextToFind);
            if (viewModel.WholeWord)
                s = "\\W" + s + "\\W";

            r = new Regex(s, o, TimeSpan.FromMilliseconds(500));
        }

        return r;
    }

    private static int FindPlainText(string text, string pattern, int startIndex, StringComparison comparison, bool rightToLeft, bool wholeWord)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
            return -1;

        if (!wholeWord)
        {
            return rightToLeft
                ? text.LastIndexOf(pattern, Math.Clamp(startIndex, 0, text.Length - 1), comparison)
                : text.IndexOf(pattern, Math.Max(0, startIndex), comparison);
        }

        if (rightToLeft)
        {
            var probe = Math.Clamp(startIndex, 0, text.Length - 1);
            while (probe >= 0)
            {
                var idx = text.LastIndexOf(pattern, probe, comparison);
                if (idx < 0) return -1;
                if (IsWholeWordMatch(text, idx, pattern.Length)) return idx;
                probe = idx - 1;
            }

            return -1;
        }

        for (var probe = Math.Max(0, startIndex); probe < text.Length;)
        {
            var idx = text.IndexOf(pattern, probe, comparison);
            if (idx < 0) return -1;
            if (IsWholeWordMatch(text, idx, pattern.Length)) return idx;
            probe = idx + 1;
        }

        return -1;
    }

    private static bool IsWholeWordMatch(string text, int index, int length)
    {
        var leftOk = index <= 0 || !IsWordChar(text[index - 1]);
        var rightIndex = index + length;
        var rightOk = rightIndex >= text.Length || !IsWordChar(text[rightIndex]);
        return leftOk && rightOk;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        ((TabItem) DataContext).HasSearchOpen = false;
    }

    private void OnTabClose(object sender, EventArgs eventArgs)
    {
        if (eventArgs is not TabControlViewModel.TabEventArgs e || e.TabToRemove.Document?.FileName is not { } fileName)
            return;

        if (_savedCarets.ContainsKey(fileName))
            _savedCarets.Remove(fileName);
    }

    private void SaveCaretLoc(int offset)
    {
        if (_ignoreCaret)
        {
            _ignoreCaret = false;
            return;
        } // first always point to the end of the file for some reason

        if (_caretsOffsets.Count >= 10)
            _caretsOffsets.RemoveAt(0);
        if (!_caretsOffsets.Contains(offset))
        {
            _caretsOffsets.Add(offset);
            _caretsOffsets.CurrentIndex = _caretsOffsets.Count - 1;
        }
    }

    private void OnMouseRelease(object sender, MouseButtonEventArgs e)
    {
        SaveCaretLoc(MyAvalonEditor.CaretOffset);
        SyncPseudoPreviewSelectionFromJsonCaret();
    }

    private void OnPseudoPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || DataContext is not TabItem tabItem || !tabItem.ShowPseudoPreviewPane)
            return;
        if (IsClickOnScrollChrome(e))
            return;
        if (PseudoPreviewEditor.Document == null || MyAvalonEditor.Document == null)
            return;
        if (TryFocusStatementFromPseudoClick(e.GetPosition(PseudoPreviewEditor)))
            e.Handled = true;
    }
    
    private void OnPseudoPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || DataContext is not TabItem tabItem || !tabItem.ShowPseudoPreviewPane)
            return;
        if (IsClickOnScrollChrome(e))
            return;
        if (PseudoPreviewEditor.Document == null || MyAvalonEditor.Document == null)
            return;
        if (TryFocusStatementFromPseudoClick(e.GetPosition(PseudoPreviewEditor)))
            e.Handled = true;
    }

    private static bool IsClickOnScrollChrome(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return false;

        return HasVisualAncestor<ScrollBar>(source) ||
               HasVisualAncestor<Thumb>(source) ||
               HasVisualAncestor<Track>(source) ||
               HasVisualAncestor<RepeatButton>(source);
    }

    private static bool HasVisualAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        var current = node;
        while (current != null)
        {
            if (current is T)
                return true;
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void OnScrollOffsetChanged(object sender, EventArgs e)
    {
        var doc = MyAvalonEditor.Document;
        if (doc == null)
            return;

        if (_renderMode == RenderMode.Full && doc.TextLength < RichRenderMaxTextLength)
            return;

        var now = DateTime.UtcNow;
        var verticalOffset = MyAvalonEditor.VerticalOffset;
        if ((now - _lastScrollDiagAt).TotalMilliseconds < 1200 &&
            Math.Abs(verticalOffset - _lastScrollDiagOffset) < 40)
            return;

        _lastScrollDiagAt = now;
        _lastScrollDiagOffset = verticalOffset;

        var memMb = GC.GetTotalMemory(false) / (1024 * 1024);
        global::FModel.App.AppendCrashDiagnostic(
            $"EditorScroll mode={_renderMode} vOffset={verticalOffset:F1} textLen={doc.TextLength} lines={doc.LineCount} memMB={memMb} file={doc.FileName}");
    }

    private void SyncPseudoPreviewSelectionFromJsonCaret()
    {
        if (DataContext is not TabItem tabItem || !tabItem.ShowPseudoPreviewPane)
            return;
        if (MyAvalonEditor.Document == null || PseudoPreviewEditor.Document == null)
            return;
        if (MyAvalonEditor.CaretOffset < 0 || MyAvalonEditor.CaretOffset > MyAvalonEditor.Document.TextLength)
            return;

        var currentLine = MyAvalonEditor.Document.GetLineByOffset(MyAvalonEditor.CaretOffset);
        var currentLineText = MyAvalonEditor.Document.GetText(currentLine);
        var statementMatch = JsonStatementRegex.Match(currentLineText);
        if (!statementMatch.Success || !statementMatch.Groups.TryGetValue("idx", out var statementGroup))
            return;

        var token = $"stmt:{statementGroup.Value}";
        var pseudoText = PseudoPreviewEditor.Document.Text;
        if (string.IsNullOrEmpty(pseudoText))
            return;

        var tokenIndex = pseudoText.IndexOf(token, StringComparison.Ordinal);
        if (tokenIndex < 0)
            return;

        PseudoPreviewEditor.Select(tokenIndex, token.Length);
        var pseudoLine = PseudoPreviewEditor.Document.GetLineByOffset(tokenIndex).LineNumber;
        PseudoPreviewEditor.ScrollToLine(pseudoLine);
        PseudoPreviewEditor.TextArea.Caret.BringCaretToView();
    }

    private bool FocusStatementIndexInJson(int statementIndex)
    {
        var jsonText = MyAvalonEditor.Document.Text;
        if (string.IsNullOrEmpty(jsonText))
            return false;

        var match = Regex.Match(
            jsonText,
            $"\"StatementIndex\"\\s*:\\s*{statementIndex}\\b",
            RegexOptions.CultureInvariant);

        if (!match.Success)
            return false;

        var line = MyAvalonEditor.Document.GetLineByOffset(match.Index);
        MyAvalonEditor.Select(line.Offset, line.Length);
        MyAvalonEditor.ScrollToLine(line.LineNumber);
        MyAvalonEditor.TextArea.Caret.BringCaretToView();
        MyAvalonEditor.Focus();
        return true;
    }

    private bool TryFocusStatementFromPseudoClick(Point clickPoint)
    {
        if (TryResolvePseudoStatementIndex(clickPoint, out var statementIndex))
            return FocusStatementIndexInJson(statementIndex);
        return false;
    }

    private bool TryResolvePseudoStatementIndex(Point clickPoint, out int statementIndex)
    {
        statementIndex = -1;
        var doc = PseudoPreviewEditor.Document;
        if (doc == null)
            return false;

        var pos = PseudoPreviewEditor.GetPositionFromPoint(clickPoint);
        if (pos == null)
            return false;

        var offset = doc.GetOffset(pos.Value.Line, Math.Max(1, pos.Value.Column));

        var line = doc.GetLineByOffset(offset);
        var lineText = doc.GetText(line.Offset, line.Length);
        var lineMatch = PseudoStatementRegex.Match(lineText);
        if (lineMatch.Success &&
            lineMatch.Groups.TryGetValue("idx", out var lineIdxGroup) &&
            int.TryParse(lineIdxGroup.Value, out statementIndex))
        {
            return true;
        }

        var windowStart = Math.Max(0, offset - 128);
        var windowLength = Math.Min(doc.TextLength - windowStart, 256);
        if (windowLength <= 0)
            return false;

        var snippet = doc.GetText(windowStart, windowLength);
        var snippetMatch = PseudoStatementRegex.Match(snippet);
        if (!snippetMatch.Success || !snippetMatch.Groups.TryGetValue("idx", out var snippetIdxGroup))
            return false;

        return int.TryParse(snippetIdxGroup.Value, out statementIndex);
    }
}
