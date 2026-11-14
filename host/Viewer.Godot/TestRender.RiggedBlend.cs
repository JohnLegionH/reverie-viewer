// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;
using GVector3 = Godot.Vector3;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// A rigged mesh with a blended face (--tr-avatars=riggedblend): three avatars, the first standing and the others mid-walk (so most of them
    /// read a row of the bone texture other than the first), each wear the synthetic shirt, whose sleeves are drawn by the blended variant of the
    /// face shader while its body is drawn by the opaque one, both in one instance. The scene is drawn with the sleeves opaque and again with
    /// them 90 % opaque; the second picture can differ from the first only by the slight see-through of the sleeves, so the sleeves must be
    /// posed by the same bones in both. A sleeve skinned from the wrong row of the bone texture stands elsewhere and the pictures differ widely.
    /// --tr-alter=norigged (negative control): the meshes carry no full skin, so none is drawn and the checks fail.
    /// </summary>
    private async Task<int> AvatarRiggedBlend(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets, string? alter)
    {
        var avatarRig = new AvatarRig(body.Skeleton);
        var shirt = RiggedPieces(avatarRig).First(p => p.Id == ShirtId);
        if (alter == "norigged") shirt = shirt with { Geometry = shirt.Geometry with { Skin = shirt.Geometry.Skin! with { Data = null } } };
        var rig = MakeRig(new Vector2I(1280, 720));
        var scenario = rig.Scenario;
        var floor = Floor(scenario, new Vector3(128, 128, 20), 8f);
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("rigged-blend", 1000, 1000, 256, 256, true));
        using var animations = AnimationSourceFor(assets);
        double now = 0;
        var view = MakeView("server", rig);
        var avatars = new AvatarPresenter(scenario, world, Guid.Empty, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, null, () => now) { HoldWalkWithClock = true };
        using var meshes = RiggedMeshes([shirt]);
        var prims = new PrimLayer(scenario, world, meshes, null, _budget);
        var presenter = new WorldPresenter(world, view, prims: prims, avatars: avatars);
        var size = new Vector3(0.45f, 0.6f, 1.9f);
        uint[] wearers = [800, 900, 1000];
        float[] ys = [126.4f, 128f, 129.6f];
        for (int i = 0; i < 3; i++)
            world.ApplyAvatar(FixtureSim, new(wearers[i], RiggedAgents[i], $"Blend Test {i + 1}", new(128, ys[i], 20.95f), Quaternion.Identity, size));
        foreach (int i in new[] { 1, 2 }) world.ApplyAnimations(FixtureSim, RiggedAgents[i], [new(WalkId, 2)]);
        var cam = rig.Camera;
        cam.Fov = 40f; cam.Near = 0.1f; cam.Far = 100f;
        cam.Position = new GVector3(134f, 21.3f, -128f);
        cam.LookAt(new GVector3(128f, 20.95f, -128f), GVector3.Up);

        async Task<Image> Pass(uint offset, float sleeveAlpha)
        {
            var sleeve = new Rgba(0.95f, 0.8f, 0.15f, sleeveAlpha);
            var piece = shirt with { Colours = new FaceColors(new Rgba(0.85f, 0.2f, 0.2f, 1), [null, sleeve, sleeve]) };
            for (int i = 0; i < 3; i++)
                world.ApplyObject(FixtureSim, WornRigged(wearers[i] * 10 + offset, wearers[i], piece), eventIsAttachment: true);
            var sw = Stopwatch.StartNew();
            bool Settled() => avatars.Drawn == 3 && avatars.BodiesPending == 0 && prims.Instances == 3 && prims.Dirty == 0 && animations.Waiting == 0 && animations.InFlight == 0 && animations.Ready >= 1
                && (Figure(prims.Describe(), "rigged_shown") ?? 3) == 3;
            while (!Settled() && sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                presenter.Frame();
                await Frames(1);
            }
            if (!Settled()) _failures.Add($"not settled in 30 s: avatars drawn {avatars.Drawn} of 3, rigged meshes drawn {prims.Instances} of 3, dirty {prims.Dirty}, animations waiting {animations.Waiting} in flight {animations.InFlight} ready {animations.Ready}");
            presenter.Frame();
            now = 0.6;
            presenter.Frame();
            await SettlePoses(avatars, presenter);
            await Frames(8);
            presenter.Frame();
            ExpectBodiesSettled(avatars, "blend pass");
            var image = rig.Texture.GetImage();
            image.Convert(Image.Format.Rgba8);
            return image;
        }

        var opaque = await Pass(0, 1f);
        opaque.SavePng(Path.Combine(_out, _tag + "-opaque.png"));
        world.Kill(FixtureSim, wearers.Select(w => w * 10).ToArray());
        for (int i = 0; i < 20; i++) { presenter.Frame(); await Frames(1); }
        var blended = await Pass(1, 0.9f);
        blended.SavePng(Path.Combine(_out, _tag + ".png"));

        // the sleeves' colour against the 10 % of the floor and sky behind them can move a channel by at most about a fifth of its range;
        // a sleeve standing elsewhere moves whole areas by more than that
        var diff = ImageDiff.Compare(opaque.GetData(), blended.GetData(), opaque.GetWidth(), opaque.GetHeight(), 4, new DiffTolerance(ChannelThreshold: 60, MaxDifferingFraction: 1, MaxMeanAbsError: 255));
        const double limit = 0.0005;
        _result["mode"] = "avatars-rigged-blend";
        _result["avatars_rigged_blend"] = new
        {
            meshes_drawn = prims.Instances, rigged_shown = Figure(prims.Describe(), "rigged_shown") ?? -1, avatars_drawn = avatars.Drawn, differing_pixels = diff.Differing, differing_fraction = Math.Round(diff.DifferingFraction, 6), limit_fraction = limit,
            prims = prims.Describe(), altered = alter, png = _tag + ".png", png_opaque = _tag + "-opaque.png",
        };
        if (prims.Instances != 3) _failures.Add($"{prims.Instances} of 3 worn rigged meshes are drawn");
        if (diff.DifferingFraction > limit) _failures.Add($"the shirt with see-through sleeves differs from the opaque one in {diff.DifferingFraction:P3} of the picture (limit {limit:P3}): the sleeves are posed by other bones");
        presenter.Dispose();
        prims.Clear();
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        Rids.Free(floor.Instance);
        Rids.Free(floor.Mesh);
        floor.Material.Dispose();
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }
}
