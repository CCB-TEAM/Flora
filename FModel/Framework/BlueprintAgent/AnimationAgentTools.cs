using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FModel.BlueprintGraph.Animation;
using FModel.BlueprintGraph.Widgets;
using FModel.ViewModels;
using FModel.Views.BlueprintViewer;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using static FModel.Framework.BlueprintAgent.Schema;

namespace FModel.Framework.BlueprintAgent;

/// <summary>
/// Read-only tools for the UMG animations of an open widget blueprint: list them, read their keys, evaluate their curves and
/// render a filmstrip. Nothing here changes the asset. See docs/flora-timeline-studio-design.md in the kardsweb repository.
/// </summary>
public static class AnimationAgentTools
{
    private static readonly JObject BlueprintArg = String("Open widget blueprint: package path or name (see list_blueprints). Optional when only one is open.");
    private static readonly JObject AnimationArg = String("Animation name as anim_list shows it.");
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IEnumerable<AgentTool> Build() =>
    [
        new()
        {
            Name = "anim_list",
            Description = "Lists the UMG animations of a widget blueprint, read from the cooked asset: length, the span in which something moves, the editor frame grid, how many widgets, " +
                          "tracks, channels and keys, and sound markers. All times are timeline times. On screen an animation lasts timeline length / PlaybackSpeed, and the speed is an argument " +
                          "of the PlayAnimation call in the blueprint code: the game often plays UI animations at 2x to 8x. For every animation this lists the PlayAnimation calls found in the " +
                          "blueprint (direction, speed, start time, loops) and the resulting time on screen.",
            Access = AgentToolAccess.Read,
            InputSchema = Object(("blueprint", BlueprintArg, false)),
            Run = (args, _) =>
            {
                var viewer = WidgetViewer(args);
                var animations = viewer.Designer.ReadAnimations();
                if (animations.Count == 0)
                    return Task.FromResult($"{viewer.Entry.Path} has no animations.");

                var sb = new StringBuilder();
                sb.AppendLine($"{viewer.Entry.Path}: {animations.Count} animation(s)");
                foreach (var a in animations)
                {
                    if (a.Error != null)
                    {
                        sb.AppendLine($"  {a.Name}: could not be read completely ({a.Error})");
                        continue;
                    }

                    var channels = a.AllChannels.ToList();
                    var tracks = a.Bindings.SelectMany(o => o.Tracks).ToList();
                    sb.Append($"  {a.Name}: length {Num(a.LengthSeconds)} s, moving {Num(a.ActiveSeconds)} s, grid {Num(a.DisplayRate)} fps, ")
                        .Append($"{a.Bindings.Count} widget(s), {tracks.Count} track(s), {channels.Count} channel(s), {channels.Sum(o => o.Keys.Length)} key(s)");
                    if (a.Markers.Count > 0)
                        sb.Append($", {a.Markers.Count} sound marker(s)");
                    var notDrawn = tracks.Where(o => !o.IsPreviewed).Select(o => $"{o.ClassName}.{o.Property}").Distinct().ToList();
                    if (notDrawn.Count > 0)
                        sb.Append($"; the preview does not draw: {string.Join(", ", notDrawn)}");
                    sb.AppendLine();

                    // how the blueprint code plays it: the speed decides how long it lasts on screen
                    foreach (var call in a.PlayCalls)
                        sb.AppendLine($"      played by the blueprint: {call}{(call.Function.StartsWith("ExecuteUbergraph", StringComparison.Ordinal) ? "" : $" (in {call.Function})")}");
                    if (a.GameSpeed is { } speed)
                        sb.AppendLine($"      on screen: about {(a.LengthSeconds / speed * 1000).ToString("0.#", Invariant)} ms when played forward (timeline {Num(a.LengthSeconds)} s / speed {Num(speed)})");
                }
                return Task.FromResult(sb.ToString());
            },
        },
        new()
        {
            Name = "anim_get",
            Description = "The keys of one animation: for every widget it animates, its tracks, section indices/ranges and channels with every zero-based key index (time in seconds, value, interpolation and tangent mode). " +
                          "Keys that are not plain automatic cubic keys also show their tangents in value per second and their weights. Filter by widget or channel name to keep it short.",
            Access = AgentToolAccess.Read,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("animation", AnimationArg, true),
                ("widget", String("Only this widget (the name the animation binds to, case-insensitive)."), false),
                ("channel", String("Only channels whose name or property contains this, e.g. Translation, Scale, RenderOpacity, R."), false)),
            Run = (args, _) =>
            {
                var viewer = WidgetViewer(args);
                var animation = FindAnimation(viewer, args.Str("animation"));
                var widget = args.Str("widget", false);
                var channelFilter = args.Str("channel", false);

                var sb = new StringBuilder();
                sb.AppendLine($"{animation.Name}: length {Num(animation.LengthSeconds)} s ({animation.PlaybackStartTicks} to {animation.PlaybackEndTicks} ticks at {Num(animation.TicksPerSecond)}/s), grid {Num(animation.DisplayRate)} fps");
                foreach (var binding in animation.Bindings.Where(o => widget == null || string.Equals(o.Widget, widget, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var track in binding.Tracks)
                    {
                        var sections = track.Sections
                            .Select(section => (Section: section, Channels: section.Channels
                                .Where(channel => channelFilter == null || channel.Name.Contains(channelFilter, StringComparison.OrdinalIgnoreCase) || channel.Property.Contains(channelFilter, StringComparison.OrdinalIgnoreCase))
                                .Where(channel => channel.Keys.Length > 0)
                                .ToList()))
                            .Where(o => o.Channels.Count > 0)
                            .ToList();
                        if (sections.Count == 0)
                            continue;

                        foreach (var section in sections)
                        {
                            sb.AppendLine($"{binding.Widget}{(binding.IsRootWidget ? " (the whole widget)" : "")} / {track.ClassName} / {track.Property} / section[{section.Section.Index}] {RangeText(section.Section.StartTicks, section.Section.EndTicks, section.Section.StartExclusive, section.Section.EndExclusive)}{(track.IsPreviewed ? "" : "  (the preview does not draw this track)")}");
                            foreach (var channel in section.Channels)
                            {
                                sb.AppendLine($"  {channel.Name}  ({channel.Keys.Length} key(s); before/after: {channel.PreExtrapolation}/{channel.PostExtrapolation})");
                                for (var i = 0; i < channel.Keys.Length; i++)
                                    sb.AppendLine($"    key#{i}: {KeyText(channel, i)}");
                            }
                        }
                    }
                }
                foreach (var marker in animation.Markers)
                    sb.AppendLine($"{marker.Kind} marker at {Num(marker.Seconds)} s: {marker.Name}");
                return Task.FromResult(sb.ToString());
            },
        },
        new()
        {
            Name = "anim_eval",
            Description = "Evaluates the curves of an animation at some times (timeline seconds) the way the engine does, using the stored tangents. Returns a table: one row per channel, one column per time. " +
                          "Use it to check peaks, overshoot and end values without looking at pictures. Default: 9 evenly spaced times from the start to the end of the animation.",
            Access = AgentToolAccess.Read,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("animation", AnimationArg, true),
                ("times", Array("Times in seconds on the animation's timeline.", Number("A time in seconds.")), false),
                ("widget", String("Only this widget."), false),
                ("channel", String("Only channels whose name or property contains this."), false)),
            Run = (args, _) =>
            {
                var viewer = WidgetViewer(args);
                var animation = FindAnimation(viewer, args.Str("animation"));
                var times = ParseTimes(args, animation, 9);
                var widget = args.Str("widget", false);
                var channelFilter = args.Str("channel", false);

                var rows = new List<string[]>();
                var header = new[] { "channel" }.Concat(times.Select(o => Num(o) + " s")).ToArray();
                foreach (var binding in animation.Bindings.Where(o => widget == null || string.Equals(o.Widget, widget, StringComparison.OrdinalIgnoreCase)))
                foreach (var track in binding.Tracks)
                foreach (var channel in track.Channels.Where(o => o.Keys.Length > 0))
                {
                    if (channelFilter != null && !channel.Name.Contains(channelFilter, StringComparison.OrdinalIgnoreCase) && !channel.Property.Contains(channelFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    rows.Add(new[] { $"{binding.Widget}.{channel.Property}.{channel.Name}" }
                        .Concat(times.Select(t => ChannelEvaluator.EvaluateSeconds(channel, t) is { } v ? v.ToString("0.####", Invariant) : "-")).ToArray());
                }
                if (rows.Count == 0)
                    return Task.FromResult("No channel matches.");

                var sb = new StringBuilder();
                sb.AppendLine($"{animation.Name}: values on the timeline (a time outside the keys holds the first or last value)");
                sb.Append(Table(header, rows));
                return Task.FromResult(sb.ToString());
            },
        },
        new()
        {
            Name = "anim_preview",
            Description = "Renders a filmstrip of an animation to a PNG file (a few frames of the whole widget at chosen times) and returns its path, so the animation can be looked at by opening the image. " +
                          "The picture shows the widget laid out by Flora's designer with the animation's values applied: positions, scales, rotation, opacity and colors are exact, " +
                          "fonts, materials and blur are approximate. Times are timeline seconds, not real seconds (see anim_list about PlaybackSpeed). The file goes to the temp folder; nothing in the game or the project changes.",
            Access = AgentToolAccess.Read,
            InputSchema = Object(("blueprint", BlueprintArg, false), ("animation", AnimationArg, true),
                ("times", Array("Times in seconds on the animation's timeline. Default: evenly spaced from start to end.", Number("A time in seconds.")), false),
                ("count", Integer("How many evenly spaced frames when 'times' is not given (default 6, at most 16)."), false),
                ("frame_width", Integer("Width of one frame in pixels (default 360)."), false),
                ("resolution", String("For a full-screen widget: the screen resolution to lay out for, e.g. 1920x1080 (the game scales its UI by the shortest side, so the widget is laid out in a larger space). Default: the designer's size."), false)),
            Run = async (args, _) =>
            {
                var viewer = WidgetViewer(args);
                var animation = FindAnimation(viewer, args.Str("animation"));
                var times = ParseTimes(args, animation, Math.Clamp(args.Int("count", 6), 1, 16));
                var frameWidth = Math.Clamp(args.Int("frame_width", 360), 120, 1200);

                var directory = Path.Combine(Path.GetTempPath(), "Flora", "anim-preview");
                var file = Path.Combine(directory, $"{Safe(viewer.Entry.NameWithoutExtension)}_{Safe(animation.Name)}_{DateTime.Now:yyyyMMdd-HHmmss}.png");

                FilmstripResult result;
                try
                {
                    result = await AnimationFilmstrip.RenderAsync(viewer.Designer, animation, times, frameWidth, args.Str("resolution", false), file);
                }
                catch (ArgumentException e)
                {
                    throw new AgentToolException(e.Message);
                }
                return $"{result.File}\n{result.Width}x{result.Height} px, {result.Frames.Count} frame(s) of {result.FrameWidth}x{result.FrameHeight}; {result.Note}design space {result.DesignWidth:0}x{result.DesignHeight:0}.\n" +
                       string.Join("\n", result.Frames.Select(o => "  " + o));
            },
        },
    ];

    // ---- helpers ----

    private static JObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    private static BlueprintViewerViewModel WidgetViewer(JObject args)
    {
        var viewer = BlueprintAgentHost.Resolve(args.Str("blueprint", false));
        if (viewer.Designer.Info?.WidgetClass == null)
            throw new AgentToolException($"{viewer.Entry.Path} is not a widget blueprint, or it is still loading (open_blueprint waits for it).");
        return viewer;
    }

    private static WidgetAnim FindAnimation(BlueprintViewerViewModel viewer, string name)
    {
        var all = viewer.Designer.ReadAnimations();
        return all.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new AgentToolException($"No animation '{name}'. This blueprint has: {string.Join(", ", all.Select(o => o.Name))} (anim_list).");
    }

    private static List<double> ParseTimes(JObject args, WidgetAnim animation, int defaultCount)
    {
        var given = new List<double>();
        if (args["times"] is { Type: not JTokenType.Null } token)
        {
            var items = token is JArray array ? array.Select(o => o).ToList() : token.ToString().Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(o => (JToken) o).ToList();
            foreach (var item in items)
            {
                if (item.Type is JTokenType.Integer or JTokenType.Float)
                    given.Add(item.Value<double>());
                else if (double.TryParse(item.ToString(), NumberStyles.Float, Invariant, out var parsed))
                    given.Add(parsed);
                else
                    throw new AgentToolException($"'{item}' in 'times' is not a number.");
            }
        }
        if (given.Count > 0)
            return given.Take(64).ToList();

        var start = animation.StartSeconds;
        var end = animation.EndSeconds;
        if (defaultCount <= 1 || end <= start)
            return [start];
        return Enumerable.Range(0, defaultCount).Select(i => start + (end - start) * i / (defaultCount - 1)).ToList();
    }

    private static string Num(double value) => value.ToString("0.###", Invariant);

    private static string RangeText(int start, int end, bool startExclusive, bool endExclusive) =>
        $"{(startExclusive ? "(" : "[")}{start}, {end}{(endExclusive ? ")" : "]")}";

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static string KeyText(AnimChannel channel, int index)
    {
        var key = channel.Keys[index];
        var text = $"{Num(channel.Times[index] / channel.TicksPerSecond)} s = {key.Value.ToString("0.####", Invariant)}  {key.Interp.ToString().ToLowerInvariant()}/{key.TangentMode.ToString().ToLowerInvariant()}";
        if (key.Interp == KeyInterp.Cubic && key.TangentMode != KeyTangentMode.Auto)
        {
            var perSecond = channel.TicksPerSecond;
            text += $"  tangents arrive {(key.ArriveTangent * perSecond).ToString("0.###", Invariant)} leave {(key.LeaveTangent * perSecond).ToString("0.###", Invariant)} per s";
        }
        if (key.WeightMode != KeyWeightMode.None)
            text += $"  weights {key.WeightMode.ToString().ToLowerInvariant()} arrive {key.ArriveWeight.ToString("0.###", Invariant)} leave {key.LeaveWeight.ToString("0.###", Invariant)}";
        return text;
    }

    private static string Table(string[] header, List<string[]> rows)
    {
        var widths = new int[header.Length];
        foreach (var row in rows.Prepend(header))
            for (var i = 0; i < row.Length; i++)
                widths[i] = Math.Max(widths[i], row[i].Length);

        var sb = new StringBuilder();
        foreach (var row in rows.Prepend(header))
            sb.AppendLine(string.Join("  ", row.Select((o, i) => i == 0 ? o.PadRight(widths[i]) : o.PadLeft(widths[i]))));
        return sb.ToString();
    }
}
