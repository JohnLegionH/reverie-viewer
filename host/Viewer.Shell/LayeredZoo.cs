// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// L4 "layered transparency": blended faces at known depths in front of an opaque backdrop, each case built so the
/// order it is drawn in shows in its colour, and probe points where the expected colour is worked out from the blend
/// arithmetic (linear light: what is drawn later covers what is behind by its alpha). Two scenes:
/// <list type="bullet">
/// <item><see cref="Make"/> (fixtures/materials/layered-transparency.texzoo.json, camera <see cref="Eye"/> looking east,
/// 60 degrees): (1) a wide near pane off to the side over a far pane straight ahead, the near pane's centre farther
/// from the eye than the far pane's; (2) a pane inside a blended box, a little in front of the box's centre; (3) a
/// tube seen across its hole, its outer near face over its inner far face (two blended faces of one object); (4) the
/// first case as a linkset (the far pane a lit child of the near pane).</item>
/// <item><see cref="MakeWater"/> (layered-water.texzoo.json, camera <see cref="WaterEye"/> a metre above the water):
/// a pane crossing the water and a pane under it, before a wall that is black and white under the water.</item>
/// </list>
/// Every face is fullbright (drawn unlit) except the linkset's far pane, so the colours are exact; where the result
/// depends on what the engine draws (the lit pane, the water) the expected colour is worked from reference probes
/// beside it. Faces carry a plain white texture (fixtures/materials/tex-white-64.j2c) tinted with their colour and alpha.
/// </summary>
public static class LayeredZoo
{
    public const string ImageFile = "tex-white-64.j2c";
    public const int ImageSize = 64;
    public static readonly Guid White = new("4e560009-0000-4000-8000-000000000001");
    public const float Alpha = 0.5f;
    public const float WaterHeight = 20f;
    /// <summary>The time of day the water scene is drawn at (the sun's place is fixed, so the water's look is).</summary>
    public const double DayFraction = 0.3;
    /// <summary>The cameras: the point they stand at (SL axes); both look east, level, 60 degrees.</summary>
    public static readonly Vector3 Eye = new(40, 128, 30), WaterEye = new(40, 128, 21);
    public const float FovDegrees = 60f;

    public static readonly Rgba Grey = new(0.5f, 0.5f, 0.5f, 1), Red = new(1, 0, 0, Alpha), Blue = new(0, 0, 1, Alpha), Green = new(0, 1, 0, Alpha),
        Yellow = new(1, 1, 0, Alpha), Magenta = new(1, 0, 1, Alpha), Cyan = new(0, 1, 1, Alpha), Orange = new(1, 0.5f, 0, Alpha), LitWhite = new(1, 1, 1, Alpha),
        Black = new(0, 0, 0, 1), WhiteOpaque = new(1, 1, 1, 1);

    /// <summary>The white image's texels as decoded (every texel decodes to this value).</summary>
    public const int Texel8 = 255;

    /// <summary>The tube's faces: its outer surface (0) and its inner surface (2); 1 and 3 are its two ends.</summary>
    public const int TubeOuterFace = 0, TubeInnerFace = 2;
    /// <summary>A tube: a square profile swept round a circle about the prim's x axis, a hole half its size.</summary>
    public static readonly PrimShape Tube = new() { ProfileCurve = 1, PathCurve = 32, PathScaleY = 0.25 };

    private static FaceTexture Tex(bool fullbright = true) => FaceTexture.Plain(White) with { Fullbright = fullbright };
    private static ZooPrim Box(uint id, string name, Vector3 at, Vector3 scale, Rgba colour, bool fullbright = true, uint parent = 0, PrimShape? shape = null, FaceColors? colours = null, Quaternion? rot = null) =>
        new(id, parent, name, at, rot ?? Quaternion.Identity, scale, shape ?? PrimShape.Box, colours ?? new FaceColors(colour), new FaceTextures(Tex(fullbright)));

    public static IReadOnlyList<ZooPrim> Make() =>
    [
        Box(8001, "backdrop", new(75, 128, 30), new(0.2f, 80, 50), Grey),
        // (1) the near pane (centre 14.1 m away, 10 m along the view) and the far pane (13.3 m away, 13 m along it)
        Box(8002, "near-wide", new(50, 118, 33), new(0.05f, 16, 4), Red),
        Box(8003, "far-ahead", new(53, 125, 33), new(0.05f, 4, 3), Blue),
        // (2) a blended box 14 m away and a pane inside it, half a metre in front of its centre
        Box(8004, "glass-box", new(54, 134, 33), new(4, 4, 4), Green),
        Box(8005, "inner-pane", new(53.5f, 134, 33), new(0.05f, 2, 2), Yellow),
        // (3) a 4 m tube 14 m away, its axis turned north-south, so the view east crosses its hole: magenta outside
        // and on its ends, cyan inside
        Box(8006, "tube", new(54, 125, 24), new(4, 4, 4), Magenta, shape: Tube, rot: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
            colours: new FaceColors(Magenta, Enumerable.Range(0, TubeInnerFace + 1).Select(f => f == TubeInnerFace ? (Rgba?)Cyan : null))),
        // (4) case 1 as a linkset: the near pane the root, the far pane a lit child (its position local to the root)
        Box(8007, "linked-near", new(50, 138, 27), new(0.05f, 16, 4), Orange),
        Box(8008, "linked-far", new(3, -7, 0), new(0.05f, 4, 3), LitWhite, fullbright: false, parent: 8007),
    ];

    public static IReadOnlyList<ZooPrim> MakeWater() =>
    [
        // a wall 12 m away: grey above the water, black (south) and white (north) under it
        Box(8101, "wall-above", new(52.1f, 128, 23), new(0.2f, 30, 6), Grey),
        Box(8102, "wall-black", new(52.1f, 120.5f, 17), new(0.2f, 15, 6), Black),
        Box(8103, "wall-white", new(52.1f, 135.5f, 17), new(0.2f, 15, 6), WhiteOpaque),
        // a pane 8 m away crossing the water (18.5 to 21.5 m), and a pane 10 m away under it
        Box(8104, "crossing", new(48, 128, 20), new(0.05f, 8, 3), Red),
        Box(8105, "under", new(50, 128, 17), new(0.05f, 8, 1.6f), Green),
    ];

    /// <summary>A probe: a direction from the camera (yaw from east toward north, pitch up, degrees) and how its expected
    /// colour is worked out (null: a reference probe, measured only).</summary>
    public sealed record Probe(string Name, float Yaw, float Pitch, string? Expected);

    public static readonly IReadOnlyList<Probe> Probes =
    [
        new("near_only", -35, 12, "red over grey"),
        new("far_only", -8, 12, "blue over grey"),
        new("near_over_far", -16, 12, "red over (blue over grey)"),
        new("box_only", 31, 12, "green over grey"),
        new("pane_in_box", 24, 12, "green over (yellow over grey)"),
        new("tube_outer_only", -10.9f, -29, "magenta over grey"),
        new("tube_outer_over_inner", -10.9f, -23, "magenta over (cyan over grey)"),
        new("linked_far_only", 8, -12, null),
        new("linked_near_only", 35, -12, "orange over grey"),
        new("linked_near_over_far", 16, -12, "orange over (the lit far pane over grey, measured)"),
    ];

    public static readonly IReadOnlyList<Probe> WaterProbes =
    [
        new("above_ref", 30, -1, null),
        new("crossing_above", 13, -1, "red over (the wall above the water, measured)"),
        new("black_ref", -30, -12, null),
        new("white_ref", 30, -12, null),
        new("crossing_below_black", -13, -12, "water over (red over black), the water from the references"),
        new("crossing_below_white", 13, -12, "water over (red over white), the water from the references"),
        new("under_black_ref", -30, -21, null),
        new("under_white_ref", 30, -21, null),
        new("under_black", -12, -21, "water over (green over black), the water from the references"),
        new("under_white", 12, -21, "water over (green over white), the water from the references"),
    ];

    /// <summary>A probe's direction (SL axes, unit length).</summary>
    public static Vector3 Direction(Probe p)
    {
        float y = p.Yaw * MathF.PI / 180f, t = p.Pitch * MathF.PI / 180f;
        return new Vector3(MathF.Cos(t) * MathF.Cos(y), MathF.Cos(t) * MathF.Sin(y), MathF.Sin(t));
    }

    /// <summary>sRGB to linear (as the face shader converts its colour) and back (as the engine writes the image).</summary>
    public static float ToLinear(float c) => c < 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    public static float ToSrgb(float c) => c < 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
    /// <summary>A face's colour as drawn (linear): its tint times the decoded white texel, then to linear light.</summary>
    public static Vector3 Linear(Rgba c) => new(ToLinear(c.R * Texel8 / 255f), ToLinear(c.G * Texel8 / 255f), ToLinear(c.B * Texel8 / 255f));

    /// <summary><paramref name="c"/> blended over <paramref name="behind"/> (linear) by its alpha.</summary>
    public static Vector3 Over(Rgba c, Vector3 behind) => Linear(c) * c.A + behind * (1 - c.A);

    /// <summary>
    /// The expected colour (linear) at each probe that has one, from the blend arithmetic and, where the scene's own
    /// lighting or water decides part of it, the reference probes measured in the same image (<paramref name="measured"/>,
    /// linear). The water is taken as a blend over what is under it with a fixed colour and coverage at each probe, so
    /// its effect is read from the black and white references: water(x) = black + (white - black) * x / white wall.
    /// </summary>
    public static IReadOnlyDictionary<string, Vector3> Expected(bool water, IReadOnlyDictionary<string, Vector3> measured)
    {
        var g = Linear(Grey);
        var e = new Dictionary<string, Vector3>();
        if (!water)
        {
            e["near_only"] = Over(Red, g);
            e["far_only"] = Over(Blue, g);
            e["near_over_far"] = Over(Red, Over(Blue, g));
            e["box_only"] = Over(Green, g);
            e["pane_in_box"] = Over(Green, Over(Yellow, g));
            e["tube_outer_only"] = Over(Magenta, g);
            e["tube_outer_over_inner"] = Over(Magenta, Over(Cyan, g));
            e["linked_near_only"] = Over(Orange, g);
            e["linked_near_over_far"] = Over(Orange, measured["linked_far_only"]);
            return e;
        }
        var w = Linear(WhiteOpaque);   // the white wall as drawn (the black one is 0)
        Vector3 Water(string black, string white, Vector3 x) => measured[black] + (measured[white] - measured[black]) * x / w;
        e["crossing_above"] = Over(Red, measured["above_ref"]);
        e["crossing_below_black"] = Water("black_ref", "white_ref", Over(Red, Vector3.Zero));
        e["crossing_below_white"] = Water("black_ref", "white_ref", Over(Red, w));
        e["under_black"] = Water("under_black_ref", "under_white_ref", Over(Green, Vector3.Zero));
        e["under_white"] = Water("under_black_ref", "under_white_ref", Over(Green, w));
        return e;
    }

    /// <summary>A linear colour as the 8-bit sRGB the image holds.</summary>
    public static int[] Srgb8(Vector3 c) => [(int)MathF.Round(Math.Clamp(ToSrgb(Math.Clamp(c.X, 0, 1)), 0, 1) * 255),
        (int)MathF.Round(Math.Clamp(ToSrgb(Math.Clamp(c.Y, 0, 1)), 0, 1) * 255), (int)MathF.Round(Math.Clamp(ToSrgb(Math.Clamp(c.Z, 0, 1)), 0, 1) * 255)];

    /// <summary>The allowed difference per channel (8-bit): exact cases, and cases read through the water's references
    /// (the water's coverage changes a little with the angle of view).</summary>
    public const int Tolerance = 3, WaterTolerance = 12;

    /// <summary>The plain white image (RGBA8, top row first).</summary>
    public static byte[] Pixels() => Enumerable.Repeat((byte)255, ImageSize * ImageSize * 4).ToArray();

    public static string Serialise(IReadOnlyList<ZooPrim> prims, bool water)
    {
        var root = JsonNode.Parse(PrimZoo.Serialise(prims))!.AsObject();
        root["format"] = TextureZoo.Format;
        root["images"] = new JsonObject { [White.ToString()] = ImageFile };
        root["meshes"] = new JsonObject();
        if (water)
        {
            root["water_height"] = WaterHeight;
            root["day_fraction"] = DayFraction;
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }
}
