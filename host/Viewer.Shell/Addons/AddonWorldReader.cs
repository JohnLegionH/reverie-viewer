// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Addons;
using Viewer.Core.World;

namespace Viewer.Shell.Addons;

/// <summary>Where the camera and the own avatar are drawn, in the anchor region's frame (the frame the presentation draws in).</summary>
public readonly record struct AddonViewpoint(Vector3 CameraPosition, Vector3 CameraForward, Vector3 CameraUp, Vector3 AgentPosition, Vector3 AgentVelocity);

/// <summary>What add-ons read of the world, taken from the current region's world state and the presentation's viewpoint.</summary>
public static class AddonWorldReader
{
    /// <summary>The world as a snapshot in the current region's own metres; null while the world has no region.</summary>
    public static AddonWorldSnapshot? Read(WorldState world, AddonViewpoint view, SkyFrame? sky, AddonEnvironment? environment)
    {
        if (world.Region is not { } region)
            return null;
        var origin = world.OriginOffset;
        var camera = view.CameraPosition - origin;
        return new AddonWorldSnapshot(region.Name, region.SizeX, region.SizeY,
            new AddonCameraPose(camera, Unit(view.CameraForward), Unit(view.CameraUp)),
            view.AgentPosition - origin, view.AgentVelocity, region.WaterHeight,
            region.WaterHeight is { } water && camera.Z < water,   // the test the world sounds make for "under water"
            sky is null ? null : Unit(sky.SunDirection), environment);
    }

    /// <summary>The sky in brief. <paramref name="source"/> is the presentation's own account of where the day cycle came from
    /// ("region", "parcel", or "default: why").</summary>
    public static AddonEnvironment Summary(string? source, SkyChoice choice, SkyFrame sky) =>
        new(source is "region" or "parcel" ? source : "default", choice.ToString().ToLowerInvariant(), sky.SunDirection.Z < 0,
            float.IsFinite(sky.CloudShadow) ? Math.Clamp(sky.CloudShadow, 0f, 1f) : 0f);

    /// <summary>The ground at a place in the region, between its samples as the land is drawn; null outside the region, or until every
    /// 16 m patch the place lies between has arrived.</summary>
    public static float? GroundHeight(WorldState world, float x, float y)
    {
        if (!Inside(world, x, y, out var region))
            return null;
        int x0 = (int)x, y0 = (int)y;
        foreach (int px in new[] { x0, Math.Min(x0 + 1, region.SizeX - 1) })
            foreach (int py in new[] { y0, Math.Min(y0 + 1, region.SizeY - 1) })
                if (!world.TryGetPatch(px / 16, py / 16, out _))
                    return null;
        var origin = world.OriginOffset;
        return RegionGround.Height([world], x + origin.X, y + origin.Y);
    }

    /// <summary>The wind the region sent for a place in it; null outside the region or while it has sent none (only the 256 m
    /// region's wind layer is read, so a larger region has none).</summary>
    public static Vector3? WindAt(WorldState world, float x, float y) =>
        Inside(world, x, y, out var region) && world.Wind is { } wind ? wind.At(x, y, region.SizeX, region.SizeY) : null;

    private static bool Inside(WorldState world, float x, float y, out Viewer.Core.Session.RegionInfo region)
    {
        region = world.Region!;
        return region is not null && x >= 0 && y >= 0 && x < region.SizeX && y < region.SizeY;   // false for NaN too
    }

    private static Vector3 Unit(Vector3 v) => v.LengthSquared() > 0 ? Vector3.Normalize(v) : v;
}
