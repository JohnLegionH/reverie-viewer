// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A region with a hundred thousand objects arriving at once still shows its land and starts presenting its objects: terrain patches do
/// not queue behind the objects, and the deferred work (terrain chunks, objects) keeps a share of every frame while the queue drains.
/// The official viewer reads land patches every frame apart from its object messages, and gives geometry its own time budget.
/// </summary>
public class PresentationBacklogTests
{
    private const int Objects = 166_396, PatchesAcross = 64;   // a 1024 m region

    /// <summary>Time in microseconds. A patch costs 1, an object 5, a chunk step 100 (each 16 patches make one chunk to build).</summary>
    private sealed class Sink : IPresentationSink
    {
        public long[] Now = [0];
        public int PatchesApplied, ObjectsApplied, StepsRun, ResetsApplied, StepsReady;
        public void ApplyTerrain(WorldChange c)
        {
            if (c.Kind == WorldChangeKind.RegionReset) { ResetsApplied++; return; }
            Now[0] += 1; PatchesApplied++;
            if (PatchesApplied % 16 == 0) StepsReady++;
        }
        public void Apply(WorldChange c)
        {
            if (c.Kind == WorldChangeKind.TerrainPatch) ApplyTerrain(c);
            else if (c.Kind == WorldChangeKind.RegionReset) ResetsApplied++;
            else { Now[0] += 5; ObjectsApplied++; }
        }
        public bool Step()
        {
            if (StepsReady == 0) return false;
            StepsReady--; Now[0] += 100; StepsRun++;
            return true;
        }
    }

    private static PresentationCoordinator Crowded(Sink sink, bool configured)
    {
        var c = new PresentationCoordinator(sink, budgetMs: 1.0, clock: () => sink.Now[0] * 1000, ticksPerSecond: 1_000_000_000) { ApplyPrompt = sink.ApplyTerrain };
        if (configured) PresentationPolicy.Configure(c);
        for (uint i = 0; i < Objects; i++) c.Enqueue(new WorldChange(WorldChangeKind.ObjectAdded, new ObjectId(1, i)));
        for (int y = 0; y < PatchesAcross; y++)
            for (int x = 0; x < PatchesAcross; x++)
                c.Enqueue(new WorldChange(WorldChangeKind.TerrainPatch, new ObjectId(1, 0), PatchX: x, PatchY: y));
        return c;
    }

    private static void Frames(PresentationCoordinator c, Sink s, int n)
    {
        for (int i = 0; i < n; i++) { c.RunFrame(); s.Now[0] += 15_000; }   // the rest of a 60 Hz frame
    }

    [Fact]
    public void Every_terrain_patch_is_applied_within_two_seconds_of_frames_behind_a_huge_object_backlog()
    {
        var s = new Sink(); var c = Crowded(s, configured: true);
        Frames(c, s, 120);
        Assert.Equal(PatchesAcross * PatchesAcross, s.PatchesApplied);
    }

    [Fact]
    public void Every_terrain_chunk_is_built_within_three_seconds_of_frames_behind_a_huge_object_backlog()
    {
        var s = new Sink(); var c = Crowded(s, configured: true);
        Frames(c, s, 180);
        Assert.Equal(PatchesAcross * PatchesAcross / 16, s.StepsRun);
    }

    [Fact]
    public void The_deferred_work_runs_in_every_frame_while_the_queue_drains()
    {
        var s = new Sink { StepsReady = 1000 }; var c = Crowded(s, configured: true);
        for (int i = 0; i < 20; i++)
        {
            var stats = c.RunFrame(); s.Now[0] += 15_000;
            Assert.True(c.Backlog > 0);
            Assert.True(stats.Steps > 0, "a frame with work ready for the deferred side gives it time");
        }
    }

    [Fact]
    public void A_patch_is_applied_once_and_a_reset_reaches_the_lane_and_the_queue()
    {
        var s = new Sink();
        var c = new PresentationCoordinator(s, budgetMs: double.MaxValue) { ApplyPrompt = s.ApplyTerrain };
        PresentationPolicy.Configure(c);
        c.Enqueue(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(2, 0)));
        c.Enqueue(new WorldChange(WorldChangeKind.TerrainPatch, new ObjectId(2, 0), PatchX: 0, PatchY: 0));
        c.RunFrame();
        Assert.Equal(1, s.PatchesApplied);
        Assert.Equal(2, s.ResetsApplied);   // once in the lane, once in the queue (the objects and avatars clear there)
    }

    [Fact]
    public void A_patch_that_arrives_after_its_regions_reset_is_applied_after_it_in_the_lane()
    {
        var order = new List<WorldChangeKind>();
        var s = new Sink();
        var c = new PresentationCoordinator(s, budgetMs: double.MaxValue) { ApplyPrompt = ch => order.Add(ch.Kind) };
        PresentationPolicy.Configure(c);
        c.Enqueue(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(2, 0)));
        c.Enqueue(new WorldChange(WorldChangeKind.TerrainPatch, new ObjectId(2, 0), PatchX: 3, PatchY: 4));
        // The prompt lane stops after its time allowance on the real clock (a frame always applies at least one change), so on a loaded machine the patch
        // can wait for the next frame. What is asserted is the order, not which frame: frames run until the patch is applied.
        for (int frame = 0; frame < 50 && order.Count < 2; frame++) c.RunFrame();
        Assert.Equal(new[] { WorldChangeKind.RegionReset, WorldChangeKind.TerrainPatch }, order);
    }

    /// <summary>A clock that moves by a fixed step on every reading, so a frame's measured time does not depend on how fast the machine runs it.</summary>
    private sealed class SteppedClock(long step)
    {
        private long _ticks;
        public long Read() => _ticks += step;
    }

    [Fact]
    public void A_patch_that_arrives_after_its_regions_reset_is_never_applied_before_it_when_the_frame_is_slow()
    {
        // With a first apply that takes longer than the prompt lane's allowance (a cold start, a pre-empted thread) the reset is the only prompt change of
        // its frame and the patch follows in the next one: the order of the two never changes, only the frame the patch is applied in.
        var order = new List<WorldChangeKind>();
        var clock = new SteppedClock(step: 2_000_000);   // 2 ms per reading, in nanoseconds
        var c = new PresentationCoordinator(new Sink(), budgetMs: double.MaxValue, clock: clock.Read, ticksPerSecond: 1_000_000_000) { ApplyPrompt = ch => order.Add(ch.Kind) };
        PresentationPolicy.Configure(c);
        c.Enqueue(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(2, 0)));
        c.Enqueue(new WorldChange(WorldChangeKind.TerrainPatch, new ObjectId(2, 0), PatchX: 3, PatchY: 4));
        c.RunFrame();
        Assert.Equal(new[] { WorldChangeKind.RegionReset }, order);   // the patch waits for the next frame, it does not jump ahead
        c.RunFrame();
        Assert.Equal(new[] { WorldChangeKind.RegionReset, WorldChangeKind.TerrainPatch }, order);
    }

    [Fact]
    public void Object_changes_never_take_the_prompt_lane()
    {
        Assert.False(PresentationPolicy.IsPrompt(new WorldChange(WorldChangeKind.ObjectAdded, new ObjectId(1, 5))));
        Assert.True(PresentationPolicy.IsPrompt(new WorldChange(WorldChangeKind.TerrainPatch, new ObjectId(1, 0), PatchX: 1, PatchY: 1)));
        Assert.False(PresentationPolicy.AlsoQueued(new WorldChange(WorldChangeKind.TerrainPatch, new ObjectId(1, 0), PatchX: 1, PatchY: 1)));
        Assert.True(PresentationPolicy.AlsoQueued(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(1, 0))));
    }
}

/// <summary>What is presented first: what an avatar wears, then the nearest objects (the official viewer rebuilds the nearest, largest groups first).</summary>
public class NearestFirstSetTests
{
    [Fact]
    public void The_nearest_ring_comes_first_and_ids_order_within_a_ring()
    {
        var ring = new Dictionary<uint, int> { [1] = 9, [2] = 3, [3] = 3, [4] = 0 };
        var set = new NearestFirstSet(id => ring[id]);
        foreach (var id in new uint[] { 1, 2, 3, 4 }) set.Add(id);
        var order = new List<uint>();
        while (set.Count > 0) { var n = set.Nearest; order.Add(n); set.Remove(n); }
        Assert.Equal(new uint[] { 4, 2, 3, 1 }, order);
    }

    [Fact]
    public void What_is_worn_goes_before_everything()
    {
        var set = new NearestFirstSet(id => id == 500 ? -1 : (int)id);
        set.Add(1); set.Add(500); set.Add(2);
        Assert.Equal(500u, set.Nearest);
    }

    [Fact]
    public void An_id_added_twice_waits_once_and_keeps_its_ring()
    {
        int calls = 0;
        var set = new NearestFirstSet(id => { calls++; return 5; });
        Assert.True(set.Add(7));
        Assert.False(set.Add(7));
        Assert.Equal(1, set.Count);
        Assert.Equal(1, calls);
        Assert.True(set.Contains(7));
    }

    [Fact]
    public void Rebucketing_after_the_camera_moves_brings_the_new_nearest_first()
    {
        var rings = new Dictionary<uint, int> { [1] = 0, [2] = 50 };
        var set = new NearestFirstSet(id => rings[id]);
        set.Add(1); set.Add(2);
        Assert.Equal(1u, set.Nearest);
        rings[1] = 80; rings[2] = 0;   // the camera is now beside object 2
        set.Rebucket();
        Assert.Equal(2u, set.Nearest);
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void Clear_empties_the_set_and_it_can_be_filled_again()
    {
        var set = new NearestFirstSet(id => (int)id);
        set.Add(1); set.Add(2);
        set.Clear();
        Assert.Equal(0, set.Count);
        Assert.False(set.Contains(1));
        set.Add(2);
        Assert.Equal(2u, set.Nearest);
    }
}

/// <summary>After a crossing into a crowded region the walker's own clothes are presented at once: they are taken ahead of the region's objects.</summary>
public class WornObjectLaneTests
{
    private static WorldChange Obj(WorldChangeKind k, uint i) => new(k, new ObjectId(1, i));
    private static bool Worn(ObjectId id) => id.LocalId == 900_001;

    [Theory]
    [InlineData(WorldChangeKind.ObjectAdded)]
    [InlineData(WorldChangeKind.ObjectChanged)]
    public void An_object_worn_on_an_avatar_is_urgent(WorldChangeKind kind) =>
        Assert.True(PresentationPolicy.IsUrgent(Obj(kind, 900_001), hasPrims: true, Worn));

    [Fact]
    public void An_object_in_the_region_is_not_urgent() =>
        Assert.False(PresentationPolicy.IsUrgent(Obj(WorldChangeKind.ObjectAdded, 5), hasPrims: true, Worn));

    [Fact]
    public void A_worn_object_is_not_urgent_without_a_prim_layer_to_present_it() =>
        Assert.False(PresentationPolicy.IsUrgent(Obj(WorldChangeKind.ObjectAdded, 900_001), hasPrims: false, Worn));

    [Theory]
    [InlineData(WorldChangeKind.AvatarAdded)]
    [InlineData(WorldChangeKind.AvatarChanged)]
    [InlineData(WorldChangeKind.AvatarMoved)]
    [InlineData(WorldChangeKind.AvatarRemoved)]
    [InlineData(WorldChangeKind.AppearanceChanged)]
    [InlineData(WorldChangeKind.AnimationsChanged)]
    public void An_avatars_own_changes_are_urgent(WorldChangeKind kind) =>
        Assert.True(PresentationPolicy.IsUrgent(Obj(kind, 3), hasPrims: false, _ => false));

    [Fact]
    public void A_worn_object_behind_a_huge_queue_is_applied_in_the_first_frame()
    {
        long now = 0; var seen = new List<WorldChange>();
        var sink = new NullSink();
        var c = new PresentationCoordinator(sink, budgetMs: 1.0, clock: () => now, ticksPerSecond: 1000) { ApplyUrgent = seen.Add };
        c.IsUrgent = ch => PresentationPolicy.IsUrgent(ch, true, Worn);
        for (uint i = 0; i < 100_000; i++) c.Enqueue(Obj(WorldChangeKind.ObjectAdded, i));
        c.Enqueue(Obj(WorldChangeKind.ObjectAdded, 900_001));
        c.RunFrame();
        Assert.Equal(900_001u, Assert.Single(seen).Id.LocalId);
    }

    private sealed class NullSink : IPresentationSink { public void Apply(WorldChange change) { } public bool Step() => false; }
}
