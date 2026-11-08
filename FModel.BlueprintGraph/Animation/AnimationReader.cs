using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Curves;
using CUE4Parse.UE4.Objects.MovieScene;
using FModel.BlueprintGraph.Widgets;

namespace FModel.BlueprintGraph.Animation;

/// <summary>
/// Reads the UMG animations of a cooked widget blueprint through CUE4Parse (read only):
/// WidgetBlueprintGeneratedClass.Animations[] -> WidgetAnimation.MovieScene -> ObjectBindings[].Tracks[] -> Sections[] -> float channels.
/// </summary>
public static class AnimationReader
{
    public static List<WidgetAnim> Read(UWidgetBlueprintGeneratedClass widgetClass)
    {
        var result = new List<WidgetAnim>();
        foreach (var animation in WidgetProps.Objects(widgetClass, "Animations"))
        {
            try
            {
                result.Add(ReadAnimation(animation));
            }
            catch (Exception e)
            {
                // one unreadable animation must not hide the others
                result.Add(new WidgetAnim { Name = Clean(animation.Name), Variable = Clean(animation.Name), Error = $"{e.GetType().Name}: {e.Message}" });
            }
        }

        // how the blueprint code plays them (the speed decides how long they last on screen)
        List<PlayCall> calls;
        try
        {
            calls = PlayCallScanner.Scan(widgetClass);
        }
        catch
        {
            calls = [];
        }
        foreach (var anim in result)
            anim.PlayCalls = calls.Where(o => string.Equals(o.Animation, anim.Variable, StringComparison.Ordinal)).ToList();
        return result;
    }

    private static string Clean(string exportName) => exportName.EndsWith("_INST", StringComparison.Ordinal) ? exportName[..^5] : exportName;

    public static WidgetAnim ReadAnimation(UObject animation)
    {
        var label = WidgetProps.Text(animation, "DisplayLabel");
        var anim = new WidgetAnim { Name = !string.IsNullOrEmpty(label) && label != "None" ? label : Clean(animation.Name), Variable = Clean(animation.Name) };

        var scene = WidgetProps.Object(animation, "MovieScene") ?? throw new InvalidOperationException("the animation has no MovieScene");

        if (WidgetProps.GetRaw(scene, "PlaybackRange") is FMovieSceneFrameRange range)
        {
            anim.PlaybackStartTicks = range.Value.LowerBound.Value.Value;
            anim.PlaybackEndTicks = range.Value.UpperBound.Value.Value;
            anim.PlaybackStartExclusive = range.Value.LowerBound.Type == ERangeBoundTypes.Exclusive;
            anim.PlaybackEndExclusive = range.Value.UpperBound.Type == ERangeBoundTypes.Exclusive;
        }

        anim.DisplayRate = WidgetProps.GetRaw(scene, "DisplayRate") switch
        {
            FFrameRate r when r.Denominator != 0 => (double) r.Numerator / r.Denominator,
            FStructFallback s when WidgetProps.Int(s, "Denominator", 1) != 0 => (double) WidgetProps.Int(s, "Numerator") / WidgetProps.Int(s, "Denominator", 1),
            _ => 0,
        };

        // bindings of the user widget itself (the animation fades or moves the whole blueprint): WidgetAnimation.AnimationBindings flags them
        var rootNames = WidgetProps.Structs(animation, "AnimationBindings")
            .Where(o => WidgetProps.Bool(o, "bIsRootWidget"))
            .Select(o => WidgetProps.Text(o, "WidgetName"))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var binding in WidgetProps.Structs(scene, "ObjectBindings"))
        {
            var name = WidgetProps.Text(binding, "BindingName");
            var item = new AnimBinding { Widget = name, IsRootWidget = rootNames.Contains(name) };
            foreach (var track in WidgetProps.Objects(binding, "Tracks"))
                item.Tracks.Add(ReadTrack(track));
            anim.Bindings.Add(item);
        }

        // the first channel names the tick resolution (it is stored per channel; the MovieScene keeps its default unwritten)
        var first = anim.AllChannels.FirstOrDefault();
        if (first != null)
            anim.TicksPerSecond = first.TicksPerSecond;

        // audio markers: scene level tracks (older cooks call them MasterTracks)
        foreach (var name in new[] { "Tracks", "MasterTracks" })
            foreach (var track in WidgetProps.Objects(scene, name))
                ReadMarkers(track, anim);

        return anim;
    }

    private static AnimTrack ReadTrack(UObject track)
    {
        var result = new AnimTrack
        {
            ClassName = track.ExportType,
            Property = WidgetProps.Text(WidgetProps.Struct(track, "PropertyBinding"), "PropertyName"),
            Kind = track.ExportType switch
            {
                "MovieScene2DTransformTrack" => AnimTrackKind.Transform2D,
                "MovieSceneFloatTrack" => AnimTrackKind.Float,
                "MovieSceneColorTrack" => AnimTrackKind.Color,
                _ => AnimTrackKind.Other,
            },
        };

        var sectionIndex = 0;
        foreach (var section in WidgetProps.Objects(track, "Sections"))
        {
            var item = new AnimSection { Index = sectionIndex++ };
            if (WidgetProps.GetRaw(section, "SectionRange") is FMovieSceneFrameRange range)
            {
                item.StartTicks = range.Value.LowerBound.Value.Value;
                item.EndTicks = range.Value.UpperBound.Value.Value;
                item.StartExclusive = range.Value.LowerBound.Type == ERangeBoundTypes.Exclusive;
                item.EndExclusive = range.Value.UpperBound.Type == ERangeBoundTypes.Exclusive;
            }

            // fixed arrays such as Translation[2] come as one tag per element, told apart by ArrayIndex
            foreach (var tag in section.Properties)
            {
                if (tag.Tag?.GenericValue is FScriptStruct { StructType: FMovieSceneChannel<float> channel })
                {
                    var curve = ToChannel(channel, ChannelName(result.Kind, tag.Name.Text, tag.ArrayIndex), result.Property);
                    result.Channels.Add(curve);
                    item.Channels.Add(curve);
                }
            }
            result.Sections.Add(item);
        }
        return result;
    }

    private static string ChannelName(AnimTrackKind kind, string property, int index)
    {
        var axis = index switch { 0 => "X", 1 => "Y", _ => $"[{index}]" };
        switch (kind)
        {
            case AnimTrackKind.Transform2D:
                return property switch
                {
                    "Translation" or "Scale" or "Shear" => $"{property}.{axis}",
                    "Rotation" => "Angle",
                    _ => index == 0 ? property : $"{property}[{index}]",
                };
            case AnimTrackKind.Color:
                return property switch
                {
                    "RedCurve" => "R",
                    "GreenCurve" => "G",
                    "BlueCurve" => "B",
                    "AlphaCurve" => "A",
                    _ => index == 0 ? property : $"{property}[{index}]",
                };
            case AnimTrackKind.Float:
                return property == "FloatCurve" ? "Value" : index == 0 ? property : $"{property}[{index}]";
            default:
                return index == 0 ? property : $"{property}[{index}]";
        }
    }

    private static AnimChannel ToChannel(FMovieSceneChannel<float> source, string name, string property)
    {
        var tick = source.TickResolution.Denominator != 0 ? (double) source.TickResolution.Numerator / source.TickResolution.Denominator : 0;
        return new AnimChannel
        {
            Name = name,
            Property = property,
            Times = source.Times.Select(o => o.Value).ToArray(),
            Keys = source.Values.Select(o => new AnimKey(
                o.Value, o.Tangent.ArriveTangent, o.Tangent.LeaveTangent, o.Tangent.ArriveTangentWeight, o.Tangent.LeaveTangentWeight,
                (KeyWeightMode) (byte) o.Tangent.TangentWeightMode, (KeyInterp) (byte) o.InterpMode, (KeyTangentMode) (byte) o.TangentMode)).ToArray(),
            PreExtrapolation = Extrapolation(source.PreInfinityExtrap),
            PostExtrapolation = Extrapolation(source.PostInfinityExtrap),
            DefaultValue = source.DefaultValue,
            HasDefaultValue = source.bHasDefaultValue,
            TicksPerSecond = tick > 0 ? tick : 60000,
        };
    }

    private static ChannelExtrapolation Extrapolation(ERichCurveExtrapolation value) => value switch
    {
        ERichCurveExtrapolation.RCCE_Cycle => ChannelExtrapolation.Cycle,
        ERichCurveExtrapolation.RCCE_CycleWithOffset => ChannelExtrapolation.CycleWithOffset,
        ERichCurveExtrapolation.RCCE_Oscillate => ChannelExtrapolation.Oscillate,
        ERichCurveExtrapolation.RCCE_Linear => ChannelExtrapolation.Linear,
        ERichCurveExtrapolation.RCCE_None => ChannelExtrapolation.None,
        _ => ChannelExtrapolation.Constant,
    };

    private static void ReadMarkers(UObject track, WidgetAnim anim)
    {
        if (track.ExportType != "MovieSceneAudioTrack")
            return;

        foreach (var section in WidgetProps.Objects(track, "AudioSections").Concat(WidgetProps.Objects(track, "Sections")))
        {
            var sound = WidgetProps.Object(section, "Sound");
            var start = WidgetProps.GetRaw(section, "SectionRange") is FMovieSceneFrameRange range ? range.Value.LowerBound.Value.Value : 0;
            anim.Markers.Add(new AnimMarker(start / anim.TicksPerSecond, "sound", sound?.Name ?? "(unresolved sound)"));
        }
    }
}
