// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// The objects of one region that move or spin between the region's updates, and where each is drawn now. An object is taken in
/// when an update gives it a velocity, an acceleration or an angular velocity, each frame carries it on
/// (<see cref="InterpolatedObject"/>, the official viewer's rules), and it is let go when an update leaves it at rest where the
/// region states it. Avatars and what they wear are not handled here. Main thread only.
/// </summary>
public sealed class ObjectInterpolator
{
    private sealed class Entry
    {
        public readonly InterpolatedObject Motion = new();
        public (Vector3 Position, Quaternion Rotation, Vector3 Velocity, Vector3 Acceleration, Vector3 Angular) Stated;
        public bool Root;
    }

    private readonly WorldState _world;
    private readonly Dictionary<uint, Entry> _moving = new();
    private readonly List<uint> _changed = new();

    public ObjectInterpolator(WorldState world) => _world = world;

    /// <summary>Seconds since the last packet of any kind came on the region's circuit; the carried motion tapers off only when this
    /// is over two seconds too.</summary>
    public double CircuitQuietSeconds { get; set; }

    /// <summary>Whether a region the viewer knows lies at a place outside this one (in this region's coordinates). Null: none does.</summary>
    public Func<Vector3, bool>? HasRegionAt { get; set; }

    /// <summary>The land height at a place in this region's coordinates. Null: not known.</summary>
    public Func<Vector3, float>? LandHeight { get; set; }

    /// <summary>Whether the user has the object selected (by local id); a selected object is not carried.</summary>
    public Func<uint, bool>? Selected { get; set; }

    public int Count => _moving.Count;

    public bool Carries(uint localId) => _moving.ContainsKey(localId);

    /// <summary>The local ids of the carried objects.</summary>
    public IReadOnlyCollection<uint> Carried => _moving.Keys;

    /// <summary>The object was added, changed (<paramref name="full"/>: a full update), moved (a terse update) or removed.</summary>
    public void Note(ObjectId id, bool full, double now)
    {
        var o = _world.Object(id);
        if (o is null || o.AttachmentPoint is not null || _world.Attachment(o.Id) is not null)
        {
            _moving.Remove(id.LocalId);
            return;
        }
        bool moving = o.Velocity != Vector3.Zero || o.Acceleration != Vector3.Zero || o.AngularVelocity != Vector3.Zero;
        if (!_moving.TryGetValue(id.LocalId, out var e))
        {
            if (!moving) return;
            _moving[id.LocalId] = e = new Entry();
        }
        else if (e.Stated == (o.Position, o.Rotation, o.Velocity, o.Acceleration, o.AngularVelocity) && e.Root == (o.ParentLocalId == 0))
        {
            return;   // not an update of its motion (its textures or shape changed): the object goes on from where it is
        }
        e.Stated = (o.Position, o.Rotation, o.Velocity, o.Acceleration, o.AngularVelocity);
        e.Root = o.ParentLocalId == 0;
        e.Motion.ScaleLength = o.Scale.Length();
        e.Motion.Update(e.Root ? o.Position - _world.OriginOffset : o.Position, o.Rotation, o.Velocity, o.Acceleration, o.AngularVelocity, now, full);
        if (!e.Motion.Moving && !e.Motion.Turned) _moving.Remove(id.LocalId);   // at rest where the region states it
    }

    public void Clear() => _moving.Clear();

    /// <summary>One frame: every carried object goes on to <paramref name="now"/>. Gives the objects whose place changed (the list is
    /// reused by the next call).</summary>
    public IReadOnlyList<uint> Frame(double now)
    {
        _changed.Clear();
        if (_moving.Count == 0) return _changed;
        var region = new RegionFrame
        {
            TimeDilation = _world.TimeDilation,
            CircuitQuietSeconds = CircuitQuietSeconds,
            Width = _world.Region?.SizeX ?? 256f,
            HasRegionAt = HasRegionAt,
            LandHeight = LandHeight,
        };
        var linked = region with { Bounded = false };   // a child's place is relative to its parent
        foreach (var (local, e) in _moving)
            if (Selected?.Invoke(local) != true && e.Motion.Idle(now, e.Root ? region : linked))   // a selected object is not carried (LLViewerObject::idleUpdate: !isSelected())
                _changed.Add(local);
        return _changed;
    }

    /// <summary>Where a carried object is now: a root's position in the world's coordinates, a child's relative to its parent.</summary>
    public bool TryPlace(uint localId, out Vector3 position, out Quaternion rotation)
    {
        if (!_moving.TryGetValue(localId, out var e))
        {
            (position, rotation) = (default, Quaternion.Identity);
            return false;
        }
        position = e.Root ? e.Motion.Position + _world.OriginOffset : e.Motion.Position;
        rotation = e.Motion.Rotation;
        return true;
    }
}
