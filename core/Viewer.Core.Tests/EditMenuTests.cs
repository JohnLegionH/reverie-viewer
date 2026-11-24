// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Edit entry of the right-click menu of an object in the world, as in the official viewer's object menu: it opens build mode with the
/// Edit tool and the object's linkset selected (a child prim selects its root). Delete in the same menu keeps the Build window's Delete rules.
/// Neither entry is offered on avatars, land or a worn object. Each test builds its own world, so the class runs in parallel.
/// </summary>
public class EditMenuTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("2a4c6e80-1b3d-4f5a-8c7e-9d0f1a2b3c01");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;

    private sealed class Rig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public InteractionController C;
        public EditController E;

        public Rig()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            ActionResult Submit(ActionIntent i) { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }
            C = new InteractionController(World, Submit, Me, new SocialHooks(_ => { }, _ => { }, _ => { }, _ => { }, () => { }), _ => false);
            E = new EditController(World, Submit, () => EditLimits.Official, regions: () => [new(0, 0, 256, 256)]);
        }

        public void Prim(uint local, Vector3 pos, uint flags, uint parent = 0) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), parent, 0, pos, Quaternion.Identity, new Vector3(2, 2, 2), Interaction: new PrimInteraction(0, flags)));

        public PickResult Down(float x, float y) =>
            ObjectPicker.Pick(World, new PickRay(new Vector3(x, y, 60), new Vector3(0, 0, -1)), (_, _) => 20f, ignoreAgent: Me);
    }

    [Fact]
    public void The_menu_of_an_object_in_the_world_offers_Edit_and_Delete_and_Edit_is_enabled()
    {
        var mine = new PrimInteraction(0, Own);
        var list = ContextMenus.ForObject(new ObjectMenuContext(mine, mine, false, false, false));
        var ids = list.Select(e => e.Id).ToList();
        Assert.Contains("edit", ids);
        Assert.Contains("delete", ids);
        Assert.True(ids.IndexOf("edit") < ids.IndexOf("delete"));
        Assert.True(list.Single(e => e.Id == "edit").Enabled);
        Assert.Equal("menu.edit", list.Single(e => e.Id == "edit").LabelKey);
    }

    [Fact]
    public void A_worn_object_has_neither_Edit_nor_Delete_in_its_menu()
    {
        var mine = new PrimInteraction(0, Own);
        var ids = ContextMenus.ForObject(new ObjectMenuContext(mine, mine, Attachment: true, false, false)).Select(e => e.Id).ToList();
        Assert.DoesNotContain("edit", ids);
        Assert.DoesNotContain("delete", ids);
    }

    [Fact]
    public void The_menus_of_an_avatar_a_name_the_own_avatar_and_land_have_neither_Edit_nor_Delete()
    {
        var all = new List<ContextEntry>();
        all.AddRange(ContextMenus.ForAvatar(new AvatarMenuContext(false, false)));
        all.AddRange(ContextMenus.ForAvatar(new AvatarMenuContext(true, false)));
        all.AddRange(ContextMenus.ForName(new AvatarMenuContext(false, false)));
        all.AddRange(ContextMenus.ForSelf(new SelfMenuContext(false, false, [])));
        all.AddRange(ContextMenus.ForLand(new LandMenuContext(true, true)));
        Assert.DoesNotContain(all, e => e.Id is "edit" or "delete");
    }

    [Fact]
    public void Choosing_Edit_asks_for_the_root_even_when_a_child_prim_was_clicked()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(10, 10, 32), Own, parent: 1);
        var asked = new List<uint>();
        r.C.EditRequested += id => asked.Add(id);
        r.C.RightClick(r.Down(10, 10));   // the child is on top
        Assert.Contains(r.C.Entries, e => e.Id == "edit");
        Assert.True(r.C.Choose("edit"));
        Assert.Equal([1u], asked);
    }

    [Fact]
    public void Edit_lets_go_of_the_right_click_selection_once_and_before_the_build_tools_select()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        var seenAtRequest = new List<ActionIntent>();
        r.C.EditRequested += _ => seenAtRequest.AddRange(r.Sent);
        r.C.RightClick(r.Down(10, 10));
        r.Sent.Clear();
        Assert.True(r.C.Choose("edit"));
        r.C.CloseMenu();   // what the viewer does right after a choice
        Assert.Contains(seenAtRequest, i => i is DeselectObjectsIntent);
        Assert.Single(r.Sent, i => i is DeselectObjectsIntent);
    }

    [Fact]
    public void Edit_opens_build_mode_with_the_Edit_tool_and_selects_the_linkset_root_alone()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(10, 10, 32), Own, parent: 1);
        r.Prim(3, new Vector3(50, 50, 30), Own);
        r.E.SelectTool(BuildTool.Edit);
        r.E.SelectMany([3], shift: false);   // something else was selected before
        r.E.CloseBuild();
        Assert.False(r.E.BuildMode);
        Assert.True(r.E.EditObject(2));      // the child prim: its root is chosen
        Assert.True(r.E.BuildMode);
        Assert.Equal(BuildTool.Edit, r.E.Tool);
        Assert.Equal([1u], r.E.Selection);
        Assert.False(r.E.EditLinked);
    }

    [Fact]
    public void Edit_replaces_what_build_mode_had_selected()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(3, new Vector3(50, 50, 30), Own);
        r.E.SelectTool(BuildTool.Edit);
        r.E.SelectMany([3], shift: false);
        Assert.True(r.E.EditObject(1));
        Assert.Equal([1u], r.E.Selection);
    }

    [Fact]
    public void An_object_worn_by_someone_else_has_neither_Edit_nor_Delete()
    {
        var other = new PrimInteraction(0, Own);
        var ids = ContextMenus.ForObject(new ObjectMenuContext(other, other, Attachment: false, SittingOnIt: false, Sitting: false, WornByOther: true)).Select(e => e.Id).ToList();
        Assert.DoesNotContain("edit", ids);
        Assert.DoesNotContain("delete", ids);
        Assert.Contains("touch", ids);   // the rest of the menu is as before
    }

    [Fact]
    public void Edit_on_an_object_already_in_the_build_selection_sends_its_select_again_after_the_right_click_released_it()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.E.SelectTool(BuildTool.Edit);
        r.E.SelectMany([1], shift: false);
        r.Sent.Clear();
        Assert.True(r.E.EditObject(1));
        Assert.Equal([1u], r.E.Selection);
        Assert.Contains(r.Sent, i => i is ObjectPropertiesIntent { Hold: true });   // the region is told it is selected
    }

    [Fact]
    public void Edit_selects_the_object_even_with_Select_Only_My_Objects_on_as_the_official_menu_does()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), 0);   // not mine
        r.Prim(3, new Vector3(50, 50, 30), Own);
        r.E.SelectTool(BuildTool.Edit);
        r.E.SelectMany([3], shift: false);
        r.E.SelectOwnedOnly = true;
        Assert.True(r.E.EditObject(1));
        Assert.Equal([1u], r.E.Selection);
    }

    [Fact]
    public void Edit_takes_the_root_with_Edit_linked_on_and_lets_go_of_a_selected_part()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(10, 10, 32), Own, parent: 1);
        r.E.SelectTool(BuildTool.Edit);
        r.E.EditLinked = true;
        r.E.SelectMany([2], shift: false);
        Assert.Equal([2u], r.E.Selection);
        Assert.True(r.E.EditObject(2));
        Assert.Equal([1u], r.E.Selection);
        Assert.True(r.E.EditLinked);
    }

    [Fact]
    public void Edit_refuses_an_object_that_is_not_in_the_world_or_is_worn()
    {
        var r = new Rig();
        Assert.False(r.E.EditObject(99));
        Assert.False(r.E.BuildMode);
        r.World.ApplyObject(Sim, new PrimData(7, G(7), 500, 5, new Vector3(1, 1, 1), Quaternion.Identity, new Vector3(1, 1, 1), Interaction: new PrimInteraction(0, Own)), eventIsAttachment: true);
        Assert.NotNull(r.World.Snapshot().Objects.Values.Single(o => o.Id.LocalId == 7).AttachmentPoint);   // it is worn: the world says so
        Assert.False(r.E.EditObject(7));
        Assert.Empty(r.E.Selection);
    }

    [Fact]
    public void Delete_in_the_menu_sends_the_delete_for_an_object_the_person_may_delete_and_for_nothing_else()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(50, 50, 30), 0);   // someone else's, not modifiable
        r.C.RightClick(r.Down(10, 10));
        r.Sent.Clear();
        Assert.True(r.C.Choose("delete"));
        Assert.Equal(new ActionIntent[] { new DeleteObjectIntent(1) }, r.Sent);
        r.C.RightClick(r.Down(50, 50));
        r.Sent.Clear();
        Assert.False(Assert.Single(r.C.Entries, e => e.Id == "delete").Enabled);
        Assert.False(r.C.Choose("delete"));
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Delete_in_the_menu_asks_the_Build_windows_question_for_a_locked_object()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own & ~ObjectFlagBits.Move);
        var asked = new List<Viewer.Shell.Notifications.Notification>();
        r.C.ConfirmationRequested += asked.Add;
        r.C.RightClick(r.Down(10, 10));
        r.Sent.Clear();
        Assert.True(r.C.Choose("delete"));
        Assert.Empty(r.Sent);
        Assert.Equal("confirm.delete.ConfirmObjectDeleteLock", Assert.Single(asked).Body.Key);
    }
}
