// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Linq;
using System.Numerics;
using Viewer.Core.World;

namespace Viewer.Godot;

/// <summary>A development aid for the live ground check: bare, level land well above the water, from the viewer's own terrain and object data.</summary>
public partial class Main
{
    /// <summary>Searches every region the session holds terrain for. Each region is reported with its grid cell and how many of its places each rule turned
    /// down; <c>found</c> is the nearest place in the agent's own region and <c>best</c> the place to go to, with the grid cell to teleport to.
    /// <paramref name="prefer"/> names regions to try first, in that order (names only go in, never out); in another region the search starts at the point
    /// nearest the first preferred region's middle. Without a preference the agent's region comes first, then the others by distance.</summary>
    public object? PlainGroundNear(float nearX, float nearY, float minAboveWater, float clearRadius, string[]? prefer = null)
    {
        if (Model?.Session is not { } session || session.World.Region is null) return null;
        var worlds = session.Worlds.ToList();
        prefer ??= [];
        // the middle of the first preferred region that the session holds, in the anchor frame: the others are searched from the point of each nearest to it
        Vector2? focus = null;
        foreach (var name in prefer)
            if (worlds.FirstOrDefault(w => string.Equals(w.Region.Name, name, System.StringComparison.OrdinalIgnoreCase)) is { } pw)
            { focus = new Vector2(pw.OriginOffset.X + pw.Region.SizeX / 2f, pw.OriginOffset.Y + pw.Region.SizeY / 2f); break; }
        var rows = new System.Collections.Generic.List<object>();
        var candidates = new System.Collections.Generic.List<(int Rank, double Distance, int[] Grid, Vector3 Place)>();
        object? own = null;
        foreach (var w in worlds)
        {
            var region = w.Region;
            // a region whose handshake gave no water height is searched with the usual default (20 m) and says so, rather than being left out
            bool waterAssumed = region.WaterHeight is null;
            float water = region.WaterHeight ?? 20f;
            var world = w.World;
            float? Ground(float x, float y)
            {
                int gx = System.Math.Clamp((int)x, 0, region.SizeX - 1), gy = System.Math.Clamp((int)y, 0, region.SizeY - 1);
                return world.TryGetPatch(gx / 16, gy / 16, out var h) && h.Length >= 256 ? h[(gy % 16) * 16 + gx % 16] : null;
            }
            var objects = world.Snapshot().Objects.Values.Where(o => o.AttachmentPoint is null).Select(o => o.Position).ToList();
            bool here = w.IsCurrent;
            float sx = here ? nearX : region.SizeX / 2f, sy = here ? nearY : region.SizeY / 2f;
            if (!here && focus is { } f)
            {
                sx = System.Math.Clamp(f.X - w.OriginOffset.X, 0, region.SizeX);
                sy = System.Math.Clamp(f.Y - w.OriginOffset.Y, 0, region.SizeY);
            }
            var r = PlainGround.Search(Ground, water, region.SizeX, objects, minAboveWater, clearRadius, sx, sy);
            var found = r.Found is { } p ? new { x = p.X, y = p.Y, z = p.Z } : null;
            var grid = new[] { region.GridX, region.GridY };
            rows.Add(new
            {
                grid, current = here, preferred = System.Array.FindIndex(prefer, n => string.Equals(n, region.Name, System.StringComparison.OrdinalIgnoreCase)), water_height = (float?)water, water_assumed = waterAssumed,
                searched = true, terrain_patches = world.TerrainPatchCount, objects = objects.Count,
                places = r.Cells, no_terrain = r.NoTerrain, too_low = r.TooLow, not_level = r.NotLevel, object_near = r.ObjectNear, plain = r.Plain, found,
            });
            if (here) own = found;
            if (r.Found is { } place)
            {
                int pref = System.Array.FindIndex(prefer, n => string.Equals(n, region.Name, System.StringComparison.OrdinalIgnoreCase));
                int rank = pref >= 0 ? pref : prefer.Length + (here ? 0 : 1);
                var middle = new Vector2(w.OriginOffset.X + region.SizeX / 2f, w.OriginOffset.Y + region.SizeY / 2f);
                candidates.Add((rank, Vector2.Distance(middle, focus ?? Vector2.Zero), grid, place));
            }
        }
        var chosen = candidates.OrderBy(c => c.Rank).ThenBy(c => c.Distance).Select(c => ((int[] Grid, Vector3 Place)?)(c.Grid, c.Place)).FirstOrDefault();
        object? best = chosen is { } c0 ? new { grid = c0.Grid, x = c0.Place.X, y = c0.Place.Y, z = c0.Place.Z } : null;
        return new { water_height = session.World.Region!.WaterHeight, water_assumed = session.World.Region.WaterHeight is null, objects = session.World.Snapshot().Objects.Count, found = own, best, regions = rows.ToArray() };
    }
}
