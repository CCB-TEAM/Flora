using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FModel.BlueprintGraph.Animation;
using FModel.Framework.BlueprintEditing;
using FModel.Settings;
using FModel.ViewModels;
using Newtonsoft.Json.Linq;
using static FModel.Framework.BlueprintAgent.Schema;

namespace FModel.Framework.BlueprintAgent;

/// <summary>Pending, reviewable edits to existing cooked UMG animation channels and ranges.</summary>
public static class AnimationEditAgentTools
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly JObject BlueprintArg = String("Open widget blueprint: package path or name (see list_blueprints). Optional when only one is open.");
    private static readonly JObject AnimationArg = String("Animation name as anim_list shows it.");

    public static IEnumerable<AgentTool> Build() =>
    [
        new()
        {
            Name = "anim_export",
            Description = "Exports the selected animation as schemaVersion 1 JSON, including existing widget bindings, tracks, sections, channel metadata, keys, tangents, weights, ranges, tick resolution, and display rate. The JSON is a review/edit interchange format; it cannot add or remove asset structure. If path is supplied, writes the same JSON to that file and also returns it.",
            Access = AgentToolAccess.Read,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("animation", AnimationArg, true),
                ("path", String("Optional output JSON path. Omit to return the JSON only."), false)),
            Run = (args, _) => Task.FromResult(Export(args)),
        },
        new()
        {
            Name = "anim_import",
            Description = "Reads a schemaVersion 1 animation JSON file and stages only P3 edits to existing keys and ranges in the open blueprint session. Identity, timebase, bindings, tracks, sections, channel metadata, defaults, extrapolation, and key counts must match. Use dry_run first; no package or pak is written.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("blueprint", BlueprintArg, false),
                ("path", String("Animation JSON path created by anim_export."), true),
                ("animation", AnimationArg, false),
                ("dry_run", Boolean("Show the proposed diff without changing the pending blueprint session."), false)),
            Run = ImportAsync,
        },
        new()
        {
            Name = "anim_edit",
            Description = "Applies an atomic batch of edits to existing UMG animation float channels in the open widget blueprint. Supports set_key, move_key, set_interp, set_tangent, retime, and set_range. " +
                          "Times and ranges are timeline seconds at the animation's tick resolution, snapped to its DisplayRate grid according to the saved preference (on by default). Key indices are zero-based as shown by anim_get. " +
                          "Automatic tangent handling uses the saved animation setting (local by default); choose local, channel, or none globally or per operation. Time/range changes refresh the affected Section → Track → MovieScene → WidgetAnimation signatures. " +
                          "Weighted channels reject non-identity retime until that conversion is verified. " +
                          "The edit remains pending in the blueprint session; this tool does not export files or build a pak. Use dry_run first to inspect the diff.",
            Access = AgentToolAccess.Edit,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("animation", AnimationArg, true),
                ("operations", Array("Ordered atomic operations. Each object has an op field; channel edits also identify widget, track_class, property, section, channel, and zero-based key index.", Any("Animation edit operation object.")), true),
                ("tangents", Enum("Automatic tangent handling for set_key, move_key, and set_interp: saved animation setting (local by default), channel, or none."), false),
                ("dry_run", Boolean("Show the proposed diff without changing the pending blueprint session."), false),
                ("snap_to_display_rate", Boolean("Snap user-entered seconds to the animation's frame grid (uses the saved setting, on by default)."), false)),
            Run = RunAsync,
        },
    ];

    private static string Export(JObject args)
    {
        var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
        var animation = FindAnimation(viewer, args.Str("animation"));
        if (animation.Error != null)
            throw new AgentToolException($"Animation '{animation.Name}' could not be exported completely: {animation.Error}");
        var json = AnimationJsonExchange.Export(animation, viewer.Entry.Path).ToString();
        var path = args.Str("path", false);
        if (path == null)
            return json;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, json, new System.Text.UTF8Encoding(false));
        return $"Wrote {animation.AllChannels.Sum(channel => channel.Keys.Length)} keys to {fullPath}.\n{json}";
    }

    private static async Task<string> ImportAsync(JObject args, CancellationToken cancellation)
    {
        var path = Path.GetFullPath(args.Str("path"));
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new AgentToolException($"Animation JSON file not found: {path}");
        if (info.Length > 5 * 1024 * 1024)
            throw new AgentToolException("Animation JSON is larger than the 5 MiB import limit.");

        JObject document;
        try
        {
            document = JObject.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            throw new AgentToolException($"Could not read animation JSON: {e.Message}");
        }

        var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
        var name = args.Str("animation", false) ?? document["animation"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(name))
            throw new AgentToolException("The import requires an animation name in the file or arguments.");
        var animation = FindAnimation(viewer, name);
        if (animation.Error != null)
            throw new AgentToolException($"Animation '{animation.Name}' could not be read completely: {animation.Error}");

        IReadOnlyList<JObject> operations;
        try
        {
            operations = AnimationJsonExchange.BuildImportOperations(animation, document, viewer.Entry.Path);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new AgentToolException("Animation import was rejected without changing the pending session: " + e.Message);
        }
        if (operations.Count == 0)
            return $"{animation.Name}: the JSON matches the current animation; no edit was staged.";

        var editArgs = new JObject
        {
            ["blueprint"] = viewer.Entry.Path,
            ["animation"] = animation.Name,
            ["operations"] = new JArray(operations),
            ["snap_to_display_rate"] = false,
            ["dry_run"] = Bool(args, "dry_run", false),
        };
        return await RunAsync(editArgs, cancellation);
    }

    private static async Task<string> RunAsync(JObject args, CancellationToken cancellation)
    {
        var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
        if (viewer.Designer.Info?.WidgetClass == null)
            throw new AgentToolException($"{viewer.Entry.Path} is not a widget blueprint, or it is still loading.");
        var animation = viewer.Designer.ReadAnimations().FirstOrDefault(o => string.Equals(o.Name, args.Str("animation"), StringComparison.OrdinalIgnoreCase))
                        ?? throw new AgentToolException($"No animation '{args.Str("animation")}'; use anim_list to see the available names.");
        if (animation.Error != null)
            throw new AgentToolException($"Animation '{animation.Name}' could not be read completely: {animation.Error}");
        if (args["operations"] is not JArray operations || operations.Count == 0)
            throw new AgentToolException("'operations' must be a non-empty array.");
        if (operations.Count > 64)
            throw new AgentToolException("At most 64 operations can be applied atomically.");

        var proposed = Clone(animation);
        var snap = args["snap_to_display_rate"] == null
            ? UserSettings.Default.AnimationSnapToDisplayRate
            : Bool(args, "snap_to_display_rate", UserSettings.Default.AnimationSnapToDisplayRate);
        var tangentUpdate = ReadTangentUpdate(args["tangents"], UserSettings.Default.AnimationTangentUpdateMode);
        var summaries = new List<string>();
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i] is not JObject operation)
                throw new AgentToolException($"operations[{i}] must be an object.");
            try
            {
                summaries.Add(ApplyOperation(proposed, operation, snap, tangentUpdate));
            }
            catch (AgentToolException)
            {
                throw;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
            {
                throw new AgentToolException($"operations[{i}] failed atomically: {e.Message}");
            }
        }

        IReadOnlyList<string> changes;
        try
        {
            ValidateAnimation(proposed);
            changes = AnimationAssetEditor.DescribeChanges(animation, proposed);
        }
        catch (AgentToolException)
        {
            throw;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new AgentToolException("The edit batch is inconsistent: " + e.Message);
        }
        if (changes.Count == 0)
            return $"{animation.Name}: no data changes; the pending blueprint session was left untouched.";

        if (Bool(args, "dry_run", false))
            return FormatResult(animation, proposed, changes, summaries, null, true);

        cancellation.ThrowIfCancellationRequested();
        if (!await viewer.Editor.EnsureLoadedAsync() || viewer.Editor.Session == null)
            throw new AgentToolException("The blueprint edit session could not be loaded. No animation data was changed.");

        var session = viewer.Editor.Session;
        AnimationAssetEditDiff committed = null;
        var validation = session.ApplyAnimationEdit(
            asset => committed = AnimationAssetEditor.Apply(asset, animation, proposed),
            package =>
            {
                var blueprintError = viewer.Editor.Verifier?.Invoke(package, session);
                return blueprintError ?? AnimationAssetEditor.VerifyReadback(package, proposed);
            });
        if (!validation.Success)
            throw new AgentToolException("Animation edit was rolled back: " + validation.Message);
        if (committed == null || !committed.HasChanges)
            throw new AgentToolException("The edit produced no serialized changes; the pending preview was not updated.");

        AnimationAssetEditor.CopyState(proposed, animation);
        viewer.Designer.NotifyAnimationsChanged();
        return FormatResult(animation, proposed, committed.Changes, summaries, committed.SignaturesRefreshed, false);
    }

    /// <summary>Runs the same validated, pending-session edit path used by anim_edit from the visual editor.</summary>
    public static Task<string> RunFromUiAsync(JObject args, CancellationToken cancellation = default) => RunAsync(args, cancellation);

    private static string ApplyOperation(WidgetAnim animation, JObject operation, bool snap, TangentUpdateMode defaultTangentUpdate)
    {
        var op = ReadString(operation, "op");
        if (string.Equals(op, "retime", StringComparison.OrdinalIgnoreCase))
        {
            if (operation["factor"] != null && operation["new_length_seconds"] != null)
                throw new AgentToolException("retime accepts either 'factor' or 'new_length_seconds', not both.");
            var factor = RetimeFactor(operation, animation, snap);
            var offsetSeconds = ReadNumber(operation, "offset_seconds", false, 0);
            if (operation["new_length_seconds"] != null)
                offsetSeconds += animation.StartSeconds * (1 - factor);
            var timelineOffset = SecondsToTicks(offsetSeconds, animation.TicksPerSecond, animation.DisplayRate, snap);
            foreach (var binding in animation.Bindings)
            foreach (var track in binding.Tracks)
            foreach (var section in track.Sections)
            {
                foreach (var channel in section.Channels)
                {
                    var channelOffset = SecondsToTicks(offsetSeconds, channel.TicksPerSecond, animation.DisplayRate, snap);
                    AnimationChannelEditor.Retime(channel, factor, channelOffset);
                }
                section.StartTicks = MapTick(section.StartTicks, factor, timelineOffset);
                section.EndTicks = MapTick(section.EndTicks, factor, timelineOffset);
            }
            animation.PlaybackStartTicks = MapTick(animation.PlaybackStartTicks, factor, timelineOffset);
            animation.PlaybackEndTicks = MapTick(animation.PlaybackEndTicks, factor, timelineOffset);
            ValidateRange(animation.PlaybackStartTicks, animation.PlaybackEndTicks, animation.PlaybackStartExclusive, animation.PlaybackEndExclusive, "PlaybackRange");
            foreach (var binding in animation.Bindings)
            foreach (var track in binding.Tracks)
            foreach (var section in track.Sections)
                ValidateRange(section.StartTicks, section.EndTicks, section.StartExclusive, section.EndExclusive, $"{binding.Widget}/{track.Property}/section {section.Index}");
            return $"retime factor={factor.ToString("0.######", Invariant)}, offset={offsetSeconds.ToString("0.####", Invariant)} s";
        }

        if (string.Equals(op, "set_range", StringComparison.OrdinalIgnoreCase))
            return SetRange(animation, operation, snap);

        var target = FindChannel(animation, operation);
        var keyIndex = ReadInt(operation, "key", true, 0);
        if ((uint) keyIndex >= (uint) target.Channel.Keys.Length)
            throw new AgentToolException($"Key index {keyIndex} is out of range for {target.Widget}/{target.Channel.Name} (0..{target.Channel.Keys.Length - 1}).");

        var oldKey = target.Channel.Keys[keyIndex];
        var tangentUpdate = ReadTangentUpdate(operation["tangents"], defaultTangentUpdate);
        switch (op.ToLowerInvariant())
        {
            case "set_key":
            {
                var value = ToSingle(ReadNumber(operation, "value", true, 0), "value");
                AnimationChannelEditor.SetKey(target.Channel, keyIndex, value, tangentUpdate);
                return $"{target.Widget}/{target.Track.ClassName}/{target.Track.Property}/{target.Channel.Name} key#{keyIndex} value {oldKey.Value.ToString("0.####", Invariant)} -> {value.ToString("0.####", Invariant)}";
            }
            case "move_key":
            {
                var seconds = ReadNumber(operation, "time_seconds", true, 0);
                var ticks = SecondsToTicks(seconds, target.Channel.TicksPerSecond, animation.DisplayRate, snap);
                var oldTime = target.Channel.Times[keyIndex];
                AnimationChannelEditor.MoveKey(target.Channel, keyIndex, ticks, tangentUpdate);
                ExpandRangesForKeyMove(animation, target.Track, target.Section);
                return $"{target.Widget}/{target.Track.ClassName}/{target.Track.Property}/{target.Channel.Name} key#{keyIndex} time {oldTime} -> {ticks} ticks ({seconds.ToString("0.####", Invariant)} s before grid snap)";
            }
            case "set_interp":
            {
                var interpolation = ReadEnum(operation, "interp", oldKey.Interp);
                var tangentMode = ReadEnum(operation, "tangent_mode", oldKey.TangentMode);
                AnimationChannelEditor.SetInterp(target.Channel, keyIndex, interpolation, tangentMode, tangentUpdate);
                return $"{target.Widget}/{target.Track.ClassName}/{target.Track.Property}/{target.Channel.Name} key#{keyIndex} interp/mode {oldKey.Interp}/{oldKey.TangentMode} -> {interpolation}/{tangentMode}";
            }
            case "set_tangent":
            {
                if (oldKey.Interp != KeyInterp.Cubic)
                    throw new AgentToolException("set_tangent requires a cubic key; run set_interp with interp='cubic' first.");
                var arrive = ReadNumber(operation, "arrive_per_second", true, 0);
                var leave = ReadNumber(operation, "leave_per_second", true, 0);
                var tangentMode = ReadEnum(operation, "tangent_mode", KeyTangentMode.Break);
                var weightMode = ReadEnum(operation, "weight_mode", KeyWeightMode.None);
                var arriveWeight = ReadNumber(operation, "arrive_weight", false, oldKey.ArriveWeight);
                var leaveWeight = ReadNumber(operation, "leave_weight", false, oldKey.LeaveWeight);
                AnimationChannelEditor.SetTangent(target.Channel, keyIndex, arrive, leave, tangentMode, weightMode, arriveWeight, leaveWeight);
                return $"{target.Widget}/{target.Track.ClassName}/{target.Track.Property}/{target.Channel.Name} key#{keyIndex} tangents arrive={arrive.ToString("0.####", Invariant)} leave={leave.ToString("0.####", Invariant)} per second";
            }
            default:
                throw new AgentToolException($"Unknown animation edit operation '{op}'. Use set_key, move_key, set_interp, set_tangent, retime, or set_range.");
        }
    }

    private static string SetRange(WidgetAnim animation, JObject operation, bool snap)
    {
        var scope = ReadString(operation, "scope");
        var start = SecondsToTicks(ReadNumber(operation, "start_seconds", true, 0), animation.TicksPerSecond, animation.DisplayRate, snap);
        var end = SecondsToTicks(ReadNumber(operation, "end_seconds", true, 0), animation.TicksPerSecond, animation.DisplayRate, snap);
        if (string.Equals(scope, "playback", StringComparison.OrdinalIgnoreCase))
        {
            var startExclusive = Bool(operation, "start_exclusive", animation.PlaybackStartExclusive);
            var endExclusive = Bool(operation, "end_exclusive", animation.PlaybackEndExclusive);
            ValidateRange(start, end, startExclusive, endExclusive, "PlaybackRange");
            var before = RangeText(animation.PlaybackStartTicks, animation.PlaybackEndTicks, animation.PlaybackStartExclusive, animation.PlaybackEndExclusive);
            animation.PlaybackStartTicks = start;
            animation.PlaybackEndTicks = end;
            animation.PlaybackStartExclusive = startExclusive;
            animation.PlaybackEndExclusive = endExclusive;
            return $"PlaybackRange {before} -> {RangeText(start, end, startExclusive, endExclusive)}";
        }
        if (string.Equals(scope, "section", StringComparison.OrdinalIgnoreCase))
        {
            var target = FindTrack(animation, operation);
            var sectionIndex = ReadInt(operation, "section", false, 0);
            if ((uint) sectionIndex >= (uint) target.Track.Sections.Count)
                throw new AgentToolException($"Section {sectionIndex} is out of range for {target.Widget}/{target.Track.ClassName}.");
            var section = target.Track.Sections[sectionIndex];
            var startExclusive = Bool(operation, "start_exclusive", section.StartExclusive);
            var endExclusive = Bool(operation, "end_exclusive", section.EndExclusive);
            ValidateRange(start, end, startExclusive, endExclusive, "SectionRange");
            var before = RangeText(section.StartTicks, section.EndTicks, section.StartExclusive, section.EndExclusive);
            section.StartTicks = start;
            section.EndTicks = end;
            section.StartExclusive = startExclusive;
            section.EndExclusive = endExclusive;
            return $"{target.Widget}/{target.Track.ClassName}/{target.Track.Property}/section[{sectionIndex}] {before} -> {RangeText(start, end, startExclusive, endExclusive)}";
        }
        throw new AgentToolException("set_range scope must be 'playback' or 'section'.");
    }

    private sealed record ChannelTarget(string Widget, AnimTrack Track, AnimSection Section, AnimChannel Channel);
    private sealed record TrackTarget(string Widget, AnimTrack Track);

    private static ChannelTarget FindChannel(WidgetAnim animation, JObject operation)
    {
        var track = FindTrack(animation, operation);
        var sectionIndex = ReadInt(operation, "section", false, 0);
        if ((uint) sectionIndex >= (uint) track.Track.Sections.Count)
            throw new AgentToolException($"Section {sectionIndex} is out of range for {track.Widget}/{track.Track.ClassName}.");
        var section = track.Track.Sections[sectionIndex];
        var name = ReadString(operation, "channel");
        var matches = section.Channels.Where(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
            throw new AgentToolException($"Expected one channel '{name}' in {track.Widget}/{track.Track.ClassName}/section {sectionIndex}; found {matches.Count}.");
        return new ChannelTarget(track.Widget, track.Track, section, matches[0]);
    }

    private static TrackTarget FindTrack(WidgetAnim animation, JObject operation)
    {
        var widget = ReadString(operation, "widget");
        var trackClass = ReadString(operation, "track_class");
        var property = ReadString(operation, "property");
        var bindings = animation.Bindings.Where(o => string.Equals(o.Widget, widget, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = bindings.SelectMany(b => b.Tracks.Select(t => (Binding: b, Track: t)))
            .Where(o => string.Equals(o.Track.ClassName, trackClass, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(o.Track.Property, property, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
            throw new AgentToolException($"Expected one track {widget}/{trackClass}/{property}; found {matches.Count}. Check anim_get output.");
        return new TrackTarget(matches[0].Binding.Widget, matches[0].Track);
    }

    private static void ExpandRangesForKeyMove(WidgetAnim animation, AnimTrack track, AnimSection section)
    {
        var keys = section.Channels.SelectMany(o => o.Times).ToArray();
        if (keys.Length > 0)
        {
            var minimum = keys.Min();
            var maximum = keys.Max();
            if (minimum < section.StartTicks || (minimum == section.StartTicks && section.StartExclusive))
            {
                section.StartTicks = minimum;
                section.StartExclusive = false;
            }
            if (maximum > section.EndTicks || (maximum == section.EndTicks && section.EndExclusive))
            {
                section.EndTicks = maximum;
                section.EndExclusive = false;
            }
        }

        var sections = animation.Bindings.SelectMany(b => b.Tracks).SelectMany(t => t.Sections).ToList();
        if (sections.Count == 0)
            return;

        var allStart = sections.Min(o => o.StartTicks);
        var allStartExclusive = sections.Where(o => o.StartTicks == allStart).All(o => o.StartExclusive);
        var allEnd = sections.Max(o => o.EndTicks);
        var allEndExclusive = sections.Where(o => o.EndTicks == allEnd).All(o => o.EndExclusive);
        if (!ContainsLowerBound(animation.PlaybackStartTicks, animation.PlaybackStartExclusive, allStart, allStartExclusive))
        {
            animation.PlaybackStartTicks = allStart;
            animation.PlaybackStartExclusive = allStartExclusive;
        }
        if (!ContainsUpperBound(animation.PlaybackEndTicks, animation.PlaybackEndExclusive, allEnd, allEndExclusive))
        {
            animation.PlaybackEndTicks = allEnd;
            animation.PlaybackEndExclusive = allEndExclusive;
        }
        ValidateRange(animation.PlaybackStartTicks, animation.PlaybackEndTicks, animation.PlaybackStartExclusive, animation.PlaybackEndExclusive, "PlaybackRange");
    }

    private static bool ContainsLowerBound(int outerStart, bool outerExclusive, int innerStart, bool innerExclusive) =>
        outerStart < innerStart || (outerStart == innerStart && (!outerExclusive || innerExclusive));

    private static bool ContainsUpperBound(int outerEnd, bool outerExclusive, int innerEnd, bool innerExclusive) =>
        outerEnd > innerEnd || (outerEnd == innerEnd && (!outerExclusive || innerExclusive));

    private static double RetimeFactor(JObject operation, WidgetAnim animation, bool snap)
    {
        if (operation["factor"] != null)
        {
            var factor = ReadNumber(operation, "factor", true, 0);
            if (!double.IsFinite(factor) || factor <= 0)
                throw new AgentToolException("retime factor must be a finite positive number.");
            return factor;
        }
        if (operation["new_length_seconds"] == null)
            throw new AgentToolException("retime requires either 'factor' or 'new_length_seconds'.");
        var currentLength = animation.LengthSeconds;
        if (!double.IsFinite(currentLength) || currentLength <= 0)
            throw new AgentToolException("Cannot derive a retime factor from an empty or invalid playback range.");
        var newLengthTicks = SecondsToTicks(ReadNumber(operation, "new_length_seconds", true, 0), animation.TicksPerSecond, animation.DisplayRate, snap);
        if (newLengthTicks <= 0)
            throw new AgentToolException("new_length_seconds must map to at least one timeline tick.");
        return (newLengthTicks / animation.TicksPerSecond) / currentLength;
    }

    private static int SecondsToTicks(double seconds, double ticksPerSecond, double displayRate, bool snap)
    {
        if (!double.IsFinite(seconds) || !double.IsFinite(ticksPerSecond) || ticksPerSecond <= 0)
            throw new AgentToolException("Time and tick resolution must be finite; tick resolution must be positive.");
        var ticks = seconds * ticksPerSecond;
        if (!double.IsFinite(ticks))
            throw new AgentToolException("Time is outside the supported tick range.");
        if (snap && displayRate > 0 && double.IsFinite(displayRate))
        {
            var grid = ticksPerSecond / displayRate;
            if (grid > 0 && double.IsFinite(grid))
                ticks = Math.Round(ticks / grid, MidpointRounding.AwayFromZero) * grid;
        }
        var rounded = Math.Round(ticks, MidpointRounding.AwayFromZero);
        if (rounded < int.MinValue || rounded > int.MaxValue)
            throw new AgentToolException("Time is outside the supported Int32 tick range.");
        return (int) rounded;
    }

    private static int MapTick(int tick, double factor, int offset)
    {
        var mapped = tick * factor + offset;
        if (!double.IsFinite(mapped))
            throw new AgentToolException("Retime produced a non-finite frame number.");
        var rounded = Math.Round(mapped, MidpointRounding.AwayFromZero);
        if (rounded < int.MinValue || rounded > int.MaxValue)
            throw new AgentToolException("Retime produced a frame number outside the supported Int32 tick range.");
        return (int) rounded;
    }

    private static void ValidateRange(int start, int end, bool startExclusive, bool endExclusive, string label)
    {
        if (end < start || (end == start && (startExclusive || endExclusive)))
            throw new AgentToolException($"{label} must be a non-empty ordered range.");
    }

    private static void ValidateAnimation(WidgetAnim animation)
    {
        ValidateRange(animation.PlaybackStartTicks, animation.PlaybackEndTicks,
            animation.PlaybackStartExclusive, animation.PlaybackEndExclusive, "PlaybackRange");
        foreach (var binding in animation.Bindings)
        foreach (var track in binding.Tracks)
        foreach (var section in track.Sections)
        {
            ValidateRange(section.StartTicks, section.EndTicks, section.StartExclusive, section.EndExclusive,
                $"{binding.Widget}/{track.Property}/section[{section.Index}]");
            if (!ContainsRange(animation.PlaybackStartTicks, animation.PlaybackStartExclusive,
                    animation.PlaybackEndTicks, animation.PlaybackEndExclusive,
                    section.StartTicks, section.StartExclusive, section.EndTicks, section.EndExclusive))
                throw new AgentToolException($"PlaybackRange does not contain {binding.Widget}/{track.Property}/section[{section.Index}].");
            foreach (var channel in section.Channels)
            {
                if (channel.Times.Length != channel.Keys.Length)
                    throw new AgentToolException($"{binding.Widget}/{channel.Name} has different time and key counts.");
                if (!double.IsFinite(channel.TicksPerSecond) || channel.TicksPerSecond <= 0)
                    throw new AgentToolException($"{binding.Widget}/{channel.Name} has an invalid tick resolution.");
                for (var i = 0; i < channel.Keys.Length; i++)
                {
                    if (i > 0 && channel.Times[i] <= channel.Times[i - 1])
                        throw new AgentToolException($"{binding.Widget}/{channel.Name} key times must be strictly increasing and collision-free.");
                    if (!InsideRange(channel.Times[i], section.StartTicks, section.StartExclusive, section.EndTicks, section.EndExclusive))
                        throw new AgentToolException($"{binding.Widget}/{channel.Name} key#{i} is outside section[{section.Index}].");
                    var key = channel.Keys[i];
                    if (!float.IsFinite(key.Value) || !float.IsFinite(key.ArriveTangent) || !float.IsFinite(key.LeaveTangent) ||
                        !float.IsFinite(key.ArriveWeight) || !float.IsFinite(key.LeaveWeight) || key.ArriveWeight < 0 || key.LeaveWeight < 0)
                        throw new AgentToolException($"{binding.Widget}/{channel.Name} key#{i} contains an invalid curve value, tangent, or weight.");
                }
            }
        }
    }

    private static bool InsideRange(int value, int start, bool startExclusive, int end, bool endExclusive) =>
        (value > start || value == start && !startExclusive) && (value < end || value == end && !endExclusive);

    private static bool ContainsRange(int outerStart, bool outerStartExclusive, int outerEnd, bool outerEndExclusive,
        int innerStart, bool innerStartExclusive, int innerEnd, bool innerEndExclusive) =>
        (outerStart < innerStart || outerStart == innerStart && (!outerStartExclusive || innerStartExclusive)) &&
        (outerEnd > innerEnd || outerEnd == innerEnd && (!outerEndExclusive || innerEndExclusive));

    private static string FormatResult(WidgetAnim before, WidgetAnim after, IReadOnlyList<string> changes,
        IReadOnlyList<string> operations, int? signaturesRefreshed, bool dryRun)
    {
        var state = dryRun ? "Dry run; no pending edit was recorded." : "Validated and staged in the open blueprint session; no package or pak was written.";
        var sb = new System.Text.StringBuilder($"{after.Name}: {state}\n");
        foreach (var operation in operations)
            sb.AppendLine("  operation: " + operation);
        sb.AppendLine("Diff:");
        foreach (var change in changes.Take(40))
            sb.AppendLine("  " + change);
        if (changes.Count > 40)
            sb.AppendLine($"  ... {changes.Count - 40} more change(s)");
        if (!dryRun)
            sb.AppendLine($"Refreshed signatures: {signaturesRefreshed ?? 0}.");
        sb.AppendLine($"Timeline: {before.LengthSeconds.ToString("0.####", Invariant)} s -> {after.LengthSeconds.ToString("0.####", Invariant)} s.");
        if (after.GameSpeed is { } speed && speed > 0)
            sb.AppendLine($"Approx. on-screen duration at the first forward PlayAnimation speed ({speed:0.####}x): {AnimationChannelEditor.ToRuntimeDurationSeconds(after.LengthSeconds, speed).ToString("0.####", Invariant)} s.");
        else
            sb.AppendLine("On-screen duration is unknown until the blueprint's PlayAnimation speed is resolved.");
        var curveSummaries = ChangedChannels(before, after).Take(8).Select(change => CurveSummary(change.Widget, change.Track, change.Channel, change.Before, change.After, before, after)).ToList();
        if (curveSummaries.Count > 0)
        {
            sb.AppendLine("Preview evaluator summary (sampled; not an engine or game measurement):");
            foreach (var summary in curveSummaries)
                sb.AppendLine("  " + summary);
        }
        return sb.ToString().TrimEnd();
    }

    private sealed record ChangedChannel(string Widget, AnimTrack Track, AnimChannel Channel, AnimChannel Before, AnimChannel After);

    private static IEnumerable<ChangedChannel> ChangedChannels(WidgetAnim before, WidgetAnim after)
    {
        for (var bi = 0; bi < before.Bindings.Count; bi++)
        for (var ti = 0; ti < before.Bindings[bi].Tracks.Count; ti++)
        for (var si = 0; si < before.Bindings[bi].Tracks[ti].Sections.Count; si++)
        {
            var oldSection = before.Bindings[bi].Tracks[ti].Sections[si];
            var newSection = after.Bindings[bi].Tracks[ti].Sections[si];
            foreach (var oldChannel in oldSection.Channels)
            {
                var newChannel = newSection.Channels.First(o => o.Name == oldChannel.Name);
                if (!oldChannel.Times.SequenceEqual(newChannel.Times) || !oldChannel.Keys.SequenceEqual(newChannel.Keys))
                    yield return new ChangedChannel(before.Bindings[bi].Widget, before.Bindings[bi].Tracks[ti], oldChannel, oldChannel, newChannel);
            }
        }
    }

    private static string CurveSummary(string widget, AnimTrack track, AnimChannel channel, AnimChannel beforeChannel, AnimChannel afterChannel,
        WidgetAnim before, WidgetAnim after)
    {
        var start = Math.Min(before.StartSeconds, after.StartSeconds);
        var end = Math.Max(before.EndSeconds, after.EndSeconds);
        var intervals = Math.Clamp(Math.Max(beforeChannel.Keys.Length, afterChannel.Keys.Length) * 16, 16, 128);
        var oldValues = Sample(beforeChannel, start, end, intervals);
        var newValues = Sample(afterChannel, start, end, intervals);
        if (oldValues.Count == 0 || newValues.Count == 0)
            return $"{widget}/{track.Property}/{channel.Name}: no finite preview samples.";

        var oldKeyMin = beforeChannel.Keys.Min(o => o.Value);
        var oldKeyMax = beforeChannel.Keys.Max(o => o.Value);
        var newKeyMin = afterChannel.Keys.Min(o => o.Value);
        var newKeyMax = afterChannel.Keys.Max(o => o.Value);
        var oldOvershoot = oldValues.Min() < oldKeyMin - 1e-4 || oldValues.Max() > oldKeyMax + 1e-4;
        var newOvershoot = newValues.Min() < newKeyMin - 1e-4 || newValues.Max() > newKeyMax + 1e-4;
        return $"{widget}/{track.Property}/{channel.Name}: sampled min/max {oldValues.Min().ToString("0.####", Invariant)}/{oldValues.Max().ToString("0.####", Invariant)} -> {newValues.Min().ToString("0.####", Invariant)}/{newValues.Max().ToString("0.####", Invariant)}; overshoot {oldOvershoot} -> {newOvershoot}; endpoints {oldValues[0].ToString("0.####", Invariant)}..{oldValues[^1].ToString("0.####", Invariant)} -> {newValues[0].ToString("0.####", Invariant)}..{newValues[^1].ToString("0.####", Invariant)}.";
    }

    private static List<double> Sample(AnimChannel channel, double start, double end, int intervals)
    {
        var samples = new List<double>(intervals + 1);
        for (var i = 0; i <= intervals; i++)
        {
            var time = start + (end - start) * i / intervals;
            if (ChannelEvaluator.EvaluateSeconds(channel, time) is { } value && double.IsFinite(value))
                samples.Add(value);
        }
        return samples;
    }

    private static WidgetAnim Clone(WidgetAnim source)
    {
        var copy = new WidgetAnim
        {
            Name = source.Name,
            Variable = source.Variable,
            PlaybackStartTicks = source.PlaybackStartTicks,
            PlaybackEndTicks = source.PlaybackEndTicks,
            PlaybackStartExclusive = source.PlaybackStartExclusive,
            PlaybackEndExclusive = source.PlaybackEndExclusive,
            TicksPerSecond = source.TicksPerSecond,
            DisplayRate = source.DisplayRate,
            Error = source.Error,
            PlayCalls = [.. source.PlayCalls],
            Markers = [.. source.Markers],
        };
        foreach (var binding in source.Bindings)
        {
            var clonedBinding = new AnimBinding { Widget = binding.Widget, IsRootWidget = binding.IsRootWidget };
            foreach (var track in binding.Tracks)
            {
                var clonedTrack = new AnimTrack { ClassName = track.ClassName, Property = track.Property, Kind = track.Kind };
                foreach (var section in track.Sections)
                {
                    var clonedSection = new AnimSection
                    {
                        Index = section.Index,
                        StartTicks = section.StartTicks,
                        EndTicks = section.EndTicks,
                        StartExclusive = section.StartExclusive,
                        EndExclusive = section.EndExclusive,
                    };
                    foreach (var channel in section.Channels)
                    {
                        var clonedChannel = Clone(channel);
                        clonedSection.Channels.Add(clonedChannel);
                        clonedTrack.Channels.Add(clonedChannel);
                    }
                    clonedTrack.Sections.Add(clonedSection);
                }
                clonedBinding.Tracks.Add(clonedTrack);
            }
            copy.Bindings.Add(clonedBinding);
        }
        return copy;
    }

    private static AnimChannel Clone(AnimChannel source) => new()
    {
        Name = source.Name,
        Property = source.Property,
        Times = (int[]) source.Times.Clone(),
        Keys = (AnimKey[]) source.Keys.Clone(),
        PreExtrapolation = source.PreExtrapolation,
        PostExtrapolation = source.PostExtrapolation,
        DefaultValue = source.DefaultValue,
        HasDefaultValue = source.HasDefaultValue,
        TicksPerSecond = source.TicksPerSecond,
    };

    private static WidgetAnim FindAnimation(BlueprintViewerViewModel viewer, string name) =>
        viewer.Designer.ReadAnimations().FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new AgentToolException($"No animation '{name}'; use anim_list to see the available names.");

    private static double ReadNumber(JObject value, string name, bool required, double fallback)
    {
        var token = value[name];
        if (token == null || token.Type == JTokenType.Null)
        {
            if (required)
                throw new AgentToolException($"'{name}' is required.");
            return fallback;
        }
        if (token.Type is not (JTokenType.Integer or JTokenType.Float))
            throw new AgentToolException($"'{name}' must be a JSON number.");
        var number = token.Value<double>();
        if (!double.IsFinite(number))
            throw new AgentToolException($"'{name}' must be finite.");
        return number;
    }

    private static int ReadInt(JObject value, string name, bool required, int fallback)
    {
        var token = value[name];
        if (token == null || token.Type == JTokenType.Null)
        {
            if (required)
                throw new AgentToolException($"'{name}' is required.");
            return fallback;
        }
        if (token.Type != JTokenType.Integer || !int.TryParse(token.ToString(), NumberStyles.Integer, Invariant, out var result))
            throw new AgentToolException($"'{name}' must be a 32-bit integer.");
        return result;
    }

    private static string ReadString(JObject value, string name)
    {
        var token = value[name];
        if (token?.Type != JTokenType.String || string.IsNullOrWhiteSpace(token.Value<string>()))
            throw new AgentToolException($"'{name}' is required and must be a non-empty string.");
        return token.Value<string>();
    }

    private static TEnum ReadEnum<TEnum>(JObject value, string name, TEnum fallback) where TEnum : struct, Enum
    {
        var token = value[name];
        if (token == null || token.Type == JTokenType.Null)
            return fallback;
        if (token.Type != JTokenType.String || !System.Enum.TryParse<TEnum>(token.Value<string>(), true, out var result) || !System.Enum.IsDefined(result))
            throw new AgentToolException($"'{name}' must be a {typeof(TEnum).Name} value.");
        return result;
    }

    private static bool Bool(JObject value, string name, bool fallback)
    {
        var token = value[name];
        if (token == null || token.Type == JTokenType.Null)
            return fallback;
        if (token.Type != JTokenType.Boolean)
            throw new AgentToolException($"'{name}' must be a JSON boolean.");
        return token.Value<bool>();
    }

    private static TangentUpdateMode ReadTangentUpdate(JToken token, TangentUpdateMode fallback)
    {
        if (token == null || token.Type == JTokenType.Null)
            return fallback;
        if (token.Type != JTokenType.String || !System.Enum.TryParse<TangentUpdateMode>(token.Value<string>(), true, out var mode) || !System.Enum.IsDefined(mode))
            throw new AgentToolException("'tangents' must be one of: local, channel, none.");
        return mode;
    }

    private static float ToSingle(double value, string name)
    {
        if (!double.IsFinite(value) || value > float.MaxValue || value < -float.MaxValue)
            throw new AgentToolException($"'{name}' cannot be represented as a finite float.");
        return (float) value;
    }

    private static string RangeText(int start, int end, bool startExclusive, bool endExclusive) =>
        $"{(startExclusive ? "(" : "[")}{start}, {end}{(endExclusive ? ")" : "]")}";
}
