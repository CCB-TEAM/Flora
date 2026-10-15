#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using FModel.BlueprintGraph.Widgets;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace FModel.Views.BlueprintViewer;

/// <summary>
/// Approximate UMG designer preview of a widget tree laid out by <see cref="WidgetLayout"/>.
/// Right/middle drag to pan, wheel to zoom, left click to select the top-most widget.
/// </summary>
public partial class WidgetDesignerCanvas : SKElement
{
    private static readonly SKColor BackgroundColor = new(38, 38, 38);
    private static readonly SKPaint DesignAreaPaint = GraphStyle.Fill(20, 20, 20);
    private static readonly SKPaint DesignAreaBorder = GraphStyle.Stroke(1, 90, 90, 90);
    private static readonly SKPaint OutlinePaint = new() { Color = new SKColor(255, 255, 255, 45), IsStroke = true, StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash([4, 3], 0) };
    private static readonly SKPaint HoverPaint = GraphStyle.Stroke(new SKColor(100, 180, 255), 1.5f);
    private static readonly SKPaint SelectedPaint = GraphStyle.Stroke(SKColors.Orange, 2);
    private static readonly SKPaint FooterPaint = GraphStyle.Fill(21, 21, 21);
    private static readonly SKPaint TextPaint = GraphStyle.Fill(255, 255, 255);
    private static readonly SKPaint PlaceholderStroke = GraphStyle.Stroke(1, 140, 140, 140);
    private static readonly SKPaint PlaceholderFill = GraphStyle.Fill(60, 60, 60, 200);

    private WidgetNode? _root;
    private WidgetLayout? _layout;
    private float _designWidth = 1920, _designHeight = 1080;

    private Vector _translation;
    private float _scaling = 0.5f;
    private bool _isPanning;
    private Point _lastMousePosition;
    private Size _renderSize;

    public WidgetNode? SelectedWidget { get; private set; }
    public WidgetNode? HoveredWidget { get; private set; }
    public bool ShowOutlines { get; set; } = true;

    /// <summary>Returns a decoded image for a brush resource (texture/material), or null while it isn't available.</summary>
    public Func<UObject, SKBitmap?>? ImageResolver { get; set; }

    public event Action<WidgetNode?>? SelectionChanged;

    public WidgetDesignerCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    private double ViewWidth => ActualWidth > 0 ? ActualWidth : _renderSize.Width;
    private double ViewHeight => ActualHeight > 0 ? ActualHeight : _renderSize.Height;

    public void SetTree(WidgetNode? root, WidgetLayout? layout, float designWidth, float designHeight)
    {
        _root = root;
        _layout = layout;
        _designWidth = designWidth;
        _designHeight = designHeight;
        SelectedWidget = null;
        HoveredWidget = null;
        Relayout();
        ZoomToFit();
    }

    public void SetDesignSize(float width, float height)
    {
        _designWidth = width;
        _designHeight = height;
        Relayout();
        ZoomToFit();
    }

    /// <summary>Lays out and redraws the current tree again, keeping the view (after property edits).</summary>
    public void Refresh() => Relayout();

    private void Relayout()
    {
        if (_root != null && _layout != null)
            _layout.Layout(_root, _designWidth, _designHeight);
        InvalidateVisual();
    }

    public void Select(WidgetNode? node, bool raise = false)
    {
        SelectedWidget = node;
        if (raise)
            SelectionChanged?.Invoke(node);
        InvalidateVisual();
    }

    public void ZoomToFit()
    {
        if (ViewWidth <= 0 || ViewHeight <= 0)
            return;

        _scaling = (float) Math.Min((ViewWidth - 40) / _designWidth, (ViewHeight - 65) / _designHeight);
        _scaling = Math.Clamp(_scaling, 0.02f, 4f);
        _translation = new Vector(-(ViewWidth - _designWidth * _scaling) / 2, -(ViewHeight - 25 - _designHeight * _scaling) / 2);
        InvalidateVisual();
    }

    #region input

    private Point ToDesign(Point view) => new((view.X + _translation.X) / _scaling, (view.Y + _translation.Y) / _scaling);

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton == MouseButton.Left)
        {
            Select(HitTest(ToDesign(e.GetPosition(this))), true);
        }
        else if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _isPanning = true;
            CaptureMouse();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        _isPanning = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (_isPanning)
            _translation += _lastMousePosition - position;

        HoveredWidget = HitTest(ToDesign(position));
        _lastMousePosition = position;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var origin = e.GetPosition(this);
        var newScale = Math.Clamp(e.Delta > 0 ? _scaling * 1.2f : _scaling / 1.2f, 0.02f, 8f);
        var originVector = new Vector(origin.X, origin.Y);
        _translation = (_translation + originVector) * (newScale / _scaling) - originVector;
        _scaling = newScale;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Top-most visible widget under the point (draw order, ignoring render transforms).</summary>
    public WidgetNode? HitTest(Point designPoint)
    {
        WidgetNode? hit = null;
        if (_root != null)
            Visit(_root, (float) designPoint.X, (float) designPoint.Y);
        return hit;

        void Visit(WidgetNode node, float px, float py)
        {
            // hidden widgets keep their space but, like in Slate, neither they nor their children can be clicked
            if (!node.IsVisible || node.Visibility == "Hidden")
                return;
            if (px >= node.X && py >= node.Y && px <= node.X + node.Width && py <= node.Y + node.Height)
                hit = node;
            var (cx, cy) = node.HasContentTransform ? node.ParentToContent(px, py) : (px, py);
            foreach (var child in DrawOrder(node))
                Visit(child, cx, cy);
        }
    }

    private IEnumerable<WidgetNode> DrawOrder(WidgetNode node)
    {
        return _layout?.GetKind(node) == WidgetKind.CanvasPanel
            ? node.Children.OrderBy(o => WidgetProps.Int(o.Slot, "ZOrder"))
            : node.Children;
    }

    #endregion

    #region rendering

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        var canvas = e.Surface.Canvas;
        var dpiScale = ActualWidth > 0 ? (float) (e.Info.Width / ActualWidth) : 1f;
        canvas.Save();
        canvas.Scale(dpiScale);
        Render(canvas, (float) ActualWidth, (float) ActualHeight);
        canvas.Restore();
    }

    public void Render(SKCanvas canvas, float width, float height)
    {
        _renderSize = new Size(width, height);
        canvas.Clear(BackgroundColor);

        canvas.Save();
        canvas.Translate((float) -_translation.X, (float) -_translation.Y);
        canvas.Scale(_scaling);

        var designRect = new SKRect(0, 0, _designWidth, _designHeight);
        canvas.DrawRect(designRect, DesignAreaPaint);

        if (_root != null)
            DrawWidget(canvas, _root, 1f);

        canvas.DrawRect(designRect, DesignAreaBorder);

        using (var hover = HoverPaint.Clone())
        using (var selected = SelectedPaint.Clone())
        {
            hover.StrokeWidth /= _scaling;
            selected.StrokeWidth /= _scaling;
            if (HoveredWidget is { IsVisible: true } && HoveredWidget != SelectedWidget)
                canvas.DrawRect(DesignRectOf(HoveredWidget), hover);
            if (SelectedWidget is { IsVisible: true })
                canvas.DrawRect(DesignRectOf(SelectedWidget), selected);
        }
        canvas.Restore();

        canvas.DrawRect(new SKRect(0, height - 25, width, height), FooterPaint);
        var status = $"Design size: {_designWidth:0}x{_designHeight:0}    Zoom: {_scaling:N2}";
        if (HoveredWidget != null)
        {
            var (hx, hy, hw, hh) = HoveredWidget.DesignRect();
            status += $"    {HoveredWidget.DisplayName}  ({hx:0}, {hy:0})  {hw:0}x{hh:0}";
        }
        canvas.DrawText2(status, 5, height - 8, SKTextAlign.Left, GraphStyle.TextFont, TextPaint);
    }

    private static SKRect RectOf(WidgetNode node) => SKRect.Create(node.X, node.Y, node.Width, node.Height);

    private static SKRect DesignRectOf(WidgetNode node)
    {
        var (x, y, w, h) = node.DesignRect();
        return SKRect.Create(x, y, w, h);
    }

    private void DrawWidget(SKCanvas canvas, WidgetNode node, float parentOpacity)
    {
        if (!node.IsVisible)
            return;

        var e = node.Export;
        var opacity = parentOpacity * WidgetProps.Float(e, "RenderOpacity", 1);
        var hidden = node.Visibility == "Hidden";
        var rect = RectOf(node);

        canvas.Save();
        ApplyRenderTransform(canvas, node, rect);

        if (WidgetProps.Enum(e, "Clipping", "Inherit") is "ClipToBounds" or "ClipToBoundsAlways" || _layout?.GetKind(node) == WidgetKind.ScrollBox)
            canvas.ClipRect(rect);

        if (!hidden && opacity > 0)
            DrawSelf(canvas, node, rect, opacity);

        if (ShowOutlines && _scaling > 0.05f)
        {
            using var outline = OutlinePaint.Clone();
            outline.StrokeWidth = 1 / _scaling;
            canvas.DrawRect(rect, outline);
        }

        // a hidden widget hides its children too
        if (hidden)
        {
            canvas.Restore();
            return;
        }

        if (node.HasContentTransform)
        {
            canvas.Translate(node.ContentOriginX, node.ContentOriginY);
            canvas.Scale(node.ContentScaleX, node.ContentScaleY);
            canvas.Translate(-node.ContentOriginX, -node.ContentOriginY);
        }

        foreach (var child in DrawOrder(node))
            DrawWidget(canvas, child, opacity);

        canvas.Restore();
    }

    private static void ApplyRenderTransform(SKCanvas canvas, WidgetNode node, SKRect rect)
    {
        var transform = WidgetProps.Struct(node.Export, "RenderTransform");
        if (transform == null)
            return;

        var (tx, ty) = WidgetProps.Vector2(transform, "Translation");
        var (sx, sy) = WidgetProps.Vector2(transform, "Scale", 1, 1);
        var angle = WidgetProps.Float(transform, "Angle");
        var (px, py) = WidgetProps.Vector2(node.Export, "RenderTransformPivot", 0.5f, 0.5f);
        var pivotX = rect.Left + rect.Width * px;
        var pivotY = rect.Top + rect.Height * py;

        canvas.Translate(tx, ty);
        canvas.Translate(pivotX, pivotY);
        canvas.RotateDegrees(angle);
        canvas.Scale(sx, sy);
        canvas.Translate(-pivotX, -pivotY);
    }

    private void DrawSelf(SKCanvas canvas, WidgetNode node, SKRect rect, float opacity)
    {
        var e = node.Export;
        switch (_layout?.GetKind(node))
        {
            case WidgetKind.Image:
            {
                var tint = WidgetProps.LinearColor(e, "ColorAndOpacity", WidgetColor.White);
                DrawBrush(canvas, WidgetProps.Struct(e, "Brush"), rect, tint.WithAlpha(opacity));
                break;
            }
            case WidgetKind.Border:
            {
                var tint = WidgetProps.LinearColor(e, "BrushColor", WidgetColor.White);
                DrawBrush(canvas, WidgetProps.Struct(e, "Background"), rect, tint.WithAlpha(opacity));
                break;
            }
            case WidgetKind.Button:
            {
                var style = WidgetProps.Struct(e, "WidgetStyle");
                var tint = WidgetProps.LinearColor(e, "BackgroundColor", WidgetColor.White);
                DrawBrush(canvas, WidgetProps.Struct(style, "Normal"), rect, tint.WithAlpha(opacity));
                break;
            }
            case WidgetKind.TextBlock:
            case WidgetKind.RichTextBlock:
                DrawTextWidget(canvas, node, rect, opacity);
                break;
            case WidgetKind.ProgressBar:
            {
                var style = WidgetProps.Struct(e, "WidgetStyle");
                DrawBrush(canvas, WidgetProps.Struct(style, "BackgroundImage"), rect, new WidgetColor(0.2f, 0.2f, 0.2f, opacity), true);
                var percent = Math.Clamp(WidgetProps.Float(e, "Percent"), 0, 1);
                var fillTint = WidgetProps.LinearColor(e, "FillColorAndOpacity", WidgetColor.White).WithAlpha(opacity);
                var fillType = WidgetProps.Enum(e, "BarFillType", "LeftToRight");
                var fillRect = fillType switch
                {
                    "RightToLeft" => new SKRect(rect.Right - rect.Width * percent, rect.Top, rect.Right, rect.Bottom),
                    "TopToBottom" => new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + rect.Height * percent),
                    "BottomToTop" => new SKRect(rect.Left, rect.Bottom - rect.Height * percent, rect.Right, rect.Bottom),
                    _ => new SKRect(rect.Left, rect.Top, rect.Left + rect.Width * percent, rect.Bottom),
                };
                DrawBrush(canvas, WidgetProps.Struct(style, "FillImage"), fillRect, fillTint, true);
                break;
            }
            case WidgetKind.CheckBox:
            {
                var box = SKRect.Create(rect.Left, rect.MidY - 10, 20, 20);
                canvas.DrawRoundRect(box, 3, 3, PlaceholderFill);
                canvas.DrawRoundRect(box, 3, 3, PlaceholderStroke);
                if (WidgetProps.Enum(e, "CheckedState", "Unchecked") == "Checked")
                    canvas.DrawText2("✓", box.MidX, box.Bottom - 4, SKTextAlign.Center, GraphStyle.TextFont, TextPaint);
                break;
            }
            case WidgetKind.Slider:
            {
                var value = Math.Clamp(WidgetProps.Float(e, "Value"), 0, 1);
                canvas.DrawRect(new SKRect(rect.Left, rect.MidY - 2, rect.Right, rect.MidY + 2), PlaceholderStroke);
                canvas.DrawCircle(rect.Left + rect.Width * value, rect.MidY, 7, PlaceholderFill);
                canvas.DrawCircle(rect.Left + rect.Width * value, rect.MidY, 7, PlaceholderStroke);
                break;
            }
            case WidgetKind.EditableText:
            case WidgetKind.ComboBox:
            {
                canvas.DrawRect(rect, PlaceholderFill);
                canvas.DrawRect(rect, PlaceholderStroke);
                var text = WidgetProps.Text(e, "Text");
                if (string.IsNullOrEmpty(text))
                    text = WidgetProps.Text(e, "HintText");
                if (string.IsNullOrEmpty(text))
                    text = WidgetProps.Text(e, "SelectedOption");
                if (!string.IsNullOrEmpty(text))
                    canvas.DrawText2(text, rect.Left + 6, rect.MidY + 5, SKTextAlign.Left, GraphStyle.TextFont, TextPaint);
                break;
            }
            case WidgetKind.Throbber:
                for (var i = 0; i < 3; i++)
                    canvas.DrawCircle(rect.Left + rect.Width * (i + 1) / 4, rect.MidY, Math.Min(rect.Width, rect.Height) / 8, PlaceholderStroke);
                break;
        }
    }

    #endregion

    #region brushes

    private void DrawBrush(SKCanvas canvas, FStructFallback? brush, SKRect rect, WidgetColor tint, bool fillWhenEmpty = true)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var drawAs = WidgetProps.Enum(brush, "DrawAs", "Image");
        if (drawAs == "NoDrawType")
            return;

        var color = ToSkColor(Multiply(WidgetProps.SlateColor(brush, "TintColor", WidgetColor.White), tint));
        if (color.Alpha == 0)
            return;

        var resource = WidgetProps.Object(brush, "ResourceObject");
        var bitmap = resource != null ? ImageResolver?.Invoke(resource) : null;

        if (drawAs == "RoundedBox")
        {
            var outline = WidgetProps.Struct(brush, "OutlineSettings");
            var radius = RoundedRadius(outline, rect);
            using var fill = GraphStyle.Fill(color);
            canvas.DrawRoundRect(rect, radius, radius, fill);
            var width = WidgetProps.Float(outline, "Width");
            if (width > 0)
            {
                var outlineColor = ToSkColor(Multiply(WidgetProps.SlateColor(outline, "Color", new WidgetColor(0, 0, 0, 0)), tint));
                using var stroke = GraphStyle.Stroke(outlineColor, width);
                canvas.DrawRoundRect(rect, radius, radius, stroke);
            }
            return;
        }

        if (bitmap == null)
        {
            if (resource == null && fillWhenEmpty)
            {
                // a brush without resource draws a tinted white quad
                using var fill = GraphStyle.Fill(color);
                canvas.DrawRect(rect, fill);
            }
            else if (resource != null)
            {
                DrawUnresolvedResource(canvas, rect, color, resource.Name);
            }
            return;
        }

        using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium, ColorFilter = TintFilter(color) };
        if (drawAs is "Box" or "Border")
        {
            var margin = WidgetProps.Margin(brush, "Margin", WidgetMargin.Zero);
            var center = new SKRectI(
                (int) (margin.Left * bitmap.Width), (int) (margin.Top * bitmap.Height),
                bitmap.Width - (int) (margin.Right * bitmap.Width), bitmap.Height - (int) (margin.Bottom * bitmap.Height));
            if (center.Width > 0 && center.Height > 0 && (margin.Left > 0 || margin.Top > 0 || margin.Right > 0 || margin.Bottom > 0))
            {
                canvas.DrawBitmapNinePatch(bitmap, center, rect, paint);
                return;
            }
        }

        canvas.DrawBitmap(bitmap, rect, paint);
    }

    private static float RoundedRadius(FStructFallback? outline, SKRect rect)
    {
        if (WidgetProps.Enum(outline, "RoundingType", "FixedRadius") == "HalfHeightRadius")
            return rect.Height / 2;

        var radii = WidgetProps.GetRaw(outline, "CornerRadii");
        return radii switch
        {
            CUE4Parse.UE4.Objects.Core.Math.FVector4 v => (float) Math.Max(Math.Max(v.X, v.Y), Math.Max(v.Z, v.W)),
            FStructFallback s => Math.Max(WidgetProps.Float(s, "X"), WidgetProps.Float(s, "Y")),
            _ => 0,
        };
    }

    private static void DrawUnresolvedResource(SKCanvas canvas, SKRect rect, SKColor color, string name)
    {
        // materials (and textures still loading) are shown as a hatched tinted quad with the resource name
        using var fill = GraphStyle.Fill(color.WithAlpha((byte) (color.Alpha * 0.5f)));
        canvas.DrawRect(rect, fill);
        canvas.Save();
        canvas.ClipRect(rect);
        using var hatch = GraphStyle.Stroke(new SKColor(255, 255, 255, 40), 1);
        for (var x = rect.Left - rect.Height; x < rect.Right; x += 12)
            canvas.DrawLine(x, rect.Bottom, x + rect.Height, rect.Top, hatch);
        if (rect.Width > 40 && rect.Height > 14)
        {
            using var label = GraphStyle.Fill(new SKColor(255, 255, 255, 150));
            canvas.DrawText2(name, rect.Left + 3, rect.Top + 13, SKTextAlign.Left, GraphStyle.TextFont, label);
        }
        canvas.Restore();
    }

    private static SKColorFilter TintFilter(SKColor color)
    {
        float r = color.Red / 255f, g = color.Green / 255f, b = color.Blue / 255f, a = color.Alpha / 255f;
        return SKColorFilter.CreateColorMatrix(
        [
            r, 0, 0, 0, 0,
            0, g, 0, 0, 0,
            0, 0, b, 0, 0,
            0, 0, 0, a, 0,
        ]);
    }

    private static WidgetColor Multiply(WidgetColor a, WidgetColor b) => new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);

    /// <summary>UMG colors are linear, the preview draws in sRGB.</summary>
    private static SKColor ToSkColor(WidgetColor c)
    {
        static byte ToSrgb(float linear)
        {
            linear = Math.Clamp(linear, 0, 1);
            var srgb = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
            return (byte) Math.Round(srgb * 255);
        }

        return new SKColor(ToSrgb(c.R), ToSrgb(c.G), ToSrgb(c.B), (byte) Math.Round(Math.Clamp(c.A, 0, 1) * 255));
    }

    #endregion

    #region text

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex RichTextTags();

    public static string DisplayText(WidgetNode node, WidgetLayout layout)
    {
        var text = WidgetLayout.TextOf(node);
        return layout.GetKind(node) == WidgetKind.RichTextBlock ? RichTextTags().Replace(text, "") : text;
    }

    private void DrawTextWidget(SKCanvas canvas, WidgetNode node, SKRect rect, float opacity)
    {
        var text = DisplayText(node, _layout!);
        if (string.IsNullOrEmpty(text))
            return;

        var e = node.Export;
        var color = ToSkColor(WidgetProps.SlateColor(e, "ColorAndOpacity", WidgetColor.White).WithAlpha(opacity));
        var margin = WidgetProps.Margin(e, "Margin", WidgetMargin.Zero);
        var inner = new SKRect(rect.Left + margin.Left, rect.Top + margin.Top, rect.Right - margin.Right, rect.Bottom - margin.Bottom);
        var fontSize = WidgetLayout.FontSizePx(node);
        var wrap = WidgetProps.Bool(e, "AutoWrapText") ? inner.Width : 0;
        var justify = WidgetProps.Enum(e, "Justification", "Left");
        var font = FontOf(node);

        using var paint = GraphStyle.Fill(color);
        using var outline = font.OutlineSize > 0
            ? new SKPaint
            {
                IsAntialias = true, IsStroke = true, StrokeJoin = SKStrokeJoin.Round,
                StrokeWidth = font.OutlineSize * 2, Color = ToSkColor(font.OutlineColor.WithAlpha(opacity)),
            }
            : null;
        var shadowColor = WidgetProps.LinearColor(e, "ShadowColorAndOpacity", new WidgetColor(0, 0, 0, 0)).WithAlpha(opacity);
        using var shadow = shadowColor.A > 0 ? GraphStyle.Fill(ToSkColor(shadowColor)) : null;
        var (shadowX, shadowY) = WidgetProps.Vector2(e, "ShadowOffset", 1, 1);

        var lineHeight = fontSize * 1.2f;
        var y = inner.Top + fontSize;
        foreach (var line in WrapLines(text, o => font.Measure(o, fontSize), wrap))
        {
            var (x, align) = justify switch
            {
                "Center" => (inner.MidX, SKTextAlign.Center),
                "Right" => (inner.Right, SKTextAlign.Right),
                _ => (inner.Left, SKTextAlign.Left),
            };
            if (shadow != null)
                font.Draw(canvas, line, x + shadowX, y + shadowY, align, fontSize, shadow);
            if (outline != null)
                font.Draw(canvas, line, x, y, align, fontSize, outline);
            font.Draw(canvas, line, x, y, align, fontSize, paint);
            y += lineHeight;
        }
    }

    /// <summary>The game font of a text widget (FSlateFontInfo "Font"), loaded through the shared asset cache.</summary>
    public static WidgetFont FontOf(WidgetNode node) => WidgetAssetCache.GetFont(WidgetProps.Struct(node.Export, "Font"));

    /// <summary>Splits text on new lines and, when wrapping, on spaces or between characters (CJK has no spaces).</summary>
    public static List<string> WrapLines(string text, Func<string, float> measure, float wrapWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (wrapWidth <= 0 || measure(paragraph) <= wrapWidth)
            {
                lines.Add(paragraph);
                continue;
            }

            var current = "";
            foreach (var token in Regex.Split(paragraph, @"(\s+)").Where(o => o.Length > 0))
            {
                var pieces = measure(token) > wrapWidth ? token.Select(c => c.ToString()) : [token];
                foreach (var piece in pieces)
                {
                    if (current.Length > 0 && measure(current + piece) > wrapWidth)
                    {
                        lines.Add(current.TrimEnd());
                        current = piece.TrimStart();
                    }
                    else
                    {
                        current += piece;
                    }
                }
            }
            lines.Add(current);
        }
        return lines;
    }

    /// <summary>Text measuring for <see cref="WidgetLayout"/>, consistent with the renderer.</summary>
    public static (float Width, float Height) MeasureText(WidgetNode node, string text, float fontSizePx, float wrapWidth)
    {
        var font = FontOf(node);
        var lines = WrapLines(text, o => font.Measure(o, fontSizePx), wrapWidth);
        var width = lines.Count == 0 ? 0 : lines.Max(o => font.Measure(o, fontSizePx));
        return (width, Math.Max(1, lines.Count) * fontSizePx * 1.2f);
    }

    #endregion
}
