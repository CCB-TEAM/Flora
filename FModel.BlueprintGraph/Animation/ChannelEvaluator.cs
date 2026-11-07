using System;

namespace FModel.BlueprintGraph.Animation;

/// <summary>
/// Evaluates a float channel the way UE 5.6 does at run time. A port of tools/anim-analysis/curve_eval.py, which was written from:
/// MovieScene/Private/Channels/MovieSceneCurveChannelImpl.cpp (segment choice, extrapolation),
/// MovieScene/Private/Channels/MovieSceneInterpolation.cpp (FCubicBezierInterpolation, FWeightedCubicInterpolation) and
/// Engine/Public/Curves/CurveEvaluation.h (BezierInterp).
/// Times are ticks, tangents are value per tick. Stored tangents are used as they are: the run time never recomputes them.
/// Not compared with the engine's own output yet (test T-eval in docs/flora-timeline-studio-design.md).
/// </summary>
public static class ChannelEvaluator
{
    /// <summary>Sequencer.LinearCubicInterpolation (default 1): a linear key followed by a cubic key interpolates as cubic.</summary>
    private const bool LinearCubicInterpolation = true;

    /// <summary>
    /// The channel value at <paramref name="tick"/>, or null when the channel gives no value there
    /// (no keys and no default value, or extrapolation None outside the keys).
    /// </summary>
    public static double? Evaluate(AnimChannel channel, double tick)
    {
        var times = channel.Times;
        var keys = channel.Keys;
        var n = times.Length;
        if (n == 0)
            return channel.HasDefaultValue ? channel.DefaultValue : null;
        if (n == 1)
            return keys[0].Value;

        if (tick < times[0])
            return Extrapolate(channel, tick, before: true);
        if (tick > times[n - 1])
            return Extrapolate(channel, tick, before: false);
        if (tick == times[n - 1])
            return keys[n - 1].Value;

        // the segment [times[lo], times[lo + 1])
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (times[mid] <= tick)
                lo = mid;
            else
                hi = mid;
        }
        return EvalSegment(channel, lo, tick);
    }

    /// <summary>Evaluates at a time in seconds.</summary>
    public static double? EvaluateSeconds(AnimChannel channel, double seconds) => Evaluate(channel, seconds * channel.TicksPerSecond);

    private static double? Extrapolate(AnimChannel ch, double tick, bool before)
    {
        var mode = before ? ch.PreExtrapolation : ch.PostExtrapolation;
        var n = ch.Times.Length;
        var key = before ? ch.Keys[0] : ch.Keys[n - 1];
        switch (mode)
        {
            case ChannelExtrapolation.None:
                return null;

            case ChannelExtrapolation.Linear:
            {
                double origin = before ? ch.Times[0] : ch.Times[n - 1];
                double slope;
                if (key.Interp == KeyInterp.Constant)
                {
                    slope = 0;
                }
                else if (key.Interp == KeyInterp.Cubic)
                {
                    slope = before ? key.ArriveTangent : key.LeaveTangent;
                }
                else
                {
                    // linear key: the slope of the neighbouring segment
                    var (a, b) = before ? (0, 1) : (n - 2, n - 1);
                    double dx = ch.Times[b] - ch.Times[a];
                    slope = dx == 0 ? 0 : (ch.Keys[b].Value - ch.Keys[a].Value) / dx;
                }
                return key.Value + slope * (tick - origin);
            }

            default:
                // Constant. The cycle modes are not used by any game animation (all 2152 channels are Constant) and evaluate like Constant here.
                return key.Value;
        }
    }

    /// <summary>The value between key <paramref name="i"/> and key <paramref name="i"/> + 1 (GetInterpolationForKey).</summary>
    private static double EvalSegment(AnimChannel ch, int i, double tick)
    {
        var k1 = ch.Keys[i];
        var k2 = ch.Keys[i + 1];
        double t1 = ch.Times[i];
        var dx = ch.Times[i + 1] - t1;

        var interp = k1.Interp;
        if (interp == KeyInterp.Linear && LinearCubicInterpolation && k2.Interp == KeyInterp.Cubic)
            interp = KeyInterp.Cubic;

        switch (interp)
        {
            case KeyInterp.Cubic:
            {
                if (k1.Value == k2.Value && k1.LeaveTangent == 0 && k2.ArriveTangent == 0 && k1.LeaveWeight == 0 && k2.ArriveWeight == 0)
                    return k1.Value;
                if (k1.WeightMode is KeyWeightMode.Both or KeyWeightMode.Leave || k2.WeightMode is KeyWeightMode.Both or KeyWeightMode.Arrive)
                    return EvalWeighted(ch, i, tick);

                // FCubicBezierInterpolation: the interval is in ticks, the tangents in value per tick
                double p0 = k1.Value;
                var p1 = p0 + k1.LeaveTangent * dx / 3.0;
                double p3 = k2.Value;
                var p2 = p3 - k2.ArriveTangent * dx / 3.0;
                return Bezier(p0, p1, p2, p3, (tick - t1) / dx);
            }

            case KeyInterp.Linear:
            {
                var dy = (double) k2.Value - k1.Value;
                return dy == 0 ? k1.Value : k1.Value + dy / dx * (tick - t1);
            }

            default:
                // constant: holds the key value until the next key
                return k1.Value;
        }
    }

    /// <summary>UE::Curves::BezierInterp: de Casteljau with a float alpha.</summary>
    private static double Bezier(double p0, double p1, double p2, double p3, double alpha)
    {
        double a = (float) alpha;
        var p01 = p0 + a * (p1 - p0);
        var p12 = p1 + a * (p2 - p1);
        var p23 = p2 + a * (p3 - p2);
        var p012 = p01 + a * (p12 - p01);
        var p123 = p12 + a * (p23 - p12);
        return p012 + a * (p123 - p012);
    }

    /// <summary>FWeightedCubicInterpolation: control points in the (seconds, value) plane, parameter found from the x curve.</summary>
    private static double EvalWeighted(AnimChannel ch, int i, double tick)
    {
        var k1 = ch.Keys[i];
        var k2 = ch.Keys[i + 1];
        var interval = 1.0 / ch.TicksPerSecond;
        double toSeconds = (float) (1.0 / (float) interval);
        var time1 = ch.Times[i] / ch.TicksPerSecond;
        var time2 = ch.Times[i + 1] / ch.TicksPerSecond;
        var dxs = time2 - time1;
        double dx = ch.Times[i + 1] - ch.Times[i];

        var startWeighted = k1.WeightMode is KeyWeightMode.Both or KeyWeightMode.Leave;
        var endWeighted = k2.WeightMode is KeyWeightMode.Both or KeyWeightMode.Arrive;

        var angle = Math.Atan(k1.LeaveTangent * toSeconds);
        double startWeight;
        if (startWeighted)
        {
            startWeight = k1.LeaveWeight;
        }
        else
        {
            var dy = k1.LeaveTangent / interval * dxs;
            startWeight = Math.Sqrt(dxs * dxs + dy * dy) / 3.0;
        }
        var startTanX = Math.Cos(angle) * startWeight + time1;
        var startTanY = Math.Sin(angle) * startWeight + k1.Value;
        var normalizedStart = (startTanX - time1) / dxs;

        angle = Math.Atan(k2.ArriveTangent * toSeconds);
        double endWeight;
        if (endWeighted)
        {
            endWeight = k2.ArriveWeight;
        }
        else
        {
            var dy = k2.ArriveTangent / interval * dxs;
            endWeight = Math.Sqrt(dxs * dxs + dy * dy) / 3.0;
        }
        var endTanX = -Math.Cos(angle) * endWeight + time2;
        var endTanY = -Math.Sin(angle) * endWeight + k2.Value;
        var normalizedEnd = (endTanX - time1) / dxs;

        var interp = (tick - ch.Times[i]) / dx;
        var u = SolveCubicUnit(normalizedStart, normalizedEnd, interp);
        return Bezier(k1.Value, startTanY, endTanY, k2.Value, u);
    }

    /// <summary>
    /// Solves 3(1-u)^2 u a + 3(1-u) u^2 b + u^3 = x for u in [0, 1] and returns the largest root, or 0 when there is none.
    /// The engine solves the cubic in closed form (MovieSceneInterpolation.cpp:918); a scan with bisection gives the same root.
    /// </summary>
    private static double SolveCubicUnit(double a, double b, double x)
    {
        double F(double u) => 3 * (1 - u) * (1 - u) * u * a + 3 * (1 - u) * u * u * b + u * u * u - x;

        const int steps = 512;
        double? best = null;
        double prevU = 0, prevF = F(0);
        if (Math.Abs(prevF) < 1e-12)
            best = 0.0;
        for (var i = 1; i <= steps; i++)
        {
            var u = (double) i / steps;
            var f = F(u);
            if (Math.Abs(f) < 1e-12)
            {
                best = u;
            }
            else if (prevF * f < 0)
            {
                double lo = prevU, hi = u;
                for (var j = 0; j < 60; j++)
                {
                    var mid = 0.5 * (lo + hi);
                    if (F(lo) < 0 == F(mid) < 0)
                        lo = mid;
                    else
                        hi = mid;
                }
                best = 0.5 * (lo + hi);
            }
            prevU = u;
            prevF = f;
        }
        return best ?? 0.0;
    }
}
