// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// <summary>
    /// A measuring aid, not a pass-or-fail case: one avatar drawn from a visual-parameter set named in a local JSON file (NV_FACE_LOOK_PARAMS:
    /// {"order": [ids], "bytes": [values]}), seen from the front through a long lens so the face is hardly foreshortened. It writes the picture and, per
    /// height level of the head, the silhouette's width in the picture (metres) next to the same width computed from the shaped, skinned head mesh on the
    /// CPU, and the eyes' distance. With the variable unset the case is skipped with its reason; --tr-alter=defaultmale draws the default male instead.
    /// </summary>
    private async Task<int> AvatarFaceShape(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? alter)
    {
        string? file = System.Environment.GetEnvironmentVariable("NV_FACE_LOOK_PARAMS");
        if (alter != "defaultmale" && (file is null || !File.Exists(file))) { _result["skipped"] = "NV_FACE_LOOK_PARAMS names no parameter file"; return 0; }
        if (body.Shape is null) { _failures.Add("the character folder gave no visual parameters"); return 1; }
        VisualParamSet vp;
        if (alter == "defaultmale") vp = new([255], [VisualParamSet.MaleParamId]);
        else
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file!));
            var order = doc.RootElement.GetProperty("order").EnumerateArray().Select(e => e.GetInt32()).ToImmutableArray();
            var raw = doc.RootElement.GetProperty("bytes").EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
            vp = new(raw, order);
        }
        var avatarRig = new AvatarRig(body.Skeleton);
        var shape = AvatarShape.From(avatarRig, body.Shape, vp);
        var measure = shape.Measure(null)!.Value;

        // a live outline picture's reply (the automation API's body_mask): the same picture size and field of view, and the camera put at the same place from the drawn head joint
        string? liveMaskFile = System.Environment.GetEnvironmentVariable("NV_FACE_MASK_LIVE");
        JsonElement? liveMask = null;
        if (liveMaskFile is not null)
        {
            if (!File.Exists(liveMaskFile)) { _failures.Add("NV_FACE_MASK_LIVE names no file"); return 1; }
            liveMask = JsonDocument.Parse(File.ReadAllText(liveMaskFile)).RootElement.Clone();
        }
        var rig = MakeRig(liveMask is { } lm ? new Vector2I(lm.GetProperty("width").GetInt32(), lm.GetProperty("height").GetInt32()) : new Vector2I(960, 640));
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("face-shape", 1000, 1000, 256, 256, true));
        double now = 0;
        var view = MakeView("server", rig);
        var self = Guid.Parse("5a4e0003-0000-4000-8000-000000000001");
        var agent = Guid.Parse("5a4e0003-0000-4000-8000-000000000011");
        var avatars = new AvatarPresenter(rig.Scenario, world, self, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), null, null, null, () => now);
        var presenter = new WorldPresenter(world, view, avatars: avatars);

        const float Soles = 20.0f;
        float Num(string name, float fallback) => float.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
        float Distance = Num("NV_FACE_LOOK_DISTANCE", 3.0f), fovDeg = Num("NV_FACE_LOOK_FOV", 12f), pitch = float.DegreesToRadians(Num("NV_FACE_LOOK_PITCH", 0f));
        var joints = new AvatarJoints(avatarRig) { Shape = shape };
        joints.Begin(null, 0);
        var el = joints.World(avatarRig.Find("mEyeLeft")).Translation;
        var er = joints.World(avatarRig.Find("mEyeRight")).Translation;
        float eyeAbove = (el.Z + er.Z) / 2 + measure.PelvisToFoot;   // above the soles
        float eyeSpacing = Math.Abs(el.Y - er.Y);
        var cam = rig.Camera;
        cam.Fov = fovDeg; cam.Near = 0.05f; cam.Far = 100f;
        var aim = new GVector3(128f, Soles + eyeAbove - 0.05f, -128f);
        cam.Position = aim + new GVector3(Distance * MathF.Cos(pitch), Distance * MathF.Sin(pitch), 0f);
        cam.LookAt(aim, GVector3.Up);

        world.ApplyAvatar(FixtureSim, new(900, agent, "Test User", new(128, 128, Soles + measure.Height / 2), System.Numerics.Quaternion.Identity, new(0.45f, 0.6f, measure.Height)));
        world.ApplyAppearance(FixtureSim, new AvatarAppearanceData(agent, [.. new Guid[45]], vp, 1, 1), self);

        async Task<Image> Settle(Func<bool> ready, string what)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!ready() && sw.Elapsed < TimeSpan.FromSeconds(30)) { presenter.Frame(); await Frames(1); }
            if (!ready()) _failures.Add($"{what}: not settled in 30 s");
            presenter.Frame();
            await SettlePoses(avatars, presenter);
            await Frames(8);
            presenter.Frame();
            ExpectBodiesSettled(avatars, what);
            var img = rig.Texture.GetImage();
            img.Convert(Image.Format.Rgba8);
            return img;
        }
        var img = await Settle(() => avatars.Drawn == 1 && avatars.BodiesPending == 0, "one avatar");
        img.SavePng(Path.Combine(_out, _tag + "-face.png"));
        // NV_FACE_LOOK_EXPRESSION names an expression animation (express_smile and the like): the region is made to name it, and once it holds at full weight
        // (the official fade-in is 0.3 s, the hold lasts until 1.4 s) a second picture is taken
        if (System.Environment.GetEnvironmentVariable("NV_FACE_LOOK_EXPRESSION") is { Length: > 0 } exprName)
        {
            if (Emotes.FromEmoteName(exprName) is not { } exprId || !Emotes.IsEmote(exprId)) { _failures.Add($"NV_FACE_LOOK_EXPRESSION: {exprName} is not an expression animation"); return 1; }
            avatars.ExpressionTrace(agent, reset: true);
            world.ApplyAnimations(FixtureSim, agent, [new(exprId, 1)]);
            // the region's list reaches the avatar on a later frame: let it start while the clock stands, then move the clock into the hold
            for (int i = 0; i < 10; i++) { presenter.Frame(); await Frames(1); }
            now += 0.8;
            presenter.Frame();   // the weights move on this frame, and the face starts being made: the settle below waits for it
            await Frames(1);
            var withExpr = await Settle(() => avatars.BodiesPending == 0, "one avatar with " + exprName);
            withExpr.SavePng(Path.Combine(_out, _tag + "-face-" + exprName + ".png"));
            _result["expression"] = exprName;
            _result["expression_trace"] = avatars.ExpressionTrace(agent, reset: false);
        }
        _result["drawn_seat"] = avatars.TryDrawnSeat(agent, out var drawnSeat) ? new[] { drawnSeat.X, drawnSeat.Y, drawnSeat.Z } : null;   // the same readings the running viewer reports for its own avatar
        _result["drawn_limbs"] = avatars.DrawnLimbs(agent);
        _result["shape_dump"] = avatars.ShapeDump(agent);   // the same dump the running viewer gives for its own avatar
        if (liveMask is { } live)
        {
            float[] F(string k) => live.GetProperty(k).EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var hw = F("head_world");
            var headLive = new System.Numerics.Matrix4x4(hw[0], hw[1], hw[2], hw[3], hw[4], hw[5], hw[6], hw[7], hw[8], hw[9], hw[10], hw[11], hw[12], hw[13], hw[14], hw[15]);
            if (!System.Numerics.Matrix4x4.Invert(headLive, out var toLiveHead)) { _failures.Add("the live head joint has no inverse"); return 1; }
            var e3 = F("eye"); var f3 = F("forward"); var u3 = F("up");
            var eyeInHead = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(e3[0], e3[1], e3[2]), toLiveHead);
            var fwdInHead = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(f3[0], f3[1], f3[2]), toLiveHead);
            var upInHead = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(u3[0], u3[1], u3[2]), toLiveHead);
            var keptAt = cam.Transform; float keptFov = cam.Fov;   // put back afterwards: the rest of the case reads the picture from its own camera
            cam.Fov = live.GetProperty("fov_deg").GetSingle();
            System.Numerics.Matrix4x4 headHere = default;
            void Place(System.Numerics.Matrix4x4 head)
            {
                headHere = head;
                var eye = System.Numerics.Vector3.Transform(eyeInHead, head);
                var fwd = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.TransformNormal(fwdInHead, head));
                var up = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.TransformNormal(upInHead, head));
                cam.Position = BodyMask.ToEngine(eye);
                cam.LookAt(BodyMask.ToEngine(eye + fwd), BodyMask.ToEngine(up));
            }
            bool SetMask(Material? m, out System.Numerics.Matrix4x4 h) => avatars.SetMask(agent, m, true, out h);
            var mask = await BodyMask.Capture(this, cam, SetMask, Place, () => rig.Texture.GetImage());
            if (mask is null) _failures.Add("the outline picture found no body");
            else
            {
                mask.SavePng(Path.Combine(_out, _tag + "-mask.png"));
                var t = cam.GlobalTransform;
                var eye = BodyMask.FromEngine(t.Origin);
                _result["mask"] = new { width = mask.GetWidth(), height = mask.GetHeight(), fov_deg = cam.Fov, eye = new[] { eye.X, eye.Y, eye.Z },
                    head_world = new[] { headHere.M41, headHere.M42, headHere.M43 }, eye_in_head = new[] { eyeInHead.X, eyeInHead.Y, eyeInHead.Z } };
            }
            cam.Transform = keptAt; cam.Fov = keptFov;
        }
        world.Kill(FixtureSim, [900]);
        var empty = (await Settle(() => avatars.Drawn == 0, "empty scene")).GetData();

        // the same head on the CPU: shaped, skinned in the pose the layer uses at clock zero
        var morphed = BodyMorph.Compute(body, body.Shape, shape);
        var pose = new AvatarPose(body, null);
        var skin = new System.Numerics.Matrix4x4[pose.PaletteSize];
        pose.Evaluate(0, skin, shape, null);
        int hi = body.Parts.ToList().FindIndex(p => p.Type == "headMesh");
        var part = body.Parts[hi];
        var pos = morphed?[hi].Positions ?? part.Positions;
        var cpu = new System.Numerics.Vector3[pos.Length];
        for (int i = 0; i < pos.Length; i++)
        {
            var a = System.Numerics.Vector3.Transform(pos[i], skin[part.BoneA[i]]);
            var b = System.Numerics.Vector3.Transform(pos[i], skin[part.BoneB[i]]);
            cpu[i] = a * (1 - part.WeightB[i]) + b * part.WeightB[i];
        }

        int w = img.GetWidth(), h = img.GetHeight();
        var data = img.GetData();
        float pxPerMetre = h / (2f * Distance * MathF.Tan(MathF.PI / 180f * fovDeg / 2f));
        var rows = new List<object>();
        foreach (float dz in new[] { 0.03f, 0.0f, -0.03f, -0.06f, -0.09f, -0.12f, -0.15f })
        {
            // the picture's row at this height: the camera looks level at (eye height - 0.05)
            int y = (int)MathF.Round(h / 2f - (dz + 0.05f) * pxPerMetre);
            if (y < 0 || y >= h) continue;
            int lo = int.MaxValue, hiX = -1;
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (Math.Abs(data[i] - empty[i]) + Math.Abs(data[i + 1] - empty[i + 1]) + Math.Abs(data[i + 2] - empty[i + 2]) > 40) { lo = Math.Min(lo, x); hiX = Math.Max(hiX, x); }
            }
            float z = (el.Z + er.Z) / 2 + dz;
            float cl = float.MaxValue, ch = float.MinValue;
            foreach (var v in cpu) if (MathF.Abs(v.Z - z) <= 0.004f && v.X > el.X - 0.09f) { cl = Math.Min(cl, v.Y); ch = Math.Max(ch, v.Y); }
            rows.Add(new { dz, row = y, picture_width_m = hiX < 0 ? 0 : Math.Round((hiX - lo + 1) / pxPerMetre, 4), cpu_width_m = ch < cl ? 0 : Math.Round(ch - cl, 4) });
        }
        _result["mode"] = "avatars-face-shape";
        _result["avatars_face_shape"] = new { eye_spacing_m = Math.Round(eyeSpacing, 4), px_per_metre = Math.Round(pxPerMetre, 1), rows };
        presenter.Dispose();
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }
}
