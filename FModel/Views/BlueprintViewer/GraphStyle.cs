#nullable enable

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FModel.BlueprintGraph.Engine;
using SkiaSharp;
using static FModel.BlueprintGraph.Engine.EngineBPData;

namespace FModel.Views.BlueprintViewer;

/// <summary>
/// Colors, fonts and drawing helpers of the blueprint graph (ported from UE Blueprint Graph Viewer).
/// </summary>
internal static class GraphStyle
{
    public static readonly SKTypeface Typeface = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
    public static readonly SKFont TextFont = new(Typeface, 12.5f);
    public static readonly SKFont TextFontBody = new(Typeface, 28);

    private static readonly FrozenDictionary<PinType, SKPaint> PinsColors = new Dictionary<PinType, SKPaint>
    {
        { PinType.exec, Fill(SKColors.White) },
        { PinType.Bool, Fill(SKColors.Crimson) },
        { PinType.Byte, Fill(SKColors.DarkCyan) },
        { PinType.Enum, Fill(SKColors.DarkCyan) },
        { PinType.Int, Fill(SKColors.MediumAquamarine) },
        { PinType.Int64, Fill(SKColors.PaleGreen) },
        { PinType.Float, Fill(SKColors.Lime) },
        { PinType.Double, Fill(SKColors.Lime) },
        { PinType.Name, Fill(SKColors.BlueViolet) },
        { PinType.String, Fill(SKColors.Magenta) },
        { PinType.Text, Fill(SKColors.HotPink) },
        { PinType.Vector, Fill(SKColors.Gold) },
        { PinType.Rotator, Fill(SKColors.MediumSlateBlue) },
        { PinType.Transform, Fill(SKColors.Tomato) },
        { PinType.Interface, Fill(SKColors.Khaki) },
        { PinType.Object, Fill(SKColors.DeepSkyBlue) },
        { PinType.SoftObject, Fill(SKColors.PaleTurquoise) },
        { PinType.Class, Fill(SKColors.Indigo) },
        { PinType.SoftClass, Fill(SKColors.Violet) },
        { PinType.Delegate, Fill(SKColors.Crimson) },
        { PinType.Struct, Fill(SKColors.RoyalBlue) },
        { PinType.Wildcard, Fill(SKColors.Gray) },
        { PinType.Unknown, Fill(SKColors.Red) },
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<PinType, SKPaint> PinsStrokes =
        PinsColors.ToFrozenDictionary(o => o.Key, o => Stroke(o.Value.Color, 2));

    private static readonly SKColor CallColor = new(84, 129, 159);
    private static readonly SKColor EventColor = new(160, 27, 43);
    private static readonly SKColor FunctionColor = new(134, 39, 159);
    private static readonly SKColor FlowColor = new(130, 130, 130);
    private static readonly SKColor PureColor = new(102, 136, 97);
    private static readonly SKColor SwitchColor = new(149, 149, 16);

    private static readonly FrozenDictionary<string, SKColor> NodesColors = new Dictionary<string, SKColor>
    {
        { "AddDelegate", CallColor }, { "CallDelegate", CallColor }, { "CallFunction", CallColor },
        { "CallArrayFunction", CallColor }, { "ClearDelegate", CallColor }, { "RemoveDelegate", CallColor },
        { "SpawnActorFromClass", CallColor }, { "GenericCreateObject", CallColor }, { "AssignmentStatement", CallColor },
        { "CreateWidget", CallColor }, { "Tunnel", CallColor },
        { "Event", EventColor }, { "InputAction", EventColor }, { "InputKey", EventColor }, { "InputAxisKey", EventColor },
        { "InputAxisEvent", EventColor }, { "EnhancedInputAction", EventColor }, { "ComponentBoundEvent", EventColor },
        { "WidgetAnimationEvent", EventColor },
        { "FunctionEntry", FunctionColor }, { "FunctionResult", FunctionColor },
        { "IfThenElse", FlowColor }, { "ExecutionSequence", FlowColor }, { "MacroInstance", FlowColor },
        { "VariableSet", new SKColor(85, 85, 85) },
        { "DynamicCast", new SKColor(19, 114, 118) }, { "ClassDynamicCast", new SKColor(63, 20, 111) },
        { "Select", PureColor }, { "MakeArray", PureColor }, { "MakeMap", PureColor }, { "MakeSet", PureColor }, { "TemporaryVariable", PureColor },
        { "SwitchInteger", SwitchColor }, { "SwitchString", SwitchColor }, { "SwitchName", SwitchColor },
        { "SwitchEnum", SwitchColor }, { "Timeline", SwitchColor },
        { "MakeStruct", new SKColor(20, 50, 130) },
    }.ToFrozenDictionary();

    private static readonly SKPaint UnknownPaint = Fill(SKColors.Red);

    public static readonly SKColor NodeBaseColor = new(15, 17, 15);

    private static readonly FrozenDictionary<PinType, SKColor> NodeTintColors =
        PinsColors.ToFrozenDictionary(o => o.Key, o => BlendOver(o.Value.Color, NodeBaseColor, 0.18f));

    public static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true };
    public static SKPaint Fill(byte r, byte g, byte b, byte a = 255) => Fill(new SKColor(r, g, b, a));
    public static SKPaint Stroke(SKColor color, float width) => new() { Color = color, IsAntialias = true, IsStroke = true, StrokeWidth = width };
    public static SKPaint Stroke(float width, byte r, byte g, byte b, byte a = 255) => Stroke(new SKColor(r, g, b, a), width);

    public static SKPaint GetPinColor(PinType type) => PinsColors.GetValueOrDefault(type) ?? UnknownPaint;
    public static SKPaint GetPinStroke(PinType type) => PinsStrokes.GetValueOrDefault(type) ?? UnknownPaint;
    public static SKColor GetNodeTintColor(PinType type) => NodeTintColors.TryGetValue(type, out var color) ? color : SKColors.Red;

    public static SKColor GetNodeHeaderColor(string nodeType, bool pure)
    {
        var type = nodeType.StartsWith("K2Node_", StringComparison.Ordinal) ? nodeType[7..] : nodeType;
        if (pure && type is "CallFunction" or "CallArrayFunction")
            return PureColor;
        return NodesColors.TryGetValue(type, out var color) ? color : SKColors.Red;
    }

    private static SKColor BlendOver(SKColor top, SKColor bottom, float alpha)
    {
        byte Mix(byte t, byte b) => (byte) (t * alpha + b * (1 - alpha));
        return new SKColor(Mix(top.Red, bottom.Red), Mix(top.Green, bottom.Green), Mix(top.Blue, bottom.Blue));
    }

    /// <summary>
    /// Text width for the font (SkiaSharp 2.88 SKFont has no string measuring).
    /// </summary>
    public static float Measure(this SKFont font, string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        return paint.MeasureText(text);
    }

    private static void DrawAligned(SKCanvas canvas, string text, float x, float y, SKTextAlign align, SKFont font, SKPaint paint)
    {
        if (align != SKTextAlign.Left)
        {
            var width = font.Measure(text);
            x -= align == SKTextAlign.Center ? width / 2 : width;
        }
        canvas.DrawText(text, x, y, font, paint);
    }

    /// <summary>
    /// Draws text, switching to a system fallback typeface for glyphs the main font lacks (e.g. CJK names).
    /// </summary>
    public static void DrawText2(this SKCanvas canvas, string text, float x, float y, SKTextAlign align, SKFont font, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (font.ContainsGlyphs(text))
        {
            DrawAligned(canvas, text, x, y, align, font, paint);
            return;
        }

        SKTypeface? fallback = null;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (font.ContainsGlyphs(element))
                continue;

            fallback = SKFontManager.Default.MatchCharacter(font.Typeface.FamilyName, font.Typeface.FontStyle, null, element.EnumerateRunes().First().Value);
            if (fallback != null)
                break;
        }

        using var fallbackFont = fallback != null ? new SKFont(fallback, font.Size) : null;
        DrawAligned(canvas, text, x, y, align, fallbackFont ?? font, paint);
    }

    public static void DrawTextWithWrapping(SKCanvas canvas, string text, SKRect rect, SKFont font, SKPaint paint, Action<float> beforeRendering)
    {
        var spaceWidth = font.Measure(" ");
        var wordX = rect.Left;
        var wordY = rect.Top + font.Size;

        var words = text.Split(' ');
        var coords = new SKPoint[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            var wordWidth = font.Measure(words[i]);
            if (wordWidth > rect.Right - wordX)
            {
                wordY += font.Spacing;
                wordX = rect.Left;
            }
            coords[i] = new SKPoint(wordX, wordY);
            wordX += wordWidth + spaceWidth;
        }

        beforeRendering(wordY - rect.Top);
        for (var i = 0; i < words.Length; i++)
            canvas.DrawText2(words[i], coords[i].X, coords[i].Y, SKTextAlign.Left, font, paint);
    }

    private static readonly SKPath ExecConnector = SKPath.ParseSvgPathData("M1,1 L1,13 L4,13 L13,7 L4,1 Z");
    private static readonly SKPath DelegateConnector = SKPath.ParseSvgPathData("M4,2 L2,4 L2,10 L4,12 L10,12 L12,10 L12,4 L10,2 Z");
    private static readonly SKPath ArrayConnector = SKPath.ParseSvgPathData("M0,4H4V0H0Z M0,9H4V5H0Z M0,14H4V10H0Z M5,4H9V0H5Z M5,9H9V5H5Z M5,14H9V10H5Z M10,14H14V10H10Z M10,9H14V5H10Z M10,0V4H14V0Z");
    private static readonly SKPath MapConnector = SKPath.ParseSvgPathData("M0,4H4V0H0Z M0,9H4V5H0Z M0,14H4V10H0Z");
    private static readonly SKPath MapConnector2 = SKPath.ParseSvgPathData("M5,4H14V0H5Z M5,9H14V5H5Z M5,14H14V10H5Z");
    private static readonly SKPath SetConnector = SKPath.ParseSvgPathData("M8 1c2.6 0 1 5 3 6-2 1-.4 6-3 6v1c2 0 3 0 3.5-5 0-1 1.5-1 2.5-1V6c-1 0-2.5 0-2.5-1C11 0 10 0 8 0ZM6 1C3.4 1 5 6 3 7c2 1 .4 6 3 6v1c-2 0-3 0-3.5-5C2.5 8 0 8 0 8V6S2.5 6 2.5 5C3 0 4 0 6 0Z");

    public static void DrawPinConnector(SKCanvas canvas, float x, float y, bool isConnected, GraphPinType type)
    {
        var forceFill = false;
        SKPath? path1;
        SKPath? path2 = null;
        if (type.PinCategory == PinType.exec)
        {
            path1 = ExecConnector;
        }
        else if (type.PinCategory == PinType.Delegate)
        {
            path1 = DelegateConnector;
        }
        else
        {
            path1 = type.ContainerType switch
            {
                EngineEnums.EPinContainerType.Array => ArrayConnector,
                EngineEnums.EPinContainerType.Set => SetConnector,
                EngineEnums.EPinContainerType.Map => MapConnector,
                _ => null,
            };
            forceFill = path1 != null;
            path2 = type.ContainerType == EngineEnums.EPinContainerType.Map ? MapConnector2 : null;
        }

        var paint = isConnected || forceFill ? GetPinColor(type.PinCategory) : GetPinStroke(type.PinCategory);
        if (path1 == null)
        {
            canvas.DrawCircle(x, y, type.IsReference ? 4f : 7.5f, paint);
            return;
        }

        canvas.Save();
        canvas.Translate(x - 6, y - 7);
        canvas.DrawPath(path1, paint);
        if (path2 != null)
            canvas.DrawPath(path2, GetPinColor(type.PinSubCategory));
        canvas.Restore();
    }
}
