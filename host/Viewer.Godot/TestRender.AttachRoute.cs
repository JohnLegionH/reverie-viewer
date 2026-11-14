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
using Viewer.Godot.Presentation;

namespace Viewer.Godot;

/// <summary>
/// How worn objects follow their joints, measured (--tr-avatars=attachroute --tr-route=own|nodes --tr-crowd=N): N avatars posed
/// by the grid's walk, each wearing eight boxes. Route "own": the joint matrices of <see cref="AvatarJoints"/> and one engine
/// transform per box, set through the RenderingServer. Route "nodes": a <c>Skeleton3D</c> per avatar holding the bones the
/// points hang from (their poses set from the same clip every frame), a <c>BoneAttachment3D</c> per box and a
/// <c>MeshInstance3D</c> under it, which the engine moves with the bone. Only the attachments are drawn (no bodies), so the
/// two routes are compared on what they differ in; after 2 s unrecorded, --tr-crowd-seconds (default 10) of frames are recorded.
/// </summary>
public partial class TestRender
{
    private async Task<int> AvatarAttachRoute(AvatarBody body, string? assets, string[] args)
    {
        int n = int.Parse(Arg(args, "--tr-crowd=") ?? "25");
        string route = Arg(args, "--tr-route=") ?? "own";
        double seconds = double.Parse(Arg(args, "--tr-crowd-seconds=") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
        var size = (Arg(args, "--tr-size=") ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
        int seed = int.Parse(Arg(args, "--tr-seed=") ?? "8");
        if (route is not ("own" or "nodes")) throw new ArgumentException("--tr-route must be own or nodes");
        var walk = ClipFrom(assets, WalkId);
        if (walk is null) { _failures.Add("the walk animation is not in the asset cache (--tr-assets=)"); return 1; }
        FreeRunning();
        var w = await PipelineWarmup.RunAsync(this, Viewport.Msaa.Disabled, () => Frames(1));
        _result["warmup"] = new { ms = Math.Round(w.Ms, 2), pipelines = w.Pipelines, variants = w.Variants, frames = w.Frames };
        var rig = MakeRig(new Vector2I(size[0], size[1]));
        var vpRid = rig.Viewport.GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vpRid, true);
        var scenario = rig.Scenario;
        var cam = rig.Camera;
        cam.Fov = 60f; cam.Near = 0.2f; cam.Far = 512f;
        var centre = new System.Numerics.Vector3(128, 128, 20);
        var floor = Floor(scenario, centre, 120f);
        var avatarRig = new AvatarRig(body.Skeleton);
        var tracks = AvatarJoints.TracksFor(avatarRig, walk);
        int[] pointNodes = CrowdPoints.Select(avatarRig.PointNode).ToArray();
        var needed = new SortedSet<int>();   // the nodes the points hang from, parents first (the rig's order)
        foreach (int p in pointNodes)
            for (int k = p; k >= 0; k = avatarRig.Parent(k)) needed.Add(k);
        int[] chain = needed.ToArray();
        var boxMesh = new BoxMesh { Size = new Vector3(0.1f, 0.1f, 0.1f) };
        var boxMaterial = PrimLayer.ColourMaterial(new Viewer.Core.World.Rgba(0.9f, 0.3f, 0.2f, 1f));
        boxMesh.Material = boxMaterial;
        int side = (int)Math.Ceiling(Math.Sqrt(n));
        float span = (side - 1) * 2.5f;
        var rnd = new Random(seed);
        float hip = body.Skeleton.Joints[0].Position.Z - body.FootHeight;
        var joints = new AvatarJoints[n];
        var phases = new double[n];
        var worlds = new System.Numerics.Matrix4x4[n];
        var instances = new List<Rid>();
        var roots = new List<Node3D>();
        var skeletons = new Skeleton3D[n];
        var bones = new int[n][];
        var offset = System.Numerics.Matrix4x4.CreateTranslation(0, 0, 0.05f);
        for (int i = 0; i < n; i++)
        {
            float x = centre.X - span / 2 + (i % side) * 2.5f, y = centre.Y - span / 2 + (i / side) * 2.5f;
            phases[i] = rnd.NextDouble() * walk.Duration;
            worlds[i] = System.Numerics.Matrix4x4.CreateTranslation(x, y, centre.Z + hip);
            joints[i] = new AvatarJoints(avatarRig);
            if (route == "own")
            {
                for (int k = 0; k < pointNodes.Length; k++) instances.Add(Rids.InstanceCreate2(boxMesh.GetRid(), scenario, "test box"));
                continue;
            }
            var root = new Node3D { Position = new Vector3(x, centre.Z + hip, -y) };
            var skeleton = new Skeleton3D();
            root.AddChild(skeleton);
            var index = new Dictionary<int, int>();
            foreach (int node in chain)
            {
                int b = skeleton.GetBoneCount();
                skeleton.AddBone(avatarRig.NodeName(node));
                index[node] = b;
                int parent = avatarRig.Parent(node);
                if (parent >= 0) skeleton.SetBoneParent(b, index[parent]);
                skeleton.SetBoneRest(b, new Transform3D(new Basis(EngineQ(avatarRig.DefaultRotation[node])), EngineV(node == avatarRig.Pelvis ? System.Numerics.Vector3.Zero : avatarRig.DefaultPosition[node])));
            }
            bones[i] = chain.Select(c => index[c]).ToArray();
            for (int k = 0; k < pointNodes.Length; k++)
            {
                var attachment = new BoneAttachment3D { BoneName = avatarRig.NodeName(pointNodes[k]) };
                skeleton.AddChild(attachment);
                attachment.AddChild(new MeshInstance3D { Mesh = boxMesh, Position = EngineV(new System.Numerics.Vector3(0, 0, 0.05f)) });
            }
            skeletons[i] = skeleton;
            rig.Root.AddChild(root);
            roots.Add(root);
        }
        float back = span * 0.9f + 9f;
        var target = new Vector3(centre.X, centre.Z + 1f, -centre.Y);
        cam.Position = target + new Vector3(back * 0.5f, back * 0.45f + 2f, back * 0.75f);
        cam.LookAt(target, Vector3.Up);

        void Update(double t)
        {
            for (int i = 0; i < n; i++)
            {
                float clipTime = walk.TimeAt(t + phases[i]);
                if (route == "own")
                {
                    joints[i].Begin(tracks, clipTime);
                    for (int k = 0; k < pointNodes.Length; k++)
                    {
                        var frame = offset * joints[i].World(pointNodes[k]) * worlds[i];
                        RenderingServer.InstanceSetTransform(instances[i * pointNodes.Length + k], ToEngineTransform(frame));
                    }
                }
                else
                {
                    var sk = skeletons[i];
                    for (int c = 0; c < chain.Length; c++)
                    {
                        int node = chain[c];
                        var track = node < tracks.Length ? tracks[node] : null;
                        var rot = track is { RotationTimes.Length: > 0 } ? AnimationClip.RotationAt(track, clipTime) : avatarRig.DefaultRotation[node];
                        System.Numerics.Vector3 pos = track is { PositionTimes.Length: > 0 } ? AnimationClip.PositionAt(track, clipTime) : node == avatarRig.Pelvis ? System.Numerics.Vector3.Zero : avatarRig.DefaultPosition[node];
                        sk.SetBonePosePosition(bones[i][c], EngineV(pos));
                        sk.SetBonePoseRotation(bones[i][c], EngineQ(rot));
                    }
                }
            }
        }
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 2)
        {
            Update(clock.Elapsed.TotalSeconds);
            await Frames(1);
        }
        int cap = (int)(seconds * 5000) + 1024;
        var frameMs = new List<double>(cap);
        var updateMs = new List<double>(cap);
        var cpuMs = new List<double>(cap);
        var gpuMs = new List<double>(cap);
        int[] gc0 = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        var measure = Stopwatch.StartNew();
        long last = Stopwatch.GetTimestamp();
        while (measure.Elapsed.TotalSeconds < seconds)
        {
            long u0 = Stopwatch.GetTimestamp();
            Update(clock.Elapsed.TotalSeconds);
            updateMs.Add((Stopwatch.GetTimestamp() - u0) * 1000.0 / Stopwatch.Frequency);
            await Frames(1);
            long t = Stopwatch.GetTimestamp();
            frameMs.Add((t - last) * 1000.0 / Stopwatch.Frequency);
            last = t;
            cpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(vpRid));
            gpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(vpRid));
        }
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        img.SavePng(Path.Combine(_out, _tag + ".png"));
        _result["mode"] = "attachroute";
        _result["attachroute"] = new
        {
            route, avatars = n, boxes = n * pointNodes.Length, nodes_in_chain = chain.Length, seconds, frames = frameMs.Count, size = $"{size[0]}x{size[1]}", seed,
            frame_ms = Dist(frameMs), over_16_7_ms = frameMs.Count(f => f > 16.7), update_ms = Dist(updateMs),
            render_cpu_ms = Dist(cpuMs), render_gpu_ms = Dist(gpuMs),
            collections = new { gen0 = GC.CollectionCount(0) - gc0[0], gen1 = GC.CollectionCount(1) - gc0[1], gen2 = GC.CollectionCount(2) - gc0[2] },
            process_memory = PeakMemory(),
        };
        foreach (var rid in instances) Rids.Free(rid);
        foreach (var r in roots) { r.GetParent()?.RemoveChild(r); r.Free(); }
        boxMesh.Dispose();
        boxMaterial.Dispose();
        Rids.Free(floor.Instance);
        Rids.Free(floor.Mesh);
        floor.Material.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }

    private static Vector3 EngineV(System.Numerics.Vector3 v) => new(v.X, v.Z, -v.Y);

    private static Quaternion EngineQ(System.Numerics.Quaternion q) => new Quaternion(q.X, q.Z, -q.Y, q.W).Normalized();

    private static Transform3D ToEngineTransform(in System.Numerics.Matrix4x4 frame)
    {
        var r = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.CreateFromRotationMatrix(frame));
        return new Transform3D(new Basis(EngineQ(r)), new Vector3(frame.M41, frame.M43, -frame.M42));
    }
}
