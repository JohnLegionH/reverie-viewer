// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Avatars;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Meshing;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Mirror's shape step: which shape parameters of a prim are handed and how each changes when the prim is reflected about one of its own
/// middle planes (one table per parameter), checked against the real mesh (the reflected vertices of the old shape are the vertices of the
/// new one); the sculpt's Mirror bit; the notice naming parts that keep their handedness; the sculpt block as sent; and Mirror a copy.
/// Every test builds its own world, so the class runs in parallel.
/// </summary>
public class MirrorShapeTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("2a4c6e80-1b3d-4f5a-8c7e-9d0f1a2b3c01");
    private static readonly Guid MapId = Guid.Parse("5b7d9f01-2c4e-4a68-8b0d-1e3f5a7c9d02");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;
    private static readonly PrimShape Box = PrimShape.Box;
    private static readonly PrimShape Cylinder = new() { ProfileCurve = 0, PathCurve = 16 };

    private static void Near(double want, double got, double tol = 1e-9) => Assert.True(Math.Abs(want - got) <= tol, $"want {want}, got {got}");

    private static List<Vector3> Vertices(PrimShape s) =>
        [.. PrimGeometry.Build(s, PrimDetail.High).SelectMany(f => Enumerable.Range(0, f.Positions.Length / 3).Select(i => new Vector3(f.Positions[3 * i], f.Positions[3 * i + 1], f.Positions[3 * i + 2])))];

    private static Vector3 Reflected(Vector3 v, int axis) => axis == 0 ? new(-v.X, v.Y, v.Z) : axis == 1 ? new(v.X, -v.Y, v.Z) : new(v.X, v.Y, -v.Z);

    private static bool SameSurface(List<Vector3> a, List<Vector3> b) =>
        a.All(p => b.Any(q => Vector3.Distance(p, q) < 3e-3f)) && b.All(q => a.Any(p => Vector3.Distance(p, q) < 3e-3f));

    // ---------------- one table per shape parameter ----------------

    [Fact]
    public void Twist_is_negated_about_a_profile_plane_and_swapped_end_for_end_about_the_path_plane()
    {
        var s = Box with { PathTwistBegin = 0.2, PathTwist = 0.5 };
        foreach (int axis in new[] { 0, 1 })
        {
            var f = ShapeMirror.Flip(s, axis)!;
            Near(-0.2, f.PathTwistBegin); Near(-0.5, f.PathTwist);
        }
        var z = ShapeMirror.Flip(s, 2)!;
        Near(0.5, z.PathTwistBegin); Near(0.2, z.PathTwist);
    }

    [Fact]
    public void Top_shear_negates_on_its_own_axis_and_cannot_be_flipped_about_the_path_plane()
    {
        var s = Box with { PathShearX = 0.3, PathShearY = -0.2 };
        var x = ShapeMirror.Flip(s, 0)!;
        Near(-0.3, x.PathShearX); Near(-0.2, x.PathShearY);
        var y = ShapeMirror.Flip(s, 1)!;
        Near(0.3, y.PathShearX); Near(0.2, y.PathShearY);
        Assert.Null(ShapeMirror.Flip(s, 2));   // the top moves, the bottom stays: the image needs the whole prim to move too
    }

    [Fact]
    public void Taper_of_a_straight_path_is_reversed_about_the_path_plane_and_kept_about_the_others()
    {
        var s = Box with { PathScaleX = 0.6, PathScaleY = 1.3 };   // taper is 1 minus the path scale
        var z = ShapeMirror.Flip(s, 2)!;
        Near(1.4, z.PathScaleX); Near(0.7, z.PathScaleY);
        foreach (int axis in new[] { 0, 1 })
        {
            var f = ShapeMirror.Flip(s, axis)!;
            Near(0.6, f.PathScaleX); Near(1.3, f.PathScaleY);
        }
    }

    [Fact]
    public void Path_cut_is_reversed_about_the_path_plane_and_kept_about_the_others()
    {
        var s = Box with { PathBegin = 0.1, PathEnd = 0.8 };
        var z = ShapeMirror.Flip(s, 2)!;
        Near(0.2, z.PathBegin); Near(0.9, z.PathEnd);
        foreach (int axis in new[] { 0, 1 })
        {
            var f = ShapeMirror.Flip(s, axis)!;
            Near(0.1, f.PathBegin); Near(0.8, f.PathEnd);
        }
    }

    [Fact]
    public void Profile_cut_is_reflected_about_a_profile_plane_and_kept_about_the_path_plane()
    {
        // a circle profile starts at the Y end of its outline: about its X plane a cut [b, e] becomes [0.5 - e, 0.5 - b], about its Y plane [1 - e, 1 - b]
        var s = Cylinder with { ProfileBegin = 0.1, ProfileEnd = 0.4 };
        var x = ShapeMirror.Flip(s, 0)!;
        Near(0.1, x.ProfileBegin); Near(0.4, x.ProfileEnd);
        var y = ShapeMirror.Flip(s, 1)!;
        Near(0.6, y.ProfileBegin); Near(0.9, y.ProfileEnd);
        var z = ShapeMirror.Flip(s, 2)!;
        Near(0.1, z.ProfileBegin); Near(0.4, z.ProfileEnd);
        // a square profile starts a quarter of the way round: about X a cut [0.25, 0.5] becomes [0.75, 1] (taken round the profile once)
        var box = Box with { ProfileBegin = 0.25, ProfileEnd = 0.5 };
        var bx = ShapeMirror.Flip(box, 0)!;
        Near(0.75, bx.ProfileBegin); Near(1.0, bx.ProfileEnd);
        var by = ShapeMirror.Flip(box, 1)!;
        Near(0.25, by.ProfileBegin); Near(0.5, by.ProfileEnd);
    }

    [Fact]
    public void A_profile_cut_that_would_run_past_the_end_cannot_be_flipped_about_the_plane_that_does_it()
    {
        var s = Cylinder with { ProfileBegin = 0.3, ProfileEnd = 0.7 };
        Assert.Null(ShapeMirror.Flip(s, 0));
        Assert.NotNull(ShapeMirror.Flip(s, 1));
    }

    [Fact]
    public void A_triangle_profile_has_no_image_about_its_x_plane()
    {
        var prism = ShapeRules.OfKind(PrimKind.Prism);
        Assert.Null(ShapeMirror.Flip(prism, 0));
        Assert.Equal(prism, ShapeMirror.Flip(prism, 1));
        Assert.Equal(prism, ShapeMirror.Flip(prism, 2));
        Assert.Null(ShapeMirror.Flip(Cylinder with { ProfileHollow = 0.4, ProfileHole = ShapeRules.HoleTriangle }, 0));
    }

    [Fact]
    public void Hollow_hole_and_profile_type_are_not_handed()
    {
        var s = Cylinder with { ProfileHollow = 0.5, ProfileHole = 32 };
        foreach (int axis in new[] { 0, 1, 2 })
            Assert.Equal(s, ShapeMirror.Flip(s, axis));
    }

    [Fact]
    public void A_plain_prim_is_its_own_image_about_every_plane()
    {
        foreach (var s in new[] { Box, Cylinder, ShapeRules.OfKind(PrimKind.Sphere), ShapeRules.OfKind(PrimKind.Torus) })
            foreach (int axis in new[] { 0, 1, 2 })
                Assert.Equal(s, ShapeMirror.Flip(s, axis));
    }

    [Fact]
    public void A_curved_path_with_handed_settings_is_not_flipped_and_says_so()
    {
        var torus = ShapeRules.OfKind(PrimKind.Torus) with { PathTwist = 0.3 };
        foreach (int axis in new[] { 0, 1, 2 })
            Assert.Null(ShapeMirror.Flip(torus, axis));
    }

    [Fact]
    public void A_flexible_path_is_not_flipped()
    {
        Assert.Null(ShapeMirror.Flip(Box with { PathCurve = 128, PathTwist = 0.1 }, 2));
    }

    // ---------------- against the real mesh ----------------

    public static IEnumerable<object[]> Shapes()
    {
        var shapes = new (string, PrimShape)[]
        {
            ("box", Box),
            ("box twist", Box with { PathTwistBegin = 0.2, PathTwist = 0.5 }),
            ("box twist opposed", Box with { PathTwistBegin = -0.3, PathTwist = 0.6 }),
            ("box shear", Box with { PathShearX = 0.3, PathShearY = -0.2 }),
            ("box taper", Box with { PathScaleX = 0.6, PathScaleY = 1.3 }),
            ("box path cut", Box with { PathBegin = 0.1, PathEnd = 0.8 }),
            ("box path cut and twist", Box with { PathBegin = 0.25, PathEnd = 0.75, PathTwistBegin = 0.1, PathTwist = 0.4 }),
            ("box profile cut", Box with { ProfileBegin = 0.25, ProfileEnd = 0.5 }),
            ("box profile cut low", Box with { ProfileBegin = 0.0, ProfileEnd = 0.25 }),
            ("box everything", Box with { PathTwistBegin = 0.1, PathTwist = -0.4, PathShearX = 0.1, PathShearY = 0.2, PathScaleX = 0.8, PathScaleY = 0.5, PathBegin = 0.1, PathEnd = 0.9, ProfileBegin = 0.0, ProfileEnd = 0.5 }),
            ("cylinder twist", Cylinder with { PathTwistBegin = 0.2, PathTwist = 0.5 }),
            ("cylinder profile cut", Cylinder with { ProfileBegin = 0.1, ProfileEnd = 0.4 }),
            ("cylinder hollow", Cylinder with { ProfileHollow = 0.5, ProfileHole = 32, PathTwist = 0.25 }),
            ("prism", ShapeRules.OfKind(PrimKind.Prism)),
            ("prism twist", ShapeRules.OfKind(PrimKind.Prism) with { PathTwistBegin = 0.2, PathTwist = 0.5 }),
            ("prism profile cut", ShapeRules.OfKind(PrimKind.Prism) with { ProfileBegin = 0.0, ProfileEnd = 0.5 }),
            ("prism hollow", ShapeRules.OfKind(PrimKind.Prism) with { ProfileHollow = 0.4 }),
            ("cylinder triangle hole", Cylinder with { ProfileHollow = 0.4, ProfileHole = ShapeRules.HoleTriangle }),
            ("box circle hole", Box with { ProfileHollow = 0.4, ProfileHole = ShapeRules.HoleCircle, PathTwist = 0.3 }),
            ("sphere", ShapeRules.OfKind(PrimKind.Sphere)),
            ("torus", ShapeRules.OfKind(PrimKind.Torus)),
        };
        foreach (var (name, shape) in shapes)
            foreach (int axis in new[] { 0, 1, 2 })
                yield return [name, shape, axis];
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_flipped_shape_is_the_reflection_of_the_old_shapes_mesh(string name, PrimShape shape, int axis)
    {
        var flipped = ShapeMirror.Flip(shape, axis);
        if (flipped is null)
            return;   // declared not flippable: the next test pins which ones must be
        var want = Vertices(shape).Select(v => Reflected(v, axis)).ToList();
        Assert.True(SameSurface(want, Vertices(flipped)), $"{name} about axis {axis}");
    }

    [Theory]
    [InlineData("box twist", 0)] [InlineData("box twist", 1)] [InlineData("box twist", 2)]
    [InlineData("box shear", 0)] [InlineData("box shear", 1)]
    [InlineData("box taper", 0)] [InlineData("box taper", 2)]
    [InlineData("box path cut", 1)] [InlineData("box path cut", 2)]
    [InlineData("box profile cut", 0)] [InlineData("box profile cut low", 1)]
    [InlineData("cylinder twist", 2)] [InlineData("cylinder profile cut", 0)]
    public void These_shapes_can_be_flipped_about_these_planes(string name, int axis)
    {
        var shape = Shapes().Select(o => (Name: (string)o[0], Shape: (PrimShape)o[1], Axis: (int)o[2])).First(o => o.Name == name && o.Axis == axis).Shape;
        Assert.NotNull(ShapeMirror.Flip(shape, axis));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Flipping_twice_gives_the_shape_back(string name, PrimShape shape, int axis)
    {
        if (ShapeMirror.Flip(shape, axis) is not { } once)
            return;
        var twice = ShapeMirror.Flip(once, axis)!;
        Near(shape.PathBegin, twice.PathBegin); Near(shape.PathEnd, twice.PathEnd);
        Near(shape.ProfileBegin, twice.ProfileBegin); Near(shape.ProfileEnd, twice.ProfileEnd);
        Near(shape.PathTwist, twice.PathTwist); Near(shape.PathTwistBegin, twice.PathTwistBegin);
        Near(shape.PathShearX, twice.PathShearX); Near(shape.PathShearY, twice.PathShearY);
        Near(shape.PathScaleX, twice.PathScaleX); Near(shape.PathScaleY, twice.PathScaleY);
    }

    // ---------------- sculpts and meshes ----------------

    [Fact]
    public void A_sculpts_mirror_bit_is_toggled_about_its_x_plane_and_only_there()
    {
        var sculpt = new PrimShape { ProfileCurve = 0, PathCurve = 32, SculptType = SculptMesher.Sphere, SculptId = MapId };
        var on = ShapeMirror.Flip(sculpt, 0)!;
        Assert.Equal(SculptMesher.FlagMirror, on.SculptFlags);
        Assert.Equal(sculpt.SculptId, on.SculptId);
        Assert.Equal(sculpt.SculptType, on.SculptType);
        Assert.Equal(0, ShapeMirror.Flip(on, 0)!.SculptFlags);
        var keepsInvert = ShapeMirror.Flip(sculpt with { SculptFlags = SculptMesher.FlagInvert }, 0)!;
        Assert.Equal(SculptMesher.FlagInvert | SculptMesher.FlagMirror, keepsInvert.SculptFlags);
        Assert.Null(ShapeMirror.Flip(sculpt, 1));
        Assert.Null(ShapeMirror.Flip(sculpt, 2));
    }

    [Fact]
    public void A_mesh_cannot_be_flipped()
    {
        var mesh = Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = MapId };
        foreach (int axis in new[] { 0, 1, 2 })
            Assert.Null(ShapeMirror.Flip(mesh, axis));
    }

    [Fact]
    public void The_sculpt_block_carries_the_map_and_the_type_with_its_flags_and_reads_back()
    {
        var packet = LmvFeatures.Sculpt(new LibreMetaverse.UUID(Me), new LibreMetaverse.UUID(Me), 7, MapId, SculptMesher.Sphere | SculptMesher.FlagMirror);
        var block = Assert.Single(packet.ObjectData);
        Assert.Equal(7u, block.ObjectLocalID);
        Assert.Equal(LmvFeatures.ParamSculpt, block.ParamType);
        Assert.True(block.ParamInUse);
        Assert.Equal(17u, block.ParamSize);
        var back = LmvTransport.ShapeFromSculptBlock(block.ParamData);
        Assert.Equal(MapId, back.SculptId);
        Assert.Equal(SculptMesher.Sphere, back.SculptType);
        Assert.Equal(SculptMesher.FlagMirror, back.SculptFlags);
    }

    // ---------------- the controller ----------------

    private sealed class Rig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public EditController C;

        public Rig()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new EditController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, () => EditLimits.Official,
                regions: () => [new(0, 0, 256, 256)]);
            C.SelectTool(BuildTool.Edit);
        }

        public void Prim(uint local, Vector3 pos, PrimShape? shape, uint parent = 0, uint flags = Own, Quaternion? turn = null) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), parent, 0, pos, turn ?? Quaternion.Identity, new Vector3(1, 1, 1), Shape: shape, Interaction: new PrimInteraction(0, flags)));

        public ObjectState Obj(uint local) => World.Object(new ObjectId(World.Epoch, local))!;
        public void Pick(uint local) => C.SelectMany([local], shift: false);
        public IEnumerable<T> All<T>() where T : ActionIntent => Sent.OfType<T>();
    }

    [Fact]
    public void Mirror_sends_the_flipped_shape_of_a_twisted_sheared_box_about_the_plane_that_keeps_it_exact()
    {
        var r = new Rig();
        var twisted = Box with { PathTwistBegin = 0.2, PathTwist = 0.5, PathShearX = 0.3 };
        r.Prim(1, new Vector3(20, 30, 40), twisted);
        r.Pick(1);
        Assert.True(r.C.Mirror(ArrangeAxis.Z, MirrorPlane.SelectionCentre));
        var sent = Assert.Single(r.All<SetObjectShapeIntent>());
        Assert.Equal(1u, sent.LocalId);
        // the shear forbids the path plane, so the prim turns about a profile plane instead: the shape sent is that flip
        Assert.Equal(ShapeRules.Encode(ShapeMirror.Flip(twisted, 0)!), sent.Shape);
    }

    [Fact]
    public void Mirror_about_x_flips_the_shape_about_the_plane_the_part_already_lies_across()
    {
        var r = new Rig();
        var twisted = Box with { PathTwistBegin = 0.2, PathTwist = 0.5 };
        r.Prim(1, new Vector3(20, 30, 40), twisted);
        r.Pick(1);
        Assert.True(r.C.Mirror(ArrangeAxis.X, MirrorPlane.SelectionCentre));
        Assert.Equal(ShapeRules.Encode(ShapeMirror.Flip(twisted, 0)!), Assert.Single(r.All<SetObjectShapeIntent>()).Shape);
    }

    [Fact]
    public void A_symmetric_part_sends_no_shape_message()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(20, 30, 40), Box);
        r.Prim(2, new Vector3(2, 1, 0), Cylinder, parent: 1);
        r.Pick(1);
        Assert.True(r.C.Mirror(ArrangeAxis.X, MirrorPlane.SelectionCentre));
        Assert.Empty(r.All<SetObjectShapeIntent>());
        Assert.Empty(r.C.MirrorNotice);
    }

    [Fact]
    public void A_sculpt_gets_its_mirror_bit_toggled_and_its_map_resent()
    {
        var r = new Rig();
        var sculpt = new PrimShape { ProfileCurve = 0, PathCurve = 32, SculptType = SculptMesher.Torus, SculptId = MapId };
        r.Prim(1, new Vector3(20, 30, 40), Box);
        r.Prim(2, new Vector3(3, 0, 0), sculpt, parent: 1);
        r.Pick(1);
        Assert.True(r.C.Mirror(ArrangeAxis.X, MirrorPlane.SelectionCentre));
        var sent = Assert.Single(r.All<SetSculptIntent>());
        Assert.Equal(2u, sent.LocalId);
        Assert.Equal(MapId, sent.Texture);
        Assert.Equal(SculptMesher.Torus | SculptMesher.FlagMirror, sent.TypeAndFlags);
        Assert.Empty(r.All<SetObjectShapeIntent>());
    }

    [Fact]
    public void A_sculpt_mirrored_about_a_y_plane_is_flipped_about_x_and_turned_half_over()
    {
        var r = new Rig();
        var sculpt = new PrimShape { ProfileCurve = 0, PathCurve = 32, SculptType = SculptMesher.Sphere, SculptId = MapId };
        r.Prim(1, new Vector3(20, 30, 40), sculpt);
        r.Pick(1);
        Assert.True(r.C.Mirror(ArrangeAxis.Y, MirrorPlane.SelectionCentre));
        Assert.Equal(SculptMesher.FlagMirror, Assert.Single(r.All<SetSculptIntent>()).TypeAndFlags & SculptMesher.FlagMirror);
        var turn = Assert.Single(r.All<UpdateObjectIntent>(), u => u.Rotation is not null).Rotation!.Value;
        Assert.True(MathF.Abs(MathF.Abs(turn.Z) - 1f) < 1e-4f, $"half a turn about the local z axis, got {turn}");
    }

    [Fact]
    public void A_mesh_part_is_placed_but_named_in_the_notice_and_so_is_a_prim_that_cannot_flip()
    {
        var r = new Rig();
        var mesh = Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = MapId };
        var torus = ShapeRules.OfKind(PrimKind.Torus) with { PathTwist = 0.3 };
        r.Prim(1, new Vector3(20, 30, 40), Box);
        r.Prim(2, new Vector3(3, 0, 0), mesh, parent: 1);
        r.Prim(3, new Vector3(-3, 0, 0), torus, parent: 1);
        r.Pick(1);
        Assert.True(r.C.Mirror(ArrangeAxis.X, MirrorPlane.SelectionCentre));
        Assert.Equal(2, r.C.MirrorNotice.Count);
        Assert.Contains(r.C.MirrorNotice, n => n.Contains('2'));
        Assert.Contains(r.C.MirrorNotice, n => n.Contains('3'));
        Assert.Empty(r.All<SetObjectShapeIntent>());
        Assert.Contains(r.All<UpdateObjectIntent>(), u => u.LocalId == 2);   // still placed
    }

    [Fact]
    public void The_notice_is_closed_by_OK()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(20, 30, 40), Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = MapId });
        r.Pick(1);
        Assert.False(r.C.Mirror(ArrangeAxis.X, MirrorPlane.SelectionCentre));   // a lone mesh on the plane through its middle: nothing to send, yet it is named
        Assert.NotEmpty(r.C.MirrorNotice);
        r.C.CloseMirrorNotice();
        Assert.Empty(r.C.MirrorNotice);
    }

    [Fact]
    public void Mirror_a_copy_duplicates_in_place_then_mirrors_the_copy_about_the_original_selections_plane()
    {
        var r = new Rig();
        var twisted = Box with { PathTwistBegin = 0.2, PathTwist = 0.5 };
        r.Prim(1, new Vector3(20, 30, 40), twisted);
        r.Pick(1);
        Assert.True(r.C.MirrorCopy(ArrangeAxis.X, MirrorPlane.SelectionCentre));
        var dup = Assert.Single(r.All<DuplicateObjectsIntent>());
        Assert.Equal(Vector3.Zero, dup.Offset);
        Assert.Equal([1u], dup.Roots);
        Assert.Equal(EditController.LmvDuplicateCreateSelected, dup.Flags);
        Assert.Empty(r.All<UpdateObjectIntent>());   // nothing is mirrored until the copy is there
        Assert.Empty(r.All<SetObjectShapeIntent>());
        // the region sends the copy, selected, where the original stands
        r.Prim(9, new Vector3(20, 30, 40), twisted, flags: Own | ObjectFlagBits.CreateSelected);
        Assert.True(r.C.CopyArrived(r.Obj(9)));
        Assert.Empty(r.All<UpdateObjectIntent>());   // a lone unturned prim on the plane through its own middle stays as it is
        var shape = Assert.Single(r.All<SetObjectShapeIntent>());
        Assert.Equal(9u, shape.LocalId);   // the original is left alone
        Assert.Equal(ShapeRules.Encode(ShapeMirror.Flip(twisted, 0)!), shape.Shape);
    }

    [Fact]
    public void Mirror_a_copy_waits_for_every_part_of_the_copy_and_puts_the_plane_through_the_originals()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(20, 30, 40), Box);
        r.Prim(2, new Vector3(4, 0, 0), Box, parent: 1);
        r.Pick(1);
        Assert.True(r.C.MirrorCopy(ArrangeAxis.X, MirrorPlane.SelectionCentre));
        r.Prim(9, new Vector3(20, 30, 40), Box, flags: Own | ObjectFlagBits.CreateSelected);
        Assert.True(r.C.CopyArrived(r.Obj(9)));
        Assert.Empty(r.All<UpdateObjectIntent>());   // the copy's part has not arrived yet
        r.Prim(10, new Vector3(4, 0, 0), Box, parent: 9);
        r.C.CopyArrived(r.Obj(10));
        // the plane goes through the middle of the original linkset (x = 22), so the copy's root lands at x = 24
        var root = Assert.Single(r.All<UpdateObjectIntent>(), u => u.LocalId == 9 && u.Position is not null);
        Assert.True(MathF.Abs(root.Position!.Value.X - 24f) < 1e-3f, $"root x {root.Position}");
        Assert.Contains(r.All<UpdateObjectIntent>(), u => u.LocalId == 10);
    }

    [Fact]
    public void Mirror_a_copy_is_refused_where_Mirror_or_Duplicate_is()
    {
        var none = new Rig();
        Assert.False(none.C.MirrorCopy(ArrangeAxis.X, MirrorPlane.SelectionCentre));
        var noCopy = new Rig();
        noCopy.Prim(1, new Vector3(20, 30, 40), Box, flags: Own & ~ObjectFlagBits.Copy);
        noCopy.Pick(1);
        Assert.False(noCopy.C.MirrorCopy(ArrangeAxis.X, MirrorPlane.SelectionCentre));
        Assert.Empty(noCopy.All<DuplicateObjectsIntent>());
        Assert.False(noCopy.C.CanMirrorCopy);
        var ok = new Rig();
        ok.Prim(1, new Vector3(20, 30, 40), Box);
        ok.Pick(1);
        Assert.True(ok.C.CanMirrorCopy);
    }
}
