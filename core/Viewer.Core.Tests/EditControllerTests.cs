// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Avatars;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Meshing;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Build mode, the edit selection and what each edit sends, on an in-memory world: the tools and their keys' effect, click and Shift-click
/// selection, Edit linked, read-only selection, typed numbers and their clamps, shape, flags, Locked, Link with its root, Unlink and the
/// Shift-drag copy. No engine and no grid; each test builds its own world, so the class runs in parallel.
/// </summary>
public class EditControllerTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("2a4c6e80-1b3d-4f5a-8c7e-9d0f1a2b3c01");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;

    private sealed class Rig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public EditLimits Limits = EditLimits.Official;
        public List<RegionArea> Regions = [new(0, 0, 256, 256)];
        public EditController C;

        public Rig()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new EditController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, () => Limits, regions: () => Regions);
        }

        public void Prim(uint local, Vector3 pos, uint flags, uint parent = 0, Vector3? scale = null) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), parent, 0, pos, Quaternion.Identity, scale ?? new Vector3(2, 2, 2), Interaction: new PrimInteraction(0, flags)));

        public PickResult At(float x, float y) => ObjectPicker.Pick(World, new PickRay(new Vector3(x, y, 60), new Vector3(0, 0, -1)), (_, _) => 20f, ignoreAgent: Me);

        public T Last<T>() where T : ActionIntent => Assert.IsType<T>(Sent[^1]);
    }

    [Fact]
    public void Ctrl_B_opens_build_mode_with_the_Create_tool_and_closing_ends_it_with_Edit_linked_off_and_nothing_selected()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.C.ToggleBuild();
        Assert.True(r.C.BuildMode);
        Assert.Equal(BuildTool.Create, r.C.Tool);
        r.C.SelectTool(BuildTool.Edit);
        r.C.EditLinked = true;
        Assert.True(r.C.Click(r.At(10, 10), shift: false));
        Assert.Equal([1u], r.C.Selection);
        r.C.ToggleBuild();
        Assert.False(r.C.BuildMode);
        Assert.False(r.C.EditLinked);
        Assert.Empty(r.C.Selection);
        r.C.SelectTool(BuildTool.Move);   // Ctrl+2 from outside build mode opens it with that tool
        Assert.True(r.C.BuildMode);
        Assert.Equal(BuildTool.Move, r.C.Tool);
        r.C.CloseBuild();
        Assert.False(r.C.BuildMode);
    }

    [Fact]
    public void A_click_selects_the_root_Shift_click_adds_and_removes_Edit_linked_selects_the_part_and_a_click_on_nothing_clears()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(20, 0, 0), Own, parent: 1);          // a child at (30, 10)
        r.Prim(3, new Vector3(50, 50, 30), Own);
        r.C.SelectTool(BuildTool.Edit);
        Assert.True(r.C.Click(r.At(30, 10), false));
        Assert.Equal([1u], r.C.Selection);
        Assert.Contains(r.Sent, i => i is ObjectPropertiesIntent { LocalId: 1 });
        Assert.True(r.C.Click(r.At(50, 50), true));
        Assert.Equal([1u, 3u], r.C.Selection);
        Assert.Equal(3u, r.C.Primary);
        Assert.True(r.C.Click(r.At(10, 10), true));                 // Shift-click a selected one: removed
        Assert.Equal([3u], r.C.Selection);
        Assert.True(r.C.Click(r.At(10, 10), false));                // plain click: replaces
        Assert.Equal([1u], r.C.Selection);
        r.C.EditLinked = true;
        Assert.True(r.C.Click(r.At(30, 10), false));
        Assert.Equal([2u], r.C.Selection);
        Assert.True(r.C.Click(r.At(200, 200), false));              // nothing under the pointer
        Assert.Empty(r.C.Selection);
        r.C.SelectMany([1, 2, 3], shift: false);                     // drag-select, Edit linked off: roots once each
        r.C.EditLinked = false;
        r.C.SelectMany([2, 3], shift: false);
        Assert.Equal([1u, 3u], r.C.Selection);
    }

    [Fact]
    public void Clicks_are_not_the_edit_tools_outside_build_mode_or_with_the_Create_tool()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        Assert.False(r.C.Click(r.At(10, 10), false));
        r.C.ToggleBuild();
        Assert.False(r.C.Click(r.At(10, 10), false));
        Assert.Empty(r.C.Selection);
    }

    [Fact]
    public void Someone_elses_object_selects_read_only_and_no_edit_is_sent_for_it()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), 0);
        r.C.SelectTool(BuildTool.Edit);
        Assert.True(r.C.Click(r.At(10, 10), false));
        Assert.Equal([1u], r.C.Selection);
        Assert.True(r.C.Access(1).ReadOnly);
        r.Sent.Clear();
        Assert.False(r.C.SetPosition(new Vector3(12, 12, 30)));
        Assert.False(r.C.SetSize(new Vector3(1, 1, 1)));
        Assert.False(r.C.SetName("Mine now"));
        Assert.False(r.C.SetShape(PrimShape.Box with { ProfileHollow = 0.5 }));
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Typed_numbers_go_out_clamped_by_the_grids_limits_with_the_official_update_bits()
    {
        var r = new Rig();
        r.Limits = EditLimits.Official with { MaxScale = 128 };
        r.Prim(1, new Vector3(10, 10, 30), Own, scale: new Vector3(0.5f, 0.5f, 0.5f));
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(10, 10), false);
        Assert.True(r.C.SetSize(new Vector3(10, 10, 0.2f)));
        var size = r.Last<UpdateObjectIntent>();
        Assert.Equal((1u, new Vector3(10, 10, 0.2f), new Vector3(10, 10, 30), (Quaternion?)null, true), (size.LocalId, size.Scale!.Value, size.Position!.Value, size.Rotation, size.Linkset));
        Assert.True(r.C.SetSize(new Vector3(200, 0.001f, 1)));
        Assert.Equal(new Vector3(128, 0.01f, 1), r.Last<UpdateObjectIntent>().Scale);
        Assert.True(r.C.SetPosition(new Vector3(100, 12, 5000)));
        var pos = r.Last<UpdateObjectIntent>();
        Assert.Equal(new Vector3(100, 12, 4096), pos.Position);
        Assert.Null(pos.Scale);
        Assert.True(r.C.SetRotation(new Vector3(0, 0, 90)));
        var rot = r.Last<UpdateObjectIntent>();
        Assert.NotNull(rot.Position);
        Assert.True(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, rot.Rotation!.Value)) < 1e-4f);
    }

    [Fact]
    public void With_Edit_linked_a_part_moves_alone()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(20, 0, 0), Own, parent: 1);
        r.C.SelectTool(BuildTool.Edit);
        r.C.EditLinked = true;
        r.C.Click(r.At(30, 10), false);
        Assert.True(r.C.SetPosition(new Vector3(21, 0, 0)));
        var u = r.Last<UpdateObjectIntent>();
        Assert.Equal((2u, false), (u.LocalId, u.Linkset));
    }

    [Fact]
    public void Shape_flags_lock_name_and_permissions_are_sent_for_the_selection()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(10, 10), false);
        Assert.True(r.C.SetShape(PrimShape.Box with { ProfileHollow = 0.99, PathTwist = 0.5 }));
        var sh = r.Last<SetObjectShapeIntent>();
        Assert.Equal(1u, sh.LocalId);
        Assert.Equal(47500, sh.Shape.ProfileHollow);
        Assert.Equal(50, sh.Shape.PathTwist);
        Assert.True(r.C.SetFlags(physical: false, temporary: true, phantom: true));
        Assert.Equal(new SetObjectFlagsIntent(1, false, true, true), r.Last<SetObjectFlagsIntent>());
        Assert.True(r.C.SetLocked(true));
        var lk = r.Last<SetObjectLockedIntent>();
        Assert.Equal([1u], lk.LocalIds.ToArray());
        Assert.True(lk.Locked);
        Assert.True(r.C.SetName("Floor"));
        Assert.Equal(new SetObjectNameIntent(1, "Floor"), r.Last<SetObjectNameIntent>());
        Assert.True(r.C.SetDescription("Plank floor"));
        Assert.Equal(new SetObjectDescriptionIntent(1, "Plank floor"), r.Last<SetObjectDescriptionIntent>());
        Assert.True(r.C.SetPermission(0x10, 1u << 15, true));
        var pm = r.Last<SetObjectPermissionsIntent>();
        Assert.Equal([1u], pm.LocalIds.ToArray());
        Assert.Equal(((byte)0x10, true, 1u << 15), (pm.Field, pm.Set, pm.Mask));
    }

    [Fact]
    public void Link_makes_the_last_selected_the_root_and_is_refused_for_what_may_not_be_linked()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(3, new Vector3(50, 50, 30), Own);
        r.Prim(4, new Vector3(80, 80, 30), ObjectFlagBits.Modify | ObjectFlagBits.Move);   // not ours
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(10, 10), false);
        r.C.Click(r.At(50, 50), true);
        Assert.Null(r.C.Link());
        Assert.Equal([3u, 1u], r.Last<LinkObjectsIntent>().OrderedRoots.ToArray());
        r.Sent.Clear();
        r.C.Click(r.At(80, 80), true);
        Assert.Equal("link.not_owned", r.C.Link());
        Assert.DoesNotContain(r.Sent, i => i is LinkObjectsIntent);
        r.C.EditLinked = true;
        r.C.SelectMany([1, 3], false);
        Assert.Equal("link.edit_linked", r.C.Link());
    }

    [Fact]
    public void The_Build_menu_gains_Build_the_tools_Edit_linked_Link_and_Unlink_with_their_official_shortcuts()
    {
        var r = new Rig();
        var menus = new Viewer.Shell.MenuModel();
        bool inWorld = true, selected = false;
        Viewer.Shell.BuildMenu.Install(menus, () => inWorld);
        Viewer.Shell.BuildMenu.InstallEditing(menus, () => inWorld, () => r.C, () => selected);
        var build = menus.Menus.Single(m => m.Id == "build");
        Assert.Equal(["build.toggle", "build.tool.focus", "build.tool.move", "build.tool.edit", "build.create", "build.edit_linked", "build.link", "build.unlink", "build.upload"],
            build.Entries.Select(e => e.Id));
        Assert.Equal(["control|B", "control|1", "control|2", "control|3", "control|4", null, "control|L", "control|shift|L", null], build.Entries.Select(e => e.Shortcut));
        Assert.All(build.Entries, e => Assert.True(Viewer.Shell.StringTable.English().Has(e.LabelKey), e.LabelKey));
        var toggle = build.Entries[0];
        Assert.False(toggle.Checked!());
        r.C.ToggleBuild();
        Assert.True(toggle.Checked!());
        Assert.False(build.Entries.Single(e => e.Id == "build.link").IsEnabled);
        selected = true;
        Assert.True(build.Entries.Single(e => e.Id == "build.link").IsEnabled);
        Assert.Same(menus.ForShortcut("control|b"), toggle);
        Assert.DoesNotContain(Viewer.Shell.ViewerShortcuts.NotBuilt, n => n.Shortcut is "control|B" or "control|1" or "control|2" or "control|3" or "control|L" or "control|shift|L");
    }

    [Fact]
    public void Unlink_sends_every_prim_of_the_selected_linksets()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(20, 0, 0), Own, parent: 1);
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(10, 10), false);
        Assert.True(r.C.Unlink());
        Assert.Equal([1u, 2u], r.Last<DelinkObjectsIntent>().LocalIds.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void A_Shift_drag_copies_the_selection_in_place_so_the_copy_stays_behind_and_needs_copy_permission()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(5, new Vector3(60, 60, 30), ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move);   // no copy
        r.C.SelectTool(BuildTool.Move);
        r.C.Click(r.At(10, 10), false);
        Assert.True(r.C.CopyInPlace());
        var d = r.Last<DuplicateObjectsIntent>();
        Assert.Equal([1u], d.Roots.ToArray());
        Assert.Equal((Vector3.Zero, 0u), (d.Offset, d.Flags));
        r.Sent.Clear();
        r.C.Click(r.At(60, 60), false);
        Assert.False(r.C.CopyInPlace());
        Assert.DoesNotContain(r.Sent, i => i is DuplicateObjectsIntent);
    }
    // ---------------- deselecting (llselectmgr.cpp deselectAll and deselectObjectOnly send ObjectDeselect) ----------------

    private static uint[] Deselected(Rig r) => r.Sent.OfType<DeselectObjectsIntent>().SelectMany(d => d.LocalIds).ToArray();

    [Fact]
    public void What_leaves_the_selection_is_deselected_on_the_region_click_elsewhere_Shift_click_click_on_nothing_and_Edit_linked_off()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(20, 0, 0), Own, parent: 1);
        r.Prim(3, new Vector3(50, 50, 30), Own);
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(10, 10), false);
        Assert.Empty(Deselected(r));
        r.C.Click(r.At(50, 50), false);                              // something else: the first goes
        Assert.Equal([1u], Deselected(r));
        r.Sent.Clear();
        r.C.Click(r.At(10, 10), true);                               // Shift adds, nothing goes
        Assert.Empty(Deselected(r));
        r.C.Click(r.At(50, 50), true);                               // Shift-click a selected one: it goes
        Assert.Equal([3u], Deselected(r));
        r.Sent.Clear();
        r.C.Click(r.At(200, 200), false);                            // a click on nothing
        Assert.Equal([1u], Deselected(r));
        r.C.EditLinked = true;
        r.C.Click(r.At(30, 10), false);
        r.Sent.Clear();
        r.C.EditLinked = false;                                      // the part goes, its root is selected
        Assert.Equal([2u], Deselected(r));
        Assert.Contains(r.Sent, i => i is ObjectPropertiesIntent { LocalId: 1 });
        Assert.Equal([1u], r.C.Selection);
        r.Sent.Clear();
        r.C.SelectMany([3], shift: false);                           // a drag-select replaces
        Assert.Equal([1u], Deselected(r));
    }

    [Fact]
    public void Leaving_build_mode_a_teleport_and_logging_out_deselect_everything_selected_in_one_message()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(3, new Vector3(50, 50, 30), Own);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1, 3], shift: false);
        r.Sent.Clear();
        r.C.CloseBuild();
        var d = Assert.Single(r.Sent.OfType<DeselectObjectsIntent>());
        Assert.Equal([1u, 3u], d.LocalIds.Order().ToArray());

        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([3], shift: false);
        r.Sent.Clear();
        r.C.Receive(new TeleportProgressed(TeleportPhase.Started, "", true));
        Assert.Equal([3u], Deselected(r));
        Assert.Empty(r.C.Selection);

        r.C.SelectMany([1], shift: false);
        r.Sent.Clear();
        r.C.BeforeLogout();
        Assert.Equal([1u], Deselected(r));
        Assert.Empty(r.C.Selection);
        r.Sent.Clear();
        r.C.Deselect();                                              // nothing selected: nothing sent
        Assert.Empty(r.Sent);
    }

    // ---------------- typed positions (llpanelobject.cpp sendPosition: the place must lie in a region the viewer knows) ----------------

    [Fact]
    public void A_typed_position_outside_the_own_region_is_sent_when_a_known_neighbour_holds_it_and_refused_otherwise()
    {
        var r = new Rig();
        r.Regions.Add(new RegionArea(256, 0, 256, 256));              // a neighbour to the east
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(10, 10), false);
        r.Sent.Clear();
        Assert.True(r.C.SetPosition(new Vector3(300, 12, 30)));       // inside the neighbour: sent as typed, in the own region's frame
        Assert.Equal(new Vector3(300, 12, 30), r.Last<UpdateObjectIntent>().Position);
        r.Sent.Clear();
        Assert.False(r.C.SetPosition(new Vector3(-5, 12, 30)));       // west of the region, no neighbour there: nothing sent
        Assert.False(r.C.SetPosition(new Vector3(100, 600, 30)));
        Assert.Empty(r.Sent);

        var v = new Rig();                                           // a var region of 512 x 512 alone
        v.Regions[0] = new RegionArea(0, 0, 512, 512);
        v.Prim(1, new Vector3(10, 10, 30), Own);
        v.C.SelectTool(BuildTool.Edit);
        v.C.Click(v.At(10, 10), false);
        Assert.True(v.C.SetPosition(new Vector3(400, 500, 5000)));
        Assert.Equal(new Vector3(400, 500, 4096), v.Last<UpdateObjectIntent>().Position);
        Assert.False(v.C.SetPosition(new Vector3(512, 10, 30)));     // the far edge belongs to the next region (pointInRegionGlobal)
    }
    // ---------------- the handles' drags (llmaniptranslate.cpp, llmaniprotate.cpp, llmanipscale.cpp as behaviour) ----------------

    private static UpdateObjectIntent[] Updates(Rig r) => r.Sent.OfType<UpdateObjectIntent>().ToArray();
    private static void Near(Vector3 want, Vector3 got, float tol = 1e-3f) => Assert.True(Vector3.Distance(want, got) <= tol, $"want {want}, got {got}");

    [Fact]
    public void A_move_handle_drag_moves_every_selected_object_by_the_same_amount_and_snaps_the_selections_centre_to_the_grid()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10.2f, 10, 30), Own);
        r.Prim(3, new Vector3(12.2f, 10, 30), Own);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1, 3], shift: false);
        Near(new Vector3(11.2f, 10, 30), r.C.Pivot ?? new Vector3(float.NaN));   // no centre fails the comparison
        r.C.SnapEnabled = false;
        Assert.True(r.C.BeginManip());
        r.Sent.Clear();
        Assert.True(r.C.ManipMove(new Vector3(1, 0, 0)));
        var u = Updates(r);
        Assert.Equal(2, u.Length);
        Near(new Vector3(11.2f, 10, 30), u.Single(x => x.LocalId == 1).Position!.Value);
        Near(new Vector3(13.2f, 10, 30), u.Single(x => x.LocalId == 3).Position!.Value);
        Assert.All(u, x => Assert.True(x.Linkset && x.Rotation is null && x.Scale is null));
        r.Sent.Clear();
        r.C.SnapEnabled = true;                                       // the centre 11.2 + 1 = 12.2 snaps to 12.0 along x only
        Assert.True(r.C.ManipMove(new Vector3(1, 0, 0)));
        Near(new Vector3(11.0f, 10, 30), Updates(r).Single(x => x.LocalId == 1).Position!.Value);
        r.C.EndManip();
    }

    [Fact]
    public void A_ring_drag_turns_the_selection_about_its_centre_and_a_corner_or_face_drag_stretches_it()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own, scale: new Vector3(1, 1, 1));
        r.Prim(3, new Vector3(12, 10, 30), Own, scale: new Vector3(1, 1, 1));
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1, 3], shift: false);
        Assert.True(r.C.BeginManip());
        r.Sent.Clear();
        Assert.True(r.C.ManipRotate(Vector3.UnitZ, 90));
        var u = Updates(r);
        Near(new Vector3(11, 9, 30), u.Single(x => x.LocalId == 1).Position!.Value);
        Near(new Vector3(11, 11, 30), u.Single(x => x.LocalId == 3).Position!.Value);
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, u[0].Rotation!.Value));
        r.C.EndManip();

        Assert.True(r.C.BeginManip());
        r.Sent.Clear();
        Assert.True(r.C.ManipScaleCorner(2));                          // both grow, and spread from the centre
        u = Updates(r);
        Near(new Vector3(2, 2, 2), u.Single(x => x.LocalId == 1).Scale!.Value);
        Near(new Vector3(9, 10, 30), u.Single(x => x.LocalId == 1).Position!.Value);
        r.C.EndManip();

        r.C.SelectMany([1], shift: false);
        Assert.True(r.C.BeginManip());
        r.Sent.Clear();
        Assert.True(r.C.ManipScaleFace(0, 1, 3));                       // the +x face out to 3 m: the -x face stays put
        var f = Updates(r).Single();
        Near(new Vector3(3, 1, 1), f.Scale!.Value);
        Near(new Vector3(11, 10, 30), f.Position!.Value);
        Assert.False(f.Uniform);
        r.Sent.Clear();
        r.C.StretchBothSides = true;                                    // both sides: the centre stays, sent as a uniform change
        Assert.True(r.C.ManipScaleFace(0, 1, 3));
        f = Updates(r).Single();
        Near(new Vector3(10, 10, 30), f.Position!.Value);
        Assert.True(f.Uniform);
        r.C.EndManip();
    }

    [Fact]
    public void With_Edit_linked_a_handle_moves_the_part_alone_in_its_roots_frame()
    {
        var r = new Rig();
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        r.World.ApplyObject(Sim, new PrimData(1, G(1), 0, 0, new Vector3(10, 10, 30), turned, new Vector3(2, 2, 2), Interaction: new PrimInteraction(0, Own)));
        r.Prim(2, new Vector3(2, 0, 0), Own, parent: 1);              // in the world at (10, 12, 30)
        r.C.SelectTool(BuildTool.Edit);
        r.C.EditLinked = true;
        r.C.SelectMany([2], shift: false);
        Near(new Vector3(10, 12, 30), r.C.Pivot ?? new Vector3(float.NaN));   // no centre fails the comparison
        Assert.True(r.C.BeginManip());
        r.Sent.Clear();
        r.C.SnapEnabled = false;
        Assert.True(r.C.ManipMove(new Vector3(0, 1, 0)));             // one metre north in the world is one along the root's own x
        var u = Updates(r).Single();
        Assert.Equal((2u, false), (u.LocalId, u.Linkset));
        Near(new Vector3(3, 0, 0), u.Position!.Value);
    }

    [Fact]
    public void Snap_XY_puts_each_selected_object_on_the_grid_use_selection_for_grid_moves_the_grid_and_G_toggles_snapping()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10.3f, 10.8f, 30), Own);
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(10.3f, 10.8f), false);
        r.Sent.Clear();
        Assert.True(r.C.SnapSelectionXY());                            // Shift+X
        Near(new Vector3(10.5f, 11f, 30), Updates(r).Single().Position!.Value);
        r.C.UseSelectionForGrid();                                     // Shift+G: the grid's origin and turn are the selection's
        Assert.Equal(GridMode.Reference, r.C.Grid);
        Near(new Vector3(10.3f, 10.8f, 30), r.C.GridOrigin);
        r.Sent.Clear();
        Assert.True(r.C.SnapSelectionXY());                            // already on the moved grid: stays
        Near(new Vector3(10.3f, 10.8f, 30), Updates(r).Single().Position!.Value);
        bool before = r.C.SnapEnabled;
        r.C.ToggleSnap();                                              // G
        Assert.Equal(!before, r.C.SnapEnabled);
    }

    [Fact]
    public void Someone_elses_object_has_no_handle_drag_and_nothing_is_sent()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), 0);
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(10, 10), false);
        r.Sent.Clear();
        Assert.False(r.C.BeginManip());
        Assert.False(r.C.ManipMove(new Vector3(1, 0, 0)));
        Assert.False(r.C.SnapSelectionXY());
        Assert.Empty(r.Sent);
    }
}
