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
/// --tr-skinlight=legacy|eep|legacy-off (the negative control: the classic switch put off after the sky is applied, so the engine's own light draws the patches and the check must fail): flat patches of skin colours (a mid and a light skin texel, and white) on the avatar shader, lit by a stock midday frame with the sun
/// 60 degrees up, the patches turned so the angle between their normal and the sun takes several values. The picture's pixels are compared with the official classic
/// lighting worked out on the CPU (<see cref="SkyLighting.ClassicDiffuse"/>, checked against the 26.4 shaders' formulas by ClassicLightTests): the colour times
/// 1.1 * linear(ambient * ease + encode(angle^1.2) * sun), encoded for the display. Under legacy a shiny legacy-material patch (a white specular, glossiness 0.5)
/// is measured too, at its brightest (the half vector on the normal) and off it. Under eep (a sky that takes the tone-mapped path) the avatar shader keeps the engine's
/// light: with the view along the normal and the light the engine's diffuse and a Lambert response agree, so the avatar patch must give the plain face shader's pixel.
/// </summary>
public partial class TestRender
{
    /// <summary>Levels (of 255) a measured channel may differ from the expected: the engine's rounding on its way to the 8-bit picture.</summary>
    private const int SkinLightTolerance = 3;

    private static readonly (string Name, byte R, byte G, byte B)[] SkinPatches =
    [
        ("mid skin", 170, 110, 85),
        ("light skin", 235, 190, 165),
        ("white", 255, 255, 255),
    ];

    // where the patches sit across the picture (metres from its middle): the shiny one on the camera's axis, so the view vector is the normal's whatever the engine does for an orthographic view
    private static readonly float[] SkinX = [-1.6f, -0.8f, 0.8f], ExtraX = [0f, 1.6f, -2.4f];

    /// <summary>A stock midday frame (overhead-sun values of a standard day cycle) with the sun 60 degrees up; in the sky frame's axes (x east, y north, z up).</summary>
    private static SkyFrame SkinFrame(float probeAmbiance) => new(0.5f, new System.Numerics.Vector3(0f, -0.5f, 0.8660254f), new(0.73421f, 0.78158f, 0.9f),
        -System.Numerics.Vector3.UnitZ, 0.5f, new(1.05f, 1.05f, 1.05f), new(0.49548f, 0.49548f, 0.64f), new(0.24476f, 0.44872f, 0.76f), 0.19f, 0.7f, 0.00018f, 0.8f,
        1605f, 0.27f, new System.Numerics.Vector3(5f, 0.001f, -0.48f), null, probeAmbiance);

    private static int Encode8(float linear)
    {
        float v = Math.Clamp(linear, 0f, 1f);
        float e = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        return (int)MathF.Round(e * 255f);
    }

    private static float Decode8(int v)
    {
        float c = v / 255f;
        return c < 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private async Task<int> SkinLightRun(string kind)
    {
        bool eep = kind == "eep", off = kind == "legacy-off";
        const int width = 560, height = 100;
        const float pixelsPerMetre = 100f, cameraX = 0f;
        var rig = MakeRig(new Vector2I(width, height));
        rig.Camera.Projection = Camera3D.ProjectionType.Orthogonal;
        rig.Camera.Size = 1.0f;   // the view's height in metres; the width follows the picture's shape
        var layer = new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun) { HdrEnabled = true };
        var region = RegionEnvironment.Default;
        var frame = SkinFrame(eep ? 0.5f : 0f);
        layer.Apply(region, frame, null, 256, 256);
        if (off) EnvironmentLayer.ResetClassicLight();
        await Frames(2);
        if (!eep) System.IO.File.WriteAllText(System.IO.Path.Combine(_out, _tag + ".legacy-classic.gdshader"), FaceShader.CreateLegacyClassicForTest().Code);

        ImageTexture Flat(byte r, byte g, byte b) => ImageTexture.CreateFromImage(Image.CreateFromData(1, 1, false, Image.Format.Rgba8, [r, g, b, 255]));
        var quad = new QuadMesh { Size = new Vector2(0.72f, 0.9f) };
        var rows = new List<object>();
        var kept = new List<Resource>();

        // The sun's direction in the engine's axes is (0, sin 60, cos 60) = (0, 0.866, 0.5); a normal (0, sin a, cos a) makes the angle with cosine cos(a - 60 degrees).
        double[] cosines = eep ? [1.0] : [0.0, 0.2, 0.45, 0.7, 0.85, 0.9, 0.95, 1.0];
        foreach (double c in cosines)
        {
            float a = Mathf.DegToRad(60f) - MathF.Acos((float)c);
            var basis = new Basis(Vector3.Right, -a);   // turns +Z to (0, sin a, cos a)
            var tiles = new List<MeshInstance3D>();
            var materials = new List<ShaderMaterial>();
            for (int i = 0; i < SkinPatches.Length; i++)
            {
                var p = SkinPatches[i];
                var tex = Flat(p.R, p.G, p.B); kept.Add(tex);
                var mat = new ShaderMaterial { Shader = FaceShader.CreateBody(false) };
                mat.SetShaderParameter("tex", tex);
                materials.Add(mat);
                tiles.Add(new MeshInstance3D { Mesh = quad, MaterialOverride = mat, Transform = new Transform3D(basis, basis * new Vector3(SkinX[i], 0f, 0f)) });
            }
            // after the three skin patches: legacy sky, a shiny legacy-material patch (mid skin, white specular, glossiness 0.5), the same with glossiness 0 (no
            // specular: diffuse only) and, as a reference, the shader the avatars had before the classic response (reported, not gated); eep sky, the plain face
            // shader on the mid skin colour
            var extraTex = Flat(170, 110, 85); kept.Add(extraTex);
            Shader[] extra = eep ? [FaceShader.Create(false, false)] : [FaceShader.CreateLegacyClassicForTest(), FaceShader.CreateLegacyClassicForTest(), FaceShader.CreateLegacy(false, Viewer.Core.World.FaceAlpha.Opaque)];
            for (int k = 0; k < extra.Length; k++)
            {
                var mat = new ShaderMaterial { Shader = extra[k] };
                mat.SetShaderParameter("tex", extraTex);
                if (!eep && k == 1) mat.SetShaderParameter("gloss", 0f);
                materials.Add(mat);
                tiles.Add(new MeshInstance3D { Mesh = quad, MaterialOverride = mat, Transform = new Transform3D(basis, basis * new Vector3(ExtraX[k], 0f, 0f)) });
            }
            foreach (var t in tiles) rig.Root.AddChild(t);
            rig.Camera.Transform = new Transform3D(basis, basis * new Vector3(cameraX, 0f, 3f));
            await Frames(4);
            var img = rig.Texture.GetImage();
            int[] Pixel(float tileX)
            {
                var px = img.GetPixel((int)(width / 2f + (tileX - cameraX) * pixelsPerMetre), height / 2);
                return [(int)MathF.Round(px.R * 255f), (int)MathF.Round(px.G * 255f), (int)MathF.Round(px.B * 255f)];
            }
            var measured = new List<object>();
            for (int i = 0; i < tiles.Count; i++)
            {
                float tileX = i < SkinPatches.Length ? SkinX[i] : ExtraX[i - SkinPatches.Length];
                var got = Pixel(tileX);
                string name;
                int[]? want = null;
                float nl = (float)c;
                if (i < SkinPatches.Length)
                {
                    var p = SkinPatches[i];
                    name = p.Name;
                    if (!eep)
                    {
                        var d = SkyLighting.ClassicDiffuse(frame, nl);
                        want = [Encode8(Decode8(p.R) * d.X), Encode8(Decode8(p.G) * d.Y), Encode8(Decode8(p.B) * d.Z)];
                    }
                }
                else if (!eep)
                {
                    int k = i - SkinPatches.Length;
                    var d = SkyLighting.ClassicDiffuse(frame, nl);
                    // the view is the normal: the half vector's dot product with the normal and with the view is sqrt((1 + cos) / 2)
                    float nh = MathF.Sqrt((1f + nl) / 2f);
                    var spec = k == 0 ? SkyLighting.ClassicSpecular(frame, nl, nh, 1f, nh, 0.5f, System.Numerics.Vector3.One) : System.Numerics.Vector3.Zero;
                    name = k == 0 ? "shiny mid skin (white specular, glossiness 0.5)" : k == 1 ? "mid skin, glossiness 0 (no specular)" : "the legacy shader before the classic response (reported)";
                    if (k < 2) want = [Encode8(Decode8(170) * d.X + spec.X), Encode8(Decode8(110) * d.Y + spec.Y), Encode8(Decode8(85) * d.Z + spec.Z)];
                }
                else
                {
                    name = "plain face shader, mid skin";
                    want = Pixel(SkinX[0]);   // the avatar shader's own mid skin patch
                }
                int err = want is null ? 0 : Math.Max(Math.Abs(got[0] - want[0]), Math.Max(Math.Abs(got[1] - want[1]), Math.Abs(got[2] - want[2])));
                measured.Add(new { patch = name, got, want, max_error = err });
                if (want is not null && err > SkinLightTolerance)
                    _failures.Add($"{kind} sky, cosine {c}: {name}: got ({got[0]},{got[1]},{got[2]}), expected ({want[0]},{want[1]},{want[2]}) (off by {err}, allowed {SkinLightTolerance})");
            }
            rows.Add(new { cosine = c, patches = measured });
            if (c == cosines[^1]) img.SavePng(System.IO.Path.Combine(_out, $"{_tag}.png"));
            foreach (var t in tiles) { rig.Root.RemoveChild(t); t.Free(); }
        }
        _result["skinlight"] = new { sky = kind, tolerance = SkinLightTolerance, rows };
        layer.Clear();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        await Frames(1);
        return _failures.Count == 0 ? 0 : 1;
    }
}
