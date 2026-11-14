// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

using System.Threading.Tasks;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The first run of the avatar arrival's code, paid at start-up instead of in the first frame a crowd arrives. The pipeline warm-up compiles the
/// shaders and their pipelines on stand-in meshes; the first real arrival still compiled more (the real body's vertex format on a skeleton, a bake's
/// material) and ran every engine path for the first time: the body's mesh, skeleton and instances, a bake's texture and material, a body morphed to a
/// shape, the joint rows and bind table of a worn rigged mesh, and the pose workers. This does all of it once, in a hidden viewport that is drawn for a few
/// frames and then freed, with the viewer's own body, a few avatars and small pictures. Main thread; a step a frame, so no frame carries it whole.
/// </summary>
public static class ArrivalWarmup
{
    /// <param name="Ms">Milliseconds from start to end (frames included).</param>
    /// <param name="Avatars">Avatars made.</param>
    /// <param name="Frames">Frames the warm-up spread over.</param>
    public sealed record Result(double Ms, int Avatars, int Frames);

    /// <summary>More avatars than the least posed a frame, so the workers pose them.</summary>
    private const int WarmAvatars = AvatarLayer.MinPosedPerFrame + 2;

    private static readonly string[] Slots = ["head", "upper", "lower", "eyes", "hair"];

    public static async Task<Result> RunAsync(Node parent, Viewport.Msaa msaa, AvatarPresenter.LoadedBody loaded, Func<Task> nextFrame)
    {
        long t0 = Stopwatch.GetTimestamp();
        int frames = 0;
        async Task Frame() { frames++; await nextFrame(); }
        var body = loaded.Body;
        var vp = new SubViewport
        {
            Size = new Vector2I(64, 64),
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = msaa,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
            UseTaa = false,
        };
        parent.AddChild(vp);
        var env = Stage.Environment();
        var sun = Stage.Sun();
        var cam = new Camera3D { Current = true, Position = new Vector3(0, 1.2f, 4f) };
        vp.AddChild(env);
        vp.AddChild(sun);
        vp.AddChild(cam);
        cam.LookAt(new Vector3(0, 1f, 0), Vector3.Up);
        var scenario = vp.FindWorld3D().Scenario;

        var skin = new SkinTextures(body.Skeleton.Joints.Count + body.Skeleton.AttachmentPoints.Count);
        var layer = new AvatarLayer(scenario, body, loaded.Surfaces, skin, deferSurfaces: true);
        var textures = new List<ImageTexture>();
        var ids = new List<int>();
        var asset = Guid.NewGuid();
        try
        {
            // the body's mesh, one surface a frame (as the presenter makes it)
            while (!layer.Step()) await Frame();

            // a bake picture as the presenter uploads one: an image with a mip chain, then an ImageTexture, under a material per body part
            var bakes = new Dictionary<string, Texture2D>();
            foreach (var slot in Slots)
            {
                var image = Image.CreateEmpty(64, 64, true, Image.Format.Rgba8);
                image.Fill(new Color(0.6f, 0.5f, 0.4f));
                image.GenerateMipmaps();
                var texture = ImageTexture.CreateFromImage(image);
                image.Dispose();
                textures.Add(texture);
                bakes[slot] = texture;
            }
            for (int i = 0; i < WarmAvatars; i++)
            {
                ids.Add(layer.Add(new System.Numerics.Vector3(i * 0.7f - 2f, 0, 0), 0.3f * i, null, 0.1 * i));
                layer.SetBakes(ids[i], bakes, i == 0 ? new HashSet<string> { "hair" } : null);
                await Frame();
            }

            // a shape and the body morphed to it (made off the main thread, as it is for an arrival), a worn rigged mesh's joint row and bind table
            if (body.Shape is { } def)
            {
                var wireOrder = def.InIdOrder.Select(p => p.Id).Take(60).ToImmutableArray();
                var set = new VisualParamSet(wireOrder.Select(_ => (byte)200), wireOrder);
                var shape = AvatarShape.From(layer.Rig, def, set);
                var morphed = await Task.Run(() => AvatarLayer.PrepareMorphed(body, def, shape));
                layer.SetShape(ids[0], shape);
                layer.SetMorphed(ids[0], morphed);
                await Frame();
            }
            var block = new MeshSkinBlock(["mPelvis", "mTorso"], [System.Numerics.Matrix4x4.Identity, System.Numerics.Matrix4x4.Identity], System.Numerics.Matrix4x4.Identity, [], 0f, false, false);
            if (layer.WearRigged(ids[0], 1, asset, block)) skin.AcquireTable(asset, layer.Rig, block);
            // enough tables that the atlas grows once, so the first growth is not a crowd's
            var extra = new List<Guid>();
            for (int i = 0; i < TableAllocator.StartTables + 4; i++)
            {
                var other = Guid.NewGuid();
                if (skin.AcquireTable(other, layer.Rig, block) >= 0) extra.Add(other);
            }
            await Frame();
            foreach (var other in extra) skin.ReleaseTable(other);

            // posing: the workers (more avatars than the least posed a frame, with a budget), then the main thread (no budget); the frames draw it all
            double t = 0;
            for (int i = 0; i < 3; i++)
            {
                layer.Frame(t += 0.016, 4.0);
                await Frame();
            }
            layer.Frame(t += 0.016);
            await Frame();
        }
        finally
        {
            skin.ReleaseTable(asset);
            layer.Dispose();
            skin.Dispose();
            foreach (var texture in textures) texture.Dispose();
            parent.RemoveChild(vp);
            vp.Free();
        }
        return new Result((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, ids.Count, frames);
    }
}
