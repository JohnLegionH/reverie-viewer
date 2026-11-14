// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The frame's shared time allowance and the order avatars are posed in when they do not all fit it. A fake clock throughout; nothing waits.</summary>
public class FrameBudgetTests
{
    // a clock of 1000 ticks a second: one tick is one millisecond
    private sealed class Clock
    {
        public long Ticks;
        public long Now() => Ticks;
    }

    private static FrameBudget Budget(Clock clock, double totalMs = 4.0) => new(totalMs, clock.Now, 1000);

    [Fact]
    public void Remaining_counts_down_from_the_whole_and_starts_again_each_frame()
    {
        var clock = new Clock();
        var budget = Budget(clock);
        budget.Start();
        Assert.Equal(4.0, budget.RemainingMs, 6);
        clock.Ticks += 1;
        Assert.Equal(3.0, budget.RemainingMs, 6);
        Assert.Equal(1.0, budget.SpentMs, 6);
        clock.Ticks += 10;
        Assert.Equal(0.0, budget.RemainingMs);   // never negative
        budget.Start();
        Assert.Equal(4.0, budget.RemainingMs, 6);
    }

    [Fact]
    public void A_consumer_that_leaves_room_for_the_next_asks_for_a_share_of_what_is_left()
    {
        var clock = new Clock();
        var budget = Budget(clock);
        budget.Start();
        Assert.Equal(2.0, budget.AllowanceMs(0.5), 6);
        clock.Ticks += 1;   // something before it took a millisecond
        Assert.Equal(1.0, budget.AllowanceMs(0.5), 6);
        clock.Ticks += 5;
        Assert.Equal(0.0, budget.AllowanceMs(0.5));
    }

    [Fact]
    public void Two_consumers_in_turn_never_take_more_than_the_whole()
    {
        var clock = new Clock();
        var budget = Budget(clock);
        budget.Start();
        // the first takes its whole allowance (half), the second all that is left: together the whole, not twice it
        double first = budget.AllowanceMs(0.5);
        clock.Ticks += (long)first;
        double second = budget.RemainingMs;
        Assert.Equal(budget.TotalMs, first + second, 6);
    }

    [Fact]
    public void Plan_puts_an_avatar_never_posed_first_then_the_one_posed_longest_ago()
    {
        var sweep = new PoseSweep();
        long[] last = [10, 7, PoseSweep.Never, 9, 7];
        var order = sweep.Plan(last).ToArray();
        Assert.Equal([2, 1, 4, 3, 0], order);   // never; then frame 7 (ties by index); then 9; then 10
    }

    [Fact]
    public void May_continue_while_time_is_left_and_always_for_the_least_number()
    {
        Assert.True(PoseSweep.MayContinue(posedSoFar: 0, spentMs: 99, budgetMs: 1, atLeast: 1));    // one is always posed
        Assert.False(PoseSweep.MayContinue(posedSoFar: 1, spentMs: 99, budgetMs: 1, atLeast: 1));
        Assert.True(PoseSweep.MayContinue(posedSoFar: 5, spentMs: 0.9, budgetMs: 1, atLeast: 1));
        Assert.False(PoseSweep.MayContinue(posedSoFar: 5, spentMs: 1.0, budgetMs: 1, atLeast: 1));
    }

    // frames of a crowd of n avatars, each pose costing `cost` ms and `budget` ms a frame: returns for each avatar the longest run of
    // frames it went without a pose (after its first)
    private static (int[] LongestGap, int[] Posed) Simulate(int n, double cost, double budget, int atLeast, int frames, int[]? arrivalFrame = null)
    {
        var sweep = new PoseSweep();
        var last = Enumerable.Repeat(PoseSweep.Never, n).ToArray();
        var gap = new int[n];
        var worst = new int[n];
        var posed = new int[n];
        for (int f = 0; f < frames; f++)
        {
            var here = Enumerable.Range(0, n).Where(i => arrivalFrame is null || arrivalFrame[i] <= f).ToArray();
            var stamps = here.Select(i => last[i]).ToArray();
            var plan = sweep.Plan(stamps);
            double spent = 0;
            int done = 0;
            var chosen = new HashSet<int>();
            foreach (var k in plan)
            {
                if (!PoseSweep.MayContinue(done, spent, budget, atLeast)) break;
                int i = here[k];
                last[i] = f;
                spent += cost;
                done++;
                chosen.Add(i);
                posed[i]++;
            }
            foreach (var i in here)
            {
                if (chosen.Contains(i)) gap[i] = 0;
                else if (last[i] != PoseSweep.Never) worst[i] = Math.Max(worst[i], ++gap[i]);
            }
        }
        return (worst, posed);
    }

    [Fact]
    public void With_room_for_a_fifth_of_the_crowd_every_avatar_is_posed_at_least_every_fifth_frame()
    {
        var (gap, posed) = Simulate(n: 100, cost: 0.1, budget: 2.0, atLeast: 1, frames: 60);   // 20 of 100 a frame
        Assert.All(gap, g => Assert.True(g <= 4, $"an avatar went {g} frames without a pose"));
        Assert.All(posed, p => Assert.True(p >= 12, $"an avatar was posed only {p} times in 60 frames"));
    }

    [Fact]
    public void With_room_for_all_every_avatar_is_posed_every_frame()
    {
        var (gap, posed) = Simulate(n: 10, cost: 0.1, budget: 4.0, atLeast: 1, frames: 20);
        Assert.All(gap, g => Assert.Equal(0, g));
        Assert.All(posed, p => Assert.Equal(20, p));
    }

    [Fact]
    public void A_budget_smaller_than_one_pose_still_poses_the_least_number_each_frame()
    {
        var (gap, posed) = Simulate(n: 4, cost: 5.0, budget: 1.0, atLeast: 1, frames: 40);
        Assert.Equal(40, posed.Sum());   // one a frame
        Assert.All(gap, g => Assert.True(g <= 3));
    }

    [Fact]
    public void A_scene_of_a_few_avatars_is_posed_whole_in_every_frame_whatever_the_budget()
    {
        // the layer poses at least six a frame (AvatarLayer.MinPosedPerFrame): four avatars with no time left are all posed every frame
        var (gap, posed) = Simulate(n: 4, cost: 5.0, budget: 0.0, atLeast: 6, frames: 10);
        Assert.All(gap, g => Assert.Equal(0, g));
        Assert.All(posed, p => Assert.Equal(10, p));
    }

    [Fact]
    public void An_avatar_arriving_into_a_busy_crowd_is_posed_in_its_first_frame()
    {
        var arrival = Enumerable.Range(0, 51).Select(i => i == 50 ? 30 : 0).ToArray();
        var (_, posed) = Simulate(n: 51, cost: 0.1, budget: 0.5, atLeast: 1, frames: 31, arrivalFrame: arrival);   // 5 of 51 a frame
        Assert.Equal(1, posed[50]);   // posed at frame 30, the frame it arrived
    }

    [Fact]
    public void Packing_a_matrix_for_the_engine_matches_the_axis_change_by_multiplication()
    {
        var rng = new Random(8);
        var toEngine = new System.Numerics.Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        var fromEngine = System.Numerics.Matrix4x4.Transpose(toEngine);
        for (int round = 0; round < 200; round++)
        {
            var m = new System.Numerics.Matrix4x4();
            float[] v = new float[16];
            for (int i = 0; i < 16; i++) v[i] = (float)(rng.NextDouble() * 20 - 10);
            m = new System.Numerics.Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
            var e = fromEngine * m * toEngine;
            float[] want = [e.M11, e.M21, e.M31, e.M41, e.M12, e.M22, e.M32, e.M42, e.M13, e.M23, e.M33, e.M43];
            var got = new float[SkinTables.FloatsPerMatrix];
            SkinTables.PackMatrix(m, got);
            Assert.Equal(want, got);
        }
    }
}
