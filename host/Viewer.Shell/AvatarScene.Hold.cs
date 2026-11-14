// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;

namespace Viewer.Shell;

public sealed partial class AvatarScene
{
    private readonly HashSet<ObjectId> _held = new();

    /// <summary>When set, an avatar removed because another region took it over (<see cref="WorldChange.Handoff"/>) stays
    /// in the scene, drawn where it was, until <see cref="Release"/>.</summary>
    public bool HoldHandoffs { get; set; }

    /// <summary>The avatars kept after a handoff.</summary>
    public IReadOnlyCollection<AvatarEntry> Held => _held.Select(id => _entries[id]).ToList();

    /// <summary>Whether <paramref name="e"/> is kept after a handoff.</summary>
    public bool IsHeld(AvatarEntry e) => _held.Contains(e.Id);

    /// <summary>A held avatar is drawn where its agent is now: the state the region that has it now says. The state is taken as a message once, when it
    /// first differs from the one taken last: asked for in every frame, an unchanged state is not a new message, and the avatar goes on along its velocity
    /// as the region entered will draw it (the official viewer's one object is carried on every frame, <c>interpolateLinearMotion</c>; only a received
    /// update sets its position, <c>processUpdateMessage</c>). Taken in every frame it would be put back to the stated place each time the state is
    /// older than a late update may be, and jump forward in the frame the region entered takes the drawing over.</summary>
    public void Follow(AvatarEntry e, AvatarState now)
    {
        e.Size = now.Scale;
        if (OwnMotion is { } own && own.Shares(e.Motion)) return;   // the one object: only the regions' own updates move it (it is carried on meanwhile)
        if (e.Followed is { } last && last.Position == now.Position && last.Velocity == now.Velocity && last.Rotation == now.Rotation) return;
        e.Followed = now;
        InRegion(e);
        e.Motion.Update(now.Position, now.Velocity, now.Rotation, _now());
    }

    /// <summary>The user's own avatar's motion, shared by every region's scene of the session (null: this scene has its own). Set before the scene takes any
    /// avatar: the scene is then timed on the share's clock, so the scenes that share the motion keep one time.</summary>
    public OwnMotionShare? OwnMotion
    {
        get => _ownMotion;
        set
        {
            _ownMotion = value;
            if (value is not null) _now = value.Clock;
        }
    }

    private OwnMotionShare? _ownMotion;

    /// <summary>Whether the viewer knows a region at a place outside this one (in this region's coordinates): an avatar predicted off the edge is
    /// carried on for at most a second only where one lies.</summary>
    public Func<System.Numerics.Vector3, bool>? HasRegionAt { get; set; }

    /// <summary>The user's own avatar from the first of <paramref name="scenes"/> that has it (null scenes are passed over); null when none has.</summary>
    public static (AvatarScene Scene, AvatarEntry Self)? FirstSelf(params AvatarScene?[] scenes)
    {
        foreach (var scene in scenes)
            if (scene?.Self is { } self) return (scene, self);
        return null;
    }

    /// <summary>Lets go of a held avatar: it departs as any removed avatar does.</summary>
    public bool Release(ObjectId id)
    {
        if (!_held.Remove(id)) return false;
        if (_entries.Remove(id, out var gone)) Depart(gone);
        return true;
    }
}

/// <summary>
/// Each avatar, the user's own and others, as one moving object across the regions of a session. The official viewer has one object for an avatar and moves
/// it from region to region (<c>llviewerobjectlist.cpp</c> finds it by its full id; <c>llviewerobject.cpp</c>, <c>LLViewerObject::setRegion</c>): its position,
/// velocity and the time of its last update carry on, it is carried along its velocity across the border (<c>interpolateLinearMotion</c>), and the entered
/// region's first update is taken as any update is (<c>processUpdateMessage</c>), with the drawn place following it (<c>LLDrawable::updateXform</c>). Here
/// each region's scene has its own entry for the avatar, so the entries share one motion per avatar through this, on one clock. Engine-free; main thread.
/// </summary>
public sealed class OwnMotionShare
{
    private readonly Dictionary<Guid, (Viewer.Motion.IAvatarMotion Motion, int Users)> _motions = new();

    public OwnMotionShare(Func<double> clock) => Clock = clock;

    /// <summary>The clock every scene that shares the motions is timed on.</summary>
    public Func<double> Clock { get; }

    /// <summary>Entries that use a shared motion now (in any region's scene, held ones included), over every avatar.</summary>
    public int Users => _motions.Values.Sum(m => m.Users);

    /// <summary>The motion for a new entry of <paramref name="agent"/>'s avatar: the one another region's entry still uses (a crossing), or a new one when
    /// none does (the first arrival, or every earlier entry has gone).</summary>
    public Viewer.Motion.IAvatarMotion Join(Guid agent)
    {
        if (!_motions.TryGetValue(agent, out var m) || m.Users == 0) m = (new Viewer.Motion.ObjectMotion(), 0);
        _motions[agent] = (m.Motion, m.Users + 1);
        return m.Motion;
    }

    /// <summary>An entry that used <paramref name="motion"/> has gone.</summary>
    public void Leave(Viewer.Motion.IAvatarMotion motion)
    {
        foreach (var (agent, m) in _motions)
        {
            if (!ReferenceEquals(m.Motion, motion)) continue;
            if (m.Users <= 1) _motions.Remove(agent);
            else _motions[agent] = (m.Motion, m.Users - 1);
            return;
        }
    }

    /// <summary>Forgets every motion (a new presentation, a new login): the next entry starts a new one.</summary>
    public void Reset() => _motions.Clear();

    /// <summary>Whether <paramref name="motion"/> is a shared one.</summary>
    public bool Shares(Viewer.Motion.IAvatarMotion motion) => _motions.Values.Any(m => ReferenceEquals(m.Motion, motion));
}
