// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>An avatar's change is not made to wait behind a region's object backlog: a neighbour the agent walks into holds thousands of
/// objects the presentation is still taking in, and the agent's own avatar (which the camera, the controls and the name tag wait for) arrives last.</summary>
public class PresentationUrgentLaneTests
{
    private sealed class Sink : IPresentationSink
    {
        public readonly List<WorldChange> Applied = new();
        public long[]? Clock;
        public void Apply(WorldChange change) { Applied.Add(change); if (Clock is not null) Clock[0]++; }   // one millisecond each
        public bool Step() => false;
    }

    private static WorldChange Obj(uint i) => new(WorldChangeKind.ObjectAdded, new ObjectId(1, i));
    private static WorldChange Av(uint i) => new(WorldChangeKind.AvatarAdded, new ObjectId(1, i));
    private static bool IsAvatar(WorldChange c) => c.Kind is WorldChangeKind.AvatarAdded or WorldChangeKind.AvatarChanged or WorldChangeKind.AvatarMoved or WorldChangeKind.AvatarRemoved;

    private static PresentationCoordinator Make(Sink sink, List<WorldChange> urgent, long[] now) =>
        new(sink, budgetMs: 2, clock: () => now[0], ticksPerSecond: 1000) { IsUrgent = IsAvatar, ApplyUrgent = urgent.Add };

    [Fact]
    public void An_avatar_change_is_applied_in_the_next_frame_whatever_object_changes_wait_ahead_of_it()
    {
        long[] now = [0];
        var sink = new Sink { Clock = now }; var urgent = new List<WorldChange>();
        var c = Make(sink, urgent, now);
        for (uint i = 0; i < 10_000; i++) c.Enqueue(Obj(i));
        c.Enqueue(Av(7));
        c.RunFrame();
        Assert.Single(urgent);
        Assert.Equal(WorldChangeKind.AvatarAdded, urgent[0].Kind);
        Assert.True(c.Backlog > 9_000);   // the objects are still waiting, in the budgeted queue
    }

    [Fact]
    public void The_avatar_change_is_also_applied_in_its_place_in_the_queue()
    {
        var sink = new Sink(); var urgent = new List<WorldChange>();
        var c = new PresentationCoordinator(sink, budgetMs: double.MaxValue) { IsUrgent = IsAvatar, ApplyUrgent = urgent.Add };
        foreach (var ch in new[] { Obj(1), Av(2), Obj(3) }) c.Enqueue(ch);
        c.RunFrame();
        Assert.Equal(new[] { WorldChangeKind.ObjectAdded, WorldChangeKind.AvatarAdded, WorldChangeKind.ObjectAdded }, sink.Applied.Select(a => a.Kind));
    }

    [Fact]
    public void The_queue_tells_the_sink_a_change_the_fast_lane_applied_was_applied_early()
    {
        var applied = new List<(WorldChangeKind Kind, bool Early)>(); var urgent = new List<WorldChange>();
        PresentationCoordinator? c = null;
        var sink = new EarlySink(() => c!.AppliedEarly, applied);
        c = new PresentationCoordinator(sink, budgetMs: double.MaxValue) { IsUrgent = IsAvatar, ApplyUrgent = urgent.Add };
        foreach (var ch in new[] { Obj(1), Av(2) }) c.Enqueue(ch);
        c.RunFrame();
        Assert.Single(urgent);
        Assert.Equal(new[] { (WorldChangeKind.ObjectAdded, false), (WorldChangeKind.AvatarAdded, true) }, applied);
    }

    [Fact]
    public void A_change_the_queue_reaches_before_the_fast_lane_is_not_applied_early_afterwards()
    {
        var sink = new Sink(); var urgent = new List<WorldChange>();
        var c = new PresentationCoordinator(sink, budgetMs: double.MaxValue) { IsUrgent = IsAvatar, ApplyUrgent = urgent.Add, UrgentPerFrame = 0 };
        c.Enqueue(Av(2));
        c.RunFrame();              // the lane takes none this frame; the queue applies the change
        c.UrgentPerFrame = 8;
        c.RunFrame();              // the lane finds it already applied
        Assert.Single(sink.Applied);
        Assert.Empty(urgent);
    }

    private sealed class EarlySink(Func<bool> early, List<(WorldChangeKind, bool)> log) : IPresentationSink
    {
        public void Apply(WorldChange change) => log.Add((change.Kind, early()));
        public bool Step() => false;
    }

    [Fact]
    public void A_frame_applies_at_most_the_per_frame_cap_of_avatar_changes_and_every_change_is_applied_once_in_order()
    {
        var applied = new List<(WorldChangeKind Kind, bool Early)>(); var urgent = new List<WorldChange>(); long[] now = [0];
        var late = new List<uint>();
        PresentationCoordinator? c = null;
        var sink = new EarlySink(() => c!.AppliedEarly, applied);
        c = new PresentationCoordinator(sink, budgetMs: double.MaxValue) { IsUrgent = IsAvatar, ApplyUrgent = urgent.Add, UrgentPerFrame = 3 };
        for (uint i = 1; i <= 7; i++) c.Enqueue(Av(i));
        c.RunFrame();
        Assert.Equal(new uint[] { 1, 2, 3 }, urgent.Select(u => u.Id.LocalId));
        Assert.Equal(new[] { true, true, true, false, false, false, false }, applied.Select(a => a.Early));   // 4 to 7 came through the queue
        c.RunFrame(); c.RunFrame();
        Assert.Equal(new uint[] { 1, 2, 3 }, urgent.Select(u => u.Id.LocalId));   // the lane finds 4 to 7 already applied
    }

    [Fact]
    public void Without_a_lane_nothing_is_applied_early()
    {
        var sink = new Sink(); long[] now = [0];
        var c = new PresentationCoordinator(sink, budgetMs: 2, clock: () => now[0], ticksPerSecond: 1000);
        c.Enqueue(Av(1));
        c.RunFrame();
        Assert.Single(sink.Applied);   // only through the queue
    }
}
