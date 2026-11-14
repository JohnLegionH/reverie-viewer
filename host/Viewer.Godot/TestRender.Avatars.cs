// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.Imaging;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The avatar cases (--tr-avatars=prep|render|crowd, with --tr-character=&lt;character folder&gt;, read in place):
/// prep checks the skinned surfaces byte for byte against the engine's own packing; render draws one avatar in its
/// default pose and one mid-walk (the grid's walk from --tr-assets=), optionally with the avatar's own bakes
/// (--tr-bakes); crowd measures N walking avatars on an empty stage or a replayed capture; live draws two avatars from
/// synthetic region updates through the viewer's own path (the world state, the avatar presenter, the follow camera).
/// </summary>
public partial class TestRender
{
    /// <summary>The grid's built-in stand and walk animations (their asset ids are the same on every grid).</summary>
    private static readonly Guid StandId = Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f"), WalkId = Guid.Parse("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0");

    private async Task<int> AvatarRun(string[] args, string mode)
    {
        string dir = Arg(args, "--tr-character=") ?? throw new ArgumentException("--tr-avatars needs --tr-character=<folder>");
        var sw = Stopwatch.StartNew();
        var body = AvatarBody.Load(dir);
        double loadMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var surfaces = AvatarLayer.Prepare(body);
        double prepareMs = sw.Elapsed.TotalMilliseconds;
        string? assets = Arg(args, "--tr-assets=");
        _result["avatar_body"] = new
        {
            parts = body.Parts.Select(p => new { type = p.Type, bake = p.BakeSlot, vertices = p.Mesh.VertexCount, triangles = p.Mesh.Indices.Length / 3 }).ToArray(),
            vertices = body.VertexCount, triangles = body.TriangleCount, palette = body.Palette.Length,
            bones = body.Skeleton.BoneCount, collision_volumes = body.Skeleton.CollisionVolumeCount, attachment_points = body.Skeleton.AttachmentPoints.Count,
            foot_height = Math.Round(body.FootHeight, 4), height = Math.Round(body.Height, 4),
            load_ms = Math.Round(loadMs, 2), prepare_ms = Math.Round(prepareMs, 2),
        };
        WorldPresenter.NoSharedBudget = Arg(args, "--tr-alter=nobudget") is not null;   // the control: the avatars' work as it was before the shared budget
        AvatarLayer.PoseOnWorkers = Arg(args, "--tr-alter=poseonmain") is null;          // the control: poses worked out on the main thread, as before the workers
        return mode switch
        {
            // crowd:N (synthetic outfits on a default avatar each), crowd:N,real (each avatar shaped, baked at the server sizes, half in a mesh body),
            // arrive:N (the real crowd, all arriving within --tr-arrive-seconds, default 1)
            var m when m.StartsWith("crowd:", StringComparison.Ordinal) => await AvatarOutfitCrowd(body, surfaces, assets, args,
                int.Parse(m["crowd:".Length..].Split(',')[0], System.Globalization.CultureInfo.InvariantCulture), real: m.EndsWith(",real", StringComparison.Ordinal)),
            var m when m.StartsWith("arrive:", StringComparison.Ordinal) => await AvatarOutfitCrowd(body, surfaces, assets, args,
                int.Parse(m["arrive:".Length..], System.Globalization.CultureInfo.InvariantCulture), real: true, arrival: true),
            "prep" => AvatarPrep(body, AvatarLayer.PrepareUnshaped(body), Arg(args, "--tr-alter=")),   // the packing check compares the arrays as the files have them; the shared body's relaxed hands are a morph of one of them
            "render" => await AvatarRender(body, surfaces, assets, Arg(args, "--tr-bakes") is not null, Arg(args, "--tr-golden="), Arg(args, "--tr-alter=")),
            "face" => await AvatarFace(body, surfaces, assets, Arg(args, "--tr-bakes") is not null, Arg(args, "--tr-golden="), Arg(args, "--tr-alter=")),
            "crowd" => await AvatarCrowd(body, surfaces, assets, args),
            "shapelook" => await AvatarShapeLook(body, surfaces, Arg(args, "--tr-alter=")),
            "faceshape" => await AvatarFaceShape(body, surfaces, Arg(args, "--tr-alter=")),
            "morphlook" => await AvatarMorphLook(body, surfaces, Arg(args, "--tr-alter=")),
            "editlook" => await AvatarEditLook(body, surfaces, dir, Arg(args, "--tr-golden="), Arg(args, "--tr-alter=")),
            "bakelook" => await AvatarBakeLook(body, surfaces, Arg(args, "--tr-textures="), Arg(args, "--tr-alter=")),
            "capturelook" => await AvatarCaptureLook(body, surfaces, assets, args),
            "live" => await AvatarLive(body, surfaces, assets, Arg(args, "--tr-golden="), Arg(args, "--tr-alter=")),
            "attach" => await AvatarAttach(body, surfaces, assets, Arg(args, "--tr-golden="), Arg(args, "--tr-golden-back="), Arg(args, "--tr-alter=")),
            "attachcrowd" => await AvatarAttachCrowd(body, surfaces, assets, args),
            "attachroute" => await AvatarAttachRoute(body, assets, args),
            "attachtex" => await AvatarAttachTextures(body, surfaces, args),
            "rigged" => await AvatarRigged(body, surfaces, assets, Arg(args, "--tr-golden="), Arg(args, "--tr-golden-back="), Arg(args, "--tr-golden-face="), Arg(args, "--tr-alter=")),
            "riggedblend" => await AvatarRiggedBlend(body, surfaces, assets, Arg(args, "--tr-alter=")),
            "riggedtex" => await AvatarRiggedTextures(body, surfaces, Arg(args, "--tr-textures=")),
            "riggedstress" => await AvatarRiggedStress(body, surfaces, assets, int.Parse(Arg(args, "--tr-cycles=") ?? "20")),
            "riggedcrossing" => await AvatarRiggedCrossing(body, surfaces, assets),
            "riggedstall" => await AvatarRiggedStall(body, surfaces, assets, int.Parse(Arg(args, "--tr-stall-meshes=") ?? "24"), double.Parse(Arg(args, "--tr-stall-limit-ms=") ?? PrimLayer.StepBudgetMs.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture)),
            _ => throw new ArgumentException("--tr-avatars must be prep, render, shapelook, morphlook, editlook, crowd, live, attach, attachcrowd, rigged, riggedblend, riggedstress, riggedstall or crowd:<N>"),
        };
    }

    private static AnimationClip? ClipFrom(string? assets, Guid id)
    {
        if (assets is null) return null;
        var path = new Viewer.Core.Assets.LocalAssetCache(assets).AnimationPath(id);
        return File.Exists(path) ? AnimationClip.Parse(File.ReadAllBytes(path)) : null;
    }

    /// <summary>Every body part's prepared surface against the engine packing the same arrays: vertex, attribute, index
    /// and skin data and the format, byte for byte; then the handed-over mesh read back. --tr-alter=skinbyte (negative
    /// control): one skin byte of every part changed, every part must be caught.</summary>
    private int AvatarPrep(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? alter)
    {
        var rows = new List<object>();
        int identical = 0;
        object? layout = null;
        for (int i = 0; i < body.Parts.Count; i++)
        {
            var part = body.Parts[i];
            var mine = surfaces[i];
            if (alter == "skinbyte") { var sk = (byte[])mine.SkinData.Clone(); sk[0] ^= 1; mine = mine with { SkinData = sk }; }
            Rid today = EnginePackedSkinned(part);
            Rid handed = Rids.MeshCreate("test mesh");
            AvatarLayer.HandOver(handed, mine);
            var t = RenderingServer.MeshGetSurface(today, 0);
            var h = RenderingServer.MeshGetSurface(handed, 0);
            var diffs = new List<string>();
            var s = mine.Surface;
            void Bytes(string key, byte[] ours)
            {
                var theirs = t.TryGetValue(key, out var v) ? v.AsByteArray() : [];
                if (!theirs.AsSpan().SequenceEqual(ours))
                {
                    int n = Math.Min(theirs.Length, ours.Length), at = n;
                    for (int k = 0; k < n; k++) if (theirs[k] != ours[k]) { at = k; break; }
                    diffs.Add($"{key} {ours.Length} bytes vs {theirs.Length}, first difference at {at} (ours {Convert.ToHexString(ours.Skip(at / 16 * 16).Take(32).ToArray())}, engine {Convert.ToHexString(theirs.Skip(at / 16 * 16).Take(32).ToArray())})");
                }
            }
            Bytes("vertex_data", s.VertexData);
            Bytes("attribute_data", s.AttributeData);
            Bytes("index_data", s.IndexData);
            Bytes("skin_data", mine.SkinData);
            ulong format = (ulong)t["format"].AsInt64();
            if (format != s.Format) diffs.Add($"format {s.Format:X} vs {format:X}");
            if (t["vertex_count"].AsInt32() != s.VertexCount) diffs.Add($"vertex_count {s.VertexCount} vs {t["vertex_count"].AsInt32()}");
            var ta = t["aabb"].AsAabb();
            var pa = new Aabb(new Vector3(s.Aabb[0], s.Aabb[1], s.Aabb[2]), new Vector3(s.Aabb[3], s.Aabb[4], s.Aabb[5]));
            if (ta.Position != pa.Position || ta.Size != pa.Size) diffs.Add($"aabb {pa} vs {ta}");
            // read back: every key the engine keeps for the handed-over surface matches the packed one, except the
            // per-bone boxes, which the engine works out only when it packs (the avatar instance has its own box)
            var onlyPacked = new List<string>();
            foreach (var key in t.Keys.Select(k => k.AsString()).Union(h.Keys.Select(k => k.AsString())).Distinct())
            {
                bool inT = t.TryGetValue(key, out var tv), inH = h.TryGetValue(key, out var hv);
                if (inT && !inH) { onlyPacked.Add(key); continue; }
                if (!inT) { diffs.Add($"read back: extra key {key}"); continue; }
                bool eq = tv.VariantType == Variant.Type.PackedByteArray ? tv.AsByteArray().AsSpan().SequenceEqual(hv.AsByteArray()) : GD.VarToStr(tv) == GD.VarToStr(hv);
                if (!eq) diffs.Add($"read back: {key} differs");
            }
            if (onlyPacked.Any(k => k != "bone_aabbs")) diffs.Add("read back: missing " + string.Join(",", onlyPacked.Where(k => k != "bone_aabbs")));
            layout ??= new
            {
                keys = t.Keys.Select(k => k.AsString()).ToArray(), format = format.ToString("X"),
                skin_bytes = t.TryGetValue("skin_data", out var sd) ? sd.AsByteArray().Length : -1,
                skin_head = t.TryGetValue("skin_data", out var sd2) ? Convert.ToHexString(sd2.AsByteArray().Take(32).ToArray()) : null,
                vertex_stride = RenderingServer.MeshSurfaceGetFormatVertexStride((RenderingServer.ArrayFormat)format, s.VertexCount),
                skin_stride = RenderingServer.MeshSurfaceGetFormatSkinStride((RenderingServer.ArrayFormat)format, s.VertexCount),
            };
            if (diffs.Count == 0) identical++;
            rows.Add(new { part = part.Type, vertices = s.VertexCount, identical = diffs.Count == 0, only_when_packed = onlyPacked, diffs });
            Rids.Free(today);
            Rids.Free(handed);
        }
        _result["mode"] = "avatarprep";
        _result["avatarprep"] = new { parts = body.Parts.Count, identical, altered = alter, layout, cases = rows };
        if (alter == "skinbyte")
        {
            if (identical != 0) _failures.Add($"negative control: {identical} altered parts compared identical");
            return identical == 0 ? 0 : 1;
        }
        if (identical != body.Parts.Count) _failures.Add($"{body.Parts.Count - identical} of {body.Parts.Count} parts differ from the engine's packing");
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>One avatar in its default pose (left) and one mid-walk (right), facing the camera on a grey floor; with
    /// --tr-bakes both wear the avatar's own bakes (self-bakes.json in the asset cache). --tr-alter=still (negative
    /// control): the right one in its default pose too; the diff must fail.</summary>
    private async Task<int> AvatarRender(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets, bool withBakes, string? golden, string? alter)
    {
        var walk = ClipFrom(assets, WalkId);
        if (walk is null) { _failures.Add("the walk animation is not in the asset cache (--tr-assets=)"); return 1; }
        var rig = MakeRig();
        var scenario = rig.Scenario;
        var floor = Floor(scenario, new System.Numerics.Vector3(128, 128, 20), 8f);
        var layer = new AvatarLayer(scenario, body, surfaces);
        var textures = new List<ImageTexture>();
        Dictionary<string, Texture2D>? bakes = withBakes ? LoadBakes(assets!, textures) : null;
        if (withBakes && (bakes is null || bakes.Count == 0)) { _failures.Add("no bakes in the asset cache (self-bakes.json)"); return 1; }
        // SL: an avatar faces +x at yaw 0; the camera stands east of them looking west, so south is on the left
        double mid = walk.LoopIn + (walk.LoopOut - walk.LoopIn) * 0.5;
        layer.Add(new(128, 127.35f, 20), 0f, null, 0, bakes);
        layer.Add(new(128, 128.65f, 20), 0f, alter == "still" ? null : walk, mid, bakes);
        layer.Frame(0);
        var cam = rig.Camera;
        cam.Fov = 40f; cam.Near = 0.1f; cam.Far = 100f;
        cam.Position = new Vector3(132.6f, 21.15f, -128f);
        cam.LookAt(new Vector3(128f, 20.95f, -128f), Vector3.Up);
        await Frames(8);
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        string png = Path.Combine(_out, _tag + ".png");
        img.SavePng(png);
        _result["mode"] = "avatars";
        _result["avatars"] = new
        {
            count = layer.Count, palette = layer.PaletteSize, surfaces = layer.Surfaces, engine_calls_last_frame = layer.CallsLastFrame,
            walk = new { duration = walk.Duration, loop_in = walk.LoopIn, loop_out = walk.LoopOut, joints = walk.Tracks.Count, time = Math.Round(walk.TimeAt(mid), 4) },
            bakes = bakes?.Keys.OrderBy(k => k).ToArray(), altered = alter, png = Path.GetFileName(png),
        };
        if (golden is not null) DiffGolden(img, golden);
        layer.Dispose();
        foreach (var t in textures) t.Dispose();
        Rids.Free(floor.Instance);
        Rids.Free(floor.Mesh);
        floor.Material.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// The default avatar's face from the front, close up, in its default pose (with --tr-bakes, wearing the avatar's own bakes): the
    /// eyes' placement, size and texture mapping as drawn. Reports where each eyeball sits in the body's frame (centre and extent,
    /// the way the checks read it) and the eye joints' own positions. --tr-golden= compares the picture.
    /// </summary>
    private async Task<int> AvatarFace(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets, bool withBakes, string? golden, string? alter = null)
    {
        var rig = MakeRig();
        // --tr-alter=legacysky: the face under the stock Midday (Legacy) sky (the official classic lighting), so the classic response is covered by a picture of an avatar
        EnvironmentLayer? sky = null;
        if (alter == "legacysky")
        {
            sky = new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun) { HdrEnabled = true };
            sky.Apply(Viewer.Core.World.RegionEnvironment.Default, SkinFrame(0f), null, 256, 256);
        }
        // --tr-alter=eepsky: the same face under a stock Midday sky that sets a reflection probe ambiance (the tone-mapped path), so the ambient the sky probe gives is covered by a picture of an avatar
        if (alter == "eepsky")
        {
            // the picture is taken twice: first with no automatic exposure, then with the exposure the scene settles at (see below)
            EnvironmentLayer.DynamicExposure = false;
            sky = new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun) { HdrEnabled = true };
            sky.Apply(Viewer.Core.World.RegionEnvironment.Default, SkinFrame(0.5f), null, 256, 256);
            for (int i = 0; i < 60 && !sky.ProbeSettled; i++) { sky.TickProbe(0); await Frames(1); }
            if (!sky.ProbeSettled) _failures.Add("the sky probe's capture did not finish in 60 frames");
        }
        var floor = Floor(rig.Scenario, new System.Numerics.Vector3(128, 128, 20), 2f);
        var layer = new AvatarLayer(rig.Scenario, body, surfaces);
        var textures = new List<ImageTexture>();
        Dictionary<string, Texture2D>? bakes = withBakes ? LoadBakes(assets!, textures) : null;
        if (withBakes && (bakes is null || bakes.Count == 0)) { _failures.Add("no bakes in the asset cache (self-bakes.json)"); return 1; }
        layer.Add(new(128, 128, 20), 0f, null, 0, bakes);
        layer.Frame(0);
        var sk = body.Skeleton;
        var left = sk.DefaultWorld[sk.IndexOf("mEyeLeft")].Translation;
        var right = sk.DefaultWorld[sk.IndexOf("mEyeRight")].Translation;
        float eyeHeight = (left.Z + right.Z) / 2 - body.FootHeight;
        var cam = rig.Camera;
        cam.Fov = 24f; cam.Near = 0.05f; cam.Far = 50f;
        cam.Position = new Vector3(128.8f, 20f + eyeHeight, -128f);
        cam.LookAt(new Vector3(128f, 20f + eyeHeight, -128f), Vector3.Up);
        await Frames(8);
        Image? plain = null;
        (float Scale, float MeanLuminance, float Target) measured = default;
        if (alter == "eepsky")
        {
            // The automatic exposure moves towards its value at the official speed per frame interval, so a picture taken a few frames after it
            // starts depends on how long those frames took (a served run and a lone run differ). Take the picture without it, then switch it
            // on with the jump-to-the-settled-value test switch (the one the haze cases use), so the picture is the settled one whatever the timing.
            plain = rig.Texture.GetImage();
            plain.Convert(Image.Format.Rgba8);
            EnvironmentLayer.DynamicExposure = true; HdrTonemapEffect.ExposureInstant = true; HdrTonemapEffect.ReadExposure = true;
            sky!.Apply(Viewer.Core.World.RegionEnvironment.Default, SkinFrame(0.5f), null, 256, 256);
            for (int i = 0; i < 60 && !sky.ProbeSettled; i++) { sky.TickProbe(0); await Frames(1); }
            await Frames(8);
            measured = HdrTonemapEffect.LastExposure;
        }
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        if (plain is not null)
            ExposureModelCheck(plain, img, measured, 0.5f);
        string png = Path.Combine(_out, _tag + ".png");
        img.SavePng(png);
        object Eye(string type)
        {
            var p = body.Parts.First(x => x.Type == type);
            var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
            foreach (var v in p.Positions) { min = System.Numerics.Vector3.Min(min, v); max = System.Numerics.Vector3.Max(max, v); }
            var c = (min + max) / 2;
            return new { part = type, centre = new[] { Math.Round(c.X, 4), Math.Round(c.Y, 4), Math.Round(c.Z, 4) }, extent = new[] { Math.Round(max.X - min.X, 4), Math.Round(max.Y - min.Y, 4), Math.Round(max.Z - min.Z, 4) } };
        }
        // Before the eyes' own bake arrives the eyeballs show their plain white (the sclera), not the skin tone the body parts wear: the eye's
        // pixel is grey-white (blue about as strong as red) where skin is warm (blue well under red). Read at each eyeball's centre.
        var eyeRatios = new List<double>();
        if (!withBakes)
        {
            foreach (var type in new[] { "eyeBallLeftMesh", "eyeBallRightMesh" })
            {
                var p = body.Parts.First(x => x.Type == type);
                var c = new System.Numerics.Vector3(p.Positions.Average(v => v.X), p.Positions.Average(v => v.Y), p.Positions.Average(v => v.Z));
                var world = new Vector3(128f + c.X, 20f + c.Z - body.FootHeight, -(128f + c.Y));
                var at = cam.UnprojectPosition(world);
                double r = 0, b = 0;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        var px = img.GetPixel(Math.Clamp((int)at.X + dx, 0, img.GetWidth() - 1), Math.Clamp((int)at.Y + dy, 0, img.GetHeight() - 1));
                        r += px.R; b += px.B;
                    }
                eyeRatios.Add(r > 0 ? b / r : 0);
            }
            if (eyeRatios.Any(x => x < 0.85)) _failures.Add($"an eyeball is drawn in skin tone before its bake arrives (blue/red at its centre {string.Join(", ", eyeRatios.Select(x => x.ToString("F2")))}; white reads about 1.0, skin 0.7)");
        }
        _result["mode"] = "avatar-face";
        _result["avatar_face"] = new
        {
            eyes = new[] { Eye("eyeBallLeftMesh"), Eye("eyeBallRightMesh") },
            joints = new { left = new[] { left.X, left.Y, left.Z }, right = new[] { right.X, right.Y, right.Z } },
            eye_blue_over_red = eyeRatios.Select(x => Math.Round(x, 3)).ToArray(), eye_height_above_soles = Math.Round(eyeHeight, 4), bakes = bakes?.Keys.OrderBy(k => k).ToArray(), png = Path.GetFileName(png),
        };
        if (golden is not null) DiffGolden(img, golden);
        EnvironmentLayer.DynamicExposure = true; HdrTonemapEffect.ExposureInstant = false; HdrTonemapEffect.ReadExposure = false;
        layer.Dispose();
        sky?.Clear();
        foreach (var t in textures) t.Dispose();
        Rids.Free(floor.Instance);
        Rids.Free(floor.Mesh);
        floor.Material.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Where the skin patches of the face picture sit, as fractions of the picture's width and height.</summary>
    private static readonly (string Name, float X, float Y)[] FacePatches = [("forehead", 0.50f, 0.27f), ("left cheek", 0.40f, 0.62f), ("right cheek", 0.60f, 0.62f), ("chin", 0.50f, 0.80f)];

    /// <summary>The colour that, put through the tone mapper at this exposure, gives <paramref name="target"/> (Newton's method on the CPU model; the mapper is
    /// close to the identity for the mid-range colours of skin, so the start is the target itself).</summary>
    private static System.Numerics.Vector3 InvertToneMap(System.Numerics.Vector3 target, float exposure, Viewer.Core.World.ToneMapType type, float mix)
    {
        var c = target / exposure;
        for (int i = 0; i < 40; i++)
        {
            var at = Viewer.Core.World.ToneMap.Apply(c, exposure, type, mix);
            var f = at - target;
            if (f.Length() < 1e-6f) break;
            const float h = 1e-3f;
            var jx = (Viewer.Core.World.ToneMap.Apply(c + System.Numerics.Vector3.UnitX * h, exposure, type, mix) - at) / h;
            var jy = (Viewer.Core.World.ToneMap.Apply(c + System.Numerics.Vector3.UnitY * h, exposure, type, mix) - at) / h;
            var jz = (Viewer.Core.World.ToneMap.Apply(c + System.Numerics.Vector3.UnitZ * h, exposure, type, mix) - at) / h;
            float det = System.Numerics.Vector3.Dot(jx, System.Numerics.Vector3.Cross(jy, jz));
            if (MathF.Abs(det) < 1e-8f) break;
            var d = new System.Numerics.Vector3(System.Numerics.Vector3.Dot(f, System.Numerics.Vector3.Cross(jy, jz)), System.Numerics.Vector3.Dot(jx, System.Numerics.Vector3.Cross(f, jz)),
                System.Numerics.Vector3.Dot(jx, System.Numerics.Vector3.Cross(jy, f))) / det;
            c = System.Numerics.Vector3.Max(c - d, System.Numerics.Vector3.Zero);
        }
        return c;
    }

    /// <summary>A patch's mean colour in linear light.</summary>
    private static System.Numerics.Vector3 PatchMean(Image img, float fx, float fy)
    {
        int cx = (int)(fx * img.GetWidth()), cy = (int)(fy * img.GetHeight());
        var sum = System.Numerics.Vector3.Zero;
        int n = 0;
        for (int dx = -3; dx <= 3; dx++)
            for (int dy = -3; dy <= 3; dy++)
            {
                var p = img.GetPixel(Math.Clamp(cx + dx, 0, img.GetWidth() - 1), Math.Clamp(cy + dy, 0, img.GetHeight() - 1));
                sum += new System.Numerics.Vector3(Decode8((int)MathF.Round(p.R * 255f)), Decode8((int)MathF.Round(p.G * 255f)), Decode8((int)MathF.Round(p.B * 255f)));
                n++;
            }
        return sum / n;
    }

    /// <summary>The face picture with the automatic exposure against the same picture without it, by a CPU model of the official chain: the scale the exposure
    /// settles at is worked out from the mean luminance the pass sampled (<see cref="Viewer.Core.World.AutoExposure.Settled"/>) and must match the scale the pass
    /// holds; each skin patch of the picture without the exposure is taken back through the tone mapper to its scene colour, put through the mapper again at the
    /// settled scale, and must match the picture with the exposure to within <see cref="FaceModelTolerance"/> levels. Results go to the case's output.</summary>
    private void ExposureModelCheck(Image plain, Image exposed, (float Scale, float MeanLuminance, float Target) measured, float probeAmbiance)
    {
        float modelScale = Viewer.Core.World.AutoExposure.Settled(measured.MeanLuminance, probeAmbiance);
        float mix = Viewer.Core.World.ColourPath.TonemapMix(probeAmbiance);
        var type = Viewer.Core.World.ToneMap.DefaultType;
        float baseExposure = Viewer.Core.World.ToneMap.DefaultExposure;
        var rows = new List<object>();
        int worst = 0;
        foreach (var (name, fx, fy) in FacePatches)
        {
            var before = PatchMean(plain, fx, fy);
            var scene = InvertToneMap(before, baseExposure, type, mix);
            var want = Viewer.Core.World.ToneMap.Apply(scene, baseExposure * modelScale, type, mix);
            var got = PatchMean(exposed, fx, fy);
            int[] w = [Encode8(want.X), Encode8(want.Y), Encode8(want.Z)], g = [Encode8(got.X), Encode8(got.Y), Encode8(got.Z)], b = [Encode8(before.X), Encode8(before.Y), Encode8(before.Z)];
            int err = Math.Max(Math.Abs(w[0] - g[0]), Math.Max(Math.Abs(w[1] - g[1]), Math.Abs(w[2] - g[2])));
            worst = Math.Max(worst, err);
            rows.Add(new { patch = name, without_exposure = b, with_exposure = g, model = w, max_error = err });
        }
        _result["exposure_model"] = new { measured_scale = measured.Scale, model_scale = modelScale, mean_luminance = measured.MeanLuminance, target = measured.Target, tolerance = FaceModelTolerance, worst_patch_error = worst, patches = rows };
        if (MathF.Abs(measured.Scale - modelScale) > 0.02f * modelScale)
            _failures.Add($"the face's automatic exposure scale is {measured.Scale:F4}, the model says {modelScale:F4}");
        if (worst > FaceModelTolerance)
            _failures.Add($"a skin patch of the face with the automatic exposure is off the CPU model by {worst} levels (allowed {FaceModelTolerance})");
    }

    private const int FaceModelTolerance = 2;

    /// <summary>The animation source the harness gives the avatar presenter: the grid's animations from the local asset cache.</summary>
    private static AnimationSource AnimationSourceFor(string? assets) => new((id, _) =>
    {
        var path = assets is null ? null : new Viewer.Core.Assets.LocalAssetCache(assets).AnimationPath(id);
        return Task.FromResult(path is not null && File.Exists(path) ? Viewer.Core.Assets.AssetFetchResult.Ok(File.ReadAllBytes(path)) : Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.NotFound));
    });

    /// <summary>An appearance whose bake faces carry <paramref name="bakes"/> (by slot name).</summary>
    private static Viewer.Core.World.AvatarAppearanceData Appearance(Guid agent, IReadOnlyDictionary<string, Guid> bakes)
    {
        var faces = new Guid[45];
        for (int bt = 0; bt < Viewer.Core.World.AppearanceSlots.Count; bt++)
            if (Viewer.Core.World.AppearanceSlots.SlotOf(bt) is { } slot && bakes.TryGetValue(slot, out var id))
                faces[Viewer.Core.World.AppearanceSlots.FaceOf(bt)] = id;
        return new(agent, [.. faces], Viewer.Core.World.VisualParamSet.Empty, 1, 1);
    }

    private static readonly Guid LiveSelf = Guid.Parse("5e1f5e1f-0000-4000-8000-000000000001"), LiveOther = Guid.Parse("07e407e4-0000-4000-8000-000000000002");

    /// <summary>
    /// Two avatars as the viewer draws them in world, from synthetic region updates: the user's own (standing, wearing the
    /// avatar's own bakes from the asset cache) and another walking north at 1 m/s (default skin), both named, through the
    /// world state, the avatar presenter (animations from the local asset cache) and the follow camera behind the user's
    /// avatar turned 35 degrees. A fixed clock: everything settles at 0 s, then the frame is drawn at 0.6 s (the walker
    /// carried 0.6 m and 0.6 s into its walk). --tr-alter=still (negative control): the region reports no animation and no velocity for
    /// the walker, so it stands where it began (0.6 m behind the walker of the golden: the walk pose 0.6 s in is close to a stand, so the
    /// missing animation alone changes a handful of pixels, below the diff tolerance, and would not tell the two cases apart); the diff must fail.
    /// </summary>
    private async Task<int> AvatarLive(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets, string? golden, string? alter)
    {
        var rig = MakeRig();
        var scenario = rig.Scenario;
        var floor = Floor(scenario, new System.Numerics.Vector3(128, 128, 20), 10f);
        var world = new Viewer.Core.World.WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("avatars", 1000, 1000, 256, 256, true));
        var bakeFiles = new Dictionary<Guid, string>();
        var bakeIds = new Dictionary<string, Guid>();
        var file = assets is null ? null : Path.Combine(assets, "self-bakes.json");
        if (file is not null && File.Exists(file))
            foreach (var (slot, id) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [])
            {
                var g = Guid.Parse(id);
                bakeIds[slot] = g;
                bakeFiles[g] = new Viewer.Core.Assets.LocalAssetCache(assets!).TexturePath(g);
            }
        if (bakeIds.Count == 0) { _failures.Add("no bakes in the asset cache (self-bakes.json)"); return 1; }
        using var textures = TextureSourceFor("", bakeFiles);
        using var animations = AnimationSourceFor(assets);
        double now = 0;
        var view = MakeView("server", rig);
        var avatars = new AvatarPresenter(scenario, world, LiveSelf, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, textures, null, () => now);
        var presenter = new WorldPresenter(world, view, avatars: avatars);
        // the region places an avatar by its centre: 0.95 m above the floor for its 1.9 m
        var size = new System.Numerics.Vector3(0.45f, 0.6f, 1.9f);
        var east = System.Numerics.Quaternion.Identity;
        var north = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, MathF.PI / 2);
        world.ApplyAvatar(FixtureSim, new(800, LiveSelf, "Test User", new(126, 128, 20.95f), east, size));
        world.ApplyAvatar(FixtureSim, new(900, LiveOther, "Example Walker", new(129.5f, 127, 20.95f), north, size, alter == "still" ? System.Numerics.Vector3.Zero : new(0, 1, 0)));
        world.ApplyAppearance(FixtureSim, Appearance(LiveSelf, bakeIds), LiveSelf);
        world.ApplyAnimations(FixtureSim, LiveSelf, [new(StandId, 1)]);
        world.ApplyAnimations(FixtureSim, LiveOther, alter == "still" ? [] : [new(WalkId, 2)]);
        var follow = new FollowCamera { ThirdPersonHeadOffset = default };   // the camera as the golden was taken: no head offset
        follow.SetOrbit(35 * MathF.PI / 180, 0);
        var cam = rig.Camera;
        cam.Fov = 60f; cam.Near = 0.1f; cam.Far = 200f;
        void Aim()
        {
            var self = avatars.Scene.Self;
            if (self is null || !avatars.Scene.Place(self, out var soles, out float yaw)) return;
            var (eye, target) = follow.Pose(soles + new System.Numerics.Vector3(0, 0, self.Size.Z / 2), yaw, 1.0);   // a second passes: the camera has settled
            cam.LookAtFromPosition(new Vector3(eye.X, eye.Z, -eye.Y), new Vector3(target.X, target.Z, -target.Y), Vector3.Up);
        }
        // settle at 0 s: both drawn, their animations read (the stand at least), the own bakes on the GPU
        int bodySlots = bakeIds.Keys.Count(k => AvatarBody.DefaultParts.Any(p => p.Bake == k));
        var sw = Stopwatch.StartNew();
        bool Settled() => avatars.Drawn == 2 && animations.Waiting == 0 && animations.InFlight == 0
            && animations.Ready >= (alter == "still" ? 1 : 2) && avatars.BakeTextures >= bodySlots;
        while (!Settled() && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            presenter.Frame();
            Aim();
            await Frames(1);
        }
        if (!Settled()) _failures.Add($"not settled in 30 s: drawn {avatars.Drawn}, animations ready {animations.Ready} waiting {animations.Waiting} in flight {animations.InFlight}, bake textures {avatars.BakeTextures} of {bodySlots}");
        // The walk-rate state moves once per presented frame even while the clock is held still, so how far it has moved would depend on
        // how many frames the settling took (the picture then differed from one run to the next). Restart the walker's animation here,
        // at 0 s: its state begins from nothing and the frames from here on are a fixed number.
        if (alter != "still")
        {
            world.ApplyAnimations(FixtureSim, LiveOther, []);
            presenter.Frame();
            Aim();
            world.ApplyAnimations(FixtureSim, LiveOther, [new(WalkId, 2)]);
        }
        // one more frame at 0 s: an animation read after the last settling frame is chosen here, starting at 0 s, not at
        // the drawn frame (where it would start at 0.6 s and be drawn at its beginning)
        presenter.Frame();
        now = 0.6;
        presenter.Frame();
        Aim();
        await Frames(8);
        presenter.Frame();
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        string png = Path.Combine(_out, _tag + ".png");
        img.SavePng(png);
        _result["mode"] = "avatars-live";
        _result["avatars_live"] = new
        {
            drawn = avatars.Drawn, bake_textures = avatars.BakeTextures, body_bake_slots = bodySlots, animations_ready = animations.Ready,
            presenter = avatars.Describe(), camera = new { orbit_yaw_deg = 35, zoom = follow.Zoom, distance = follow.Distance }, altered = alter, png = Path.GetFileName(png),
        };
        if (golden is not null) DiffGolden(img, golden);
        presenter.Dispose();
        if (avatars.LiveResources != 0) _failures.Add($"{avatars.LiveResources} avatar resources live after the presenter went");
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        Rids.Free(floor.Instance);
        Rids.Free(floor.Mesh);
        floor.Material.Dispose();
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }

    private void DiffGolden(Image img, string golden, string key = "diff")
    {
        if (!Path.IsPathFullyQualified(golden) || !File.Exists(golden)) { _failures.Add("golden not found: " + Path.GetFileName(golden)); return; }
        var g = Image.LoadFromFile(golden);
        g.Convert(Image.Format.Rgba8);
        int w = img.GetWidth(), h = img.GetHeight();
        var diff = g.GetWidth() != w || g.GetHeight() != h
            ? new DiffResult(false, g.GetWidth(), g.GetHeight(), 0, 0, 0, 0, "golden size differs")
            : ImageDiff.Compare(img.GetData(), g.GetData(), w, h, 4, DiffTolerance.Terrain);
        _result[key] = new Dictionary<string, object?>
        {
            ["golden"] = Path.GetFileName(golden), ["pass"] = diff.Pass, ["differing"] = diff.Differing, ["differing_fraction"] = diff.DifferingFraction,
            ["mean_abs_error"] = Math.Round(diff.MeanAbsError, 4), ["max_channel_error"] = diff.MaxChannelError, ["summary"] = diff.ToString(),
        };
        if (!diff.Pass) _failures.Add(key.Replace('_', ' ') + " " + diff);
    }

    /// <summary>The avatar's own bakes named by the asset cache's self-bakes.json, decoded through the guarded decoder.</summary>
    private static Dictionary<string, Texture2D>? LoadBakes(string assets, List<ImageTexture> made)
    {
        var file = Path.Combine(assets, "self-bakes.json");
        if (!File.Exists(file)) return null;
        var cache = new Viewer.Core.Assets.LocalAssetCache(assets);
        var bakes = new Dictionary<string, Texture2D>();
        foreach (var (slot, id) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [])
        {
            var path = cache.TexturePath(Guid.Parse(id));
            if (!File.Exists(path)) continue;
            var d = Viewer.Core.Protocol.LmvTexture.Decode(File.ReadAllBytes(path));
            if (d.Texture is not { } tex) continue;
            var mips = MipChain.Build(tex.Width, tex.Height, tex.Rgba);
            var img = Image.CreateFromData(tex.Width, tex.Height, true, Image.Format.Rgba8, mips);
            var it = ImageTexture.CreateFromImage(img);
            img.Dispose();
            made.Add(it);
            bakes[slot] = it;
        }
        return bakes;
    }

    private sealed record FloorQuad(Rid Mesh, Rid Instance, Material Material);

    /// <summary>A flat grey square of <paramref name="half"/> metres each way at <paramref name="centre"/> (SL).</summary>
    private static FloorQuad Floor(Rid scenario, System.Numerics.Vector3 centre, float half)
    {
        float x = centre.X, y = centre.Y, z = centre.Z;
        float[] pos = [x - half, y - half, z, x + half, y - half, z, x + half, y + half, z, x - half, y + half, z];
        float[] nrm = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1];
        var surface = MeshSurfaceData.Encode(new Viewer.Core.Meshing.MeshFace(pos, nrm, [0, 0, 1, 0, 1, 1, 0, 1], [0, 1, 2, 0, 2, 3]));
        var mesh = Rids.MeshCreate("test mesh");
        PrimLayer.HandOver(mesh, surface);
        var m = PrimLayer.ColourMaterial(new Viewer.Core.World.Rgba(0.55f, 0.55f, 0.55f, 1f));
        RenderingServer.MeshSurfaceSetMaterial(mesh, 0, m.GetRid());
        var inst = Rids.InstanceCreate2(mesh, scenario, "test instance");
        return new FloorQuad(mesh, inst, m);
    }

    /// <summary>
    /// N avatars walking (the grid's walk, phases staggered by a seeded pick, each on its own 1 m circle, spread on a
    /// 2.5 m grid) in front of a fixed camera, on an empty stage (--tr-fixture=empty: a floor and the sky) or on a replayed
    /// capture (its meshes, textures and materials from --tr-assets=, settled before measuring; the crowd at the scene's
    /// centre on the terrain). After 2 s unrecorded, --tr-crowd-seconds (default 10) of frames are recorded: frame time,
    /// the avatar layer's main-thread time posing and handing over, its engine calls, the scene's own frame work, the
    /// engine's render CPU and GPU time, draw calls, collections and pipeline compilations.
    /// </summary>
    private async Task<int> AvatarCrowd(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets, string[] args)
    {
        int n = int.Parse(Arg(args, "--tr-crowd=") ?? "10");
        double seconds = double.Parse(Arg(args, "--tr-crowd-seconds=") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
        string fixture = Arg(args, "--tr-fixture=") ?? "empty";
        var size = (Arg(args, "--tr-size=") ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
        int seed = int.Parse(Arg(args, "--tr-seed=") ?? "8");
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
        WorldPresenter? presenter = null;
        PrimLayer? prims = null;
        Loaded? loaded = null;
        ITerrainView? terrainView = null;
        MeshSource? meshes = null;
        TextureSource? textures = null;
        FloorQuad? floor = null;
        System.Numerics.Vector3 centre;
        Func<float, float, float> ground;
        if (fixture == "empty")
        {
            centre = new(128, 128, 20);
            floor = Floor(scenario, centre, 120f);
            ground = (_, _) => 20f;
        }
        else
        {
            if (assets is null) throw new ArgumentException("a replayed crowd stage needs --tr-assets=");
            _assetDir = assets;
            loaded = Load(fixture);
            var view = terrainView = MakeView("server", rig);
            meshes = CacheMeshSource(assets);
            textures = CacheTextureSource(assets);
            prims = new PrimLayer(scenario, loaded.World, meshes, textures, _budget) { MaterialAssets = CacheMaterialSource(assets) };
            var roots = loaded.World.Snapshot().Objects.Values.Where(o => o.ParentLocalId == 0 && o.AttachmentPoint is null).Select(o => o.Position).ToList();
            float Median(IEnumerable<float> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? 128 : s[s.Count / 2]; }
            var world = loaded.World;
            ground = (x, y) => TerrainHeight(world, x, y);
            float cx = Median(roots.Select(p => p.X)), cy = Median(roots.Select(p => p.Y));
            centre = new(cx, cy, ground(cx, cy));
            presenter = new WorldPresenter(loaded.World, view, prims: prims, terrainTextures: textures,
                environment: new EnvironmentLayer(scenario, rig.Environment, rig.Sun)) { DayFractionOverride = 0.3 };
        }
        // the crowd: a square grid 2.5 m apart around the centre, each on a 1 m circle; the camera back from its south-east
        // corner, high enough to see every one
        int side = (int)Math.Ceiling(Math.Sqrt(n));
        float span = (side - 1) * 2.5f;
        var rnd = new Random(seed);
        var layer = new AvatarLayer(scenario, body, surfaces);
        var spots = new List<(System.Numerics.Vector3 C, double Phase, float Start)>();
        for (int i = 0; i < n; i++)
        {
            float x = centre.X - span / 2 + (i % side) * 2.5f, y = centre.Y - span / 2 + (i / side) * 2.5f;
            double phase = rnd.NextDouble() * walk.Duration;
            float start = (float)(rnd.NextDouble() * Math.Tau);
            spots.Add((new(x, y, ground(x, y)), phase, start));
            layer.Add(new(x + MathF.Cos(start), y + MathF.Sin(start), ground(x, y)), start + MathF.PI / 2, walk, phase);
        }
        float back = span * 0.9f + 9f;
        var target = new Vector3(centre.X, centre.Z + 1f, -centre.Y);
        cam.Position = target + new Vector3(back * 0.5f, back * 0.45f + 2f, back * 0.75f);
        cam.LookAt(target, Vector3.Up);
        if (prims is not null)
        {
            prims.Camera = cam.GlobalPosition;
            prims.ResidencyView = PrimLayer.ViewOf(cam, size[0], size[1]);
            prims.UpdateLods();
            await SettleMeshes(presenter!, prims, meshes, textures);
        }
        const float Radius = 1f, Speed = 0.6f;   // radians a second round each circle
        void Step(double t)
        {
            for (int i = 0; i < spots.Count; i++)
            {
                var (c, _, start) = spots[i];
                float a = start + (float)(t * Speed);
                layer.Move(i, new(c.X + Radius * MathF.Cos(a), c.Y + Radius * MathF.Sin(a), c.Z), a + MathF.PI / 2);
            }
        }
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 2)
        {
            double t = clock.Elapsed.TotalSeconds;
            Step(t);
            presenter?.Frame();
            layer.Frame(t);
            await Frames(1);
        }
        // sized up front (a few thousand frames a second at most), so no list grows inside the recorded frames
        int cap = (int)(seconds * 5000) + 1024;
        var frameMs = new List<double>(cap);
        var poseMs = new List<double>(cap);
        var uploadMs = new List<double>(cap);
        var sceneMs = new List<double>(cap);
        var cpuMs = new List<double>(cap);
        var gpuMs = new List<double>(cap);
        var draws = new List<double>(cap);
        var calls = new List<double>(cap);
        int[] gc0 = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        var pipelines0 = PipelineWarmup.Counters();
        var measure = Stopwatch.StartNew();
        long last = Stopwatch.GetTimestamp();
        while (measure.Elapsed.TotalSeconds < seconds)
        {
            double t = clock.Elapsed.TotalSeconds;
            long s0 = Stopwatch.GetTimestamp();
            Step(t);
            presenter?.Frame();
            sceneMs.Add((Stopwatch.GetTimestamp() - s0) * 1000.0 / Stopwatch.Frequency);
            layer.Frame(t);
            poseMs.Add(layer.PoseMsLastFrame);
            uploadMs.Add(layer.UploadMsLastFrame);
            calls.Add(layer.CallsLastFrame);
            await Frames(1);
            long now = Stopwatch.GetTimestamp();
            frameMs.Add((now - last) * 1000.0 / Stopwatch.Frequency);
            last = now;
            cpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(vpRid));
            gpuMs.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(vpRid));
            draws.Add(RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame));
        }
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        img.SavePng(Path.Combine(_out, _tag + ".png"));
        var pipelines = PipelineWarmup.Counters().Zip(pipelines0, (a, b) => a - b).ToArray();
        var avatarMs = poseMs.Zip(uploadMs, (a, b) => a + b).ToList();
        _result["mode"] = "crowd";
        _result["crowd"] = new
        {
            avatars = n, stage = fixture == "empty" ? "empty" : Path.GetFileName(fixture), seconds, frames = frameMs.Count, size = $"{size[0]}x{size[1]}", seed,
            palette = layer.PaletteSize, surfaces_per_avatar = layer.Surfaces, triangles_per_avatar = body.TriangleCount, vertices_per_avatar = body.VertexCount,
            frame_ms = Dist(frameMs), over_16_7_ms = frameMs.Count(f => f > 16.7),
            avatar_main_ms = Dist(avatarMs), pose_ms = Dist(poseMs), upload_ms = Dist(uploadMs), engine_calls_per_frame = Dist(calls),
            scene_frame_ms = presenter is null ? null : Dist(sceneMs),
            render_cpu_ms = Dist(cpuMs), render_gpu_ms = Dist(gpuMs), draw_calls = Dist(draws),
            collections = new { gen0 = GC.CollectionCount(0) - gc0[0], gen1 = GC.CollectionCount(1) - gc0[1], gen2 = GC.CollectionCount(2) - gc0[2] },
            pipelines_compiled = PipelineWarmup.Named(pipelines),
            walk = new { duration = walk.Duration, joints = walk.Tracks.Count },
        };
        layer.Dispose();
        presenter?.Dispose();
        terrainView?.Clear();
        loaded?.Dispose();
        textures?.Dispose();
        meshes?.Dispose();
        if (floor is not null) { Rids.Free(floor.Instance); Rids.Free(floor.Mesh); floor.Material.Dispose(); }
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>The terrain height at SL (x, y) from the region's patches (16 x 16 samples a patch), nearest sample.</summary>
    private static float TerrainHeight(Viewer.Core.World.WorldState world, float x, float y)
    {
        int ix = Math.Clamp((int)x, 0, (world.Region?.SizeX ?? 256) - 1), iy = Math.Clamp((int)y, 0, (world.Region?.SizeY ?? 256) - 1);
        return world.TryGetPatch(ix / 16, iy / 16, out var h) ? h[(iy % 16) * 16 + ix % 16] : 20f;
    }
}
