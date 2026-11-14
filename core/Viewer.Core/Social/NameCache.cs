// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Social;

/// <summary>
/// Agent names for one session (ADR-006: one cache per session, nothing static). Names are learned from what the grid sends
/// anyway (an agent's local chat, an instant message) and from the grid's answers to name requests. Asking for an unknown
/// name queues its id; <see cref="Flush"/> sends the queued ids as one request, and an id is not asked for again while its
/// request is outstanding, unless <see cref="RetryAfter"/> has passed. Thread-safe: learning happens on network threads,
/// reading on the interface's thread. Names are personal data: this class never logs them.
/// </summary>
public sealed class NameCache
{
    /// <summary>How long an unanswered request is left before the id may be asked for again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>The most ids one request carries (larger batches are split).</summary>
    public const int MaxBatch = 64;

    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, string> _names = new();
    private readonly List<Guid> _queued = new();
    private readonly HashSet<Guid> _queuedSet = new();
    private readonly Dictionary<Guid, DateTimeOffset> _asked = new();

    public NameCache(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Raised (outside the lock) when a name is learned or changes: the agent and the new name.</summary>
    public event Action<Guid, string>? NameChanged;

    /// <summary>The name known for <paramref name="agent"/>, or null (and the id is queued for the next <see cref="Flush"/>).</summary>
    public string? Get(Guid agent)
    {
        if (agent == Guid.Empty)
            return null;
        lock (_gate)
        {
            if (_names.TryGetValue(agent, out var name))
                return name;
            if (_asked.TryGetValue(agent, out var at) && _clock() - at < RetryAfter)
                return null;
            if (_queuedSet.Add(agent))
                _queued.Add(agent);
            return null;
        }
    }

    /// <summary>Records a name. An empty id or a blank name is ignored.</summary>
    public void Learn(Guid agent, string? name)
    {
        if (agent == Guid.Empty || string.IsNullOrWhiteSpace(name))
            return;
        string n = name.Trim();
        lock (_gate)
        {
            if (_names.TryGetValue(agent, out var had) && had == n)
                return;
            _names[agent] = n;
            _asked.Remove(agent);
            if (_queuedSet.Remove(agent))
                _queued.Remove(agent);
        }
        NameChanged?.Invoke(agent, n);
    }

    /// <summary>Learns from a communication event: an agent's chat line, an instant message, a name reply.</summary>
    public void Learn(TransportEvent e)
    {
        switch (e)
        {
            case ChatReceived { Source: ChatSource.Agent } c:
                Learn(c.SourceId, c.FromName);
                break;
            case InstantMessageReceived { Group: false } m:
                Learn(m.FromAgent, m.FromName);
                break;
            case InstantMessageReceived { Kind: ImKind.GroupMessage } g:
                Learn(g.FromAgent, g.FromName);   // a member's message names its sender
                break;
            case FriendsListed l:
                foreach (var f in l.Friends)
                    Learn(f.Id, f.Name);
                break;
            case FriendshipOffered o:
                Learn(o.From, o.FromName);
                break;
            case FriendshipAnswered a:
                Learn(a.From, a.FromName);
                break;
            case TeleportOffered t:
                Learn(t.From, t.FromName);
                break;
            case TeleportRequested q:
                Learn(q.From, q.FromName);
                break;
            case GroupNoticeReceived { } n when n.Sender != n.GroupId:
                Learn(n.Sender, n.FromName);   // a notice from the group itself names the group, not a person
                break;
            case AgentNamesReceived r:
                foreach (var (id, name) in r.Names)
                    Learn(id, name);
                break;
        }
    }

    /// <summary>Sends the queued ids to <paramref name="requester"/> in batches of at most <see cref="MaxBatch"/>; returns how many were asked for.</summary>
    public int Flush(IAgentNameRequester? requester)
    {
        if (requester is null)
            return 0;
        Guid[] ids;
        lock (_gate)
        {
            if (_queued.Count == 0)
                return 0;
            ids = _queued.ToArray();
            _queued.Clear();
            _queuedSet.Clear();
            var now = _clock();
            foreach (var id in ids)
                _asked[id] = now;
        }
        for (int i = 0; i < ids.Length; i += MaxBatch)
            requester.RequestAgentNames(ids.Skip(i).Take(MaxBatch).ToArray());
        return ids.Length;
    }

    /// <summary>The short form shown until a name arrives: the first eight hex digits of the id.</summary>
    public static string Placeholder(Guid agent) => agent.ToString("N")[..8];
}

/// <summary>Conversation ids for one-to-one instant messages.</summary>
public static class ImSessionId
{
    /// <summary>The conversation id two agents share: the other's id XOR ours; with ourselves, our own id (the protocol's rule,
    /// which the official viewer and the library both follow).</summary>
    public static Guid For(Guid self, Guid other)
    {
        if (self == other)
            return self;
        Span<byte> a = stackalloc byte[16], b = stackalloc byte[16];
        self.TryWriteBytes(a);
        other.TryWriteBytes(b);
        for (int i = 0; i < 16; i++)
            a[i] ^= b[i];
        return new Guid(a);
    }
}
