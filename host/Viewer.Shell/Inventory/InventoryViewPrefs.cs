// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Inventory;

/// <summary>The inventory window's view options kept between openings and runs (the official viewer keeps its sort in InventorySortOrder:
/// +1 by date, +2 folders always by name, +4 system folders on top).</summary>
public static class InventoryViewPrefs
{
    private const int ByDateBit = 1;

    public static InventorySort Load(SettingsService settings) =>
        (settings.Get(Settings.InventorySortOrder) & ByDateBit) != 0 ? InventorySort.ByDate : InventorySort.ByName;

    /// <summary>Keeps the sort, leaving the other bits of the stored value as they are.</summary>
    public static void Save(SettingsService settings, InventorySort sort)
    {
        int had = settings.Get(Settings.InventorySortOrder);
        settings.Set(Settings.InventorySortOrder, sort == InventorySort.ByDate ? had | ByDateBit : had & ~ByDateBit);
    }
}

/// <summary>Tells whether the inventory tree must be drawn again: only when its rows differ from the rows drawn last. Drawing the tree
/// again clears it, which drops the pointer's place, the scroll position and a click in progress, so a folder read again with the same
/// answer must not cause it.</summary>
public sealed class InventoryTreeRedraw
{
    private IReadOnlyList<InventoryRow>? _drawn;

    public bool Needs(IReadOnlyList<InventoryRow> rows)
    {
        if (_drawn is not null && _drawn.SequenceEqual(rows))
            return false;
        _drawn = rows.ToArray();
        return true;
    }

    /// <summary>Forgets the rows drawn last, so the next picture is drawn whatever it holds.</summary>
    public void Reset() => _drawn = null;
}
