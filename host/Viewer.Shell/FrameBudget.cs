// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;

namespace Viewer.Shell;

/// <summary>
/// One frame's main-thread time allowance, shared by everything that does presentation work in the frame (queued changes and the
/// prim layer's steps, the avatars' arrivals, bakes and shapes, posing). Each consumer asks what is left when it starts, so the sum of
/// what they take stays near the whole instead of every consumer taking the whole. A consumer always may do one unit of work, so a
/// budget smaller than one unit cannot stall the frame.
/// </summary>
public sealed class FrameBudget
{
    private readonly Func<long> _now;
    private readonly double _tickMs;
    private long _start;

    /// <param name="totalMs">The frame's allowance in milliseconds.</param>
    /// <param name="clock">Ticks (tests pass a fake); default <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="ticksPerSecond">The clock's frequency; default <see cref="Stopwatch.Frequency"/>.</param>
    public FrameBudget(double totalMs, Func<long>? clock = null, long ticksPerSecond = 0)
    {
        TotalMs = totalMs;
        _now = clock ?? Stopwatch.GetTimestamp;
        _tickMs = 1000.0 / (ticksPerSecond > 0 ? ticksPerSecond : Stopwatch.Frequency);
        _start = _now();
    }

    public double TotalMs { get; set; }

    /// <summary>Starts a frame: nothing is spent yet.</summary>
    public void Start()
    {
        _start = _now();
        // a frame that refused a unit and started none leaves the next frame's first unit to go ahead whatever it costs, so work cannot starve
        _forceFirst = UnitsStarted == 0 && UnitsRefused > 0;
        UnitsStarted = 0;
        UnitsRefused = 0;
    }

    private bool _forceFirst;

    /// <summary>Milliseconds since <see cref="Start"/>.</summary>
    public double SpentMs => (_now() - _start) * _tickMs;

    /// <summary>What is left of the whole allowance (never negative).</summary>
    public double RemainingMs => Math.Max(0, TotalMs - SpentMs);

    /// <summary>Units of work begun this frame through <see cref="TryBeginUnit"/>, and units refused (for the report).</summary>
    public int UnitsStarted { get; private set; }
    public int UnitsRefused { get; private set; }

    /// <summary>
    /// The rule every presentation consumer asks before a unit of work that cannot be cut short (a surface handed to the engine, a body added): the unit
    /// may start when its estimated time fits what is left of the frame. Otherwise it waits for the next frame, whose first unit goes ahead whatever it
    /// costs, so a unit larger than the whole frame still runs, every other frame at the worst. A caller counts the unit as begun when this says yes.
    /// </summary>
    public bool TryBeginUnit(double estimateMs)
    {
        if (estimateMs <= RemainingMs + 1e-9 || (_forceFirst && UnitsStarted == 0))
        {
            UnitsStarted++;
            return true;
        }
        UnitsRefused++;
        return false;
    }

    /// <summary>What is left of the first <paramref name="share"/> (0 to 1) of the allowance: a consumer that must leave the rest to those
    /// after it asks for this.</summary>
    public double AllowanceMs(double share) => Math.Max(0, TotalMs * share - SpentMs);
}
