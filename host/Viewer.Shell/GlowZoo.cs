// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// L4 "glow": a grid of flat panels floating in the sky, one column per kind of face (a coloured lit face, a textured lit
/// face, an alpha-blended face, a fullbright face, a PBR face with an emissive ring) and one row per glow value (0, 0.1,
/// 0.5 and 1.0, the 0 row being the control), seen square on from <see cref="Eye"/>. The scene is drawn against a dark
/// sky and a bright one. At each panel two probes are read: one on the face and one beside it, where the glow's halo
/// falls. Flat panels (coloured, blended and fullbright) have an expected colour worked from the glow rules and the
/// blur's reference arithmetic (<see cref="GlowBlur"/>): the face's colour as it is displayed times the glow value is
/// gathered into the glow picture, blurred and added to the displayed picture. The textured and PBR panels are checked as brighter than their control, not exactly.
/// </summary>
public static class GlowZoo
{
    public const string ImageFile = "tex-white-64.j2c";
    public static readonly Guid White = new("4e560009-0000-4000-8000-000000000030");

    /// <summary>The camera: a point (SL axes) looking east, level, with a vertical field of view in degrees.</summary>
    public static readonly Vector3 Eye = new(40, 128, 30);
    public const float FovDegrees = 30f;

    /// <summary>The panels' plane (x), size and spacing; the grid is centred on the camera's axis.</summary>
    public const float PanelX = 60f, PanelSize = 1.6f, Spacing = 2.4f;

    public static readonly string[] Kinds = ["coloured", "textured", "blended", "fullbright", "pbr"];
    public static readonly float[] Glows = [0f, 0.1f, 0.5f, 1f];

    /// <summary>The colours the flat panels are tinted: the coloured lit face, the blended face (also fullbright, so its
    /// colour is exact, at half alpha) and the fullbright face.</summary>
    public static readonly Rgba ColouredTint = new(1f, 0.35f, 0.2f, 1f), BlendedTint = new(0.2f, 0.6f, 1f, 0.5f), FullbrightTint = new(0.3f, 1f, 0.35f, 1f);

    public static uint LocalId(int kind, int row) => (uint)(9100 + kind * 10 + row);

    /// <summary>The centre of a panel (SL axes): kinds run left to right as seen (towards the south), rows top to bottom.</summary>
    public static Vector3 Centre(int kind, int row) =>
        new(PanelX, Eye.Y - (kind - (Kinds.Length - 1) / 2f) * Spacing, Eye.Z + ((Glows.Length - 1) / 2f - row) * Spacing);

    public static IReadOnlyList<ZooPrim> Make()
    {
        var prims = new List<ZooPrim>();
        for (int k = 0; k < Kinds.Length; k++)
            for (int r = 0; r < Glows.Length; r++)
            {
                float glow = Glows[r];
                Rgba tint = Kinds[k] switch { "coloured" => ColouredTint, "blended" => BlendedTint, "fullbright" => FullbrightTint, _ => Rgba.White };
                Guid tex = Kinds[k] switch { "textured" => MaterialZoo.BaseTex, "pbr" => MaterialZoo.BaseTex, _ => White };
                bool fullbright = Kinds[k] is "blended" or "fullbright";
                var ft = FaceTexture.Plain(tex) with { Fullbright = fullbright, Glow = glow };
                prims.Add(new ZooPrim(LocalId(k, r), 0, $"{Kinds[k]}-{glow:0.0}", Centre(k, r), Quaternion.Identity, new Vector3(0.05f, PanelSize, PanelSize),
                    PrimShape.Box, new FaceColors(tint), new FaceTextures(ft)));
            }
        return prims;
    }

    /// <summary>The PBR column's material on every face of its panels.</summary>
    public static IReadOnlyDictionary<uint, IReadOnlyList<RenderMaterialEntry>> RenderMaterials() =>
        Enumerable.Range(0, Glows.Length).ToDictionary(r => LocalId(Kinds.ToList().IndexOf("pbr"), r),
            _ => (IReadOnlyList<RenderMaterialEntry>)Enumerable.Range(0, 6).Select(f => new RenderMaterialEntry(f, MaterialZoo.MEmissive)).ToList());

    public static readonly IReadOnlyDictionary<Guid, string> Images = new Dictionary<Guid, string>
    {
        [White] = ImageFile, [MaterialZoo.BaseTex] = "mat-base.j2c", [MaterialZoo.EmissiveTex] = "mat-emissive.j2c",
    };

    public static readonly IReadOnlyDictionary<Guid, string> Materials = new Dictionary<Guid, string> { [MaterialZoo.MEmissive] = "mat-emissive.material" };

    public static string Serialise() => MaterialZoo.SerialiseCustom(Make(), Images, Materials, RenderMaterials());

    /// <summary>The skies the scene is drawn against (sRGB, 8 bits): a dark one and a bright one.</summary>
    public static readonly IReadOnlyDictionary<string, int[]> Skies = new Dictionary<string, int[]> { ["dark"] = [8, 10, 24], ["bright"] = [225, 232, 245] };

    /// <summary>The allowed difference per channel (8 bits) between a flat panel's probe and the worked colour: the blur is
    /// done in 8 bits per pass on the graphics card, and the glow picture is drawn at twice its size and averaged down.</summary>
    public const int Tolerance = 14;

    /// <summary>
    /// Where a panel's probes sit: its centre and a point just beyond its right edge (as seen), where the halo falls. The PBR
    /// panel glows only where its emissive ring is, so its probes sit on the ring (0.45 m above the centre) and in the hole
    /// beside it (0.28 m above the centre).
    /// </summary>
    public static (Vector3 Face, Vector3 Halo) ProbePoints(int kind, int row)
    {
        var c = Centre(kind, row);
        if (Kinds[kind] == "pbr") return (c + new Vector3(0, 0, 0.45f), c + new Vector3(0, 0, 0.28f));
        return (c, c + new Vector3(0, -(PanelSize / 2 + 0.15f), 0));
    }

    /// <summary>The panel's edges as the view sees them, as shares of the view (u right, v down), from a pinhole camera at
    /// <see cref="Eye"/> looking east with the vertical field <see cref="FovDegrees"/> and the view's aspect.</summary>
    public static (float U0, float V0, float U1, float V1) Rect(int kind, int row, float aspect)
    {
        var c = Centre(kind, row);
        float depth = c.X - Eye.X, tan = MathF.Tan(FovDegrees * MathF.PI / 360f);
        float U(float y) => 0.5f + (Eye.Y - y) / (depth * tan * aspect) / 2f;
        float V(float z) => 0.5f - (z - Eye.Z) / (depth * tan) / 2f;
        float h = PanelSize / 2;
        return (U(c.Y + h), V(c.Z + h), U(c.Y - h), V(c.Z - h));
    }

    /// <summary>The view shares (u, v) of a point of the scene.</summary>
    public static (float U, float V) Project(Vector3 p, float aspect)
    {
        float depth = p.X - Eye.X, tan = MathF.Tan(FovDegrees * MathF.PI / 360f);
        return (0.5f + (Eye.Y - p.Y) / (depth * tan * aspect) / 2f, 0.5f - (p.Z - Eye.Z) / (depth * tan) / 2f);
    }

    /// <summary>
    /// The colour (8-bit sRGB per channel) a probe at <paramref name="at"/> should show: <paramref name="displayed"/>
    /// (0..1 per channel, what the view shows there without glow) plus the blurred glow picture. The picture gathers, for
    /// each panel in <paramref name="glowInputs"/>, its colour (0..1, gamma-encoded) times its row's glow value over the
    /// panel's rectangle in a view of the given aspect.
    /// </summary>
    public static int[] Expected(IReadOnlyList<(int Kind, int Row, float[] Colour)> glowInputs, float aspect, (float U, float V) at, float[] displayed)
    {
        var pic = new float[GlowBlur.Resolution * GlowBlur.Resolution * 3];
        foreach (var (kind, row, colour) in glowInputs)
        {
            float w = FaceGlow.Quantise(Glows[row]);
            var (u0, v0, u1, v1) = Rect(kind, row, aspect);
            GlowBlur.Fill(pic, u0, v0, u1, v1, colour[0] * w, colour[1] * w, colour[2] * w);
        }
        var blurred = GlowBlur.Run(pic, GlowBlur.Resolution, GlowBlur.Resolution);
        var (r, g, b) = GlowBlur.At(blurred, at.U, at.V);
        return
        [
            (int)MathF.Round(GlowBlur.Add(displayed[0], r) * 255f),
            (int)MathF.Round(GlowBlur.Add(displayed[1], g) * 255f),
            (int)MathF.Round(GlowBlur.Add(displayed[2], b) * 255f),
        ];
    }
}
