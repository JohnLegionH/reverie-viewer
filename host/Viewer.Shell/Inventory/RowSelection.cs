// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Inventory;

/// <summary>
/// How a click with Ctrl or Shift changes the rows chosen in a list, as the official viewer's folder view does (LLFolderView: Ctrl toggles
/// one row in or out of the choice, Shift chooses every row from the one last chosen to the one clicked, in the order they are shown).
/// Pure, so it is tested without the engine; the inventory window's tree feeds it the rows it shows.
/// </summary>
public static class RowSelection
{
    /// <summary>The rows from <paramref name="anchor"/> to <paramref name="target"/> inclusive, in the order shown, whichever comes first.
    /// When the anchor is not among the rows (or is empty) only the target is chosen; when the target is not shown, nothing.</summary>
    public static IReadOnlyList<Guid> Range(IReadOnlyList<Guid> shown, Guid anchor, Guid target)
    {
        int to = IndexOf(shown, target);
        if (to < 0)
            return [];
        int from = anchor == Guid.Empty ? -1 : IndexOf(shown, anchor);
        if (from < 0)
            return [target];
        int first = Math.Min(from, to), last = Math.Max(from, to);
        var rows = new List<Guid>(last - first + 1);
        for (int i = first; i <= last; i++)
            rows.Add(shown[i]);
        return rows;
    }

    /// <summary>The choice after a Ctrl-click on <paramref name="target"/>: it joins the rows chosen, or leaves them when it was already
    /// among them. A choice never becomes empty by this click on its last row (the row stays chosen).</summary>
    public static IReadOnlyList<Guid> Toggle(IReadOnlyList<Guid> chosen, Guid target)
    {
        if (target == Guid.Empty)
            return chosen;
        if (!chosen.Contains(target))
            return [.. chosen, target];
        return chosen.Count <= 1 ? chosen : chosen.Where(id => id != target).ToList();
    }

    private static int IndexOf(IReadOnlyList<Guid> rows, Guid id)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i] == id)
                return i;
        return -1;
    }
}
