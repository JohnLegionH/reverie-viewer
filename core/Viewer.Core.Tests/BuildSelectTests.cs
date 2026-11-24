// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Avatars;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Build;
using Viewer.Shell.Notifications;
using Viewer.Shell.Ui;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Selecting several objects and removing them, after the official viewer's drag selection (lltoolselectrect.cpp, llglsandbox.cpp) and
/// its Delete command, llselectmgr.cpp selectDelete: what a dragged box takes, with and without Select By Surrounding; which roots Delete removes from a
/// selection and the one question it asks for the whole set; and the right-click menu's Delete on one of several selected objects. Each test
/// builds its own world, so the class runs in parallel.
/// </summary>
public class BuildSelectTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("2a4c6e80-1b3d-4f5a-8c7e-9d0f1a2b3c01");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;

    private static Vector2?[] Square(float left, float top, float right, float bottom) =>
        [new Vector2(left, top), new Vector2(right, top), new Vector2(left, bottom), new Vector2(right, bottom)];

    [Fact]
    public void A_box_takes_an_object_only_when_it_surrounds_it_unless_Select_By_Surrounding_is_off()
    {
        var box = (A: new Vector2(100, 100), B: new Vector2(300, 300));
        var inside = Square(150, 150, 250, 250);
        var crossing = Square(250, 150, 350, 250);   // half in, half out
        var apart = Square(400, 400, 450, 450);
        Assert.True(BoxSelectRules.Takes(inside, box.A, box.B, surround: true));
        Assert.True(BoxSelectRules.Takes(inside, box.B, box.A, surround: true));   // dragged up and to the left
        Assert.False(BoxSelectRules.Takes(crossing, box.A, box.B, surround: true));
        Assert.True(BoxSelectRules.Takes(crossing, box.A, box.B, surround: false));
        Assert.False(BoxSelectRules.Takes(apart, box.A, box.B, surround: true));
        Assert.False(BoxSelectRules.Takes(apart, box.A, box.B, surround: false));
        Assert.False(BoxSelectRules.Takes([], box.A, box.B, surround: false));   // nothing to test: not taken
    }

    [Fact]
    public void A_corner_behind_the_camera_is_outside_the_box()
    {
        var box = (A: new Vector2(100, 100), B: new Vector2(300, 300));
        Vector2?[] oneBehind = [new Vector2(150, 150), new Vector2(250, 250), null, new Vector2(200, 200)];
        Assert.False(BoxSelectRules.Takes(oneBehind, box.A, box.B, surround: true));
        Assert.True(BoxSelectRules.Takes(oneBehind, box.A, box.B, surround: false));
        Assert.False(BoxSelectRules.Takes([null, null], box.A, box.B, surround: false));
    }

    [Fact]
    public void The_corners_of_an_object_are_its_bounding_box_turned_by_its_rotation()
    {
        var plain = BoxSelectRules.Corners(new Vector3(10, 20, 30), Quaternion.Identity, new Vector3(4, 2, 2));
        Assert.Equal(8, plain.Distinct().Count());
        Assert.Equal(8, plain.Count(c => Math.Abs(c.X - 10) == 2f && Math.Abs(c.Y - 20) == 1f && Math.Abs(c.Z - 30) == 1f));
        var turned = BoxSelectRules.Corners(new Vector3(10, 20, 30), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), new Vector3(4, 2, 2));
        Assert.All(turned, c =>
        {
            Assert.InRange(Math.Abs(c.X - 10), 0.99f, 1.01f);   // a quarter turn swaps the long side onto Y
            Assert.InRange(Math.Abs(c.Y - 20), 1.99f, 2.01f);
        });
    }

    [Fact]
    public void A_box_reaches_only_objects_within_the_official_select_distance()
    {
        Assert.True(BoxSelectRules.InReach(new Vector3(100, 100, 20), new Vector3(100, 164, 20)));
        Assert.False(BoxSelectRules.InReach(new Vector3(100, 100, 20), new Vector3(100, 164.5f, 20)));
    }

    [Fact]
    public void One_question_covers_the_whole_selection_as_the_official_viewer_asks_it()
    {
        Assert.Null(BuildRules.DeleteConfirmationForSet([Own, Own]));
        Assert.Null(BuildRules.DeleteConfirmationForSet([]));
        Assert.Equal("ConfirmObjectDeleteLock", BuildRules.DeleteConfirmationForSet([Own, Own & ~ObjectFlagBits.Move]));
        Assert.Equal("ConfirmObjectDeleteNoCopy", BuildRules.DeleteConfirmationForSet([Own & ~ObjectFlagBits.Copy, Own]));
        Assert.Equal("ConfirmObjectDeleteNoOwn", BuildRules.DeleteConfirmationForSet([Own, ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy]));
        Assert.Equal("ConfirmObjectDeleteNoCopyNoOwn", BuildRules.DeleteConfirmationForSet([Own & ~ObjectFlagBits.Copy, ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy]));
    }

    private sealed class EditRig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public EditController C;

        public EditRig()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new EditController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, () => EditLimits.Official,
                regions: () => [new(0, 0, 256, 256)]);
        }

        public void Prim(uint local, Vector3 pos, uint flags, uint parent = 0) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), parent, 0, pos, Quaternion.Identity, new Vector3(2, 2, 2), Interaction: new PrimInteraction(0, flags)));
    }

    [Fact]
    public void A_box_selection_deletes_by_Select_By_Surrounding_on_as_the_official_viewer_has_it()
    {
        Assert.True(new EditRig().C.SelectBySurrounding);
    }

    [Fact]
    public void Delete_takes_each_selected_root_once_skips_what_may_not_be_deleted_and_names_nothing_for_an_empty_selection()
    {
        var r = new EditRig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(2, 0, 0), Own, parent: 1);
        r.Prim(3, new Vector3(50, 50, 30), Own & ~ObjectFlagBits.Copy);
        r.Prim(4, new Vector3(70, 70, 30), 0);   // someone else's, not modifiable
        r.C.SelectTool(BuildTool.Edit);
        Assert.Empty(r.C.DeleteTargets());
        r.C.EditLinked = true;   // the part and its root are both chosen: the root goes once
        r.C.SelectMany([1, 2, 3, 4], shift: false);
        var targets = r.C.DeleteTargets();
        Assert.Equal([1u, 3u], targets.Select(t => t.Root));
        Assert.Equal(Own, targets[0].Flags);
        Assert.Equal(Own & ~ObjectFlagBits.Copy, targets[1].Flags);
        r.C.SelectMany([4], shift: false);
        Assert.Empty(r.C.DeleteTargets());
    }

    [Fact]
    public void Objects_sent_to_the_trash_leave_the_selection()
    {
        var r = new EditRig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(50, 50, 30), Own);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1, 2], shift: false);
        r.C.Forget([1u]);
        Assert.Equal([2u], r.C.Selection);
    }

    private sealed class MenuRig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public InteractionController C;

        public MenuRig()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new InteractionController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, Me,
                new SocialHooks(_ => { }, _ => { }, _ => { }, _ => { }, () => { }), _ => false);
        }

        public void Prim(uint local, Vector3 pos, uint flags) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), 0, 0, pos, Quaternion.Identity, new Vector3(2, 2, 2), Interaction: new PrimInteraction(0, flags)));

        public PickResult Down(float x, float y) =>
            ObjectPicker.Pick(World, new PickRay(new Vector3(x, y, 60), new Vector3(0, 0, -1)), (_, _) => 20f, ignoreAgent: Me);
    }

    [Fact]
    public void Delete_of_several_objects_sends_one_delete_each_at_once_when_nothing_needs_a_question()
    {
        var r = new MenuRig();
        r.C.DeleteObjects([(1, Own), (2, Own)]);
        Assert.Equal(new ActionIntent[] { new DeleteObjectIntent(1), new DeleteObjectIntent(2) }, r.Sent);
    }

    [Fact]
    public void Delete_of_several_objects_asks_one_question_and_sends_nothing_until_it_is_confirmed()
    {
        var r = new MenuRig();
        var asked = new List<Notification>();
        r.C.ConfirmationRequested += asked.Add;
        r.C.DeleteObjects([(1, Own), (2, Own & ~ObjectFlagBits.Copy), (3, Own & ~ObjectFlagBits.Move)]);
        Assert.Empty(r.Sent);
        var question = Assert.Single(asked);
        Assert.Equal("confirm.delete.ConfirmObjectDeleteLockNoCopy", question.Body.Key);
        question.Responder!(new NotificationAnswer(1, ""));   // Cancel
        Assert.Empty(r.Sent);
        question.Responder!(new NotificationAnswer(0, ""));   // OK
        Assert.Equal(new ActionIntent[] { new DeleteObjectIntent(1), new DeleteObjectIntent(2), new DeleteObjectIntent(3) }, r.Sent);
    }

    [Fact]
    public void An_empty_set_deletes_nothing_and_asks_nothing()
    {
        var r = new MenuRig();
        var asked = 0;
        r.C.ConfirmationRequested += _ => asked++;
        r.C.DeleteObjects([]);
        Assert.Empty(r.Sent);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void Delete_in_the_menu_of_a_selected_object_deletes_the_whole_selection_and_of_any_other_object_only_that_one()
    {
        var r = new MenuRig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(30, 10, 30), Own);
        r.Prim(3, new Vector3(50, 10, 30), Own);
        r.C.DeleteSetFor = root => root is 1 or 2 ? [(1, Own), (2, Own)] : null;
        r.C.RightClick(r.Down(10, 10));
        r.Sent.Clear();
        Assert.True(r.C.Choose("delete"));
        Assert.Equal(new ActionIntent[] { new DeleteObjectIntent(1), new DeleteObjectIntent(2) }, r.Sent);
        r.C.RightClick(r.Down(50, 10));
        r.Sent.Clear();
        Assert.True(r.C.Choose("delete"));
        Assert.Equal(new ActionIntent[] { new DeleteObjectIntent(3) }, r.Sent);
    }

    [Fact]
    public void A_Delete_that_does_not_apply_is_greyed_with_its_reason()
    {
        var other = new PrimInteraction(0, 0);
        var entry = Assert.Single(ContextMenus.ForObject(new ObjectMenuContext(other, other, false, false, false)), e => e.Id == "delete");
        Assert.False(entry.Enabled);
        Assert.Equal("menu.delete.reason.not_allowed", entry.ReasonKey);
        var mine = new PrimInteraction(0, Own);
        var enabled = Assert.Single(ContextMenus.ForObject(new ObjectMenuContext(mine, mine, false, false, false)), e => e.Id == "delete");
        Assert.True(enabled.Enabled);
        Assert.Null(enabled.ReasonKey);
    }
}
