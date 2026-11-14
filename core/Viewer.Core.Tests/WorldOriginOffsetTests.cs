// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A world anchored away from its region's own corner keeps free-standing things in the anchor's frame and leaves
/// everything that hangs from something else relative to it.</summary>
public class WorldOriginOffsetTests
{
    private static readonly SimRef Sim = new(257000UL << 32 | 256000UL, "East");
    private static readonly Vector3 Offset = new(256, -512, 0);

    private static WorldState World(Vector3? offset = null)
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.BeginRegion(Sim, new RegionInfo("East", 1004, 1000, 256, 256, true));
        if (offset is { } o) w.SetOriginOffset(o);
        return w;
    }

    private static PrimData Prim(uint local, uint parent = 0, int point = 0) =>
        new(local, Guid.NewGuid(), parent, point, new Vector3(10, 20, 30), Quaternion.Identity, Vector3.One);

    private static AvatarData Av(uint local, uint parent = 0) =>
        new(local, Guid.NewGuid(), "Someone", new Vector3(5, 6, 7), Quaternion.Identity, Vector3.One, ParentLocalId: parent);

    [Fact]
    public void The_offset_is_kept()
    {
        var w = World(Offset);
        Assert.Equal(Offset, w.OriginOffset);
    }

    [Fact]
    public void A_free_standing_prim_is_placed_in_the_anchor_frame()
    {
        var w = World(Offset);
        w.ApplyObject(Sim, Prim(1));
        Assert.Equal(new Vector3(266, -492, 30), w.Snapshot().Objects[1].Position);
    }

    [Fact]
    public void A_linked_child_stays_relative_to_its_root()
    {
        var w = World(Offset);
        w.ApplyObject(Sim, Prim(1));
        w.ApplyObject(Sim, Prim(2, parent: 1));
        Assert.Equal(new Vector3(10, 20, 30), w.Snapshot().Objects[2].Position);
    }

    [Fact]
    public void A_worn_object_stays_relative_to_its_avatar()
    {
        var w = World(Offset);
        w.ApplyAvatar(Sim, Av(5));
        w.ApplyObject(Sim, Prim(6, parent: 5, point: 1));
        Assert.Equal(new Vector3(10, 20, 30), w.Snapshot().Objects[6].Position);
    }

    [Fact]
    public void A_standing_avatar_is_placed_and_a_seated_one_stays_relative()
    {
        var w = World(Offset);
        w.ApplyAvatar(Sim, Av(5));
        w.ApplyObject(Sim, Prim(1));
        w.ApplyAvatar(Sim, Av(7, parent: 1));
        Assert.Equal(new Vector3(261, -506, 7), w.Snapshot().Avatars[5].Position);
        Assert.Equal(new Vector3(5, 6, 7), w.Snapshot().Avatars[7].Position);
    }

    [Fact]
    public void A_terse_update_is_placed_the_same_way()
    {
        var w = World(Offset);
        w.ApplyObject(Sim, Prim(1));
        w.ApplyObject(Sim, Prim(2, parent: 1));
        w.ApplyAvatar(Sim, Av(5));

        w.ApplyMove(Sim, 1, false, new Vector3(1, 2, 3), Quaternion.Identity, Vector3.Zero);
        w.ApplyMove(Sim, 2, false, new Vector3(4, 5, 6), Quaternion.Identity, Vector3.Zero);
        w.ApplyMove(Sim, 5, true, new Vector3(7, 8, 9), Quaternion.Identity, Vector3.Zero);

        var snap = w.Snapshot();
        Assert.Equal(new Vector3(257, -510, 3), snap.Objects[1].Position);
        Assert.Equal(new Vector3(4, 5, 6), snap.Objects[2].Position);
        Assert.Equal(new Vector3(263, -504, 9), snap.Avatars[5].Position);
    }

    [Fact]
    public void A_duplicate_update_changes_nothing()
    {
        var w = World(Offset);
        var raised = 0;
        w.Changed += _ => raised++;
        w.ApplyObject(Sim, Prim(1) with { FullId = Guid.Parse("0f0f0f0f-0000-0000-0000-000000000001") });
        w.ApplyObject(Sim, Prim(1) with { FullId = Guid.Parse("0f0f0f0f-0000-0000-0000-000000000001") });
        Assert.Equal(1, raised);
    }

    [Fact]
    public void The_agents_own_motion_is_placed_in_the_anchor_frame()
    {
        var w = World(Offset);
        SelfMotionSample? got = null;
        w.SelfMoved += s => got = s;

        w.ApplySelfMotion(Sim, new SelfMotion(new Vector3(100, 100, 25), Quaternion.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero, 0, default));

        Assert.NotNull(got);
        Assert.Equal(new Vector3(356, -412, 25), got!.Motion.Position);
        Assert.Equal(new Vector3(356, -412, 25), w.LastSelfMotion!.Motion.Position);
    }

    [Fact]
    public void A_retired_world_is_empty_says_so_and_takes_another_region_later()
    {
        var w = World(Offset);
        w.ApplyObject(Sim, Prim(1));
        w.ApplyAvatar(Sim, Av(5));
        var changes = new List<WorldChange>();
        w.Changed += changes.Add;
        int epoch = w.Epoch;

        w.Retire();

        Assert.Empty(w.Snapshot().Objects);
        Assert.Empty(w.Snapshot().Avatars);
        Assert.Null(w.Region);
        Assert.True(w.Epoch > epoch);
        Assert.Contains(changes, c => c.Kind == WorldChangeKind.RegionReset);

        var other = new SimRef(300000UL << 32 | 256000UL, "Other");
        w.BeginRegion(other, new RegionInfo("Other", 1171, 1000, 256, 256, true));
        w.ApplyObject(other, Prim(2));
        Assert.Single(w.Snapshot().Objects);
    }

    [Fact]
    public void Without_an_offset_nothing_moves()
    {
        var w = World();
        w.ApplyObject(Sim, Prim(1));
        w.ApplyAvatar(Sim, Av(5));
        Assert.Equal(Vector3.Zero, w.OriginOffset);
        Assert.Equal(new Vector3(10, 20, 30), w.Snapshot().Objects[1].Position);
        Assert.Equal(new Vector3(5, 6, 7), w.Snapshot().Avatars[5].Position);
    }
}
