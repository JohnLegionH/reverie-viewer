// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// Make before break for objects that change region (see <see cref="HandoffLedger"/>): what this layer draws of an object another region
/// takes over stays drawn until that region's layer draws it; what this layer is given by another region is reported once it is drawn.
/// </summary>
public sealed partial class PrimLayer
{
    private readonly Dictionary<uint, Guid> _held = new();       // local id here -> full id: drawn on, though the world no longer has it
    private readonly Dictionary<uint, Guid> _awaiting = new();   // local id here -> full id: given to this region, not drawn yet

    /// <summary>The ledger shared by every region's layers; null draws as before (an object leaves with the region that held it).</summary>
    public HandoffLedger? Handoffs { get; set; }

    /// <summary>Objects kept drawn after another region took them over, and objects given to this region and not drawn yet.</summary>
    public (int Held, int Awaiting) HandoffCounts => (_held.Count, _awaiting.Count);

    /// <summary>The full ids of the worn pieces of one wearer presented now (an instance made for a worn or a rigged piece; hidden with a hidden own avatar, as the count after a crossing counts), added to <paramref name="into"/>: a piece drawn by two regions during a handoff counts once.</summary>
    public void CollectWornDrawn(HashSet<Guid> into, Guid wearer)
    {
        foreach (var local in _worn.Keys.Concat(_rigged.Keys))
        {
            // the wearer as the world says it now; a piece the world no longer has (handed over) keeps what it said last
            if (_world.Object(new ObjectId(_world.Epoch, local)) is { WearerAgentId: { } now } o && now != Guid.Empty) _wearerAgent[local] = (now, o.FullId);
            if (_wearerAgent.TryGetValue(local, out var a) && a.Agent == wearer && _instances.ContainsKey(local)) into.Add(a.Full);   // drawn while its instance exists, whatever the world says by now
        }
    }

    // The linked parts of a worn piece (a worn item is usually many parts linked to one root): only the root names its wearer, and each part is drawn on the
    // avatar through it. A part is handed over like its root: kept drawn, and held in the ledger, until the region entered draws it. Kept apart from
    // _wearerAgent, which lists the roots a wearer's pieces hang from.
    private readonly Dictionary<uint, (Guid Agent, Guid Full)> _partWearer = new();

    private (Guid Agent, Guid Full) WearerOfPiece(uint local) =>
        _wearerAgent.TryGetValue(local, out var root) ? root : _partWearer.GetValueOrDefault(local);

    private readonly Dictionary<uint, (Guid Agent, Guid Full)> _wearerAgent = new();   // local id -> the agent wearing it, as the world said when it last sent it (kept for a piece handed over)

    /// <summary>A worn piece's wearer is learned when the world says it, which can be after the piece was added; remembered while the piece is
    /// drawn, so it is known when the world no longer has the piece (handed over).</summary>
    private void RememberWearers()
    {
        foreach (var local in _worn.Keys)
            if (!_wearerAgent.ContainsKey(local) && !_partWearer.ContainsKey(local)) Remember(local);
        foreach (var local in _rigged.Keys)
            if (!_wearerAgent.ContainsKey(local) && !_partWearer.ContainsKey(local)) Remember(local);
    }

    private void Remember(uint local)
    {
        if (_world.Object(new ObjectId(_world.Epoch, local)) is not { } o) return;
        if (o.WearerAgentId is { } a && a != Guid.Empty) { _wearerAgent[local] = (a, o.FullId); return; }
        // a linked part: the root it hangs from names the wearer
        if (o.ParentLocalId != 0 && _world.Object(new ObjectId(_world.Epoch, o.ParentLocalId)) is { WearerAgentId: { } rootWearer } && rootWearer != Guid.Empty)
            _partWearer[local] = (rootWearer, o.FullId);
    }

    /// <summary>The wearer of some pieces was handed to another region: the pieces are drawn on, and held in the ledger, until the region that has the
    /// wearer now draws each of them (the avatar is let go of after them). A piece the other region has not been given yet is not lost meanwhile.</summary>
    private void HoldPiecesOfHeldWearers(IAttachmentFrames frames)
    {
        foreach (var local in _worn.Keys.ToList())
            if (!_held.ContainsKey(local) && frames.WearerHeld(_worn[local].Wearer)) HoldPiece(local);
        foreach (var local in _rigged.Keys.ToList())
            if (!_held.ContainsKey(local) && frames.WearerHeld(_rigged[local].Wearer)) HoldPiece(local);
    }

    private void HoldPiece(uint local)
    {
        var full = WearerOfPiece(local).Full;
        if (!_instances.ContainsKey(local) || full == Guid.Empty) return;
        _held[local] = full;
        _awaiting.Remove(local);
        Handoffs!.Hold(full, piece: true);
    }

    /// <summary>
    /// A worn piece waiting to be presented again finds that its world no longer has it. The world lets go of an object the moment another region's world takes
    /// it over, on the network thread, while the removal that says so waits in this layer's queue; the deferred work can run in between. If another region's
    /// world holds the object (the identity hub knows it), the object has moved on, as the official viewer's one object list moves an object from region to
    /// region (<c>llviewerobjectlist.cpp</c> processObjectUpdate, <c>llviewerobject.cpp</c> setRegion): it is kept drawn and held, as the queued removal
    /// would have held it, until the region entered draws it. An object nobody holds was killed and is removed as before.
    /// </summary>
    private bool KeepIfMovedOn(uint local)
    {
        if (Handoffs is null || !_instances.ContainsKey(local)) return false;
        var wearer = WearerOfPiece(local);
        if (wearer.Full == Guid.Empty) return false;
        if (Handoffs.Exists?.Invoke(wearer.Full) != true) return false;
        if (WornFrameTrace.On) WornFrameTrace.Event(this, "kept-moved-on", local, wearer.Full);
        HoldPiece(local);
        return _held.ContainsKey(local);
    }

    /// <summary>
    /// A worn piece that is still in this layer's world, drawn, whose wearer is no longer in it: when another region's world holds the wearer, the wearer
    /// was handed over and the piece follows in its turn (see <see cref="HoldPiecesOfHeldWearers"/>, which holds it once the avatars have applied the
    /// removal). The deferred work can find it before that: the piece is kept drawn and held, not freed as unplaceable. A wearer nobody holds has left,
    /// and the piece is removed as before.
    /// </summary>
    private bool KeepIfWearerMovedOn(uint local)
    {
        if (Handoffs is null || !_instances.ContainsKey(local)) return false;
        var wearer = WearerOfPiece(local);
        if (wearer.Agent == Guid.Empty || wearer.Full == Guid.Empty) return false;
        if (Handoffs.Exists?.Invoke(wearer.Agent) != true) return false;
        if (WornFrameTrace.On) WornFrameTrace.Event(this, "kept-wearer-moved-on", local, wearer.Full);
        HoldPiece(local);
        return _held.ContainsKey(local);
    }

    private void NoteWearer(WorldChange c)
    {
        if (_world.Object(c.Id) is { WearerAgentId: { } a } o && a != Guid.Empty) _wearerAgent[c.Id.LocalId] = (a, o.FullId);
    }

    private Guid? FullIdOf(uint local) =>
        _held.TryGetValue(local, out var full) ? full : _world.Object(new ObjectId(_world.Epoch, local))?.FullId;

    /// <summary>A handoff removal: keep what is drawn (true), else the caller removes as usual.</summary>
    private bool HoldHandedOver(WorldChange c)
    {
        if (Handoffs is null || !c.Handoff || c.FullId == Guid.Empty || !_instances.ContainsKey(c.Id.LocalId))
        {
            if (WornFrameTrace.On && IsWornLocal(c.Id.LocalId))
                WornFrameTrace.Event(this, "not-held", c.Id.LocalId, c.FullId, $"handoff={c.Handoff} instance={_instances.ContainsKey(c.Id.LocalId)}");
            return false;
        }
        if (WornFrameTrace.On) WornFrameTrace.Event(this, "hold-handed-over", c.Id.LocalId, c.FullId);
        _held[c.Id.LocalId] = c.FullId;
        _awaiting.Remove(c.Id.LocalId);
        Handoffs.Hold(c.FullId, piece: true);
        return true;
    }

    /// <summary>The region sends an object this layer was holding for a handoff (it came back, under the same id): it is a live object here again.</summary>
    private void ForgetHeld(WorldChange c)
    {
        if (_held.Remove(c.Id.LocalId, out var full)) Handoffs?.Forget(full);
    }

    /// <summary>An object given to this region by another one: wait to report it drawn.</summary>
    private void ExpectHandedOver(WorldChange c)
    {
        if (Handoffs is null || !c.Handoff) return;
        if (_world.Object(c.Id) is not { } o) return;
        _held.Remove(c.Id.LocalId);
        _awaiting[c.Id.LocalId] = o.FullId;
        Handoffs.Expect(o.FullId);
    }

    /// <summary>Each frame: held objects the ledger lets go of are removed; objects given to this region that are drawn are reported.</summary>
    private void PollHandoffs()
    {
        if (Handoffs is not null) RememberWearers();
        if (Handoffs is not null && Attachments is { } frames) HoldPiecesOfHeldWearers(frames);
        if (Handoffs is null || (_held.Count == 0 && _awaiting.Count == 0)) return;
        foreach (var (local, full) in _held.ToList())
        {
            if (!_instances.ContainsKey(local)) { _held.Remove(local); Handoffs.TryRelease(full); if (WornFrameTrace.On) WornFrameTrace.Event(this, "held-without-instance", local, full); continue; }
            if (!Handoffs.TryRelease(full)) continue;
            _held.Remove(local);
            if (WornFrameTrace.On) WornFrameTrace.Event(this, "release-and-remove", local, full);
            Remove(local);
        }
        foreach (var (local, full) in _awaiting.ToList())
        {
            if (_world.Object(new ObjectId(_world.Epoch, local)) is null) { _awaiting.Remove(local); continue; }   // gone again
            if (!_instances.ContainsKey(local) || _dirty.Contains(local)) continue;
            if (_worn.TryGetValue(local, out var w) && !w.Shown) continue;   // worn, and its wearer is not drawn yet
            if (_rigged.TryGetValue(local, out var skinned) && !skinned.Shown) continue;   // a rigged piece, and its wearer is not drawn yet: the region that gave it keeps drawing it
            Handoffs.Entered(full);
            if (WornFrameTrace.On) WornFrameTrace.Event(this, "entered", local, full, _rigged.TryGetValue(local, out var rg) ? $"rigged shown={rg.Shown}" : _worn.TryGetValue(local, out var wn) ? $"plain shown={wn.Shown}" : "other");
            _awaiting.Remove(local);
        }
    }
}

public sealed partial class PrimLayer
{
    /// <summary>A short name for this layer in a trace (NV_WORN_FRAMES).</summary>
    public string Tag => (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this) & 0xFFFF).ToString("x");

    /// <summary>The region this layer's world is, for a trace.</summary>
    public string RegionName => _world.Region?.Name ?? "";

    /// <summary>Whether the local id is a worn piece here, or the world says the object hangs on an avatar (a trace filter).</summary>
    private bool IsWornLocal(uint local) =>
        _worn.ContainsKey(local) || _rigged.ContainsKey(local) || _wearerAgent.ContainsKey(local)
        || (_world.Object(new ObjectId(_world.Epoch, local)) is { WearerAgentId: { } a } && a != Guid.Empty);

    /// <summary>Every worn piece of <paramref name="wearer"/> this layer has now (NV_WORN_FRAMES).</summary>
    public void WornFrameStates(Guid wearer, List<WornFrameState> into)
    {
        foreach (var local in _worn.Keys.Concat(_rigged.Keys).Distinct())
        {
            if (!_wearerAgent.TryGetValue(local, out var a) || a.Agent != wearer) continue;
            bool rigged = _rigged.TryGetValue(local, out var r);
            bool shown = rigged ? r!.Shown : _worn.TryGetValue(local, out var w) && w.Shown;
            into.Add(new WornFrameState(a.Full, rigged, _instances.ContainsKey(local), shown, _held.ContainsKey(local), rigged ? r!.Last.Origin : null));
        }
    }

    /// <summary>The full ids of every worn object this layer shows now, the linked parts of a worn piece included, added to <paramref name="into"/> (a test and trace aid:
    /// <see cref="WornFrameStates"/> lists the roots a wearer's pieces hang from).</summary>
    public void WornObjectsShown(HashSet<Guid> into)
    {
        foreach (var local in _worn.Keys.Concat(_rigged.Keys).Distinct())
        {
            bool shown = _rigged.TryGetValue(local, out var r) ? r.Shown : _worn.TryGetValue(local, out var w) && w.Shown;
            if (shown && _instances.ContainsKey(local) && FullIdOf(local) is { } full && full != Guid.Empty) into.Add(full);
        }
    }

    /// <summary>The trace line for an object change that concerns a worn piece (NV_WORN_FRAMES).</summary>
    private void TraceChange(WorldChange c)
    {
        uint local = c.Id.LocalId;
        if (!IsWornLocal(local)) return;
        var full = c.FullId != Guid.Empty ? c.FullId : _world.Object(c.Id)?.FullId ?? _wearerAgent.GetValueOrDefault(local).Full;
        WornFrameTrace.Event(this, "apply-" + c.Kind, local, full, $"handoff={c.Handoff} instance={_instances.ContainsKey(local)} dirty={_dirty.Contains(local)}");
    }
}

/// <summary>A worn piece as placed this frame: the skeleton it is bound to (<see cref="IAttachmentFrames.TrySkeleton"/>) and the joint matrix it follows.</summary>
public readonly record struct WornBinding(uint Local, bool Rigged, int Point, object Layer, int SkeletonRow, System.Numerics.Matrix4x4 Joint);

public sealed partial class PrimLayer
{
    /// <summary>Every shown worn piece of <paramref name="wearer"/> (a full agent id) with the skeleton and joint it is bound to now.</summary>
    public void WornBindings(Guid wearer, List<WornBinding> into)
    {
        if (Attachments is not { } frames) return;
        foreach (var (local, w) in _worn)
            if (w.Shown && _wearerAgent.TryGetValue(local, out var a) && a.Agent == wearer
                && frames.TryPoint(w.Wearer, w.Point, out var joint) && frames.TrySkeleton(w.Wearer, out var layer, out var row))
                into.Add(new WornBinding(local, false, w.Point, layer, row, joint));
        foreach (var (local, r) in _rigged)
            if (r.Shown && _wearerAgent.TryGetValue(local, out var a) && a.Agent == wearer
                && frames.TryRigged(r.Wearer, out _, out var place) && frames.TrySkeleton(r.Wearer, out var layer, out var row))
                into.Add(new WornBinding(local, true, -1, layer, row, place));
    }

    /// <summary>One line per worn piece of <paramref name="wearer"/> that this layer has: whether it is drawn, shown, held after a handoff, its
    /// level of detail and the distance that level was chosen with (the camera to the piece, or for a rigged piece to the wearer's box). A worn piece is
    /// never held by the draw-distance gate (<c>gate=n/a</c>).</summary>
    public void WornReport(Guid wearer, List<string> into)
    {
        foreach (var local in _worn.Keys.Concat(_rigged.Keys).Distinct())
        {
            if (!_wearerAgent.TryGetValue(local, out var a) || a.Agent != wearer) continue;
            bool rigged = _rigged.TryGetValue(local, out var r);
            bool drawn = _instances.ContainsKey(local);
            bool shown = rigged ? r!.Shown : _worn.TryGetValue(local, out var w) && w.Shown;
            int lod = _meshObjects.TryGetValue(local, out var m) ? m.Lod : -1;
            float dist = _meshObjects.TryGetValue(local, out var mo) ? (rigged ? WearerLodDistance(r!.Wearer, mo.Origin) : MeshLodDistance(local, mo.Origin)) : -1;
            into.Add($"{a.Full.ToString("N")[..8]}:{(rigged ? "rigged" : "plain")}:{(drawn ? "drawn" : "absent")}:{(shown ? "shown" : "hidden")}:{(_held.ContainsKey(local) ? "held" : "live")}:lod={lod}:d={dist:0.#}:gate=n/a");
        }
    }
}

public sealed partial class PrimLayer
{
    /// <summary>Why the world's object <paramref name="o"/> is not presented now, or null when it is (an instance exists, alone or hidden under a merged cell's mesh).
    /// A development aid: every state an object can wait in or be left in is named.</summary>
    public string? NotDrawnReason(ObjectState o)
    {
        uint local = o.Id.LocalId;
        if (_instances.ContainsKey(local)) return null;
        if (_skipped.Contains(local)) return "left out: " + _skipReason.GetValueOrDefault(local, "unknown");
        if (_gate.IsHeldBeyond(local)) return $"beyond the draw distance ({_gate.Distance:0} m)";
        foreach (var (asset, set) in _waiting) if (set.Contains(local)) return "waiting for the mesh asset " + asset.ToString("N")[..8];
        foreach (var ((asset, lod), set) in _lodWaiting) if (set.ContainsKey(local)) return $"waiting for level {lod} of the mesh asset " + asset.ToString("N")[..8];
        foreach (var (tex, set) in _waitingTex) if (set.Contains(local)) return "waiting for the sculpt map " + tex.ToString("N")[..8];
        if (_dirty.Contains(local)) return "queued to be presented";
        if (o.ParentLocalId != 0 && _world.Object(new ObjectId(_world.Epoch, o.ParentLocalId)) is null) return "its parent is not in the region (yet)";
        return "no instance and no record of why";
    }
}
