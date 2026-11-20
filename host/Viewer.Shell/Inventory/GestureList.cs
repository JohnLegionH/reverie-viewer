// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;

namespace Viewer.Shell.Inventory;

/// <summary>One row of the Gestures window: the gesture's inventory item, its name, its chat trigger and its key (both shown only once
/// an active gesture's file is read), and whether it is active.</summary>
public sealed record GestureListRow(Guid ItemId, string Name, string Trigger, string Shortcut, bool Active);

/// <summary>The Gestures window's columns, which a click on a header sorts by.</summary>
public enum GestureColumn { Name, Trigger, Key }

/// <summary>What the activate button does for the selection.</summary>
public enum GestureToggle { None, Activate, Deactivate }

/// <summary>Which of the Gestures window's buttons work for the selection.</summary>
public sealed record GestureListButtons(GestureToggle Toggle, bool Edit, bool Play, bool Stop = false, bool Delete = false);

/// <summary>
/// The Gestures window's rows, built as the official window builds them (llfloatergesture.cpp buildGestureList and addGesture): every
/// active gesture, then every gesture item in the Gestures folder and its subfolders (links and the Trash left out) that is not active.
/// An active gesture whose file has been read shows its chat trigger and key; any other shows no trigger and "---" for its key. An active
/// gesture the inventory does not hold yet shows as loading. Rows are sorted by name, as the window sorts on opening (postBuild,
/// sortByColumn "name" ascending).
/// </summary>
public static class GestureListRows
{
    /// <summary>The key column of a gesture with no key, or whose file is not read (addGesture: "---").</summary>
    public const string NoShortcut = "---";

    private const uint MaskAlt = 0x0002;   // indra_constants.h MASK_ALT

    public static IReadOnlyList<GestureListRow> Build(InventoryModel inventory, IReadOnlyCollection<Guid> active, Func<Guid, GestureDocument?> loaded, string loadingName)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(loaded);
        var rows = new Dictionary<Guid, GestureListRow>();
        foreach (var id in active)
        {
            string name = inventory.Item(id)?.Name ?? loadingName;
            rows[id] = loaded(id) is { } doc
                ? new GestureListRow(id, name, doc.Trigger, Shortcut(doc.Key, doc.Mask), true)
                : new GestureListRow(id, name, "", NoShortcut, true);
        }
        Guid folder = inventory.FolderOfKind(FolderKind.Gesture);
        Guid trash = inventory.FolderOfKind(FolderKind.Trash);
        if (folder != Guid.Empty)
            foreach (var item in inventory.AllItems())
            {
                // LLIsType(AT_GESTURE) with EXCLUDE_TRASH: a link is not a gesture asset, and the Trash is left out
                if (item.Kind != ItemKind.Gesture || item.LinkedId is not null || rows.ContainsKey(item.Id))
                    continue;
                if (!inventory.IsDescendantOf(item.Id, folder) || trash != Guid.Empty && inventory.IsDescendantOf(item.Id, trash))
                    continue;
                rows[item.Id] = new GestureListRow(item.Id, item.Name, "", NoShortcut, false);
            }
        return rows.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ItemId).ToArray();
    }

    /// <summary>A gesture's key as the official accelerator text (llkeyboard.cpp stringFromAccelerator: "Ctrl+", "Alt+", "Shift+" in
    /// that order, then the key); "---" with no key.</summary>
    public static string Shortcut(byte key, uint mask)
    {
        string name = GestureKeys.Label(key);
        if (key == GestureKeys.None || name.Length == 0)
            return NoShortcut;
        return ((mask & GestureKeys.MaskControl) != 0 ? "Ctrl+" : "") + ((mask & MaskAlt) != 0 ? "Alt+" : "")
            + ((mask & GestureKeys.MaskShift) != 0 ? "Shift+" : "") + name;
    }
}

/// <summary>
/// The Gestures window's rows and choice, as the official window keeps them (llfloatergesture.cpp): the list allows several rows chosen
/// (floater_gesture.xml multi_select); the first row is chosen when the list fills and a chosen row stays chosen when it is rebuilt
/// (postBuild selectFirstItem, refreshAll). The current row (the last one clicked) is the one Edit and Play act on (onClickEdit and
/// onClickPlay read getCurrentID). Stop works while a chosen gesture plays, Delete on any chosen row (it asks first). A click on a
/// column header sorts by it. The activate button activates the choice unless all of it is active, when it deactivates all of it
/// (onActivateBtnClick, is_mixed); Edit needs exactly one row chosen (isActionEnabled "edit_gesture").
/// </summary>
public sealed class GestureList
{
    /// <summary>Whether a delete question still holds after the list was filled again: every gesture it names is still listed.</summary>
    public static bool PendingStillValid(IReadOnlyList<Guid> pending, IReadOnlyList<GestureListRow> rows) =>
        pending.All(id => rows.Any(r => r.ItemId == id));

    private readonly List<Guid> _selected = new();

    public IReadOnlyList<GestureListRow> Rows { get; private set; } = [];

    /// <summary>The chosen rows' items, in the order they were chosen.</summary>
    public IReadOnlyCollection<Guid> Selected => _selected.ToArray();

    /// <summary>The row last chosen, or empty.</summary>
    public Guid Current => _selected.Count > 0 ? _selected[^1] : Guid.Empty;

    /// <summary>The column the rows are sorted by (the window sorts by name when it opens: postBuild sortByColumn "name").</summary>
    public GestureColumn SortColumn { get; private set; } = GestureColumn.Name;

    public bool SortAscending { get; private set; } = true;

    private readonly HashSet<Guid> _playing = new();

    /// <summary>The gestures playing now.</summary>
    public IReadOnlyCollection<Guid> Playing => _playing.ToArray();

    /// <summary>A click on a column header: the same column again reverses the order, another column sorts by it, ascending (the
    /// official list's sort-on-header). The name breaks ties, so equal triggers keep a steady order.</summary>
    public void SortBy(GestureColumn column)
    {
        if (column == SortColumn)
            SortAscending = !SortAscending;
        else
        {
            SortColumn = column;
            SortAscending = true;
        }
        Rows = Sorted(Rows);
    }

    private IReadOnlyList<GestureListRow> Sorted(IEnumerable<GestureListRow> rows)
    {
        string Key(GestureListRow r) => SortColumn switch { GestureColumn.Trigger => r.Trigger, GestureColumn.Key => r.Shortcut, _ => r.Name };
        var primary = SortAscending ? rows.OrderBy(Key, StringComparer.OrdinalIgnoreCase) : rows.OrderByDescending(Key, StringComparer.OrdinalIgnoreCase);
        // ties: the name, ascending, then the item (a steady order); with the name column itself the item breaks the tie in the same direction
        return (SortColumn == GestureColumn.Name ? primary.ThenBy(r => r.ItemId) : primary.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ItemId)).ToArray();
    }

    /// <summary>Tells the list which gestures are playing now (the Stop button works on those).</summary>
    public void SetPlaying(IEnumerable<Guid> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _playing.Clear();
        _playing.UnionWith(items);
    }

    /// <summary>The chosen gestures, in the order they were chosen: what Delete takes to the Trash.</summary>
    public IReadOnlyList<Guid> ToDelete() => _selected.ToArray();

    /// <summary>The chosen gestures that are playing: what Stop ends.</summary>
    public IReadOnlyList<Guid> ToStop() => _selected.Where(_playing.Contains).ToArray();

    public void SetRows(IReadOnlyList<GestureListRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Rows = Sorted(rows);
        _selected.RemoveAll(id => !rows.Any(r => r.ItemId == id));
        if (_selected.Count == 0 && Rows.Count > 0)
            _selected.Add(Rows[0].ItemId);
    }

    /// <summary>A click on a row: alone it chooses that row only; with <paramref name="add"/> (Ctrl held) it adds the row to the choice,
    /// or takes it out when it was chosen.</summary>
    public void Click(Guid itemId, bool add)
    {
        if (!Rows.Any(r => r.ItemId == itemId))
            return;
        if (!add)
        {
            _selected.Clear();
            _selected.Add(itemId);
        }
        else if (!_selected.Remove(itemId))
            _selected.Add(itemId);
    }

    private IEnumerable<GestureListRow> Chosen => _selected.Select(id => Rows.First(r => r.ItemId == id));

    public GestureListButtons Buttons
    {
        get
        {
            if (_selected.Count == 0)
                return new GestureListButtons(GestureToggle.None, false, false, false, false);
            var toggle = Chosen.All(r => r.Active) ? GestureToggle.Deactivate : GestureToggle.Activate;
            return new GestureListButtons(toggle, _selected.Count == 1, true, _selected.Any(_playing.Contains), true);
        }
    }

    /// <summary>What the activate button sends: every chosen gesture deactivated when all are active, else every inactive one activated.</summary>
    public IReadOnlyList<(Guid ItemId, bool Activate)> Toggle() =>
        Buttons.Toggle switch
        {
            GestureToggle.Deactivate => Chosen.Select(r => (r.ItemId, false)).ToArray(),
            GestureToggle.Activate => Chosen.Where(r => !r.Active).Select(r => (r.ItemId, true)).ToArray(),
            _ => [],
        };
}
