// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text.Json;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Texture tab's edits and the Object and Texture tabs' copy and paste, after the official viewer's LLPanelFace, LLToolFace and
/// LLPanelObject (read as behaviour only): Select Face, the face settings sent for the selected faces only, repeats per metre,
/// Align planar faces, PBR materials applied and removed with their texture transforms, and copy and paste. No engine and no grid;
/// each test builds its own world, so the class runs in parallel.
/// </summary>
public class FaceEditTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("2a4c6e80-1b3d-4f5a-8c7e-9d0f1a2b3c01");
    private static readonly Guid Wood = Guid.Parse("8955f4e3-71f0-4b39-9c1e-3f2a6b1d0c11");
    private static readonly Guid Stone = Guid.Parse("1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5");
    private static readonly Guid Gold = Guid.Parse("6e7d8c9b-0a1f-4e2d-9c3b-4a5f6e7d8c9b");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;

    private sealed class Rig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public EditController C;

        public Rig()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new EditController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, () => EditLimits.Official);
        }

        public void Prim(uint local, Vector3 pos, uint flags = Own, uint parent = 0, Vector3? scale = null, Quaternion? rot = null, FaceTextures? tex = null,
            FaceColors? colours = null) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), parent, 0, pos, rot ?? Quaternion.Identity, scale ?? new Vector3(2, 2, 2),
                Colors: colours, Textures: tex, Interaction: new PrimInteraction(0, flags)));

        /// <summary>A pick straight down onto the top face at x, y.</summary>
        public PickResult At(float x, float y) => ObjectPicker.Pick(World, new PickRay(new Vector3(x, y, 60), new Vector3(0, 0, -1)), (_, _) => 20f, ignoreAgent: Me);

        public SetObjectFacesIntent[] Faces() => Sent.OfType<SetObjectFacesIntent>().ToArray();
    }

    private static void Near(float want, float got, float tol = 1e-3f) => Assert.True(MathF.Abs(want - got) <= tol, $"want {want}, got {got}");

    // ---------------- Select Face (lltoolface.cpp) ----------------

    [Fact]
    public void Select_Face_picks_the_clicked_face_Shift_adds_and_removes_faces_and_a_click_on_nothing_clears()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30));
        r.Prim(3, new Vector3(14, 10, 30));
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectFaces = true;
        Assert.True(r.C.Click(r.At(10, 10), shift: false));
        Assert.Equal(new[] { (1u, 0) }, r.C.SelectedFaces);
        Assert.Equal(new[] { 1u }, r.C.Selection);
        Assert.True(r.C.Click(r.At(14, 10), shift: true));            // another object's face joins
        Assert.Equal(new[] { (1u, 0), (3u, 0) }, r.C.SelectedFaces);
        Assert.True(r.C.Click(r.At(14, 10), shift: true));            // and leaves again
        Assert.Equal(new[] { (1u, 0) }, r.C.SelectedFaces);
        Assert.True(r.C.Click(r.At(50, 50), shift: false));
        Assert.Empty(r.C.SelectedFaces);
        Assert.Empty(r.C.Selection);
    }

    [Fact]
    public void Select_Face_picks_a_face_of_a_linked_part_and_Shift_takes_faces_across_the_linkset()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30));
        r.Prim(2, new Vector3(4, 0, 0), parent: 1);                   // in the world at (14, 10, 30)
        r.C.SelectTool(BuildTool.Edit);
        r.C.EditLinked = true;
        r.C.SelectFaces = true;
        Assert.True(r.C.Click(r.At(14, 10), shift: false));
        Assert.Equal(new[] { (2u, 0) }, r.C.SelectedFaces);
        Assert.True(r.C.Click(r.At(10, 10), shift: true));
        Assert.Equal(new[] { (2u, 0), (1u, 0) }, r.C.SelectedFaces);
    }

    // ---------------- face settings sent for the selected faces only ----------------

    [Fact]
    public void An_edit_on_selected_faces_sends_those_faces_alone_with_their_other_settings_kept()
    {
        var r = new Rig();
        var tex = new FaceTextures(FaceTexture.Plain(Wood), [null, FaceTexture.Plain(Stone) with { RepeatU = 3 }]);
        r.Prim(1, new Vector3(10, 10, 30), tex: tex, colours: new FaceColors(new Rgba(1, 1, 1, 1)));
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectFaces = true;
        r.C.Click(r.At(10, 10), shift: false);
        r.Sent.Clear();
        Assert.True(r.C.EditFaces(new FaceChange(TextureId: Gold, Glow: 0.3f, Transparency: 40)));
        var sent = Assert.Single(r.Faces());
        Assert.Equal(1u, sent.LocalId);
        var face = Assert.Single(sent.Faces);
        Assert.Equal(0, face.Face);
        Assert.Equal(Gold, face.Settings.TextureId);
        Near(0.3f, face.Settings.Glow);
        Near(0.6f, face.Settings.Colour.A);
        Near(1f, face.Settings.Colour.R);
        Near(1f, face.Settings.RepeatU);                               // face 0 keeps its own mapping

        r.C.SelectFaces = false;                                       // the whole object: every face, each keeping its own
        r.Sent.Clear();
        Assert.True(r.C.EditFaces(new FaceChange(Fullbright: true, TexGen: TexGen.Planar)));
        var all = Assert.Single(r.Faces());
        Assert.Equal(Enumerable.Range(0, 6), all.Faces.Select(f => f.Face));
        Assert.All(all.Faces, f => Assert.True(f.Settings.Fullbright && f.Settings.TexGen == TexGen.Planar));
        Assert.Equal(Stone, all.Faces[1].Settings.TextureId);
        Near(3f, all.Faces[1].Settings.RepeatU);
        Assert.Equal(Wood, all.Faces[2].Settings.TextureId);
    }

    [Fact]
    public void Colour_rotation_offset_and_repeats_are_set_as_typed_and_a_face_without_modify_is_left_alone()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), tex: new FaceTextures(FaceTexture.Plain(Wood)), colours: new FaceColors(new Rgba(1, 1, 1, 0.5f)));
        r.Prim(3, new Vector3(14, 10, 30), flags: ObjectFlagBits.Move);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectFaces = true;
        r.C.Click(r.At(10, 10), shift: false);
        r.C.Click(r.At(14, 10), shift: true);
        r.Sent.Clear();
        Assert.True(r.C.EditFaces(new FaceChange(Colour: new Vector3(1, 0, 0), RotationDegrees: 90, OffsetU: 0.25f, OffsetV: -0.5f, RepeatU: 2, RepeatV: 4)));
        var sent = Assert.Single(r.Faces());                           // the one without modify sends nothing
        var s = Assert.Single(sent.Faces).Settings;
        Assert.Equal(new Rgba(1, 0, 0, 0.5f), s.Colour);               // colour keeps the alpha
        Near(MathF.PI / 2, s.Rotation);
        Assert.Equal((0.25f, -0.5f, 2f, 4f), (s.OffsetU, s.OffsetV, s.RepeatU, s.RepeatV));
    }

    [Theory]
    [InlineData(0, 3f, 6f)]       // top: x by y
    [InlineData(1, 3f, 0.75f)]    // x by z
    [InlineData(2, 6f, 0.75f)]    // y by z
    [InlineData(5, 3f, 6f)]       // bottom
    public void Repeats_per_metre_use_the_faces_own_two_sides_of_the_object(int face, float u, float v)
    {
        // LLPrimitive::getTESTAxes: face 0 and 5 and up are x by y, 1 and 3 x by z, 2 and 4 y by z
        var (ru, rv) = FaceRules.RepeatsForPerMetre(1.5f, face, new Vector3(2, 4, 0.5f));
        Near(u, ru);
        Near(v, rv);
        Near(1.5f, FaceRules.PerMetre(ru, face, new Vector3(2, 4, 0.5f)));
    }

    // ---------------- Align planar faces (llface.cpp calcAlignedPlanarTE as behaviour) ----------------

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(30f, 0f)]
    [InlineData(30f, 90f)]
    [InlineData(-45f, 15f)]
    public void Align_planar_faces_makes_the_texture_run_on_from_the_last_selected_face_across_the_others(float anchorDegrees, float objectTurn)
    {
        var r = new Rig();
        var anchor = new FaceTexture(Wood, 2, 1.5f, 0.1f, 0.3f, anchorDegrees * MathF.PI / 180f, TexGen.Planar, false, 0);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, objectTurn * MathF.PI / 180f);
        r.Prim(3, new Vector3(12.3f, 10.4f, 30), tex: new FaceTextures(FaceTexture.Plain(Wood) with { TexGen = TexGen.Planar }), rot: turn);
        r.Prim(1, new Vector3(10, 10, 30), tex: new FaceTextures(anchor));
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectFaces = true;
        r.C.Click(r.At(12.3f, 10.4f), shift: false);
        r.C.Click(r.At(10, 10), shift: true);                          // the last selected face is the one the others follow
        r.Sent.Clear();
        Assert.True(r.C.AlignPlanarFaces());
        var sent = Assert.Single(r.Faces());
        Assert.Equal(3u, sent.LocalId);
        var aligned = Assert.Single(sent.Faces).Settings;
        var a = r.World.Snapshot().Objects.Values.Single(o => o.Id.LocalId == 1);
        var b = r.World.Snapshot().Objects.Values.Single(o => o.Id.LocalId == 3);
        // every point of the shared plane gets the same texture place on both faces, up to whole repeats
        foreach (var p in new[] { new Vector3(11, 10.5f, 31), new Vector3(12.9f, 9.1f, 31), new Vector3(11.6f, 11.2f, 31), new Vector3(10.2f, 9.6f, 31) })
        {
            var ua = FaceRules.PlanarUv(a.Position, a.Rotation, Vector3.UnitZ, FaceRules.Settings(a, 0), p);
            var ub = FaceRules.PlanarUv(b.Position, b.Rotation, Vector3.UnitZ, aligned, p);
            float du = ua.X - ub.X, dv = ua.Y - ub.Y;
            Assert.True(MathF.Abs(du - MathF.Round(du)) < 2e-3f && MathF.Abs(dv - MathF.Round(dv)) < 2e-3f, $"at {p}: {ua} vs {ub}");
        }
        Assert.InRange(aligned.OffsetU, -1f, 1f);
        Assert.InRange(aligned.OffsetV, -1f, 1f);
    }

    [Fact]
    public void Align_planar_faces_leaves_faces_that_are_not_planar()
    {
        var r = new Rig();
        r.Prim(3, new Vector3(12.3f, 10, 30), tex: new FaceTextures(FaceTexture.Plain(Wood)));
        r.Prim(1, new Vector3(10, 10, 30), tex: new FaceTextures(FaceTexture.Plain(Wood) with { TexGen = TexGen.Planar }));
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectFaces = true;
        r.C.Click(r.At(12.3f, 10), shift: false);
        r.C.Click(r.At(10, 10), shift: true);
        r.Sent.Clear();
        Assert.False(r.C.AlignPlanarFaces());
        Assert.Empty(r.Faces());
    }

    // ---------------- PBR materials (ModifyMaterialParams, as the official viewer's LLGLTFMaterialList sends them) ----------------

    [Fact]
    public void A_material_is_applied_to_and_removed_from_the_selected_faces_only()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30));
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectFaces = true;
        r.C.Click(r.At(10, 10), shift: false);
        r.Sent.Clear();
        Assert.True(r.C.ApplyMaterial(Gold));
        var put = Assert.IsType<SetFaceMaterialIntent>(Assert.Single(r.Sent));
        Assert.Equal((G(1), Gold), (put.ObjectId, put.MaterialId));
        Assert.Equal(new[] { 0 }, put.Faces);
        Assert.Null(put.OverrideJson);
        r.Sent.Clear();
        Assert.True(r.C.RemoveMaterial());
        var off = Assert.IsType<SetFaceMaterialIntent>(Assert.Single(r.Sent));
        Assert.Equal(new[] { 0 }, off.Faces);
        Assert.Equal(Guid.Empty, off.MaterialId);
    }

    [Fact]
    public void A_PBR_texture_transform_is_sent_as_the_faces_override_on_every_map()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30));
        r.World.ApplyRenderMaterials(Sim, 1, [new RenderMaterialEntry(0, Gold)]);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectFaces = true;
        r.C.Click(r.At(10, 10), shift: false);
        r.Sent.Clear();
        Assert.True(r.C.SetMaterialTransform(new TextureTransform(0.25f, 0.5f, 2, 3, 0.5f)));
        var put = Assert.IsType<SetFaceMaterialIntent>(Assert.Single(r.Sent));
        Assert.Equal(Gold, put.MaterialId);                            // the material stays
        using var doc = JsonDocument.Parse(put.OverrideJson!);
        var m = doc.RootElement.GetProperty("materials")[0];
        foreach (var slot in new[] { m.GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture"), m.GetProperty("pbrMetallicRoughness").GetProperty("metallicRoughnessTexture"),
                     m.GetProperty("normalTexture"), m.GetProperty("emissiveTexture") })
        {
            var t = slot.GetProperty("extensions").GetProperty("KHR_texture_transform");
            Near(0.25f, t.GetProperty("offset")[0].GetSingle());
            Near(0.5f, t.GetProperty("offset")[1].GetSingle());
            Near(2f, t.GetProperty("scale")[0].GetSingle());
            Near(3f, t.GetProperty("scale")[1].GetSingle());
            Near(0.5f, t.GetProperty("rotation").GetSingle());
        }
        Assert.Equal(new TextureTransform(0.25f, 0.5f, 2, 3, 0.5f), PbrMaterial.FromGltf(put.OverrideJson!)?.BaseColorTransform);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(35f)]
    public void Align_planar_faces_lines_up_PBR_faces_through_their_texture_transforms_too(float objectTurn)
    {
        // the official viewer leaves PBR faces out of Align planar faces (secondlife/viewer issue 2975); here their glTF texture
        // transforms are lined up the same way, so the anchor's material runs on across the other face
        var r = new Rig();
        var planar = new FaceTextures(FaceTexture.Plain(Wood) with { TexGen = TexGen.Planar });
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, objectTurn * MathF.PI / 180f);
        r.Prim(3, new Vector3(12.3f, 10.4f, 30), tex: planar, rot: turn);
        r.Prim(1, new Vector3(10, 10, 30), tex: planar);
        r.World.ApplyRenderMaterials(Sim, 3, [new RenderMaterialEntry(0, Gold)]);
        r.World.ApplyRenderMaterials(Sim, 1, [new RenderMaterialEntry(0, Gold)]);
        r.World.ApplyMaterialOverrides(Sim, 1, [new FaceMaterialOverride(0, "{\"ti\":[{\"o\":[0.2,0.1],\"s\":[1.5,1.5],\"r\":0.4}]}")]);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectFaces = true;
        r.C.Click(r.At(12.3f, 10.4f), shift: false);
        r.C.Click(r.At(10, 10), shift: true);
        r.Sent.Clear();
        Assert.True(r.C.AlignPlanarFaces());
        var put = Assert.Single(r.Sent.OfType<SetFaceMaterialIntent>());
        Assert.Equal((3u, Gold), (put.LocalId, put.MaterialId));
        var aligned = PbrMaterial.FromGltf(put.OverrideJson!)!.BaseColorTransform;
        var anchorT = new TextureTransform(0.2f, 0.1f, 1.5f, 1.5f, 0.4f);
        var a = r.World.Snapshot().Objects.Values.Single(o => o.Id.LocalId == 1);
        var b = r.World.Snapshot().Objects.Values.Single(o => o.Id.LocalId == 3);
        foreach (var p in new[] { new Vector3(11, 10.5f, 31), new Vector3(12.9f, 9.1f, 31), new Vector3(11.6f, 11.2f, 31) })
        {
            var ua = FaceRules.PlanarPbrUv(a.Position, a.Rotation, Vector3.UnitZ, anchorT, p);
            var ub = FaceRules.PlanarPbrUv(b.Position, b.Rotation, Vector3.UnitZ, aligned, p);
            float du = ua.X - ub.X, dv = ua.Y - ub.Y;
            Assert.True(MathF.Abs(du - MathF.Round(du)) < 2e-3f && MathF.Abs(dv - MathF.Round(dv)) < 2e-3f, $"at {p}: {ua} vs {ub}");
        }
    }

    [Fact]
    public void A_face_without_a_material_gets_no_transform_and_nothing_is_sent()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30));
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectFaces = true;
        r.C.Click(r.At(10, 10), shift: false);
        r.Sent.Clear();
        Assert.False(r.C.SetMaterialTransform(new TextureTransform(0, 0, 2, 2, 0)));
        Assert.Empty(r.Sent);
    }

    // ---------------- copy and paste (llpanelobject.cpp, llpanelface.cpp) ----------------

    [Fact]
    public void Size_copied_from_one_box_and_pasted_onto_another_is_sent_for_the_other()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), scale: new Vector3(3, 1.5f, 0.25f));
        r.Prim(3, new Vector3(14, 10, 30), scale: new Vector3(1, 1, 1));
        r.C.SelectTool(BuildTool.Edit);
        Assert.False(r.C.CanPaste(ObjectClip.Size));
        r.C.Click(r.At(10, 10), shift: false);
        Assert.Equal("<3, 1.5, 0.25>", r.C.Copy(ObjectClip.Size));
        Assert.True(r.C.CanPaste(ObjectClip.Size));
        r.C.Click(r.At(14, 10), shift: false);
        r.Sent.Clear();
        Assert.True(r.C.Paste(ObjectClip.Size));
        var u = Assert.Single(r.Sent.OfType<UpdateObjectIntent>());
        Assert.Equal((3u, new Vector3(3, 1.5f, 0.25f)), (u.LocalId, u.Scale!.Value));
    }

    [Fact]
    public void Position_rotation_and_all_three_are_copied_and_pasted_and_a_pasted_position_stays_in_the_region()
    {
        var r = new Rig();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        r.Prim(1, new Vector3(250, 10, 30), scale: new Vector3(3, 1, 1), rot: turn);
        r.Prim(3, new Vector3(14, 10, 30));
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(250, 10), shift: false);
        Assert.Equal("<250, 10, 30>", r.C.Copy(ObjectClip.Position));
        Assert.Equal("<0, 0, 90>", r.C.Copy(ObjectClip.Rotation));
        r.C.Copy(ObjectClip.All);
        r.C.Click(r.At(14, 10), shift: false);
        r.Sent.Clear();
        Assert.True(r.C.Paste(ObjectClip.Rotation));
        var u = r.Sent.OfType<UpdateObjectIntent>().Last();
        Assert.True(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, u.Rotation!.Value)) < 1e-3f);
        r.Sent.Clear();
        Assert.True(r.C.Paste(ObjectClip.All));                       // position, size and rotation
        var ups = r.Sent.OfType<UpdateObjectIntent>().ToArray();
        Assert.Contains(ups, x => x.Position == new Vector3(250, 10, 30));
        Assert.Contains(ups, x => x.Scale == new Vector3(3, 1, 1));
        Assert.Contains(ups, x => x.Rotation is { } q && Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, q)) < 1e-3f);
    }

    [Fact]
    public void The_objects_parameters_are_copied_and_pasted_as_its_shape()
    {
        var r = new Rig();
        var tube = ShapeRules.OfKind(PrimKind.Tube) with { ProfileHollow = 0.5 };
        r.World.ApplyObject(Sim, new PrimData(1, G(1), 0, 0, new Vector3(10, 10, 30), Quaternion.Identity, new Vector3(2, 2, 2), Shape: tube, Interaction: new PrimInteraction(0, Own)));
        r.Prim(3, new Vector3(14, 10, 30));
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(10, 10), shift: false);
        r.C.Copy(ObjectClip.Parameters);
        r.C.Click(r.At(14, 10), shift: false);
        r.Sent.Clear();
        Assert.True(r.C.Paste(ObjectClip.Parameters));
        var s = Assert.IsType<SetObjectShapeIntent>(Assert.Single(r.Sent));
        Assert.Equal(3u, s.LocalId);
        Assert.Equal(ShapeRules.Encode(ShapeRules.Clamp(tube)), s.Shape);
    }

    [Fact]
    public void One_copied_face_pastes_onto_every_selected_face_and_a_whole_object_onto_one_with_as_many_faces()
    {
        var r = new Rig();
        var shiny = new FaceTexture(Gold, 2, 3, 0.1f, 0.2f, 0.5f, TexGen.Planar, true, 0.4f);
        r.Prim(1, new Vector3(10, 10, 30), tex: new FaceTextures(FaceTexture.Plain(Wood), [null, shiny]), colours: new FaceColors(new Rgba(0, 1, 0, 0.75f)));
        r.Prim(3, new Vector3(14, 10, 30));
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectFaces = true;
        r.C.Click(r.At(10, 10), shift: false);
        Assert.False(r.C.CanPasteFaces);
        Assert.True(r.C.CopyFaces());
        Assert.True(r.C.CanPasteFaces);
        r.C.SelectFaces = false;
        r.C.Click(r.At(14, 10), shift: false);
        r.Sent.Clear();
        Assert.True(r.C.PasteFaces());                                 // one face copied: every face of the object takes it
        var all = Assert.Single(r.Faces());
        Assert.Equal(6, all.Faces.Count);
        Assert.All(all.Faces, f => Assert.Equal(Wood, f.Settings.TextureId));
        Assert.All(all.Faces, f => Assert.Equal(new Rgba(0, 1, 0, 0.75f), f.Settings.Colour));

        r.C.Click(r.At(10, 10), shift: false);                         // a whole object: face by face
        Assert.True(r.C.CopyFaces());
        r.C.Click(r.At(14, 10), shift: false);
        r.Sent.Clear();
        Assert.True(r.C.PasteFaces());
        var each = Assert.Single(r.Faces());
        Assert.Equal(Gold, each.Faces.Single(f => f.Face == 1).Settings.TextureId);
        Assert.True(each.Faces.Single(f => f.Face == 1).Settings.Fullbright);
        Assert.Equal(Wood, each.Faces.Single(f => f.Face == 0).Settings.TextureId);
    }
}
