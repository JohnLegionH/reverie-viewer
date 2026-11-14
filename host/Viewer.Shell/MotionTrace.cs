// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>Something the viewer told the region about the own avatar: an AgentUpdate's control bits (<see cref="Controls"/>,
/// <see cref="Run"/> unset) or a run state (<see cref="Run"/> set: 1 run, 0 walk), at <see cref="At"/> seconds.</summary>
public readonly record struct MotionEvent(double At, uint Controls, int? Run);

/// <summary>One frame's record of one avatar: where it is drawn (its soles), what the region last stated for it, and where the camera was.
/// <see cref="At"/> is when the sample was written; <see cref="Placed"/> the time on the scene's clock the avatar was placed at.</summary>
public readonly record struct MotionSample(double At, uint LocalId, bool Self, Vector3 Drawn, Vector3 Stated, Vector3 Velocity, int Updates, Vector3 Camera = default, Guid Agent = default, double Placed = double.NaN);

/// <summary>
/// A bounded per-frame record of how every avatar is drawn against what the region states for it, for checking motion
/// live (development builds read it through the automation API). Off until <see cref="Start"/>; at most
/// <see cref="Capacity"/> samples are kept, later ones are counted and dropped. The stated position is turned into soles
/// the same way the scene places the drawn one. No engine type; main thread only.
/// </summary>
public sealed class MotionTrace
{
    public const int Capacity = 200_000;
    private readonly List<MotionSample> _samples = new();
    private readonly List<MotionEvent> _sent = new();

    public bool Recording { get; private set; }
    public int Dropped { get; private set; }

    /// <summary>Whether the presentation records every region's avatars (each sample naming its agent), not only the scene of the region the agent is in.</summary>
    public bool AllRegions { get; private set; }

    public void Start(bool allRegions = false)
    {
        _samples.Clear();
        _sent.Clear();
        Dropped = 0;
        AllRegions = allRegions;
        Recording = true;
    }

    public void Stop() => Recording = false;

    /// <summary>Records every placed avatar of <paramref name="scene"/> at <paramref name="now"/> (a no-op while off).</summary>
    public void Sample(AvatarScene scene, WorldState world, double now, Vector3 camera = default)
    {
        if (!Recording) return;
        foreach (var e in scene.Entries)
        {
            if (e.Seat != 0 || world.Avatar(e.Id) is not { } stated || !scene.Place(e, out var soles, out _)) continue;
            if (_samples.Count >= Capacity) { Dropped++; continue; }
            float half = (e.Size.Z > 0.1f ? e.Size.Z : AvatarScene.DefaultHeight) / 2;
            _samples.Add(new MotionSample(now, e.Id.LocalId, e.IsSelf, soles, stated.Position - new Vector3(0, 0, half), stated.Velocity, e.Motion.Updates, camera, e.AgentId, scene.Now));
        }
    }

    /// <summary>Notes an AgentUpdate sent to the region (a no-op while off).</summary>
    public void Sent(double now, uint controls)
    {
        if (Recording && _sent.Count < Capacity) _sent.Add(new MotionEvent(now, controls, null));
    }

    /// <summary>Notes a run state told to the region (a no-op while off).</summary>
    public void RunTold(double now, bool run)
    {
        if (Recording && _sent.Count < Capacity) _sent.Add(new MotionEvent(now, 0, run ? 1 : 0));
    }

    /// <summary>The updates and run states sent since the last take, removed from the trace.</summary>
    public MotionEvent[] TakeSent()
    {
        var all = _sent.ToArray();
        _sent.Clear();
        return all;
    }

    /// <summary>The samples recorded since the last take, removed from the trace.</summary>
    public MotionSample[] Take()
    {
        var all = _samples.ToArray();
        _samples.Clear();
        return all;
    }
}
