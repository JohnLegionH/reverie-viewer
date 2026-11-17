// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Grids;

/// <summary>What looking up an address found: the grid to add (null when nothing usable answered), the string-table key of the plain
/// message that says why not, whether the address is plain http, and the server certificate that was refused when that is why.</summary>
public sealed record GridLookupResult(GridEntry? Entry, string? ProblemKey, bool PlainHttp, Viewer.Core.Net.CertificateRefusal? Certificate = null);

/// <summary>
/// Add a grid's Look up: reads the grid-info document at the address typed (Firestorm's grid manager does the same,
/// fsgridhandler.cpp addGrid, which appends get_grid_info to the address and reads the answer in gridInfoResponderCB). Only the
/// address typed is asked; certificates are checked by the fetcher, with the person's pinned choices as for every connection.
/// </summary>
public static class GridLookup
{
    /// <summary>The string-table key of the http warning.</summary>
    public const string HttpWarningKey = "grid.add.http_warning";

    /// <summary>The http warning's key for an address that is plain http, else null.</summary>
    public static string? HttpWarning(Uri address) => address.Scheme == Uri.UriSchemeHttp ? HttpWarningKey : null;

    /// <summary>Looks the typed address up.</summary>
    public static async Task<GridLookupResult> LookUpAsync(string typed, GridFetcher fetch, CancellationToken ct)
    {
        var tidy = GridAddress.Tidy(typed);
        if (tidy.Uri is not { } uri)
            return new(null, tidy.ProblemKey, false);
        // Second Life serves no grid-info document: its address is the built-in entry, asked nothing
        if (Viewer.Core.Session.GridPolicy.KindOf(uri.ToString()) == Viewer.Core.Session.GridKind.SecondLife)
            return GridList.KeyOf(uri.ToString()) == GridList.SecondLife.Key ? new(GridList.SecondLife, null, false) : new(null, "grid.add.not_grid", false);
        bool http = HttpWarning(uri) is not null;
        // the address without get_grid_info is the one kept: the list knows the grid by it
        string address = GridAddress.GridInfoOf(uri) == uri ? BaseOf(uri) : uri.ToString();
        var got = await fetch(GridAddress.GridInfoOf(uri), ct).ConfigureAwait(false);
        if (got.Status is null)
            return new(null, "grid.add.no_answer", http);
        if (got.Status is < 200 or >= 300 || GridInfo.Parse(got.Body) is not { } info)
            return new(null, "grid.add.not_grid", http);
        var name = info.Name ?? GridEntry.HostName(uri);
        return new(new GridEntry(address, name, info, false, DateTimeOffset.UtcNow), null, http);
    }

    /// <summary>Refresh info: the entry with what the grid publishes now; the name the person gave it stays.</summary>
    public static async Task<GridLookupResult> RefreshAsync(GridEntry entry, GridFetcher fetch, CancellationToken ct)
    {
        if (entry.BuiltIn)
            return new(entry, null, false);
        var found = await LookUpAsync(entry.Address, fetch, ct).ConfigureAwait(false);
        if (found.Entry is not { } fresh)
            return found;
        return found with { Entry = fresh with { Address = entry.Address, Name = entry.NameEdited ? entry.Name : fresh.Name, NameEdited = entry.NameEdited } };
    }

    private static string BaseOf(Uri gridInfo)
    {
        var b = new UriBuilder(gridInfo) { Query = "", Fragment = "" };
        string path = b.Path.TrimEnd('/');
        b.Path = path[..^"get_grid_info".Length];
        return b.Uri.ToString();
    }
}
