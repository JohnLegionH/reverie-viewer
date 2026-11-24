// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The rectangle a drag selection draws, and the count of selected linksets and parts the Build window always shows. No engine and no grid;
/// each test builds its own world, so the class runs in parallel.
/// </summary>
public class DragSelectBoxTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Full = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;

    private static (WorldState World, EditController C) Rig()
    {
        var world = new WorldState(new CoreLog(new ListSink(), "test"));
        world.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
        var sent = 0;
        var c = new EditController(world, _ => new ActionResult(ActionStatus.Sent, ++sent));
        c.SelectTool(BuildTool.Edit);
        return (world, c);
    }

    private static void Prim(WorldState world, uint local, uint parent = 0) =>
        world.ApplyObject(Sim, new PrimData(local, G(local), parent, 0, new Vector3(10, 10, 30), Quaternion.Identity, new Vector3(0.5f, 0.5f, 0.5f),
            Interaction: new PrimInteraction(0, Full)));

    [Fact]
    public void There_is_no_box_until_the_pointer_travels_past_the_slop()
    {
        var drag = new DragSelect(new Vector2(100, 100), shift: false);
        Assert.Null(drag.Box);
        drag.Move(new Vector2(101, 101));
        Assert.False(drag.Moved);
        Assert.Null(drag.Box);
    }

    [Theory]
    [InlineData(300, 250, 100, 100, 200, 150)]   // down and right
    [InlineData(40, 60, 40, 60, 60, 40)]         // up and left
    public void A_drag_has_the_box_between_the_press_and_the_pointer(float x, float y, float left, float top, float width, float height)
    {
        var drag = new DragSelect(new Vector2(100, 100), shift: true);
        drag.Move(new Vector2(x, y));
        Assert.True(drag.Moved);
        Assert.True(drag.Shift);
        Assert.Equal((new Vector2(left, top), new Vector2(width, height)), drag.Box);
    }

    [Fact]
    public void A_drag_that_comes_back_to_its_start_stays_a_drag()
    {
        var drag = new DragSelect(new Vector2(100, 100), shift: false);
        drag.Move(new Vector2(200, 200));
        drag.Move(new Vector2(100, 100));
        Assert.True(drag.Moved);
        Assert.Equal((new Vector2(100, 100), Vector2.Zero), drag.Box);
    }

    [Fact]
    public void Nothing_selected_counts_as_no_linksets_and_no_parts()
    {
        var (_, c) = Rig();
        Assert.Equal((0, 0), c.SelectionCounts());
    }

    [Fact]
    public void Whole_linksets_count_every_prim_in_them()
    {
        var (world, c) = Rig();
        Prim(world, 1);
        Prim(world, 2, parent: 1);
        Prim(world, 3, parent: 1);
        Prim(world, 7);
        c.SelectMany([1u], shift: false);
        Assert.Equal((1, 3), c.SelectionCounts());
        c.SelectMany([7u], shift: true);
        Assert.Equal((2, 4), c.SelectionCounts());
    }

    [Fact]
    public void With_Edit_linked_only_the_picked_parts_count()
    {
        var (world, c) = Rig();
        Prim(world, 1);
        Prim(world, 2, parent: 1);
        Prim(world, 3, parent: 1);
        c.EditLinked = true;
        c.SelectMany([2u], shift: false);
        Assert.Equal((1, 1), c.SelectionCounts());
        c.SelectMany([3u], shift: true);
        Assert.Equal((1, 2), c.SelectionCounts());
    }

    [Theory]
    [InlineData(0, 0, "Selected: 0 linksets, 0 parts")]
    [InlineData(1, 1, "Selected: 1 linkset, 1 part")]
    [InlineData(2, 5, "Selected: 2 linksets, 5 parts")]
    public void The_count_line_names_linksets_and_parts(int linksets, int parts, string expected) =>
        Assert.Equal(expected, BuildLayout.SelectionCountLine(linksets, parts, StringTable.English()));
}
