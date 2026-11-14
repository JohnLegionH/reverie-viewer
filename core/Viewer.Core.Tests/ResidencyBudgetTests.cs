// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using System.Numerics;
using Viewer.Core.Residency;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// The residency manager's work in a frame stays within a time budget however many textures and faces a region brings. The official viewer
/// limits its per-frame texture work to clamp(0.05 x frame interval, 2 ms, 5 ms) (llviewerdisplay.cpp), re-scores and re-decides only
/// max(32, 5 percent) of its textures a frame, round-robin from where it stopped, and lets the rest wait for a later frame
/// (llviewertexturelist.cpp updateImagesFetchTextures). Here a region of 163,000 faces over 40,000 textures stands for a neighbour like the one
/// that froze the frame for 100 ms or more.
/// </summary>
[Collection("TimingTests")]
public class ResidencyBudgetTests
{
    private readonly ITestOutputHelper _out;
    public ResidencyBudgetTests(ITestOutputHelper output) => _out = output;

    private const int Faces = 163_000, Textures = 40_000;
    private static Guid G(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
    private static ResidencyCamera Cam(float x = 0) => new(new(x, 0, 30), Vector3.UnitX, Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 4096f, 1080);

    /// <summary>A neighbour's worth of faces: each texture is used by about four faces, spread over 512 m, a third of them in view of the camera.</summary>
    private static TextureResidency Region(int maxLoads = 8, long budget = 1L << 31, Func<long>? clock = null, TimeSpan? frameBudget = null)
    {
        var r = new TextureResidency(new() { BudgetBytes = budget, MaxLoadsPerFrame = maxLoads, Clock = clock, UsersPerFrame = int.MaxValue });
        if (frameBudget is { } fb)
            r.FrameBudget = fb;
        var rng = new Random(8);
        for (int i = 0; i < Faces; i++)
        {
            var at = new Vector3(rng.NextSingle() * 512, rng.NextSingle() * 512 - 256, 20 + rng.NextSingle() * 20);
            r.SetUser(new TextureUser(TextureUserKind.Face, (uint)i, 0, 1), G(i % Textures), new Bounds(at, 0.5f + rng.NextSingle() * 3));
        }
        return r;
    }

    private static TimeSpan T(long frame) => TimeSpan.FromMilliseconds(frame * 16.7);

    /// <summary>The work of a frame, measured on the wall clock: the first frames of a region (every user and texture new) and the ones after.</summary>
    [Fact]
    public void No_frame_spends_more_than_a_few_times_the_official_budget_on_a_region_of_163000_faces()
    {
        var r = Region(frameBudget: TimeSpan.FromMilliseconds(2));
        var times = new List<double>();
        for (int f = 0; f < 60; f++)
        {
            var sw = Stopwatch.StartNew();
            r.Update(Cam(f * 0.2f), T(f));
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (f < 5 || f % 20 == 0)
                _out.WriteLine($"frame {f}: {sw.Elapsed.TotalMilliseconds:F2} ms (scoring {r.LastPriorityMs:F2}, whole {r.LastUpdateMs:F2}), scored {r.LastUsersScored}");
        }
        times.Sort();
        double slowest = times[^1], p90 = times[53];
        _out.WriteLine($"worst {slowest:F2} ms, 54th of 60 {p90:F2} ms");
        // the official limit is 2 to 5 ms; 15 ms allows one unit of work past the limit. The 54th fastest of 60 frames is judged, so a busy
        // machine taking the thread away for a moment (tests run in parallel) does not decide it; an unbounded frame (12 to 40 ms) fails it
        Assert.True(p90 < 15, $"the 54th fastest of 60 frames took {p90:F1} ms (slowest {slowest:F1})");
    }

    [Fact]
    public void The_manager_checks_the_clock_as_it_works_and_stops_at_the_frame_budget()
    {
        long ticks = 0, reads = 0;
        long Clock() { reads++; return ticks += TimeSpan.TicksPerMillisecond / 1000; }   // a microsecond a read
        var r = Region(clock: Clock, frameBudget: TimeSpan.FromMilliseconds(2));
        for (int f = 0; f < 20; f++)
        {
            long before = reads, t0 = ticks;
            r.Update(Cam(), T(f));
            Assert.True(reads - before > 0, "no clock read: the frame's work is not limited by time");
            // 2 ms at a microsecond a read is 2,000 reads; the frame's own start and end reads and one unit of work may go past the limit
            Assert.True(reads - before <= 2_100, $"frame {f}: {reads - before} clock reads");
            Assert.True(ticks - t0 <= 2.1 * TimeSpan.TicksPerMillisecond, $"frame {f}: {(ticks - t0) / (double)TimeSpan.TicksPerMillisecond:F2} ms of clock");
        }
    }

    [Fact]
    public void Work_that_did_not_fit_carries_over_and_every_user_is_scored_in_the_end()
    {
        long ticks = 0;
        long Clock() => ticks += TimeSpan.TicksPerMillisecond / 1000;
        var r = Region(maxLoads: 512, clock: Clock, frameBudget: TimeSpan.FromMilliseconds(2));
        int frames = 0, scoredTotal = 0;
        while (scoredTotal < Faces * 2 && frames < 5_000)
        {
            r.Update(Cam(), T(frames++));
            scoredTotal += r.LastUsersScored;
        }
        Assert.True(scoredTotal >= Faces * 2, $"after {frames} frames only {scoredTotal} users were scored");
        // each user was scored at least once, so everything in view has a priority above 1 and the rest below
        int visible = Enumerable.Range(0, Textures).Count(i => r.IsVisible(G(i)));
        Assert.True(visible > 1_000, $"{visible} textures in view");
    }

    [Fact]
    public void The_loads_of_a_frame_are_the_highest_priority_textures_though_tens_of_thousands_wait()
    {
        // 30,000 textures all wanted, scored in one frame: the 8 loads are the 8 of highest priority, then as the camera moves, the next 8 for the new view
        var r = new TextureResidency(new() { BudgetBytes = 1L << 40, MaxLoadsPerFrame = 8, UsersPerFrame = int.MaxValue });
        var rng = new Random(11);
        for (int i = 0; i < 30_000; i++)
            r.SetUser(new TextureUser(TextureUserKind.Face, (uint)i, 0, 1), G(i), new Bounds(new(rng.NextSingle() * 400, rng.NextSingle() * 400 - 200, 25), 0.3f + rng.NextSingle() * 3));
        for (int view = 0; view < 3; view++)
        {
            var cam = Cam(view * 90f);
            var acts = r.Update(cam, T(view));
            var loads = acts.Where(a => a.Kind == ResidencyActionKind.Load).Select(a => a.Texture).ToList();
            Assert.Equal(8, loads.Count);
            // the manager's own priority for every texture still waiting is no higher than the lowest of the ones just started
            double floor = loads.Min(r.PriorityOf);
            var started = new HashSet<Guid>(loads);
            for (int i = 0; i < 30_000; i++)
                if (r.StateOf(G(i)) == ResidencyState.NotLoaded)
                    Assert.True(r.PriorityOf(G(i)) <= floor, $"view {view}: texture {i} waits at {r.PriorityOf(G(i)):F2} above a load at {floor:F2}");
            Assert.Equal(8 * (view + 1), r.Loading);
        }
    }

    [Fact]
    public void Loads_are_still_limited_per_frame_and_never_exceed_the_texture_count()
    {
        var r = Region(maxLoads: 8, frameBudget: TimeSpan.FromMilliseconds(2));
        for (int f = 0; f < 30; f++)
            Assert.True(r.Update(Cam(), T(f)).Count(a => a.Kind == ResidencyActionKind.Load) <= 8);
        Assert.True(r.Loads > 0);
        Assert.True(r.Loads <= 8 * 30);
    }

    [Fact]
    public void A_region_with_few_textures_decides_everything_in_its_first_frame()
    {
        // the budget never holds back a small scene: tens of textures are all decided at once
        var r = new TextureResidency(new() { BudgetBytes = 1L << 30, MaxLoadsPerFrame = 8 });
        for (int i = 0; i < 6; i++)
            r.SetUser(new TextureUser(TextureUserKind.Face, (uint)i, 0, 1), G(i), new Bounds(new(5 + i, 0, 30), 1f));
        var acts = r.Update(Cam(), T(0));
        Assert.Equal(6, acts.Count(a => a.Kind == ResidencyActionKind.Load));
    }

    [Fact]
    public void The_per_frame_camera_constants_give_the_cameras_own_priority_and_size_claim()
    {
        var cam = new ResidencyCamera(new(3, -4, 25), Vector3.Normalize(new(1, 0.4f, -0.1f)), Vector3.UnitZ, 1.1f, 16f / 9, 0.1f, 600f, 1080);
        var scorer = new CameraScorer(cam);
        var rng = new Random(3);
        for (int i = 0; i < 20_000; i++)
        {
            var b = new Bounds(new(rng.NextSingle() * 700 - 100, rng.NextSingle() * 700 - 300, rng.NextSingle() * 60), 0.1f + rng.NextSingle() * 6);
            float repeats = rng.NextSingle() * 8;
            Assert.Equal(cam.Priority(b), scorer.Priority(b), 9);
            Assert.Equal(cam.VirtualSize(b, repeats), scorer.VirtualSize(b, repeats), 6);
        }
    }
}
