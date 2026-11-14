// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Social;

// Behaviour follows the official viewer's mute list (the rules in NAME-1's spec, section 2), written in our own words.

/// <summary>Where the session's block list stands.</summary>
public enum MuteListState { NotAsked, Requested, Loaded, Unavailable }

/// <summary>Why an entry was not added.</summary>
public enum MuteAddResult { Added, Changed, Duplicate, RefusedInvalid, RefusedLinden, RefusedSelf, RefusedLimit }

/// <summary>
/// One session's block list. Entries with an id are kept by id; by-name entries (the null id) are kept by name. Thread-safe:
/// the grid's list arrives on a network thread, the interface reads on its own. Names are personal data: never logged here.
/// </summary>
public sealed class MuteList
{
    /// <summary>The most entries a list holds (the official default).</summary>
    public const int Limit = 1000;

    private readonly object _gate = new();
    private Guid _self;
    private readonly Dictionary<Guid, MuteEntry> _byId = new();
    private readonly Dictionary<string, MuteEntry> _byName = new(StringComparer.Ordinal);
    private MuteListState _state = MuteListState.NotAsked;
    private readonly object _cacheLock = new();
    private string? _cachePath;
    private Action<string>? _cacheProblem;

    public MuteList(Guid self) => _self = self;

    /// <summary>Tells the list who the session's own agent is (known once the login reply has come).</summary>
    internal void SetSelf(Guid self) { lock (_gate) _self = self; }

    public MuteListState State { get { lock (_gate) return _state; } }

    /// <summary>Raised (outside the lock) when the entries or the state changed.</summary>
    public event Action? Changed;

    /// <summary>Every entry, by-name ones included, in no particular order.</summary>
    public IReadOnlyList<MuteEntry> Entries
    {
        get { lock (_gate) return _byId.Values.Concat(_byName.Values).ToArray(); }
    }

    public int Count { get { lock (_gate) return _byId.Count + _byName.Count; } }

    /// <summary>
    /// Keeps the list in the file at <paramref name="path"/> (<see cref="MuteCache"/>): the file's entries are put in the list now, so blocks
    /// apply before the grid answers (the list is not called loaded, which is the grid's word), and from here on the grid's list and every
    /// add and remove write the file whole. A file that cannot be read, or has lines that are not entries, is reported once through
    /// <paramref name="problem"/> (an event name only, never content) and what could be read is used.
    /// </summary>
    public void AttachCache(string path, Action<string>? problem = null)
    {
        var cached = MuteCache.Read(path, out int skipped);
        lock (_cacheLock)
        {
            _cachePath = path;
            _cacheProblem = problem;
        }
        if (cached is null)
        {
            if (File.Exists(path))
                problem?.Invoke("mute.cache-unreadable");
            return;
        }
        if (skipped > 0)
            problem?.Invoke("mute.cache-unreadable");
        bool any;
        lock (_gate)
        {
            Fill(cached);
            any = cached.Count > 0;
        }
        if (any)
            Changed?.Invoke();
    }

    /// <summary>Writes the list to its cache file, when there is one. Called outside the list's lock; the snapshot is taken inside the cache
    /// lock so two writers cannot leave the older list in the file.</summary>
    private void WriteCache()
    {
        lock (_cacheLock)
        {
            if (_cachePath is null)
                return;
            if (!MuteCache.Write(_cachePath, Entries))
                _cacheProblem?.Invoke("mute.cache-not-written");
        }
    }

    private void Fill(IEnumerable<MuteEntry> entries)
    {
        _byId.Clear();
        _byName.Clear();
        foreach (var e in entries)
        {
            if (e.Kind == MuteKind.ByName || e.Id == Guid.Empty)
            {
                if (!string.IsNullOrEmpty(e.Name))
                    _byName[e.Name] = e with { Kind = MuteKind.ByName, Id = Guid.Empty };
            }
            else
                _byId[e.Id] = e;
        }
    }

    internal void SetState(MuteListState state)
    {
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
        }
        Changed?.Invoke();
    }

    /// <summary>Replaces the list with the grid's. A null id or a by-name kind is a by-name entry; an entry with no name and no id is dropped.</summary>
    public void Load(IEnumerable<MuteEntry> entries)
    {
        lock (_gate)
        {
            Fill(entries);
            _state = MuteListState.Loaded;
        }
        WriteCache();
        Changed?.Invoke();
    }

    /// <summary>Whether the account name is one the official viewer treats as unblockable: last name "Linden", in either "First Last" or "first.last" form.</summary>
    public static bool IsLinden(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var parts = name.Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && string.Equals(parts[1], "Linden", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What adding <paramref name="entry"/> would do, without doing it (the interface and the pipeline ask before anything is sent).</summary>
    public MuteAddResult Check(MuteEntry entry)
    {
        lock (_gate) return CheckLocked(entry);
    }

    private MuteAddResult CheckLocked(MuteEntry entry)
    {
        if (entry.Kind == MuteKind.Resident && IsLinden(entry.Name) && (entry.Flags & MuteFlag.TextChat) == 0)
            return MuteAddResult.RefusedLinden;
        if (entry.Kind == MuteKind.Resident && entry.Id == _self)
            return MuteAddResult.RefusedSelf;
        if (entry.Kind == MuteKind.ByName)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.Id != Guid.Empty)
                return MuteAddResult.RefusedInvalid;
            if (_byName.ContainsKey(entry.Name))
                return MuteAddResult.Duplicate;
        }
        else
        {
            if (entry.Id == Guid.Empty || entry.Kind == MuteKind.External)
                return MuteAddResult.RefusedInvalid;
            if (_byId.TryGetValue(entry.Id, out var had))
                return had.Flags == entry.Flags ? MuteAddResult.Duplicate : MuteAddResult.Changed;
        }
        if (_byId.Count + _byName.Count >= Limit)
            return MuteAddResult.RefusedLimit;
        return MuteAddResult.Added;
    }

    /// <summary>Adds (or changes) one entry. Returns what happened; only <see cref="MuteAddResult.Added"/> and <see cref="MuteAddResult.Changed"/> change the list.</summary>
    public MuteAddResult Add(MuteEntry entry)
    {
        MuteAddResult r;
        lock (_gate)
        {
            r = CheckLocked(entry);
            if (r is not (MuteAddResult.Added or MuteAddResult.Changed)) return r;
            if (entry.Kind == MuteKind.ByName) _byName[entry.Name] = entry;
            else _byId[entry.Id] = entry;
        }
        WriteCache();
        Changed?.Invoke();
        return r;
    }

    /// <summary>Removes the entry for <paramref name="id"/>, or the by-name entry called <paramref name="name"/> when the id is not on the list. True when one was removed.</summary>
    public bool Remove(Guid id, string? name)
    {
        bool removed;
        lock (_gate)
        {
            removed = id != Guid.Empty && _byId.Remove(id);
            if (!removed && !string.IsNullOrEmpty(name))
                removed = _byName.Remove(name);
        }
        if (removed)
        {
            WriteCache();
            Changed?.Invoke();
        }
        return removed;
    }

    /// <summary>The entry that blocks <paramref name="id"/>, by id only; null when none.</summary>
    public MuteEntry? Find(Guid id)
    {
        lock (_gate) return _byId.TryGetValue(id, out var e) ? e : null;
    }

    /// <summary>
    /// Whether <paramref name="id"/> / <paramref name="name"/> is blocked for the property <paramref name="allowedFlag"/> (0: any property; the
    /// text-chat flag for chat and messages). An entry by id blocks unless the property's bit is set in it; a name can block only when
    /// <paramref name="nameCounts"/> (an avatar in view is known by id, so a by-name entry never blocks it) and is not empty.
    /// </summary>
    public bool IsMuted(Guid id, string? name, uint allowedFlag = 0, bool nameCounts = true)
    {
        lock (_gate)
        {
            if (id != Guid.Empty && _byId.TryGetValue(id, out var e))
                return (allowedFlag & e.Flags) == 0;
            if (!nameCounts || string.IsNullOrEmpty(name)) return false;
            return _byName.ContainsKey(name);
        }
    }
}

/// <summary>What the block list does with one thing the grid sent.</summary>
public enum MuteVerdict { Pass, Drop, DeclineFriendship }

/// <summary>
/// The drop rules, one place (NAME-1 spec 2.2). Pure: reads the event and the list. <paramref name="isAvatarInView"/> says whether an agent id
/// is an avatar the region has told this session about (a by-name entry never blocks such an avatar).
/// </summary>
public static class MuteFilter
{
    public static MuteVerdict Judge(TransportEvent e, MuteList list, Func<Guid, bool> isAvatarInView)
    {
        if (list.Count == 0) return MuteVerdict.Pass;
        const uint Text = MuteFlag.TextChat;
        switch (e)
        {
            case ChatReceived { Source: ChatSource.System }:
                return MuteVerdict.Pass;
            case ChatReceived { Source: ChatSource.Object } c:
                return list.IsMuted(c.SourceId, c.FromName, Text) || list.IsMuted(c.OwnerId, null, Text) ? MuteVerdict.Drop : MuteVerdict.Pass;
            case ChatReceived c:
                if (MuteList.IsLinden(c.FromName)) return MuteVerdict.Pass;   // a Linden's text cannot be blocked
                return list.IsMuted(c.SourceId, c.FromName, Text, nameCounts: !isAvatarInView(c.SourceId)) ? MuteVerdict.Drop : MuteVerdict.Pass;
            case InstantMessageReceived im:
                if (MuteList.IsLinden(im.FromName)) return MuteVerdict.Pass;   // a Linden's text cannot be blocked
                if (im.Group)
                    return list.IsMuted(im.SessionId, null, Text) || list.IsMuted(im.FromAgent, im.FromName, Text, !isAvatarInView(im.FromAgent)) ? MuteVerdict.Drop : MuteVerdict.Pass;
                return list.IsMuted(im.FromAgent, im.FromName, Text, nameCounts: !isAvatarInView(im.FromAgent)) ? MuteVerdict.Drop : MuteVerdict.Pass;
            case FriendshipOffered o:
                return list.IsMuted(o.From, o.FromName, Text, nameCounts: !isAvatarInView(o.From)) ? MuteVerdict.DeclineFriendship : MuteVerdict.Pass;
            case TeleportOffered t:
                return list.IsMuted(t.From, t.FromName, Text, nameCounts: !isAvatarInView(t.From)) ? MuteVerdict.Drop : MuteVerdict.Pass;
            case TeleportRequested q:
                return list.IsMuted(q.From, q.FromName, Text, nameCounts: !isAvatarInView(q.From)) ? MuteVerdict.Drop : MuteVerdict.Pass;
            case GroupNoticeReceived n:
                return list.IsMuted(n.GroupId, null, Text) || (n.Sender != n.GroupId && list.IsMuted(n.Sender, n.FromName, Text, !isAvatarInView(n.Sender))) ? MuteVerdict.Drop : MuteVerdict.Pass;
            default:
                return MuteVerdict.Pass;
        }
    }
}
