// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using System.Linq;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Godot;

/// <summary>A development aid for the live level-of-detail check: the mesh objects nearest a point, what each is drawn at and why, and the textures on its faces.</summary>
public partial class Main
{
    public object? MeshProbe(float x, float y, float z, float radius, int max, bool names = false)
    {
        var world = Model?.Session?.World;
        var prims = _currentStack?.Prims ?? _presenter?.Prims;
        if (world is null || prims is null) return null;
        var objects = world.Snapshot().Objects;
        var found = new List<(float Distance, ObjectState Object, System.Numerics.Vector3 Place)>();
        foreach (var o in objects.Values)
        {
            if (o.Shape is not { SculptType: PrimShape.SculptTypeMesh } || o.AttachmentPoint is not null) continue;
            var place = o.Position;
            if (o.ParentLocalId != 0 && objects.TryGetValue(o.ParentLocalId, out var parent))
                place = parent.Position + System.Numerics.Vector3.Transform(o.Position, parent.Rotation);
            float d = (place - new System.Numerics.Vector3(x, y, z)).Length();
            if (d <= radius) found.Add((d, o, place));
        }
        if (names) AskNames(found.OrderBy(f => f.Distance).Take(max).Select(f => f.Object));
        var camera = prims.Camera;
        return new
        {
            camera = new { x = camera.X, y = -camera.Z, z = camera.Y },
            lod_factor = prims.LodFactor,
            objects = found.OrderBy(f => f.Distance).Take(max).Select(f =>
            {
                var report = prims.MeshReport(f.Object.Id.LocalId);
                var textures = new List<string>();
                if (f.Object.Textures is { } t)
                {
                    textures.Add(t.Default.TextureId.ToString());
                    foreach (var face in t.Overrides) if (face is { } ft) textures.Add(ft.TextureId.ToString());
                }
                return new
                {
                    local = f.Object.Id.LocalId, parent = f.Object.ParentLocalId, name = _objectNames.GetValueOrDefault(f.Object.FullId, ""), x = f.Place.X, y = f.Place.Y, z = f.Place.Z,
                    scale = new[] { f.Object.Scale.X, f.Object.Scale.Y, f.Object.Scale.Z }, asset = f.Object.Shape!.SculptId.ToString(),
                    drawn_lod = report?.Lod ?? -1, wanted_lod = report?.Wanted ?? -1, available_lods = report is { } r ? System.Convert.ToString(r.Available, 2).PadLeft(4, '0') : "",
                    distance = report?.Distance ?? -1f, radius = report?.Radius ?? -1f, textures = textures.Distinct().Take(8).Select(id => System.Guid.TryParse(id, out var tid) && prims.Residency is { } res ? new { id, state = res.StateOf(tid).ToString(), level = res.LevelOf(tid), pending = res.PendingLevelOf(tid), desired = res.DesiredLevelOf(tid), levels = res.LevelsOf(tid) } : new { id, state = "", level = -1, pending = -1, desired = 0, levels = 0 }).ToArray(),
                };
            }).ToArray(),
        };
    }
}

public partial class Main
{
    /// <summary>A development aid for the live check of what is missing: every object of the region the world holds, how many the layer presents, and for each
    /// that it does not, the reason (grouped, with a sample of positions).</summary>
    public object? PresentedAudit(int sample)
    {
        var world = Model?.Session?.World;
        var prims = _currentStack?.Prims ?? _presenter?.Prims;
        if (world is null || prims is null) return null;
        var objects = world.Snapshot().Objects.Values.Where(o => o.AttachmentPoint is null && o.Shape is not null).ToList();
        var missing = objects.Select(o => (Object: o, Why: prims.NotDrawnReason(o))).Where(x => x.Why is not null).ToList();
        return new
        {
            objects = objects.Count,
            presented = objects.Count - missing.Count,
            not_presented = missing.Count,
            draw_distance_m = prims.DrawDistanceMetres,
            by_reason = missing.GroupBy(m => m.Why).OrderByDescending(g => g.Count()).Select(g => new
            {
                reason = g.Key,
                count = g.Count(),
                sample = g.Take(sample).Select(m => new
                {
                    local = m.Object.Id.LocalId, parent = m.Object.ParentLocalId,
                    kind = m.Object.Shape!.SculptType == PrimShape.SculptTypeMesh ? "mesh" : m.Object.Shape.IsSculpt ? "sculpt" : "prim",
                    x = m.Object.Position.X, y = m.Object.Position.Y, z = m.Object.Position.Z,
                    scale = new[] { m.Object.Scale.X, m.Object.Scale.Y, m.Object.Scale.Z },
                    asset = m.Object.Shape.SculptType == PrimShape.SculptTypeMesh || m.Object.Shape.IsSculpt ? m.Object.Shape.SculptId.ToString("N")[..8] : "",
                }).ToArray(),
            }).ToArray(),
        };
    }
}

public partial class Main
{
    private readonly Dictionary<System.Guid, string> _objectNames = new();   // object names the region has told, for the development probe only

    /// <summary>Asks the region for the names of the objects (development probe); answers are read by <see cref="MeshProbe"/> on later calls.</summary>
    private void AskNames(IEnumerable<ObjectState> objects)
    {
        var actions = Model?.Session?.Actions;
        if (actions is null) return;
        foreach (var o in objects)
            if (!_objectNames.ContainsKey(o.FullId) && o.FullId != System.Guid.Empty)
                actions.Submit(new Viewer.Core.Actions.ObjectPropertiesIntent(o.Id.LocalId));
    }
}
