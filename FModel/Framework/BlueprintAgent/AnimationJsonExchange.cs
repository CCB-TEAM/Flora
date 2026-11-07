using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FModel.BlueprintGraph.Animation;
using Newtonsoft.Json.Linq;

namespace FModel.Framework.BlueprintAgent;

/// <summary>Stable, reviewable JSON exchange for edits to the existing P3 animation structure.</summary>
internal static class AnimationJsonExchange
{
    public static JObject Export(WidgetAnim animation, string blueprint)
    {
        var bindings = new JArray();
        foreach (var binding in animation.Bindings)
        {
            var tracks = new JArray();
            foreach (var track in binding.Tracks)
            {
                var sections = new JArray();
                foreach (var section in track.Sections)
                {
                    var channels = new JArray();
                    foreach (var channel in section.Channels)
                    {
                        var keys = new JArray();
                        for (var i = 0; i < channel.Keys.Length; i++)
                        {
                            var key = channel.Keys[i];
                            keys.Add(new JObject
                            {
                                ["index"] = i,
                                ["t"] = channel.Times[i],
                                ["v"] = key.Value,
                                ["interp"] = key.Interp.ToString().ToLowerInvariant(),
                                ["tangentMode"] = key.TangentMode.ToString().ToLowerInvariant(),
                                ["arrivePerSecond"] = key.ArriveTangent * channel.TicksPerSecond,
                                ["leavePerSecond"] = key.LeaveTangent * channel.TicksPerSecond,
                                ["weightMode"] = key.WeightMode.ToString().ToLowerInvariant(),
                                ["arriveWeight"] = key.ArriveWeight,
                                ["leaveWeight"] = key.LeaveWeight,
                            });
                        }

                        channels.Add(new JObject
                        {
                            ["name"] = channel.Name,
                            ["property"] = channel.Property,
                            ["tickResolution"] = channel.TicksPerSecond,
                            ["preExtrapolation"] = channel.PreExtrapolation.ToString().ToLowerInvariant(),
                            ["postExtrapolation"] = channel.PostExtrapolation.ToString().ToLowerInvariant(),
                            ["hasDefaultValue"] = channel.HasDefaultValue,
                            ["defaultValue"] = channel.DefaultValue,
                            ["keys"] = keys,
                        });
                    }

                    sections.Add(new JObject
                    {
                        ["index"] = section.Index,
                        ["startTicks"] = section.StartTicks,
                        ["endTicks"] = section.EndTicks,
                        ["startExclusive"] = section.StartExclusive,
                        ["endExclusive"] = section.EndExclusive,
                        ["channels"] = channels,
                    });
                }

                tracks.Add(new JObject
                {
                    ["class"] = track.ClassName,
                    ["property"] = track.Property,
                    ["kind"] = track.Kind.ToString(),
                    ["sections"] = sections,
                });
            }

            bindings.Add(new JObject
            {
                ["widget"] = binding.Widget,
                ["isRootWidget"] = binding.IsRootWidget,
                ["tracks"] = tracks,
            });
        }

        return new JObject
        {
            ["schemaVersion"] = 1,
            ["blueprint"] = blueprint,
            ["animation"] = animation.Name,
            ["variable"] = animation.Variable,
            ["ticksPerSecond"] = animation.TicksPerSecond,
            ["displayRate"] = animation.DisplayRate,
            ["playbackRange"] = Range(animation.PlaybackStartTicks, animation.PlaybackEndTicks,
                animation.PlaybackStartExclusive, animation.PlaybackEndExclusive),
            ["bindings"] = bindings,
            ["notes"] = new JArray
            {
                "P3 imports can edit existing key values, times, interpolation, cubic User/Break tangents, and ranges.",
                "Bindings, tracks, sections, channels, key counts, extrapolation, defaults, and playback calls are read-only.",
                "Auto tangent numbers are informational; the editor recomputes them locally when a key edit changes their neighborhood.",
                "Tangent weights are exported in the channel's stored units. Non-identity retime of weighted channels is rejected.",
            },
        };
    }

    /// <summary>Converts an exported document into a validated, deterministic set of existing-key edit operations.</summary>
    public static IReadOnlyList<JObject> BuildImportOperations(WidgetAnim current, JObject document, string blueprint)
    {
        if (ReadInt(document, "schemaVersion") != 1)
            throw new ArgumentException("Only animation JSON schemaVersion 1 is supported.");
        if (document["blueprint"]?.Type == JTokenType.String &&
            !string.Equals(ReadString(document, "blueprint"), blueprint, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The file targets blueprint '{ReadString(document, "blueprint")}', not '{blueprint}'.");
        if (!string.Equals(ReadString(document, "variable"), current.Variable, StringComparison.Ordinal) ||
            !string.Equals(ReadString(document, "animation"), current.Name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The JSON animation identity does not match the selected animation.");
        if (!NearlyEqual(ReadNumber(document, "ticksPerSecond"), current.TicksPerSecond) ||
            !NearlyEqual(ReadNumber(document, "displayRate"), current.DisplayRate))
            throw new ArgumentException("The JSON timebase does not match the currently loaded animation.");

        var importedPlayback = ReadRange(RequireObject(document, "playbackRange"));
        var importedBindings = RequireArray(document, "bindings");
        if (importedBindings.Count != current.Bindings.Count)
            throw new ArgumentException("P3 import cannot add or remove animation bindings.");

        var operations = new List<JObject>();
        var rangesToApply = new List<JObject>();
        for (var bi = 0; bi < current.Bindings.Count; bi++)
        {
            var binding = current.Bindings[bi];
            var jsonBinding = RequireObject(importedBindings[bi], $"bindings[{bi}]");
            if (ReadString(jsonBinding, "widget") != binding.Widget || ReadBool(jsonBinding, "isRootWidget") != binding.IsRootWidget)
                throw new ArgumentException($"Binding identity changed at bindings[{bi}]; P3 import cannot retarget animations.");
            var tracks = RequireArray(jsonBinding, "tracks");
            if (tracks.Count != binding.Tracks.Count)
                throw new ArgumentException($"P3 import cannot add or remove tracks from '{binding.Widget}'.");

            for (var ti = 0; ti < binding.Tracks.Count; ti++)
            {
                var track = binding.Tracks[ti];
                var jsonTrack = RequireObject(tracks[ti], $"bindings[{bi}].tracks[{ti}]");
                if (ReadString(jsonTrack, "class") != track.ClassName || ReadString(jsonTrack, "property") != track.Property ||
                    ReadEnum<AnimTrackKind>(jsonTrack, "kind") != track.Kind)
                    throw new ArgumentException($"Track identity changed at {binding.Widget}/tracks[{ti}].");
                var sections = RequireArray(jsonTrack, "sections");
                if (sections.Count != track.Sections.Count)
                    throw new ArgumentException($"P3 import cannot add or remove sections from {binding.Widget}/{track.Property}.");

                for (var si = 0; si < track.Sections.Count; si++)
                {
                    var section = track.Sections[si];
                    var jsonSection = RequireObject(sections[si], $"bindings[{bi}].tracks[{ti}].sections[{si}]");
                    if (ReadInt(jsonSection, "index") != section.Index)
                        throw new ArgumentException($"Section order changed at {binding.Widget}/{track.Property}/sections[{si}].");
                    var range = ReadRange(jsonSection);
                    var channels = RequireArray(jsonSection, "channels");
                    if (channels.Count != section.Channels.Count)
                        throw new ArgumentException($"P3 import cannot add or remove channels from {binding.Widget}/{track.Property}/section[{section.Index}].");

                    for (var ci = 0; ci < section.Channels.Count; ci++)
                    {
                        var channel = section.Channels[ci];
                        var jsonChannel = RequireObject(channels[ci], $"sections[{si}].channels[{ci}]");
                        if (ReadString(jsonChannel, "name") != channel.Name || ReadString(jsonChannel, "property") != channel.Property ||
                            !NearlyEqual(ReadNumber(jsonChannel, "tickResolution"), channel.TicksPerSecond))
                            throw new ArgumentException($"Channel identity or timebase changed at {binding.Widget}/{track.Property}/section[{section.Index}]/channels[{ci}].");
                        if (ReadEnum<ChannelExtrapolation>(jsonChannel, "preExtrapolation") != channel.PreExtrapolation ||
                            ReadEnum<ChannelExtrapolation>(jsonChannel, "postExtrapolation") != channel.PostExtrapolation ||
                            ReadBool(jsonChannel, "hasDefaultValue") != channel.HasDefaultValue ||
                            !NearlyEqual(ReadNumber(jsonChannel, "defaultValue"), channel.DefaultValue))
                            throw new ArgumentException($"P3 import cannot change extrapolation or default value at {binding.Widget}/{channel.Name}.");

                        var jsonKeys = RequireArray(jsonChannel, "keys");
                        if (jsonKeys.Count != channel.Keys.Length)
                            throw new ArgumentException($"P3 import cannot add or remove keys from {binding.Widget}/{channel.Name}.");
                        var importedTimes = new int[jsonKeys.Count];
                        var importedKeys = new AnimKey[jsonKeys.Count];
                        for (var ki = 0; ki < jsonKeys.Count; ki++)
                        {
                            var jsonKey = RequireObject(jsonKeys[ki], $"channels[{ci}].keys[{ki}]");
                            if (ReadInt(jsonKey, "index") != ki)
                                throw new ArgumentException($"Key order changed at {binding.Widget}/{channel.Name}/keys[{ki}].");
                            importedTimes[ki] = ReadInt(jsonKey, "t");
                            var interp = ReadEnum<KeyInterp>(jsonKey, "interp");
                            var tangentMode = ReadEnum<KeyTangentMode>(jsonKey, "tangentMode");
                            var weightMode = ReadEnum<KeyWeightMode>(jsonKey, "weightMode");
                            importedKeys[ki] = new AnimKey(
                                ReadSingle(jsonKey, "v"),
                                ToPerTick(ReadNumber(jsonKey, "arrivePerSecond"), channel.TicksPerSecond),
                                ToPerTick(ReadNumber(jsonKey, "leavePerSecond"), channel.TicksPerSecond),
                                ReadSingle(jsonKey, "arriveWeight"),
                                ReadSingle(jsonKey, "leaveWeight"),
                                weightMode,
                                interp,
                                tangentMode);
                        }
                        ValidateTimes(importedTimes, $"{binding.Widget}/{channel.Name}");
                        foreach (var tick in importedTimes)
                        {
                            if (!Contains(range.Start, range.StartExclusive, range.End, range.EndExclusive, tick))
                                throw new ArgumentException($"Imported section range does not contain key time {tick} at {binding.Widget}/{channel.Name}.");
                        }

                        AppendMoves(operations, binding.Widget, track, section, channel, importedTimes);
                        AppendValueAndCurveEdits(operations, binding.Widget, track, section, channel, importedKeys);
                    }

                    if (!SameRange(section.StartTicks, section.EndTicks, section.StartExclusive, section.EndExclusive, range))
                        rangesToApply.Add(RangeOperation("section", binding.Widget, track, section.Index, range, current.TicksPerSecond));
                }
            }
        }

        for (var bi = 0; bi < current.Bindings.Count; bi++)
        for (var ti = 0; ti < current.Bindings[bi].Tracks.Count; ti++)
        foreach (var section in current.Bindings[bi].Tracks[ti].Sections)
        {
            var range = FindImportedSectionRange(importedBindings, bi, ti, section.Index);
            if (!ContainsRange(importedPlayback, range))
                throw new ArgumentException($"Imported playback range does not contain {current.Bindings[bi].Widget}/{current.Bindings[bi].Tracks[ti].Property}/section[{section.Index}].");
        }

        if (!SameRange(current.PlaybackStartTicks, current.PlaybackEndTicks, current.PlaybackStartExclusive, current.PlaybackEndExclusive, importedPlayback))
            rangesToApply.Add(RangeOperation("playback", null, null, 0, importedPlayback, current.TicksPerSecond));
        operations.AddRange(rangesToApply);
        if (operations.Count > 64)
            throw new ArgumentException($"This import requires {operations.Count} edit operations; anim_edit currently accepts at most 64 operations per atomic batch. Use retime for uniform time changes or split the import.");
        return operations;
    }

    private static void AppendMoves(List<JObject> operations, string widget, AnimTrack track, AnimSection section, AnimChannel channel, int[] targetTimes)
    {
        var current = (int[]) channel.Times.Clone();
        var guard = 0;
        while (!current.SequenceEqual(targetTimes) && guard++ < targetTimes.Length * targetTimes.Length + 1)
        {
            var moved = false;
            for (var i = current.Length - 1; i >= 0; i--)
            {
                if (current[i] == targetTimes[i] || !Fits(i, targetTimes[i], current))
                    continue;
                operations.Add(KeyOperation("move_key", widget, track, section.Index, channel.Name, i,
                    (double) targetTimes[i] / channel.TicksPerSecond, "time_seconds"));
                current[i] = targetTimes[i];
                moved = true;
            }
            for (var i = 0; i < current.Length; i++)
            {
                if (current[i] == targetTimes[i] || !Fits(i, targetTimes[i], current))
                    continue;
                operations.Add(KeyOperation("move_key", widget, track, section.Index, channel.Name, i,
                    (double) targetTimes[i] / channel.TicksPerSecond, "time_seconds"));
                current[i] = targetTimes[i];
                moved = true;
            }
            if (!moved)
                throw new ArgumentException($"The time edits for {widget}/{channel.Name} cannot be applied without crossing keys.");
        }
        if (!current.SequenceEqual(targetTimes))
            throw new ArgumentException($"The time edits for {widget}/{channel.Name} exceeded the safe move schedule.");
    }

    private static bool Fits(int index, int target, int[] current) =>
        (index == 0 || target > current[index - 1]) && (index + 1 == current.Length || target < current[index + 1]);

    private static void AppendValueAndCurveEdits(List<JObject> operations, string widget, AnimTrack track, AnimSection section, AnimChannel channel, AnimKey[] targetKeys)
    {
        for (var i = 0; i < targetKeys.Length; i++)
        {
            var old = channel.Keys[i];
            var target = targetKeys[i];
            if (old.Value != target.Value)
                operations.Add(KeyOperation("set_key", widget, track, section.Index, channel.Name, i, target.Value, "value"));
        }
        for (var i = 0; i < targetKeys.Length; i++)
        {
            var old = channel.Keys[i];
            var target = targetKeys[i];
            if (old.Interp != target.Interp || old.TangentMode != target.TangentMode)
            {
                var operation = KeyOperation("set_interp", widget, track, section.Index, channel.Name, i, null, null);
                operation["interp"] = target.Interp.ToString();
                operation["tangent_mode"] = target.TangentMode.ToString();
                operations.Add(operation);
            }
        }
        for (var i = 0; i < targetKeys.Length; i++)
        {
            var old = channel.Keys[i];
            var target = targetKeys[i];
            if (target.Interp != KeyInterp.Cubic || target.TangentMode is not (KeyTangentMode.User or KeyTangentMode.Break))
                continue;
            var arrive = target.ArriveTangent * channel.TicksPerSecond;
            var leave = target.LeaveTangent * channel.TicksPerSecond;
            if (NearlyEqual(old.ArriveTangent * channel.TicksPerSecond, arrive) &&
                NearlyEqual(old.LeaveTangent * channel.TicksPerSecond, leave) && old.WeightMode == target.WeightMode &&
                NearlyEqual(old.ArriveWeight, target.ArriveWeight) && NearlyEqual(old.LeaveWeight, target.LeaveWeight))
                continue;

            var operation = KeyOperation("set_tangent", widget, track, section.Index, channel.Name, i, null, null);
            operation["arrive_per_second"] = arrive;
            operation["leave_per_second"] = leave;
            operation["tangent_mode"] = target.TangentMode.ToString();
            operation["weight_mode"] = target.WeightMode.ToString();
            operation["arrive_weight"] = target.ArriveWeight;
            operation["leave_weight"] = target.LeaveWeight;
            operations.Add(operation);
        }
    }

    private static JObject KeyOperation(string op, string widget, AnimTrack track, int section, string channel, int key, object value, string valueName)
    {
        var operation = new JObject
        {
            ["op"] = op,
            ["widget"] = widget,
            ["track_class"] = track.ClassName,
            ["property"] = track.Property,
            ["section"] = section,
            ["channel"] = channel,
            ["key"] = key,
        };
        if (valueName != null)
            operation[valueName] = JToken.FromObject(value);
        return operation;
    }

    private static JObject RangeOperation(string scope, string widget, AnimTrack track, int section, (int Start, int End, bool StartExclusive, bool EndExclusive) range, double ticksPerSecond)
    {
        var operation = new JObject
        {
            ["op"] = "set_range",
            ["scope"] = scope,
            ["start_seconds"] = (double) range.Start / ticksPerSecond,
            ["end_seconds"] = (double) range.End / ticksPerSecond,
            ["start_exclusive"] = range.StartExclusive,
            ["end_exclusive"] = range.EndExclusive,
        };
        if (scope == "section")
        {
            operation["widget"] = widget;
            operation["track_class"] = track.ClassName;
            operation["property"] = track.Property;
            operation["section"] = section;
        }
        return operation;
    }

    private static (int Start, int End, bool StartExclusive, bool EndExclusive) FindImportedSectionRange(JArray bindings, int bindingIndex, int trackIndex, int sectionIndex)
    {
        var binding = RequireObject(bindings[bindingIndex], "binding");
        var track = RequireObject(RequireArray(binding, "tracks")[trackIndex], "track");
        var section = RequireObject(RequireArray(track, "sections").First(o => ReadInt(RequireObject(o, "section"), "index") == sectionIndex), "section");
        return ReadRange(section);
    }

    private static JObject Range(int start, int end, bool startExclusive, bool endExclusive) => new()
    {
        ["startTicks"] = start,
        ["endTicks"] = end,
        ["startExclusive"] = startExclusive,
        ["endExclusive"] = endExclusive,
    };

    private static (int Start, int End, bool StartExclusive, bool EndExclusive) ReadRange(JObject range)
    {
        var result = (Start: ReadInt(range, "startTicks"), End: ReadInt(range, "endTicks"),
            StartExclusive: ReadBool(range, "startExclusive"), EndExclusive: ReadBool(range, "endExclusive"));
        if (result.End < result.Start || result.End == result.Start && (result.StartExclusive || result.EndExclusive))
            throw new ArgumentException("Imported ranges must be non-empty and ordered.");
        return result;
    }

    private static bool Contains(int start, bool startExclusive, int end, bool endExclusive, int value) =>
        (value > start || value == start && !startExclusive) && (value < end || value == end && !endExclusive);

    private static bool ContainsRange((int Start, int End, bool StartExclusive, bool EndExclusive) outer,
        (int Start, int End, bool StartExclusive, bool EndExclusive) inner) =>
        (outer.Start < inner.Start || outer.Start == inner.Start && (!outer.StartExclusive || inner.StartExclusive)) &&
        (outer.End > inner.End || outer.End == inner.End && (!outer.EndExclusive || inner.EndExclusive));

    private static bool SameRange(int start, int end, bool startExclusive, bool endExclusive,
        (int Start, int End, bool StartExclusive, bool EndExclusive) target) =>
        start == target.Start && end == target.End && startExclusive == target.StartExclusive && endExclusive == target.EndExclusive;

    private static void ValidateTimes(int[] times, string label)
    {
        for (var i = 1; i < times.Length; i++)
            if (times[i] <= times[i - 1])
                throw new ArgumentException($"Imported key times for {label} must be strictly increasing and collision-free.");
    }

    private static JObject RequireObject(JToken token, string label) => token as JObject ?? throw new ArgumentException($"'{label}' must be a JSON object.");
    private static JObject RequireObject(JObject parent, string name) => RequireObject(parent[name], name);
    private static JArray RequireArray(JObject parent, string name) => parent[name] as JArray ?? throw new ArgumentException($"'{name}' must be a JSON array.");
    private static string ReadString(JObject parent, string name) => parent[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(parent[name].Value<string>())
        ? parent[name].Value<string>()
        : throw new ArgumentException($"'{name}' must be a non-empty string.");
    private static bool ReadBool(JObject parent, string name) => parent[name]?.Type == JTokenType.Boolean
        ? parent[name].Value<bool>()
        : throw new ArgumentException($"'{name}' must be a JSON boolean.");
    private static int ReadInt(JObject parent, string name) => parent[name]?.Type == JTokenType.Integer && int.TryParse(parent[name].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value
        : throw new ArgumentException($"'{name}' must be a 32-bit integer.");
    private static double ReadNumber(JObject parent, string name) => parent[name]?.Type is JTokenType.Integer or JTokenType.Float && double.IsFinite(parent[name].Value<double>())
        ? parent[name].Value<double>()
        : throw new ArgumentException($"'{name}' must be a finite JSON number.");
    private static float ReadSingle(JObject parent, string name)
    {
        var value = ReadNumber(parent, name);
        if (value > float.MaxValue || value < -float.MaxValue)
            throw new ArgumentException($"'{name}' is outside the supported float range.");
        return (float) value;
    }
    private static float ToPerTick(double valuePerSecond, double ticksPerSecond)
    {
        var result = valuePerSecond / ticksPerSecond;
        if (!double.IsFinite(result) || result > float.MaxValue || result < -float.MaxValue)
            throw new ArgumentException("Imported tangent is outside the supported float range.");
        return (float) result;
    }
    private static TEnum ReadEnum<TEnum>(JObject parent, string name) where TEnum : struct, Enum =>
        parent[name]?.Type == JTokenType.String && System.Enum.TryParse<TEnum>(parent[name].Value<string>(), true, out var value) && System.Enum.IsDefined(value)
            ? value
            : throw new ArgumentException($"'{name}' must be a valid {typeof(TEnum).Name} value.");
    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
}
