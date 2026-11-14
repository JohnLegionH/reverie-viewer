// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Vector3 = Godot.Vector3;

namespace Viewer.Godot;

/// <summary>
/// --tr-haze=on|off (with --tr-environment=&lt;a sky that sets a probe ambiance&gt;): a flat fullbright face (no lighting, so its colour is its texture's) filling the view at 100, 900
/// and 2500 m, once looking towards the light and once looking across it, under a sky that takes the tone-mapped path. Each centre pixel is compared with the official chain worked out on
/// the CPU: the face's linear colour seen through the official haze (<see cref="SkyLighting.SeenThroughHaze"/>: the scene times the red channel's survival plus the haze's own light, made linear
/// and scaled by the sky's scale), then the automatic exposure the scene settles at (the view is that one colour; <see cref="AutoExposure.Settled"/>) and the post pass's tone mapping, for a light, a dark
/// and a middle face colour. "off" leaves the haze pass out and "noexposure" the automatic exposure: the negative controls, which must fail.
/// </summary>
public partial class TestRender
{
    /// <summary>Levels (of 255) a measured channel may differ from the expected.</summary>
    private const int HazeTolerance = 2;

    private async Task<int> HazeRun(string kind)
    {
        var frame = (RegionEnvironment.FromLlsd(File.ReadAllText(_envFile ?? throw new ArgumentException("--tr-haze needs --tr-environment=")), out _) ?? RegionEnvironment.Default).SkyAt(0.5);
        var rig = MakeRig(new Vector2I(64, 64));
        rig.Camera.Fov = 10f; rig.Camera.Near = 1f; rig.Camera.Far = 3000f;
        EnvironmentLayer.HazeOff = kind == "off";
        EnvironmentLayer.DynamicExposure = kind != "noexposure";   // "noexposure" leaves the automatic exposure out while the model keeps it: the negative control for it, which must fail
        HdrTonemapEffect.ExposureInstant = true; HdrTonemapEffect.ReadExposure = true;   // the view is one flat colour, so the exposure it settles at is worked out from that colour alone
        var layer = new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun) { HdrEnabled = true };
        layer.Apply(RegionEnvironment.Default, frame, null, 256, 256);
        await Frames(3);
        var texture = ImageTexture.CreateFromImage(Image.CreateFromData(1, 1, false, Image.Format.Rgba8, [170, 110, 85, 255]));
        var material = new ShaderMaterial { Shader = FaceShader.Create(true, false) };
        material.SetShaderParameter("tex", texture);
        var quad = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(4000f, 4000f) }, MaterialOverride = material };
        rig.Root.AddChild(quad);
        // three face colours: a light one (a bright scene, the exposure at the bottom of its range), a dark leaf-like green and a middle one (the exposure part way): the
        // automatic exposure has to follow each
        var faces = new (string Name, byte R, byte G, byte B)[] { ("light", 170, 110, 85), ("dark leaf", 20, 30, 14), ("middle", 70, 52, 40) };
        float mix = ColourPath.TonemapMix(frame.ProbeAmbiance);
        var rows = new List<object>();
        var sun = frame.SunDirection;
        var towards = new System.Numerics.Vector3(sun.X, sun.Y, 0f);
        towards = System.Numerics.Vector3.Normalize(towards.LengthSquared() < 1e-6f ? System.Numerics.Vector3.UnitX : towards);
        var across = new System.Numerics.Vector3(-towards.Y, towards.X, 0f);
        foreach (var face in faces)
        foreach (var (name, dir) in new[] { ("towards the light", towards), ("across the light", across) })
        {
            var albedo = new System.Numerics.Vector3(Decode8(face.R), Decode8(face.G), Decode8(face.B));
            texture.Dispose();
            texture = ImageTexture.CreateFromImage(Image.CreateFromData(1, 1, false, Image.Format.Rgba8, [face.R, face.G, face.B, 255]));
            material.SetShaderParameter("tex", texture);
            var look = new Vector3(dir.X, dir.Z, -dir.Y);   // the engine's axes: y up, z south
            foreach (float distance in face.Name == "light" ? new[] { 100f, 900f, 2500f } : new[] { 100f, 2500f })
            {
                rig.Camera.Transform = new Transform3D(Basis.LookingAt(look, Vector3.Up), Vector3.Zero);
                quad.Transform = new Transform3D(rig.Camera.Transform.Basis, look * distance);
                await Frames(4);
                var img = rig.Texture.GetImage();
                var px = img.GetPixel(32, 32);
                int[] got = [(int)MathF.Round(px.R * 255f), (int)MathF.Round(px.G * 255f), (int)MathF.Round(px.B * 255f)];
                var seen = SkyLighting.SeenThroughHaze(frame, albedo, dir, distance);
                float exposure = AutoExposure.Settled(AutoExposure.Luminance(seen), frame.ProbeAmbiance);   // the official dynamic exposure of the hazed scene
                seen = ToneMap.Apply(seen, ToneMap.DefaultExposure * exposure, ToneMap.DefaultType, mix);
                var (measuredScale, measuredLuminance, _) = HdrTonemapEffect.LastExposure;
                int[] want = [Encode8(seen.X), Encode8(seen.Y), Encode8(seen.Z)];
                int err = Math.Max(Math.Abs(got[0] - want[0]), Math.Max(Math.Abs(got[1] - want[1]), Math.Abs(got[2] - want[2])));
                rows.Add(new { face = face.Name, direction = name, distance, got, want, max_error = err, exposure_model = exposure, exposure_measured = measuredScale, mean_luminance_measured = measuredLuminance });
                if (err > HazeTolerance)
                    _failures.Add($"haze {face.Name} face, {name}, {distance} m: got ({got[0]},{got[1]},{got[2]}), expected ({want[0]},{want[1]},{want[2]}) (off by {err}, allowed {HazeTolerance})");
                if (face.Name == "light" && name == "towards the light" && distance == 2500f) img.SavePng(Path.Combine(_out, $"{_tag}.png"));
            }
        }
        _result["haze"] = new { kind, tolerance = HazeTolerance, tonemap_ran = HdrTonemapEffect.Ran, hdr_active = layer.HdrActive, rows };
        EnvironmentLayer.HazeOff = false;
        HdrTonemapEffect.ExposureInstant = false; HdrTonemapEffect.ReadExposure = false; EnvironmentLayer.DynamicExposure = true;
        rig.Root.RemoveChild(quad); quad.Free(); material.Dispose(); texture.Dispose();
        layer.Clear();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        await Frames(1);
        return _failures.Count == 0 ? 0 : 1;
    }
}
