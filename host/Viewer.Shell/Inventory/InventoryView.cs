// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;

namespace Viewer.Shell.Inventory;

public enum InventorySort { ByName, ByDate }

public enum InventoryTab { All, Recent, Worn }

/// <summary>One visible line of the inventory tree or the recent list.</summary>
/// <param name="IconKey">Names the icon to draw (see <see cref="InventoryIcons"/>).</param>
/// <param name="Load">For a folder, how much of its contents is here.</param>
public sealed record InventoryRow(Guid Id, bool IsFolder, string Name, string IconKey, int Depth, bool Expanded, bool CanExpand,
    FolderLoad Load, FolderKind FolderKind, ItemKind ItemKind, DateTimeOffset? Date, bool IsLink, bool IsTrash, bool IsSystem, bool IsLibrary);

/// <summary>What the line under the tree says: a string-table key and its arguments.</summary>
public sealed record InventoryStatus(string Key, object[] Args);

/// <summary>Maps what an inventory line is to the name of its icon. The icons themselves are drawn by the engine side.</summary>
public static class InventoryIcons
{
    public static string ForFolder(FolderKind kind, bool open) => (kind switch
    {
        FolderKind.Trash => "inv.trash",
        FolderKind.Root => "inv.root",
        FolderKind.LostAndFound => "inv.lost",
        FolderKind.None or FolderKind.Other or FolderKind.Outfit => "inv.folder",
        _ when InventoryModel.IsProtected(kind) => "inv.system",
        _ => "inv.folder",
    }) + (open && kind != FolderKind.LostAndFound ? ".open" : "");

    public static string ForItem(ItemKind kind, bool isLink) => kind switch
    {
        ItemKind.LinkFolder => "inv.link.folder",
        ItemKind.Link => "inv.link",
        _ when isLink => "inv.link",
        ItemKind.Texture => "inv.texture",
        ItemKind.Sound => "inv.sound",
        ItemKind.CallingCard => "inv.callingcard",
        ItemKind.Landmark => "inv.landmark",
        ItemKind.Clothing => "inv.clothing",
        ItemKind.Object => "inv.object",
        ItemKind.Notecard => "inv.notecard",
        ItemKind.Script => "inv.script",
        ItemKind.Bodypart => "inv.bodypart",
        ItemKind.Animation => "inv.animation",
        ItemKind.Gesture => "inv.gesture",
        ItemKind.Mesh => "inv.mesh",
        ItemKind.Settings => "inv.settings",
        ItemKind.Material => "inv.material",
        _ => "inv.unknown",
    };

    /// <summary>Every icon name the window can ask for, each once.</summary>
    public static IReadOnlyList<string> All { get; } = Enum.GetValues<FolderKind>().SelectMany(k => new[] { ForFolder(k, false), ForFolder(k, true) })
        .Concat(Enum.GetValues<ItemKind>().Select(k => ForItem(k, false))).Append(ForItem(ItemKind.Notecard, true)).Distinct().ToList();
}

/// <summary>
/// The inventory window's logic, without the engine: which lines are visible, in what order, for the search text, the sort and
/// the tab. Behaviour follows the official viewer: My Inventory opens at the start and the Library stays closed; a folder is
/// requested when it is opened; folders order system folders first, then Trash, then the rest by name, then items (by name,
/// or newest first); a search matches a case-blind part of a name ("a+b" needs both parts) and asks for every folder not yet
/// loaded so the whole tree is searched. The Recent tab lists items made since the account last logged out (the time is kept in the
/// settings); with no such time yet it lists every item, newest first, as the official viewer does.
/// </summary>
public sealed class InventoryViewModel
{
    private readonly InventoryModel _model;
    private readonly string _myInventoryName;
    private readonly string _libraryName;
    private readonly HashSet<Guid> _expanded = new();
    private bool _autoOpened;

    public InventoryViewModel(InventoryModel model, string myInventoryName, string libraryName)
    {
        _model = model;
        _myInventoryName = myInventoryName;
        _libraryName = libraryName;
    }

    /// <summary>When the account last logged out. With a value, Recent lists the items made after it; without one, every item.</summary>
    public DateTimeOffset? RecentSince { get; set; }

    /// <summary>The Recent tab's filter (the official viewer's options: kinds, Since Logoff, hours and days ago).</summary>
    public RecentFilter Recent { get; set; } = RecentFilter.Default;

    /// <summary>The time the Recent tab's "hours and days ago" count from.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>The ids of the items the avatar wears or has attached, for the Worn tab; asked only while that tab is shown.</summary>
    public Func<IReadOnlySet<Guid>>? WornItems { get; set; }

    public InventorySort Sort { get; set; }
    public InventoryTab Tab { get; set; }
    public string SearchText { get; set; } = "";

    public bool IsExpanded(Guid folderId) { lock (_expanded) return _expanded.Contains(folderId); }

    /// <summary>Opens a folder and asks for its contents if they are not here.</summary>
    public void Expand(Guid folderId)
    {
        lock (_expanded) _expanded.Add(folderId);
        _model.EnsureLoaded(folderId);
    }

    public void Collapse(Guid folderId) { lock (_expanded) _expanded.Remove(folderId); }

    public void Toggle(Guid folderId)
    {
        if (IsExpanded(folderId)) Collapse(folderId); else Expand(folderId);
    }

    // ---------------- the lines ----------------

    public IReadOnlyList<InventoryRow> Rows()
    {
        if (!_model.HasSkeleton)
            return Array.Empty<InventoryRow>();
        if (!_autoOpened)
        {
            _autoOpened = true;
            Expand(_model.RootId);
        }
        var tokens = Tokens(SearchText);
        // a search on the tree asks for every folder not loaded yet; the Recent tab only filters what is already held, as the official viewer's
        // Recent panel filters its loaded inventory (the background fetch, when on, brings the rest)
        if (tokens.Count > 0 && Tab == InventoryTab.All)
            _model.EnsureAllLoaded();
        return Tab switch { InventoryTab.Recent => RecentRows(tokens), InventoryTab.Worn => WornRows(tokens), _ => TreeRows(tokens) };
    }

    /// <summary>The worn items under the folders that hold them, every such folder open (the official viewer's Worn tab, which
    /// shows only folders with something in them). A worn item not held yet, or an unread current outfit, is asked for.</summary>
    private List<InventoryRow> WornRows(List<string> tokens)
    {
        var cof = _model.FolderOfKind(FolderKind.CurrentOutfit);
        if (cof != Guid.Empty && _model.LoadState(cof) == FolderLoad.NotLoaded)
            _model.EnsureLoaded(cof);
        var worn = WornItems?.Invoke() ?? new HashSet<Guid>();
        var shown = new HashSet<Guid>();
        var folders = new HashSet<Guid>();
        bool missing = false;
        foreach (var id in worn)
        {
            if (_model.Item(id) is not { } item)
            {
                missing = true;
                continue;
            }
            if (tokens.Count > 0 && !Matches(item.Name, tokens))
                continue;
            shown.Add(id);
            var parent = item.ParentId;
            for (int guard = 0; parent != Guid.Empty && guard < 64 && folders.Add(parent); guard++)
                parent = _model.Folder(parent)?.ParentId ?? Guid.Empty;
        }
        if (missing)
            _model.EnsureAllLoaded();
        var rows = new List<InventoryRow>();
        foreach (var root in new[] { _model.RootId, _model.LibraryRootId })
            if (folders.Contains(root))
                AddWornFolder(rows, root, 0, folders, shown);
        return rows;
    }

    private void AddWornFolder(List<InventoryRow> rows, Guid id, int depth, HashSet<Guid> folders, HashSet<Guid> shown)
    {
        if (_model.Folder(id) is not { } f)
            return;
        string name = id == _model.RootId ? _myInventoryName : id == _model.LibraryRootId ? _libraryName : f.Name;
        rows.Add(new InventoryRow(id, true, name, InventoryIcons.ForFolder(f.Kind, true), depth, true, true, FolderLoad.Loaded, f.Kind, ItemKind.Other, null, false,
            f.Kind == FolderKind.Trash, _model.IsSystemFolder(id), _model.IsInLibrary(id)));
        foreach (var sub in OrderedFolders(_model.ChildFolders(id)))
            if (folders.Contains(sub.Id))
                AddWornFolder(rows, sub.Id, depth + 1, folders, shown);
        foreach (var item in OrderedItems(_model.ChildItems(id)))
            if (shown.Contains(item.Id))
                rows.Add(ItemRow(item, depth + 1));
    }

    private InventoryStatus WornStatus()
    {
        var cof = _model.FolderOfKind(FolderKind.CurrentOutfit);
        var worn = WornItems?.Invoke() ?? new HashSet<Guid>();
        int held = worn.Count(id => _model.Item(id) is not null);
        bool pending = (cof != Guid.Empty && _model.LoadState(cof) != FolderLoad.Loaded)
            || (held < worn.Count && (_model.UnloadedFolderCount > 0 || _model.InFlight > 0));
        return pending
            ? new("inventory.status.worn_loading", Array.Empty<object>())
            : new("inventory.status.worn", new object[] { new Counted("count.items", held) });
    }

    private List<InventoryRow> TreeRows(List<string> tokens)
    {
        var rows = new List<InventoryRow>();
        var memo = new Dictionary<Guid, bool>();
        foreach (var root in new[] { _model.RootId, _model.LibraryRootId })
        {
            if (root == Guid.Empty || (tokens.Count > 0 && !Reaches(root, tokens, memo)))
                continue;
            AddFolder(rows, root, 0, tokens, memo);
        }
        return rows;
    }

    private void AddFolder(List<InventoryRow> rows, Guid id, int depth, List<string> tokens, Dictionary<Guid, bool> memo)
    {
        var f = _model.Folder(id);
        if (f is null)
            return;
        bool searching = tokens.Count > 0;
        bool open = searching || IsExpanded(id);
        var subfolders = _model.ChildFolders(id);
        var items = _model.ChildItems(id);
        var load = _model.LoadState(id);
        if (load == FolderLoad.Loading && _model.WasLoaded(id))
            load = FolderLoad.Loaded;   // read again: it still shows what it had, so the line does not flicker to "loading" and back
        string name = id == _model.RootId ? _myInventoryName : id == _model.LibraryRootId ? _libraryName : f.Name;
        rows.Add(new InventoryRow(id, true, name, InventoryIcons.ForFolder(f.Kind, open), depth, open,
            load != FolderLoad.Loaded || subfolders.Count + items.Count > 0, load, f.Kind, ItemKind.Other, null, false,
            f.Kind == FolderKind.Trash, _model.IsSystemFolder(id), _model.IsInLibrary(id)));
        if (!open)
            return;
        foreach (var sub in OrderedFolders(subfolders))
            if (!searching || Reaches(sub.Id, tokens, memo))
                AddFolder(rows, sub.Id, depth + 1, tokens, memo);
        foreach (var item in OrderedItems(items))
            if (!searching || Matches(item.Name, tokens))
                rows.Add(ItemRow(item, depth + 1));
    }

    private InventoryRow ItemRow(InventoryItemData item, int depth)
    {
        bool link = item.Kind is ItemKind.Link or ItemKind.LinkFolder || item.LinkedId.HasValue;
        return new InventoryRow(item.Id, false, item.Name, InventoryIcons.ForItem(item.Kind, link), depth, false, false, FolderLoad.Loaded,
            FolderKind.None, item.Kind, item.CreatedAt, link, false, false, _model.IsInLibrary(item.ParentId));
    }

    private List<InventoryRow> RecentRows(List<string> tokens)
    {
        // every item held of the agent's own inventory, the hypergrid suitcase's included, by the official viewer's Recent filter (since the
        // last logout by default, with no count limit; with no stored logout time everything passes, as the official stored time starts at 0)
        var now = Now();
        var own = _model.AllItems()
            .Where(i => !_model.IsInLibrary(i.ParentId) && (tokens.Count == 0 || Matches(i.Name, tokens)))
            .Where(i => Recent.Passes(i.CreatedAt, i.Kind, now, RecentSince));
        return own.OrderByDescending(i => i.CreatedAt ?? DateTimeOffset.MinValue).ThenBy(i => i.Name, NameOrder).Select(i => ItemRow(i, 0)).ToList();
    }

    // ---------------- order ----------------

    private static readonly StringComparer NameOrder = StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, ignoreCase: true);

    /// <summary>System folders, then the Trash, then the rest, each group by name.</summary>
    private IEnumerable<InventoryFolderData> OrderedFolders(IReadOnlyList<InventoryFolderData> folders) =>
        folders.OrderBy(f => f.Kind == FolderKind.Trash ? 1 : _model.IsSystemFolder(f.Id) ? 0 : 2).ThenBy(f => f.Name, NameOrder);

    private IEnumerable<InventoryItemData> OrderedItems(IReadOnlyList<InventoryItemData> items) => Sort == InventorySort.ByDate
        ? items.OrderByDescending(i => i.CreatedAt ?? DateTimeOffset.MinValue).ThenBy(i => i.Name, NameOrder)
        : items.OrderBy(i => i.Name, NameOrder).ThenByDescending(i => i.CreatedAt ?? DateTimeOffset.MinValue);

    // ---------------- search ----------------

    /// <summary>The parts a name must contain: the text with leading space dropped, split at '+'. Empty when there is no search.</summary>
    private static List<string> Tokens(string? text)
    {
        string t = (text ?? "").TrimStart();
        if (t.Length == 0)
            return new List<string>();
        return t.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    private static bool Matches(string name, List<string> tokens) =>
        tokens.All(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the folder's own name matches, or anything loaded below it does.</summary>
    private bool Reaches(Guid folderId, List<string> tokens, Dictionary<Guid, bool> memo)
    {
        if (memo.TryGetValue(folderId, out var known))
            return known;
        memo[folderId] = false;   // a loop in the data cannot recurse forever
        var f = _model.Folder(folderId);
        bool hit = f is not null && folderId != _model.RootId && folderId != _model.LibraryRootId && Matches(f.Name, tokens);
        hit = hit || _model.ChildItems(folderId).Any(i => Matches(i.Name, tokens)) || _model.ChildFolders(folderId).Any(s => Reaches(s.Id, tokens, memo));
        memo[folderId] = hit;
        return hit;
    }

    // ---------------- status ----------------

    public InventoryStatus Status()
    {
        if (!_model.HasSkeleton)
            return new("inventory.status.waiting", Array.Empty<object>());
        if (Tab == InventoryTab.Worn)
            return WornStatus();
        int items = _model.AllItems().Count, folders = _model.AllFolders().Count;
        bool searching = Tokens(SearchText).Count > 0;
        if (searching)
        {
            if (_model.UnloadedFolderCount > 0)
                return new("inventory.status.searching", new object[] { new Counted("count.folders", _model.UnloadedFolderCount) });
            return new("inventory.status.matches", new object[] { new Counted("count.matches", Rows().Count(r => !r.IsFolder)) });
        }
        if (_model.InFlight > 0)
            return new("inventory.status.fetching", new object[] { new Counted("count.items", items), new Counted("count.folders", folders) });
        return _model.UnloadedFolderCount == 0
            ? new("inventory.status.complete", new object[] { new Counted("count.items", items), new Counted("count.folders", folders) })
            : new("inventory.status.partial", new object[] { new Counted("count.items", items), new Counted("count.folders", folders) });
    }
}
