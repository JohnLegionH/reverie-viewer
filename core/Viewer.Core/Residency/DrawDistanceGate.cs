// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Residency;

/// <summary>
/// Which objects are built for the engine, by the draw distance. Behaviour taken from the official viewer, which builds and draws geometry only
/// for what is inside the camera's view volume, far plane included (the far clip is the draw distance), and has no margin: an object is in
/// or out by its bounds against the far plane alone. Here an object is beyond when its distance from the camera, less its radius, is more than
/// the draw distance. Objects beyond are not presented; ones that fall beyond are released, farthest first; ones that come within are
/// presented again, nearest first by the presenter's own order. The work is sliced by a clock, so a region of many objects never costs a frame.
/// </summary>
public sealed class DrawDistanceGate
{
    private struct Entry
    {
        public Vector3 Origin;
        public float Radius;
        public bool Beyond;
    }

    private readonly Dictionary<uint, Entry> _at = new();
    private readonly List<uint> _scan = new();
    private int _cursor;
    private Vector3 _scanCamera = new(float.NaN, 0, 0);
    private float _scanDistance = float.NaN;
    private readonly PriorityQueue<uint, float> _release = new();   // the farthest first (priority is the negated distance)
    private int _beyond;

    /// <summary>The draw distance in metres; infinite (nothing is held back) until set.</summary>
    public float Distance { get; set; } = float.PositiveInfinity;

    /// <summary>Objects known to the gate, and how many of them are held back as beyond the distance.</summary>
    public int Tracked => _at.Count;
    public int Beyond => _beyond;

    /// <summary>Whether the object is held back as beyond the distance.</summary>
    public bool IsHeldBeyond(uint id) => _at.TryGetValue(id, out var e) && e.Beyond;

    public bool IsBeyond(Vector3 origin, float radius, Vector3 camera) =>
        !float.IsPositiveInfinity(Distance) && Vector3.Distance(camera, origin) - radius > Distance;

    /// <summary>Notes where an object is. Returns true when it is within the distance and may be presented.</summary>
    public bool Hold(uint id, Vector3 origin, float radius, Vector3 camera)
    {
        bool beyond = IsBeyond(origin, radius, camera);
        Set(id, new Entry { Origin = origin, Radius = radius, Beyond = beyond });
        return !beyond;
    }

    /// <summary>Notes an object as held back: beyond the distance, not presented.</summary>
    public void HoldBeyond(uint id, Vector3 origin, float radius) => Set(id, new Entry { Origin = origin, Radius = radius, Beyond = true });

    private void Set(uint id, Entry e)
    {
        if (_at.TryGetValue(id, out var old) && old.Beyond) _beyond--;
        _at[id] = e;
        if (e.Beyond) _beyond++;
    }

    public void Forget(uint id)
    {
        if (_at.Remove(id, out var old) && old.Beyond) _beyond--;
    }

    /// <summary>Looks at held and presented objects until <paramref name="deadlineTicks"/> on <paramref name="clock"/> (100 ns ticks), round-robin
    /// from where the last call stopped; the clock is read every sixteen objects. An object that came within the distance is passed to
    /// <paramref name="wake"/>; one that has gone beyond is queued for <see cref="TryNextRelease"/>. Returns true while a cycle is unfinished.</summary>
    public bool Scan(Vector3 camera, Func<long> clock, long deadlineTicks, Action<uint> wake)
    {
        if (float.IsPositiveInfinity(Distance)) { _cursor = _scan.Count; return false; }
        if (_cursor >= _scan.Count)
        {
            // a new cycle only when the camera or the distance has changed since the last one began
            if (!float.IsNaN(_scanCamera.X) && _scanDistance == Distance && Vector3.DistanceSquared(camera, _scanCamera) < 1f)
                return false;
            _scan.Clear();
            _scan.AddRange(_at.Keys);
            _cursor = 0;
            (_scanCamera, _scanDistance) = (camera, Distance);
        }
        int sinceClock = 0;
        while (_cursor < _scan.Count)
        {
            if (++sinceClock >= 16)
            {
                sinceClock = 0;
                if (clock() >= deadlineTicks) return true;
            }
            uint id = _scan[_cursor++];
            if (!_at.TryGetValue(id, out var e)) continue;
            bool beyondNow = IsBeyond(e.Origin, e.Radius, camera);
            if (e.Beyond && !beyondNow)
            {
                e.Beyond = false;
                _at[id] = e;
                _beyond--;
                wake(id);
            }
            else if (!e.Beyond && beyondNow)
                _release.Enqueue(id, -Vector3.Distance(camera, e.Origin));
        }
        return false;
    }

    /// <summary>The farthest presented object that is beyond the distance now, or false when there is none. The caller releases it and then
    /// notes it with <see cref="HoldBeyond"/> (releasing forgets it).</summary>
    public bool TryNextRelease(Vector3 camera, out uint id, out Vector3 origin, out float radius)
    {
        while (_release.TryDequeue(out id, out _))
        {
            if (_at.TryGetValue(id, out var e) && !e.Beyond && IsBeyond(e.Origin, e.Radius, camera))
            {
                (origin, radius) = (e.Origin, e.Radius);
                return true;
            }
        }
        (id, origin, radius) = (0, default, 0);
        return false;
    }
}
