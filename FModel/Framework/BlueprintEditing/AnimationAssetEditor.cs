using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.Core.Math;
using FModel.BlueprintGraph.Animation;
using FModel.BlueprintGraph.Widgets;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.UnrealTypes.EngineEnums;
using UAssetFrameNumber = UAssetAPI.UnrealTypes.FFrameNumber;
using UERangeBoundTypes = UAssetAPI.UnrealTypes.ERangeBoundTypes;

namespace FModel.Framework.BlueprintEditing;

public sealed record AnimationAssetEditDiff(IReadOnlyList<string> Changes, int SignaturesRefreshed)
{
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// Writes P3 edits from Flora's animation model into an existing cooked WidgetAnimation export.
/// It changes only existing float keys and frame ranges; it never creates exports or writes a pak.
/// </summary>
public static class AnimationAssetEditor
{
    private sealed record RawBinding(string Name, List<NormalExport> Tracks);
    private sealed record RawAnimation(NormalExport Animation, NormalExport Scene, List<RawBinding> Bindings);
    private sealed record RawChannel(PropertyData Property, FMovieSceneFloatChannel Channel);

    public static IReadOnlyList<string> DescribeChanges(WidgetAnim before, WidgetAnim after)
    {
        var changes = new List<string>();
        CompareShape(before, after);

        for (var bindingIndex = 0; bindingIndex < before.Bindings.Count; bindingIndex++)
        {
            var oldBinding = before.Bindings[bindingIndex];
            var newBinding = after.Bindings[bindingIndex];
            for (var trackIndex = 0; trackIndex < oldBinding.Tracks.Count; trackIndex++)
            {
                var oldTrack = oldBinding.Tracks[trackIndex];
                var newTrack = newBinding.Tracks[trackIndex];
                for (var sectionIndex = 0; sectionIndex < oldTrack.Sections.Count; sectionIndex++)
                {
                    var oldSection = oldTrack.Sections[sectionIndex];
                    var newSection = newTrack.Sections[sectionIndex];
                    if (!SameRange(oldSection.StartTicks, oldSection.EndTicks, oldSection.StartExclusive, oldSection.EndExclusive,
                            newSection.StartTicks, newSection.EndTicks, newSection.StartExclusive, newSection.EndExclusive))
                        changes.Add($"{oldBinding.Widget}/{oldTrack.ClassName}/{oldTrack.Property}/section[{sectionIndex}].range: {RangeText(oldSection.StartTicks, oldSection.EndTicks, oldSection.StartExclusive, oldSection.EndExclusive)} -> {RangeText(newSection.StartTicks, newSection.EndTicks, newSection.StartExclusive, newSection.EndExclusive)}");

                    foreach (var oldChannel in oldSection.Channels)
                    {
                        var newChannel = newSection.Channels.FirstOrDefault(o => string.Equals(o.Name, oldChannel.Name, StringComparison.Ordinal));
                        if (newChannel == null)
                            throw new InvalidOperationException($"Edited section is missing channel '{oldChannel.Name}'.");
                        if (!SameChannel(oldChannel, newChannel))
                        {
                            for (var keyIndex = 0; keyIndex < oldChannel.Keys.Length; keyIndex++)
                            {
                                if (oldChannel.Times[keyIndex] == newChannel.Times[keyIndex] && oldChannel.Keys[keyIndex] == newChannel.Keys[keyIndex])
                                    continue;
                                var affectedSegments = new List<string>();
                                if (keyIndex > 0)
                                    affectedSegments.Add($"#{keyIndex - 1}");
                                if (keyIndex + 1 < oldChannel.Keys.Length)
                                    affectedSegments.Add($"#{keyIndex}");
                                changes.Add($"{oldBinding.Widget}/{oldTrack.ClassName}/{oldTrack.Property}/{oldChannel.Name}[section {sectionIndex}] key#{keyIndex}: {KeyText(oldChannel, keyIndex)} -> {KeyText(newChannel, keyIndex)}; adjacent segment(s) affected: {(affectedSegments.Count == 0 ? "none" : string.Join(", ", affectedSegments))}");
                            }
                        }
                    }
                }
            }
        }

        if (!SameRange(before.PlaybackStartTicks, before.PlaybackEndTicks, before.PlaybackStartExclusive, before.PlaybackEndExclusive,
                after.PlaybackStartTicks, after.PlaybackEndTicks, after.PlaybackStartExclusive, after.PlaybackEndExclusive))
            changes.Add($"PlaybackRange: {RangeText(before.PlaybackStartTicks, before.PlaybackEndTicks, before.PlaybackStartExclusive, before.PlaybackEndExclusive)} -> {RangeText(after.PlaybackStartTicks, after.PlaybackEndTicks, after.PlaybackStartExclusive, after.PlaybackEndExclusive)}");

        return changes;
    }

    public static AnimationAssetEditDiff Apply(UAsset asset, WidgetAnim before, WidgetAnim after)
    {
        var changes = DescribeChanges(before, after);
        if (changes.Count == 0)
            return new AnimationAssetEditDiff(changes, 0);

        var raw = Find(asset, before);
        var affected = new HashSet<NormalExport>();
        var refreshSignatures = false;

        for (var bindingIndex = 0; bindingIndex < before.Bindings.Count; bindingIndex++)
        {
            var oldBinding = before.Bindings[bindingIndex];
            var newBinding = after.Bindings[bindingIndex];
            var rawBinding = raw.Bindings.FirstOrDefault(o => string.Equals(o.Name, oldBinding.Widget, StringComparison.Ordinal))
                             ?? throw new InvalidOperationException($"UAssetAPI could not resolve animation binding '{oldBinding.Widget}'.");
            if (rawBinding.Tracks.Count != oldBinding.Tracks.Count)
                throw new InvalidOperationException($"Track count changed for binding '{oldBinding.Widget}'; P3 does not add or remove tracks.");

            for (var trackIndex = 0; trackIndex < oldBinding.Tracks.Count; trackIndex++)
            {
                var oldTrack = oldBinding.Tracks[trackIndex];
                var newTrack = newBinding.Tracks[trackIndex];
                var rawTrack = rawBinding.Tracks[trackIndex];
                if (ClassOf(rawTrack) != oldTrack.ClassName || TrackProperty(rawTrack) != oldTrack.Property)
                    throw new InvalidOperationException($"Track mapping changed at {oldBinding.Widget}[{trackIndex}]; refusing a positional guess.");

                var rawSections = ReferencedExports(asset, Prop<ArrayPropertyData>(rawTrack, "Sections"));
                if (rawSections.Count != oldTrack.Sections.Count)
                    throw new InvalidOperationException($"Section count changed at {oldBinding.Widget}/{oldTrack.ClassName}; P3 does not add or remove sections.");

                for (var sectionIndex = 0; sectionIndex < oldTrack.Sections.Count; sectionIndex++)
                {
                    var oldSection = oldTrack.Sections[sectionIndex];
                    var newSection = newTrack.Sections[sectionIndex];
                    var rawSection = rawSections[sectionIndex];
                    var rangeChanged = !SameRange(oldSection.StartTicks, oldSection.EndTicks, oldSection.StartExclusive, oldSection.EndExclusive,
                        newSection.StartTicks, newSection.EndTicks, newSection.StartExclusive, newSection.EndExclusive);
                    if (rangeChanged)
                    {
                        SetRange(Prop<MovieSceneFrameRangePropertyData>(rawSection, "SectionRange"),
                            newSection.StartTicks, newSection.EndTicks, newSection.StartExclusive, newSection.EndExclusive,
                            $"{oldBinding.Widget}/{oldTrack.ClassName}.SectionRange[{sectionIndex}]");
                        AddOuterChain(asset, rawSection, raw.Animation, affected);
                        refreshSignatures = true;
                    }

                    foreach (var oldChannel in oldSection.Channels)
                    {
                        var newChannel = newSection.Channels.FirstOrDefault(o => string.Equals(o.Name, oldChannel.Name, StringComparison.Ordinal))
                                         ?? throw new InvalidOperationException($"Edited section is missing channel '{oldChannel.Name}'.");
                        if (SameChannel(oldChannel, newChannel))
                            continue;

                        var rawChannel = FindChannel(rawSection, oldTrack.Kind, newChannel.Name);
                        WriteChannel(rawChannel.Channel, newChannel, $"{oldBinding.Widget}/{oldTrack.Property}/{newChannel.Name}");
                        if (!oldChannel.Times.SequenceEqual(newChannel.Times))
                        {
                            AddOuterChain(asset, rawSection, raw.Animation, affected);
                            refreshSignatures = true;
                        }
                    }
                }
            }
        }

        if (!SameRange(before.PlaybackStartTicks, before.PlaybackEndTicks, before.PlaybackStartExclusive, before.PlaybackEndExclusive,
                after.PlaybackStartTicks, after.PlaybackEndTicks, after.PlaybackStartExclusive, after.PlaybackEndExclusive))
        {
            SetRange(Prop<MovieSceneFrameRangePropertyData>(raw.Scene, "PlaybackRange"),
                after.PlaybackStartTicks, after.PlaybackEndTicks, after.PlaybackStartExclusive, after.PlaybackEndExclusive,
                $"{raw.Scene.ObjectName}.PlaybackRange");
            AddOuterChain(asset, raw.Scene, raw.Animation, affected);
            refreshSignatures = true;
        }

        var refreshed = 0;
        if (refreshSignatures)
            refreshed = RefreshSignatures(affected);
        return new AnimationAssetEditDiff(changes, refreshed);
    }

    /// <summary>Checks the written animation after BlueprintEditSession has serialized and reopened it with CUE4Parse.</summary>
    public static string? VerifyReadback(IPackage package, WidgetAnim expected)
    {
        try
        {
            var widgetClass = WidgetTreeReader.FindWidgetClass(package);
            if (widgetClass == null)
                return "CUE4Parse readback has no widget blueprint class.";
            var actual = AnimationReader.Read(widgetClass).FirstOrDefault(o => string.Equals(o.Variable, expected.Variable, StringComparison.Ordinal));
            if (actual == null)
                return $"CUE4Parse readback has no animation '{expected.Name}'.";
            if (actual.Error != null)
                return $"CUE4Parse readback of '{expected.Name}' was incomplete: {actual.Error}";

            var differences = DescribeChanges(expected, actual);
            return differences.Count == 0 ? null : "CUE4Parse animation readback differs from the edit: " + string.Join("; ", differences.Take(8));
        }
        catch (Exception e)
        {
            return $"CUE4Parse animation readback failed: {e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>Copies only animation data into the cached P2 model so anim_eval and anim_preview see pending edits.</summary>
    public static void CopyState(WidgetAnim from, WidgetAnim to)
    {
        to.PlaybackStartTicks = from.PlaybackStartTicks;
        to.PlaybackEndTicks = from.PlaybackEndTicks;
        to.PlaybackStartExclusive = from.PlaybackStartExclusive;
        to.PlaybackEndExclusive = from.PlaybackEndExclusive;
        if (from.Bindings.Count != to.Bindings.Count)
            throw new InvalidOperationException("Cannot refresh the preview model after the animation binding count changed.");
        for (var bi = 0; bi < from.Bindings.Count; bi++)
        {
            var sourceBinding = from.Bindings[bi];
            var targetBinding = to.Bindings[bi];
            for (var ti = 0; ti < sourceBinding.Tracks.Count; ti++)
            for (var si = 0; si < sourceBinding.Tracks[ti].Sections.Count; si++)
            {
                var sourceSection = sourceBinding.Tracks[ti].Sections[si];
                var targetSection = targetBinding.Tracks[ti].Sections[si];
                targetSection.StartTicks = sourceSection.StartTicks;
                targetSection.EndTicks = sourceSection.EndTicks;
                targetSection.StartExclusive = sourceSection.StartExclusive;
                targetSection.EndExclusive = sourceSection.EndExclusive;
                foreach (var sourceChannel in sourceSection.Channels)
                {
                    var targetChannel = targetSection.Channels.First(o => o.Name == sourceChannel.Name);
                    targetChannel.Times = (int[]) sourceChannel.Times.Clone();
                    targetChannel.Keys = (AnimKey[]) sourceChannel.Keys.Clone();
                }
            }
        }
    }

    private static void CompareShape(WidgetAnim before, WidgetAnim after)
    {
        if (before.Variable != after.Variable || before.Bindings.Count != after.Bindings.Count)
            throw new InvalidOperationException("P3 cannot change animation identity or bindings.");
        for (var bi = 0; bi < before.Bindings.Count; bi++)
        {
            var oldBinding = before.Bindings[bi];
            var newBinding = after.Bindings[bi];
            if (oldBinding.Widget != newBinding.Widget || oldBinding.Tracks.Count != newBinding.Tracks.Count)
                throw new InvalidOperationException("P3 cannot change animation bindings or tracks.");
            for (var ti = 0; ti < oldBinding.Tracks.Count; ti++)
            {
                var oldTrack = oldBinding.Tracks[ti];
                var newTrack = newBinding.Tracks[ti];
                if (oldTrack.ClassName != newTrack.ClassName || oldTrack.Property != newTrack.Property || oldTrack.Sections.Count != newTrack.Sections.Count)
                    throw new InvalidOperationException("P3 cannot change track identity or section count.");
                for (var si = 0; si < oldTrack.Sections.Count; si++)
                {
                    var oldSection = oldTrack.Sections[si];
                    var newSection = newTrack.Sections[si];
                    if (oldSection.Channels.Count != newSection.Channels.Count)
                        throw new InvalidOperationException("P3 cannot add or remove animation channels.");
                    foreach (var oldChannel in oldSection.Channels)
                    {
                        var newChannel = newSection.Channels.FirstOrDefault(o => o.Name == oldChannel.Name);
                        if (newChannel == null)
                            throw new InvalidOperationException($"P3 cannot remove animation channel '{oldChannel.Name}'.");
                        if (oldChannel.Times.Length != newChannel.Times.Length || oldChannel.Keys.Length != newChannel.Keys.Length)
                            throw new InvalidOperationException($"P3 cannot add or remove keys from animation channel '{oldChannel.Name}'.");
                    }
                }
            }
        }
    }

    private static RawAnimation Find(UAsset asset, WidgetAnim animation)
    {
        var exportName = animation.Variable + "_INST";
        var rawAnimation = asset.Exports.OfType<NormalExport>().FirstOrDefault(e => ClassOf(e) == "WidgetAnimation" && e.ObjectName.ToString() == exportName)
                           ?? throw new InvalidOperationException($"UAssetAPI could not find WidgetAnimation '{exportName}'.");
        var sceneRef = Prop<ObjectPropertyData>(rawAnimation, "MovieScene")
                       ?? throw new InvalidOperationException($"WidgetAnimation '{exportName}' has no MovieScene reference.");
        var scene = ExportAt(asset, sceneRef.Value.Index) ?? throw new InvalidOperationException($"WidgetAnimation '{exportName}' has an invalid MovieScene reference.");
        if (ClassOf(scene) != "MovieScene")
            throw new InvalidOperationException($"WidgetAnimation '{exportName}' points to {ClassOf(scene)}, not MovieScene.");

        var bindingArray = Prop<ArrayPropertyData>(scene, "ObjectBindings")
                           ?? throw new InvalidOperationException("MovieScene has no ObjectBindings array.");
        var bindings = new List<RawBinding>();
        foreach (var binding in bindingArray.Value.OfType<StructPropertyData>())
        {
            var name = Text(binding.Value.FirstOrDefault(p => p.Name.ToString() == "BindingName"));
            var tracksProperty = binding.Value.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Tracks");
            var tracks = ReferencedExports(asset, tracksProperty);
            bindings.Add(new RawBinding(name ?? "", tracks));
        }
        return new RawAnimation(rawAnimation, scene, bindings);
    }

    private static List<NormalExport> ReferencedExports(UAsset asset, ArrayPropertyData property)
    {
        if (property?.Value == null)
            return [];
        var result = new List<NormalExport>();
        foreach (var reference in property.Value.OfType<ObjectPropertyData>())
        {
            var export = ExportAt(asset, reference.Value.Index);
            if (export != null)
                result.Add(export);
        }
        return result;
    }

    private static NormalExport ExportAt(UAsset asset, int index) =>
        index > 0 && index <= asset.Exports.Count ? asset.Exports[index - 1] as NormalExport : null;

    private static RawChannel FindChannel(NormalExport section, AnimTrackKind kind, string channelName)
    {
        var matches = section.Data.SelectMany(PropertyAndStructMembers)
            .Select(p => (Property: p, Channel: InnerChannel(p)))
            .Where(o => o.Channel != null && ChannelName(kind, o.Property.Name.ToString(), o.Property.ArrayIndex) == channelName)
            .ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException($"Expected one UAsset float channel named '{channelName}' in {section.ObjectName}; found {matches.Count}.");
        return new RawChannel(matches[0].Property, matches[0].Channel);
    }

    private static void WriteChannel(FMovieSceneFloatChannel channel, AnimChannel model, string label)
    {
        if (channel.Times.Length != channel.Values.Length || channel.Times.Length != model.Times.Length || model.Keys.Length != model.Times.Length)
            throw new InvalidOperationException($"{label} changed its key count; P3 does not add or remove keys.");
        channel.Times = model.Times.Select(o => new UAssetFrameNumber(o)).ToArray();
        for (var i = 0; i < model.Keys.Length; i++)
        {
            var source = model.Keys[i];
            var target = channel.Values[i];
            if (target.Tangent == null)
                throw new InvalidOperationException($"{label} key {i} has no tangent structure.");
            target.Value = source.Value;
            target.InterpMode = (ERichCurveInterpMode) (byte) source.Interp;
            target.TangentMode = (ERichCurveTangentMode) (byte) source.TangentMode;
            target.Tangent.ArriveTangent = source.ArriveTangent;
            target.Tangent.LeaveTangent = source.LeaveTangent;
            target.Tangent.ArriveTangentWeight = source.ArriveWeight;
            target.Tangent.LeaveTangentWeight = source.LeaveWeight;
            target.Tangent.TangentWeightMode = (ERichCurveTangentWeightMode) (byte) source.WeightMode;
        }
    }

    private static void SetRange(MovieSceneFrameRangePropertyData property, int start, int end, bool startExclusive, bool endExclusive, string label)
    {
        if (property == null)
            throw new InvalidOperationException($"{label} has no MovieSceneFrameRange property.");
        if (end < start || (end == start && (startExclusive || endExclusive)))
            throw new InvalidOperationException($"{label} range must be non-empty and ordered.");
        var range = property.Value;
        range.LowerBound.Type = startExclusive ? UERangeBoundTypes.Exclusive : UERangeBoundTypes.Inclusive;
        range.LowerBound.Value = new UAssetFrameNumber(start);
        range.UpperBound.Type = endExclusive ? UERangeBoundTypes.Exclusive : UERangeBoundTypes.Inclusive;
        range.UpperBound.Value = new UAssetFrameNumber(end);
        property.Value = range;
    }

    private static int RefreshSignatures(HashSet<NormalExport> affected)
    {
        var count = 0;
        foreach (var export in affected)
        {
            var signature = Prop<GuidPropertyData>(export, "Signature")
                            ?? throw new InvalidOperationException($"{export.ObjectName} has no editable Signature GUID.");
            signature.Value = Guid.NewGuid();
            count++;
        }
        return count;
    }

    private static void AddOuterChain(UAsset asset, NormalExport start, NormalExport animation, HashSet<NormalExport> affected)
    {
        Export current = start;
        for (var guard = 0; current != null && guard < 32; guard++)
        {
            if (current is NormalExport normal)
                affected.Add(normal);
            if (ReferenceEquals(current, animation))
                return;
            var outer = current.OuterIndex.Index;
            if (outer <= 0 || outer > asset.Exports.Count)
                break;
            current = asset.Exports[outer - 1];
        }
        throw new InvalidOperationException($"Could not trace the outer signature chain from {start.ObjectName} to {animation.ObjectName}.");
    }

    private static T Prop<T>(NormalExport export, string name) where T : PropertyData =>
        export.Data.SelectMany(PropertyAndStructMembers).OfType<T>().FirstOrDefault(p => p.Name.ToString() == name && p.ArrayIndex == 0);

    private static IEnumerable<PropertyData> PropertyAndStructMembers(PropertyData property)
    {
        yield return property;
        if (property is StructPropertyData structure)
            foreach (var inner in structure.Value)
                yield return inner;
    }

    private static FMovieSceneFloatChannel InnerChannel(PropertyData property)
    {
        if (property is StructPropertyData structure)
            foreach (var inner in structure.Value)
                if (inner is MovieSceneFloatChannelPropertyData channel)
                    return channel.Value;
        return (property as MovieSceneFloatChannelPropertyData)?.Value;
    }

    private static string TrackProperty(NormalExport track)
    {
        var propertyBinding = Prop<StructPropertyData>(track, "PropertyBinding");
        return Text(propertyBinding?.Value.FirstOrDefault(p => p.Name.ToString() == "PropertyName")) ?? "";
    }

    private static string ClassOf(Export export) => export.GetExportClassType()?.ToString() ?? "";

    private static string Text(PropertyData property) => property?.RawValue?.ToString();

    private static string ChannelName(AnimTrackKind kind, string property, int index)
    {
        var axis = index switch { 0 => "X", 1 => "Y", _ => $"[{index}]" };
        return kind switch
        {
            AnimTrackKind.Transform2D => property switch
            {
                "Translation" or "Scale" or "Shear" => $"{property}.{axis}",
                "Rotation" => "Angle",
                _ => index == 0 ? property : $"{property}[{index}]",
            },
            AnimTrackKind.Color => property switch
            {
                "RedCurve" => "R",
                "GreenCurve" => "G",
                "BlueCurve" => "B",
                "AlphaCurve" => "A",
                _ => index == 0 ? property : $"{property}[{index}]",
            },
            AnimTrackKind.Float => property == "FloatCurve" ? "Value" : index == 0 ? property : $"{property}[{index}]",
            _ => index == 0 ? property : $"{property}[{index}]",
        };
    }

    private static bool SameChannel(AnimChannel left, AnimChannel right) => left.Times.SequenceEqual(right.Times) && left.Keys.SequenceEqual(right.Keys);

    private static bool SameRange(int leftStart, int leftEnd, bool leftStartExclusive, bool leftEndExclusive,
        int rightStart, int rightEnd, bool rightStartExclusive, bool rightEndExclusive) =>
        leftStart == rightStart && leftEnd == rightEnd && leftStartExclusive == rightStartExclusive && leftEndExclusive == rightEndExclusive;

    private static string RangeText(int start, int end, bool startExclusive, bool endExclusive) =>
        $"{(startExclusive ? "(" : "[")}{start}, {end}{(endExclusive ? ")" : "]")}";

    private static string KeyText(AnimChannel channel, int index)
    {
        var key = channel.Keys[index];
        var perSecond = channel.TicksPerSecond;
        return $"t={channel.Times[index]} value={key.Value.ToString("0.####", CultureInfo.InvariantCulture)} " +
               $"{key.Interp}/{key.TangentMode} arrive={((double) key.ArriveTangent * perSecond).ToString("0.####", CultureInfo.InvariantCulture)} " +
               $"leave={((double) key.LeaveTangent * perSecond).ToString("0.####", CultureInfo.InvariantCulture)} perSecond " +
               $"weight={key.WeightMode}({key.ArriveWeight.ToString("0.####", CultureInfo.InvariantCulture)},{key.LeaveWeight.ToString("0.####", CultureInfo.InvariantCulture)})";
    }
}
