// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;
using GVector3 = Godot.Vector3;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// Many rigged meshes arriving at once (--tr-avatars=riggedstall --tr-stall-meshes=N, default 24): one avatar wears N different dense rigged
    /// shirts (about 30 000 vertices each, a size real clothing reaches) in the same frame. The longest main-thread step of the layer
    /// (<see cref="PrimLayer.WorstStepMs"/>, every step of the run) must not pass the presentation frame's budget
    /// (<see cref="PrimLayer.StepBudgetMs"/>, or --tr-stall-limit-ms=): the data work of a rigged mesh (surface encoding, the bind table) belongs
    /// off the main thread, leaving only the engine calls.
    /// </summary>
    private async Task<int> AvatarRiggedStall(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets, int count, double limitMs)
    {
        var avatarRig = new AvatarRig(body.Skeleton);
        var shirt = DenseShirt(avatarRig);
        var pieces = Enumerable.Range(0, count).Select(i => shirt with { Id = Guid.Parse($"a77a3000-0000-4000-8000-{i:x12}") }).ToList();
        // one small rigged mesh worn first, so that what happens once per process (compiling code, making the first skinned materials) is not
        // counted as a step of the burst
        var warm = RiggedPieces(avatarRig).First(p => p.Id == ShirtId) with { Id = Guid.Parse("a77a3000-0000-4000-8000-ffffffffffff") };
        var rig = MakeRig();
        var scenario = rig.Scenario;
        var floor = Floor(scenario, new Vector3(128, 128, 20), 8f);
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("rigged-stall", 1000, 1000, 256, 256, true));
        using var animations = AnimationSourceFor(assets);
        double now = 0;
        var view = MakeView("server", rig);
        var avatars = new AvatarPresenter(scenario, world, Guid.Empty, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, null, () => now);
        using var meshes = RiggedMeshes(pieces.Append(warm));
        var prims = new PrimLayer(scenario, world, meshes, null, _budget);
        var presenter = new WorldPresenter(world, view, prims: prims, avatars: avatars);
        world.ApplyAvatar(FixtureSim, new(800, RiggedAgents[0], "Rigged Stall", new(128, 128, 20.95f), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
        var cam = rig.Camera;
        cam.Fov = 40f; cam.Near = 0.1f; cam.Far = 100f;
        cam.Position = new GVector3(135f, 21.3f, -129f);
        cam.LookAt(new GVector3(128f, 20.95f, -129f), GVector3.Up);

        async Task Until(Func<bool> done, string what)
        {
            var wait = Stopwatch.StartNew();
            while (!done() && wait.Elapsed < TimeSpan.FromSeconds(60))
            {
                now += 0.05;
                presenter.Frame();
                await Frames(1);
            }
            if (!done()) _failures.Add($"not reached in 60 s: {what} (instances {prims.Instances}, rigged {prims.RiggedObjects})");
        }
        await Until(() => avatars.Drawn == 1, "the avatar drawn");
        world.ApplyObject(FixtureSim, WornRigged(8999, 800, warm), eventIsAttachment: true);
        await Until(() => Figure(prims.Describe(), "rigged_shown") == 1, "the warm-up mesh drawn");
        prims.ResetStepTimes();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++) world.ApplyObject(FixtureSim, WornRigged((uint)(9000 + i), 800, pieces[i]), eventIsAttachment: true);
        await Until(() => Figure(prims.Describe(), "rigged_shown") == count + 1, $"{count} rigged meshes drawn");
        double seconds = sw.Elapsed.TotalSeconds;
        int vertices = pieces[0].Geometry.Lods[0]!.Faces.Sum(f => f.Vertices);
        _result["mode"] = "avatars-rigged-stall";
        _result["avatars_rigged_stall"] = new
        {
            meshes = count, vertices_each = vertices, worst_step_ms = Math.Round(prims.WorstStepMs, 2), steps_over_budget = prims.StepsOverBudget, most_rigged_parts_in_a_step = prims.MostRiggedPartsInAStep, long_steps_ms = prims.LongSteps,
            handover_ms_top = prims.UploadSamples.Where(u => u.Kind == "rigged").Select(u => Math.Round(u.Ms, 2)).OrderByDescending(m => m).Take(6).ToArray(), limit_ms = limitMs, seconds = Math.Round(seconds, 2), rigged_drawn = Figure(prims.Describe(), "rigged_shown") - 1, tables = avatars.SkinTextures?.TablesInUse,
        };
        if (prims.MostRiggedPartsInAStep > 1) _failures.Add($"a step did {prims.MostRiggedPartsInAStep} parts of a rigged mesh's build (a surface given to the engine, or a bind table copied); one is the most a step may do");
        if (prims.WorstStepMs > limitMs) _failures.Add($"the longest main-thread step was {prims.WorstStepMs:0.00} ms with {count} rigged meshes arriving at once ({prims.StepsOverBudget} steps over {PrimLayer.StepBudgetMs} ms), limit {limitMs} ms");
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

    /// <summary>A shirt as <see cref="RiggedPieces"/> makes it, with a ring every few millimetres and sixty sides: about 30 000 vertices in three faces.</summary>
    private static RiggedPiece DenseShirt(AvatarRig rig)
    {
        var rest = new Matrix4x4[rig.NodeCount];
        JointWorlds.Rest(rig, rest);
        Vector3 At(string joint) => rest[rig.Find(joint)].Translation;
        string[] torso = ["mPelvis", "mTorso", "mChest", "mNeck"];
        string[] left = ["mCollarLeft", "mShoulderLeft", "mElbowLeft", "mWristLeft"], right = ["mCollarRight", "mShoulderRight", "mElbowRight", "mWristRight"];
        string[] names = [.. torso, .. left, .. right];
        byte Index(string n) => (byte)Array.IndexOf(names, n);
        Matrix4x4 Inverse(string joint) => Matrix4x4.Invert(rest[rig.Find(joint)], out var inv) ? inv : throw new InvalidOperationException(joint);
        var block = new MeshSkinBlock(names, names.Select(Inverse).ToArray(), Matrix4x4.Identity, [], 0f, false, false);
        var faces = new List<(MeshFace, FaceInfluences)>();
        var body = new MeshBuilder();
        body.Tube(torso.Select(At).ToArray(), torso.Select(Index).ToArray(), [0.17f, 0.18f, 0.19f, 0.07f], 60, 60);
        faces.Add(body.Finish());
        foreach (var arm in new[] { left, right })
        {
            var sleeve = new MeshBuilder();
            sleeve.Tube(arm.Select(At).ToArray(), arm.Select(Index).ToArray(), [0.075f, 0.07f, 0.06f, 0.05f], 60, 60);
            faces.Add(sleeve.Finish());
        }
        return new(Guid.Empty, RiggedGeometry(faces, block), 9, new FaceColors(new Rgba(0.85f, 0.2f, 0.2f, 1)));
    }
}
