// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Shell.Build;
using Viewer.Shell.Inventory;
using Xunit;
using Vector3 = System.Numerics.Vector3;

namespace Viewer.Core.Tests;

/// <summary>
/// The Build window's Contents list and handles: a double-click that survives the list being drawn again, deleting a chosen set with the
/// object's own rights, the Contents section's place and when it opens, and the handles staying where a drag left the selection until the
/// region's update arrives. In-memory worlds and a test clock; each member builds its own, so the class runs in parallel.
/// </summary>
public class ContentsListTests
{
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private static readonly Guid Me = BuildFeaturesTabTests.Me;
    private const uint All = 0x7FFFFFFF;
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;

    private static TaskItem Item(uint n, string name, int type = 7) =>
        new(G(n), name, type, type, Me, All, 0, AssetId: G(n + 500), CreatorId: G(66), BaseMask: All, GroupMask: 0, NextOwnerMask: 0x82000, Description: "", GroupId: G(55),
            Flags: 0, SaleType: 0, SalePrice: 0, CreationDate: 1700000000);

    private static BuildFeaturesTabTests.Rig Selected(uint flags = Own, params TaskItem[] items)
    {
        var r = new BuildFeaturesTabTests.Rig();
        r.Prim(1, new Vector3(10, 10, 30), flags);
        r.Select(10, 10);
        r.C.Receive(new TaskInventoryReceived(G(1), items));
        return r;
    }

    // ---------------- double-click ----------------

    [Fact]
    public void Two_clicks_on_the_same_row_close_together_are_a_double_click_and_the_next_click_starts_again()
    {
        long now = 1000;
        var t = new DoubleClickTracker(() => now);
        Assert.False(t.Click(G(1)));
        now += 300;
        Assert.True(t.Click(G(1)));
        now += 100;
        Assert.False(t.Click(G(1)));   // a third click is the first of the next pair
    }

    [Fact]
    public void Clicks_on_two_rows_or_far_apart_are_not_a_double_click()
    {
        long now = 0;
        var t = new DoubleClickTracker(() => now);
        Assert.False(t.Click(G(1)));
        now += 100;
        Assert.False(t.Click(G(2)));
        now += 900;
        Assert.False(t.Click(G(2)));
        t.Reset();
        now += 10;
        Assert.False(t.Click(G(2)));
        Assert.False(t.Click(Guid.Empty));
        Assert.False(t.Click(Guid.Empty));
    }

    // ---------------- delete ----------------

    [Fact]
    public void Delete_takes_out_every_chosen_item_the_object_allows_and_counts_them()
    {
        var r = Selected(Own, Item(11, "script", 10), Item(12, "card"), Item(13, "tex", 0));
        Assert.Equal(2, r.C.DeleteItems([G(11), G(13), G(11)]));   // a repeat is not deleted twice
        Assert.Equal([new RemoveObjectItemIntent(1, G(11)), new RemoveObjectItemIntent(1, G(13))], r.Sent.OfType<RemoveObjectItemIntent>().ToArray());
    }

    [Fact]
    public void Delete_sends_nothing_for_an_item_that_is_not_listed_or_an_object_the_agent_may_not_change()
    {
        var r = Selected(Own, Item(11, "script", 10));
        Assert.Equal(0, r.C.DeleteItems([G(99)]));
        var locked = Selected(ObjectFlagBits.Move, Item(11, "script", 10));   // neither owned nor modifiable
        Assert.Equal(0, locked.C.DeleteItems([G(11)]));
        Assert.DoesNotContain(locked.Sent, i => i is RemoveObjectItemIntent);
    }

    // ---------------- the Contents section ----------------

    [Fact]
    public void Contents_is_the_first_section_of_the_Build_window_and_opens_for_an_object_that_holds_something()
    {
        Assert.Equal(BuildSection.Contents, BuildLayout.Sections[0]);
        Assert.True(BuildLayout.ContentsOpenFor(1));
        Assert.True(BuildLayout.ContentsOpenFor(12));
        Assert.False(BuildLayout.ContentsOpenFor(0));
        Assert.False(BuildLayout.ContentsOpenFor(null));   // not read yet
    }

    // ---------------- the handles after a drag ----------------

    private static (BuildFeaturesTabTests.Rig R, EditController C, Func<long> Clock, Action<long> Advance) Dragged()
    {
        long now = 5000;
        var r = new BuildFeaturesTabTests.Rig();
        var c = new EditController(r.World, i => { r.Sent.Add(i); return new ActionResult(ActionStatus.Sent, r.Sent.Count); }, clock: () => now);
        c.SelectTool(BuildTool.Edit);
        r.Prim(1, new Vector3(10, 10, 30), Own);
        Assert.True(c.Click(r.At(10, 10), false));
        return (r, c, () => now, ms => now += ms);
    }

    [Fact]
    public void The_handles_stay_where_a_move_left_the_selection_until_the_regions_update_arrives()
    {
        var (r, c, _, _) = Dragged();
        Assert.True(c.BeginManip());
        Assert.True(c.ManipMove(new Vector3(5f, 0, 0)));   // a drag step of 5 m along x
        var dragged = c.ManipCentre!.Value;
        Assert.Equal(15f, dragged.X, 3);
        c.EndManip();
        Assert.Equal(dragged, c.ManipCentre);                   // the world still holds the old place; the handles do not jump back
        Assert.NotEqual(dragged, c.Pivot);
        // the region's update arrives: the object stands where the drag put it
        r.Prim(1, dragged, Own);
        Assert.Null(c.ManipCentre);
        Assert.Equal(dragged.X, c.Pivot!.Value.X, 3);
    }

    [Fact]
    public void The_handles_give_up_waiting_when_the_region_never_answers_and_a_new_drag_starts_clean()
    {
        var (r, c, now, advance) = Dragged();
        Assert.True(c.BeginManip());
        Assert.True(c.ManipMove(new Vector3(5f, 0, 0)));
        c.EndManip();
        Assert.NotNull(c.ManipCentre);
        advance(EditController.HoldMilliseconds + 1);
        Assert.Null(c.ManipCentre);                              // the move was refused: the handles are on the object again
        Assert.Equal(10f, c.Pivot!.Value.X, 3);
        Assert.True(c.BeginManip());
        Assert.Equal(10f, c.ManipCentre!.Value.X, 3);
        c.EndManip();
        Assert.Equal(now(), now());
    }
}
