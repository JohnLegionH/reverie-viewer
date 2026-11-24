// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Core.World;

/// <summary>A consistent copy of the world state, keyed by the simulator's local id within <see cref="Epoch"/>.</summary>
public sealed record WorldSnapshot(
    int Epoch,
    RegionInfo? Region,
    IReadOnlyDictionary<uint, ObjectState> Objects,
    IReadOnlyDictionary<uint, AvatarState> Avatars);

/// <summary>
/// One session's world: objects, linksets, avatars, attachments and terrain for the region it is in (extracted
/// from the web gateway's AgentSession object pipeline, web-viewer bb91ba5, without its socket batching).
/// Rules:
///   - Epochs (ADR-005): each region visit is an epoch bound to one simulator. <see cref="BeginRegion"/> with a different
///     simulator bumps the epoch and drops everything; an input from any other simulator is dropped (StaleEpoch).
///   - Tombstones: a killed object (local id + full id) is remembered for the epoch; a full update for it that arrives
///     late or reordered never resurrects it, not even into a slot a new object has since taken under the same local
///     id. A kill for an object never seen blocks whatever arrives later under that local id in the epoch.
///   - Idempotent delivery: a duplicated full update changes nothing and raises nothing; a duplicated kill is a no-op.
///   - W-2 / WV-2b (gateway 56dcf3c): a root attachment that arrives before its wearer is kept without a point and
///     given its point when the wearer arrives; if the simulator already holds the wearer the session missed, the
///     wearer is adopted from it at once. A killed attachment or a region change drops the pending entry.
///   - HUD attachments never enter the world (Interest).
///   - Appearance receipt (passive): every avatar's AvatarAppearance is recorded as data (faces,
///     bakes by slot, visual parameters in the 3.1.6 wire order, versions); nothing here or anywhere in the core bakes or
///     sends one. In server mode a self appearance with version 0 is refused. Appearances are dropped with the epoch.
///   - Self motion (W-30): the sim-stated samples of our own avatar, stamped on arrival, gated on change;
///     the gate is reset with the epoch so the first sample in a new region always passes.
/// Per instance, never static (ADR-006). Inputs arrive on the adapter's network threads; everything is under one lock
/// and <see cref="Changed"/> is raised outside it.
/// </summary>
public sealed class WorldState : IDisposable
{
    private readonly object _lock = new();
    private readonly CoreLog _log;
    private readonly ObjectTable _objects = new();
    private readonly Dictionary<uint, AvatarState> _avatars = new();
    private readonly HashSet<(uint LocalId, Guid FullId)> _tombstones = new();   // killed objects of this epoch
    private readonly HashSet<uint> _killedUnseen = new();                         // kills that beat their object's update
    private readonly Dictionary<uint, Dictionary<uint, int>> _pendingAttachRoots = new();  // wearer local id -> prim -> point
    private readonly Dictionary<WorldDrop, int> _drops = new();
    private readonly Dictionary<uint, Guid> _provisional = new();   // objects handed over at a crossing under an id of ours, until the region sends them under its own: local id -> full id
    private uint _nextProvisional = FirstProvisionalLocalId;
    /// <summary>The first local id given to a handed-over object that the region has not sent yet: far above the ids a simulator assigns in sequence.</summary>
    private const uint FirstProvisionalLocalId = 0xFFFF0000u;
    private readonly Dictionary<uint, Guid> _movedAway = new();   // local ids whose object another region's world took over (the old region's kills for them find nothing)
    private IdentityHub? _hub;
    private readonly TerrainStore _terrain = new();
    private readonly Dictionary<Guid, AvatarAppearance> _appearances = new();
    private readonly Dictionary<Guid, List<AppearanceReceipt>> _appearanceLog = new();

    private readonly Dictionary<Guid, (VisualParamSet Params, bool ReleaseOnAppearance)> _previews = new();
    private readonly Dictionary<Guid, float> _hovers = new();   // the hover height each avatar's last AvatarAppearance stated (absent: none)
    private readonly Dictionary<Guid, IReadOnlyList<PlayingAnimation>> _animations = new();   // what each avatar plays, by agent
    private readonly Dictionary<uint, MaterialOverrides?> _pendingOverrides = new();   // overrides that beat their object
    private readonly Dictionary<uint, IReadOnlyList<RenderMaterialEntry>> _renderMaterials = new();   // objects' PBR ids, by local id
    private readonly Dictionary<Guid, LegacyMaterial> _legacyMaterials = new();        // the region's, by material id
    private readonly PositionGate _selfGate = new();
    private SelfMotionSample? _lastSelf;
    private AppearanceMode _appearanceMode;
    private (object? Sim, AvatarAppearanceData Data, Guid Self)? _heldSelfAppearance;   // the latest appearance for us refused in server mode (see ApplyAppearance)
    private object? _sim;
    private RegionInfo? _region;
    private int _epoch;
    private bool _disposed;

    /// <summary>
    /// Logs under the OWNER's source (the session's), so in a shared sink every world entry is attributed to its session
    /// (ADR-006: logs never cross accounts). The event names (world.*, appearance.*) say which component wrote them.
    /// </summary>
    public WorldState(CoreLog log) => _log = log;

    /// <summary>Joins this world to the session's identity hub: an update for an id another world of the session holds then moves that object here
    /// (see <see cref="IdentityHub"/>). Without a hub a world stands alone, as before.</summary>
    public void AttachIdentity(IdentityHub hub)
    {
        lock (_lock) _hub = hub;
    }

    /// <summary>Raised after each applied change, outside the lock, on the thread that delivered the input.</summary>
    public event Action<WorldChange>? Changed;

    /// <summary>Raised for each self sample the on-change gate passes, outside the lock, on the delivering (packet) thread.</summary>
    public event Action<SelfMotionSample>? SelfMoved;

    /// <summary>This session's own appearance as the simulator stores it (per session, kept across regions).</summary>
    public SelfAppearanceState Self { get; } = new();

    /// <summary>Where this world's region lies relative to the region the presentation is anchored on (zero for that one).
    /// The positions of free-standing objects and avatars are kept in the anchor region's frame, so every region's world is drawn
    /// where it belongs without moving anything when the agent crosses into a neighbour. Linked children and worn objects keep
    /// the position relative to their parent.</summary>
    public Vector3 OriginOffset { get { lock (_lock) return _origin; } }

    /// <summary>Sets <see cref="OriginOffset"/>; call before the region's events arrive (the offset applies to what comes after).</summary>
    public void SetOriginOffset(Vector3 offset)
    {
        lock (_lock) _origin = offset;
    }

    private Vector3 _origin;

    public int Epoch { get { lock (_lock) return _epoch; } }
    public RegionInfo? Region { get { lock (_lock) return _region; } }

    /// <summary>The region's time dilation (1 = real time), as its object updates state it.</summary>
    public float TimeDilation { get { lock (_lock) return _timeDilation; } }

    /// <summary>The dilation an object update of this region carried. Ignored from a simulator this epoch does not belong to.</summary>
    public void SetTimeDilation(object? sim, float dilation)
    {
        lock (_lock)
            if (!_disposed && sim is not null && ReferenceEquals(_sim, sim) && !float.IsNaN(dilation))
                _timeDilation = Math.Clamp(dilation, 0f, 1f);
    }

    private float _timeDilation = 1f;

    public int Dropped(WorldDrop why) { lock (_lock) return _drops.GetValueOrDefault(why); }

    /// <summary>Attachment roots still waiting for a wearer this epoch (W-2); an orphan if it stays non-zero at the end of a replay.</summary>
    public int PendingAttachmentRoots { get { lock (_lock) return _pendingAttachRoots.Values.Sum(r => r.Count); } }

    /// <summary>The avatars the region has told this session about (a copy; no prim is copied with them).</summary>
    public IReadOnlyList<AvatarState> AvatarList()
    {
        lock (_lock)
            return _avatars.Values.ToArray();
    }

    /// <summary>The avatar the region has told this session about for this agent id, or null.</summary>
    public AvatarState? AvatarOf(Guid agentId)
    {
        lock (_lock)
            foreach (var a in _avatars.Values)
                if (a.AgentId == agentId) return a;
        return null;
    }

    /// <summary>Whether the region has told this session about an avatar with this agent id.</summary>
    public bool HasAvatar(Guid agentId)
    {
        lock (_lock)
            foreach (var a in _avatars.Values)
                if (a.AgentId == agentId) return true;
        return false;
    }

    public WorldSnapshot Snapshot()
    {
        lock (_lock)
            return new(_epoch, _region, new Dictionary<uint, ObjectState>(_objects), new Dictionary<uint, AvatarState>(_avatars));
    }

    /// <summary>How many objects are one of these roots or a child of one, counted in place (no copy of the scene).</summary>
    public int CountLinksetObjects(IReadOnlySet<uint> roots)
    {
        int count = 0;
        lock (_lock)
            foreach (var o in _objects.Values)
                if (roots.Contains(o.Id.LocalId) || roots.Contains(o.ParentLocalId))
                    count++;
        return count;
    }

    /// <summary>The avatar of this agent, or null when it is not in the region.</summary>
    public AvatarState? FindAvatar(Guid agentId)
    {
        lock (_lock)
            foreach (var a in _avatars.Values)
                if (a.AgentId == agentId)
                    return a;
        return null;
    }

    public ObjectState? Object(ObjectId id)
    {
        lock (_lock)
            return id.Epoch == _epoch && _objects.TryGetValue(id.LocalId, out var o) ? o : null;
    }

    public AvatarState? Avatar(ObjectId id)
    {
        lock (_lock)
            return id.Epoch == _epoch && _avatars.TryGetValue(id.LocalId, out var a) ? a : null;
    }

    /// <summary>The prims linked to <paramref name="root"/> (its direct and indirect children), in local-id order; empty for an unknown root.</summary>
    public IReadOnlyList<ObjectState> Linkset(ObjectId root)
    {
        lock (_lock)
        {
            if (root.Epoch != _epoch || !_objects.ContainsKey(root.LocalId))
                return [];
            var result = new List<ObjectState>();
            var frontier = new Queue<uint>([root.LocalId]);
            var seen = new HashSet<uint> { root.LocalId };
            while (frontier.Count > 0)
            {
                uint p = frontier.Dequeue();
                foreach (var o in _objects.ChildrenOf(p))
                    if (seen.Add(o.Id.LocalId))
                    {
                        result.Add(o);
                        frontier.Enqueue(o.Id.LocalId);
                    }
            }
            result.Sort((a, b) => a.Id.LocalId.CompareTo(b.Id.LocalId));
            return result;
        }
    }

    /// <summary>The objects worn directly on an avatar (a link set's root; its children hang from the root), by the avatar's
    /// local id, in local-id order, each with its attachment point. Empty for an unknown avatar.</summary>
    public IReadOnlyList<ObjectState> AttachmentRoots(uint wearerLocalId)
    {
        lock (_lock)
        {
            if (!_avatars.ContainsKey(wearerLocalId))
                return [];
            return _objects.Values.Where(o => o.ParentLocalId == wearerLocalId && o.AttachmentPoint is not null).OrderBy(o => o.Id.LocalId).ToList();
        }
    }

    /// <summary>Where an object hangs on its wearer, or null when it is not an attachment, or its wearer is not known yet.
    /// A linked child is carried up through its root (the simulator states a child relative to its root, and the root
    /// relative to the attachment point).</summary>
    public AttachedPlacement? Attachment(ObjectId id)
    {
        lock (_lock)
        {
            if (id.Epoch != _epoch || !_objects.TryGetValue(id.LocalId, out var o))
                return null;
            var position = o.Position;
            var rotation = o.Rotation;
            for (int depth = 0; depth < MaxLinkDepth; depth++)
            {
                if (o.AttachmentPoint is int point)
                    return _avatars.TryGetValue(o.ParentLocalId, out var wearer)
                        ? new AttachedPlacement(o.Id, o.ParentLocalId, wearer.AgentId, point, position, rotation)
                        : null;
                if (!_objects.TryGetValue(o.ParentLocalId, out var parent))
                    return null;
                position = parent.Position + Vector3.Transform(position, parent.Rotation);
                rotation = parent.Rotation * rotation;
                o = parent;
            }
            return null;
        }
    }

    /// <summary>How many links up the chain from a child to its root are followed (a link set is two levels; the bound stops a loop).</summary>
    private const int MaxLinkDepth = 8;

    // ---------------- terrain ----------------

    public int TerrainPatchCount { get { lock (_lock) return _terrain.PatchCount; } }

    public bool TerrainComplete { get { lock (_lock) return _region is not null && _terrain.IsComplete(_region); } }

    public int TerrainExpected { get { lock (_lock) return _region is null ? 0 : TerrainStore.ExpectedPatches(_region, _terrain.PatchSize > 0 ? _terrain.PatchSize : 16); } }

    public bool TryGetPatch(int x, int y, out float[] heights)
    {
        lock (_lock)
            return _terrain.TryGet(x, y, out heights);
    }

    // ---------------- region / epoch ----------------

    /// <summary>
    /// The session is in <paramref name="sim"/>'s region. The same simulator (a login burst that came before SimConnected,
    /// or a repeated connect) keeps the epoch; a different one starts a new epoch and drops everything of the old.
    /// </summary>
    public void BeginRegion(object sim, RegionInfo region)
    {
        ArgumentNullException.ThrowIfNull(sim);
        bool reset;
        int epoch, objects, avatars;
        lock (_lock)
        {
            if (_disposed)
                return;
            reset = _sim is not null && !ReferenceEquals(_sim, sim);
            objects = _objects.Count;
            avatars = _avatars.Count;
            if (reset)
            {
                ClearLocked();
                _timeDilation = 1f;
            }
            _sim = sim;
            _region = region;
            epoch = _epoch;
        }
        if (reset)
        {
            _log.Info("world.region-reset", CoreLog.F("epoch", epoch), CoreLog.F("dropped-objects", objects), CoreLog.F("dropped-avatars", avatars));
            Raise(new(WorldChangeKind.RegionReset, new ObjectId(epoch, 0)));
        }
    }

    /// <summary>The region this world held is gone: everything is dropped and the change is announced (a region reset), but the world stays
    /// usable and takes another region with <see cref="BeginRegion"/>.</summary>
    public void Retire()
    {
        int epoch;
        lock (_lock)
        {
            if (_disposed)
                return;
            ClearLocked();
            _sim = null;
            epoch = _epoch;
        }
        _log.Info("world.retired", CoreLog.F("epoch", epoch));
        Raise(new(WorldChangeKind.RegionReset, new ObjectId(epoch, 0)));
    }

    /// <summary>The region's size changed (a var-region rule fired); objects and terrain are kept.</summary>
    public void UpdateRegion(RegionInfo region)
    {
        lock (_lock)
        {
            if (!_disposed && _region is not null)
                _region = region;
        }
    }

    /// <summary>
    /// The agent walked out of <paramref name="from"/> into this region. The agent is one avatar that moves, so its look goes with it
    /// (the official viewer never destroys the agent's own avatar object, it re-homes it: llviewerobjectlist.cpp, <c>processObjectUpdate</c>, and takes a
    /// new look only from an appearance message that carries data: llvoavatar.cpp, <c>processAvatarAppearance</c>). What this region already holds for
    /// the agent is left alone; only what it lacks is filled in. What the agent wears is the same objects: when this world already holds the agent's
    /// avatar they are handed over to it at once (under ids of ours until the region sends them), and otherwise they move here by their full ids when
    /// this region sends them (<see cref="IdentityHub"/>).
    /// </summary>
    public void CarryOwnLook(WorldState from, Guid self)
    {
        if (ReferenceEquals(from, this) || self == Guid.Empty)
            return;
        AvatarAppearance? look;
        List<ObjectState> worn;
        lock (from._lock)
        {
            from._appearances.TryGetValue(self, out look);
            worn = from.WornByLocked(self);
        }
        if (look is null && worn.Count == 0)
            return;
        var changes = new List<WorldChange>();
        var handed = new List<(uint OldLocal, Guid FullId)>();
        lock (_lock)
        {
            if (_disposed)
                return;
            var wearer = _avatars.Values.FirstOrDefault(a => a.AgentId == self);
            if (look is not null && !_appearances.ContainsKey(self))
            {
                _appearances[self] = _hovers.TryGetValue(self, out var hover) ? look with { Hover = hover } : look;
                changes.Add(new(WorldChangeKind.AppearanceChanged, new ObjectId(_epoch, wearer?.Id.LocalId ?? 0), self));
            }
            if (worn.Count > 0 && wearer is not null && !_objects.Values.Any(o => o.WearerAgentId == self))
            {
                var ids = worn.ToDictionary(o => o.Id.LocalId, _ => _nextProvisional++);
                foreach (var o in worn)
                {
                    uint local = ids[o.Id.LocalId];
                    uint parent = o.WearerAgentId == self && o.AttachmentPoint is not null ? wearer.Id.LocalId : ids.GetValueOrDefault(o.ParentLocalId, o.ParentLocalId);
                    var id = new ObjectId(_epoch, local);
                    _objects[local] = o with { Id = id, ParentLocalId = parent };
                    _provisional[local] = o.FullId;
                    _hub?.Hold(this, o.FullId, local, false);
                    handed.Add((o.Id.LocalId, o.FullId));
                    changes.Add(new(WorldChangeKind.ObjectAdded, id, Handoff: true));
                }
            }
        }
        foreach (var (oldLocal, fullId) in handed)   // they are here now: they leave the world they came from
            if (from.ReleaseMoved(fullId, oldLocal, false))
                _hub?.CountHandoff();
        foreach (var c in changes)
            Raise(c);
    }

    /// <summary>The objects <paramref name="wearer"/> wears: the attachment roots and the prims linked below them.</summary>
    private List<ObjectState> WornByLocked(Guid wearer)
    {
        var found = new List<ObjectState>();
        var seen = new HashSet<uint>();
        var frontier = new Queue<uint>(_objects.Values.Where(o => o.WearerAgentId == wearer && o.AttachmentPoint is not null).Select(o => o.Id.LocalId));
        foreach (var f in frontier)
            seen.Add(f);
        while (frontier.Count > 0)
        {
            uint p = frontier.Dequeue();
            found.Add(_objects[p]);
            foreach (var child in _objects.ChildrenOf(p))
                if (seen.Add(child.Id.LocalId))
                    frontier.Enqueue(child.Id.LocalId);
        }
        return found;
    }

    /// <summary>The object this world holds under another local id is sent under <paramref name="sentLocal"/>: the same object with a new local id (the
    /// official viewer resets the local id on the one object, <c>llviewerobjectlist.cpp</c> <c>processObjectUpdate</c>), which also covers an object handed
    /// over at a crossing under an id of ours. The old entry gives way to the new one, raised as a removal and an addition that both carry
    /// <see cref="WorldChange.Handoff"/>.</summary>
    private bool ReplaceLocalIdLocked(Guid fullId, uint sentLocal, List<WorldChange> changes)
    {
        if (_hub?.LocalIn(this, fullId) is not { } local || local == sentLocal || !_objects.TryGetValue(local, out var kept) || kept.FullId != fullId)
            return false;
        _objects.Remove(local);
        _provisional.Remove(local);
        _hub.Release(this, fullId, local);
        changes.Add(new(WorldChangeKind.ObjectRemoved, kept.Id, Handoff: true, FullId: fullId));
        return true;
    }

    /// <summary>
    /// Another region's world took over an object or avatar of this one (the region it moved to sent an update for the same full id): it leaves
    /// this world without a tombstone and is raised as removed with <see cref="WorldChange.Handoff"/>. False when this world no longer holds that id
    /// under that local id (it was killed in the meantime, or never was here). What hangs from a handed-over avatar stays until each piece is handed
    /// over in its turn or the old region kills it.
    /// </summary>
    internal bool ReleaseMoved(Guid fullId, uint localId, bool isAvatar)
    {
        WorldChange change;
        lock (_lock)
        {
            if (_disposed)
                return false;
            if (isAvatar)
            {
                if (!_avatars.TryGetValue(localId, out var a) || a.AgentId != fullId)
                    return false;
                _avatars.Remove(localId);
                change = new(WorldChangeKind.AvatarRemoved, a.Id, Handoff: true, FullId: a.AgentId);
            }
            else
            {
                if (!_objects.TryGetValue(localId, out var o) || o.FullId != fullId)
                    return false;
                _objects.Remove(localId);
                _provisional.Remove(localId);
                _pendingOverrides.Remove(localId);
                _renderMaterials.Remove(localId);
                foreach (var roots in _pendingAttachRoots.Values)
                    roots.Remove(localId);
                change = new(WorldChangeKind.ObjectRemoved, o.Id, Handoff: true, FullId: o.FullId);
            }
            _movedAway[localId] = fullId;
        }
        Raise(change);
        return true;
    }

    /// <summary>The agent's avatar and what it wears leave this world for good: no other region claimed them after the agent crossed out (see
    /// <c>ViewerSession.LeftAvatarGrace</c>). Raised as ordinary removals.</summary>
    internal void DropAgent(Guid agent)
    {
        var changes = new List<WorldChange>();
        lock (_lock)
        {
            if (_disposed || _avatars.Values.FirstOrDefault(a => a.AgentId == agent) is not { } avatar)
                return;
            RemoveWornLocked(avatar.Id.LocalId, [], changes);
            _avatars.Remove(avatar.Id.LocalId);
            _hub?.Release(this, agent, avatar.Id.LocalId);
            changes.Add(new(WorldChangeKind.AvatarRemoved, avatar.Id));
        }
        foreach (var c in changes)
            Raise(c);
    }

    /// <summary>Whether the object is worn by the agent this session is logged in as, or linked below something that is (a few levels up).</summary>
    private bool WornBySelfLocked(ObjectState o, Guid self)
    {
        if (self == Guid.Empty) return false;
        for (int depth = 0; depth < 8; depth++)
        {
            if (o.WearerAgentId == self) return true;
            if (o.ParentLocalId == 0 || !_objects.TryGetValue(o.ParentLocalId, out var parent)) return false;
            o = parent;
        }
        return false;
    }

    /// <summary>Tells the hub which other worlds held the ids this world was just given, has those worlds let go, and marks the matching changes as handoffs.</summary>
    private void CompleteHandoffs(List<WorldChange> changes, List<(IdentityHub.Holder From, Guid FullId, int ChangeAt)> moves)
    {
        foreach (var (from, fullId, at) in moves)
        {
            if (!from.World.ReleaseMoved(fullId, from.LocalId, from.IsAvatar))
                continue;
            changes[at] = changes[at] with { Handoff = true };
            _hub?.CountHandoff();
        }
    }

    private void ClearLocked()
    {
        _hub?.ReleaseAll(this);
        _movedAway.Clear();
        _provisional.Clear();
        _treeParts.Clear();
        _epoch++;
        _objects.Clear();
        _avatars.Clear();
        _tombstones.Clear();
        _killedUnseen.Clear();
        _pendingAttachRoots.Clear();
        _terrain.Clear();
        _appearances.Clear(); _hovers.Clear(); _previews.Clear();
        _animations.Clear();
        _pendingOverrides.Clear();
        _renderMaterials.Clear();
        _legacyMaterials.Clear();
        _selfGate.Reset();
        _lastSelf = null;
        _region = null;
    }

    /// <summary>
    /// Binds the first input's simulator to the epoch if none is bound yet (the login burst can precede SimConnected);
    /// false for an input from any other simulator.
    /// </summary>
    private bool AcceptLocked(object? sim)
    {
        if (_disposed)
            return false;
        if (sim is null)
            return Drop(WorldDrop.StaleEpoch);
        _sim ??= sim;
        return ReferenceEquals(_sim, sim) || Drop(WorldDrop.StaleEpoch);
    }

    private bool Drop(WorldDrop why)
    {
        _drops[why] = _drops.GetValueOrDefault(why) + 1;
        return false;
    }

    // ---------------- objects ----------------

    /// <summary>A full object update.</summary>
    /// <param name="eventIsAttachment">The adapter's own attachment flag for the update.</param>
    /// <param name="simParentAvatar">The simulator's avatar object for <c>prim.ParentLocalId</c>, if it holds one (the wearer).</param>
    public void ApplyObject(object? sim, PrimData prim, bool eventIsAttachment = false, AvatarData? simParentAvatar = null)
    {
        var changes = new List<WorldChange>(2);
        var moves = new List<(IdentityHub.Holder From, Guid FullId, int ChangeAt)>(2);
        lock (_lock)
        {
            if (!AcceptLocked(sim))
                return;
            int point = Viewer.Core.Avatars.AttachmentPlacement.PointId(prim.AttachmentPoint);   // the add flag says how it is worn, not where
            bool isAttachment = eventIsAttachment || point != 0;
            if (isAttachment && Interest.IsHudPoint(point))
            {
                Drop(WorldDrop.Hud);
                return;
            }
            if (_killedUnseen.Contains(prim.LocalId) || _tombstones.Contains((prim.LocalId, prim.FullId)))
            {
                Drop(WorldDrop.Tombstoned);
                return;
            }

            int? attachPoint = null;
            Guid? wearer = null;
            if (isAttachment && point != 0)
            {
                // The point is carried on EVERY prim of an attachment's link set; only the prim whose parent is an
                // avatar is the root attachment. A child parents to its root like any linked prim.
                bool parentIsPrim = _objects.ContainsKey(prim.ParentLocalId);
                if (!parentIsPrim && prim.ParentLocalId != 0)
                {
                    if (!_avatars.ContainsKey(prim.ParentLocalId) && simParentAvatar is not null && simParentAvatar.LocalId == prim.ParentLocalId)
                    {
                        // WV-2b: the simulator holds a wearer this session missed; adopt it now.
                        _log.Info("world.adopt-wearer", CoreLog.F("prim", prim.LocalId), CoreLog.F("point", point), CoreLog.F("wearer", simParentAvatar.LocalId));
                        ApplyAvatarLocked(simParentAvatar, changes, moves);
                    }
                    if (_avatars.TryGetValue(prim.ParentLocalId, out var w))
                    {
                        attachPoint = point;
                        wearer = w.AgentId;
                    }
                    else
                    {
                        // W-2: the wearer is not known yet; give the prim its point when it arrives.
                        if (!_pendingAttachRoots.TryGetValue(prim.ParentLocalId, out var roots))
                            _pendingAttachRoots[prim.ParentLocalId] = roots = new();
                        roots[prim.LocalId] = point;
                    }
                }
            }

            var id = new ObjectId(_epoch, prim.LocalId);
            bool replaced = ReplaceLocalIdLocked(prim.FullId, prim.LocalId, changes);
            _objects.TryGetValue(prim.LocalId, out var existing);
            var placed = prim.ParentLocalId == 0 && !isAttachment ? prim.Position + _origin : prim.Position;   // only a free-standing prim has a place of its own
            var next = new ObjectState(id, prim.FullId, prim.ParentLocalId, attachPoint, wearer, placed, prim.Rotation, prim.Scale,
                prim.Velocity, prim.AngularVelocity, prim.Acceleration, prim.TextureAnim, prim.Shape, prim.Colors, prim.Textures,
                FaceMaterials.WithRenderMaterials(prim.Materials, _renderMaterials.GetValueOrDefault(prim.LocalId)),
                // glTF overrides arrive outside the object's updates: kept across them, or taken from those that came first
                existing is not null ? existing.MaterialOverrides : _pendingOverrides.Remove(prim.LocalId, out var early) ? early : null,
                prim.Light, prim.Projector, prim.Interaction, prim.AttachItemId == Guid.Empty ? null : prim.AttachItemId, prim.Material, prim.Flexible);
            if (existing is not null && existing.AttachmentPoint is not null && attachPoint is null && existing.ParentLocalId == prim.ParentLocalId)
                next = next with { AttachmentPoint = existing.AttachmentPoint, WearerAgentId = existing.WearerAgentId };
            if (existing == next)
                return;   // duplicated delivery: nothing changed
            _objects[prim.LocalId] = next;
            _movedAway.Remove(prim.LocalId);   // the id is in use again here
            if (_hub is { } hub)
            {
                if (existing is not null && existing.FullId != prim.FullId)
                    hub.Release(this, existing.FullId, prim.LocalId);   // a new object took a local id the old one gave up
                if (hub.Hold(this, prim.FullId, prim.LocalId, false, own: WornBySelfLocked(next, hub.SelfAgent)) is { } from)
                    moves.Add((from, prim.FullId, changes.Count));
            }
            _pendingAttachRoots.Remove(prim.LocalId);   // a child that came before this root waited under the root's id: the root is a prim, there is no wearer to wait for
            changes.Add(new(existing is null ? WorldChangeKind.ObjectAdded : WorldChangeKind.ObjectChanged, id, Handoff: replaced));
        }
        CompleteHandoffs(changes, moves);
        foreach (var c in changes)
            Raise(c);
    }

    /// <summary>A tree: drawn as one mesh built from its species (<see cref="TreeBuilder"/>), the root under the tree's own local id.</summary>
    public void ApplyTree(object? sim, TreeData tree)
    {
        var changes = new List<WorldChange>();

        lock (_lock)
        {
            if (!AcceptLocked(sim))
                return;
            if (_killedUnseen.Contains(tree.LocalId) || _tombstones.Contains((tree.LocalId, tree.FullId)))
            {
                Drop(WorldDrop.Tombstoned);
                return;
            }
            if (_treeParts.Remove(tree.LocalId, out var old))   // an update builds the tree again: the parts of the last build go
                foreach (uint part in old)
                    if (_objects.Remove(part, out var gone))
                    {
                        _hub?.Release(this, gone.FullId, part);
                        changes.Add(new(WorldChangeKind.ObjectRemoved, gone.Id));
                    }


        }
        foreach (var c in changes)
            Raise(c);
        ApplyLegacyMaterials(sim, [TreeMesh.LeafMaterial]);   // the tree's one material (a material already known changes nothing)
        var prims = TreeBuilder.Build(tree);
        foreach (var prim in prims)
            ApplyObject(sim, prim);
        lock (_lock)
            if (!_disposed)
                _treeParts[tree.LocalId] = prims.Skip(1).Select(p => p.LocalId).ToList();
    }

    /// <summary>The prims built for each tree, by the tree's local id (not counting the root): a kill of the tree takes them too.</summary>
    private readonly Dictionary<uint, List<uint>> _treeParts = new();


    /// <summary>A terse update: motion only. Ignored for an object never received in full (as the gateway).</summary>
    public void ApplyMove(object? sim, uint localId, bool isAvatar, Vector3 position, Quaternion rotation, Vector3 velocity,
        Vector3 acceleration = default, Vector3 angularVelocity = default, Vector4 footPlane = default)
    {
        WorldChange change;
        lock (_lock)
        {
            if (!AcceptLocked(sim))
                return;
            var id = new ObjectId(_epoch, localId);
            if (isAvatar)
            {
                if (!_avatars.TryGetValue(localId, out var a))
                {
                    Drop(WorldDrop.UnknownObject);
                    return;
                }
                var na = a with { Position = a.ParentLocalId == 0 ? position + _origin : position, Rotation = rotation, Velocity = velocity, AngularVelocity = angularVelocity, FootPlane = footPlane };
                // the same state again is still an update: an avatar carried on since the last one is put back where the region says
                // (llviewerobject.cpp processUpdateMessage sets the place whenever it differs from the carried one)
                _avatars[localId] = na;
                change = new(WorldChangeKind.AvatarMoved, id);
            }
            else
            {
                if (!_objects.TryGetValue(localId, out var o))
                {
                    Drop(WorldDrop.UnknownObject);
                    return;
                }
                var no = o with { Position = o.ParentLocalId == 0 && o.AttachmentPoint is null ? position + _origin : position, Rotation = rotation, Velocity = velocity, Acceleration = acceleration, AngularVelocity = angularVelocity };
                if (no == o)
                    return;
                _objects[localId] = no;
                change = new(WorldChangeKind.ObjectMoved, id);
            }
        }
        Raise(change);
    }

    /// <summary>Objects (prims or avatars) removed by the simulator. Each id is tombstoned for the epoch.</summary>
    public void Kill(object? sim, IReadOnlyList<uint> localIds)
    {
        var changes = new List<WorldChange>();
        lock (_lock)
        {
            if (!AcceptLocked(sim))
                return;
            foreach (var id in localIds)
            {
                // The object under this local id moved to another region's world: the official viewer looks the object up by region and local id
                // (process_kill_object, llviewermessage.cpp), finds none, and skips the kill.
                if (_movedAway.ContainsKey(id))
                    continue;
                // It never kills its own avatar (the same function).
                if (_hub is { SelfAgent: var self } && self != Guid.Empty && _avatars.TryGetValue(id, out var own) && own.AgentId == self)
                    continue;
                // W-2: a killed avatar takes its pending roots with it; a killed prim leaves any pending set.
                bool wasPrim = _objects.ContainsKey(id);
                var waiting = _pendingAttachRoots.Remove(id, out var pending) ? pending.Keys.ToList() : [];
                _pendingOverrides.Remove(id);
                _renderMaterials.Remove(id);
                foreach (var roots in _pendingAttachRoots.Values)
                    roots.Remove(id);
                if (_treeParts.Remove(id, out var treeParts))
                    foreach (uint part in treeParts)
                        if (_objects.Remove(part, out var partGone))
                        {
                            _hub?.Release(this, partGone.FullId, part);
                            changes.Add(new(WorldChangeKind.ObjectRemoved, partGone.Id));
                        }
                if (_objects.Remove(id, out var o))
                {
                    _hub?.Release(this, o.FullId, id);
                    _provisional.Remove(id);
                    _tombstones.Add((id, o.FullId));
                    changes.Add(new(WorldChangeKind.ObjectRemoved, o.Id));
                }
                else
                {
                    // an avatar that leaves takes what it wears (and the link sets of what it wears) with it, whether or not the
                    // simulator also kills those objects; they are not tombstoned, so they are drawn again if the avatar returns
                    // with them. Roots still waiting for an avatar that never arrived go too: nothing can place them.
                    if (!wasPrim)
                        RemoveWornLocked(id, waiting, changes);
                    if (_avatars.Remove(id, out var a))
                    {
                        _hub?.Release(this, a.AgentId, id);
                        changes.Add(new(WorldChangeKind.AvatarRemoved, a.Id));
                    }
                    else if (!wasPrim)
                        _killedUnseen.Add(id);   // a kill that beat its object's update: whatever arrives later stays dead
                }
            }
        }
        foreach (var c in changes)
            Raise(c);
    }

    /// <summary>Removes the objects worn by <paramref name="wearer"/> (those whose parent it is, and the prims linked below
    /// them), and the roots that waited for it, raising ObjectRemoved for each.</summary>
    private void RemoveWornLocked(uint wearer, List<uint> waiting, List<WorldChange> changes)
    {
        var frontier = new Queue<uint>(_objects.Values.Where(o => o.ParentLocalId == wearer).Select(o => o.Id.LocalId).Concat(waiting.Where(_objects.ContainsKey)));
        var seen = new HashSet<uint>(frontier);
        while (frontier.Count > 0)
        {
            uint p = frontier.Dequeue();
            foreach (var child in _objects.Values)
                if (child.ParentLocalId == p && seen.Add(child.Id.LocalId))
                    frontier.Enqueue(child.Id.LocalId);
        }
        foreach (uint local in seen.Order())
        {
            if (!_objects.Remove(local, out var gone))
                continue;
            _hub?.Release(this, gone.FullId, local);
            _provisional.Remove(local);
            _pendingOverrides.Remove(local);
            _renderMaterials.Remove(local);
            foreach (var roots in _pendingAttachRoots.Values)
                roots.Remove(local);
            changes.Add(new(WorldChangeKind.ObjectRemoved, gone.Id));
        }
    }

    // ---------------- materials ----------------

    /// <summary>
    /// The region's glTF overrides for one object: they replace the object's previous ones (no faces clears them). For an
    /// object not received yet they are kept until it arrives (the message can come first); a killed object takes them with it.
    /// </summary>
    public void ApplyMaterialOverrides(object? sim, uint localId, IReadOnlyList<FaceMaterialOverride> faces)
    {
        WorldChange? change = null;
        lock (_lock)
        {
            if (!AcceptLocked(sim))
                return;
            var next = faces.Count == 0 ? null : new MaterialOverrides(faces);
            if (_objects.TryGetValue(localId, out var o))
            {
                if (Equals(o.MaterialOverrides, next))
                    return;   // duplicated delivery
                _objects[localId] = o with { MaterialOverrides = next };
                change = new(WorldChangeKind.ObjectChanged, o.Id);
            }
            else if (!_killedUnseen.Contains(localId))
                _pendingOverrides[localId] = next;
        }
        if (change is { } c)
            Raise(c);
    }

    /// <summary>
    /// An object's PBR material ids from its render material parameter (null: removed, every face has none). Kept by local
    /// id, so they apply whether they come before or after the object's update and survive its later updates.
    /// </summary>
    public void ApplyRenderMaterials(object? sim, uint localId, IReadOnlyList<RenderMaterialEntry>? faces)
    {
        WorldChange? change = null;
        lock (_lock)
        {
            if (!AcceptLocked(sim) || _killedUnseen.Contains(localId))
                return;
            var entries = faces ?? [];
            _renderMaterials[localId] = entries;
            if (_objects.TryGetValue(localId, out var o))
            {
                var next = FaceMaterials.WithRenderMaterials(o.Materials, entries);
                if (Equals(next, o.Materials))
                    return;
                _objects[localId] = o with { Materials = next };
                change = new(WorldChangeKind.ObjectChanged, o.Id);
            }
        }
        if (change is { } c)
            Raise(c);
    }

    /// <summary>Legacy materials the region served; a material already known with the same values changes nothing.</summary>
    public void ApplyLegacyMaterials(object? sim, IReadOnlyList<LegacyMaterial> materials)
    {
        bool changed = false;
        int epoch;
        lock (_lock)
        {
            if (!AcceptLocked(sim))
                return;
            foreach (var m in materials)
                if (m.Id != Guid.Empty && (!_legacyMaterials.TryGetValue(m.Id, out var had) || had != m))
                {
                    _legacyMaterials[m.Id] = m;
                    changed = true;
                }
            epoch = _epoch;
        }
        if (changed)
            Raise(new(WorldChangeKind.LegacyMaterialsReceived, new ObjectId(epoch, 0)));
    }

    /// <summary>A legacy material of this region by its id, or null when the region has not served it (yet).</summary>
    public LegacyMaterial? LegacyMaterial(Guid id)
    {
        lock (_lock)
            return _legacyMaterials.GetValueOrDefault(id);
    }

    /// <summary>Every legacy material this region has served, by id.</summary>
    public IReadOnlyDictionary<Guid, LegacyMaterial> LegacyMaterials()
    {
        lock (_lock)
            return new Dictionary<Guid, LegacyMaterial>(_legacyMaterials);
    }

    // ---------------- avatars ----------------

    /// <summary>A full avatar update. Gives any attachment roots waiting for this wearer their point.</summary>
    public void ApplyAvatar(object? sim, AvatarData avatar)
    {
        var changes = new List<WorldChange>();
        var moves = new List<(IdentityHub.Holder From, Guid FullId, int ChangeAt)>(1);
        lock (_lock)
        {
            if (!AcceptLocked(sim))
                return;
            ApplyAvatarLocked(avatar, changes, moves);
        }
        CompleteHandoffs(changes, moves);
        foreach (var c in changes)
            Raise(c);
    }

    private void ApplyAvatarLocked(AvatarData avatar, List<WorldChange> changes, List<(IdentityHub.Holder From, Guid FullId, int ChangeAt)> moves)
    {
        var id = new ObjectId(_epoch, avatar.LocalId);
        _avatars.TryGetValue(avatar.LocalId, out var existing);
        var next = new AvatarState(id, avatar.AgentId, avatar.Name, avatar.ParentLocalId == 0 ? avatar.Position + _origin : avatar.Position, avatar.Rotation, avatar.Scale, avatar.Velocity,
            existing?.AngularVelocity ?? Vector3.Zero, avatar.ParentLocalId, avatar.Title ?? "", avatar.FootPlane);
        if (existing != next)
        {
            _avatars[avatar.LocalId] = next;
            _movedAway.Remove(avatar.LocalId);   // the id is in use again here
            if (_hub is { } hub)
            {
                if (existing is not null && existing.AgentId != avatar.AgentId)
                    hub.Release(this, existing.AgentId, avatar.LocalId);
                if (hub.Hold(this, avatar.AgentId, avatar.LocalId, true, own: avatar.AgentId == hub.SelfAgent && avatar.AgentId != Guid.Empty) is { } from)
                    moves.Add((from, avatar.AgentId, changes.Count));
            }
            changes.Add(new(existing is null ? WorldChangeKind.AvatarAdded : WorldChangeKind.AvatarChanged, id));
        }
        else
        {
            // the same state again is still an update of where the avatar is (see ApplyMove)
            changes.Add(new(WorldChangeKind.AvatarMoved, id));
        }
        if (!_pendingAttachRoots.Remove(avatar.LocalId, out var roots))
            return;
        foreach (var (primId, point) in roots)
        {
            if (!_objects.TryGetValue(primId, out var o) || o.ParentLocalId != avatar.LocalId)
                continue;
            _objects[primId] = o with { AttachmentPoint = point, WearerAgentId = avatar.AgentId };
            changes.Add(new(WorldChangeKind.ObjectChanged, o.Id));
        }
    }

    // ---------------- appearance (receipt only) ----------------

    public AppearanceMode AppearanceMode { get { lock (_lock) return _appearanceMode; } }

    /// <summary>
    /// The region's appearance mode, from its RegionProtocols bit 0 (chosen at every SimConnected, as the gateway does).
    /// Choosing it changes only what the core records; in neither mode does anything bake or send.
    /// </summary>
    public void ChooseAppearanceMode(string regionName, bool serverBaked)
    {
        var next = serverBaked ? AppearanceMode.Server : AppearanceMode.Client;
        AppearanceMode previous;
        (object? Sim, AvatarAppearanceData Data, Guid Self)? held;
        lock (_lock)
        {
            if (_disposed)
                return;
            previous = _appearanceMode;
            _appearanceMode = next;
            held = _heldSelfAppearance;
            _heldSelfAppearance = null;
        }
        if (previous == next)
            _log.Info("appearance.mode", CoreLog.F("region", regionName), CoreLog.F("mode", next), CoreLog.F("staying", true));
        else
            _log.Info("appearance.mode", CoreLog.F("region", regionName), CoreLog.F("bit0", serverBaked ? "set" : "clear"),
                CoreLog.F("mode", next), CoreLog.F("was", previous));
        // the appearance for us refused under the region left's server mode is this region's ordinary shape when it does not bake
        if (next == AppearanceMode.Client && held is { } h)
        {
            _log.Info("appearance.self-held-applied", CoreLog.F("region", regionName));
            ApplyAppearance(h.Sim, h.Data, h.Self);
        }
    }

    /// <summary>One AvatarAppearance as it arrived: when, its versions, and the texture id on each bake face (the raw id, including any placeholder).</summary>
    public sealed record AppearanceReceipt(DateTime Utc, int AppearanceVersion, int CofVersion, IReadOnlyDictionary<string, Guid> RawBakes);

    private void LogReceiptLocked(AvatarAppearanceData data)
    {
        if (!_appearanceLog.TryGetValue(data.AgentId, out var list)) _appearanceLog[data.AgentId] = list = new();
        var raw = new Dictionary<string, Guid>();
        for (int bt = 0; bt < AppearanceSlots.Count; bt++)
        {
            int face = AppearanceSlots.FaceOf(bt);
            if (face >= 0 && face < data.FaceTextures.Length) raw[AppearanceSlots.SlotOf(bt)!] = data.FaceTextures[face];
        }
        list.Add(new AppearanceReceipt(DateTime.UtcNow, data.AppearanceVersion, data.CofVersion, raw));
        if (list.Count > 32) list.RemoveAt(0);
    }

    /// <summary>The appearances received for an avatar, oldest first (the last 32), for the development probe.</summary>
    public IReadOnlyList<AppearanceReceipt> AppearanceReceipts(Guid agentId)
    {
        lock (_lock)
            return _appearanceLog.TryGetValue(agentId, out var l) ? l.ToList() : [];
    }

    public AvatarAppearance? Appearance(Guid agentId)
    {
        lock (_lock)
            return _appearances.GetValueOrDefault(agentId);
    }

    public IReadOnlyList<AvatarAppearance> Appearances()
    {
        lock (_lock)
            return _appearances.Values.ToList();
    }

    /// <summary>
    /// A local look for an avatar's shape while an edit is open: the visual parameters it is drawn with, in place of the ones the
    /// simulator sent. Nothing is sent and the received look is not changed (<see cref="Appearance"/> still answers with it).
    /// <paramref name="preview"/> null ends it. With <paramref name="releaseOnNextAppearance"/> the preview also ends when the
    /// simulator's next appearance for the avatar arrives (a saved edit stays on screen until the rebuilt look replaces it).
    /// </summary>
    public void SetSelfPreview(Guid agentId, VisualParamSet? preview, bool releaseOnNextAppearance = false)
    {
        WorldChange change;
        lock (_lock)
        {
            if (preview is null)
            {
                if (!_previews.Remove(agentId))
                    return;
            }
            else
                _previews[agentId] = (preview, releaseOnNextAppearance);
            uint local = _avatars.Values.FirstOrDefault(a => a.AgentId == agentId)?.Id.LocalId ?? 0;
            change = new(WorldChangeKind.AppearanceChanged, new ObjectId(_epoch, local), agentId);
        }
        Raise(change);
    }

    /// <summary>The visual parameters to draw an avatar with: the local preview when one is set, else what the simulator sent (null when neither).</summary>
    public VisualParamSet? DrawnVisualParams(Guid agentId)
    {
        lock (_lock)
            return _previews.TryGetValue(agentId, out var p) ? p.Params : _appearances.GetValueOrDefault(agentId)?.VisualParams;
    }

    /// <summary>An AvatarAppearance from the simulator. <paramref name="selfAgentId"/> is this session's own agent.</summary>
    public void ApplyAppearance(object? sim, AvatarAppearanceData data, Guid selfAgentId)
    {
        WorldChange change;
        lock (_lock)
        {
            if (!AcceptLocked(sim))
                return;
            bool isSelf = data.AgentId == selfAgentId && selfAgentId != Guid.Empty;
            if (isSelf)
                Self.RecordSimParams(data.VisualParams.Raw);   // the sim's stored blob for us, even from a refused message
            if (isSelf)
                _heldSelfAppearance = null;   // a newer appearance for us: an older one held below is no longer the one to use
            if (isSelf && AppearanceSlots.RejectsSelfAppearance(_appearanceMode, data.AppearanceVersion))
            {
                // On a teleport the new region's first appearance for us can come before its handshake, while the mode is still the region
                // left's. It is kept, and used if the handshake says this region does not bake avatars (ChooseAppearanceMode).
                _heldSelfAppearance = (sim, data, selfAgentId);
                Drop(WorldDrop.RejectedSelfAppearance);
                _log.Info("appearance.self-rejected", CoreLog.F("reason", "server mode and appearanceVersion 0: no AppearanceData block, not the region's bake"));   // the grid's ordinary first message, not a fault
                return;
            }
            LogReceiptLocked(data);
            if (isSelf)
                _log.Info("appearance.self-received", CoreLog.F("version", data.AppearanceVersion), CoreLog.F("cof", data.CofVersion), CoreLog.F("params", data.VisualParams.Count),
                    CoreLog.F("params_hash", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.VisualParams.Raw.ToArray()))[..12]),
                    CoreLog.F("sex", data.VisualParams.Sex), CoreLog.F("height_byte", data.VisualParams.TryGet(33, out var heightByte) ? heightByte.ToString() : "none"));   // which set is drawn, without its values
            var bakes = AppearanceSlots.BakesOf(data.FaceTextures);
            if (!isSelf && _appearances.TryGetValue(data.AgentId, out var before))
            {
                // a body bake this message leaves undefined does not overwrite a valid one (llvoavatar.cpp:9801-9817)
                var kept = new Dictionary<string, Guid>(bakes);
                foreach (var slot in AppearanceSlots.KeptSlots)
                    if (!kept.ContainsKey(slot) && before.Bakes.TryGetValue(slot, out var last)) kept[slot] = last;
                bakes = kept;
            }
            var next = new AvatarAppearance(data.AgentId, isSelf, bakes, data.FaceTextures, data.VisualParams, data.AppearanceVersion, data.CofVersion, _hovers.TryGetValue(data.AgentId, out var hov) ? hov : null);
            if (_appearances.TryGetValue(data.AgentId, out var existing) && existing.SameAs(next))
                return;   // duplicated delivery
            _appearances[data.AgentId] = next;
            if (_previews.TryGetValue(data.AgentId, out var shown) && shown.ReleaseOnAppearance)
                _previews.Remove(data.AgentId);   // a saved edit stays on screen until the rebuilt look arrives
            if (isSelf && _appearanceMode == AppearanceMode.Server)
                Self.RecordServerBakes(bakes);
            uint local = _avatars.Values.FirstOrDefault(a => a.AgentId == data.AgentId)?.Id.LocalId ?? 0;
            change = new(WorldChangeKind.AppearanceChanged, new ObjectId(_epoch, local), data.AgentId);
        }
        Raise(change);
    }

    /// <summary>
    /// The hover height an AvatarAppearance message stated for <paramref name="agentId"/> (its AppearanceHover block), or null when the message
    /// had none. Kept for the avatar's appearance, whichever of the two arrives first; a message without the block clears it.
    /// </summary>
    public void ApplyHover(object? sim, Guid agentId, float? z)
    {
        WorldChange? change = null;
        lock (_lock)
        {
            if (!AcceptLocked(sim))
                return;
            if (z is { } h) _hovers[agentId] = h; else _hovers.Remove(agentId);
            if (_appearances.TryGetValue(agentId, out var existing) && existing.Hover != z)
            {
                _appearances[agentId] = existing with { Hover = z };
                uint local = _avatars.Values.FirstOrDefault(a => a.AgentId == agentId)?.Id.LocalId ?? 0;
                change = new(WorldChangeKind.AppearanceChanged, new ObjectId(_epoch, local), agentId);
            }
        }
        if (change is { } c) Raise(c);
    }

    // ---------------- animations (receipt only) ----------------

    /// <summary>The animations the region last said <paramref name="agentId"/> plays in this region (empty when none or unknown).</summary>
    public IReadOnlyList<PlayingAnimation> Animations(Guid agentId)
    {
        lock (_lock)
            return _animations.GetValueOrDefault(agentId) ?? [];
    }

    /// <summary>An AvatarAnimation from the simulator: the whole list the avatar now plays. Kept per agent for the region
    /// (it may arrive before the avatar's own update); an unchanged list raises nothing.</summary>
    public void ApplyAnimations(object? sim, Guid agentId, IReadOnlyList<PlayingAnimation> playing)
    {
        WorldChange change;
        lock (_lock)
        {
            if (!AcceptLocked(sim))
                return;
            var next = playing.ToArray();
            if (_animations.TryGetValue(agentId, out var existing) && existing.SequenceEqual(next))
                return;
            _animations[agentId] = next;
            uint local = _avatars.Values.FirstOrDefault(a => a.AgentId == agentId)?.Id.LocalId ?? 0;
            change = new(WorldChangeKind.AnimationsChanged, new ObjectId(_epoch, local), agentId);
        }
        Raise(change);
    }

    // ---------------- self motion ----------------

    /// <summary>The region's wind as its last wind layer gave it (null until one arrives). Any thread.</summary>
    public RegionWind? Wind { get => Volatile.Read(ref _wind); set => Volatile.Write(ref _wind, value); }
    private RegionWind? _wind;

    /// <summary>The last self sample the gate passed in this epoch, or null.</summary>
    public SelfMotionSample? LastSelfMotion { get { lock (_lock) return _lastSelf; } }

    /// <summary>A sim-stated sample of our own avatar. Passes the on-change gate or produces nothing.</summary>
    public void ApplySelfMotion(object? sim, SelfMotion motion)
    {
        SelfMotionSample sample;
        lock (_lock)
        {
            if (!AcceptLocked(sim))
                return;
            if (_origin != Vector3.Zero)
                motion = motion with { Position = motion.Position + _origin };
            if (!_selfGate.ShouldPass(motion.Position, motion.Rotation, motion.Velocity))
                return;
            _lastSelf = sample = new SelfMotionSample(_epoch, motion);
        }
        SelfMoved?.Invoke(sample);
    }

    /// <summary>Force the next self sample through (a new consumer attached, or a teleport the epoch does not see).</summary>
    public void ResetSelfMotionGate()
    {
        lock (_lock) _selfGate.Reset();
    }

    // ---------------- terrain ----------------

    public void ApplyPatch(object? sim, int x, int y, int patchSize, float[]? heights)
    {
        WorldChange change;
        lock (_lock)
        {
            if (!AcceptLocked(sim))
                return;
            if (heights is null || !_terrain.Put(x, y, patchSize, heights))
            {
                Drop(WorldDrop.BadPatch);
                return;
            }
            change = new(WorldChangeKind.TerrainPatch, new ObjectId(_epoch, 0), PatchX: x, PatchY: y);
        }
        Raise(change);
    }

    private void Raise(WorldChange change) => Changed?.Invoke(change);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _hub?.ReleaseAll(this);
            _objects.Clear();
            _avatars.Clear();
            _tombstones.Clear();
            _killedUnseen.Clear();
            _pendingAttachRoots.Clear();
            _terrain.Clear();
            _appearances.Clear(); _hovers.Clear(); _previews.Clear();
            _animations.Clear();
            _lastSelf = null;
            _sim = null;
        }
        Changed = null;
        SelfMoved = null;
    }
}
