// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Globalization;
using System.Numerics;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>What a busy view holds: <see cref="Objects"/> boxes, <see cref="GlowPercent"/> of them glowing, <see cref="Lights"/> prim lights of which <see cref="Projectors"/> are projectors.</summary>
public readonly record struct BusySpec(int Objects, int GlowPercent, int Lights, int Projectors);

/// <summary>The counts a busy scene's prims hold (the floor and the lamps are not objects).</summary>
public readonly record struct BusyCounts(int Objects, int Glowing, int Lights, int Projectors);

/// <summary>
/// A synthetic busy view for measuring the glow pass and the projector lights at scale: boxes spread through a 200 m square of a region,
/// a share of them glowing, prim lights (some of them projectors) among them, and an eye that circles on a fixed path so that the
/// object choice for the glow picture reruns as it moves. Deterministic for a seed.
/// </summary>
public static class BusyScene
{
    public const string ObjectName = "busy-object", LampName = "busy-lamp";
    public const uint FloorId = 30000, LampBase = 31000, ObjectBase = 40000;
    public const float Min = 28f, Max = 228f, Ground = 30f;
    public const double PathSeconds = 30;
    public const float PathRadius = 70f, PathHeight = 55f;
    public static readonly Vector3 Centre = new(128, 128, Ground + 4f);

    /// <summary>Reads <c>objects:N,glow:P,lights:L,projectors:K</c> (any order; a missing number is 0).</summary>
    public static BusySpec Parse(string text)
    {
        int objects = 0, glow = 0, lights = 0, projectors = 0;
        bool any = false;
        foreach (var part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':');
            if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out int v))
                throw new ArgumentException($"busy scene: '{part}' is not name:number (objects, glow, lights, projectors)");
            switch (kv[0])
            {
                case "objects": objects = v; break;
                case "glow": glow = v; break;
                case "lights": lights = v; break;
                case "projectors": projectors = v; break;
                default: throw new ArgumentException($"busy scene: unknown name '{kv[0]}' (objects, glow, lights, projectors)");
            }
            any = true;
        }
        if (!any) throw new ArgumentException("busy scene: objects:N,glow:percent,lights:L,projectors:K");
        if (glow > 100) throw new ArgumentException("busy scene: glow is a percentage, 0 to 100");
        if (projectors > lights) throw new ArgumentException("busy scene: projectors are lights, so projectors cannot exceed lights");
        return new BusySpec(objects, glow, lights, projectors);
    }

    /// <summary>The prims: a floor, the objects (the glowing ones drawn from the whole list), then the lamps (the projectors first).</summary>
    public static IReadOnlyList<ZooPrim> Make(BusySpec spec, int seed = 8)
    {
        var rnd = new Random(seed);
        var plain = FaceTexture.Plain(GlowZoo.White);
        var prims = new List<ZooPrim>(spec.Objects + spec.Lights + 1)
        {
            new(FloorId, 0, "busy-floor", new Vector3(128, 128, Ground - 0.1f), Quaternion.Identity, new Vector3(220f, 220f, 0.2f), PrimShape.Box,
                new FaceColors(new Rgba(0.6f, 0.6f, 0.6f, 1f)), new FaceTextures(plain)),
        };
        int glowing = (int)Math.Round(spec.Objects * spec.GlowPercent / 100.0);
        var order = Enumerable.Range(0, spec.Objects).ToArray();
        for (int i = order.Length - 1; i > 0; i--) { int j = rnd.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
        var glows = new HashSet<int>(order.Take(glowing));
        for (int i = 0; i < spec.Objects; i++)
        {
            var at = new Vector3(Min + rnd.NextSingle() * (Max - Min), Min + rnd.NextSingle() * (Max - Min), Ground + 0.5f + rnd.NextSingle() * 12f);
            var rot = Quaternion.Normalize(new Quaternion(rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f, 1f));
            float size = 1f + rnd.NextSingle();
            var colour = new FaceColors(new Rgba(0.3f + 0.6f * rnd.NextSingle(), 0.3f + 0.6f * rnd.NextSingle(), 0.3f + 0.6f * rnd.NextSingle(), 1f));
            var tex = glows.Contains(i) ? plain with { Glow = 0.5f } : plain;
            prims.Add(new ZooPrim(ObjectBase + (uint)i, 0, ObjectName, at, rot, new Vector3(size), PrimShape.Box, colour, new FaceTextures(tex)));
        }
        for (int i = 0; i < spec.Lights; i++)
        {
            var at = new Vector3(Min + rnd.NextSingle() * (Max - Min), Min + rnd.NextSingle() * (Max - Min), Ground + 6f + rnd.NextSingle() * 6f);
            bool projector = i < spec.Projectors;
            // a lamp shines down its -Z, which an identity rotation leaves pointing at the floor
            prims.Add(new ZooPrim(LampBase + (uint)i, 0, LampName, at, Quaternion.Identity, new Vector3(0.4f), PrimShape.Box,
                new FaceColors(new Rgba(0.15f, 0.15f, 0.15f, 1f)), new FaceTextures(plain),
                new PrimLight(ProjectorZoo.LightColour, 2f, 15f, 0f, 0f), projector ? new PrimProjector(ProjectorZoo.Pattern, 1f, 8f, 0f) : null));
        }
        return prims;
    }

    public static BusyCounts Count(IEnumerable<ZooPrim> prims)
    {
        int objects = 0, glowing = 0, lights = 0, projectors = 0;
        foreach (var p in prims)
        {
            if (p.Name == ObjectName) { objects++; if (p.Textures!.Default.Glow > 0f) glowing++; }
            if (p.Light is not null) { lights++; if (p.Projector is not null) projectors++; }
        }
        return new BusyCounts(objects, glowing, lights, projectors);
    }

    /// <summary>The eye on its fixed circle round the middle (SL axes) after <paramref name="seconds"/>, and the point it looks at; the path closes at <see cref="PathSeconds"/>.</summary>
    public static (Vector3 Eye, Vector3 Target) EyeAt(double seconds)
    {
        double a = seconds / PathSeconds * Math.Tau;
        return (new Vector3(Centre.X + PathRadius * (float)Math.Cos(a), Centre.Y + PathRadius * (float)Math.Sin(a), PathHeight), Centre);
    }
}
