// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Assets;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using GVector3 = Godot.Vector3;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// Rigged meshes attached and detached while drawing: one walking avatar wears the shirt and the override sleeves, which are detached and worn
    /// again, over many cycles, with a full collection and the pending finalizers run after every change. Checks what is drawn at each step, that
    /// the wearer's pelvis offset and shoulder overrides come and go with the sleeves, and that nothing is left once everything is detached and the
    /// layers are cleared: no instance, no mesh, no material, no row of joint matrices, no table.
    /// </summary>
    private async Task<int> AvatarRiggedStress(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets, int cycles)
    {
        var avatarRig = new AvatarRig(body.Skeleton);
        var pieces = RiggedPieces(avatarRig);
        var rig = MakeRig();
        var scenario = rig.Scenario;
        var floor = Floor(scenario, new Vector3(128, 128, 20), 8f);
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("rigged-stress", 1000, 1000, 256, 256, true));
        using var animations = AnimationSourceFor(assets);
        double now = 0;
        var view = MakeView("server", rig);
        var avatars = new AvatarPresenter(scenario, world, Guid.Empty, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, null, () => now);
        using var meshes = RiggedMeshes(pieces);
        var prims = new PrimLayer(scenario, world, meshes, null, _budget);
        var presenter = new WorldPresenter(world, view, prims: prims, avatars: avatars);
        var size = new Vector3(0.45f, 0.6f, 1.9f);
        world.ApplyAvatar(FixtureSim, new(800, RiggedAgents[0], "Rigged Stress", new(128, 128, 20.95f), Quaternion.Identity, size));
        world.ApplyAvatar(FixtureSim, new(900, RiggedAgents[1], "Rigged Stress Two", new(128, 130, 20.95f), Quaternion.Identity, size));
        world.ApplyAnimations(FixtureSim, RiggedAgents[0], [new(WalkId, 2)]);
        var cam = rig.Camera;
        cam.Fov = 40f; cam.Near = 0.1f; cam.Far = 100f;
        cam.Position = new GVector3(135f, 21.3f, -129f);
        cam.LookAt(new GVector3(128f, 20.95f, -129f), GVector3.Up);
        var shirt = pieces.First(p => p.Id == ShirtId);
        var sleeves = pieces.First(p => p.Id == SleevesId);
        var volumes = pieces.First(p => p.Id == VolumesId);
        int baseline = 0, peakInstances = 0;
        var sw = Stopwatch.StartNew();
        async Task Until(Func<bool> done, string what)
        {
            var wait = Stopwatch.StartNew();
            while (!done() && wait.Elapsed < TimeSpan.FromSeconds(20))
            {
                now += 0.05;
                presenter.Frame();
                await Frames(1);
            }
            if (!done()) _failures.Add($"cycle step not reached in 20 s: {what} (instances {prims.Instances}, rigged {prims.RiggedObjects})");
        }
        void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        await Until(() => avatars.Drawn == 2 && animations.Ready >= 1, "avatars drawn");
        baseline = prims.Instances;
        for (int c = 0; c < cycles && _failures.Count == 0; c++)
        {
            uint a = (uint)(8000 + c * 4), b = a + 1, d = a + 2;
            world.ApplyObject(FixtureSim, WornRigged(a, 800, shirt), eventIsAttachment: true);
            world.ApplyObject(FixtureSim, WornRigged(b, 800, sleeves), eventIsAttachment: true);
            world.ApplyObject(FixtureSim, WornRigged(d, 900, volumes), eventIsAttachment: true);   // the other avatar wears one too
            await Until(() => prims.Draws(a) && prims.Draws(b) && prims.Draws(d) && Figure(prims.Describe(), "rigged_shown") == 3, $"cycle {c}: three meshes drawn");
            peakInstances = Math.Max(peakInstances, prims.Instances);
            Collect();
            for (int f = 0; f < 3; f++) { now += 0.05; presenter.Frame(); await Frames(1); }
            // detach one at a time, collecting between: the sleeves' overrides go with them
            world.Kill(FixtureSim, [b]);
            await Until(() => !prims.Draws(b), $"cycle {c}: sleeves detached");
            Collect();
            world.Kill(FixtureSim, [a, d]);
            await Until(() => !prims.Draws(a) && !prims.Draws(d), $"cycle {c}: shirt and bands detached");
            Collect();
            if (prims.RiggedObjects != 0) _failures.Add($"cycle {c}: {prims.RiggedObjects} rigged objects still held after all were detached");
        }
        if (avatars.SkinTextures is { } skin && (skin.RowsInUse != 0 || skin.TablesInUse != 0))
            _failures.Add($"after every mesh was detached the avatars hold {skin.RowsInUse} rows and {skin.TablesInUse} tables of the skin textures");
        double armOut = 0;
        if (avatars.TryPoint(800, 5, out var hand) && avatars.TryPoint(800, 10, out var pelvis))
            armOut = hand.Translation.Y - pelvis.Translation.Y;
        _result["mode"] = "avatars-rigged-stress";
        _result["avatars_rigged_stress"] = new
        {
            cycles, peak_instances = peakInstances, baseline_instances = baseline, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), instances_at_end = prims.Instances,
            rows_in_use = avatars.SkinTextures?.RowsInUse, tables_in_use = avatars.SkinTextures?.TablesInUse, row_capacity = avatars.SkinTextures?.RowCapacity,
            prims = prims.Describe(), live_prim_resources = prims.LiveResources,
        };
        if (prims.Instances != baseline) _failures.Add($"{prims.Instances} instances at the end, {baseline} before the first cycle");
        presenter.Dispose();
        if (avatars.LiveResources != 0) _failures.Add($"{avatars.LiveResources} avatar resources live after the presenter went");
        prims.Clear();
        if (prims.LiveResources != 0) _failures.Add($"{prims.LiveResources} prim resources live after Clear");
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        Rids.Free(floor.Instance);
        Rids.Free(floor.Mesh);
        floor.Material.Dispose();
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }
}
