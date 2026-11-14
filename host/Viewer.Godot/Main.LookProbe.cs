// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using System.Linq;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Godot;

/// <summary>A development aid for the live look check: where the water meets the land, where the trees and the larger textured objects are.</summary>
public partial class Main
{
    public object? LookProbe()
    {
        var s = Model?.Session;
        var world = s?.World;
        if (world?.Region is not { } region || region.WaterHeight is not { } water) return null;
        float size = region.SizeX;
        float Ground(float x, float y)
        {
            if (!world.TryGetPatch((int)x / 16, (int)y / 16, out var h) || h.Length < 256) return float.MaxValue / 4;
            return h[((int)y % 16) * 16 + (int)x % 16];
        }
        var objects = world.Snapshot().Objects.Values.Where(o => o.ParentLocalId == 0 && o.AttachmentPoint is null).ToList();
        var trees = objects.Where(o => o.Shape is { SculptType: PrimShape.SculptTypeMesh } sh && TreeMesh.TryInfo(sh.SculptId, out _, out _))
            .Select(o => { TreeMesh.TryInfo(o.Shape!.SculptId, out int species, out _); return new { x = o.Position.X, y = o.Position.Y, z = o.Position.Z, species }; }).ToList();
        var builds = objects.Where(o => o.Shape is not { SculptType: PrimShape.SculptTypeMesh } && o.Textures is { } t && t.Default.TextureId != System.Guid.Empty)
            .OrderByDescending(o => o.Scale.X * o.Scale.Y * o.Scale.Z).Take(6)
            .Select(o => new { x = o.Position.X, y = o.Position.Y, z = o.Position.Z, scale = new[] { o.Scale.X, o.Scale.Y, o.Scale.Z }, texture = o.Textures!.Default.TextureId.ToString() }).ToList();
        var shores = ShorePoints.Shores(Ground, water, size, 4).Select(p => new { x = p.X, y = p.Y, z = p.Z }).ToList();
        var sea = ShorePoints.Sea(Ground, water, size);
        return new { region = new { size, water_height = water }, water_map = _presenter?.WaterMapStatus, trees, builds, shores, sea = sea is { } p2 ? new { x = p2.X, y = p2.Y } : null };
    }
}
