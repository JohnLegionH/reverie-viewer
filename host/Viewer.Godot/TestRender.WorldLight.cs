// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Vector3 = Godot.Vector3;

namespace Viewer.Godot;

/// <summary>
/// --tr-worldlight=legacy|eep|legacy-off|eep-off: flat patches on the face shaders of the world and of the avatars, lit by a stock midday frame with the sun 60 degrees up (the
/// 26.4 Midday sky's constants, with a probe ambiance of 0 for the classic path and 0.5 for the tone-mapped one), turned so the angle between their normal and the sun takes
/// eight values. Every pixel is compared with the official chain worked out on the CPU: under legacy the classic response (<see cref="SkyLighting.ClassicDiffuse"/>,
/// <see cref="SkyLighting.ClassicSpecular"/>), under eep the non-classic response (<see cref="SkyLighting.EepDiffuse"/>, <see cref="SkyLighting.EepSpecular"/>) followed by the post pass's
/// tone mapping (<see cref="ToneMap.Apply"/>). The patches: the avatar shader on a mid and a light skin colour, the plain world face shader on a mid skin, a light skin and white,
/// and the legacy material shader on the mid skin colour, shiny (white specular, glossiness 0.5, on the camera's axis so the view vector is the normal) and matte
/// (glossiness 0). The -off kinds put the response switch back to the engine's own light after the sky is applied: the negative control, which must fail.
/// </summary>
public partial class TestRender
{
    /// <summary>Levels (of 255) a measured channel may differ from the expected.</summary>
    private const int WorldLightTolerance = 1;

    private static readonly (string Name, byte R, byte G, byte B, string Shader, float Gloss)[] WorldPatches =
    [
        ("avatar shader, mid skin", 170, 110, 85, "avatar", 0f),
        ("world face, mid skin", 170, 110, 85, "world", 0f),
        ("world face, light skin", 235, 190, 165, "world", 0f),
        ("world legacy material, shiny (white specular, glossiness 0.5)", 170, 110, 85, "shiny", 0.5f),
        ("world face, white", 255, 255, 255, "world", 0f),
        ("world legacy material, matte (glossiness 0)", 170, 110, 85, "matte", 0f),
        ("avatar shader, light skin", 235, 190, 165, "avatar", 0f),
    ];
    private static readonly float[] WorldPatchX = [-2.7f, -1.8f, -0.9f, 0f, 0.9f, 1.8f, 2.7f];

    private async Task<int> WorldLightRun(string kind)
    {
        bool eep = kind.StartsWith("eep", StringComparison.Ordinal), off = kind.EndsWith("-off", StringComparison.Ordinal), noProbe = kind.EndsWith("-noprobe", StringComparison.Ordinal), noGround = kind.EndsWith("-noground", StringComparison.Ordinal);
        const int width = 640, height = 100;
        const float pixelsPerMetre = 100f;
        var rig = MakeRig(new Vector2I(width, height));
        rig.Camera.Projection = Camera3D.ProjectionType.Orthogonal;
        rig.Camera.Size = 1.0f;
        EnvironmentLayer.DynamicExposure = false;   // the model below is the chain at exposure 1 (the automatic exposure has its own render case, the haze one, where the view is one flat colour)
        var layer = new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun) { HdrEnabled = true };
        bool dusk = kind.Contains("-dusk", StringComparison.Ordinal);
        var frame = dusk ? SharedEnvFrame(eep ? "dusk" : "classic") : SkinFrame(eep ? 0.5f : 0f);   // the classic dusk is the built-in Sunset sky, the tone-mapped one the thick-haze fixture
        if (dusk && !eep) frame = frame with { ProbeAmbiance = 0f };
        layer.Apply(RegionEnvironment.Default, frame, null, 256, 256);
        // under the tone-mapped path the haze is the official haze pass (HdrTonemapEffect) and the model below includes it; the classic path's haze is the engine's distance fog, which the
        // classic light model does not include (at the darkest faces it is one to two levels), so that case compares the light alone
        if (!eep) Stage.EnvironmentOf(rig.Environment).FogEnabled = false;
        if (off) EnvironmentLayer.ResetClassicLight();
        if (noProbe) EnvironmentLayer.DisableProbeAmbient();   // the negative control for the probe: the grey ambient alone
        await Frames(2);

        // the world under the sky: a flat ground of one known colour, in the sky probe's capture only (not in the view), lit by the sun alone as the capture lights the world; the probe's
        // capture is made by the engine (ProbeCapture) from 64 m above the camera, and the model below draws the same ground into the official chain on the CPU
        const float groundDrop = 10f, captureFar = 512f;
        int gr = 150, gg = 110, gb = 70;
        var groundAlbedo = new System.Numerics.Vector3(Decode8(gr), Decode8(gg), Decode8(gb));
        MeshInstance3D? ground = null;
        Shader? groundShader = null;
        ShaderMaterial? groundMaterial = null;
        ImageTexture? groundTexture = null;
        SkyProbe.WorldSample? groundModel = null;
        if (eep && !noProbe)
        {
            EnvironmentLayer.ProbeHazeOff = true;
            rig.Camera.Far = captureFar;
            rig.Camera.Transform = new Transform3D(Basis.Identity, Vector3.Zero);
            if (!noGround)
            {
                groundShader = FaceShader.Create(false, false);
                groundTexture = ImageTexture.CreateFromImage(Image.CreateFromData(1, 1, false, Image.Format.Rgba8, [(byte)gr, (byte)gg, (byte)gb, 255]));
                groundMaterial = new ShaderMaterial { Shader = groundShader };
                groundMaterial.SetShaderParameter("tex", groundTexture);
                ground = new MeshInstance3D
                {
                    Mesh = new QuadMesh { Size = new Vector2(40000f, 40000f) }, MaterialOverride = groundMaterial, Layers = ProbeCapture.Layer,
                    Transform = new Transform3D(new Basis(Vector3.Right, -MathF.PI / 2f), new Vector3(0f, -groundDrop, 0f)),
                };
                rig.Root.AddChild(ground);
            }
            for (int i = 0; i < 60 && !layer.ProbeSettled; i++) { layer.TickProbe(0); await Frames(1); }
            if (!layer.ProbeSettled) _failures.Add("the sky probe's capture did not finish in 60 frames");
            groundModel = SkyProbe.FlatGround(SkyProbe.SunLitRadiance(frame, groundAlbedo), groundDrop + SkyProbe.OriginHeight, captureFar);
        }

        ImageTexture Flat(byte r, byte g, byte b) => ImageTexture.CreateFromImage(Image.CreateFromData(1, 1, false, Image.Format.Rgba8, [r, g, b, 255]));
        var quad = new QuadMesh { Size = new Vector2(0.72f, 0.9f) };
        var rows = new List<object>();
        var kept = new List<Resource>();
        // under the tone-mapped path the ambient comes from the sky probe, which depends on the direction the face looks, so that case also has faces turned away from the sun
        double[] cosines = eep || dusk ? [-1.0, -0.6, -0.2, 0.0, 0.3, 0.6, 0.85, 1.0] : [0.0, 0.2, 0.45, 0.7, 0.85, 0.9, 0.95, 1.0];
        var probeMap = eep ? SkyProbe.Irradiance(frame, 1f, groundModel) : null;
        float mix = ColourPath.TonemapMix(frame.ProbeAmbiance);
        foreach (double c in cosines)
        {
            float a = Mathf.DegToRad(60f) - MathF.Acos((float)c);
            var basis = new Basis(Vector3.Right, -a);
            if (dusk)
            {
                // the sun is low and to one side: the face's normal is the sun's direction tilted up (through the zenith) by the angle whose cosine is c
                var sg = new Vector3(frame.SunDirection.X, frame.SunDirection.Z, -frame.SunDirection.Y).Normalized();
                var toSun = Basis.LookingAt(-sg, Vector3.Up);
                basis = new Basis(toSun.X, -MathF.Acos((float)c)) * toSun;
            }
            var tiles = new List<MeshInstance3D>();
            for (int i = 0; i < WorldPatches.Length; i++)
            {
                var p = WorldPatches[i];
                var tex = Flat(p.R, p.G, p.B); kept.Add(tex);
                Shader shader = p.Shader switch
                {
                    "avatar" => FaceShader.CreateBody(false),
                    "world" => FaceShader.Create(false, false),
                    _ => FaceShader.CreateLegacy(false, Viewer.Core.World.FaceAlpha.Opaque),
                };
                var mat = new ShaderMaterial { Shader = shader };
                mat.SetShaderParameter("tex", tex);
                if (p.Shader is "shiny" or "matte") mat.SetShaderParameter("gloss", p.Gloss);
                tiles.Add(new MeshInstance3D { Mesh = quad, MaterialOverride = mat, Transform = new Transform3D(basis, basis * new Vector3(WorldPatchX[i], 0f, 0f)) });
            }
            foreach (var t in tiles) rig.Root.AddChild(t);
            rig.Camera.Transform = new Transform3D(basis, basis * new Vector3(0f, 0f, 3f));
            await Frames(4);
            var img = rig.Texture.GetImage();
            var measured = new List<object>();
            float nl = (float)c;
            for (int i = 0; i < tiles.Count; i++)
            {
                var p = WorldPatches[i];
                var px = img.GetPixel((int)(width / 2f + WorldPatchX[i] * pixelsPerMetre), height / 2);
                int[] got = [(int)MathF.Round(px.R * 255f), (int)MathF.Round(px.G * 255f), (int)MathF.Round(px.B * 255f)];
                System.Numerics.Vector3 d;
                if (eep)
                {
                    // the face's world position and normal, and the camera's, in the viewer's world frame: the lookup direction into the sky probe's map
                    var n = basis * new Vector3(0f, 0f, 1f);
                    var at = basis * new Vector3(WorldPatchX[i], 0f, 0f);
                    var cam = basis * new Vector3(0f, 0f, 3f);
                    var dir = SkyProbe.LookupDirection(new System.Numerics.Vector3(at.X, at.Y, at.Z), new System.Numerics.Vector3(n.X, n.Y, n.Z), new System.Numerics.Vector3(cam.X, cam.Y, cam.Z));
                    d = SkyLighting.EepDiffuse(frame, nl, SkyProbe.SampleIrradiance(probeMap!, dir));
                }
                else d = SkyLighting.ClassicDiffuse(frame, nl);
                // the view is the normal: the half vector's dot product with the normal and with the view is sqrt((1 + cos) / 2)
                float nh = MathF.Sqrt((1f + nl) / 2f);
                System.Numerics.Vector3 spec = p.Shader != "shiny" ? System.Numerics.Vector3.Zero
                    : eep ? SkyLighting.EepSpecular(frame, nl, nh, 1f, nh, p.Gloss, System.Numerics.Vector3.One)
                    : SkyLighting.ClassicSpecular(frame, nl, nh, 1f, nh, p.Gloss, System.Numerics.Vector3.One);
                var lit = new System.Numerics.Vector3(Decode8(p.R) * d.X + spec.X, Decode8(p.G) * d.Y + spec.Y, Decode8(p.B) * d.Z + spec.Z);
                if (eep)
                {
                    // the haze between the camera and the face (the official haze pass), then the post pass's tone mapping
                    var seenFrom = basis * new Vector3(0f, 0f, 3f);
                    var seenAt = basis * new Vector3(WorldPatchX[i], 0f, 0f);
                    var ray = seenAt - seenFrom;
                    lit = SkyLighting.SeenThroughHaze(frame, lit, new System.Numerics.Vector3(ray.X, -ray.Z, ray.Y), ray.Length());
                    lit = ToneMap.Apply(lit, ToneMap.DefaultExposure, ToneMap.DefaultType, mix);
                }
                int[] want = [Encode8(lit.X), Encode8(lit.Y), Encode8(lit.Z)];
                int err = Math.Max(Math.Abs(got[0] - want[0]), Math.Max(Math.Abs(got[1] - want[1]), Math.Abs(got[2] - want[2])));
                measured.Add(new { patch = p.Name, got, want, max_error = err });
                if (err > WorldLightTolerance)
                    _failures.Add($"{kind} sky, cosine {c}: {p.Name}: got ({got[0]},{got[1]},{got[2]}), expected ({want[0]},{want[1]},{want[2]}) (off by {err}, allowed {WorldLightTolerance})");
            }
            rows.Add(new { cosine = c, patches = measured });
            if (c == cosines[^1]) img.SavePng(System.IO.Path.Combine(_out, $"{_tag}.png"));
            foreach (var t in tiles) { rig.Root.RemoveChild(t); t.Free(); }
        }
        _result["probe_capture"] = layer.DescribeProbeCapture();
        if (eep && groundModel is not null)
        {
            var dirs = new System.Numerics.Vector3[] { new(0, -1, 0), new(0, 1, 0), new(1, 0, 0), new(0, 0, 1), new(0.6f, -0.8f, 0), new(0, -0.3f, 0.95f) };
            static float[] A(System.Numerics.Vector3? v) => v is { } x ? [x.X, x.Y, x.Z] : [];
            _result["probe_compare"] = new
            {
                model_ground = A(SkyProbe.SunLitRadiance(frame, groundAlbedo)),
                sun_main = A(SkyLighting.Eep(frame).Sun), sun_pass = A(SkyLighting.Eep(SkyProbe.IrradiancePassFrame(frame)).Sun), sun_up = frame.SunDirection.Z,
                probe_sun_global = RenderingServer.GlobalShaderParameterGet("probe_sun").ToString(), classic_sun_global = RenderingServer.GlobalShaderParameterGet("classic_sun").ToString(),
                directions = dirs.Select(d => new { d = A(d), engine = A(layer.ProbeIrradianceAt(System.Numerics.Vector3.Normalize(d))), model = A(SkyProbe.SampleIrradiance(probeMap!, System.Numerics.Vector3.Normalize(d))) }).ToArray(),
            };
        }
        _result["worldlight"] = new { sky = kind, tolerance = WorldLightTolerance, tonemap_ran = HdrTonemapEffect.Ran, hdr_active = layer.HdrActive, rows };
        if (ground is not null) { rig.Root.RemoveChild(ground); ground.Free(); }
        groundMaterial?.Dispose(); groundShader?.Dispose(); groundTexture?.Dispose();
        EnvironmentLayer.ProbeHazeOff = false;
        EnvironmentLayer.DynamicExposure = true;
        layer.Clear();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        await Frames(1);
        return _failures.Count == 0 ? 0 : 1;
    }
}
