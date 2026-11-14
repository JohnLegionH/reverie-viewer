// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;
using GVector3 = Godot.Vector3;

namespace Viewer.Godot;

public partial class TestRender
{
    private static readonly Guid BakeLookSelf = Guid.Parse("b4e00001-0000-4000-8000-00000000000a"), BakeLookOther = Guid.Parse("b4e00001-0000-4000-8000-00000000000b");

    /// <summary>
    /// How bakes reach an avatar's body and the mesh it wears, for the viewer's own avatar (left) and another (right), from synthetic
    /// region updates through the world state, the avatar presenter and the prim layer, with the texture zoo's images as bakes (--tr-textures=):
    /// 1. both wear a rigged shirt whose faces name the upper-body bake placeholder, and neither has its upper bake yet: the shirt draws the default
    ///    texture (asked for by its id, never plain white), and the upper body is hidden under it;
    /// 2. the appearances arrive with an upper bake each (the grid image left, the gradient right): the shirts show them;
    /// 3. the shirts go, and each avatar's four body bakes are the alpha-blend image (left: its corners are transparent) or the opaque gradient
    ///    (right): the left body shows less of itself, the cut taking out what the alpha says.
    /// Pixel checks, no golden. --tr-alter=nobom (negative control): the faces name the zoo's grid image itself, not a placeholder, so stage 2
    /// shows no change from the wearer's bake and the checks must fail.
    /// </summary>
    private async Task<int> AvatarBakeLook(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? texDir, string? alter)
    {
        if (texDir is null) { _failures.Add("--tr-textures= is needed (the texture zoo's images)"); return 1; }
        var avatarRig = new AvatarRig(body.Skeleton);
        var shirt = RiggedPieces(avatarRig).First(p => p.Id == ShirtId);
        var rig = MakeRig(new Vector2I(960, 640));
        var scenario = rig.Scenario;
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("bake-look", 1000, 1000, 256, 256, true));
        double now = 0;
        var view = MakeView("server", rig);
        s_textureFetches.Clear();
        using var textures = TextureSourceFor(texDir, TextureZoo.Images);
        using var meshes = RiggedMeshes([shirt]);
        var avatars = new AvatarPresenter(scenario, world, BakeLookSelf, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), null, textures, null, () => now);
        var prims = new PrimLayer(scenario, world, meshes, textures, _budget);
        var presenter = new WorldPresenter(world, view, prims: prims, avatars: avatars);
        var cam = rig.Camera;
        cam.Fov = 40f; cam.Near = 0.1f; cam.Far = 100f;
        cam.Position = new GVector3(134f, 21.1f, -128f);
        cam.LookAt(new GVector3(128f, 21.1f, -128f), GVector3.Up);
        prims.Camera = cam.GlobalPosition;
        prims.ResidencyView = PrimLayer.ViewOf(cam, (int)rig.Viewport.Size.X, (int)rig.Viewport.Size.Y);
        var size = new System.Numerics.Vector3(0.45f, 0.6f, 1.9f);
        // seen from +x looking west, SL y runs to the viewer right: the own avatar (y 126.8) is drawn at the left
        world.ApplyAvatar(FixtureSim, new(800, BakeLookSelf, "Test User", new(128, 126.8f, 20.95f), System.Numerics.Quaternion.Identity, size));
        world.ApplyAvatar(FixtureSim, new(900, BakeLookOther, "Example Walker", new(128, 129.2f, 20.95f), System.Numerics.Quaternion.Identity, size));
        var upper = BakeChannels.IdOf("upper");
        var named = alter == "nobom" ? TextureZoo.Grid : upper;
        PrimData Shirt(uint local, uint wearer) =>
            WornRigged(local, wearer, shirt) with { Textures = new FaceTextures(FaceTexture.Plain(named)), Colors = new FaceColors(new Rgba(1, 1, 1, 1)) };
        world.ApplyObject(FixtureSim, Shirt(8001, 800), eventIsAttachment: true);
        world.ApplyObject(FixtureSim, Shirt(9001, 900), eventIsAttachment: true);

        async Task<Image> Settle(Func<bool> ready, string what)
        {
            var sw = Stopwatch.StartNew();
            while (!ready() && sw.Elapsed < TimeSpan.FromSeconds(30)) { presenter.Frame(); await Frames(1); }
            if (!ready()) _failures.Add($"{what}: not settled in 30 s (avatars {avatars.Drawn}, dirty {prims.Dirty}, gpu textures {prims.GpuTextures}, waiting {prims.TexturesWaiting}, bake textures {avatars.BakeTextures})");
            presenter.Frame();
            await Frames(8);
            presenter.Frame();
            var img = rig.Texture.GetImage();
            img.Convert(Image.Format.Rgba8);
            return img;
        }

        // 1: the shirts name the upper placeholder and no upper bake is there
        var look1 = new Dictionary<string, Guid>();
        world.ApplyAppearance(FixtureSim, Appearance(BakeLookSelf, look1), BakeLookSelf);
        world.ApplyAppearance(FixtureSim, Appearance(BakeLookOther, look1), BakeLookSelf);
        var img1 = await Settle(() => prims.Draws(8001) && prims.Draws(9001) && avatars.Drawn == 2 && prims.Dirty == 0, "stage 1");
        img1.SavePng(Path.Combine(_out, _tag + "-1-no-bake.png"));
        bool askedDefault = s_textureFetches.ContainsKey(BakeOnMesh.FallbackTexture);
        bool placeholderAsked = s_textureFetches.ContainsKey(upper);
        var hidden1 = HiddenOf(avatars);

        // 2: the upper bakes arrive
        world.ApplyAppearance(FixtureSim, Appearance(BakeLookSelf, new Dictionary<string, Guid> { ["upper"] = TextureZoo.Grid }), BakeLookSelf);
        world.ApplyAppearance(FixtureSim, Appearance(BakeLookOther, new Dictionary<string, Guid> { ["upper"] = TextureZoo.Gradient }), BakeLookSelf);
        var img2 = await Settle(() => prims.Draws(8001) && prims.Draws(9001) && prims.Dirty == 0 && s_textureFetches.ContainsKey(TextureZoo.Grid) && s_textureFetches.ContainsKey(TextureZoo.Gradient) && prims.GpuTextures >= 2 && prims.TexturesWaiting == 0, "stage 2");
        img2.SavePng(Path.Combine(_out, _tag + "-2-bakes.png"));

        // 3: bare bodies with four body bakes each
        world.Kill(FixtureSim, [8001, 9001]);
        var four = new[] { "head", "upper", "lower", "eyes" };
        world.ApplyAppearance(FixtureSim, Appearance(BakeLookSelf, four.ToDictionary(s => s, _ => TextureZoo.AlphaBlend)), BakeLookSelf);
        world.ApplyAppearance(FixtureSim, Appearance(BakeLookOther, four.ToDictionary(s => s, _ => TextureZoo.Gradient)), BakeLookSelf);
        var img3 = await Settle(() => avatars.Drawn == 2 && avatars.BakeTextures >= 8 && prims.Dirty == 0, "stage 3");
        img3.SavePng(Path.Combine(_out, _tag + "-3-alpha.png"));
        var hidden3 = HiddenOf(avatars);

        int w = img1.GetWidth(), h = img1.GetHeight();
        int Changed(Image a, Image b, int x0, int x1)
        {
            var (da, db) = (a.GetData(), b.GetData());
            int n = 0;
            for (int y = 0; y < h; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * w + x) * 4;
                    if (Math.Abs(da[i] - db[i]) + Math.Abs(da[i + 1] - db[i + 1]) + Math.Abs(da[i + 2] - db[i + 2]) > 60) n++;
                }
            return n;
        }
        int Backdrop(Image a, int x0, int x1, byte[] reference)
        {
            var da = a.GetData();
            int n = 0;
            for (int y = 0; y < h; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * w + x) * 4, j = (y * w + x) * 4;
                    if (Math.Abs(da[i] - reference[j]) + Math.Abs(da[i + 1] - reference[j + 1]) + Math.Abs(da[i + 2] - reference[j + 2]) <= 12) n++;
                }
            return n;
        }
        int half = w / 2;
        int leftChanged = Changed(img1, img2, 0, half), rightChanged = Changed(img1, img2, half, w);
        // stage 3, the alpha cut: the pixels where each half of the picture shows what a bare empty scene shows (the picture with no avatar in it)
        world.Kill(FixtureSim, [800, 900]);
        var img0 = await Settle(() => avatars.Drawn == 0 && prims.Dirty == 0, "empty scene");
        var empty = img0.GetData();
        int leftBackdrop = Backdrop(img3, 0, half, empty), rightBackdrop = Backdrop(img3, half, w, empty);
        _result["mode"] = "avatars-bake-look";
        _result["avatars_bake_look"] = new
        {
            fetched_default_texture = askedDefault, fetched_placeholder_id = placeholderAsked, hidden_stage1 = hidden1, hidden_stage3 = hidden3,
            stage2_changed_left = leftChanged, stage2_changed_right = rightChanged, stage3_backdrop_left = leftBackdrop, stage3_backdrop_right = rightBackdrop,
            gpu_textures = prims.GpuTextures, altered = alter,
        };
        if (!askedDefault) _failures.Add("stage 1: a bake-on-mesh face with no bake did not ask for the default texture");
        if (placeholderAsked) _failures.Add("stage 1: the placeholder id itself was asked for as a texture");
        if (hidden1.Count != 2 || hidden1.Any(x => !x.Contains("upper"))) _failures.Add($"stage 1: both avatars should have the upper body hidden under the shirt, got {string.Join(" | ", hidden1.Select(x => string.Join(",", x)))}");
        if (leftChanged < 200) _failures.Add($"stage 2: the own avatar's shirt changed in only {leftChanged} pixels when its upper bake arrived");
        if (rightChanged < 200) _failures.Add($"stage 2: the other avatar's shirt changed in only {rightChanged} pixels when its upper bake arrived");
        if (hidden3.Any(x => x.Length != 0)) _failures.Add($"stage 3: no body part should be hidden on a bare avatar with drawn bakes, got {string.Join(" | ", hidden3.Select(x => string.Join(",", x)))}");
        // the left body is cut by its bake's transparent corners, so more of the empty scene shows through it than through the opaque right one
        if (leftBackdrop <= rightBackdrop + 100) _failures.Add($"stage 3: the alpha-cut body (left) shows the scene through {leftBackdrop} pixels, the opaque one (right) {rightBackdrop}: no cut");
        presenter.Dispose();
        if (avatars.LiveResources != 0) _failures.Add($"{avatars.LiveResources} avatar resources live after the presenter went");
        prims.Clear();
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }

    private static List<string[]> HiddenOf(AvatarPresenter avatars) =>
        avatars.Scene.Entries.OrderBy(e => !e.IsSelf).Select(avatars.HiddenSlots).ToList();
}
