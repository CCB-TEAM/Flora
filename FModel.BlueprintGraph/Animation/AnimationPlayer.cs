using System;
using System.Collections.Generic;
using FModel.BlueprintGraph.Widgets;

namespace FModel.BlueprintGraph.Animation;

/// <summary>
/// The clock of the animation preview: a time on the animation's timeline that advances at <see cref="Speed"/> times real time.
/// It has no UI; the window feeds it the elapsed time of its timer and asks for the widget state to draw.
/// </summary>
public sealed class AnimationPlayer
{
    public WidgetAnim? Animation { get; private set; }

    /// <summary>Position on the timeline in seconds.</summary>
    public double Time { get; set; }

    /// <summary>Timeline seconds per real second. The game passes this to PlayAnimation (often 2 to 8 for short UI animations).</summary>
    public double Speed { get; set; } = 1;

    public bool Loop { get; set; }
    public bool IsPlaying { get; private set; }

    public void SetAnimation(WidgetAnim? animation)
    {
        Animation = animation;
        Time = animation?.StartSeconds ?? 0;
        IsPlaying = false;
    }

    public void Play()
    {
        if (Animation == null)
            return;
        if (Time >= Animation.EndSeconds)
            Time = Animation.StartSeconds;
        IsPlaying = true;
    }

    public void Pause() => IsPlaying = false;

    public void Stop()
    {
        IsPlaying = false;
        Time = Animation?.StartSeconds ?? 0;
    }

    /// <summary>Moves the clock by <paramref name="elapsedSeconds"/> of real time; true when the time changed.</summary>
    public bool Advance(double elapsedSeconds)
    {
        if (!IsPlaying || Animation == null || elapsedSeconds <= 0)
            return false;

        Time += elapsedSeconds * Speed;
        double start = Animation.StartSeconds, end = Animation.EndSeconds;
        if (Time >= end)
        {
            if (Loop && end > start)
            {
                Time = start + (Time - start) % (end - start);
            }
            else
            {
                Time = end;
                IsPlaying = false;
            }
        }
        return true;
    }

    /// <summary>What the animation sets on the widgets of the tree at the current time (null when no animation is selected).</summary>
    public Dictionary<WidgetNode, WidgetAnimOverride>? State(WidgetNode root) =>
        Animation == null ? null : AnimationOverlay.Bind(root, AnimationOverlay.Evaluate(Animation, Time), Animation);
}
