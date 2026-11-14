// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The minimum residence and the thrash window are measured in time, so the same camera moves give
/// the same residence and thrash decisions, at the same times, whatever the frame rate. The negative control drives the
/// manager with a frame-counted clock (every frame is 1/60 s, rule) and must fail the same check.
/// </summary>
public class TimeBasedTimerTests
{
    private static readonly long Each = TextureFootprint.Bytes(256, 256, true);
    private static Guid G(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
    private static readonly Guid A = G(1), B = G(2), C = G(3);

    private static ResidencyCamera Looking(Vector3 forward) =>
        new(Vector3.Zero, forward, Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 512f, 360);

    /// <summary>The camera schedule in seconds: A and B ahead (east) until 0.5 s, C (west) until 2.5 s, A and B until 5 s,
    /// C again until 6 s.</summary>
    private static ResidencyCamera CameraAt(double seconds) =>
        Looking(seconds < 0.5 || (seconds >= 2.5 && seconds < 5.0) ? Vector3.UnitX : -Vector3.UnitX);

    private sealed record Decision(ResidencyActionKind Kind, Guid Texture, double At);

    /// <summary>Runs the schedule at <paramref name="fps"/>; <paramref name="clock"/> is what the manager is told the time is
    /// at frame i (the real time for a time-based host; i/60 s for a frame-counted one). Loads upload at once.</summary>
    private static (List<Decision> Log, TextureResidency R) Run(double fps, Func<long, TimeSpan> clock)
    {
        var r = new TextureResidency(new() { BudgetBytes = 2 * Each, LowWater = 1.0, UnknownSizeEstimate = Each });
        r.SetUser(new(TextureUserKind.Face, 1, 0), A, new Bounds(new(20, 0, 0), 1));
        r.SetUser(new(TextureUserKind.Face, 2, 0), B, new Bounds(new(30, 0, 0), 1));
        r.SetUser(new(TextureUserKind.Face, 3, 0), C, new Bounds(new(-20, 0, 0), 1));
        var log = new List<Decision>();
        for (long i = 0; ; i++)
        {
            double real = TestClock.At(i, fps).TotalSeconds;
            if (real > 6.0) break;
            var now = clock(i);
            foreach (var a in r.Update(CameraAt(real), now))
            {
                log.Add(new(a.Kind, a.Texture, real));
                if (a.Kind == ResidencyActionKind.Load) r.Uploaded(a.Texture, Each, PlaceholderColour.Of(90, 90, 90, 255), now);
            }
        }
        return (log, r);
    }

    /// <summary>What must happen, in time, at any frame rate: the first loads at 0; C waits out A's and B's 1 s minimum
    /// residence and displaces B (the farther) at 1 s; B comes back at 2.5 s, 1.5 s after its eviction (inside the 2 s thrash
    /// window: thrash); C comes back at 5 s, 2.5 s after its eviction (outside: not thrash). Each decision within one frame of
    /// its time. Returns what differs (empty: as required).</summary>
    private static List<string> Check(List<Decision> log, TextureResidency r, double fps)
    {
        var expected = new (ResidencyActionKind Kind, Guid Texture, double At)[]
        {
            (ResidencyActionKind.Load, A, 0), (ResidencyActionKind.Load, B, 0),
            (ResidencyActionKind.Evict, B, 1.0), (ResidencyActionKind.Load, C, 1.0),
            (ResidencyActionKind.Evict, C, 2.5), (ResidencyActionKind.Load, B, 2.5),
            (ResidencyActionKind.Evict, B, 5.0), (ResidencyActionKind.Load, C, 5.0),
        };
        var why = new List<string>();
        if (log.Count != expected.Length) why.Add($"{fps} fps: {log.Count} decisions, expected {expected.Length}: {string.Join(", ", log.Select(d => $"{d.Kind} {d.Texture.ToString()[..8]} @{d.At:F3}"))}");
        for (int k = 0; k < Math.Min(log.Count, expected.Length); k++)
        {
            var (kind, tex, at) = expected[k];
            var d = log[k];
            if (d.Kind != kind || d.Texture != tex || d.At < at || d.At > at + 1.0 / fps + 1e-9)
                why.Add($"{fps} fps: decision {k} was {d.Kind} {d.Texture.ToString()[..8]} at {d.At:F4} s; expected {kind} {tex.ToString()[..8]} at {at} s (+1 frame)");
        }
        if (r.Thrash != 1) why.Add($"{fps} fps: thrash {r.Thrash}, expected 1");
        if (r.Evictions != 3 || r.Reloads != 2) why.Add($"{fps} fps: evictions {r.Evictions}, reloads {r.Reloads}; expected 3, 2");
        return why;
    }

    public static TheoryData<double> Rates => new() { 30, 60, 144, 1500 };   // 1500: an uncapped test run (measured 1,400-1,700)

    [Theory]
    [MemberData(nameof(Rates))]
    public void Residence_and_thrash_decisions_are_the_same_in_time_at_any_frame_rate(double fps)
    {
        var (log, r) = Run(fps, i => TestClock.At(i, fps));
        Assert.Empty(Check(log, r, fps));
    }

    [Fact]
    public void Negative_control_a_frame_counted_clock_fails_the_same_check()
    {
        // the rule: 60 frames of minimum residence, 120 of thrash window, whatever the frame rate
        var failures = new[] { 30.0, 144.0, 1500.0 }.SelectMany(fps => { var (log, r) = Run(fps, TestClock.Frames); return Check(log, r, fps); }).ToList();
        Assert.Contains(failures, f => f.StartsWith("30 fps"));
        Assert.Contains(failures, f => f.StartsWith("144 fps"));
        Assert.Contains(failures, f => f.StartsWith("1500 fps"));
        // and at 60 fps a frame-counted clock is the time-based one: the check passes (the control is not vacuous)
        var (l60, r60) = Run(60, TestClock.Frames);
        Assert.Empty(Check(l60, r60, 60));
    }

    [Fact]
    public void Defaults_keep_the_NV8a_intent_at_60_fps()
    {
        var o = new ResidencyOptions { BudgetBytes = 1 };
        Assert.Equal(TestClock.Frames(60), o.MinResidence);
        Assert.Equal(TestClock.Frames(120), o.ThrashWindow);
        Assert.Equal(TimeSpan.FromSeconds(1), o.MinResidence);
        Assert.Equal(TimeSpan.FromSeconds(2), o.ThrashWindow);
    }
}
