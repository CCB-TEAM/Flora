using System;

namespace FModel.BlueprintGraph.Animation;

/// <summary>How edits to key values, times, or interpolation update automatic tangents.</summary>
public enum TangentUpdateMode
{
    Local,
    Channel,
    None,
}

/// <summary>
/// Applies transactional edits to an in-memory <see cref="AnimChannel"/>.
/// This class does not read or write Unreal assets; callers own serialization and range/signature updates.
/// </summary>
public static class AnimationChannelEditor
{
    private const double KindaSmallNumber = 1e-4;
    private const double TwoThirds = 2.0 / 3.0;

    /// <summary>
    /// Changes one key's value, then recomputes mode-2 automatic tangents locally for that key and its neighbors.
    /// User and break tangents outside that automatic set are preserved.
    /// </summary>
    /// <param name="channel">Channel to edit.</param>
    /// <param name="keyIndex">Zero-based key index.</param>
    /// <param name="value">New finite key value.</param>
    public static void SetKey(AnimChannel channel, int keyIndex, float value, TangentUpdateMode tangentUpdate = TangentUpdateMode.Local)
    {
        ValidateChannel(channel);
        ValidateIndex(channel, keyIndex);
        ValidateEnum(tangentUpdate, nameof(tangentUpdate));
        ValidateFinite(value, nameof(value));

        var keys = (AnimKey[]) channel.Keys.Clone();
        keys[keyIndex] = keys[keyIndex] with { Value = value };
        UpdateTangents(channel.Times, keys, keyIndex, tangentUpdate);
        ValidateKeys(keys);

        channel.Keys = keys;
    }

    /// <summary>
    /// Moves one key without crossing its neighbors. Equal times are rejected so keys stay strictly ordered.
    /// Mode-2 automatic tangents are recomputed locally for the moved key and its neighbors.
    /// </summary>
    /// <param name="channel">Channel to edit.</param>
    /// <param name="keyIndex">Zero-based key index.</param>
    /// <param name="newTimeTicks">New key time in the channel's tick resolution.</param>
    public static void MoveKey(AnimChannel channel, int keyIndex, int newTimeTicks, TangentUpdateMode tangentUpdate = TangentUpdateMode.Local)
    {
        ValidateChannel(channel);
        ValidateIndex(channel, keyIndex);
        ValidateEnum(tangentUpdate, nameof(tangentUpdate));

        if (keyIndex > 0 && newTimeTicks <= channel.Times[keyIndex - 1])
            throw new ArgumentOutOfRangeException(nameof(newTimeTicks), "The moved key must remain after its previous key.");
        if (keyIndex + 1 < channel.Times.Length && newTimeTicks >= channel.Times[keyIndex + 1])
            throw new ArgumentOutOfRangeException(nameof(newTimeTicks), "The moved key must remain before its next key.");
        if (newTimeTicks == channel.Times[keyIndex])
            return;

        var times = (int[]) channel.Times.Clone();
        times[keyIndex] = newTimeTicks;
        var keys = (AnimKey[]) channel.Keys.Clone();
        UpdateTangents(times, keys, keyIndex, tangentUpdate);
        ValidateTimes(times);
        ValidateKeys(keys);

        channel.Times = times;
        channel.Keys = keys;
    }

    /// <summary>
    /// Sets the interpolation and tangent mode on one key, then recomputes mode-2 automatic tangents locally.
    /// Cubic SmartAuto tangents are not implemented and cause the edit to fail without mutation when they are in
    /// the affected neighborhood.
    /// </summary>
    /// <param name="channel">Channel to edit.</param>
    /// <param name="keyIndex">Zero-based key index.</param>
    /// <param name="interpolation">Interpolation mode for the segment starting at this key.</param>
    /// <param name="tangentMode">Tangent mode stored on this key.</param>
    public static void SetInterp(
        AnimChannel channel,
        int keyIndex,
        KeyInterp interpolation,
        KeyTangentMode tangentMode,
        TangentUpdateMode tangentUpdate = TangentUpdateMode.Local)
    {
        ValidateChannel(channel);
        ValidateIndex(channel, keyIndex);
        ValidateEnum(interpolation, nameof(interpolation));
        ValidateEnum(tangentMode, nameof(tangentMode));
        ValidateEnum(tangentUpdate, nameof(tangentUpdate));

        var keys = (AnimKey[]) channel.Keys.Clone();
        keys[keyIndex] = keys[keyIndex] with { Interp = interpolation, TangentMode = tangentMode };
        UpdateTangents(channel.Times, keys, keyIndex, tangentUpdate);
        ValidateKeys(keys);

        channel.Keys = keys;
    }

    /// <summary>
    /// Sets explicit user/break tangents. Inputs are slopes in value per second and are converted to the
    /// channel's stored value-per-tick units. Weights use the channel model's stored units.
    /// </summary>
    /// <param name="channel">Channel to edit.</param>
    /// <param name="keyIndex">Zero-based key index.</param>
    /// <param name="arriveValuePerSecond">Arrive slope in value per second.</param>
    /// <param name="leaveValuePerSecond">Leave slope in value per second.</param>
    /// <param name="tangentMode">Explicit tangent mode; only User or Break is accepted.</param>
    /// <param name="weightMode">Stored tangent weight mode.</param>
    /// <param name="arriveWeight">Arrive tangent weight in the model's stored units.</param>
    /// <param name="leaveWeight">Leave tangent weight in the model's stored units.</param>
    public static void SetTangent(
        AnimChannel channel,
        int keyIndex,
        double arriveValuePerSecond,
        double leaveValuePerSecond,
        KeyTangentMode tangentMode = KeyTangentMode.Break,
        KeyWeightMode weightMode = KeyWeightMode.None,
        double arriveWeight = 0,
        double leaveWeight = 0)
    {
        ValidateChannel(channel);
        ValidateIndex(channel, keyIndex);
        ValidateEnum(tangentMode, nameof(tangentMode));
        ValidateEnum(weightMode, nameof(weightMode));
        if (tangentMode is not (KeyTangentMode.User or KeyTangentMode.Break))
            throw new ArgumentOutOfRangeException(nameof(tangentMode), "Explicit tangents require User or Break mode.");
        ValidateFinite(arriveValuePerSecond, nameof(arriveValuePerSecond));
        ValidateFinite(leaveValuePerSecond, nameof(leaveValuePerSecond));
        ValidateFinite(arriveWeight, nameof(arriveWeight));
        ValidateFinite(leaveWeight, nameof(leaveWeight));
        if (arriveWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(arriveWeight), "Tangent weights must be non-negative.");
        if (leaveWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(leaveWeight), "Tangent weights must be non-negative.");

        var arrivePerTick = ToFiniteSingle(arriveValuePerSecond / channel.TicksPerSecond, nameof(arriveValuePerSecond));
        var leavePerTick = ToFiniteSingle(leaveValuePerSecond / channel.TicksPerSecond, nameof(leaveValuePerSecond));
        var arriveWeightValue = ToFiniteSingle(arriveWeight, nameof(arriveWeight));
        var leaveWeightValue = ToFiniteSingle(leaveWeight, nameof(leaveWeight));

        var keys = (AnimKey[]) channel.Keys.Clone();
        keys[keyIndex] = keys[keyIndex] with
        {
            ArriveTangent = arrivePerTick,
            LeaveTangent = leavePerTick,
            ArriveWeight = arriveWeightValue,
            LeaveWeight = leaveWeightValue,
            WeightMode = weightMode,
            TangentMode = tangentMode,
        };
        ValidateKeys(keys);

        channel.Keys = keys;
    }

    /// <summary>
    /// Applies the affine time mapping <c>newTime = oldTime * timeFactor + timeOffsetTicks</c>.
    /// Times are rounded to the nearest tick, with midpoint ties away from zero. Unweighted slopes are divided
    /// by the positive scale factor so the curve shape is preserved; weighted channels reject non-identity scaling.
    /// This method changes channel times only and leaves animation/section ranges and signatures to the caller.
    /// </summary>
    /// <param name="channel">Channel to edit.</param>
    /// <param name="timeFactor">Finite positive time scale.</param>
    /// <param name="timeOffsetTicks">Integer offset in the channel's tick resolution.</param>
    public static void Retime(AnimChannel channel, double timeFactor, int timeOffsetTicks = 0)
    {
        ValidateChannel(channel);
        ValidateFinite(timeFactor, nameof(timeFactor));
        if (timeFactor <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeFactor), "The time factor must be positive to preserve key order.");

        var scalesTime = timeFactor != 1.0;
        if (scalesTime && Array.Exists(channel.Keys, key => key.WeightMode != KeyWeightMode.None))
            throw new NotSupportedException("Non-identity time scaling of weighted tangent channels is not supported.");

        var times = new int[channel.Times.Length];
        for (var i = 0; i < channel.Times.Length; i++)
        {
            var mapped = channel.Times[i] * timeFactor + timeOffsetTicks;
            if (!double.IsFinite(mapped))
                throw new ArgumentOutOfRangeException(nameof(timeFactor), "The retimed key time is not finite.");
            var rounded = Math.Round(mapped, MidpointRounding.AwayFromZero);
            if (rounded < int.MinValue || rounded > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeFactor), "The retimed key time is outside the supported tick range.");
            times[i] = (int) rounded;
        }
        ValidateTimes(times);

        var keys = (AnimKey[]) channel.Keys.Clone();
        if (scalesTime)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = keys[i] with
                {
                    ArriveTangent = ToFiniteSingle(keys[i].ArriveTangent / timeFactor, nameof(timeFactor)),
                    LeaveTangent = ToFiniteSingle(keys[i].LeaveTangent / timeFactor, nameof(timeFactor)),
                };
            }
        }
        ValidateKeys(keys);

        channel.Times = times;
        channel.Keys = keys;
    }

    /// <summary>
    /// Recomputes UE 5.6 mode-2 automatic tangents for the indexed key and its immediate neighbors only.
    /// Linear segments receive their adjacent secant slopes; cubic Auto keys use the no-overshoot clamp.
    /// User and Break keys are left unchanged. Cubic SmartAuto keys in the affected neighborhood are rejected.
    /// </summary>
    /// <param name="channel">Channel to edit.</param>
    /// <param name="keyIndex">Zero-based index around which to recompute.</param>
    public static void AutoSetTangents(AnimChannel channel, int keyIndex)
    {
        ValidateChannel(channel);
        ValidateIndex(channel, keyIndex);

        var keys = (AnimKey[]) channel.Keys.Clone();
        RecomputeLocalTangents(channel.Times, keys, keyIndex);
        ValidateKeys(keys);

        channel.Keys = keys;
    }

    /// <summary>Converts animation timeline seconds to expected playback seconds using a positive playback speed.</summary>
    /// <param name="timelineSeconds">Non-negative finite timeline duration in seconds.</param>
    /// <param name="speed">Positive finite play rate.</param>
    /// <returns><paramref name="timelineSeconds"/> divided by <paramref name="speed"/>.</returns>
    public static double ToRuntimeDurationSeconds(double timelineSeconds, double speed)
    {
        ValidateFinite(timelineSeconds, nameof(timelineSeconds));
        if (timelineSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timelineSeconds), "Timeline duration must be non-negative.");
        ValidateFinite(speed, nameof(speed));
        if (speed <= 0)
            throw new ArgumentOutOfRangeException(nameof(speed), "Playback speed must be positive.");
        return timelineSeconds / speed;
    }

    private static void RecomputeLocalTangents(int[] times, AnimKey[] keys, int changedIndex)
    {
        var first = Math.Max(0, changedIndex - 1);
        var last = Math.Min(keys.Length - 1, changedIndex + 1);

        // Check the whole edit neighborhood before changing even the temporary key array.
        for (var i = first; i <= last; i++)
        {
            if (keys[i].Interp == KeyInterp.Cubic && keys[i].TangentMode == KeyTangentMode.SmartAuto)
                throw new NotSupportedException($"Mode-2 tangent recomputation does not support cubic SmartAuto key {i}.");
        }

        for (var i = first; i <= last; i++)
            RecomputeKeyTangent(times, keys, i);
    }

    private static void UpdateTangents(int[] times, AnimKey[] keys, int changedIndex, TangentUpdateMode mode)
    {
        switch (mode)
        {
            case TangentUpdateMode.Local:
                RecomputeLocalTangents(times, keys, changedIndex);
                break;
            case TangentUpdateMode.Channel:
                for (var i = 0; i < keys.Length; i++)
                    RecomputeLocalTangents(times, keys, i);
                break;
            case TangentUpdateMode.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static void RecomputeKeyTangent(int[] times, AnimKey[] keys, int index)
    {
        var key = keys[index];
        var lastIndex = keys.Length - 1;

        if (index == 0)
        {
            if (key.Interp == KeyInterp.Linear && lastIndex >= 1)
            {
                var dt = Math.Max(KindaSmallNumber, (double) times[1] - times[0]);
                key = key with
                {
                    LeaveTangent = ToFiniteSingle((keys[1].Value - (double) key.Value) / dt, nameof(times)),
                    WeightMode = KeyWeightMode.None,
                };
            }
            else if (key.Interp == KeyInterp.Cubic && key.TangentMode == KeyTangentMode.Auto)
            {
                key = key with { ArriveTangent = 0, LeaveTangent = 0, WeightMode = KeyWeightMode.None };
            }
            keys[index] = key;
            return;
        }

        if (index == lastIndex)
        {
            if (key.Interp == KeyInterp.Linear)
            {
                var dt = Math.Max(KindaSmallNumber, (double) times[index] - times[index - 1]);
                key = key with
                {
                    ArriveTangent = ToFiniteSingle((key.Value - (double) keys[index - 1].Value) / dt, nameof(times)),
                    WeightMode = KeyWeightMode.None,
                };
            }
            else if (key.Interp == KeyInterp.Cubic && key.TangentMode == KeyTangentMode.Auto)
            {
                key = key with { ArriveTangent = 0, LeaveTangent = 0, WeightMode = KeyWeightMode.None };
            }
            keys[index] = key;
            return;
        }

        if (key.Interp == KeyInterp.Cubic && key.TangentMode == KeyTangentMode.Auto)
        {
            var prev = keys[index - 1];
            var next = keys[index + 1];
            var prevToNext = Math.Max(KindaSmallNumber, (double) times[index + 1] - times[index - 1]);
            var a = ToFiniteSingle((double) key.Value - prev.Value, nameof(keys));
            var b = ToFiniteSingle((double) next.Value - key.Value, nameof(keys));
            var deltaSum = ToFiniteSingle((double) a + b, nameof(keys));
            var tangent = ToFiniteSingle(deltaSum / prevToNext, nameof(keys));
            var between = (prev.Value < key.Value && key.Value < next.Value)
                          || (prev.Value > key.Value && key.Value > next.Value);

            if (between)
            {
                var toPrev = Math.Max(KindaSmallNumber, (double) times[index] - times[index - 1]);
                var toNext = Math.Max(KindaSmallNumber, (double) times[index + 1] - times[index]);
                var prevSlope = (key.Value - (double) prev.Value) / (TwoThirds * toPrev);
                var nextSlope = (next.Value - (double) key.Value) / (TwoThirds * toNext);
                tangent = ToFiniteSingle(ClampTangent(tangent, prevSlope, nextSlope), nameof(keys));
            }
            else
            {
                tangent = 0;
            }

            key = key with
            {
                ArriveTangent = tangent,
                LeaveTangent = tangent,
                WeightMode = KeyWeightMode.None,
            };
            keys[index] = key;
            return;
        }

        if (key.Interp == KeyInterp.Linear)
        {
            var dtPrev = Math.Max(KindaSmallNumber, (double) times[index] - times[index - 1]);
            var dtNext = Math.Max(KindaSmallNumber, (double) times[index + 1] - times[index]);
            key = key with
            {
                ArriveTangent = ToFiniteSingle((key.Value - (double) keys[index - 1].Value) / dtPrev, nameof(keys)),
                LeaveTangent = ToFiniteSingle((keys[index + 1].Value - (double) key.Value) / dtNext, nameof(keys)),
                WeightMode = KeyWeightMode.None,
            };
            keys[index] = key;
        }
    }

    private static double ClampTangent(double tangent, double prevSlope, double nextSlope)
    {
        if (SignNoZero(prevSlope) != SignNoZero(nextSlope)
            || SignNoZero(tangent) != SignNoZero(nextSlope))
            return 0;

        return nextSlope >= 0
            ? Math.Min(Math.Min(tangent, nextSlope), prevSlope)
            : Math.Max(Math.Max(tangent, nextSlope), prevSlope);
    }

    // Mirrors MovieSceneCurveChannelImpl.cpp's sign comparison, where zero is treated as positive.
    private static int SignNoZero(double value) => value > 0 ? -1 : 1;

    private static void ValidateChannel(AnimChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.Times is null)
            throw new ArgumentException("Channel times cannot be null.", nameof(channel));
        if (channel.Keys is null)
            throw new ArgumentException("Channel keys cannot be null.", nameof(channel));
        if (!double.IsFinite(channel.TicksPerSecond) || channel.TicksPerSecond <= 0)
            throw new ArgumentException("TicksPerSecond must be finite and positive.", nameof(channel));
        if (channel.Times.Length != channel.Keys.Length)
            throw new ArgumentException("Channel Times and Keys must have the same length.", nameof(channel));

        ValidateTimes(channel.Times);
        ValidateKeys(channel.Keys);
    }

    private static void ValidateTimes(int[] times)
    {
        for (var i = 1; i < times.Length; i++)
        {
            if (times[i] <= times[i - 1])
                throw new ArgumentException("Channel times must be strictly increasing and collision-free.", nameof(times));
        }
    }

    private static void ValidateKeys(AnimKey[] keys)
    {
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            ValidateFinite(key.Value, nameof(keys));
            ValidateFinite(key.ArriveTangent, nameof(keys));
            ValidateFinite(key.LeaveTangent, nameof(keys));
            ValidateFinite(key.ArriveWeight, nameof(keys));
            ValidateFinite(key.LeaveWeight, nameof(keys));
            ValidateEnum(key.WeightMode, nameof(keys));
            ValidateEnum(key.Interp, nameof(keys));
            ValidateEnum(key.TangentMode, nameof(keys));
        }
    }

    private static void ValidateIndex(AnimChannel channel, int keyIndex)
    {
        if ((uint) keyIndex >= (uint) channel.Keys.Length)
            throw new ArgumentOutOfRangeException(nameof(keyIndex), keyIndex, "Key index is outside the channel.");
    }

    private static void ValidateFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite.");
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite.");
    }

    private static float ToFiniteSingle(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value > float.MaxValue || value < -float.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "Value cannot be represented as a finite float.");
        var converted = (float) value;
        if (!float.IsFinite(converted))
            throw new ArgumentOutOfRangeException(parameterName, "Value cannot be represented as a finite float.");
        return converted;
    }

    private static void ValidateEnum<TEnum>(TEnum value, string parameterName) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "Value is not a defined enum member.");
    }
}
