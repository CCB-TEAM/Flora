#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FModel.BlueprintGraph.Animation;
using FModel.BlueprintGraph.Widgets;
using FModel.Settings;
using FModel.ViewModels;
using SkiaSharp;

namespace FModel.Views.BlueprintViewer;

/// <summary>What <see cref="AnimationFilmstrip.RenderAsync"/> made.</summary>
public sealed record FilmstripResult(string File, int Width, int Height, int FrameWidth, int FrameHeight, float DesignWidth, float DesignHeight, string Note, List<string> Frames);

/// <summary>
/// Renders a few frames of a widget animation, side by side with their times, into one PNG. The frames come from a separate copy of the
/// widget tree (the open designer is not touched) drawn by the designer canvas with the animation's values applied.
/// </summary>
public static class AnimationFilmstrip
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static async Task<FilmstripResult> RenderAsync(WidgetDesignerViewModel designer, WidgetAnim animation, IReadOnlyList<double> times, int frameWidth,
        string? resolution, string file)
    {
        var scene = designer.CreatePreviewScene() ?? throw new InvalidOperationException("The widget tree could not be read.");
        var (root, layout) = scene;
        await designer.PreloadImagesAsync();

        var note = "";
        float width, height;
        if (layout.GetKind(root) == WidgetKind.CanvasPanel && !string.IsNullOrWhiteSpace(resolution))
        {
            var parts = resolution.ToLowerInvariant().Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var screenW) || !int.TryParse(parts[1], out var screenH) || screenW < 200 || screenH < 200)
                throw new ArgumentException("The resolution must look like 1920x1080.");
            (width, height) = ScreenScale.WidgetSpace(screenW, screenH);
            note = $"laid out for a {screenW}x{screenH} screen (UI scale {ScreenScale.Scale(screenW, screenH):0.####}, widget space {width:0}x{height:0}); ";
        }
        else
        {
            (width, height) = DesignSize(layout, root);
        }

        var canvas = new WidgetDesignerCanvas { ImageResolver = designer.ResolveImage };
        canvas.SetTree(root, layout, width, height);

        // widgets the animation binds to that this blueprint's tree does not have (nothing to draw for them)
        var owned = AnimationOverlay.OwnedNodes(root, animation);
        var missing = animation.Bindings.Select(o => o.Widget).Where(o => !owned.ContainsKey(o)).Distinct().ToList();
        if (missing.Count > 0)
            note += $"widget(s) not found in the tree: {string.Join(", ", missing)}; ";

        var frameHeight = Math.Clamp((int) Math.Round(frameWidth * height / width), 40, 1200);
        var frames = new List<SKBitmap>();
        var lines = new List<string>();
        try
        {
            foreach (var time in times)
            {
                var state = AnimationOverlay.Evaluate(animation, time);
                canvas.SetAnimation(AnimationOverlay.Bind(root, state, animation));
                frames.Add(canvas.RenderPreview(frameWidth, frameHeight));
                lines.Add($"t = {Num(time)} s: {Describe(state)}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var (stripWidth, stripHeight) = Save(frames, times, frameWidth, frameHeight, file);
            return new FilmstripResult(file, stripWidth, stripHeight, frameWidth, frameHeight, width, height, note, lines);
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    /// <summary>Like the designer: a screen fills the default design size, a component is shown at its desired size.</summary>
    private static (float Width, float Height) DesignSize(WidgetLayout layout, WidgetNode root)
    {
        float defaultWidth = UserSettings.Default.WidgetDesignerWidth, defaultHeight = UserSettings.Default.WidgetDesignerHeight;
        if (layout.GetKind(root) != WidgetKind.CanvasPanel)
        {
            layout.Layout(root, defaultWidth, defaultHeight);
            if (root.DesiredWidth >= 1 && root.DesiredHeight >= 1)
                return (root.DesiredWidth, root.DesiredHeight);
        }
        return (defaultWidth, defaultHeight);
    }

    private static string Num(double value) => value.ToString("0.###", Invariant);

    /// <summary>One line telling what the animation sets at this moment.</summary>
    public static string Describe(Dictionary<string, WidgetAnimOverride> state)
    {
        var parts = new List<string>();
        foreach (var (widget, o) in state.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            var items = new List<string>();

            void Add(string label, float? value)
            {
                if (value != null)
                    items.Add($"{label} {value.Value.ToString("0.##", Invariant)}");
            }

            Add("opacity", o.Opacity);
            Add("x", o.TranslateX);
            Add("y", o.TranslateY);
            Add("scale.x", o.ScaleX);
            Add("scale.y", o.ScaleY);
            Add("angle", o.Angle);
            foreach (var (label, color) in new[] { ("color", o.ColorAndOpacity), ("brush", o.BrushColor), ("fill", o.FillColor) })
            {
                if (color != null)
                    items.Add($"{label} ({color.R?.ToString("0.##", Invariant)},{color.G?.ToString("0.##", Invariant)},{color.B?.ToString("0.##", Invariant)},{color.A?.ToString("0.##", Invariant)})");
            }
            if (items.Count > 0)
                parts.Add($"{widget}: {string.Join(", ", items)}");
        }
        return parts.Count == 0 ? "nothing is set" : string.Join("; ", parts);
    }

    private static (int Width, int Height) Save(List<SKBitmap> frames, IReadOnlyList<double> times, int frameWidth, int frameHeight, string file)
    {
        const int pad = 8, labelHeight = 22;
        var columns = Math.Min(frames.Count, frameWidth <= 400 ? 4 : 3);
        var rows = (frames.Count + columns - 1) / columns;
        var width = pad + columns * (frameWidth + pad);
        var height = pad + rows * (frameHeight + labelHeight + pad);

        using var strip = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(strip))
        using (var paint = new SKPaint { Color = new SKColor(230, 230, 230), TextSize = 14, IsAntialias = true })
        {
            canvas.Clear(new SKColor(24, 24, 24));
            for (var i = 0; i < frames.Count; i++)
            {
                var x = pad + i % columns * (frameWidth + pad);
                var y = pad + i / columns * (frameHeight + labelHeight + pad);
                canvas.DrawBitmap(frames[i], x, y);
                canvas.DrawText($"t = {Num(times[i])} s", x + 2, y + frameHeight + 16, paint);
            }
        }

        using var image = SKImage.FromBitmap(strip);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(file, data.ToArray());
        return (width, height);
    }
}
