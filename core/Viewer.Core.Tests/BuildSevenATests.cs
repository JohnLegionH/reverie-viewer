// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Build;
using Xunit;
using ClickAction = Viewer.Core.Interaction.ClickAction;
using Vec3 = System.Numerics.Vector3;
using Quat = System.Numerics.Quaternion;

namespace Viewer.Core.Tests;

/// <summary>
/// The Create tool's shapes and options, Duplicate, "when left-clicked", For sale, Return and the land impact. Each rule is the official
/// viewer's, named in the source it was written from. No engine and no grid; every test builds its own world, so the class runs in parallel.
/// </summary>
public class BuildSevenATests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly UUID Agent = new("2a4c6e80-1b3d-4f5a-8c7e-9d0f1a2b3c01"), Session = new("2a4c6e80-1b3d-4f5a-8c7e-9d0f1a2b3c02"),
        Group = new("2a4c6e80-1b3d-4f5a-8c7e-9d0f1a2b3c03");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;
    private static readonly Placement Ray = new(new Vec3(1, 2, 3), new Vec3(4, 5, 6), Guid.Empty, true);

    private static ObjectAddPacket Add(CreateShape s, byte species = 0) =>
        LmvBuild.ObjectAdd(Agent, Session, Group, Ray, Vec3.One, 3, s, species);

    // ---------------- Create: the fifteen shapes ----------------

    [Fact]
    public void The_create_tool_has_the_fifteen_official_buttons_in_the_official_order()
    {
        Assert.Equal(["Cube", "Prism", "Pyramid", "Tetrahedron", "Cylinder", "Hemicylinder", "Cone", "Hemicone", "Sphere", "Hemisphere", "Torus", "Tube", "Ring", "Tree", "Grass"],
            CreateShapes.All.Select(s => s.ToString()));
    }

    [Fact]
    public void The_cube_is_the_box_the_create_tool_always_made()
    {
        var d = Add(CreateShape.Cube).ObjectData;
        Assert.Equal(9, d.PCode);
        Assert.Equal(2u, d.AddFlags);
        Assert.Equal((byte)0x10, d.PathCurve);
        Assert.Equal((byte)1, d.ProfileCurve);
        Assert.Equal((byte)100, d.PathScaleX);
        Assert.Equal((byte)100, d.PathScaleY);
        Assert.Equal(0, d.PathShearX);
        Assert.Equal(LibreMetaverse.Quaternion.Identity, d.Rotation);
    }

    [Fact]
    public void Each_volume_is_sent_with_the_profile_path_cut_and_top_size_of_the_official_placer()
    {
        // (shape, profile curve, path curve, path scale x, path scale y, path shear x, profile begin, profile end, path end) as packed on the wire
        (CreateShape, byte, byte, byte, byte, byte, ushort, ushort, ushort)[] expected =
        [
            (CreateShape.Prism, 1, 0x10, 200, 100, 206, 0, 0, 0),          // ratio (0,1), shear (-0.5,0): 200 - 0, 200 - 100, -50 as a byte
            (CreateShape.Pyramid, 1, 0x10, 200, 200, 0, 0, 0, 0),
            (CreateShape.Tetrahedron, 3, 0x10, 200, 200, 0, 0, 0, 0),
            (CreateShape.Cylinder, 0, 0x10, 100, 100, 0, 0, 0, 0),
            (CreateShape.Hemicylinder, 0, 0x10, 100, 100, 0, 12500, 12500, 0),   // profile cut a quarter to three quarters: 0.25/0.00002, 50000 - 37500
            (CreateShape.Cone, 0, 0x10, 200, 200, 0, 0, 0, 0),
            (CreateShape.Hemicone, 0, 0x10, 200, 200, 0, 12500, 12500, 0),
            (CreateShape.Sphere, 5, 0x20, 100, 100, 0, 0, 0, 0),
            (CreateShape.Hemisphere, 5, 0x20, 100, 100, 0, 0, 0, 25000),          // half of the path: 50000 - 25000
            (CreateShape.Torus, 0, 0x20, 100, 175, 0, 0, 0, 0),                   // ratio (1, 0.25): 200 - 25
            (CreateShape.Tube, 1, 0x20, 100, 175, 0, 0, 0, 0),
            (CreateShape.Ring, 3, 0x20, 100, 175, 0, 0, 0, 0),
        ];
        foreach (var (shape, profile, path, sx, sy, shear, pb, pe, pathEnd) in expected)
        {
            var d = Add(shape).ObjectData;
            Assert.True(d.PCode == 9, shape.ToString());
            Assert.True(d.ProfileCurve == profile, shape + " profile");
            Assert.True(d.PathCurve == path, shape + " path");
            Assert.True(d.PathScaleX == sx && d.PathScaleY == sy, shape + " top size");
            Assert.True(d.PathShearX == shear, shape + " shear");
            Assert.True(d.ProfileBegin == pb && d.ProfileEnd == pe, shape + " profile cut");
            Assert.True(d.PathEnd == pathEnd, shape + " path cut");
            Assert.True(d.AddFlags == 2u, shape + " arrives selected");
        }
    }

    [Fact]
    public void A_sphere_and_the_three_torus_kinds_are_made_turned_a_quarter_about_the_Y_axis_and_the_rest_are_not()
    {
        foreach (var shape in new[] { CreateShape.Sphere, CreateShape.Torus, CreateShape.Tube, CreateShape.Ring })
        {
            var r = Add(shape).ObjectData.Rotation;
            Assert.True(Math.Abs(r.Y - MathF.Sin(MathF.PI / 4)) < 1e-5 && Math.Abs(r.W - MathF.Cos(MathF.PI / 4)) < 1e-5 && r.X == 0 && r.Z == 0, shape.ToString());
        }
        foreach (var shape in new[] { CreateShape.Cube, CreateShape.Prism, CreateShape.Cylinder, CreateShape.Hemisphere, CreateShape.Cone })
            Assert.Equal(LibreMetaverse.Quaternion.Identity, Add(shape).ObjectData.Rotation);
    }

    [Fact]
    public void A_tree_and_a_patch_of_grass_are_sent_with_their_pcode_and_species_and_are_not_made_selected()
    {
        var tree = Add(CreateShape.Tree, 7).ObjectData;
        Assert.Equal((byte)0xFF, tree.PCode);
        Assert.Equal(0u, tree.AddFlags);
        Assert.Equal((byte)7, tree.State);
        var grass = Add(CreateShape.Grass, 3).ObjectData;
        Assert.Equal((byte)0x5F, grass.PCode);
        Assert.Equal(0u, grass.AddFlags);
        Assert.Equal((byte)3, grass.State);
        Assert.True(CreateShapes.Of(CreateShape.Cube).CreateSelected);
        Assert.False(CreateShapes.Of(CreateShape.Tree).CreateSelected);
    }

    [Fact]
    public void Grass_has_a_random_size_and_species_a_tree_the_default_half_metre_and_a_volume_species_zero()
    {
        Assert.Equal(new Vec3(10, 10, 1), CreateShapes.PlantScale(CreateShape.Grass, () => 0f));
        Assert.Equal(new Vec3(30, 30, 3), CreateShapes.PlantScale(CreateShape.Grass, () => 1f));
        Assert.Equal(new Vec3(0.5f, 0.5f, 0.5f), CreateShapes.PlantScale(CreateShape.Tree, () => 0.9f));
        Assert.Equal((byte)0, CreateShapes.Species(CreateShape.Tree, () => 0f));
        Assert.Equal((byte)20, CreateShapes.Species(CreateShape.Tree, () => 1f));
        Assert.Equal((byte)5, CreateShapes.Species(CreateShape.Grass, () => 1f));
        Assert.Equal((byte)0, CreateShapes.Species(CreateShape.Cube, () => 0.9f));
    }

    [Fact]
    public void The_create_intent_carries_its_shape_to_the_command_and_a_plain_one_is_still_a_box()
    {
        var plain = (CreateObjectCommand)new CreateBoxIntent(Ray).ToCommand()!;
        Assert.Equal(CreateShape.Cube, plain.Shape);
        var torus = (CreateObjectCommand)new CreateBoxIntent(Ray, null, null, CreateShape.Torus, 0).ToCommand()!;
        Assert.Equal(CreateShape.Torus, torus.Shape);
    }

    // ---------------- Create: the options ----------------

    private sealed class Rig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public EditController C;
        public long Now;

        public Rig()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            World.ApplyAvatar(Sim, new AvatarData(500, G(500), "Me", new Vec3(100, 100, 21), Quat.Identity, new Vec3(0.45f, 0.6f, 1.9f)));
            C = new EditController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, clock: () => Now);
        }

        public void Prim(uint local, uint flags, byte click = 0, int attachment = 0, uint parent = 0, Vec3? at = null) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), attachment != 0 ? 500 : parent, attachment, at ?? new Vec3(10, 10, 30), Quat.Identity, new Vec3(2, 2, 2),
                Shape: Viewer.Core.Meshing.PrimShape.Box, Interaction: new PrimInteraction(click, flags)));

        /// <summary>A stand-in worn prim: the update carries the attachment flag, so the world holds it as worn (the wearer is not modelled).</summary>
        public void Worn(uint local, uint flags) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), 500, 5, new Vec3(1, 0, 0), Quat.Identity, new Vec3(0.2f, 0.2f, 0.2f),
                Shape: Viewer.Core.Meshing.PrimShape.Box, Interaction: new PrimInteraction(0, flags)), eventIsAttachment: true);

        public void Props(uint local, byte saleType, int price, uint ownerMask = Own << 0 | (1u << 13) | (1u << 14) | (1u << 15)) =>
            C.Receive(new ObjectPropertiesReceived(G(local), "Thing", "", Guid.Empty, Guid.Empty, Guid.Empty, null, ownerMask, 0, 0, price, saleType != 0, saleType));

        public void Select(uint local)
        {
            C.SelectMany([local], shift: false);
            Sent.Clear();
        }

        public T Last<T>() where T : ActionIntent => Assert.IsType<T>(Sent[^1]);
    }

    [Fact]
    public void Pressing_a_shape_button_chooses_it_and_turns_Copy_selection_off_while_the_copy_options_start_as_the_official_ones()
    {
        var r = new Rig();
        Assert.Equal(CreateShape.Cube, r.C.ShapeChoice);
        Assert.False(r.C.CopySelection);
        Assert.True(r.C.CopyCenters);
        Assert.False(r.C.CopyRotates);
        r.C.CopySelection = true;
        r.C.ChooseShape(CreateShape.Hemisphere);
        Assert.Equal(CreateShape.Hemisphere, r.C.ShapeChoice);
        Assert.False(r.C.CopySelection);
    }

    [Fact]
    public void Copy_selection_places_a_copy_of_the_selection_on_the_ray_with_the_copy_options_and_not_selected()
    {
        var r = new Rig();
        r.Prim(1, Own);
        r.Prim(2, Own, at: new Vec3(50, 50, 30));
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1, 2], shift: false);
        r.Sent.Clear();
        r.C.CopySelection = true;
        r.C.CopyRotates = true;
        var pick = new PickResult(PickKind.Land, null, null, new Vec3(110, 100, 20), 14f);
        Assert.True(r.C.PlaceCopy(pick, new Vec3(100, 100, 30), Vec3.UnitX, Vec3.UnitX, 0.1f));
        var copy = r.Last<DuplicateOnRayIntent>();
        Assert.Equal([1u, 2u], copy.Roots);
        Assert.True(copy.CopyCenters);
        Assert.True(copy.CopyRotates);
        Assert.True(copy.Placement.BypassRaycast);
        Assert.Equal(0u, copy.Flags);
    }

    [Fact]
    public void Copy_selection_refuses_a_click_that_cannot_place_and_a_worn_selection_and_sends_nothing_for_an_empty_selection()
    {
        var r = new Rig();
        r.Prim(1, Own, attachment: 5);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1], shift: false);
        r.Sent.Clear();
        var land = new PickResult(PickKind.Land, null, null, new Vec3(110, 100, 20), 14f);
        Assert.False(r.C.PlaceCopy(new PickResult(PickKind.None, null, null, default, 0), new Vec3(100, 100, 30), Vec3.UnitX, Vec3.UnitX, 0.1f));
        Assert.False(r.C.PlaceCopy(new PickResult(PickKind.Land, null, null, new Vec3(300, 100, 20), 200f), new Vec3(100, 100, 30), Vec3.UnitX, Vec3.UnitX, 0.1f));
        var empty = new Rig();
        Assert.True(empty.C.PlaceCopy(land, new Vec3(100, 100, 30), Vec3.UnitX, Vec3.UnitX, 0.1f));
        Assert.Empty(empty.Sent);
    }

    [Fact]
    public void The_duplicate_on_ray_message_carries_the_ray_the_two_options_and_the_roots()
    {
        var p = new Placement(new Vec3(1, 2, 3), new Vec3(4, 5, 6), Guid.Parse("11111111-1111-1111-1111-111111111111"), false);
        var m = LmvEdit.DuplicateOnRay(Agent, Session, Group, [7, 9], p, copyCenters: true, copyRotates: false, flags: 0);
        Assert.Equal(new LibreMetaverse.Vector3(1, 2, 3), m.AgentData.RayStart);
        Assert.Equal(new LibreMetaverse.Vector3(4, 5, 6), m.AgentData.RayEnd);
        Assert.False(m.AgentData.BypassRaycast);
        Assert.True(m.AgentData.CopyCenters);
        Assert.False(m.AgentData.CopyRotates);
        Assert.Equal(new UUID("11111111-1111-1111-1111-111111111111"), m.AgentData.RayTargetID);
        Assert.Equal(Group, m.AgentData.GroupID);
        Assert.Equal([7u, 9u], m.ObjectData.Select(o => o.ObjectLocalID));
    }

    // ---------------- Duplicate ----------------

    [Fact]
    public void Duplicate_copies_each_selected_root_half_a_metre_east_and_north_asks_for_the_copy_selected_and_lets_the_originals_go()
    {
        var r = new Rig();
        r.Prim(1, Own);
        r.Prim(2, Own, parent: 1);
        r.Prim(3, Own, at: new Vec3(50, 50, 30));
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1, 3], shift: false);
        r.Sent.Clear();
        Assert.Null(r.C.Duplicate());
        var dup = r.Sent.OfType<DuplicateObjectsIntent>().Single();
        Assert.Equal([1u, 3u], dup.Roots);
        Assert.Equal(new Vec3(0.5f, 0.5f, 0), dup.Offset);
        Assert.Equal(2u, dup.Flags);
        Assert.Contains(r.Sent, i => i is DeselectObjectsIntent { LocalIds: [1, 3] });
        Assert.Empty(r.C.Selection);
    }

    [Fact]
    public void Duplicate_is_on_only_for_a_copyable_selection_and_says_why_it_did_not_copy()
    {
        var r = new Rig();
        r.Prim(1, Own & ~ObjectFlagBits.Copy);
        r.Prim(2, Own, attachment: 5);
        Assert.Equal("duplicate.none", r.C.Duplicate());
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1], shift: false);
        Assert.False(r.C.CanDuplicate);
        Assert.Equal("duplicate.no_copy", r.C.Duplicate());
        Assert.DoesNotContain(r.Sent, i => i is DuplicateObjectsIntent);
    }

    [Fact]
    public void Duplicate_refuses_a_worn_object_and_sends_nothing()
    {
        var r = new Rig();
        r.Worn(7, Own);
        Assert.NotNull(r.World.Object(new ObjectId(r.World.Epoch, 7))!.AttachmentPoint);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([7], shift: false);
        Assert.Equal([7u], r.C.Selection);   // a worn object can be selected, as in the official viewer
        r.Sent.Clear();
        Assert.False(r.C.CanDuplicate);
        Assert.Equal("duplicate.worn", r.C.Duplicate());
        Assert.Empty(r.Sent);
        // a worn object chosen together with a copyable one still stops the whole copy (selectDuplicate: isAttachment)
        r.Prim(1, Own);
        r.C.SelectMany([1], shift: true);
        r.Sent.Clear();
        Assert.Equal("duplicate.worn", r.C.Duplicate());
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void A_copy_that_arrives_made_selected_joins_the_selection_and_one_that_arrives_too_late_does_not()
    {
        var r = new Rig();
        r.Prim(1, Own);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1], shift: false);
        Assert.Null(r.C.Duplicate());
        r.Prim(9, Own | ObjectFlagBits.CreateSelected, at: new Vec3(10.5f, 10.5f, 30));
        Assert.True(r.C.CopyArrived(r.World.Object(new ObjectId(r.World.Epoch, 9))!));
        Assert.Equal([9u], r.C.Selection);
        // nothing more is expected
        r.Prim(10, Own | ObjectFlagBits.CreateSelected);
        Assert.False(r.C.CopyArrived(r.World.Object(new ObjectId(r.World.Epoch, 10))!));
        var late = new Rig();
        late.Prim(1, Own);
        late.C.SelectTool(BuildTool.Edit);
        late.C.SelectMany([1], shift: false);
        Assert.Null(late.C.Duplicate());
        late.Now = 60_000;
        late.Prim(9, Own | ObjectFlagBits.CreateSelected);
        Assert.False(late.C.CopyArrived(late.World.Object(new ObjectId(late.World.Epoch, 9))!));
    }

    [Fact]
    public void The_Build_menu_has_Object_then_Duplicate_on_Ctrl_D_enabled_by_a_copyable_selection_and_Ctrl_D_is_no_longer_listed_as_missing()
    {
        var menus = new MenuModel();
        var r = new Rig();
        r.Prim(1, Own);
        BuildMenu.InstallObject(menus, () => true, () => r.C);
        var entry = menus.Menus.Single(m => m.Id == BuildMenu.MenuId).Entries.Single(e => e.Id == BuildMenu.ObjectId);
        var dup = entry.Children!.Single(c => c.Id == BuildMenu.DuplicateId);
        Assert.Equal("control|D", dup.Shortcut);
        Assert.False(dup.Enabled());
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1], shift: false);
        Assert.True(dup.Enabled());
        Assert.DoesNotContain(ViewerShortcuts.NotBuilt, n => n.Shortcut == "control|D");
    }

    [Fact]
    public void A_Shift_drag_leaves_its_copy_only_once_the_pointer_leaves_the_two_pixel_slop()
    {
        Assert.False(EditController.LeavesDragSlop(100, 100, 100, 100));
        Assert.False(EditController.LeavesDragSlop(100, 100, 101, 101));
        Assert.True(EditController.LeavesDragSlop(100, 100, 102, 100));
        Assert.True(EditController.LeavesDragSlop(100, 100, 100, 98));
        var r = new Rig();
        r.Prim(1, Own);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1], shift: false);
        r.Sent.Clear();
        Assert.True(r.C.CopyInPlace());
        var dup = r.Last<DuplicateObjectsIntent>();
        Assert.Equal(Vec3.Zero, dup.Offset);
        Assert.Equal(0u, dup.Flags);   // the copy stays behind and the originals stay selected
    }

    // ---------------- when left-clicked ----------------

    [Fact]
    public void The_click_list_has_the_official_choices_and_maps_them_to_the_protocol_actions()
    {
        Assert.Equal(["Touch", "Sit", "Buy", "Pay", "Open", "Zoom", "Ignore", "None"], ClickChoices.All.Select(c => c.ToString()));
        Assert.Equal<byte>([0, 1, 2, 3, 4, 7, 9, 8], ClickChoices.All.Select(ClickChoices.ActionOf));
        foreach (var c in ClickChoices.All)
            Assert.Equal(c, ClickChoices.ChoiceOf(ClickChoices.ActionOf(c)));
        // play and open media are not in the official list: they show as Touch
        Assert.Equal(ClickChoice.Touch, ClickChoices.ChoiceOf(ClickAction.Play));
        Assert.Equal(ClickChoice.Touch, ClickChoices.ChoiceOf(ClickAction.OpenMedia));
    }

    [Fact]
    public void Buy_is_refused_for_an_object_not_for_sale_and_Pay_is_set_with_a_warning_when_no_script_takes_money()
    {
        Assert.Equal((false, "edit.click.cant_buy"), ClickChoices.Check(ClickChoice.Buy, forSale: false, payable: true));
        Assert.Equal((true, (string?)null), ClickChoices.Check(ClickChoice.Buy, forSale: true, payable: false));
        Assert.Equal((true, "edit.click.not_payable"), ClickChoices.Check(ClickChoice.Pay, forSale: false, payable: false));
        Assert.Equal((true, (string?)null), ClickChoices.Check(ClickChoice.Pay, forSale: false, payable: true));
        Assert.Equal((true, (string?)null), ClickChoices.Check(ClickChoice.Zoom, forSale: false, payable: false));
    }

    [Fact]
    public void The_click_action_goes_to_every_prim_of_the_selected_linkset_and_the_list_is_on_only_for_what_the_agent_may_modify()
    {
        var r = new Rig();
        r.Prim(1, Own);
        r.Prim(2, Own, parent: 1);
        r.Prim(3, ObjectFlagBits.Copy);
        r.Prim(1, Own);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1], shift: false);
        r.Props(1, 0, 10);
        r.Sent.Clear();
        Assert.True(r.C.ClickChoiceEnabled);
        Assert.Equal((true, (string?)null), r.C.SetClickChoice(ClickChoice.Zoom));
        var set = r.Last<SetClickActionIntent>();
        Assert.Equal(7, set.Action);
        Assert.Equal([1u, 2u], set.LocalIds.OrderBy(i => i));
        r.C.SelectMany([3], shift: false);
        Assert.False(r.C.ClickChoiceEnabled);
        r.Sent.Clear();
        Assert.False(r.C.SetClickChoice(ClickChoice.Zoom).Sent);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Choosing_Buy_for_an_object_not_for_sale_sends_nothing_and_says_why_but_for_sale_it_sends()
    {
        var r = new Rig();
        r.Prim(1, Own);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1], shift: false);
        r.Props(1, 0, 10);
        r.Sent.Clear();
        Assert.Equal((false, "edit.click.cant_buy"), r.C.SetClickChoice(ClickChoice.Buy));
        Assert.Empty(r.Sent);
        r.Props(1, 2, 25);
        Assert.True(r.C.SetClickChoice(ClickChoice.Buy).Sent);
        Assert.Equal(2, r.Last<SetClickActionIntent>().Action);
    }

    [Fact]
    public void The_click_action_message_has_one_block_for_each_object_with_the_action()
    {
        var m = LmvEdit.ClickAction(Agent, Session, [4, 5, 6], 7);
        Assert.Equal([4u, 5u, 6u], m.ObjectData.Select(o => o.ObjectLocalID));
        Assert.All(m.ObjectData, o => Assert.Equal((byte)7, o.ClickAction));
    }

    [Fact]
    public void A_left_click_on_a_Zoom_object_zooms_and_a_Play_or_Open_Media_one_is_not_handled_yet()
    {
        var zoom = new PrimInteraction(ClickAction.Zoom, Own);
        Assert.Equal(LeftClick.Zoom, ClickAction.Left(zoom, zoom, false, false));
        Assert.Equal(HoverCursor.Zoom, ClickAction.Cursor(zoom, zoom, false, false));
        var play = new PrimInteraction(ClickAction.Play, Own);
        Assert.Equal(LeftClick.Unhandled, ClickAction.Left(play, play, false, false));
    }

    [Fact]
    public void A_click_on_a_Zoom_object_asks_the_host_to_frame_it_and_sends_nothing()
    {
        var c = new Clicks();
        c.World.ApplyObject(Sim, new PrimData(1, G(1), 0, 0, new Vec3(10, 10, 30), Quat.Identity, new Vec3(2, 2, 2), Shape: Viewer.Core.Meshing.PrimShape.Box,
            Interaction: new PrimInteraction(ClickAction.Zoom, 0)));
        ObjectState? framed = null;
        c.C.ZoomRequested += o => framed = o;
        c.C.LeftDown(ObjectPicker.Pick(c.World, new PickRay(new Vec3(10, 10, 60), new Vec3(0, 0, -1)), null));
        Assert.Equal(1u, framed?.Id.LocalId);
        Assert.Empty(c.Sent);
    }

    [Fact]
    public void The_zoom_distance_is_twice_the_box_over_the_tangent_of_the_angle_of_view_widened_by_the_aspect()
    {
        float fov = MathF.PI / 3;
        Assert.Equal(new Vec3(3, 4, 0).Length() * 2f / MathF.Atan(fov * 2f), ClickChoices.ZoomDistance(new Vec3(3, 4, 0), fov, 2f), 4);
        Assert.Equal(new Vec3(3, 4, 0).Length() * 2f / MathF.Atan(fov), ClickChoices.ZoomDistance(new Vec3(3, 4, 0), fov, 0.8f), 4);
        Assert.Equal(2f / MathF.Atan(0.1f), ClickChoices.ZoomDistance(Vec3.UnitX, 0.01f, 0.5f), 4);   // the angle is never below 0.1
    }

    [Fact]
    public void A_picked_object_set_to_Ignore_is_passed_over_when_the_pick_asks_for_that()
    {
        var r = new Rig();
        r.Prim(1, Own, click: ClickAction.Ignore);
        var ray = new PickRay(new Vec3(10, 10, 60), new Vec3(0, 0, -1));
        Assert.Equal(PickKind.Object, ObjectPicker.Pick(r.World, ray, null).Kind);
        Assert.Equal(PickKind.None, ObjectPicker.Pick(r.World, ray, null, skip: o => o.Interaction?.ClickAction == ClickAction.Ignore).Kind);
    }

    // ---------------- for sale ----------------

    private const uint Transfer = 1u << 13;
    private static SaleNode N(SaleKind t, int price, uint mask = Transfer) => new(t, price, mask);

    [Fact]
    public void For_sale_shows_the_roots_sale_type_and_price_and_defaults_the_type_to_Copy_when_nothing_is_for_sale()
    {
        var one = SaleRules.ViewOf([N(SaleKind.Contents, 40)], canChange: true, allTransfer: true);
        Assert.True(one.ForSale);
        Assert.Equal(40, one.Price);
        Assert.Equal(SaleKind.Contents, one.Type);
        Assert.True(one.BoxEnabled && one.PriceEnabled && one.TypeEnabled);
        var off = SaleRules.ViewOf([N(SaleKind.NotForSale, 10)], true, true);
        Assert.False(off.ForSale);
        Assert.Equal(SaleKind.Copy, off.Type);
        Assert.True(off.BoxEnabled);
        Assert.True(off.PriceEnabled);
        Assert.False(off.TypeEnabled);   // the type is chosen once the box is ticked
    }

    [Fact]
    public void For_sale_is_undecided_for_mixed_roots_and_the_price_and_type_wait_for_agreeing_prices()
    {
        var mixed = SaleRules.ViewOf([N(SaleKind.Copy, 5), N(SaleKind.NotForSale, 5)], true, true);
        Assert.True(mixed.Mixed);
        Assert.Equal(0, mixed.Price);
        Assert.False(mixed.PriceEnabled);
        var prices = SaleRules.ViewOf([N(SaleKind.Copy, 5), N(SaleKind.Copy, 9)], true, true);
        Assert.True(prices.PriceMixed);
        Assert.False(prices.TypeEnabled);
        Assert.True(prices.PriceEnabled);
    }

    [Fact]
    public void For_sale_is_off_for_what_the_agent_does_not_own_or_cannot_transfer_except_to_take_an_old_sale_off()
    {
        Assert.False(SaleRules.ViewOf([N(SaleKind.NotForSale, 0)], canChange: false, true).BoxEnabled);
        Assert.False(SaleRules.ViewOf([N(SaleKind.NotForSale, 0, mask: 0)], true, true).BoxEnabled);
        Assert.False(SaleRules.ViewOf([N(SaleKind.NotForSale, 0)], true, allTransfer: false).BoxEnabled);
        var old = SaleRules.ViewOf([N(SaleKind.Copy, 3)], true, allTransfer: false);
        Assert.True(old.BoxEnabled);
        Assert.False(old.TypeEnabled);
    }

    [Fact]
    public void The_sale_sent_is_the_chosen_type_when_ticked_else_not_for_sale_with_the_default_price_for_an_undecided_one_and_off_for_a_negative_one()
    {
        Assert.Equal((SaleKind.Original, 99), SaleRules.Commit(true, SaleKind.Original, 99));
        Assert.Equal((SaleKind.NotForSale, 99), SaleRules.Commit(false, SaleKind.Original, 99));
        Assert.Equal((SaleKind.Copy, 10), SaleRules.Commit(true, SaleKind.Copy, null));
        Assert.Equal((SaleKind.NotForSale, 0), SaleRules.Commit(true, SaleKind.Copy, -4));
        Assert.Equal((SaleKind.Copy, 0), SaleRules.Commit(true, SaleKind.Copy, 0));   // free is allowed: it gives the buy window
    }

    [Fact]
    public void The_sale_info_message_has_one_block_for_each_root_with_the_type_and_price()
    {
        var m = LmvEdit.SaleInfo(Agent, Session, [3, 8], 2, 150);
        Assert.Equal([3u, 8u], m.ObjectData.Select(o => o.LocalID));
        Assert.All(m.ObjectData, o => { Assert.Equal((byte)2, o.SaleType); Assert.Equal(150, o.SalePrice); });
    }

    [Fact]
    public void Putting_an_object_on_sale_sends_the_sale_and_changes_a_Touch_click_to_Buy_and_taking_it_off_changes_it_back()
    {
        var r = new Rig();
        r.Prim(1, Own);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1], shift: false);
        r.Props(1, 0, 10);
        r.Sent.Clear();
        Assert.True(r.C.SetSale(true, SaleKind.Copy, 75));
        var sale = r.Sent.OfType<SetObjectSaleIntent>().Single();
        Assert.Equal([1u], sale.Roots);
        Assert.Equal(2, sale.SaleType);
        Assert.Equal(75, sale.Price);
        Assert.Equal(2, r.Sent.OfType<SetClickActionIntent>().Single().Action);
        Assert.Equal(SaleKind.Copy, r.C.SaleState()!.Type);
        // an object whose click is Sit keeps it
        var sit = new Rig();
        sit.Prim(1, Own, click: ClickAction.Sit);
        sit.C.SelectTool(BuildTool.Edit);
        sit.C.SelectMany([1], shift: false);
        sit.Props(1, 0, 10);
        sit.Sent.Clear();
        Assert.True(sit.C.SetSale(true, SaleKind.Copy, 5));
        Assert.Empty(sit.Sent.OfType<SetClickActionIntent>());
        Assert.Null(ClickChoices.AfterSaleChange([ClickAction.Touch, ClickAction.Buy], true, false) is { } x && x != ClickAction.Touch ? "wrong" : null);
    }

    [Fact]
    public void The_sale_is_refused_for_an_object_that_is_not_the_agents()
    {
        var r = new Rig();
        r.Prim(1, ObjectFlagBits.Copy | ObjectFlagBits.Transfer);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1], shift: false);
        r.Props(1, 0, 10);
        r.Sent.Clear();
        Assert.False(r.C.SetSale(true, SaleKind.Copy, 5));
        Assert.Empty(r.Sent);
    }

    // ---------------- return ----------------

    [Fact]
    public void Return_is_offered_for_any_object_that_is_not_worn_and_asks_first()
    {
        var r = new Rig();
        var plain = ContextMenus.ForObject(new ObjectMenuContext(new PrimInteraction(0, Own), new PrimInteraction(0, Own), false, false, false));
        var entry = plain.Single(e => e.Id == "return");
        Assert.True(entry.Enabled);
        Assert.Equal(["return/question", "return/confirm"], entry.Children!.Select(c => c.Id));
        Assert.False(entry.Children![0].Enabled);   // the question is text
        Assert.True(entry.Children![1].Enabled);
        // another's object too: the region decides
        var other = ContextMenus.ForObject(new ObjectMenuContext(new PrimInteraction(0, 0), new PrimInteraction(0, 0), false, false, false));
        Assert.True(other.Single(e => e.Id == "return").Enabled);
        var worn = ContextMenus.ForObject(new ObjectMenuContext(new PrimInteraction(0, Own), new PrimInteraction(0, Own), true, false, false));
        Assert.DoesNotContain(worn, e => e.Id == "return");
        Assert.True(BuildRules.CanReturn(false));
        Assert.False(BuildRules.CanReturn(true));
        Assert.NotNull(r);
    }

    [Fact]
    public void Return_sends_a_derez_to_the_return_destination_with_the_active_group_in_packets_of_250_that_count_each_other()
    {
        var txn = UUID.Random();
        var one = LmvBuild.ReturnToOwner(Agent, Session, Group, [42], txn);
        var p = Assert.Single(one);
        Assert.Equal((byte)9, p.AgentBlock.Destination);
        Assert.Equal(UUID.Zero, p.AgentBlock.DestinationID);
        Assert.Equal(Group, p.AgentBlock.GroupID);
        Assert.Equal(txn, p.AgentBlock.TransactionID);
        Assert.Equal([42u], p.ObjectData.Select(o => o.ObjectLocalID));
        var many = LmvBuild.ReturnToOwner(Agent, Session, Group, [.. Enumerable.Range(1, 251).Select(i => (uint)i)], txn);
        Assert.Equal(2, many.Count);
        Assert.Equal(250, many[0].ObjectData.Length);
        Assert.Single(many[1].ObjectData);
        Assert.All(many, m => Assert.Equal((byte)2, m.AgentBlock.PacketCount));
        Assert.Equal([(byte)0, (byte)1], many.Select(m => m.AgentBlock.PacketNumber));
    }

    // ---------------- land impact ----------------

    [Fact]
    public void Land_impact_adds_each_distinct_roots_linkset_cost_skips_worn_ones_counts_unanswered_as_zero_and_drops_the_fraction()
    {
        var costs = new Dictionary<uint, float> { [1] = 2.6f, [2] = 3.7f };
        float total = LandImpact.Total([(1u, false), (1u, false), (2u, false), (3u, false), (2u, true)], r => costs.TryGetValue(r, out var c) ? c : null);
        Assert.Equal(6.3f, total, 3);
        Assert.Equal(6, LandImpact.Shown(total));
        Assert.Equal(0f, LandImpact.Total([(5u, true)], _ => 9f));
    }

    [Fact]
    public void The_object_cost_answer_is_read_from_its_two_keys_and_an_error_or_a_missing_entry_gives_nothing()
    {
        var id = Guid.NewGuid();
        var ok = new OSDMap { [id.ToString()] = new OSDMap { ["resource_cost"] = 1.5, ["linked_set_resource_cost"] = 4.25, ["physics_cost"] = 9.0 } };
        var read = Assert.Single(LmvFeatures.ParseCosts(ok));
        Assert.Equal((id, 1.5f, 4.25f), read);
        Assert.Empty(LmvFeatures.ParseCosts(new OSDMap { ["error"] = new OSDMap() }));
        Assert.Empty(LmvFeatures.ParseCosts(null));
    }

    [Fact]
    public void The_selection_shows_its_land_impact_once_the_region_answers_and_asks_for_it_when_an_object_is_selected()
    {
        var r = new Rig();
        r.Prim(1, Own);
        r.Prim(2, Own, parent: 1);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1], shift: false);
        var asked = r.Sent.OfType<RequestObjectCostIntent>().Single();
        Assert.Equal([G(1)], asked.ObjectIds);
        Assert.Equal((1, 0), r.C.LandImpactShown());   // not answered yet: 0
        r.C.Receive(new ObjectCostReceived(G(1), 1f, 12.9f));
        Assert.Equal((1, 12), r.C.LandImpactShown());
        r.C.Deselect();
        Assert.Null(r.C.LandImpactShown());
    }

    // ---------------- the create click through the interaction controller ----------------

    private sealed class Clicks
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public InteractionController C;
        public int Boxes;

        public Clicks()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            C = new InteractionController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, G(900),
                new SocialHooks(_ => { }, _ => { }, _ => { }, _ => { }, () => { }), _ => false);
            C.BoxCreated += () => Boxes++;
        }

        public bool Place(CreateShape shape, Func<float>? random = null)
        {
            C.BeginCreate();
            return C.Place(new PickResult(PickKind.Land, null, null, new Vec3(110, 100, 20), 14f), new Vec3(100, 100, 30), Vec3.UnitX, Vec3.UnitX, 0.1f, shape, random);
        }
    }

    [Fact]
    public void Placing_a_torus_sends_that_shape_and_expects_the_new_prim_settings_while_placing_grass_sends_a_random_patch_and_does_not()
    {
        var c = new Clicks();
        Assert.True(c.Place(CreateShape.Torus));
        var torus = Assert.IsType<CreateBoxIntent>(c.Sent[^1]);
        Assert.Equal(CreateShape.Torus, torus.Shape);
        Assert.Equal(1, c.Boxes);
        Assert.True(c.Place(CreateShape.Grass, () => 1f));
        var grass = Assert.IsType<CreateBoxIntent>(c.Sent[^1]);
        Assert.Equal(CreateShape.Grass, grass.Shape);
        Assert.Equal(new Vec3(30, 30, 3), grass.Scale);
        Assert.Equal((byte)5, grass.Species);
        Assert.Equal(1, c.Boxes);   // a plant does not arrive selected, so no settings wait for it
        Assert.True(c.Place(CreateShape.Tree, () => 0f));
        Assert.Equal(new Vec3(0.5f, 0.5f, 0.5f), Assert.IsType<CreateBoxIntent>(c.Sent[^1]).Scale);
    }

    [Fact]
    public void The_Return_entry_sends_the_return_for_the_clicked_objects_root_only_from_its_confirm_entry()
    {
        var c = new Clicks();
        c.World.ApplyObject(Sim, new PrimData(1, G(1), 0, 0, new Vec3(10, 10, 30), Quat.Identity, new Vec3(2, 2, 2), Shape: Viewer.Core.Meshing.PrimShape.Box,
            Interaction: new PrimInteraction(0, 0)));
        var pick = ObjectPicker.Pick(c.World, new PickRay(new Vec3(10, 10, 60), new Vec3(0, 0, -1)), null);
        c.C.RightClick(pick);
        Assert.False(c.C.Choose("return"));
        Assert.DoesNotContain(c.Sent, i => i is ReturnObjectsIntent);
        c.C.RightClick(pick);
        Assert.True(c.C.Choose("return/confirm"));
        Assert.Equal([1u], Assert.IsType<ReturnObjectsIntent>(c.Sent[^1]).Roots);
    }
}
