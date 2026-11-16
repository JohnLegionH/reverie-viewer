// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
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
/// The Build window's texture, material and colour pickers, after the official viewer's LLFloaterTexturePicker and LLFloaterColorPicker
/// (read as behaviour only): a choice is applied as it is made (the official Apply now, on by default), OK keeps it, and Cancel or closing
/// the picker any other way puts back what each face had when the picker opened (the official selectionRevertTextures and
/// selectionRevertColors, each face its own). No engine and no grid; each test builds its own world, so the class runs in parallel.
/// </summary>
public class PickerSessionTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("4b7e2c19-8d3a-4f6e-a1b2-c3d4e5f60718");
    private static readonly Guid Wood = Guid.Parse("0d9f3a71-2c4b-4e85-9a6d-7b1c2e3f4a50");
    private static readonly Guid Stone = Guid.Parse("a3e5c7f9-1b2d-4c6e-8f0a-2b4d6f8a0c1e");
    private static readonly Guid Gold = Guid.Parse("5f1e2d3c-4b5a-4987-a6b5-c4d3e2f1a0b9");
    private static readonly Guid Brick = Guid.Parse("e9d8c7b6-a5f4-4e3d-8c2b-1a0f9e8d7c6b");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;

    // ---------------- the session: apply as chosen, OK keeps, Cancel or close puts back ----------------

    private sealed class Calls
    {
        public List<Guid> Applied = new();
        public int Reverted;
        public PickSession<Guid> Session;
        public Calls() => Session = new PickSession<Guid>(Applied.Add, () => Reverted++);
    }

    [Fact]
    public void A_choice_is_applied_as_it_is_made()
    {
        var c = new Calls();
        c.Session.Choose(Wood);
        c.Session.Choose(Stone);
        Assert.Equal(new[] { Wood, Stone }, c.Applied);
        Assert.Equal(0, c.Reverted);
    }

    [Fact]
    public void OK_keeps_the_choice_and_the_close_that_follows_puts_nothing_back()
    {
        var c = new Calls();
        c.Session.Choose(Wood);
        c.Session.Ok();
        c.Session.Closed();
        Assert.Equal(0, c.Reverted);
        Assert.True(c.Session.Kept);
        Assert.True(c.Session.Ended);
    }

    [Fact]
    public void Cancel_puts_back_once_and_the_close_that_follows_does_not_put_back_again()
    {
        var c = new Calls();
        c.Session.Choose(Wood);
        c.Session.Cancel();
        c.Session.Closed();
        Assert.Equal(1, c.Reverted);
        Assert.False(c.Session.Kept);
        Assert.True(c.Session.Ended);
    }

    [Fact]
    public void Closing_the_picker_without_OK_puts_back()
    {
        var c = new Calls();
        c.Session.Choose(Wood);
        c.Session.Closed();
        Assert.Equal(1, c.Reverted);
        Assert.True(c.Session.Ended);
    }

    [Fact]
    public void A_choice_after_the_picker_has_ended_is_not_applied()
    {
        var c = new Calls();
        c.Session.Choose(Wood);
        c.Session.Cancel();
        c.Session.Choose(Stone);
        Assert.Equal(new[] { Wood }, c.Applied);
    }

    // ---------------- what is put back: each face its own ----------------

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

        public void Prim(uint local, Vector3 pos, FaceTextures? tex = null, FaceColors? colours = null) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), 0, 0, pos, Quaternion.Identity, new Vector3(2, 2, 2),
                Colors: colours, Textures: tex, Interaction: new PrimInteraction(0, Own)));

        public PickResult At(float x, float y) => ObjectPicker.Pick(World, new PickRay(new Vector3(x, y, 60), new Vector3(0, 0, -1)), (_, _) => 20f, ignoreAgent: Me);
    }

    [Fact]
    public void Putting_back_textures_gives_each_face_the_texture_it_had_when_the_picker_opened()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), tex: new FaceTextures(FaceTexture.Plain(Wood), [null, FaceTexture.Plain(Stone) with { RepeatU = 3 }]));
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(10, 10), shift: false);
        var saved = r.C.SaveFacePicks();
        r.C.EditFaces(new FaceChange(TextureId: Gold));
        r.Sent.Clear();
        Assert.True(r.C.RevertTextures(saved));
        var sent = Assert.Single(r.Sent.OfType<SetObjectFacesIntent>());
        Assert.Equal(Enumerable.Range(0, 6), sent.Faces.Select(f => f.Face));
        Assert.Equal(Wood, sent.Faces[0].Settings.TextureId);
        Assert.Equal(Stone, sent.Faces[1].Settings.TextureId);
        Assert.Equal(3f, sent.Faces[1].Settings.RepeatU);
        Assert.Equal(Wood, sent.Faces[2].Settings.TextureId);
    }

    [Fact]
    public void Putting_back_colours_gives_each_object_the_colour_it_had_and_keeps_its_transparency()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), colours: new FaceColors(new Rgba(0.2f, 0.4f, 0.6f, 0.5f)));
        r.Prim(3, new Vector3(14, 10, 30), colours: new FaceColors(new Rgba(1f, 0.5f, 0f, 1f)));
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(10, 10), shift: false);
        r.C.Click(r.At(14, 10), shift: true);
        var saved = r.C.SaveFacePicks();
        r.C.EditFaces(new FaceChange(Colour: new Vector3(1, 0, 0)));
        r.Sent.Clear();
        Assert.True(r.C.RevertColours(saved));
        var sent = r.Sent.OfType<SetObjectFacesIntent>().ToDictionary(s => s.LocalId);
        Assert.Equal(new[] { 1u, 3u }, sent.Keys.OrderBy(k => k));
        Assert.All(sent[1].Faces, f => Assert.Equal(new Rgba(0.2f, 0.4f, 0.6f, 0.5f), f.Settings.Colour));
        Assert.All(sent[3].Faces, f => Assert.Equal(new Rgba(1f, 0.5f, 0f, 1f), f.Settings.Colour));
    }

    [Fact]
    public void Putting_back_materials_gives_each_face_the_material_it_had_or_none()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30));
        r.World.ApplyRenderMaterials(Sim, 1, [new RenderMaterialEntry(0, Gold), new RenderMaterialEntry(2, Brick)]);
        r.C.SelectTool(BuildTool.Edit);
        r.C.Click(r.At(10, 10), shift: false);
        var saved = r.C.SaveFacePicks();
        r.C.ApplyMaterial(Stone);
        r.Sent.Clear();
        Assert.True(r.C.RevertMaterials(saved));
        var put = r.Sent.OfType<SetFaceMaterialIntent>().ToList();
        Assert.Equal(new[] { 0 }, put.Single(p => p.MaterialId == Gold).Faces);
        Assert.Equal(new[] { 2 }, put.Single(p => p.MaterialId == Brick).Faces);
        Assert.Equal(new[] { 1, 3, 4, 5 }, put.Single(p => p.MaterialId == Guid.Empty).Faces);
    }
}
