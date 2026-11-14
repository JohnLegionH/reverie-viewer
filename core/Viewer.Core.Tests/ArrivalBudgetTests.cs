// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Residency;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The shared arrival budget: what a unit of presentation work costs for its size, the rule that a frame starts no unit that does not fit
/// what is left of it (the first unit after a refusal excepted), a mesh handed to the engine a surface at a time, the pose floor, and the
/// residency manager sorting its resident textures only when it has to free some. A fake clock throughout; nothing waits.
/// </summary>
public class ArrivalBudgetTests
{
    // a clock of 1000 ticks a second: one tick is one millisecond
    private sealed class Clock
    {
        public long Ticks;
        public long Now() => Ticks;
    }

    private static FrameBudget Budget(Clock clock, double totalMs = 4.0) => new(totalMs, clock.Now, 1000);

    /// <summary>A model that has seen <paramref name="n"/> units of size 1 taking <paramref name="msEach"/> milliseconds.</summary>
    private static UnitCostModel Trained(double msEach, int n = 8)
    {
        var m = new UnitCostModel();
        for (int i = 0; i < n; i++) m.Record(1, msEach);
        return m;
    }

    [Fact]
    public void The_estimate_is_unknown_until_enough_units_are_measured_then_scales_with_size()
    {
        var m = new UnitCostModel();
        for (int i = 0; i < UnitCostModel.MinSamples - 1; i++) m.Record(1, 2.0);
        Assert.Equal(0.0, m.EstimateMs(5));
        m.Record(1, 2.0);
        Assert.Equal(UnitCostModel.MinSamples, m.Samples);
        Assert.Equal(10.0, m.EstimateMs(5), 6);   // 2 ms per unit of size, a piece of size 5
        Assert.Equal(2.0, m.EstimateMs(1), 6);
    }

    [Fact]
    public void The_estimate_is_the_ninetieth_percentile_of_the_cost_per_size()
    {
        var m = new UnitCostModel(window: 32, quantile: 0.9);
        for (int ms = 1; ms <= 10; ms++) m.Record(1, ms);   // costs 1 to 10 per unit of size
        Assert.Equal(18.0, m.EstimateMs(2), 6);              // the 9th of 10, for a piece of size 2
    }

    [Fact]
    public void Old_measurements_are_forgotten_as_the_window_moves_on()
    {
        var m = new UnitCostModel(window: 8, quantile: 0.9);
        for (int i = 0; i < 8; i++) m.Record(1, 50.0);
        Assert.Equal(50.0, m.EstimateMs(1), 6);
        for (int i = 0; i < 8; i++) m.Record(1, 1.0);
        Assert.Equal(1.0, m.EstimateMs(1), 6);
        Assert.Equal(8, m.Samples);
    }

    [Fact]
    public void A_unit_that_fits_what_is_left_may_start_and_is_counted()
    {
        var clock = new Clock();
        var budget = Budget(clock);
        budget.Start();
        Assert.True(budget.TryBeginUnit(1.0));
        clock.Ticks += 2;
        Assert.True(budget.TryBeginUnit(2.0));   // exactly what is left
        Assert.Equal(2, budget.UnitsStarted);
        Assert.Equal(0, budget.UnitsRefused);
    }

    [Fact]
    public void A_unit_that_does_not_fit_is_refused_while_an_earlier_one_has_started_this_frame()
    {
        var clock = new Clock();
        var budget = Budget(clock);
        budget.Start();
        Assert.True(budget.TryBeginUnit(1.0));
        clock.Ticks += 3;
        Assert.False(budget.TryBeginUnit(2.0));   // 1 ms left
        Assert.Equal(1, budget.UnitsStarted);
        Assert.Equal(1, budget.UnitsRefused);
    }

    [Fact]
    public void A_unit_bigger_than_the_whole_frame_runs_in_the_frame_after_the_one_that_refused_it()
    {
        var clock = new Clock();
        var budget = Budget(clock);
        budget.Start();
        Assert.False(budget.TryBeginUnit(10.0));   // refused: nothing else has started, but it does not fit
        budget.Start();
        Assert.True(budget.TryBeginUnit(10.0));    // the next frame's first unit goes ahead, so the work cannot starve
        Assert.False(budget.TryBeginUnit(10.0));   // and only the first
        budget.Start();
        Assert.False(budget.TryBeginUnit(10.0));   // the frame after a frame that ran one is back to the rule
    }

    [Fact]
    public void Starting_a_frame_counts_from_nothing_again()
    {
        var clock = new Clock();
        var budget = Budget(clock);
        budget.Start();
        Assert.True(budget.TryBeginUnit(1.0));
        budget.Start();
        Assert.Equal(0, budget.UnitsStarted);
        Assert.Equal(0, budget.UnitsRefused);
    }

    [Fact]
    public void A_list_of_pieces_is_handed_over_in_order_and_no_frame_starts_a_piece_that_does_not_fit()
    {
        var clock = new Clock();
        var budget = Budget(clock);
        var costs = Trained(1.0);   // a piece of size s takes s ms
        double[] sizes = [1, 1, 1, 1, 3];
        var order = new List<int>();
        int next = 0;
        var perFrame = new List<double>();
        for (int frame = 0; frame < 10 && next < sizes.Length; frame++)
        {
            budget.Start();
            int handed = StagedHandOver.Run(sizes, ref next, budget, costs, i => { order.Add(i); clock.Ticks += (long)sizes[i]; });
            perFrame.Add(budget.SpentMs);
            Assert.True(handed > 0 || next >= sizes.Length);
        }
        Assert.Equal([0, 1, 2, 3, 4], order);
        Assert.Equal(sizes.Length, next);
        Assert.Equal(2, perFrame.Count);      // the 3 ms piece did not fit after four 1 ms pieces: the second frame
        Assert.All(perFrame, spent => Assert.True(spent <= budget.TotalMs + 1e-9));
    }

    [Fact]
    public void Pieces_each_bigger_than_the_frame_still_all_finish()
    {
        var clock = new Clock();
        var budget = Budget(clock);
        var costs = Trained(1.0);
        double[] sizes = [10, 10, 10];
        int next = 0, frames = 0;
        while (next < sizes.Length && frames < 20)
        {
            budget.Start();
            StagedHandOver.Run(sizes, ref next, budget, costs, i => clock.Ticks += (long)sizes[i]);
            frames++;
        }
        Assert.Equal(sizes.Length, next);
        Assert.True(frames <= 6, $"took {frames} frames");   // one piece every other frame at the worst
    }

    [Fact]
    public void Each_piece_handed_over_is_measured_for_the_model()
    {
        var clock = new Clock();
        var budget = Budget(clock, totalMs: 100);
        var costs = new UnitCostModel();
        double[] sizes = [2, 2, 2, 2, 2];
        int next = 0;
        budget.Start();
        int handed = StagedHandOver.Run(sizes, ref next, budget, costs, i => clock.Ticks += 6);   // 3 ms for each unit of size
        Assert.Equal(5, handed);                                   // nothing is known yet: all go ahead
        Assert.Equal(5, costs.Samples);
        Assert.Equal(3.0, costs.EstimateMs(1), 6);
    }

    [Fact]
    public void Until_the_model_knows_the_cost_the_first_pieces_go_ahead_and_then_the_estimate_decides()
    {
        var clock = new Clock();
        var budget = Budget(clock, totalMs: 10);
        var costs = new UnitCostModel();
        double[] sizes = [4, 4, 4, 4, 4, 4];   // 4 ms each: three fit in 10 ms
        int next = 0;
        budget.Start();
        int handed = StagedHandOver.Run(sizes, ref next, budget, costs, i => clock.Ticks += 4);
        // the first four are unmeasured and run (16 ms spent); the fifth is refused by the estimate
        Assert.Equal(UnitCostModel.MinSamples, handed);
        Assert.Equal(UnitCostModel.MinSamples, next);
    }

    [Fact]
    public void The_pose_floor_holds_only_while_it_is_cheap_and_one_pose_is_always_made()
    {
        Assert.True(PoseSweep.MayContinue(posedSoFar: 0, spentMs: 99, budgetMs: 1, atLeast: 6, forcedCapMs: 1));   // one is always posed
        Assert.True(PoseSweep.MayContinue(posedSoFar: 3, spentMs: 0.5, budgetMs: 0, atLeast: 6, forcedCapMs: 1));  // under the cap: the floor holds
        Assert.False(PoseSweep.MayContinue(posedSoFar: 3, spentMs: 1.2, budgetMs: 0, atLeast: 6, forcedCapMs: 1)); // over the cap: it does not
        Assert.False(PoseSweep.MayContinue(posedSoFar: 6, spentMs: 0.5, budgetMs: 0, atLeast: 6, forcedCapMs: 1)); // the floor is met
        Assert.True(PoseSweep.MayContinue(posedSoFar: 9, spentMs: 0.9, budgetMs: 1, atLeast: 6, forcedCapMs: 0.5)); // the budget still allows more
    }

    // ---- the residency manager: the resident textures are put in order only when something has to be freed ----

    private const long MiB = 1024 * 1024;
    private static Guid G(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
    private static ResidencyCamera Cam() => new(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 512f, 360);

    private static TextureResidency Loaded(long budget, int textures)
    {
        var r = new TextureResidency(new() { BudgetBytes = budget, MinResidence = TimeSpan.Zero, UsersPerFrame = 256 });
        for (int t = 0; t < textures; t++)
            r.SetUser(new TextureUser(TextureUserKind.Face, (uint)t, 0), G(t), new Bounds(new(10 + t, 0, 0), 2f));
        return r;
    }

    [Fact]
    public void A_frame_that_frees_nothing_does_not_put_the_resident_textures_in_order()
    {
        var r = Loaded(budget: 512 * MiB, textures: 40);
        var t = TimeSpan.Zero;
        // every texture loads and is uploaded, small: well under the budget
        for (int frame = 0; frame < 6; frame++)
        {
            foreach (var a in r.Update(Cam(), t += TimeSpan.FromMilliseconds(16)).Where(a => a.Kind == ResidencyActionKind.Load))
                r.Uploaded(a.Texture, 256 * 1024, PlaceholderColour.Of(100, 110, 120, 255), t);
        }
        Assert.True(r.ResidentBytes > 0, "textures are resident");
        long before = r.ResidentSorts;
        for (int frame = 0; frame < 10; frame++) r.Update(Cam(), t += TimeSpan.FromMilliseconds(16));
        Assert.Equal(0, r.ResidentSorts - before);
    }

    [Fact]
    public void A_frame_that_has_to_free_memory_puts_them_in_order_once()
    {
        var r = Loaded(budget: 64 * MiB, textures: 40);
        var t = TimeSpan.Zero;
        foreach (var a in r.Update(Cam(), t += TimeSpan.FromMilliseconds(16)).Where(a => a.Kind == ResidencyActionKind.Load))
            r.Uploaded(a.Texture, 1 * MiB, PlaceholderColour.Of(100, 110, 120, 255), t);
        for (int frame = 0; frame < 4; frame++) r.Update(Cam(), t += TimeSpan.FromMilliseconds(16));
        r.SetBudget(8 * MiB);   // the budget falls: the next decision must free memory
        long sortsBefore = r.ResidentSorts, evictionsBefore = r.Evictions + r.StepsDown;
        var actions = r.Update(Cam(), t += TimeSpan.FromMilliseconds(16));
        Assert.True(r.Evictions + r.StepsDown > evictionsBefore, "memory was freed");
        Assert.NotEmpty(actions);
        Assert.Equal(1, r.ResidentSorts - sortsBefore);
    }

    [Fact]
    public void The_decisions_over_a_seeded_walk_are_what_they_were()
    {
        // the same scene and walk as always: the order the resident textures are looked at in may change when it is worked out, never what is decided
        var rnd = new Random(7);
        var r = new TextureResidency(new() { BudgetBytes = 24 * MiB, MinResidence = TimeSpan.FromMilliseconds(500), LowWater = 0.9, UsersPerFrame = 128 });
        var size = new Dictionary<Guid, long>();
        for (int t = 0; t < 200; t++) { int side = 32 << rnd.Next(0, 6); size[G(t)] = TextureFootprint.Bytes(side, side, true); }
        for (uint f = 0; f < 400; f++)
            r.SetUser(new TextureUser(TextureUserKind.Face, f, 0), G(rnd.Next(0, 200)), new Bounds(new(rnd.NextSingle() * 256, rnd.NextSingle() * 256, rnd.NextSingle() * 40), 0.2f + rnd.NextSingle() * 6));
        var log = new StringBuilder();
        for (int frame = 0; frame < 300; frame++)
        {
            float a = frame * 0.01f;
            var cam = new ResidencyCamera(new(128 + 100 * MathF.Cos(a), 128 + 100 * MathF.Sin(a), 20), Vector3.Normalize(new(-MathF.Sin(a), MathF.Cos(a), 0)), Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 512f, 360);
            var now = TimeSpan.FromMilliseconds(frame * 16);
            foreach (var act in r.Update(cam, now))
            {
                log.Append(frame).Append(' ').Append(act.Kind).Append(' ').Append(act.Texture.GetHashCode()).Append(' ').Append(act.Level).Append(';');
                if (act.Kind == ResidencyActionKind.Load) r.Uploaded(act.Texture, size[act.Texture], PlaceholderColour.Of(100, 110, 120, 255), now);
            }
        }
        string text = log.ToString();
        Assert.True(text.Length > 1000, "the walk makes decisions");
        Assert.Equal(ExpectedWalk, Checksum(text));
    }

    // ---- the runtime's tiered compilation: code that runs in a burst gets its optimized version at once ----

    /// <summary>A crowd arriving is a burst of code that has never run: with the runtime's defaults it runs as unoptimized, instrumented tier-0 code until
    /// a timer that restarts whenever more new code is compiled has run out, which in a burst it does not for the burst's whole length. Counting calls from
    /// the first call and leaving out the profiling tier puts the hot paths on their optimized code within the first frames (measured on the 25-avatar
    /// arrival, 2 runs each: slices over 4 ms of processor time 25 and 35 down to 8 and 8, main-thread processor time 15 % lower).</summary>
    [Fact]
    public void The_viewer_host_gets_hot_code_to_its_optimized_version_without_waiting_or_profiling()
    {
        string csproj = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "Viewer.Godot.csproj"));
        Assert.Matches(@"<TieredPGO>\s*false\s*</TieredPGO>", csproj);
        Assert.Matches(@"<RuntimeHostConfigurationOption\s+Include=""System\.Runtime\.TieredCompilation\.CallCountingDelayMs""\s+Value=""0""", csproj);
    }

    private static string Checksum(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    private const string ExpectedWalk = "ac9d1cc889af971d";
}
