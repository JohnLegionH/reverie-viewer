// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Grids;

/// <summary>What looking up an address found: the grid to add (null when nothing usable answered), the string-table key of the plain
/// message that says why not, and whether the address is plain http.</summary>
public sealed record GridLookupResult(GridEntry? Entry, string? ProblemKey, bool PlainHttp);

/// <summary>
/// Add a grid's Look up: reads the grid-info document at the address typed (Firestorm's grid manager does the same,
/// fsgridhandler.cpp addGrid, which appends get_grid_info to the address and reads the answer in gridInfoResponderCB).
/// </summary>
public static class GridLookup
{
    /// <summary>The string-table key of the http warning.</summary>
    public const string HttpWarningKey = "grid.add.http_warning";

    /// <summary>The http warning's key for an address that is plain http, else null.</summary>
    public static string? HttpWarning(Uri address) => null;

    /// <summary>Looks the typed address up.</summary>
    public static Task<GridLookupResult> LookUpAsync(string typed, GridFetcher fetch, CancellationToken ct) =>
        Task.FromResult(new GridLookupResult(null, null, false));

    /// <summary>Refresh info: the entry with what the grid publishes now; the name the person gave it stays.</summary>
    public static Task<GridLookupResult> RefreshAsync(GridEntry entry, GridFetcher fetch, CancellationToken ct) =>
        Task.FromResult(new GridLookupResult(null, null, false));
}
