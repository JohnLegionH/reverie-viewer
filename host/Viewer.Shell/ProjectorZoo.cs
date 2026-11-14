// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// L4 "projector": four projector lamps in a row, each throwing a patterned picture (a grid with an arrow) down and forward
/// onto a pale floor and wall a few metres away, seen square on from <see cref="Eye"/> in a nearly dark region. The beams are
/// kept apart (a dark gap between neighbours) so each one is plain to see and to probe. The lamps differ in one thing each:
/// A is a narrow beam in sharp focus, B the same beam with the focus close to the lamp (the picture blurs with distance), C a
/// wider beam in sharp focus, and D the same wider beam with ambiance. A small glowing panel hangs in C's beam, so the glow
/// picture has a face the projector lights. The sun and the ambient light are turned down by the render so the beams show.
/// </summary>
public static class ProjectorZoo
{
    public const string Name = "projector.matzoo.json";

    /// <summary>The camera: a point (SL axes) looking east, level, with a vertical field of view in degrees.</summary>
    public static readonly Vector3 Eye = new(32, 129, 34);
    public const float FovDegrees = 40f;

    /// <summary>The wall's face (x) and the floor's top (z).</summary>
    public const float WallX = 70f, FloorZ = 30f;

    public static readonly Guid Pattern = TextureZoo.Grid;
    public static readonly Guid White = GlowZoo.White;

    /// <summary>The lamps: name, east-west position (y), field of view (radians), focus (metres), ambiance.</summary>
    public static readonly (string Name, float Y, float Fov, float Focus, float Ambiance)[] Lamps =
    [
        ("narrow-sharp", 114f, 0.8f, 8f, 0f),
        ("narrow-soft", 124f, 0.8f, 1f, 0f),
        ("wide-sharp", 134f, 1.1f, 8f, 0f),
        ("wide-ambiance", 144f, 1.1f, 8f, 0.6f),
    ];

    public static readonly Vector3 LampOffset = new(64f, 0f, 35f);
    public static readonly Vector3 AimAt = new(WallX - 0.1f, 0f, FloorZ + 1f);
    public const float Radius = 20f, Intensity = 2.5f;
    public static readonly Vector3 LightColour = new(1f, 0.95f, 0.85f);

    public static uint LampId(int i) => (uint)(9300 + i);
    public const uint FloorId = 9290, WallId = 9291, GlowPanelId = 9295;
    public static readonly Vector3 GlowPanel = new(WallX - 1f, 134f, 32.5f);

    /// <summary>The occluder: a small box hanging in the narrow soft beam (lamp <see cref="ShadowLamp"/>), halfway between the lamp and the wall and to
    /// one side of the beam's axis, so that it throws a shadow on the wall inside the beam, clear of the other probes.</summary>
    public const uint ShadowBlockId = 9296;
    public const int ShadowLamp = 1;
    public static readonly Vector3 ShadowBlockCentre = new(67f, 124.85f, 33.6f);
    public static readonly Vector3 ShadowBlockSize = new(0.2f, 0.8f, 0.8f);

    public static Vector3 LampPosition(int i) => new(LampOffset.X, Lamps[i].Y, LampOffset.Z);

    /// <summary>The rotation that turns a prim's -Z (where its light shines) to look from the lamp at its aim point.</summary>
    public static Quaternion Aim(int i)
    {
        var to = Vector3.Normalize(new Vector3(AimAt.X, Lamps[i].Y, AimAt.Z) - LampPosition(i));
        var from = new Vector3(0, 0, -1);
        var axis = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(axis, 1f + Vector3.Dot(from, to)));
    }

    public static IReadOnlyList<ZooPrim> Make()
    {
        // every face has a texture entry (a plain white one): what the real grid sends, and what the glow picture draws black
        var pale = new FaceColors(new Rgba(0.92f, 0.92f, 0.92f, 1f));   // light surfaces show the projected pattern at full contrast
        var plain = new FaceTextures(FaceTexture.Plain(White));
        var prims = new List<ZooPrim>
        {
            new(FloorId, 0, "floor", new Vector3(65f, 128f, FloorZ - 0.1f), Quaternion.Identity, new Vector3(30f, 60f, 0.2f), PrimShape.Box, pale, plain),
            new(WallId, 0, "wall", new Vector3(WallX + 0.1f, 128f, FloorZ + 6f), Quaternion.Identity, new Vector3(0.2f, 60f, 12f), PrimShape.Box, pale, plain),
        };
        for (int i = 0; i < Lamps.Length; i++)
        {
            var l = Lamps[i];
            prims.Add(new ZooPrim(LampId(i), 0, "lamp-" + l.Name, LampPosition(i), Aim(i), new Vector3(0.5f, 0.5f, 0.5f), PrimShape.Box,
                new FaceColors(new Rgba(0.15f, 0.15f, 0.15f, 1f)), plain,
                new PrimLight(LightColour, Intensity, Radius, 0f, 0f), new PrimProjector(Pattern, l.Fov, l.Focus, l.Ambiance)));
        }
        // the occluder: a dark box between lamp B and the wall (its shadow falls on the wall inside B's beam)
        prims.Add(new ZooPrim(ShadowBlockId, 0, "shadow-block", ShadowBlockCentre, Quaternion.Identity, ShadowBlockSize, PrimShape.Box,
            new FaceColors(new Rgba(0.3f, 0.3f, 0.3f, 1f)), plain));
        // a white panel hanging in front of the wall, in the wide sharp beam, glowing
        var glow = FaceTexture.Plain(White) with { Glow = 1f };
        prims.Add(new ZooPrim(GlowPanelId, 0, "glow-panel", GlowPanel, Quaternion.Identity, new Vector3(0.05f, 1f, 1f), PrimShape.Box,
            new FaceColors(Rgba.White), new FaceTextures(glow)));
        return prims;
    }

    public static readonly IReadOnlyDictionary<Guid, string> Images = new Dictionary<Guid, string>
    {
        [White] = GlowZoo.ImageFile, [Pattern] = "../textures/tex-grid-arrow.j2c",
    };

    public static string Serialise() => MaterialZoo.SerialiseCustom(Make(), Images, new Dictionary<Guid, string>(), new Dictionary<uint, IReadOnlyList<RenderMaterialEntry>>());

    /// <summary>Whether a world point lies inside a lamp's picture window (the pyramid of its field of view, from the apex to the reach),
    /// and how far from the beam's axis it is as a share of the window's half-size there (0 on the axis, 1 at the window's edge).</summary>
    public static (bool Inside, float Edge) Window(int lamp, Vector3 world)
    {
        var prim = Make().First(p => p.LocalId == LampId(lamp));
        var beam = LightRules.BeamOf(prim.Light!.Value, prim.Projector, prim.Scale)!.Value;
        var local = Vector3.Transform(world - prim.Position, Quaternion.Inverse(prim.Rotation));
        float along = -(local.Z - beam.Apex.Z);   // distance in front of the apex along the shining direction
        if (along < beam.Near || along > beam.Far) return (false, float.PositiveInfinity);
        float half = MathF.Tan(beam.Fov / 2) * along;
        float edge = MathF.Max(MathF.Abs(local.Y) / half, MathF.Abs(local.X) / (half * beam.Aspect));
        return (edge <= 1f, edge);
    }

    /// <summary>One probe: a point on the wall or floor, the lamp it belongs to, and whether that lamp's beam holds it.</summary>
    public readonly record struct Probe(string Name, Vector3 Point, int Lamp, bool Inside);

    /// <summary>
    /// The probes: for each lamp a point on the wall and one on the floor inside its beam, and two just outside it (on the wall above the
    /// beam, and on the wall in the gap beside it). A render must show the pattern inside and a dark flat surface outside.
    /// </summary>
    public static IReadOnlyList<Probe> Probes()
    {
        var list = new List<Probe>();
        for (int i = 0; i < Lamps.Length; i++)
        {
            float y = Lamps[i].Y;
            // the gap beside a wide beam is wider; the last lamp (with ambiance) lights a round cone wider than its picture, so its gap is wider still
            float gap = i == Lamps.Length - 1 ? 6.5f : Lamps[i].Fov > 1f ? 5.5f : 5f;
            float side = i == Lamps.Length - 1 ? y + gap : y - gap;   // the free side of the row's last lamp, else the side before it
            list.Add(new Probe($"{Lamps[i].Name}-wall-in", new Vector3(WallX, y, FloorZ + 2f), i, true));
            list.Add(new Probe($"{Lamps[i].Name}-floor-in", new Vector3(WallX - 2f, y, FloorZ), i, true));
            list.Add(new Probe($"{Lamps[i].Name}-wall-above-out", new Vector3(WallX, y, FloorZ + 9f), i, false));
            list.Add(new Probe($"{Lamps[i].Name}-wall-gap-out", new Vector3(WallX, side, FloorZ + 2f), i, false));
        }
        return list;
    }

    /// <summary>One shadow probe: a point on the wall inside the shadow lamp's beam, and whether the occluder stands between it and the lamp.</summary>
    public readonly record struct ShadowProbe(string Name, Vector3 Point, bool Shadowed);

    /// <summary>The shadow probes: one on the wall in the occluder's shadow and one on the wall in the same beam at the same height on the other side
    /// of the beam's axis, in the light. With shadows drawn the first is dark and the second shows the pattern; with shadows off both show it.</summary>
    public static IReadOnlyList<ShadowProbe> ShadowProbes()
    {
        float y = Lamps[ShadowLamp].Y;
        return [new ShadowProbe("shadow-wall-shadowed", new Vector3(WallX, y + 1.7f, FloorZ + 2.2f), true),
                new ShadowProbe("shadow-wall-lit", new Vector3(WallX, y - 1.7f, FloorZ + 2.2f), false)];
    }

    /// <summary>The view shares (u right, v down) of a point of the scene seen from <see cref="Eye"/>.</summary>
    public static (float U, float V) Project(Vector3 p, float aspect)
    {
        float depth = p.X - Eye.X, tan = MathF.Tan(FovDegrees * MathF.PI / 360f);
        return (0.5f + (Eye.Y - p.Y) / (depth * tan * aspect) / 2f, 0.5f - (p.Z - Eye.Z) / (depth * tan) / 2f);
    }
}
