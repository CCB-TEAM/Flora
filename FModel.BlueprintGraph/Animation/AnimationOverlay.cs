using System;
using System.Collections.Generic;
using System.Linq;
using FModel.BlueprintGraph.Widgets;

namespace FModel.BlueprintGraph.Animation;

/// <summary>A color whose components an animation may drive one by one; components that are null keep the widget's own value.</summary>
public sealed class ColorOverride
{
    public float? R, G, B, A;

    public WidgetColor ApplyTo(WidgetColor original) => new(R ?? original.R, G ?? original.G, B ?? original.B, A ?? original.A);
}

/// <summary>
/// What an animation sets on one widget at one moment. Every member that is null leaves the widget's own value alone.
/// Only the properties the game's animations mostly drive are here: RenderTransform, RenderOpacity and three color properties.
/// </summary>
public sealed class WidgetAnimOverride
{
    public float? Opacity;
    public float? TranslateX, TranslateY, ScaleX, ScaleY, ShearX, ShearY, Angle;
    public ColorOverride? ColorAndOpacity, BrushColor, FillColor;

    /// <summary>
    /// Only for the root of a tree, from an animation of the user widget itself: the alpha of the widget's ColorAndOpacity, which fades
    /// everything inside it (the color components of that tint are not drawn).
    /// </summary>
    public float? TintAlpha;

    public bool HasTransform => TranslateX != null || TranslateY != null || ScaleX != null || ScaleY != null || ShearX != null || ShearY != null || Angle != null;
}

/// <summary>Turns a <see cref="WidgetAnim"/> at a time into per-widget overrides, and finds the widgets of a blueprint they belong to.</summary>
public static class AnimationOverlay
{
    /// <summary>True when the preview can apply the track to a widget (other tracks are listed but not drawn).</summary>
    public static bool IsSupported(AnimTrack track) => track.Kind switch
    {
        AnimTrackKind.Transform2D => track.Property == "RenderTransform",
        AnimTrackKind.Float => track.Property == "RenderOpacity",
        // ContentColorAndOpacity (a tint for everything inside a border or button) is not drawn yet
        AnimTrackKind.Color => track.Property is "ColorAndOpacity" or "BrushColor" or "FillColorAndOpacity",
        _ => false,
    };

    /// <summary>
    /// Overrides by widget name at <paramref name="seconds"/> on the animation timeline (0 = the start of the animation).
    /// Several bindings of the same widget are merged.
    /// </summary>
    public static Dictionary<string, WidgetAnimOverride> Evaluate(WidgetAnim animation, double seconds)
    {
        var result = new Dictionary<string, WidgetAnimOverride>(StringComparer.Ordinal);
        foreach (var binding in animation.Bindings)
        {
            if (!result.TryGetValue(binding.Widget, out var target))
                result[binding.Widget] = target = new WidgetAnimOverride();

            foreach (var track in binding.Tracks)
            {
                if (IsSupported(track))
                    Apply(track, seconds, target);
            }
        }
        return result;
    }

    private static void Apply(AnimTrack track, double seconds, WidgetAnimOverride target)
    {
        ColorOverride? color = null;
        if (track.Kind == AnimTrackKind.Color)
        {
            color = track.Property switch
            {
                "ColorAndOpacity" => target.ColorAndOpacity ??= new ColorOverride(),
                "BrushColor" => target.BrushColor ??= new ColorOverride(),
                _ => target.FillColor ??= new ColorOverride(),
            };
        }

        foreach (var channel in track.Channels)
        {
            if (ChannelEvaluator.EvaluateSeconds(channel, seconds) is not { } value)
                continue;

            var v = (float) value;
            switch (track.Kind)
            {
                case AnimTrackKind.Transform2D:
                    switch (channel.Name)
                    {
                        case "Translation.X": target.TranslateX = v; break;
                        case "Translation.Y": target.TranslateY = v; break;
                        case "Scale.X": target.ScaleX = v; break;
                        case "Scale.Y": target.ScaleY = v; break;
                        case "Shear.X": target.ShearX = v; break;
                        case "Shear.Y": target.ShearY = v; break;
                        case "Angle": target.Angle = v; break;
                    }
                    break;

                case AnimTrackKind.Float:
                    target.Opacity = v;
                    break;

                case AnimTrackKind.Color:
                    switch (channel.Name)
                    {
                        case "R": color!.R = v; break;
                        case "G": color!.G = v; break;
                        case "B": color!.B = v; break;
                        case "A": color!.A = v; break;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// The widgets an animation of this blueprint can address, by name. A user widget (another blueprint placed in the tree)
    /// counts, but what is inside it belongs to that blueprint's own animations. With <paramref name="animation"/> given, its bindings of the
    /// user widget itself (see <see cref="AnimBinding.IsRootWidget"/>) stand for the root of the tree.
    /// </summary>
    public static Dictionary<string, WidgetNode> OwnedNodes(WidgetNode root, WidgetAnim? animation = null)
    {
        var result = new Dictionary<string, WidgetNode>(StringComparer.Ordinal);
        Visit(root);
        if (animation != null)
        {
            foreach (var binding in animation.Bindings.Where(o => o.IsRootWidget))
                result[binding.Widget] = root;
        }
        return result;

        void Visit(WidgetNode node)
        {
            result.TryAdd(node.Name, node);
            if (node.IsUserWidget)
                return;
            foreach (var child in node.Children)
                Visit(child);
        }
    }

    /// <summary>
    /// Overrides by node, ready for the designer canvas. Widgets the tree does not have are skipped. Pass the animation so that its
    /// bindings of the user widget itself land on the root of the tree.
    /// </summary>
    public static Dictionary<WidgetNode, WidgetAnimOverride> Bind(WidgetNode root, Dictionary<string, WidgetAnimOverride> state, WidgetAnim? animation = null)
    {
        var owned = OwnedNodes(root, animation);
        var rootNames = animation?.Bindings.Where(o => o.IsRootWidget).Select(o => o.Widget).ToHashSet(StringComparer.Ordinal) ?? [];
        var result = new Dictionary<WidgetNode, WidgetAnimOverride>();
        foreach (var (name, value) in state)
        {
            if (!owned.TryGetValue(name, out var node))
                continue;

            var item = rootNames.Contains(name) ? ForUserWidget(value) : value;
            // the root can be addressed twice, as the user widget and by its own name: both apply
            result[node] = result.TryGetValue(node, out var existing) ? Merge(existing, item) : item;
        }
        return result;
    }

    /// <summary>
    /// What an animation of the user widget itself sets, as an override of the root of its tree. The widget's ColorAndOpacity tints
    /// everything inside it; the preview draws its alpha as a fade of the whole tree and leaves the color components out.
    /// </summary>
    private static WidgetAnimOverride ForUserWidget(WidgetAnimOverride o) => new()
    {
        Opacity = o.Opacity, TranslateX = o.TranslateX, TranslateY = o.TranslateY, ScaleX = o.ScaleX, ScaleY = o.ScaleY,
        ShearX = o.ShearX, ShearY = o.ShearY, Angle = o.Angle, BrushColor = o.BrushColor, FillColor = o.FillColor,
        TintAlpha = o.ColorAndOpacity?.A,
    };

    /// <summary>Two overrides of one widget: what <paramref name="later"/> sets wins, the rest comes from <paramref name="earlier"/>.</summary>
    private static WidgetAnimOverride Merge(WidgetAnimOverride earlier, WidgetAnimOverride later) => new()
    {
        Opacity = later.Opacity ?? earlier.Opacity,
        TranslateX = later.TranslateX ?? earlier.TranslateX, TranslateY = later.TranslateY ?? earlier.TranslateY,
        ScaleX = later.ScaleX ?? earlier.ScaleX, ScaleY = later.ScaleY ?? earlier.ScaleY,
        ShearX = later.ShearX ?? earlier.ShearX, ShearY = later.ShearY ?? earlier.ShearY, Angle = later.Angle ?? earlier.Angle,
        ColorAndOpacity = later.ColorAndOpacity ?? earlier.ColorAndOpacity,
        BrushColor = later.BrushColor ?? earlier.BrushColor, FillColor = later.FillColor ?? earlier.FillColor,
        TintAlpha = later.TintAlpha ?? earlier.TintAlpha,
    };
}
