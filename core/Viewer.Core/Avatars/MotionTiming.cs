// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Avatars;

/// <summary>
/// When one motion started and stopped, and so what weight it has at a time, as the official viewer's motion controller works it out
/// (llcharacter/llmotioncontroller.cpp updateMotionsByType): ease in is cubic_step over the clip's ease-in duration from the start; a stopped motion
/// eases out by cubic_step over its ease-out duration from the weight it had when it stopped (the residual weight); in between the weight is one. A
/// clip that does not loop stops itself so that its ease out ends with its duration (activateMotionInstance). Pure: the weight is a function of time.
/// </summary>
public readonly record struct MotionTiming(double Activation, double EaseIn, double EaseOut, double? Stop, double ResidualStop)
{
    /// <summary>cubic_step (llmath.h): 0 to 1 over 0 to 1, with zero slope at both ends.</summary>
    public static double CubicStep(double x)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        return x * x * (3.0 - 2.0 * x);
    }

    /// <summary>The timing of a motion of <paramref name="clip"/> started at <paramref name="now"/>.</summary>
    public static MotionTiming Start(AnimationClip clip, double now)
    {
        var t = new MotionTiming(now, Math.Max(clip.EaseIn, 0), Math.Max(clip.EaseOut, 0), null, 0);
        if (clip.Duration > 0 && !clip.Loop)
        {
            double stop = now + Math.Max(clip.Duration - t.EaseOut, 0);
            return t with { Stop = stop, ResidualStop = t.Weight(stop) };
        }
        return t;
    }

    /// <summary>This timing with the motion stopped at <paramref name="now"/> (no change when it is already stopped by then).</summary>
    public MotionTiming StoppedAt(double now) => Stop is { } s && s <= now ? this : this with { Stop = now, ResidualStop = EasedWeight(now) };

    /// <summary>Whether the motion has stopped and finished easing out at <paramref name="now"/>.</summary>
    public bool Finished(double now) => Stop is { } s && now > s + EaseOut;

    /// <summary>The motion's weight at <paramref name="t"/>.</summary>
    public double Weight(double t)
    {
        if (Stop is { } s && t > s) return EaseOut <= 0 ? 0.0 : ResidualStop * CubicStep(1.0 - (t - s) / EaseOut);
        return EasedWeight(t);
    }

    /// <summary>The weight a motion that has not stopped has: nothing before it starts, then eased in.</summary>
    private double EasedWeight(double t)
    {
        if (t < Activation) return 0.0;
        if (EaseIn <= 0 || t >= Activation + EaseIn) return 1.0;
        return CubicStep((t - Activation) / EaseIn);
    }
}
