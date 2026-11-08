using System;
using System.Collections.Generic;
using System.Linq;

namespace FModel.BlueprintGraph.Animation;

// In-memory model of a cooked UMG widget animation (WidgetAnimation -> MovieScene -> bindings -> tracks -> float channels).
// It is a plain data model so it can be filled from CUE4Parse (read-only preview) now and from UAssetAPI (editing) later.
// Times are integer ticks of the channel's TickResolution (the game uses 60000 ticks per second).

/// <summary>ERichCurveInterpMode: how the segment that starts at a key is interpolated.</summary>
public enum KeyInterp : byte
{
    Linear = 0,
    Constant = 1,
    Cubic = 2,
    None = 3,
}

/// <summary>ERichCurveTangentMode.</summary>
public enum KeyTangentMode : byte
{
    Auto = 0,
    User = 1,
    Break = 2,
    SmartAuto = 3,
}

/// <summary>ERichCurveTangentWeightMode.</summary>
public enum KeyWeightMode : byte
{
    None = 0,
    Arrive = 1,
    Leave = 2,
    Both = 3,
}

/// <summary>ERichCurveExtrapolation, in the engine's order.</summary>
public enum ChannelExtrapolation : byte
{
    Cycle = 0,
    CycleWithOffset = 1,
    Oscillate = 2,
    Linear = 3,
    Constant = 4,
    None = 5,
}

/// <summary>
/// One key of a float channel. Tangents are slopes in value per tick (not per second); weights are only used when
/// <see cref="WeightMode"/> says so.
/// </summary>
public readonly record struct AnimKey(
    float Value,
    float ArriveTangent,
    float LeaveTangent,
    float ArriveWeight,
    float LeaveWeight,
    KeyWeightMode WeightMode,
    KeyInterp Interp,
    KeyTangentMode TangentMode);

/// <summary>A float curve (FMovieSceneFloatChannel).</summary>
public sealed class AnimChannel
{
    /// <summary>Readable channel name: Translation.X, Scale.Y, Angle, Value, R, G, B, A...</summary>
    public string Name = "";

    /// <summary>The widget property the track drives (RenderTransform, RenderOpacity, ColorAndOpacity...).</summary>
    public string Property = "";

    /// <summary>Key times in ticks, strictly increasing; same length as <see cref="Keys"/>.</summary>
    public int[] Times = [];

    public AnimKey[] Keys = [];
    public ChannelExtrapolation PreExtrapolation = ChannelExtrapolation.Constant;
    public ChannelExtrapolation PostExtrapolation = ChannelExtrapolation.Constant;
    public float DefaultValue;
    public bool HasDefaultValue;
    public double TicksPerSecond = 60000;

    public double FirstSeconds => Times.Length == 0 ? 0 : Times[0] / TicksPerSecond;
    public double LastSeconds => Times.Length == 0 ? 0 : Times[^1] / TicksPerSecond;

    public override string ToString() => $"{Property}.{Name} ({Times.Length} keys)";
}

public enum AnimTrackKind
{
    /// <summary>MovieScene2DTransformTrack: Translation, Scale, Shear and Rotation channels of a RenderTransform.</summary>
    Transform2D,

    /// <summary>MovieSceneFloatTrack: one channel for a float property such as RenderOpacity.</summary>
    Float,

    /// <summary>MovieSceneColorTrack: R, G, B, A channels of a color property.</summary>
    Color,

    /// <summary>Any other track: visibility, margins, vectors... The preview does not apply these.</summary>
    Other,
}

public sealed class AnimTrack
{
    /// <summary>Export class of the track, for example MovieScene2DTransformTrack.</summary>
    public string ClassName = "";

    public AnimTrackKind Kind;

    /// <summary>The bound property name (PropertyBinding.PropertyName).</summary>
    public string Property = "";

    /// <summary>Float channels of all sections of the track.</summary>
    public List<AnimChannel> Channels = [];

    /// <summary>Sections in the order stored by this track. Kept separately so time edits can update their ranges.</summary>
    public List<AnimSection> Sections = [];

    /// <summary>True when the preview knows how to apply this track to a widget.</summary>
    public bool IsPreviewed => AnimationOverlay.IsSupported(this);

    public override string ToString() => $"{ClassName} {Property}";
}

/// <summary>One MovieScene section and its active range, in the animation's tick resolution.</summary>
public sealed class AnimSection
{
    public int Index;
    public int StartTicks;
    public int EndTicks;
    public bool StartExclusive;
    public bool EndExclusive;
    public List<AnimChannel> Channels = [];
}

/// <summary>The tracks that animate one widget of the blueprint (ObjectBindings[].BindingName is the widget name).</summary>
public sealed class AnimBinding
{
    public string Widget = "";

    /// <summary>
    /// True when the binding animates the user widget itself, the whole blueprint (WidgetAnimation.AnimationBindings[].bIsRootWidget),
    /// not a widget inside its tree. <see cref="Widget"/> is then a generated name such as WBP_Foo_C_66 that no tree widget has.
    /// </summary>
    public bool IsRootWidget;

    public List<AnimTrack> Tracks = [];
}

/// <summary>An audio or event marker of the animation timeline (not played by the preview).</summary>
public readonly record struct AnimMarker(double Seconds, string Kind, string Name);

public sealed class WidgetAnim
{
    /// <summary>The name blueprints use (DisplayLabel, or the export name without _INST).</summary>
    public string Name = "";

    /// <summary>MovieScene.PlaybackRange in ticks; <see cref="PlaybackEndExclusive"/> says whether the upper bound is exclusive.</summary>
    public int PlaybackStartTicks;

    public int PlaybackEndTicks;
    public bool PlaybackStartExclusive;
    public bool PlaybackEndExclusive;
    public double TicksPerSecond = 60000;

    /// <summary>Editor frame grid in frames per second (20 for most game animations).</summary>
    public double DisplayRate;

    public List<AnimBinding> Bindings = [];
    public List<AnimMarker> Markers = [];

    /// <summary>Set when the animation could not be read completely; the other fields hold what was read before the failure.</summary>
    public string? Error;

    /// <summary>The blueprint variable of the animation (the export name without _INST); PlayAnimation calls name the animation by it.</summary>
    public string Variable = "";

    /// <summary>The PlayAnimation calls in the blueprint's code that play this animation.</summary>
    public List<PlayCall> PlayCalls = [];

    /// <summary>The playback speed the game passes when it plays the animation forward, when it is a constant (the first such call).</summary>
    public double? GameSpeed => PlayCalls.FirstOrDefault(o => !o.Reverse && o.Speed is > 0)?.Speed;

    public double StartSeconds => PlaybackStartTicks / TicksPerSecond;

    /// <summary>The engine uses the raw upper bound of the playback range as the end time.</summary>
    public double EndSeconds => PlaybackEndTicks / TicksPerSecond;

    public double LengthSeconds => EndSeconds - StartSeconds;

    public IEnumerable<AnimChannel> AllChannels => Bindings.SelectMany(b => b.Tracks).SelectMany(t => t.Channels);

    /// <summary>Time from the first to the last key over all channels that actually move (the "effective" duration).</summary>
    public double ActiveSeconds
    {
        get
        {
            var moving = AllChannels.Where(c => c.Times.Length >= 2 && c.Keys.Any(k => k.Value != c.Keys[0].Value)).ToList();
            return moving.Count == 0 ? 0 : moving.Max(c => c.LastSeconds) - moving.Min(c => c.FirstSeconds);
        }
    }

    public override string ToString() => $"{Name} ({LengthSeconds:0.###} s)";
}
