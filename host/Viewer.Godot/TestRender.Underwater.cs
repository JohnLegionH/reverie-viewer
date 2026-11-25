// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Vector3 = Godot.Vector3;

namespace Viewer.Godot;

/// <summary>
/// --tr-uw=faces|haze|dive (with --tr-environment=&lt;a sky&gt;): flat patches seen by a camera under open water at 18 m.
/// "faces" (an ordinary sky): see-through patches below the surface, one for each blended face shader. Each is first drawn with the below-surface view left out
/// and no air fog (the patch's own colour), then with the view on; what is shown must be that colour with the water fog along the way
/// (<see cref="UnderwaterView.Fog"/>: times what survives, plus the fog's light), as the official viewer's alpha shaders apply it. Under a sky that does not take
/// the tone-mapped path the official lit alpha shaders also scale the result by 1.1, which the patch's own colour already carries, so the fog's light is scaled too.
/// The last patch is half transparent, over the hazed open water.
/// "haze" (either sky): an opaque patch 300 m away above the water, looked at through the surface, must carry the sky's air haze, then be dimmed and fogged by the
/// water on the way to the surface; an opaque patch 8 m away under the water must carry the water haze and no air haze (hazeF.glsl: haze on what is above the
/// water only). --tr-alter=nohaze leaves the air haze out (the negative control).
/// "dive": the time of the first frames after the camera goes under, after the pipeline warm-up.
/// </summary>
public partial class TestRender
{
    private const float UwWater = 18f, UwDepth = 6f;
    /// <summary>How far a shown channel (0..1) may be from the expected one.</summary>
    private const float UwTolerance = 0.02f;
    /// <summary>The same for the haze patches, which are one flat colour: the air haze moves them less than a fog does.</summary>
    private const float UwHazeTolerance = 0.008f;

    private const string Metal = "pbr metal (colour not checked)";

    private static float UwShown(float c) { c = Math.Clamp(c, 0f, 1f); return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f; }
    private static float UwLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static System.Numerics.Vector3 UwCentre(Image img, Vector2 at)
    {
        var sum = System.Numerics.Vector3.Zero;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                var c = img.GetPixel((int)at.X + dx, (int)at.Y + dy);
                sum += new System.Numerics.Vector3(c.R, c.G, c.B) / 25f;
            }
        return sum;
    }

    private static float UwGap(System.Numerics.Vector3 a, System.Numerics.Vector3 b) =>
        MathF.Max(MathF.Abs(a.X - b.X), MathF.Max(MathF.Abs(a.Y - b.Y), MathF.Abs(a.Z - b.Z)));

    private async Task<int> UnderwaterRun(string kind, string? alter)
    {
        var environment = RegionEnvironment.FromLlsd(File.ReadAllText(_envFile ?? throw new ArgumentException("--tr-uw needs --tr-environment=")), out var why)
            ?? throw new ArgumentException("--tr-environment: " + why);
        if (kind == "dive") return await UnderwaterDive(environment);
        if (kind != "faces" && kind != "haze") throw new ArgumentException("--tr-uw must be faces, haze or dive");
        var frame = environment.SkyAt(0.3);
        EnvironmentLayer.HazeOff = alter == "nohaze";
        EnvironmentLayer.DynamicExposure = false;   // the exposure stays 1, so the expected colours need no model of it
        var rig = MakeRig(new Vector2I(960, 540));
        rig.Camera.Fov = 60f; rig.Camera.Near = 0.2f; rig.Camera.Far = 1000f;
        var layer = new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun) { HdrEnabled = true, WaterFlat = true, CloudsEnabled = false };
        layer.Apply(environment, frame, UwWater, 256, 256);
        bool hdr = layer.HdrActive;
        var eye = new Vector3(128f, UwWater - UwDepth, -128f);
        var water = environment.Water;
        bool night = frame.SunDirection.Z < 0;
        float slant = WaterShading.FogSlant(night ? frame.MoonDirection.Z : frame.SunDirection.Z);
        float density = UnderwaterView.FogDensity(water.FogDensity, water.FogMod, underwater: true);
        var made = new List<(MeshInstance3D Quad, ShaderMaterial Material, ImageTexture Texture)>();
        var rows = new List<object>();

        (MeshInstance3D, ShaderMaterial) Patch(Shader shader, string textureName, string tintName, float alpha, byte r, byte g, byte b)
        {
            var texture = ImageTexture.CreateFromImage(Image.CreateFromData(1, 1, false, Image.Format.Rgba8, [r, g, b, 255]));
            var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter(textureName, texture);
            material.SetShaderParameter(tintName, new global::Godot.Vector4(1f, 1f, 1f, alpha));
            var quad = new MeshInstance3D { Mesh = new QuadMesh(), MaterialOverride = material };
            rig.Root.AddChild(quad);
            made.Add((quad, material, texture));
            return (quad, material);
        }

        // the patch drawn alone (below-surface view on or off; off also takes the engine's air fog away), the picture's pixel where its middle is
        async Task<(System.Numerics.Vector3 Shown, Image Picture)> Look(MeshInstance3D quad, bool under)
        {
            foreach (var m in made) m.Quad.Visible = m.Quad == quad;
            layer.UnderwaterOff = !under;
            layer.Apply(environment, frame, UwWater, 256, 256);
            layer.OrderWater(eye);
            if (!under) Stage.EnvironmentOf(rig.Environment).FogEnabled = false;
            await Frames(4);
            if (under && !layer.UnderwaterDrawn) _failures.Add("underwater: the below-surface view is not drawn");
            var img = rig.Texture.GetImage();
            return (UwCentre(img, rig.Camera.UnprojectPosition(quad.GlobalPosition)), img);
        }

        if (kind == "faces")
        {
            if (hdr) throw new ArgumentException("--tr-uw=faces needs a sky that does not take the tone-mapped path");
            rig.Camera.Transform = new Transform3D(Basis.LookingAt(new Vector3(-1, 0, 0), Vector3.Up), eye);
            var cases = new (string Name, Shader Shader, string Texture, string Tint, float Alpha, float LightScale)[]
            {
                ("fullbright", KeptShaders.Face(true, true), "tex", "tint", 1f, 1f),
                ("lit", KeptShaders.Face(false, true), "tex", "tint", 1f, 1.1f),
                ("pbr glass", KeptShaders.Pbr(false, FaceAlpha.Blend), "base_tex", "base_factor", 1f, 1.1f),
                (Metal, KeptShaders.Pbr(false, FaceAlpha.Blend), "base_tex", "base_factor", 1f, 1.1f),
                ("material", KeptShaders.Legacy(false, FaceAlpha.Blend), "tex", "tint", 1f, 1.1f),
                ("half transparent", KeptShaders.Face(true, true), "tex", "tint", 0.5f, 1f),
            };
            for (int i = 0; i < cases.Length; i++)
            {
                var c = cases[i];
                var (quad, material) = Patch(c.Shader, c.Texture, c.Tint, c.Alpha, 230, 160, 90);
                if (c.Texture == "base_tex") material.SetShaderParameter("metallic", c.Name == Metal ? 1f : 0f);
                var offset = new Vector3((i - 2.5f) * 1.6f, -1.5f, -8f);   // right, up, ahead: below the horizon, so the half transparent patch lies over open water
                var basis = rig.Camera.Transform.Basis;
                quad.Transform = new Transform3D(basis, eye + basis * offset);
                quad.Scale = new Vector3(1.4f, 1.4f, 1f);
                float distance = offset.Length(), rise = offset.Y / distance;
                var (bareShown, barePicture) = await Look(quad, under: false);
                var (got, picture) = await Look(quad, under: true);
                var (light, survives) = UnderwaterView.Fog(water.FogColor, density, slant, UwDepth, rise, distance);
                System.Numerics.Vector3 wantLinear;
                if (c.Alpha >= 1f)
                    wantLinear = new System.Numerics.Vector3(UwLinear(bareShown.X), UwLinear(bareShown.Y), UwLinear(bareShown.Z)) * survives + light * c.LightScale;
                else
                {
                    // half the patch (its texture's own colour: fullbright) with its fog, half the hazed open water behind it
                    var own = new System.Numerics.Vector3(Decode8(230), Decode8(160), Decode8(90));
                    var (farLight, farSurvives) = UnderwaterView.Fog(water.FogColor, density, slant, UwDepth, rise, rig.Camera.Far);
                    var behind = UnderwaterView.Haze(System.Numerics.Vector3.Zero, farLight, farSurvives);
                    wantLinear = (own * survives + light) * c.Alpha + behind * (1f - c.Alpha);
                }
                var want = new System.Numerics.Vector3(UwShown(wantLinear.X), UwShown(wantLinear.Y), UwShown(wantLinear.Z));
                float gap = UwGap(got, want), tells = UwGap(bareShown, want);
                // the metallic face is not held to the official colour (see FaceShader.UnderwaterFace): its row only says it is dimmed, and how far off it is
                bool exact = c.Name != Metal;
                rows.Add(new { name = c.Name, distance, got = new[] { got.X, got.Y, got.Z }, want = new[] { want.X, want.Y, want.Z }, bare = new[] { bareShown.X, bareShown.Y, bareShown.Z }, gap, exact_checked = exact, pass = exact ? gap <= UwTolerance : got.X < bareShown.X - UwTolerance });
                if (!exact && got.X >= bareShown.X - UwTolerance) _failures.Add($"underwater faces {c.Name}: not dimmed by the water ({got.X:0.000} against {bareShown.X:0.000} alone)");
                if (tells <= 2f * UwTolerance) _failures.Add($"underwater faces {c.Name}: the check cannot tell fog from none (the patch alone is within {tells:0.000} of the expected colour)");
                if (exact && gap > UwTolerance) _failures.Add($"underwater faces {c.Name}: shown ({got.X:0.000}, {got.Y:0.000}, {got.Z:0.000}), the official water fog gives ({want.X:0.000}, {want.Y:0.000}, {want.Z:0.000})");
                if (i == 0) picture.SavePng(Path.Combine(_out, $"{_tag}.png"));
                barePicture.Dispose(); picture.Dispose();
            }
        }
        else
        {
            var albedo = new System.Numerics.Vector3(Decode8(40), Decode8(200), Decode8(60));
            float mix = ColourPath.TonemapMix(frame.ProbeAmbiance);
            System.Numerics.Vector3 Displayed(System.Numerics.Vector3 v)
            {
                if (hdr) v = ToneMap.Apply(v, ToneMap.DefaultExposure, ToneMap.DefaultType, mix);
                return new System.Numerics.Vector3(UwShown(v.X), UwShown(v.Y), UwShown(v.Z));
            }
            // the air haze this sky gives a surface seen from above the water: the haze pass's model on the tone-mapped path, else the engine's fog as the layer set it
            System.Numerics.Vector3 AirHazed(System.Numerics.Vector3 v, Vector3 direction, float distance)
            {
                if (hdr) return SkyLighting.SeenThroughHaze(frame, v, new System.Numerics.Vector3(direction.X, -direction.Z, direction.Y), distance);
                var e = Stage.EnvironmentOf(rig.Environment);
                var fog = new System.Numerics.Vector3(UwLinear(e.FogLightColor.R), UwLinear(e.FogLightColor.G), UwLinear(e.FogLightColor.B)) * e.FogLightEnergy;
                float amount = 1f - MathF.Exp(-distance * e.FogDensity);
                return System.Numerics.Vector3.Lerp(v, fog, amount);
            }
            var cases = new (string Name, float Elevation, float Distance, bool Above)[] { ("above the water, through the surface", 25f, 900f, true), ("under the water", -10f, 8f, false) };
            foreach (var c in cases)
            {
                var direction = new Vector3(-MathF.Cos(Mathf.DegToRad(c.Elevation)), MathF.Sin(Mathf.DegToRad(c.Elevation)), 0f);
                rig.Camera.Transform = new Transform3D(Basis.LookingAt(direction, Vector3.Up), eye);
                var (quad, _) = Patch(KeptShaders.Face(true, false), "tex", "tint", 1f, 40, 200, 60);
                quad.Transform = new Transform3D(rig.Camera.Transform.Basis, eye + direction * c.Distance);
                quad.Scale = Vector3.One * (c.Distance * 0.25f);
                var (got, picture) = await Look(quad, under: true);
                float way = c.Above ? UwDepth / direction.Y : c.Distance;
                var (light, survives) = UnderwaterView.Fog(water.FogColor, density, slant, UwDepth, direction.Y, way);
                var hazed = AirHazed(albedo, direction, c.Distance);
                var want = Displayed(c.Above ? UnderwaterView.SurfaceFromBelow(hazed, light, survives) : UnderwaterView.Haze(albedo, light, survives));
                var wrong = Displayed(c.Above ? UnderwaterView.SurfaceFromBelow(albedo, light, survives) : UnderwaterView.Haze(hazed, light, survives));
                float gap = UwGap(got, want), tells = UwGap(want, wrong);
                rows.Add(new { name = c.Name, c.Distance, got = new[] { got.X, got.Y, got.Z }, want = new[] { want.X, want.Y, want.Z }, wrong = new[] { wrong.X, wrong.Y, wrong.Z }, gap, tells, pass = gap <= UwHazeTolerance });
                // the patch far above must be able to tell; the near one only under the engine's fog (the tone-mapped sky's haze is too thin at 8 m to show)
                if ((c.Above || !hdr) && tells <= 1.5f * UwHazeTolerance && alter is null) _failures.Add($"underwater haze, {c.Name}: the check cannot tell (with and without the air haze differ by {tells:0.000})");
                if (gap > UwHazeTolerance) _failures.Add($"underwater haze, {c.Name}: shown ({got.X:0.000}, {got.Y:0.000}, {got.Z:0.000}), expected ({want.X:0.000}, {want.Y:0.000}, {want.Z:0.000}); with the air haze the wrong way round ({wrong.X:0.000}, {wrong.Y:0.000}, {wrong.Z:0.000})");
                picture.SavePng(Path.Combine(_out, c.Above ? $"{_tag}.png" : $"{_tag}-below.png"));
                picture.Dispose();
            }
        }
        _result["uw"] = new { kind, hdr, tolerance = kind == "haze" ? UwHazeTolerance : UwTolerance, rows };
        EnvironmentLayer.HazeOff = false;
        EnvironmentLayer.DynamicExposure = true;
        foreach (var m in made) { rig.Root.RemoveChild(m.Quad); m.Quad.Free(); m.Material.Dispose(); m.Texture.Dispose(); }
        layer.Clear();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        await Frames(1);
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>The first dive after the warm-up: frames drawn above the water, then the camera goes under; the time each frame took and the pipelines compiled in them.</summary>
    private async Task<int> UnderwaterDive(RegionEnvironment environment)
    {
        await PipelineWarmup.RunAsync(this, Viewport.Msaa.Disabled, () => Frames(1));
        var frame = environment.SkyAt(0.3);
        var rig = MakeRig(new Vector2I(1280, 720));
        rig.Camera.Fov = 60f; rig.Camera.Near = 0.2f; rig.Camera.Far = 1000f;
        var layer = new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun) { HdrEnabled = true };
        layer.Apply(environment, frame, UwWater, 256, 256);
        void Aim(float height)
        {
            var eye = new Vector3(128f, height, -128f);
            rig.Camera.Transform = new Transform3D(Basis.LookingAt(new Vector3(-1f, -0.1f, 0f).Normalized(), Vector3.Up), eye);
            layer.OrderWater(eye);
        }
        async Task<double> Timed()
        {
            long t = Stopwatch.GetTimestamp();
            await Frames(1);
            return Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
        Aim(UwWater + 4f);
        await Frames(60);
        var above = new List<double>();
        for (int i = 0; i < 30; i++) above.Add(await Timed());
        above.Sort();
        var counters = PipelineWarmup.Counters();
        long start = Stopwatch.GetTimestamp();
        Aim(UwWater - UwDepth);
        double aimMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var under = new List<double>();
        for (int i = 0; i < 10; i++) under.Add(Math.Round(await Timed(), 2));
        var after = PipelineWarmup.Counters();
        var compiled = new long[after.Length];
        for (int i = 0; i < after.Length; i++) compiled[i] = after[i] - counters[i];
        bool drawn = layer.UnderwaterDrawn;
        _result["uw_dive"] = new { drawn, above_median_ms = Math.Round(above[above.Count / 2], 2), above_max_ms = Math.Round(above[^1], 2), switch_ms = Math.Round(aimMs, 2), first_ms = Math.Round(aimMs + under[0], 2), under_ms = under, pipelines = PipelineWarmup.Named(compiled) };
        if (!drawn) _failures.Add("first dive: the below-surface view is not drawn");
        layer.Clear();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        await Frames(1);
        return _failures.Count == 0 ? 0 : 1;
    }
}
