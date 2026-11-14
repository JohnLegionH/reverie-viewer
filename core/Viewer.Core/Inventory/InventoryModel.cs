// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Inventory;

/// <summary>How much of one folder's contents this session has.</summary>
public enum FolderLoad { NotLoaded, Loading, Loaded, Failed }

/// <summary>
/// One session's inventory: the folder tree from the login skeleton, filled in lazily one folder at a time (a folder is
/// requested when it is first opened, never all at once), and kept current by what the grid pushes. A folder's answer is the
/// whole truth for that folder: anything the model held there that the answer lacks is dropped. Thread-safe: the network
/// thread applies events and the interface thread reads. Names are personal data and are never logged here.
/// Behaviour follows the official viewer's inventory model (the skeleton at login, folder contents fetched on demand,
/// pushed updates applied to the cache); the code is our own.
/// </summary>
public sealed class InventoryModel
{
    /// <summary>The most folder requests the model has outstanding at once; further folders wait their turn.</summary>
    public const int DefaultMaxInFlight = 4;

    private readonly object _gate = new();
    private readonly Action<Guid, Guid>? _requester;
    private readonly int _maxInFlight;

    private readonly Dictionary<Guid, InventoryFolderData> _folders = new();
    private readonly Dictionary<Guid, InventoryItemData> _items = new();
    private readonly Dictionary<Guid, List<Guid>> _subfolders = new();
    private readonly Dictionary<Guid, List<Guid>> _contents = new();
    private readonly Dictionary<Guid, FolderLoad> _load = new();
    private readonly Queue<Guid> _waiting = new();
    private readonly HashSet<Guid> _inFlight = new();
    private readonly HashSet<Guid> _refetch = new();   // folders changed while a request was out: the answer may predate the change
    private readonly HashSet<Guid> _everLoaded = new();   // folders whose contents arrived at least once this session
    private int _revision;

    private Guid _owner, _root, _libraryOwner, _libraryRoot;
    private int _attachmentLimit = -1;

    /// <param name="requester">Asks the grid for a folder's contents: the folder and its owner. Called outside the model's lock.</param>
    public InventoryModel(Action<Guid, Guid>? requester = null, int maxInFlight = DefaultMaxInFlight)
    {
        _requester = requester;
        _maxInFlight = Math.Max(1, maxInFlight);
    }

    /// <summary>Raised (outside the lock, on the thread that caused it) whenever something a view shows has changed.</summary>
    public event Action? Changed;

    /// <summary>Moves on with every real change, so a view can tell its picture is out of date.</summary>
    public int Revision { get { lock (_gate) return _revision; } }

    public bool HasSkeleton { get { lock (_gate) return _root != Guid.Empty; } }
    public Guid OwnerId { get { lock (_gate) return _owner; } }
    public Guid RootId { get { lock (_gate) return _root; } }

    /// <summary>The most objects the grid lets the avatar wear (from the login reply's account benefits); -1 when the grid sent none.</summary>
    public int AttachmentLimit { get { lock (_gate) return _attachmentLimit; } }
    public Guid LibraryOwnerId { get { lock (_gate) return _libraryOwner; } }
    public Guid LibraryRootId { get { lock (_gate) return _libraryRoot; } }

    /// <summary>How many folder requests are outstanding.</summary>
    public int InFlight { get { lock (_gate) return _inFlight.Count; } }

    /// <summary>Folders (both trees) whose contents are not all here yet.</summary>
    public int UnloadedFolderCount
    {
        get { lock (_gate) return _folders.Keys.Count(id => LoadOf(id) != FolderLoad.Loaded); }
    }

    // ---------------- applying what the grid says ----------------

    public void Apply(TransportEvent e)
    {
        List<(Guid Folder, Guid Owner)> start;
        bool changed;
        lock (_gate)
        {
            changed = e switch
            {
                InventorySkeletonReceived s => ApplySkeleton(s),
                InventoryFolderFetched f => ApplyFetched(f),
                InventoryChanged c => ApplyChanged(c),
                _ => false,
            };
            if (changed)
                _revision++;
            start = StartWaiting();
        }
        Send(start);
        if (changed)
            Changed?.Invoke();
    }

    private bool ApplySkeleton(InventorySkeletonReceived s)
    {
        _folders.Clear(); _items.Clear(); _subfolders.Clear(); _contents.Clear(); _load.Clear(); _waiting.Clear(); _inFlight.Clear(); _everLoaded.Clear();
        _owner = s.OwnerId; _root = s.RootId; _libraryOwner = s.LibraryOwnerId; _libraryRoot = s.LibraryRootId;
        _attachmentLimit = s.AttachmentLimit;
        foreach (var f in s.Folders.Concat(s.LibraryFolders))
            PutFolder(f);
        return true;
    }

    private bool ApplyFetched(InventoryFolderFetched f)
    {
        if (!_folders.TryGetValue(f.FolderId, out var folder))
            return false;
        _inFlight.Remove(f.FolderId);
        if (!f.Ok)
        {
            _refetch.Remove(f.FolderId);
            return SetLoad(f.FolderId, FolderLoad.Failed);
        }
        bool changed = false;
        var keepFolders = f.Folders.Select(x => x.Id).ToHashSet();
        var keepItems = f.Items.Select(x => x.Id).ToHashSet();
        foreach (var gone in ListOf(_subfolders, f.FolderId).Where(id => !keepFolders.Contains(id)).ToList())
            changed |= RemoveFolder(gone);
        foreach (var gone in ListOf(_contents, f.FolderId).Where(id => !keepItems.Contains(id)).ToList())
            changed |= RemoveItem(gone);
        foreach (var sub in f.Folders)
            changed |= PutFolder(sub with { ParentId = f.FolderId });
        foreach (var item in f.Items)
            changed |= PutItem(item with { ParentId = f.FolderId });
        var updated = folder with
        {
            Version = f.Version >= 0 ? f.Version : folder.Version,
            DescendentCount = f.DescendentCount >= 0 ? f.DescendentCount : folder.DescendentCount,
        };
        if (updated != folder)
        {
            _folders[f.FolderId] = updated;
            changed = true;
        }
        if (_refetch.Remove(f.FolderId))
        {
            _load[f.FolderId] = FolderLoad.Loading;   // changed while this answer was on its way: ask once more
            _everLoaded.Add(f.FolderId);
            _waiting.Enqueue(f.FolderId);
            return true;
        }
        changed |= SetLoad(f.FolderId, FolderLoad.Loaded);
        return changed;
    }

    private bool ApplyChanged(InventoryChanged c)
    {
        bool changed = false;
        foreach (var id in c.RemovedItems)
            changed |= RemoveItem(id);
        foreach (var id in c.RemovedFolders)
            changed |= RemoveFolder(id);
        foreach (var f in c.Folders)
            changed |= PutFolder(f);
        foreach (var i in c.Items)
            changed |= PutItem(i);
        return changed;
    }

    /// <summary>Adds or updates a folder, keeping a known version when the new data has none. True when anything changed.</summary>
    private bool PutFolder(InventoryFolderData f)
    {
        if (f.Id == Guid.Empty)
            return false;
        if (_folders.TryGetValue(f.Id, out var had))
        {
            var merged = f with
            {
                Version = f.Version >= 0 ? f.Version : had.Version,
                DescendentCount = f.DescendentCount >= 0 ? f.DescendentCount : had.DescendentCount,
            };
            if (merged == had)
                return false;
            if (had.ParentId != merged.ParentId)
            {
                ListOf(_subfolders, had.ParentId).Remove(f.Id);
                Link(_subfolders, merged.ParentId, f.Id);
            }
            _folders[f.Id] = merged;
            return true;
        }
        _folders[f.Id] = f;
        if (f.ParentId != Guid.Empty)
            Link(_subfolders, f.ParentId, f.Id);
        return true;
    }

    private bool PutItem(InventoryItemData i)
    {
        if (i.Id == Guid.Empty)
            return false;
        if (_items.TryGetValue(i.Id, out var had))
        {
            if (had == i)
                return false;
            if (had.ParentId != i.ParentId)
            {
                ListOf(_contents, had.ParentId).Remove(i.Id);
                Link(_contents, i.ParentId, i.Id);
            }
            _items[i.Id] = i;
            return true;
        }
        _items[i.Id] = i;
        Link(_contents, i.ParentId, i.Id);
        return true;
    }

    private bool RemoveItem(Guid id)
    {
        if (!_items.Remove(id, out var item))
            return false;
        ListOf(_contents, item.ParentId).Remove(id);
        return true;
    }

    /// <summary>Removes a folder and everything under it.</summary>
    private bool RemoveFolder(Guid id)
    {
        if (!_folders.Remove(id, out var folder))
            return false;
        ListOf(_subfolders, folder.ParentId).Remove(id);
        foreach (var sub in ListOf(_subfolders, id).ToList())
            RemoveFolder(sub);
        foreach (var item in ListOf(_contents, id).ToList())
            RemoveItem(item);
        _subfolders.Remove(id);
        _contents.Remove(id);
        _load.Remove(id);
        _inFlight.Remove(id);
        _everLoaded.Remove(id);
        return true;
    }

    private static List<Guid> ListOf(Dictionary<Guid, List<Guid>> map, Guid key) => map.TryGetValue(key, out var l) ? l : new List<Guid>();

    private static void Link(Dictionary<Guid, List<Guid>> map, Guid parent, Guid child)
    {
        if (!map.TryGetValue(parent, out var list))
            map[parent] = list = new List<Guid>();
        if (!list.Contains(child))
            list.Add(child);
    }

    private FolderLoad LoadOf(Guid id) => _load.TryGetValue(id, out var s) ? s : FolderLoad.NotLoaded;

    private bool SetLoad(Guid id, FolderLoad state)
    {
        if (state == FolderLoad.Loaded)
            _everLoaded.Add(id);
        if (LoadOf(id) == state)
            return false;
        _load[id] = state;
        return true;
    }

    // ---------------- lazy loading ----------------

    public FolderLoad LoadState(Guid folderId)
    {
        lock (_gate)
            return _folders.ContainsKey(folderId) ? LoadOf(folderId) : FolderLoad.NotLoaded;
    }

    /// <summary>Whether the folder's contents arrived at least once this session (a folder read again still has what it showed).</summary>
    public bool WasLoaded(Guid folderId) { lock (_gate) return _everLoaded.Contains(folderId); }

    /// <summary>Asks for the folder's contents if they are not here and not already on their way. True when the contents are on
    /// their way (asked now, asked before, or waiting for a free slot); false for an unknown folder, one already loaded and
    /// one whose last request failed (use <see cref="Retry"/>).</summary>
    public bool EnsureLoaded(Guid folderId)
    {
        List<(Guid Folder, Guid Owner)> start;
        bool pending, changed = false;
        lock (_gate)
        {
            if (_root == Guid.Empty || !_folders.ContainsKey(folderId))
                return false;
            var state = LoadOf(folderId);
            if (state == FolderLoad.Loading)
                return true;
            if (state != FolderLoad.NotLoaded)
                return false;
            _load[folderId] = FolderLoad.Loading;
            _waiting.Enqueue(folderId);
            _revision++;
            changed = true;
            pending = true;
            start = StartWaiting();
        }
        Send(start);
        if (changed)
            Changed?.Invoke();
        return pending;
    }

    /// <summary>Tries a failed folder again. True when it was failed and is now on its way.</summary>
    public bool Retry(Guid folderId)
    {
        lock (_gate)
        {
            if (LoadOf(folderId) != FolderLoad.Failed || !_folders.ContainsKey(folderId))
                return false;
            _load[folderId] = FolderLoad.NotLoaded;
        }
        return EnsureLoaded(folderId);
    }

    /// <summary>Moves waiting folders into the free request slots (under the lock) and returns the requests to send.</summary>
    private List<(Guid Folder, Guid Owner)> StartWaiting()
    {
        var start = new List<(Guid, Guid)>();
        while (_inFlight.Count < _maxInFlight && _waiting.Count > 0)
        {
            var id = _waiting.Dequeue();
            if (!_folders.ContainsKey(id) || LoadOf(id) != FolderLoad.Loading)
                continue;
            _inFlight.Add(id);
            start.Add((id, IsInLibraryLocked(id) ? _libraryOwner : _owner));
        }
        return start;
    }

    private void Send(List<(Guid Folder, Guid Owner)> start)
    {
        foreach (var (folder, owner) in start)
        {
            try { _requester?.Invoke(folder, owner); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // the request never left: the folder is failed, not waiting forever
                bool changed;
                lock (_gate)
                {
                    _inFlight.Remove(folder);
                    changed = _folders.ContainsKey(folder) && SetLoad(folder, FolderLoad.Failed);
                    if (changed) _revision++;
                }
                if (changed) Changed?.Invoke();
            }
        }
    }

    /// <summary>Drops what is known of a folder's contents and asks for them again (after a command, so the grid's answer settles what the folder holds).</summary>
    public bool Invalidate(Guid folderId)
    {
        lock (_gate)
        {
            if (_root == Guid.Empty || !_folders.ContainsKey(folderId))
                return false;
            var state = LoadOf(folderId);
            if (state == FolderLoad.Loading)
            {
                _refetch.Add(folderId);
                return true;
            }
            _load[folderId] = FolderLoad.NotLoaded;
        }
        return EnsureLoaded(folderId);
    }

    /// <summary>Asks for every folder not yet loaded (a search needs the whole tree); the request limit still applies. Returns how many were queued.</summary>
    public int EnsureAllLoaded()
    {
        List<(Guid Folder, Guid Owner)> start;
        int queued = 0;
        lock (_gate)
        {
            if (_root == Guid.Empty)
                return 0;
            foreach (var id in _folders.Keys.ToList())
            {
                if (LoadOf(id) != FolderLoad.NotLoaded)
                    continue;
                _load[id] = FolderLoad.Loading;
                _waiting.Enqueue(id);
                queued++;
            }
            if (queued > 0)
                _revision++;
            start = StartWaiting();
        }
        Send(start);
        if (queued > 0)
            Changed?.Invoke();
        return queued;
    }


    // ---------------- reading ----------------

    public InventoryFolderData? Folder(Guid id) { lock (_gate) return _folders.TryGetValue(id, out var f) ? f : null; }

    public InventoryItemData? Item(Guid id) { lock (_gate) return _items.TryGetValue(id, out var i) ? i : null; }

    public IReadOnlyList<InventoryFolderData> ChildFolders(Guid folderId)
    {
        lock (_gate)
            return ListOf(_subfolders, folderId).Select(id => _folders[id]).ToList();
    }

    public IReadOnlyList<InventoryItemData> ChildItems(Guid folderId)
    {
        lock (_gate)
            return ListOf(_contents, folderId).Select(id => _items[id]).ToList();
    }

    /// <summary>Every item held, in no particular order.</summary>
    public IReadOnlyList<InventoryItemData> AllItems() { lock (_gate) return _items.Values.ToList(); }

    /// <summary>Every folder held (both trees), in no particular order.</summary>
    public IReadOnlyList<InventoryFolderData> AllFolders() { lock (_gate) return _folders.Values.ToList(); }

    /// <summary>The agent's own folder of this kind (the library's folders do not count), or empty. The one directly under My Inventory
    /// wins, as the official viewer finds it (LLInventoryModel::findCategoryUUIDForType looks at the root's children): a hypergrid
    /// suitcase folder holds copies of the system folders, and the login skeleton may list those first. A kind found only deeper is
    /// still returned.</summary>
    public Guid FolderOfKind(FolderKind kind)
    {
        lock (_gate)
        {
            InventoryFolderData? deeper = null;
            foreach (var f in _folders.Values)
            {
                if (f.Kind != kind || f.Id == _root)
                    continue;
                if (f.ParentId == _root && _root != Guid.Empty)
                    return f.Id;
                if (deeper is null && !IsInLibraryLocked(f.Id))
                    deeper = f;
            }
            return deeper?.Id ?? Guid.Empty;
        }
    }

    public bool IsInLibrary(Guid id) { lock (_gate) return IsInLibraryLocked(id); }

    private bool IsInLibraryLocked(Guid id)
    {
        if (_libraryRoot == Guid.Empty)
            return false;
        Guid at = id;
        for (int depth = 0; depth < 4096 && at != Guid.Empty; depth++)
        {
            if (at == _libraryRoot)
                return true;
            if (_folders.TryGetValue(at, out var f)) at = f.ParentId;
            else if (_items.TryGetValue(at, out var i)) at = i.ParentId;
            else return false;
        }
        return false;
    }

    /// <summary>True when <paramref name="id"/> (a folder or an item) lies below <paramref name="ancestor"/> (not equal to it).</summary>
    public bool IsDescendantOf(Guid id, Guid ancestor)
    {
        lock (_gate)
        {
            Guid at = _folders.TryGetValue(id, out var f) ? f.ParentId : _items.TryGetValue(id, out var i) ? i.ParentId : Guid.Empty;
            for (int depth = 0; depth < 4096 && at != Guid.Empty; depth++)
            {
                if (at == ancestor)
                    return true;
                at = _folders.TryGetValue(at, out var p) ? p.ParentId : Guid.Empty;
            }
            return false;
        }
    }

    /// <summary>True for a folder that has a purpose of its own (Textures, Trash, the root and the like). These cannot be
    /// renamed, moved or trashed.</summary>
    public bool IsSystemFolder(Guid id)
    {
        lock (_gate)
            return _folders.TryGetValue(id, out var f) && (id == _root || id == _libraryRoot || IsProtected(f.Kind));
    }

    /// <summary>Whether a folder kind marks a system folder: every kind with a purpose except a plain folder, an outfit the
    /// user made, and a kind this viewer does not know.</summary>
    public static bool IsProtected(FolderKind kind) =>
        kind is not (FolderKind.None or FolderKind.Outfit or FolderKind.Other or FolderKind.MarketplaceListings or FolderKind.MarketplaceStock or FolderKind.MarketplaceVersion);
}
