// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Linq;
using Viewer.Core.World;

namespace Viewer.Godot;

/// <summary>A development aid for the live check of objects that move or spin between the region's updates: each carried object as
/// the region last stated it and as it is drawn now.</summary>
public partial class Main
{
    public object? MovingProbe(int max)
    {
        var prims = _currentStack?.Prims ?? _presenter?.Prims;
        var world = _currentStack?.World ?? Model?.Session?.World;
        if (world is null || prims is null) return null;
        static float[] V(System.Numerics.Vector3 v) => [v.X, v.Y, v.Z];
        static float[] Q(System.Numerics.Quaternion q) => [q.X, q.Y, q.Z, q.W];
        var moving = prims.Moving;
        // counted from the world itself, so a moving object the layer does not carry shows as a difference
        var all = world.Snapshot().Objects.Values;
        int stated = all.Count(o => o.AttachmentPoint is null && world.Attachment(o.Id) is null
            && (o.Velocity != System.Numerics.Vector3.Zero || o.Acceleration != System.Numerics.Vector3.Zero || o.AngularVelocity != System.Numerics.Vector3.Zero));
        return new
        {
            t = prims.MovingClock(),
            time_dilation = world.TimeDilation,
            region_objects = all.Count(),
            stated_moving = stated,
            carried = moving.Count,
            objects = moving.Carried.OrderBy(l => l).Take(max).Select(local =>
            {
                var o = world.Object(new ObjectId(world.Epoch, local));
                moving.TryPlace(local, out var drawn, out var drawnRot);
                return new
                {
                    local,
                    root = o?.ParentLocalId == 0,
                    stated = o is null ? null : V(o.Position),
                    stated_rot = o is null ? null : Q(o.Rotation),
                    velocity = o is null ? null : V(o.Velocity),
                    acceleration = o is null ? null : V(o.Acceleration),
                    angular = o is null ? null : V(o.AngularVelocity),
                    drawn = V(drawn),
                    drawn_rot = Q(drawnRot),
                };
            }).ToArray(),
        };
    }
}
