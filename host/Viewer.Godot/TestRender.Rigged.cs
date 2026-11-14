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

/// <summary>
/// Avatars wearing rigged meshes (--tr-avatars=rigged|riggedstress, with --tr-character=&lt;character folder&gt;): four avatars, two
/// standing and two mid-walk, wear synthetic rigged meshes made here from the real skeleton, drawn through the viewer's own path (world
/// state, avatar presenter, prim layer) and skinned in the face shader. The first two wear a shirt (a torso and two sleeves weighted
/// across mTorso, mChest, collar, shoulder, elbow and wrist), a mesh weighted to Bento face joints (boxes on the ears, forehead, nose and
/// chin) and a mesh weighted to collision volumes (bands on belly, upper legs and upper arms, with a bind shape matrix). The other two
/// wear sleeves that carry joint position overrides (the shoulders further out) and a pelvis offset, authored in the overridden pose.
/// The scene is written against what the viewer drew before rigged meshes were drawn (it uses only the prim layer's description for its
/// counts), so it also runs on a build that counts them and draws none, and then fails for the meshes it does not draw.
/// riggedstress attaches and detaches rigged meshes on a walking avatar while drawing, with collections forced all the while.
/// </summary>
public partial class TestRender
{
    private static readonly Guid[] RiggedAgents =
    [
        Guid.Parse("a77a1a00-0000-4000-8000-00000000000a"), Guid.Parse("a77a1b00-0000-4000-8000-00000000000b"),
        Guid.Parse("a77a1c00-0000-4000-8000-00000000000c"), Guid.Parse("a77a1d00-0000-4000-8000-00000000000d"),
    ];

    private static readonly Guid ShirtId = Guid.Parse("a77a2000-0000-4000-8000-000000000001"), FaceId = Guid.Parse("a77a2000-0000-4000-8000-000000000002"),
        VolumesId = Guid.Parse("a77a2000-0000-4000-8000-000000000003"), SleevesId = Guid.Parse("a77a2000-0000-4000-8000-000000000004");

    private sealed record RiggedPiece(Guid Id, MeshGeometry Geometry, int Point, FaceColors Colours);

    /// <summary>One mesh's vertices as they are collected: positions, normals, UVs, up to four influences each.</summary>
    private sealed class MeshBuilder
    {
        public readonly List<float> Positions = [], Normals = [], Uvs = [], Weights = [];
        public readonly List<byte> Joints = [];
        public readonly List<int> Indices = [];
        public int Count => Positions.Count / 3;

        public void Vertex(Vector3 p, Vector3 n, Vector2 uv, params (byte Joint, float Weight)[] influences)
        {
            Positions.AddRange([p.X, p.Y, p.Z]);
            Normals.AddRange([n.X, n.Y, n.Z]);
            Uvs.AddRange([uv.X, uv.Y]);
            // equal joints merge; the four slots are filled in order, unused ones are joint 0 with weight 0
            var merged = influences.GroupBy(i => i.Joint).Select(g => (Joint: g.Key, Weight: g.Sum(i => i.Weight))).Where(i => i.Weight > 0).Take(4).ToArray();
            float sum = merged.Sum(i => i.Weight);
            for (int k = 0; k < 4; k++)
            {
                Joints.Add(k < merged.Length ? merged[k].Joint : (byte)0);
                Weights.Add(k < merged.Length ? merged[k].Weight / sum : 0f);
            }
        }

        public (MeshFace Face, FaceInfluences Influences) Finish() =>
            (new MeshFace(Positions.ToArray(), Normals.ToArray(), Uvs.ToArray(), Indices.ToArray()), new FaceInfluences(Count, Joints.ToArray(), Weights.ToArray(), 0));

        /// <summary>A tube through points, a ring at each point and between them, each ring weighted between the joints at the points it
        /// lies between. Triangles wind anticlockwise seen from outside (SL's winding).</summary>
        public void Tube(Vector3[] points, byte[] joints, float[] radii, int stepsPerSegment, int sides)
        {
            var rings = new List<(Vector3 Centre, float Radius, Vector3 Axis, (byte, float)[] Weights)>();
            for (int i = 0; i + 1 < points.Length; i++)
            {
                var axis = Vector3.Normalize(points[i + 1] - points[i]);
                for (int s = 0; s < stepsPerSegment; s++)
                {
                    float t = (float)s / stepsPerSegment;
                    rings.Add((Vector3.Lerp(points[i], points[i + 1], t), radii[i] + (radii[i + 1] - radii[i]) * t, axis, [(joints[i], 1 - t), (joints[i + 1], t)]));
                }
            }
            int last = points.Length - 1;
            rings.Add((points[last], radii[last], Vector3.Normalize(points[last] - points[last - 1]), [(joints[last], 1f)]));
            int first = Count;
            for (int k = 0; k < rings.Count; k++)
            {
                var (c, r, a, w) = rings[k];
                var h = MathF.Abs(a.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
                var u = Vector3.Normalize(Vector3.Cross(h, a));
                var v = Vector3.Cross(a, u);
                for (int j = 0; j <= sides; j++)
                {
                    float th = 2 * MathF.PI * j / sides;
                    var n = MathF.Cos(th) * u + MathF.Sin(th) * v;
                    Vertex(c + r * n, n, new Vector2((float)j / sides, (float)k / (rings.Count - 1)), w);
                }
            }
            int stride = sides + 1;
            for (int k = 0; k + 1 < rings.Count; k++)
                for (int j = 0; j < sides; j++)
                {
                    int p00 = first + k * stride + j, p01 = p00 + 1, p10 = p00 + stride, p11 = p10 + 1;
                    Indices.AddRange([p00, p01, p11, p00, p11, p10]);
                }
        }

        /// <summary>A box of half-size <paramref name="half"/> bound wholly to one joint.</summary>
        public void Box(Vector3 centre, float half, byte joint)
        {
            (Vector3 N, Vector3 U, Vector3 V)[] sides =
            [
                (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ), (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
                (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX), (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
                (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX),
            ];
            foreach (var (n, u, v) in sides)
            {
                int at = Count;
                Vertex(centre + half * (n - u - v), n, new Vector2(0, 0), (joint, 1f));
                Vertex(centre + half * (n + u - v), n, new Vector2(1, 0), (joint, 1f));
                Vertex(centre + half * (n + u + v), n, new Vector2(1, 1), (joint, 1f));
                Vertex(centre + half * (n - u + v), n, new Vector2(0, 1), (joint, 1f));
                Indices.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);
            }
        }
    }

    private static MeshGeometry RiggedGeometry(IReadOnlyList<(MeshFace Face, FaceInfluences Influences)> faces, MeshSkinBlock block)
    {
        var lod = new MeshLod(faces.Select(f => f.Face).ToList());
        var influences = faces.Select(f => f.Influences).ToList();
        var data = new MeshSkinData(block, null, [influences, influences, influences, influences], [null, null, null, null]);
        return new MeshGeometry([lod, lod, lod, lod], new MeshSkin(block.JointNames.Count, true, faces.Sum(f => f.Influences.Vertices), data));
    }

    /// <summary>The scene's rigged meshes, made from the skeleton's rest pose: the inverse bind of each joint is the inverse of its rest world.</summary>
    private static List<RiggedPiece> RiggedPieces(AvatarRig rig)
    {
        var rest = new Matrix4x4[rig.NodeCount];
        JointWorlds.Rest(rig, rest);
        Vector3 At(string joint) => rest[rig.Find(joint)].Translation;
        Matrix4x4 Inverse(Matrix4x4[] worlds, string joint) => Matrix4x4.Invert(worlds[rig.Find(joint)], out var inv) ? inv : throw new InvalidOperationException(joint);
        MeshSkinBlock Block(string[] names, Matrix4x4[] worlds, Matrix4x4? bindShape = null, Matrix4x4[]? alternate = null, float pelvis = 0f) =>
            new(names, names.Select(n => Inverse(worlds, n)).ToArray(), bindShape ?? Matrix4x4.Identity, alternate ?? [], pelvis, false, false);
        var pieces = new List<RiggedPiece>();

        // a shirt: the torso from the pelvis to the neck and a sleeve down each arm
        {
            string[] torso = ["mPelvis", "mTorso", "mChest", "mNeck"];
            string[] left = ["mCollarLeft", "mShoulderLeft", "mElbowLeft", "mWristLeft"], right = ["mCollarRight", "mShoulderRight", "mElbowRight", "mWristRight"];
            string[] names = [.. torso, .. left, .. right];
            byte Index(string n) => (byte)Array.IndexOf(names, n);
            var faces = new List<(MeshFace, FaceInfluences)>();
            var body = new MeshBuilder();
            body.Tube(torso.Select(At).ToArray(), torso.Select(Index).ToArray(), [0.17f, 0.18f, 0.19f, 0.07f], 3, 20);
            faces.Add(body.Finish());
            foreach (var arm in new[] { left, right })
            {
                var sleeve = new MeshBuilder();
                sleeve.Tube(arm.Select(At).ToArray(), arm.Select(Index).ToArray(), [0.075f, 0.07f, 0.06f, 0.05f], 3, 14);
                faces.Add(sleeve.Finish());
            }
            pieces.Add(new(ShirtId, RiggedGeometry(faces, Block(names, rest)), 9, new FaceColors(new Rgba(0.85f, 0.2f, 0.2f, 1), [null, new Rgba(0.95f, 0.8f, 0.15f, 1), new Rgba(0.95f, 0.8f, 0.15f, 1)])));
        }

        // Bento face joints: a box on each ear tip, the forehead, the nose and the chin
        {
            string[] names = ["mFaceEar2Left", "mFaceEar2Right", "mFaceForeheadCenter", "mFaceNoseCenter", "mFaceChin"];
            var b = new MeshBuilder();
            for (int i = 0; i < names.Length; i++) b.Box(At(names[i]), 0.035f, (byte)i);
            pieces.Add(new(FaceId, RiggedGeometry([b.Finish()], Block(names, rest)), 2, new FaceColors(new Rgba(0.15f, 0.7f, 0.9f, 1))));
        }

        // collision volumes: a band on the belly, each upper leg and each upper arm; the mesh has a bind shape matrix (the vertices are authored 2 cm
        // to one side of where they end up, and the matrix carries them across)
        {
            string[] names = ["BELLY", "L_UPPER_LEG", "R_UPPER_LEG", "L_UPPER_ARM", "R_UPPER_ARM"];
            var shift = new Vector3(-0.02f, 0, 0);
            var b = new MeshBuilder();
            void Band(int joint, Vector3 axis, float r)
            {
                var c = At(names[joint]) + shift;
                b.Tube([c - axis * 0.04f, c + axis * 0.04f], [(byte)joint, (byte)joint], [r, r], 1, 16);
            }
            Band(0, Vector3.UnitZ, 0.2f);
            Band(1, Vector3.UnitZ, 0.115f);
            Band(2, Vector3.UnitZ, 0.115f);
            Band(3, Vector3.UnitY, 0.085f);
            Band(4, Vector3.UnitY, 0.085f);
            pieces.Add(new(VolumesId, RiggedGeometry([b.Finish()], Block(names, rest, bindShape: Matrix4x4.CreateTranslation(0.02f, 0, 0))), 10, new FaceColors(new Rgba(0.2f, 0.75f, 0.3f, 1))));
        }

        // sleeves with joint position overrides: both shoulders 10 cm further out, a pelvis offset of 5 cm. The mesh is authored in that pose, so
        // its inverse binds are the inverses of the overridden rest worlds, and the alternate matrices carry the overridden positions.
        {
            string[] left = ["mCollarLeft", "mShoulderLeft", "mElbowLeft", "mWristLeft"], right = ["mCollarRight", "mShoulderRight", "mElbowRight", "mWristRight"];
            string[] names = [.. left, .. right];
            var positions = (Vector3[])rig.DefaultPosition.Clone();
            positions[rig.Find("mShoulderLeft")] += new Vector3(0, 0.10f, 0);
            positions[rig.Find("mShoulderRight")] += new Vector3(0, -0.10f, 0);
            var moved = new Matrix4x4[rig.NodeCount];
            JointWorlds.Compute(rig, positions, rig.DefaultRotation, moved);
            Vector3 Moved(string j) => moved[rig.Find(j)].Translation;
            var alternate = names.Select(n => Matrix4x4.CreateTranslation(positions[rig.Find(n)])).ToArray();
            var faces = new List<(MeshFace, FaceInfluences)>();
            foreach (var arm in new[] { left, right })
            {
                var sleeve = new MeshBuilder();
                byte Index(string n) => (byte)Array.IndexOf(names, n);
                sleeve.Tube(arm.Select(Moved).ToArray(), arm.Select(Index).ToArray(), [0.08f, 0.075f, 0.065f, 0.055f], 4, 14);
                faces.Add(sleeve.Finish());
            }
            pieces.Add(new(SleevesId, RiggedGeometry(faces, Block(names, moved, alternate: alternate, pelvis: 0.05f)), 1, new FaceColors(new Rgba(0.6f, 0.35f, 0.9f, 1))));
        }
        return pieces;
    }

    private MeshSource RiggedMeshes(IEnumerable<RiggedPiece> pieces)
    {
        var geometry = pieces.ToDictionary(p => p.Id, p => p.Geometry);
        return new MeshSource(new SessionMeshes(new SessionAssets(SharedAssets, _ => true,
            (id, _) => Task.FromResult(geometry.ContainsKey(id) ? AssetFetchResult.Ok(new byte[] { 1 }) : AssetFetchResult.Of(AssetFetchStatus.NotFound))),
            (id, _) => (geometry.GetValueOrDefault(id), null)));
    }

    private static PrimData WornRigged(uint local, uint wearer, RiggedPiece piece) =>
        new(local, PrimId(local), wearer, piece.Point, Vector3.Zero, Quaternion.Identity, new Vector3(1, 1, 1),
            Shape: PrimShape.Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = piece.Id }, Colors: piece.Colours);

    /// <summary>
    /// Four avatars, left to right: standing in a shirt, a Bento mesh and collision-volume bands; mid-walk in the same; standing in sleeves with
    /// joint overrides and a pelvis offset; mid-walk in the same. Drawn from the front (--tr-golden=), from behind (--tr-golden-back=) and the first
    /// avatar's face from close by (--tr-golden-face=). --tr-alter=norigged (negative control): the meshes carry no full skin, as a build that does not
    /// skin them would see them, so none is drawn and the checks must fail.
    /// </summary>
    private async Task<int> AvatarRigged(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets, string? golden, string? goldenBack, string? goldenFace, string? alter)
    {
        var avatarRig = new AvatarRig(body.Skeleton);
        var pieces = RiggedPieces(avatarRig);
        if (alter == "norigged")
            pieces = pieces.Select(p => p with { Geometry = p.Geometry with { Skin = p.Geometry.Skin! with { Data = null } } }).ToList();
        var rig = MakeRig(new Vector2I(1280, 720));
        var scenario = rig.Scenario;
        var floor = Floor(scenario, new Vector3(128, 128, 20), 8f);
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("rigged", 1000, 1000, 256, 256, true));
        using var animations = AnimationSourceFor(assets);
        double now = 0;
        var view = MakeView("server", rig);
        var avatars = new AvatarPresenter(scenario, world, Guid.Empty, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, null, () => now) { HoldWalkWithClock = true };
        using var meshes = RiggedMeshes(pieces);
        var prims = new PrimLayer(scenario, world, meshes, null, _budget);
        var reports = new List<RiggedNotDrawn>();
        prims.RiggedAttachmentMet = reports.Add;
        var drawnReports = new List<RiggedDrawn>();
        prims.RiggedAttachmentDrawn = drawnReports.Add;
        var presenter = new WorldPresenter(world, view, prims: prims, avatars: avatars);
        var size = new Vector3(0.45f, 0.6f, 1.9f);
        var east = Quaternion.Identity;
        uint[] wearers = [800, 900, 1000, 1100];
        float[] ys = [125.9f, 127.3f, 128.7f, 130.1f];
        for (int i = 0; i < 4; i++)
            world.ApplyAvatar(FixtureSim, new(wearers[i], RiggedAgents[i], $"Rigged Test {i + 1}", new(128, ys[i], 20.95f), east, size));
        foreach (int i in new[] { 1, 3 }) world.ApplyAnimations(FixtureSim, RiggedAgents[i], [new(WalkId, 2)]);
        int expected = 0;
        for (int i = 0; i < 4; i++)
        {
            var wear = i < 2 ? new[] { ShirtId, FaceId, VolumesId } : new[] { SleevesId };
            for (int k = 0; k < wear.Length; k++)
            {
                world.ApplyObject(FixtureSim, WornRigged((uint)(wearers[i] * 10 + k), wearers[i], pieces.First(p => p.Id == wear[k])), eventIsAttachment: true);
                expected++;
            }
        }
        var cam = rig.Camera;
        cam.Fov = 40f; cam.Near = 0.1f; cam.Far = 100f;
        void Aim(bool front)
        {
            cam.Position = new GVector3(front ? 134f : 122f, 21.3f, -128f);
            cam.LookAt(new GVector3(128f, 20.95f, -128f), GVector3.Up);
            prims.Camera = cam.GlobalPosition;   // the level of detail of a worn rigged mesh follows the camera's distance to its wearer
        }
        Aim(true);
        var sw = Stopwatch.StartNew();
        bool Settled() => avatars.Drawn == 4 && avatars.BodiesPending == 0 && prims.Instances == expected && prims.Dirty == 0 && animations.Waiting == 0 && animations.InFlight == 0 && animations.Ready >= 1
            && (Figure(prims.Describe(), "rigged_shown") ?? expected) == expected && (Figure(prims.Describe(), "rigged_lod_min") ?? 3) == 3;   // near (6 m): the highest level
        while (!Settled() && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            prims.UpdateLods();
            presenter.Frame();
            await Frames(1);
        }
        if (!Settled()) _failures.Add($"not settled in 30 s: avatars drawn {avatars.Drawn} of 4, rigged meshes drawn {prims.Instances} of {expected}, dirty {prims.Dirty}, animations ready {animations.Ready}");
        presenter.Frame();
        now = 0.6;
        presenter.Frame();
        await SettlePoses(avatars, presenter);
        await Frames(8);
        presenter.Frame();
        ExpectBodiesSettled(avatars, "front view");
        var front = rig.Texture.GetImage();
        front.Convert(Image.Format.Rgba8);
        front.SavePng(Path.Combine(_out, _tag + ".png"));

        // joint overrides and the pelvis offset, measured on the avatars' left hands (the point hangs from mWristLeft): standing avatar 1 (no overrides)
        // against standing avatar 3 (shoulders 10 cm out along y, the whole avatar 5 cm higher), each against its own pelvis point
        System.Numerics.Matrix4x4 hand1 = default, hand3 = default, pelvis1 = default, pelvis3 = default;
        bool measured = avatars.TryPoint(800, 5, out hand1) & avatars.TryPoint(1000, 5, out hand3) & avatars.TryPoint(800, 10, out pelvis1) & avatars.TryPoint(1000, 10, out pelvis3);
        double armOut = measured ? ((hand3.Translation - pelvis3.Translation) - (hand1.Translation - pelvis1.Translation)).Length() : double.NaN;   // the shoulder moves 10 cm in its parent's frame; both stand the same, so the hand moves 10 cm
        double lifted = measured ? (pelvis3.Translation.Z - pelvis1.Translation.Z) : double.NaN;
        if (!(Math.Abs(armOut - 0.10) < 0.002)) _failures.Add($"the left hand of the avatar wearing joint overrides is {armOut:F4} m from where it is on the avatar without them (expected 0.10 m)");
        if (!(Math.Abs(lifted - 0.05) < 0.002)) _failures.Add($"the avatar wearing a pelvis offset is {lifted:F4} m higher than the one without (expected 0.05 m)");

        Aim(false);
        presenter.Frame();
        await Frames(8);
        presenter.Frame();
        ExpectBodiesSettled(avatars, "back view");
        var back = rig.Texture.GetImage();
        back.Convert(Image.Format.Rgba8);
        string backPng = Path.Combine(_out, _tag + "-back.png");
        back.SavePng(backPng);
        // the first avatar's face from the front, close by
        cam.Fov = 30f;
        cam.Position = new GVector3(129.2f, 21.85f, -ys[0]);
        cam.LookAt(new GVector3(128f, 21.75f, -ys[0]), GVector3.Up);
        prims.Camera = cam.GlobalPosition;
        presenter.Frame();
        await Frames(8);
        presenter.Frame();
        ExpectBodiesSettled(avatars, "face view");
        var faceImage = rig.Texture.GetImage();
        faceImage.Convert(Image.Format.Rgba8);
        string facePng = Path.Combine(_out, _tag + "-face.png");
        faceImage.SavePng(facePng);

        // far away: the same pieces, drawn at a lower level of detail chosen from the wearer's distance and the size of its box, none lost
        int nearLod = Figure(prims.Describe(), "rigged_lod_max") ?? -1;
        cam.Position = new GVector3(128f + 90f, 21.3f, -128f);
        cam.LookAt(new GVector3(128f, 20.95f, -128f), GVector3.Up);
        prims.Camera = cam.GlobalPosition;
        sw.Restart();
        while ((Figure(prims.Describe(), "rigged_lod_max") ?? 9) >= nearLod && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            prims.UpdateLods();
            presenter.Frame();
            await Frames(1);
        }
        await Frames(4);
        presenter.Frame();
        int farLod = Figure(prims.Describe(), "rigged_lod_max") ?? -1, farShown = Figure(prims.Describe(), "rigged_shown") ?? -1;
        if (!(farLod >= 0 && farLod < nearLod)) _failures.Add($"far away (90 m) the rigged meshes are at level {farLod}, near they are at {nearLod}: expected a lower level far away");
        if (farShown != expected) _failures.Add($"{farShown} of {expected} rigged meshes are drawn 90 m away");

        var description = prims.Describe();
        _result["mode"] = "avatars-rigged";
        _result["avatars_rigged"] = new
        {
            expected_meshes = expected, meshes_drawn = prims.Instances, avatars_drawn = avatars.Drawn, arm_out_m = Math.Round(armOut, 4), lifted_m = Math.Round(lifted, 4),
            prims = description, presenter = avatars.Describe(), altered = alter, png = _tag + ".png", png_back = Path.GetFileName(backPng), png_face = Path.GetFileName(facePng),
        };
        if (prims.Instances != expected) _failures.Add($"{prims.Instances} of {expected} worn rigged meshes are drawn");
        if (Figure(description, "rigged_objects") != expected) _failures.Add($"rigged objects reported: {Figure(description, "rigged_objects")?.ToString() ?? "not reported"}, expected {expected}");
        if (Figure(description, "skipped_rigged_attachments") != 0) _failures.Add($"{Figure(description, "skipped_rigged_attachments")} rigged meshes worn are counted and not drawn, expected none");
        _result["rigged_reports"] = reports.Select(r => new { r.Why, r.Temporary, r.Point, asset = r.Asset.ToString(), r.WearerName }).ToArray();
        if (alter == "norigged")
        {
            if (drawnReports.Count(d => d.PlainBecause == RiggedWhy.SkinNotRead) != expected) _failures.Add($"a mesh with no readable skin: {drawnReports.Count(d => d.PlainBecause == RiggedWhy.SkinNotRead)} of {expected} reported as drawn plain because the skin was not read");
        }
        else if (drawnReports.Count != expected || drawnReports.Any(d => d.Joints <= 0 || d.Lod < 0)) _failures.Add($"{drawnReports.Count} of {expected} drawn rigged meshes were reported as drawn once each, with their joints and level");
        if (alter != "norigged" && reports.Any(r => !r.Temporary)) _failures.Add($"rigged meshes reported not drawn for a lasting reason: {string.Join(", ", reports.Where(r => !r.Temporary).Select(r => r.Why))}");
        if (golden is not null) DiffGolden(front, golden);
        if (goldenBack is not null) DiffGolden(back, goldenBack, "diff_back");
        if (goldenFace is not null) DiffGolden(faceImage, goldenFace, "diff_face");
        presenter.Dispose();
        if (avatars.LiveResources != 0) _failures.Add($"{avatars.LiveResources} avatar resources live after the presenter went");
        prims.Clear();
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        Rids.Free(floor.Instance);
        Rids.Free(floor.Mesh);
        floor.Material.Dispose();
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// A rigged mesh whose faces have textures: one standing avatar wears the shirt with the texture zoo's grid on every face (--tr-textures=, the zoo's
    /// images), drawn through the viewer's texture path (residency, GPU textures, the skinned face shader with the texture bound), and then the
    /// same shirt with colours only; the two pictures must differ where the shirt is.
    /// </summary>
    private async Task<int> AvatarRiggedTextures(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? texDir)
    {
        if (texDir is null) { _failures.Add("--tr-textures= is needed (the texture zoo's images)"); return 1; }
        var avatarRig = new AvatarRig(body.Skeleton);
        var shirt = RiggedPieces(avatarRig).First(p => p.Id == ShirtId);
        var rig = MakeRig(new Vector2I(640, 640));
        var scenario = rig.Scenario;
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("rigged-textures", 1000, 1000, 256, 256, true));
        double now = 0;
        var view = MakeView("server", rig);
        using var textures = TextureSourceFor(texDir, TextureZoo.Images);
        using var meshes = RiggedMeshes([shirt]);
        var avatars = new AvatarPresenter(scenario, world, Guid.Empty, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), null, null, null, () => now);
        var prims = new PrimLayer(scenario, world, meshes, textures, _budget);
        var presenter = new WorldPresenter(world, view, prims: prims, avatars: avatars);
        var cam = rig.Camera;
        cam.Fov = 30f; cam.Near = 0.1f; cam.Far = 100f;
        cam.Position = new GVector3(132f, 21.4f, -128f);
        cam.LookAt(new GVector3(128f, 21.4f, -128f), GVector3.Up);
        prims.Camera = cam.GlobalPosition;
        prims.ResidencyView = PrimLayer.ViewOf(cam, (int)rig.Viewport.Size.X, (int)rig.Viewport.Size.Y);
        world.ApplyAvatar(FixtureSim, new(800, RiggedAgents[0], "Textured Test", new(128, 128, 20.95f), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
        var texture = TextureZoo.Images.Keys.First();
        world.ApplyObject(FixtureSim, WornRigged(8001, 800, shirt) with { Textures = new FaceTextures(FaceTexture.Plain(texture)), Colors = new FaceColors(new Rgba(1, 1, 1, 1)) }, eventIsAttachment: true);
        var sw = Stopwatch.StartNew();
        bool Ready() => prims.Draws(8001) && avatars.Drawn == 1 && prims.Dirty == 0 && prims.GpuTextures >= 1 && prims.TexturesWaiting == 0;
        while (!Ready() && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            presenter.Frame();
            await Frames(1);
        }
        if (!Ready()) _failures.Add($"not settled in 30 s: drawn {prims.Draws(8001)}, avatars {avatars.Drawn}, dirty {prims.Dirty}, gpu textures {prims.GpuTextures}, waiting {prims.TexturesWaiting}");
        presenter.Frame();
        await Frames(8);
        presenter.Frame();
        var textured = rig.Texture.GetImage();
        textured.Convert(Image.Format.Rgba8);
        textured.SavePng(Path.Combine(_out, _tag + ".png"));
        int skinnedMaterials = Figure(prims.Describe(), "skinned_materials") ?? -1;
        // the same shirt in colours only
        world.Kill(FixtureSim, [8001]);
        world.ApplyObject(FixtureSim, WornRigged(8002, 800, shirt), eventIsAttachment: true);
        for (int i = 0; i < 40 && !prims.Draws(8002); i++) { presenter.Frame(); await Frames(1); }
        presenter.Frame();
        await Frames(8);
        presenter.Frame();
        var plain = rig.Texture.GetImage();
        plain.Convert(Image.Format.Rgba8);
        plain.SavePng(Path.Combine(_out, _tag + "-plain.png"));
        var diff = ImageDiff.Compare(textured.GetData(), plain.GetData(), textured.GetWidth(), textured.GetHeight(), 4, new DiffTolerance(ChannelThreshold: 24, MaxDifferingFraction: 1, MaxMeanAbsError: 255));
        _result["mode"] = "avatars-rigged-textures";
        _result["avatars_rigged_textures"] = new { drawn = prims.Draws(8002), gpu_textures = prims.GpuTextures, skinned_materials = skinnedMaterials, differing_pixels = diff.Differing, differing_fraction = Math.Round(diff.DifferingFraction, 5), prims = prims.Describe() };
        if (skinnedMaterials < 1) _failures.Add("no skinned material was made for the textured faces");
        if (diff.DifferingFraction < 0.005) _failures.Add($"the textured and the colour-only shirts differ in only {diff.DifferingFraction:P3} of the picture (expected the texture to show)");
        presenter.Dispose();
        prims.Clear();
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }
}
