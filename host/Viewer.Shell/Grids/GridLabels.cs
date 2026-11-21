// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Shell.Grids;

/// <summary>
/// How a grid's name is shown. A grid supplies its own name, so a grid must not be able to pass itself off as Second Life or as another
/// grid the person has saved: a name that is "Second Life", "SL" or holds "Linden" (any case) is shown under the grid's host unless the
/// login address is Second Life's own, two saved grids with the same name are both shown with their hosts, and the grid picker and the
/// Hypergrid "Visiting" line always show the host beside the name.
/// </summary>
public static class GridLabels
{
    /// <summary>Whether a name could be taken for Second Life's: "Second Life", "SL", or anything holding "Linden", in any case.</summary>
    public static bool ImpersonatesSecondLife(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        string n = name.Trim();
        return n.Equals("Second Life", StringComparison.OrdinalIgnoreCase) || n.Equals("SL", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Linden", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name to show for a grid: its own name, or its host when the name is Second Life's or missing and the grid is not Second
    /// Life (by its login address, <paramref name="loginAddress"/>; null for a grid known only by what a region said).</summary>
    public static string SafeName(string? name, string host, string? loginAddress = null)
    {
        bool own = loginAddress is not null && GridPolicy.KindOf(loginAddress) == GridKind.SecondLife;
        if (string.IsNullOrWhiteSpace(name) || (!own && ImpersonatesSecondLife(name)))
            return host;
        return name.Trim();
    }

    /// <summary>A grid entry's host, with the port when it is not the scheme's own.</summary>
    public static string HostOf(GridEntry entry) =>
        Uri.TryCreate(entry.Address, UriKind.Absolute, out var uri) ? GridEntry.HostName(uri) : entry.Address;

    /// <summary>The name of an entry as shown, safe against posing as Second Life.</summary>
    public static string NameOf(GridEntry entry) => SafeName(entry.Name, HostOf(entry), entry.Address);

    /// <summary>The grid picker's line for an entry: its name and, always, its host.</summary>
    public static string PickerLabel(GridEntry entry)
    {
        string host = HostOf(entry), name = NameOf(entry);
        return name.Equals(host, StringComparison.OrdinalIgnoreCase) ? host : name + " (" + host + ")";
    }

    /// <summary>The name to show where there is room for the grid's name alone (the top bar): the name, with the host added when another
    /// saved grid has the same name.</summary>
    public static string Brand(GridEntry entry, IEnumerable<GridEntry> all)
    {
        string name = NameOf(entry);
        bool shared = all.Any(e => e.Key != entry.Key && NameOf(e).Equals(name, StringComparison.OrdinalIgnoreCase));
        string host = HostOf(entry);
        return shared && !name.Equals(host, StringComparison.OrdinalIgnoreCase) ? name + " (" + host + ")" : name;
    }

    /// <summary>The status line while a region belongs to another grid than the home grid: "Visiting name (host)".</summary>
    public static string VisitingLine(StringTable strings, string? name, string host) =>
        strings.Get("topbar.visiting", SafeName(name, host), host);
}
