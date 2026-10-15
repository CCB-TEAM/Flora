using System;
using System.Collections.Generic;
using System.Linq;

namespace FModel.BlueprintGraph.Widgets;

public enum WidgetKind
{
    Unknown,
    UserWidget,
    CanvasPanel,
    HorizontalBox,
    VerticalBox,
    ScrollBox,
    Overlay,
    Border,
    Button,
    SizeBox,
    ScaleBox,
    UniformGridPanel,
    GridPanel,
    WrapBox,
    WidgetSwitcher,
    ContentWidget,
    Image,
    TextBlock,
    RichTextBlock,
    ProgressBar,
    Spacer,
    CheckBox,
    Slider,
    EditableText,
    ComboBox,
    Throbber,
}

/// <summary>Measures text for layout: returns the size of the node's text drawn at the given font size (pixels) and wrap width (0 = no wrap).</summary>
public delegate (float Width, float Height) TextMeasurer(WidgetNode node, string text, float fontSizePx, float wrapWidth);

/// <summary>
/// Approximates Slate layout of a widget tree in design space. Unset slot properties use the UMG defaults.
/// </summary>
public class WidgetLayout
{
    // UMG font sizes are points at 96 DPI
    public const float PointsToPixels = 96f / 72f;

    private static readonly Dictionary<string, WidgetKind> KnownKinds = new(StringComparer.Ordinal)
    {
        ["UserWidget"] = WidgetKind.UserWidget,
        ["CanvasPanel"] = WidgetKind.CanvasPanel, ["ConstraintCanvas"] = WidgetKind.CanvasPanel,
        ["HorizontalBox"] = WidgetKind.HorizontalBox,
        ["VerticalBox"] = WidgetKind.VerticalBox,
        ["ScrollBox"] = WidgetKind.ScrollBox, ["ListView"] = WidgetKind.ScrollBox, ["TileView"] = WidgetKind.ScrollBox, ["TreeView"] = WidgetKind.ScrollBox,
        ["Overlay"] = WidgetKind.Overlay, ["StackBox"] = WidgetKind.VerticalBox,
        ["Border"] = WidgetKind.Border, ["BackgroundBlur"] = WidgetKind.Border,
        ["Button"] = WidgetKind.Button,
        ["SizeBox"] = WidgetKind.SizeBox,
        ["ScaleBox"] = WidgetKind.ScaleBox,
        ["UniformGridPanel"] = WidgetKind.UniformGridPanel,
        ["GridPanel"] = WidgetKind.GridPanel,
        ["WrapBox"] = WidgetKind.WrapBox,
        ["WidgetSwitcher"] = WidgetKind.WidgetSwitcher,
        ["ContentWidget"] = WidgetKind.ContentWidget, ["NamedSlot"] = WidgetKind.ContentWidget, ["SafeZone"] = WidgetKind.ContentWidget,
        ["RetainerBox"] = WidgetKind.ContentWidget, ["InvalidationBox"] = WidgetKind.ContentWidget, ["MenuAnchor"] = WidgetKind.ContentWidget,
        ["Image"] = WidgetKind.Image,
        ["TextBlock"] = WidgetKind.TextBlock, ["TextLayoutWidget"] = WidgetKind.TextBlock,
        ["RichTextBlock"] = WidgetKind.RichTextBlock,
        ["ProgressBar"] = WidgetKind.ProgressBar,
        ["Spacer"] = WidgetKind.Spacer,
        ["CheckBox"] = WidgetKind.CheckBox,
        ["Slider"] = WidgetKind.Slider, ["SpinBox"] = WidgetKind.Slider,
        ["EditableText"] = WidgetKind.EditableText, ["EditableTextBox"] = WidgetKind.EditableText,
        ["MultiLineEditableText"] = WidgetKind.EditableText, ["MultiLineEditableTextBox"] = WidgetKind.EditableText,
        ["ComboBoxString"] = WidgetKind.ComboBox, ["ComboBoxKey"] = WidgetKind.ComboBox,
        ["Throbber"] = WidgetKind.Throbber, ["CircularThrobber"] = WidgetKind.Throbber,
    };

    private readonly TextMeasurer _measureText;
    private readonly Func<string, string?>? _superClassOf;
    private readonly Dictionary<string, WidgetKind> _kindCache = new(StringComparer.Ordinal);

    /// <param name="measureText">text measuring of the host renderer</param>
    /// <param name="superClassOf">native class name -> parent class name (from mappings), to classify game specific widgets</param>
    public WidgetLayout(TextMeasurer measureText, Func<string, string?>? superClassOf = null)
    {
        _measureText = measureText;
        _superClassOf = superClassOf;
    }

    public WidgetKind GetKind(WidgetNode node)
    {
        if (node.IsUserWidget)
            return WidgetKind.UserWidget;

        if (_kindCache.TryGetValue(node.ClassName, out var cached))
            return cached;

        var kind = WidgetKind.Unknown;
        var className = node.ClassName;
        for (var depth = 0; className != null && depth < 16; depth++)
        {
            if (KnownKinds.TryGetValue(className, out kind))
                break;
            kind = WidgetKind.Unknown;
            className = _superClassOf?.Invoke(className);
        }

        if (kind == WidgetKind.Unknown && node.Children.Count > 0)
            kind = WidgetKind.ContentWidget;

        _kindCache[node.ClassName] = kind;
        return kind;
    }

    public void Layout(WidgetNode root, float width, float height)
    {
        Measure(root);
        Arrange(root, 0, 0, width, height);
    }

    #region slot defaults

    private static string DefaultHAlign(string slotClass) => slotClass switch
    {
        "OverlaySlot" or "UniformGridSlot" or "WrapBoxSlot" or "GridSlot" => "HAlign_Left",
        "ButtonSlot" or "ScaleBoxSlot" => "HAlign_Center",
        _ => "HAlign_Fill",
    };

    private static string DefaultVAlign(string slotClass) => slotClass switch
    {
        "OverlaySlot" or "UniformGridSlot" or "WrapBoxSlot" or "GridSlot" => "VAlign_Top",
        "ButtonSlot" or "ScaleBoxSlot" => "VAlign_Center",
        _ => "VAlign_Fill",
    };

    private static WidgetMargin DefaultPadding(string slotClass) => slotClass switch
    {
        "BorderSlot" or "ButtonSlot" => new WidgetMargin(4, 2, 4, 2),
        _ => WidgetMargin.Zero,
    };

    /// <summary>Slot class of the child, or the one its parent panel creates when the slot is unknown.</summary>
    private string SlotClass(WidgetNode child)
    {
        if (!string.IsNullOrEmpty(child.SlotClassName))
            return child.SlotClassName;

        return child.Parent == null ? "" : GetKind(child.Parent) switch
        {
            WidgetKind.Overlay => "OverlaySlot",
            WidgetKind.Border => "BorderSlot",
            WidgetKind.Button => "ButtonSlot",
            WidgetKind.ScaleBox => "ScaleBoxSlot",
            WidgetKind.UniformGridPanel => "UniformGridSlot",
            WidgetKind.GridPanel => "GridSlot",
            WidgetKind.WrapBox => "WrapBoxSlot",
            _ => "",
        };
    }

    public WidgetMargin SlotPadding(WidgetNode child) =>
        WidgetProps.Margin(child.Slot, "Padding", DefaultPadding(SlotClass(child)));

    public string SlotHAlign(WidgetNode child) =>
        WidgetProps.Enum(child.Slot, "HorizontalAlignment", DefaultHAlign(SlotClass(child)));

    public string SlotVAlign(WidgetNode child) =>
        WidgetProps.Enum(child.Slot, "VerticalAlignment", DefaultVAlign(SlotClass(child)));

    /// <summary>Box slot size rule: (IsFill, weight). UMG box slots default to Automatic.</summary>
    private static (bool Fill, float Value) SlotSize(WidgetNode child)
    {
        var size = WidgetProps.Struct(child.Slot, "Size");
        if (size == null)
            return (false, 1);
        return (WidgetProps.Enum(size, "SizeRule", "Automatic") == "Fill", WidgetProps.Float(size, "Value", 1));
    }

    #endregion

    #region measure

    private void Measure(WidgetNode node)
    {
        foreach (var child in node.Children)
            Measure(child);

        node.IsVisible = !node.IsCollapsed;
        if (!node.IsVisible)
        {
            node.DesiredWidth = node.DesiredHeight = 0;
            return;
        }

        var (w, h) = ComputeDesired(node);
        node.DesiredWidth = Math.Max(0, w);
        node.DesiredHeight = Math.Max(0, h);
    }

    private IEnumerable<WidgetNode> Visible(WidgetNode node) => node.Children.Where(o => !o.IsCollapsed);

    private (float, float) ChildWithPadding(WidgetNode child)
    {
        var padding = SlotPadding(child);
        return (child.DesiredWidth + padding.Horizontal, child.DesiredHeight + padding.Vertical);
    }

    private (float, float) MaxOfChildren(WidgetNode node)
    {
        float w = 0, h = 0;
        foreach (var child in Visible(node))
        {
            var (cw, ch) = ChildWithPadding(child);
            w = Math.Max(w, cw);
            h = Math.Max(h, ch);
        }
        return (w, h);
    }

    private (float, float) ComputeDesired(WidgetNode node)
    {
        var e = node.Export;
        switch (GetKind(node))
        {
            case WidgetKind.CanvasPanel:
            {
                float w = 0, h = 0;
                foreach (var child in Visible(node))
                {
                    var (anchorMin, anchorMax, offsets, _, autoSize) = CanvasSlot(child);
                    if (anchorMin.X == anchorMax.X)
                        w = Math.Max(w, offsets.Left + (autoSize ? child.DesiredWidth : offsets.Right));
                    if (anchorMin.Y == anchorMax.Y)
                        h = Math.Max(h, offsets.Top + (autoSize ? child.DesiredHeight : offsets.Bottom));
                }
                return (w, h);
            }
            case WidgetKind.HorizontalBox:
            case WidgetKind.WrapBox:
            {
                float w = 0, h = 0;
                foreach (var child in Visible(node))
                {
                    var (cw, ch) = ChildWithPadding(child);
                    w += cw;
                    h = Math.Max(h, ch);
                }
                return (w, h);
            }
            case WidgetKind.VerticalBox:
            case WidgetKind.ScrollBox:
            {
                var horizontal = GetKind(node) == WidgetKind.ScrollBox && WidgetProps.Enum(e, "Orientation", "Orient_Vertical") == "Orient_Horizontal";
                float w = 0, h = 0;
                foreach (var child in Visible(node))
                {
                    var (cw, ch) = ChildWithPadding(child);
                    if (horizontal) { w += cw; h = Math.Max(h, ch); }
                    else { h += ch; w = Math.Max(w, cw); }
                }
                return (w, h);
            }
            case WidgetKind.UniformGridPanel:
            case WidgetKind.GridPanel:
            {
                var (rows, columns, cellW, cellH) = GridMetrics(node);
                var slotPadding = WidgetProps.Margin(e, "SlotPadding", WidgetMargin.Zero);
                return (columns * (cellW + slotPadding.Horizontal), rows * (cellH + slotPadding.Vertical));
            }
            case WidgetKind.SizeBox:
            {
                var (w, h) = MaxOfChildren(node);
                if (WidgetProps.Bool(e, "bOverride_MinDesiredWidth")) w = Math.Max(w, WidgetProps.Float(e, "MinDesiredWidth"));
                if (WidgetProps.Bool(e, "bOverride_MinDesiredHeight")) h = Math.Max(h, WidgetProps.Float(e, "MinDesiredHeight"));
                if (WidgetProps.Bool(e, "bOverride_MaxDesiredWidth")) w = Math.Min(w, WidgetProps.Float(e, "MaxDesiredWidth"));
                if (WidgetProps.Bool(e, "bOverride_MaxDesiredHeight")) h = Math.Min(h, WidgetProps.Float(e, "MaxDesiredHeight"));
                if (WidgetProps.Bool(e, "bOverride_WidthOverride")) w = WidgetProps.Float(e, "WidthOverride");
                if (WidgetProps.Bool(e, "bOverride_HeightOverride")) h = WidgetProps.Float(e, "HeightOverride");
                return (w, h);
            }
            case WidgetKind.WidgetSwitcher:
            {
                var active = ActiveSwitcherChild(node);
                return active == null ? (0, 0) : ChildWithPadding(active);
            }
            case WidgetKind.Border:
            {
                var (w, h) = MaxOfChildren(node);
                if (node.Children.Count == 0)
                {
                    var padding = WidgetProps.Margin(e, "Padding", new WidgetMargin(4, 2, 4, 2));
                    return (padding.Horizontal, padding.Vertical);
                }
                return (w, h);
            }
            case WidgetKind.Button:
            {
                var (w, h) = MaxOfChildren(node);
                return node.Children.Count == 0 ? (8, 4) : (w, h);
            }
            case WidgetKind.Image:
            {
                var brush = WidgetProps.Struct(e, "Brush");
                var (w, h) = WidgetProps.Vector2(brush, "ImageSize", 32, 32);
                return (w, h);
            }
            case WidgetKind.TextBlock:
            case WidgetKind.RichTextBlock:
            {
                var text = TextOf(node);
                if (string.IsNullOrEmpty(text))
                    return (0, FontSizePx(node) * 1.2f);
                var margin = WidgetProps.Margin(e, "Margin", WidgetMargin.Zero);
                var (w, h) = _measureText(node, text, FontSizePx(node), 0);
                return (w + margin.Horizontal, h + margin.Vertical);
            }
            case WidgetKind.Spacer:
                return WidgetProps.Vector2(e, "Size", 1, 1);
            case WidgetKind.ProgressBar:
                return (100, 16);
            case WidgetKind.CheckBox:
                return node.Children.Count > 0 ? MaxOfChildren(node) : (24, 24);
            case WidgetKind.Slider:
                return (100, 18);
            case WidgetKind.EditableText:
                return (120, 30);
            case WidgetKind.ComboBox:
                return (120, 30);
            case WidgetKind.Throbber:
                return (32, 32);
            case WidgetKind.UserWidget:
            case WidgetKind.ScaleBox:
            case WidgetKind.Overlay:
            case WidgetKind.ContentWidget:
                return MaxOfChildren(node);
            default:
                return node.Children.Count > 0 ? MaxOfChildren(node) : (100, 30);
        }
    }

    public static string TextOf(WidgetNode node) => WidgetProps.Text(node.Export, "Text");

    public static float FontSizePx(WidgetNode node)
    {
        var font = WidgetProps.Struct(node.Export, "Font");
        return WidgetProps.Float(font, "Size", 24) * PointsToPixels;
    }

    public static WidgetNode? ActiveSwitcherChild(WidgetNode switcher)
    {
        var index = WidgetProps.Int(switcher.Export, "ActiveWidgetIndex");
        return index >= 0 && index < switcher.Children.Count ? switcher.Children[index] : switcher.Children.FirstOrDefault();
    }

    private (int Rows, int Columns, float CellW, float CellH) GridMetrics(WidgetNode node)
    {
        int rows = 1, columns = 1;
        float cellW = 0, cellH = 0;
        foreach (var child in Visible(node))
        {
            rows = Math.Max(rows, WidgetProps.Int(child.Slot, "Row") + 1);
            columns = Math.Max(columns, WidgetProps.Int(child.Slot, "Column") + 1);
            var (cw, ch) = ChildWithPadding(child);
            cellW = Math.Max(cellW, cw);
            cellH = Math.Max(cellH, ch);
        }
        return (rows, columns, cellW, cellH);
    }

    public static ((float X, float Y) AnchorMin, (float X, float Y) AnchorMax, WidgetMargin Offsets, (float X, float Y) Alignment, bool AutoSize) CanvasSlot(WidgetNode child)
    {
        var layout = WidgetProps.Struct(child.Slot, "LayoutData");
        var anchors = WidgetProps.Struct(layout, "Anchors");
        return (
            WidgetProps.Vector2(anchors, "Minimum"),
            WidgetProps.Vector2(anchors, "Maximum"),
            WidgetProps.Margin(layout, "Offsets", new WidgetMargin(0, 0, 100, 30)),
            WidgetProps.Vector2(layout, "Alignment"),
            WidgetProps.Bool(child.Slot, "bAutoSize"));
    }

    #endregion

    #region arrange

    private void Arrange(WidgetNode node, float x, float y, float w, float h)
    {
        node.X = x;
        node.Y = y;
        node.Width = Math.Max(0, w);
        node.Height = Math.Max(0, h);
        node.ContentScaleX = node.ContentScaleY = 1;
        if (!node.IsVisible)
            return;

        var e = node.Export;
        switch (GetKind(node))
        {
            case WidgetKind.CanvasPanel:
                foreach (var child in Visible(node))
                {
                    var (anchorMin, anchorMax, offsets, alignment, autoSize) = CanvasSlot(child);
                    ArrangeCanvasAxis(anchorMin.X, anchorMax.X, offsets.Left, offsets.Right, alignment.X, autoSize, child.DesiredWidth, w, out var cx, out var cw);
                    ArrangeCanvasAxis(anchorMin.Y, anchorMax.Y, offsets.Top, offsets.Bottom, alignment.Y, autoSize, child.DesiredHeight, h, out var cy, out var ch);
                    Arrange(child, x + cx, y + cy, cw, ch);
                }
                break;
            case WidgetKind.HorizontalBox:
                ArrangeBox(node, x, y, w, h, true);
                break;
            case WidgetKind.VerticalBox:
                ArrangeBox(node, x, y, w, h, false);
                break;
            case WidgetKind.ScrollBox:
            {
                // content is stacked at desired size and may overflow the (clipped) scroll box
                var horizontal = WidgetProps.Enum(e, "Orientation", "Orient_Vertical") == "Orient_Horizontal";
                var offset = 0f;
                foreach (var child in Visible(node))
                {
                    var padding = SlotPadding(child);
                    if (horizontal)
                    {
                        var cellW = child.DesiredWidth + padding.Horizontal;
                        ArrangeAligned(child, x + offset, y, cellW, h);
                        offset += cellW;
                    }
                    else
                    {
                        var cellH = child.DesiredHeight + padding.Vertical;
                        ArrangeAligned(child, x, y + offset, w, cellH);
                        offset += cellH;
                    }
                }
                break;
            }
            case WidgetKind.WrapBox:
            {
                float cx = 0, cy = 0, rowH = 0;
                var wrapWidth = WidgetProps.Bool(e, "bExplicitWrapSize") ? WidgetProps.Float(e, "WrapSize", w) : w;
                foreach (var child in Visible(node))
                {
                    var (cw, ch) = ChildWithPadding(child);
                    if (cx > 0 && cx + cw > wrapWidth)
                    {
                        cx = 0;
                        cy += rowH;
                        rowH = 0;
                    }
                    ArrangeAligned(child, x + cx, y + cy, cw, ch);
                    cx += cw;
                    rowH = Math.Max(rowH, ch);
                }
                break;
            }
            case WidgetKind.UniformGridPanel:
            case WidgetKind.GridPanel:
            {
                var (rows, columns, _, _) = GridMetrics(node);
                var slotPadding = WidgetProps.Margin(e, "SlotPadding", WidgetMargin.Zero);
                var cellW = w / columns;
                var cellH = h / rows;
                foreach (var child in Visible(node))
                {
                    var row = WidgetProps.Int(child.Slot, "Row");
                    var column = WidgetProps.Int(child.Slot, "Column");
                    ArrangeAligned(child,
                        x + column * cellW + slotPadding.Left, y + row * cellH + slotPadding.Top,
                        cellW - slotPadding.Horizontal, cellH - slotPadding.Vertical);
                }
                break;
            }
            case WidgetKind.WidgetSwitcher:
            {
                var active = ActiveSwitcherChild(node);
                foreach (var child in node.Children)
                {
                    if (child == active)
                        ArrangeAligned(child, x, y, w, h);
                    else
                        Hide(child);
                }
                break;
            }
            case WidgetKind.ScaleBox:
                if (Visible(node).FirstOrDefault() is { } scaled)
                    ArrangeScaled(node, scaled, x, y, w, h);
                break;
            default:
                // overlay, border, button, size box, user widget and other single content widgets
                foreach (var child in Visible(node))
                    ArrangeAligned(child, x, y, w, h);
                break;
        }

        foreach (var child in node.Children.Where(o => o.IsCollapsed))
            Hide(child);
    }

    private static void Hide(WidgetNode node)
    {
        foreach (var n in node.SelfAndDescendants())
            n.IsVisible = false;
    }

    private static void ArrangeCanvasAxis(float anchorMin, float anchorMax, float offsetStart, float offsetEnd, float alignment,
        bool autoSize, float desired, float parentSize, out float position, out float size)
    {
        if (anchorMin == anchorMax)
        {
            // Offsets are position (start) and size (end)
            size = autoSize ? desired : offsetEnd;
            position = anchorMin * parentSize + offsetStart - size * alignment;
        }
        else
        {
            // stretched: Offsets are margins from the anchors
            var start = anchorMin * parentSize + offsetStart;
            var end = anchorMax * parentSize - offsetEnd;
            size = end - start;
            position = start - size * alignment;
        }
    }

    private void ArrangeBox(WidgetNode node, float x, float y, float w, float h, bool horizontal)
    {
        var children = Visible(node).ToList();
        float fixedSize = 0, fillWeight = 0;
        foreach (var child in children)
        {
            var padding = SlotPadding(child);
            var (fill, value) = SlotSize(child);
            fixedSize += horizontal ? padding.Horizontal : padding.Vertical;
            if (fill) fillWeight += Math.Max(0, value);
            else fixedSize += horizontal ? child.DesiredWidth : child.DesiredHeight;
        }

        var remaining = Math.Max(0, (horizontal ? w : h) - fixedSize);
        var offset = 0f;
        foreach (var child in children)
        {
            var padding = SlotPadding(child);
            var (fill, value) = SlotSize(child);
            var content = fill
                ? fillWeight > 0 ? remaining * Math.Max(0, value) / fillWeight : 0
                : horizontal ? child.DesiredWidth : child.DesiredHeight;
            var cell = content + (horizontal ? padding.Horizontal : padding.Vertical);

            if (horizontal)
                ArrangeAligned(child, x + offset, y, cell, h);
            else
                ArrangeAligned(child, x, y + offset, w, cell);
            offset += cell;
        }
    }

    /// <summary>Places a child in a cell using its slot padding and alignment.</summary>
    private void ArrangeAligned(WidgetNode child, float cellX, float cellY, float cellW, float cellH)
    {
        var padding = SlotPadding(child);
        var availX = cellX + padding.Left;
        var availY = cellY + padding.Top;
        var availW = Math.Max(0, cellW - padding.Horizontal);
        var availH = Math.Max(0, cellH - padding.Vertical);

        var (cx, cw) = Align(SlotHAlign(child), availX, availW, child.DesiredWidth);
        var (cy, ch) = Align(SlotVAlign(child), availY, availH, child.DesiredHeight);
        Arrange(child, cx, cy, cw, ch);
    }

    private static (float Position, float Size) Align(string alignment, float start, float available, float desired)
    {
        if (alignment.EndsWith("_Fill", StringComparison.Ordinal))
            return (start, available);

        var size = Math.Min(desired, available);
        return alignment switch
        {
            _ when alignment.EndsWith("_Center", StringComparison.Ordinal) => (start + (available - size) / 2, size),
            _ when alignment.EndsWith("_Right", StringComparison.Ordinal) || alignment.EndsWith("_Bottom", StringComparison.Ordinal) => (start + available - size, size),
            _ => (start, size),
        };
    }

    private void ArrangeScaled(WidgetNode scaleBox, WidgetNode child, float x, float y, float w, float h)
    {
        var stretch = WidgetProps.Enum(scaleBox.Export, "Stretch", "ScaleToFit");
        var dw = Math.Max(1, child.DesiredWidth);
        var dh = Math.Max(1, child.DesiredHeight);
        var scale = stretch switch
        {
            "None" => 1f,
            "ScaleToFill" => Math.Max(w / dw, h / dh),
            "ScaleToFitX" => w / dw,
            "ScaleToFitY" => h / dh,
            "UserSpecified" => WidgetProps.Float(scaleBox.Export, "UserSpecifiedScale", 1),
            _ => Math.Min(w / dw, h / dh),
        };

        var stretchDirection = WidgetProps.Enum(scaleBox.Export, "StretchDirection", "Both");
        if (stretchDirection == "DownOnly") scale = Math.Min(scale, 1);
        else if (stretchDirection == "UpOnly") scale = Math.Max(scale, 1);

        var (scaleX, scaleY) = stretch == "Fill" ? (w / dw, h / dh) : (scale, scale);
        if (scaleX <= 0 || scaleY <= 0 || float.IsNaN(scaleX) || float.IsNaN(scaleY))
            (scaleX, scaleY) = (1, 1);

        // like SScaleBox: the child is aligned (slot alignment/padding) in the unscaled area (allotted size / scale),
        // then the whole subtree is scaled around the scale box origin
        scaleBox.ContentScaleX = scaleX;
        scaleBox.ContentScaleY = scaleY;
        scaleBox.ContentOriginX = x;
        scaleBox.ContentOriginY = y;
        ArrangeAligned(child, x, y, w / scaleX, h / scaleY);
    }

    #endregion
}
