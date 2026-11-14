// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.World;

/// <summary>
/// Which region's world holds each object and avatar of one session, by full id. The official viewer keeps one object list keyed by full id
/// across regions: when another region sends an update for an id it already holds, it keeps the object and gives it the new local id and
/// region, and does not make a new one (the object list's update handling, <c>llviewerobjectlist.cpp</c> <c>processObjectUpdate</c>). Each
/// <see cref="WorldState"/> of a session keeps its own table by local id; this hub is what lets a world find that an id it is told about is
/// already held by another world, so the object is handed over instead of being removed there and made again here.
/// Lock order: a world's lock, then the hub's; the hub never calls into a world.
/// </summary>
public sealed class IdentityHub
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Holder> _held = new();
    private int _handoffs;

    /// <summary>Who holds an id: the world, the local id it knows it by, and whether it is an avatar.</summary>
    internal readonly record struct Holder(WorldState World, uint LocalId, bool IsAvatar, bool Own = false);

    /// <summary>The agent this session is logged in as: its avatar is never removed by a kill (the official viewer ignores a kill for its own avatar).</summary>
    public Guid SelfAgent { get { lock (_lock) return _self; } set { lock (_lock) _self = value; } }
    private Guid _self;

    /// <summary>How many objects and avatars have been handed from one world to another.</summary>
    public int Handoffs { get { lock (_lock) return _handoffs; } }

    /// <summary>The world the agent is in. A neighbour's world does not take the agent's own avatar or what it wears from it: the neighbour is not where
    /// the agent is, and a copy there (the agent's own presence in a region next door) is not the avatar itself. When the agent crosses, the neighbour becomes the
    /// current world, and what it then sends is handed over.</summary>
    public WorldState? Current { get { lock (_lock) return _current; } set { lock (_lock) _current = value; } }
    private WorldState? _current;

    /// <summary>Records that <paramref name="world"/> now holds <paramref name="fullId"/> as <paramref name="localId"/>. Returns the other world that held it
    /// and must now let go, if one did. A world that is not the current one does not take the agent's own avatar, or what it wears, from the current one (nothing is recorded then); anything else a neighbour sends for an id the current world holds has moved there, as the official viewer moves any object whose update comes from another region.</summary>
    internal Holder? Hold(WorldState world, Guid fullId, uint localId, bool isAvatar, bool own = false)
    {
        lock (_lock)
        {
            Holder? previous = _held.TryGetValue(fullId, out var h) && !ReferenceEquals(h.World, world) ? h : null;
            if (previous is { Own: true } p && ReferenceEquals(p.World, _current) && !ReferenceEquals(world, _current))
                return null;
            _held[fullId] = new Holder(world, localId, isAvatar, own);
            return previous;
        }
    }

    /// <summary>The local id <paramref name="world"/> knows <paramref name="fullId"/> by, when it is the one holding it.</summary>
    internal uint? LocalIn(WorldState world, Guid fullId)
    {
        lock (_lock)
            return _held.TryGetValue(fullId, out var h) && ReferenceEquals(h.World, world) && !h.IsAvatar ? h.LocalId : null;
    }

    /// <summary>The world has let go of the id (removed it for good), if it is the one holding it as that local id.</summary>
    internal void Release(WorldState world, Guid fullId, uint localId)
    {
        lock (_lock)
            if (_held.TryGetValue(fullId, out var h) && ReferenceEquals(h.World, world) && h.LocalId == localId)
                _held.Remove(fullId);
    }

    /// <summary>The world holds nothing any more (its region was reset or it was disposed).</summary>
    internal void ReleaseAll(WorldState world)
    {
        lock (_lock)
        {
            var mine = _held.Where(kv => ReferenceEquals(kv.Value.World, world)).Select(kv => kv.Key).ToList();
            foreach (var id in mine)
                _held.Remove(id);
        }
    }

    /// <summary>The avatar of <paramref name="agent"/> as the world that holds it now says; null when no world does.</summary>
    public AvatarState? CurrentAvatar(Guid agent)
    {
        Holder? h;
        lock (_lock) h = _held.TryGetValue(agent, out var x) && x.IsAvatar ? x : null;
        return h is { } holder ? holder.World.Avatar(new ObjectId(holder.World.Epoch, holder.LocalId)) : null;   // asked outside the hub's lock: a world asks the hub while it holds its own
    }

    /// <summary>Whether some world holds the object or avatar.</summary>
    public bool Holds(Guid fullId) { lock (_lock) return _held.ContainsKey(fullId); }

    internal void CountHandoff()
    {
        lock (_lock) _handoffs++;
    }
}
