#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FModel.BlueprintGraph;
using FModel.BlueprintGraph.Nodes;
using Newtonsoft.Json;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using static FModel.BlueprintGraph.Engine.EngineBPData;

namespace FModel.Views.BlueprintViewer;

/// <summary>
/// Skia-rendered blueprint graph: right/middle drag to pan, wheel to zoom, left click/drag to select and move nodes.
/// Rendering ported from UE Blueprint Graph Viewer's GraphView2.
/// </summary>
public class BlueprintGraphCanvas : SKElement
{
    private static readonly SKColor BackgroundColor = new(38, 38, 38);
    private static readonly SKPaint FooterPaint = GraphStyle.Fill(21, 21, 21);
    private static readonly SKPaint GridPaint = GraphStyle.Stroke(1, 43, 43, 43, 128);
    private static readonly SKPaint GridPaint2 = GraphStyle.Stroke(2, 27, 27, 27);
    private static readonly SKPaint NodeBorder = GraphStyle.Stroke(SKColors.Black, 1);
    private static readonly SKPaint NodeSelectedBorder = GraphStyle.Stroke(SKColors.Orange, 2);
    private static readonly SKPaint NodeSearchResultBorder = GraphStyle.Stroke(SKColors.White, 3);
    private static readonly SKPaint NodeInferredMarker = GraphStyle.Fill(255, 170, 0);
    private static readonly SKPaint NodeValue = GraphStyle.Fill(34, 34, 34);
    private static readonly SKPaint NodeValueBorder = GraphStyle.Stroke(1, 153, 153, 153);
    private static readonly SKPaint NodeValueEditedBorder = GraphStyle.Stroke(2, 240, 198, 116);
    private static readonly SKPaint Selection = GraphStyle.Fill(30, 50, 80, 70);
    private static readonly SKPaint SelectionBorder = GraphStyle.Stroke(1, 30, 80, 150);
    private static readonly SKPaint TextPaint = GraphStyle.Fill(255, 255, 255);
    private static readonly SKPaint TextPaint2 = GraphStyle.Fill(255, 255, 255, 128);

    private const byte NodeBodyAlpha = 235;
    private const byte CompactNodeBodyAlpha = 175;
    private const int GridSnap = 8;

    private readonly List<BPNode> _nodes = [];
    private readonly List<(GraphPin Source, GraphPin Target)> _connections = [];

    public IReadOnlyList<BPNode> Nodes => _nodes;
    public List<BPNode> SelectedNodes { get; } = [];
    public IReadOnlyCollection<BPNode> SearchResult { get; set; } = [];
    public GraphPin? MouseOverPin { get; private set; }
    public GraphPin? SelectedPin { get; private set; }

    public event Action? SelectionChanged;

    public float ViewScale => _scaling;

    /// <summary>A right click (without panning) landed on a node (and maybe one of its pins); the node is selected first. The point is in view coordinates.</summary>
    public event Action<BPNode, GraphPin?, Point>? NodeContextRequested;

    // wires changed by pending (agent) edits
    private static readonly SKPaint EditedWire = GraphStyle.Stroke(3, 240, 198, 116);

    private Point _rightPressPosition;

    private Vector _translation;
    private float _scaling = 1;

    private bool _isPanning;
    private bool _isDraggingNode;
    private bool _isSelecting;
    private Point _lastMousePosition;
    private Point _mousePosOnGraph;
    private Point _mousePosOnGraphOnPress;
    private List<BPNode> _selectedNodesBeforeSelection = [];
    private BPNode? _mouseOverNode;

    public BlueprintGraphCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public void SetGraph(BPGraph? graph)
    {
        _nodes.Clear();
        _connections.Clear();
        SelectedNodes.Clear();
        SearchResult = [];
        _mouseOverNode = null;
        MouseOverPin = null;
        SelectedPin = null;

        if (graph != null)
        {
            _nodes.AddRange(graph.Nodes);
            CollectConnections();
        }

        _translation = new Vector(0, 0);
        _scaling = 1;
        InvalidateVisual();
    }

    /// <summary>Reads the links of the nodes again (after wires were edited) and redraws.</summary>
    public void RebuildConnections()
    {
        CollectConnections();
        InvalidateVisual();
    }

    private void CollectConnections()
    {
        _connections.Clear();
        var seen = new HashSet<(GraphPin, GraphPin)>();
        foreach (var node in _nodes)
        {
            foreach (var pin in node.Output)
                foreach (var target in pin.LinkedTo)
                    if (seen.Add((pin, target))) _connections.Add((pin, target));
            foreach (var pin in node.Input)
                foreach (var source in pin.LinkedTo)
                    if (seen.Add((source, pin))) _connections.Add((source, pin));
        }
    }

    /// <summary>Pan so the node sits in the upper left third of the view, at 100% zoom.</summary>
    public void FocusNode(BPNode? node, bool select = false)
    {
        if (node == null)
            return;

        _scaling = 1;
        _translation = new Vector(node.X - ViewWidth / 4, node.Y - ViewHeight / 4);
        if (select)
        {
            SelectedNodes.Clear();
            SelectedNodes.Add(node);
            SelectionChanged?.Invoke();
        }
        InvalidateVisual();
    }

    public void ZoomToFit()
    {
        if (_nodes.Count == 0 || ViewWidth <= 0 || ViewHeight <= 0)
            return;

        var minX = _nodes.Min(o => o.X);
        var minY = _nodes.Min(o => o.Y);
        var maxX = _nodes.Max(o => o.Right);
        var maxY = _nodes.Max(o => o.Bottom);
        var scale = (float) Math.Min(ViewWidth / Math.Max(1, maxX - minX + 100), (ViewHeight - 25) / Math.Max(1, maxY - minY + 100));
        _scaling = Math.Clamp(scale, 0.01f, 1f);
        _translation = new Vector((minX - 50) * _scaling, (minY - 50) * _scaling);
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _mousePosOnGraphOnPress = _mousePosOnGraph;

        if (e.ChangedButton == MouseButton.Left && TryHitValueBox(e.GetPosition(this), out var hitNode, out var hitPin, out _))
        {
            // clicking a value box selects its pin, without starting a node drag
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                SelectedNodes.Clear();
            if (!SelectedNodes.Contains(hitNode))
                SelectedNodes.Add(hitNode);
            SelectedPin = hitPin;
            SelectionChanged?.Invoke();
            InvalidateVisual();
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            UpdateNodesSelection();
            _isDraggingNode = _mouseOverNode != null;
            _isSelecting = !_isDraggingNode;
            _selectedNodesBeforeSelection = _isSelecting && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? [.. SelectedNodes] : [];
        }
        else if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _isPanning = true;
            if (e.ChangedButton == MouseButton.Right)
                _rightPressPosition = e.GetPosition(this);
        }

        CaptureMouse();
        InvalidateVisual();
    }

    private bool TryHitValueBox(Point position, out BPNode node, out GraphPin pin, out Rect screenRect)
    {
        node = null!;
        pin = null!;
        screenRect = default;
        if (_scaling <= 0.2f)
            return false;

        var graphX = (position.X + _translation.X) / _scaling;
        var graphY = (position.Y + _translation.Y) / _scaling;
        var hitNode = _nodes.LastOrDefault(o => graphX > o.X && graphY > o.Y && graphX < o.Right && graphY < o.Bottom);
        if (hitNode == null)
            return false;

        foreach (var candidate in hitNode.Input)
        {
            if (!HasValueBox(candidate))
                continue;

            var local = ValueBoxLocal(candidate, hitNode, out _);
            if (graphX < hitNode.X + local.Left || graphX > hitNode.X + local.Right || graphY < hitNode.Y + local.Top || graphY > hitNode.Y + local.Bottom)
                continue;

            node = hitNode;
            pin = candidate;
            screenRect = new Rect((hitNode.X + local.Left) * _scaling - _translation.X, (hitNode.Y + local.Top) * _scaling - _translation.Y,
                local.Width * _scaling, local.Height * _scaling);
            return true;
        }
        return false;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isSelecting)
            SelectionChanged?.Invoke();

        var position = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Right && (position - _rightPressPosition).Length < 4)
        {
            var graphX = (position.X + _translation.X) / _scaling;
            var graphY = (position.Y + _translation.Y) / _scaling;
            if (_nodes.LastOrDefault(o => graphX > o.X && graphY > o.Y && graphX < o.Right && graphY < o.Bottom) is { } node)
            {
                if (!SelectedNodes.Contains(node))
                {
                    SelectedNodes.Clear();
                    SelectedNodes.Add(node);
                    SelectedPin = null;
                    SelectionChanged?.Invoke();
                }
                _isPanning = false;
                ReleaseMouseCapture();
                InvalidateVisual();
                NodeContextRequested?.Invoke(node, MouseOverPin, position);
                return;
            }
        }

        _isPanning = false;
        _isDraggingNode = false;
        _isSelecting = false;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var newPos = e.GetPosition(this);
        var newGraphPos = new Point((newPos.X + _translation.X) / _scaling, (newPos.Y + _translation.Y) / _scaling);

        _mouseOverNode = _nodes.LastOrDefault(o =>
            newGraphPos.X > o.X && newGraphPos.Y > o.Y && newGraphPos.X < o.Right && newGraphPos.Y < o.Bottom);

        MouseOverPin = null;
        if (_mouseOverNode != null && newGraphPos.Y > _mouseOverNode.Y + (_mouseOverNode.HeaderHidden ? 0 : 30))
        {
            var pins = newGraphPos.X < _mouseOverNode.X + _mouseOverNode.NodeWidth / 2f ? _mouseOverNode.Input : _mouseOverNode.Output;
            MouseOverPin = pins.FirstOrDefault(o => !o.IsHidden && newGraphPos.Y < o.Y + 16);
        }

        if (_isPanning)
            _translation += _lastMousePosition - newPos;

        if (_isDraggingNode)
        {
            var offsetX = (float) (Math.Floor(newGraphPos.X / GridSnap) * GridSnap - Math.Floor(_mousePosOnGraph.X / GridSnap) * GridSnap);
            var offsetY = (float) (Math.Floor(newGraphPos.Y / GridSnap) * GridSnap - Math.Floor(_mousePosOnGraph.Y / GridSnap) * GridSnap);
            if (offsetX != 0 || offsetY != 0)
                foreach (var node in SelectedNodes)
                    node.SetPosition(node.X + offsetX, node.Y + offsetY);
        }

        if (_isSelecting)
        {
            var minX = Math.Min(_mousePosOnGraphOnPress.X, newGraphPos.X);
            var minY = Math.Min(_mousePosOnGraphOnPress.Y, newGraphPos.Y);
            var maxX = Math.Max(_mousePosOnGraphOnPress.X, newGraphPos.X);
            var maxY = Math.Max(_mousePosOnGraphOnPress.Y, newGraphPos.Y);

            var inRect = _nodes.Where(o => o.X < maxX && minX < o.Right && o.Y < maxY && minY < o.Bottom).ToList();
            SelectedNodes.Clear();
            SelectedNodes.AddRange(_selectedNodesBeforeSelection.Where(o => !inRect.Contains(o)));
            SelectedNodes.AddRange(inRect.Where(o => !_selectedNodesBeforeSelection.Contains(o)));
        }

        _lastMousePosition = newPos;
        _mousePosOnGraph = newGraphPos;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Zoom(e.Delta, e.GetPosition(this));
        e.Handled = true;
    }

    public void Zoom(double delta, Point origin)
    {
        var newScale = delta > 0 ? _scaling * 1.25f : delta < 0 ? _scaling / 1.25f : _scaling;
        newScale = (float) Math.Clamp(Math.Round(newScale, 4), 0.01, 3);
        if (newScale is > 0.9f and < 1.1f)
            newScale = 1;

        var originVector = new Vector(origin.X, origin.Y);
        var mul = newScale / _scaling;
        _scaling = newScale;
        _translation = (_translation + originVector) * mul - originVector;
        _translation = new Vector(Math.Round(_translation.X), Math.Round(_translation.Y));
        InvalidateVisual();
    }

    private void UpdateNodesSelection()
    {
        SelectedPin = MouseOverPin;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (_mouseOverNode == null || !SelectedNodes.Contains(_mouseOverNode))
        {
            if (!ctrl)
                SelectedNodes.Clear();
            if (_mouseOverNode != null)
                SelectedNodes.Add(_mouseOverNode);
        }
        else if (ctrl)
        {
            SelectedNodes.Remove(_mouseOverNode);
        }
        SelectionChanged?.Invoke();
    }

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

    private double ViewWidth => ActualWidth > 0 ? ActualWidth : _renderSize.Width;
    private double ViewHeight => ActualHeight > 0 ? ActualHeight : _renderSize.Height;
    private Size _renderSize;

    /// <summary>
    /// Draws the graph in view (device independent) coordinates; also used to render the graph off screen.
    /// </summary>
    public void Render(SKCanvas canvas, float width, float height)
    {
        _renderSize = new Size(width, height);
        canvas.Clear(BackgroundColor);
        canvas.Save();

        RenderGrid(canvas, width, height);

        canvas.Save();
        canvas.Translate((float) -_translation.X, (float) -_translation.Y);
        canvas.Scale(_scaling);

        foreach (var (source, target) in _connections)
        {
            if (!IsLineInView(source.X, source.Y, target.X, target.Y, width, height))
                continue;

            var paint = source.IsEdited || target.IsEdited ? EditedWire : GraphStyle.GetPinStroke(source.PinType.PinCategory);
            if (_scaling > 0.1)
            {
                using var path = new SKPath();
                path.MoveTo(source.X, source.Y);
                path.CubicTo(source.X + 70, source.Y, target.X - 70, target.Y, target.X, target.Y);
                canvas.DrawPath(path, paint);
            }
            else
            {
                canvas.DrawLine(source.X, source.Y, target.X, target.Y, paint);
            }
        }

        foreach (var node in _nodes)
        {
            if (IsNodeInView(node, width, height))
                RenderNode(canvas, node);
        }

        foreach (var node in SelectedNodes)
            canvas.DrawRoundRect(node.X, node.Y, node.NodeWidth, node.NodeHeight, 5, 5, NodeSelectedBorder);
        foreach (var node in SearchResult)
            canvas.DrawRoundRect(node.X, node.Y, node.NodeWidth, node.NodeHeight, 5, 5, NodeSearchResultBorder);

        if (_mouseOverNode != null && MouseOverPin != null)
            RenderPinHighlight(canvas, MouseOverPin);

        canvas.Restore();

        if (_isSelecting)
        {
            var start = new SKPoint((float) (_mousePosOnGraphOnPress.X * _scaling - _translation.X), (float) (_mousePosOnGraphOnPress.Y * _scaling - _translation.Y));
            var end = new SKPoint((float) (_mousePosOnGraph.X * _scaling - _translation.X), (float) (_mousePosOnGraph.Y * _scaling - _translation.Y));
            var rect = new SKRect(start.X, start.Y, end.X, end.Y);
            canvas.DrawRect(rect, Selection);
            canvas.DrawRect(rect, SelectionBorder);
        }

        canvas.DrawRect(new SKRect(0, height - 25, width, height), FooterPaint);
        canvas.DrawText2($"Zoom: {_scaling:N2}    Nodes: {_nodes.Count}    Selected: {SelectedNodes.Count}", 5, height - 8, SKTextAlign.Left, GraphStyle.TextFont, TextPaint);
        canvas.Restore();
    }

    private void RenderPinHighlight(SKCanvas canvas, GraphPin pin)
    {
        float width = 35;
        if (!pin.IsNameHidden)
        {
            width += GraphStyle.TextFont.Measure(pin.PinFriendlyName ?? "");
            if (pin.IsInput && !pin.IsConnected)
                width += Math.Clamp(GraphStyle.TextFont.Measure(pin.Value ?? ""), 30, 400) + 10;
        }
        if (pin.IsOutput)
            width *= -1;

        var rect = new SKRect(pin.X, pin.Y - 16, pin.X + width, pin.Y + 16);
        var color = GraphStyle.GetPinColor(pin.PinType.PinCategory).Color;
        using var paint = new SKPaint();
        paint.Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top),
            [color.WithAlpha(0), color.WithAlpha(100), color.WithAlpha(0)], [0, 0.7f, 1], SKShaderTileMode.Repeat);
        canvas.DrawRect(rect, paint);

        // long values don't fit in the pin box, show them in a popup
        if (pin.Value is { Length: > 55 })
        {
            var formatted = JsonConvert.SerializeObject(pin.Value).Trim('"');
            var popupRect = new SKRect((float) _mousePosOnGraph.X, (float) _mousePosOnGraph.Y + 15, (float) _mousePosOnGraph.X + 400, (float) _mousePosOnGraph.Y + 200);
            GraphStyle.DrawTextWithWrapping(canvas, formatted, popupRect, GraphStyle.TextFont, TextPaint, textHeight =>
            {
                var background = new SKRect(popupRect.Left - 5, popupRect.Top - 5, popupRect.Right + 5, popupRect.Top + 5 + textHeight);
                canvas.DrawRoundRect(background, 5, 5, NodeValue);
                canvas.DrawRoundRect(background, 5, 5, NodeValueBorder);
            });
        }
    }

    private bool IsNodeInView(BPNode node, float width, float height)
    {
        return node.Right >= _translation.X / _scaling &&
               node.Bottom >= _translation.Y / _scaling &&
               node.X <= (_translation.X + width) / _scaling &&
               node.Y <= (_translation.Y + height) / _scaling;
    }

    private bool IsLineInView(float x1, float y1, float x2, float y2, float width, float height)
    {
        var minX = _translation.X / _scaling;
        var maxX = (_translation.X + width) / _scaling;
        var minY = _translation.Y / _scaling;
        var maxY = (_translation.Y + height) / _scaling;
        return !(x1 < minX && x2 < minX) && !(x1 > maxX && x2 > maxX) && !(y1 < minY && y2 < minY) && !(y1 > maxY && y2 > maxY);
    }

    private void RenderNode(SKCanvas canvas, BPNode node)
    {
        canvas.Save();
        canvas.Translate(node.X, node.Y);

        var nodeRect = new SKRect(0, 0, node.NodeWidth, node.NodeHeight);
        var detailed = _scaling > 0.2;
        var bodyAlpha = node.NodeType is "K2Node_VariableGet" or "K2Node_VariableSet" or "K2Node_PromotableOperator" ? CompactNodeBodyAlpha : NodeBodyAlpha;

        using (var background = GraphStyle.Fill(GraphStyle.NodeBaseColor.WithAlpha(bodyAlpha)))
        {
            if (detailed) canvas.DrawRoundRect(nodeRect, 5, 5, background);
            else canvas.DrawRect(nodeRect, background);
        }

        if (node.TintPin != null && detailed)
        {
            var edge = GraphStyle.NodeBaseColor;
            var mid = GraphStyle.GetNodeTintColor(node.TintPin.PinType.PinCategory);
            using var tintPaint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(node.NodeWidth, 0), [edge, mid, edge], [0f, 0.5f, 1f], SKShaderTileMode.Clamp),
                Color = SKColors.Black.WithAlpha(bodyAlpha),
            };
            if (node.TintHeaderOnly)
            {
                var headerRect = new SKRect(0, 0, node.NodeWidth, 34);
                using var rr = new SKRoundRect(headerRect, 0);
                rr.SetNinePatch(headerRect, 5, 5, 5, 0);
                canvas.DrawRoundRect(rr, tintPaint);
            }
            else
            {
                canvas.DrawRoundRect(nodeRect, 5, 5, tintPaint);
            }
        }

        if (!node.HeaderHidden)
        {
            var headerRect = new SKRect(0, 0, node.NodeWidth, 25);
            using var headerPaint = GraphStyle.Fill(GraphStyle.GetNodeHeaderColor(node.NodeType, node.Pure).WithAlpha(NodeBodyAlpha));
            if (detailed)
            {
                using var rr = new SKRoundRect(headerRect, 0);
                rr.SetNinePatch(headerRect, 5, 5, 5, 0);
                canvas.DrawRoundRect(rr, headerPaint);
                canvas.DrawText2(node.Name, node.HeaderCenter ? node.NodeWidth / 2f : 7, 17,
                    node.HeaderCenter ? SKTextAlign.Center : SKTextAlign.Left, GraphStyle.TextFont, TextPaint);
            }
            else
            {
                canvas.DrawRect(headerRect, headerPaint);
            }
        }

        if (detailed)
        {
            if (node.ShowNameAsBody)
                canvas.DrawText2(node.Name, node.NodeWidth / 2f, node.NodeHeight / 2f + 10, SKTextAlign.Center, GraphStyle.TextFontBody, TextPaint2);
            if (node.CompactTitle)
                canvas.DrawText2(node.Name, node.NodeWidth / 2f, 22, SKTextAlign.Center, GraphStyle.TextFont, TextPaint);

            canvas.DrawRoundRect(nodeRect, 5, 5, NodeBorder);

            foreach (var pin in node.Input)
                RenderPin(canvas, node, pin);
            foreach (var pin in node.Output)
                RenderPin(canvas, node, pin);

            if (node.IsSignatureInferred)
                canvas.DrawCircle(node.NodeWidth - 6, 6, 4, NodeInferredMarker);
        }

        canvas.Restore();
    }

    private static void RenderPin(SKCanvas canvas, BPNode node, GraphPin pin)
    {
        if (pin.IsHidden)
            return;

        var connectorX = pin.IsOutput ? node.NodeWidth - 20 : 20;
        var connectorY = pin.Y - node.Y;
        GraphStyle.DrawPinConnector(canvas, connectorX, connectorY, pin.IsConnected, pin.PinType);

        var textY = connectorY + 4f;
        float nameWidth = 35;
        if (!pin.IsNameHidden)
        {
            canvas.DrawText2(pin.PinFriendlyName, pin.IsOutput ? node.NodeWidth - 35 : 35, textY,
                pin.IsOutput ? SKTextAlign.Right : SKTextAlign.Left, GraphStyle.TextFont, TextPaint);
            nameWidth = GraphStyle.TextFont.Measure(pin.PinFriendlyName ?? "") + 35;
        }

        if (HasValueBox(pin))
        {
            var rect = ValueBoxLocal(pin, node, out var formatted);
            canvas.DrawRoundRect(rect, 5, 5, NodeValue);
            canvas.DrawRoundRect(rect, 5, 5, pin.IsEdited ? NodeValueEditedBorder : NodeValueBorder);
            canvas.DrawText2(formatted.Length > 55 ? $"{formatted[..55]}..." : formatted, rect.Left + 5, textY, SKTextAlign.Left, GraphStyle.TextFont, TextPaint);
        }
    }

    private static bool HasValueBox(GraphPin pin) =>
        pin is { IsInput: true, IsConnected: false, IsHidden: false } && pin.PinType.PinCategory != PinType.exec;

    /// <summary>The rectangle of the pin's value box in node coordinates.</summary>
    private static SKRect ValueBoxLocal(GraphPin pin, BPNode node, out string formatted)
    {
        var nameWidth = pin.IsNameHidden ? 35f : GraphStyle.TextFont.Measure(pin.PinFriendlyName ?? "") + 35;
        formatted = JsonConvert.SerializeObject(pin.Value ?? "").Trim('"');
        var valueWidth = Math.Clamp(GraphStyle.TextFont.Measure(formatted), 30, 400) + 10;
        return SKRect.Create(nameWidth + 5, pin.Y - node.Y - 10, valueWidth, 20);
    }

    private void RenderGrid(SKCanvas canvas, float width, float height)
    {
        if (_scaling > 0.4)
            RenderLines(16, GridPaint);
        if (_scaling > 0.3)
            RenderLines(128, GridPaint2);
        if (_scaling is <= 0.3f and > 0.05f)
        {
            RenderLines(128, GridPaint);
            RenderLines(1024, GridPaint2);
        }

        void RenderLines(int cellWidth, SKPaint paint)
        {
            var cell = cellWidth * _scaling;
            var startX = (float) (-_translation.X % cell);
            var startY = (float) (-_translation.Y % cell);
            for (var x = startX; x < width + cell; x += cell)
                canvas.DrawLine(x, 0, x, height, paint);
            for (var y = startY; y < height + cell; y += cell)
                canvas.DrawLine(0, y, width, y, paint);
        }
    }
}
