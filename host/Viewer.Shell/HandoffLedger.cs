// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// Make before break for what is drawn when an object changes region. The official viewer keeps one drawable for an object and moves it from
/// one region to the next (llviewerobject.cpp setRegion, lldrawable.cpp movePartition), so the object is never missing from a frame. Here each
/// region draws with its own presenters, so the presenter of the region left keeps drawing a handed-over object (<see cref="Hold"/>) until the
/// presenter of the region entered says it draws it (<see cref="Entered"/>), or <see cref="TimeoutSeconds"/> have passed. Engine-free; main thread.
/// A worn piece is let go of as soon as its own copy is drawn; the avatar is let go of when its own copy is drawn and no piece is still held,
/// because a held piece is posed by the held avatar.
/// </summary>
public sealed class HandoffLedger
{
    private readonly Func<double> _now;
    private readonly Dictionary<Guid, (double Since, bool Piece)> _held = new();
    private readonly Dictionary<Guid, double> _entered = new();

    public HandoffLedger(Func<double> now)
    {
        _now = now;
        OwnMotion = new OwnMotionShare(now);
    }

    /// <summary>The user's own avatar's one motion, shared by the scenes of every region that is given this ledger (on the ledger's clock).</summary>
    public OwnMotionShare OwnMotion { get; }

    /// <summary>Objects (pieces and avatars) the region left is still drawing.</summary>
    public int Held => _held.Count;

    /// <summary>The region left keeps drawing <paramref name="fullId"/> (a worn piece or an object when <paramref name="piece"/>, else an avatar).</summary>
    public void Hold(Guid fullId, bool piece) => _held[fullId] = (_now(), piece);

    /// <summary>The region entered is given <paramref name="fullId"/>: an earlier mark that it draws it is out of date.</summary>
    public void Expect(Guid fullId) => _entered.Remove(fullId);

    /// <summary>Nothing is wanted drawn for <paramref name="fullId"/> by the region left any more (the id is back in a region that draws it, or gone).</summary>
    public void Forget(Guid fullId) { _held.Remove(fullId); _entered.Remove(fullId); }

    /// <summary>Whether the object still exists in any region (the identity hub knows it); null: not known, so it is taken to exist.</summary>
    public Func<Guid, bool>? Exists { get; set; }

    /// <summary>Where an agent's avatar is now, as the region that holds it says (null: not known). A held avatar is drawn there every frame.</summary>
    public Func<Guid, Viewer.Core.World.AvatarState?>? Locate { get; set; }

    /// <summary>The region entered draws <paramref name="fullId"/>.</summary>
    public void Entered(Guid fullId)
    {
        double now = _now();
        _entered[fullId] = now;
        if (_entered.Count > 64)   // marks nobody asked for (an object that was not held) do not pile up
            foreach (var (id, at) in _entered.ToList())
                if (now - at > 30) _entered.Remove(id);
    }

    /// <summary>Whether the region left lets go of <paramref name="fullId"/> now (true also for an id not held). When true it is no longer held.</summary>
    public bool TryRelease(Guid fullId)
    {
        if (!_held.TryGetValue(fullId, out var h)) return true;
        // no timer: the official viewer drops nothing on a clock, the object is one and the same. What ends a hold is the region entered drawing it, or
        // the object being gone from every region (killed, taken off, its wearer logged out)
        bool timedOut = Exists is { } exists && !exists(fullId);
        bool drawn = _entered.ContainsKey(fullId);
        // the avatar poses the pieces the region left still draws: it goes after them
        bool piecesLeft = !h.Piece && _held.Any(kv => kv.Value.Piece);
        if (!(timedOut || (drawn && !piecesLeft))) return false;
        _held.Remove(fullId);
        _entered.Remove(fullId);
        return true;
    }
}
