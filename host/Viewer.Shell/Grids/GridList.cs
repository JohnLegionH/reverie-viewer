// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Grids;

/// <summary>One grid on the list: the address the person added it by, the name shown, and what its grid-info document said.</summary>
public sealed record GridEntry(string Address, string Name, GridInfo? Info, bool NameEdited = false, DateTimeOffset? Refreshed = null)
{
    /// <summary>The key the list knows the grid by: host, port and path of its address, without get_grid_info, in lower case.</summary>
    public string Key => "";
}

/// <summary>
/// The grids the person has added, kept in the viewer's own settings folder (grids.json), and which one is chosen. No grid ships
/// with the viewer: the list starts empty. Firestorm keeps its user grids the same way (fsgridhandler.cpp, grids.user.xml).
/// </summary>
public sealed class GridList
{
    public const string FileName = "grids.json";

    public GridList(string? settingsDir) { }

    public IReadOnlyList<GridEntry> Entries => [];

    /// <summary>The chosen grid, or null when none is.</summary>
    public GridEntry? Selected => null;

    /// <summary>Adds a grid, or replaces the one with the same key, saves the list and chooses it.</summary>
    public GridEntry Add(GridEntry entry) => entry;

    /// <summary>Takes the grid off the list (its saved names and passwords are not touched) and saves.</summary>
    public bool Remove(string key) => false;

    /// <summary>Chooses the grid with this key. False when the list has no such grid.</summary>
    public bool Select(string key) => false;

    /// <summary>The grid with this address's key, or null.</summary>
    public GridEntry? Find(string address) => null;

    /// <summary>--grid=&lt;address&gt; on the command line: adds the grid when it is new and chooses it. Null when the option is not an address.</summary>
    public GridEntry? ApplyCommandLine(string? gridOption) => null;

    /// <summary>The key of an address (host, port and path, without get_grid_info, lower case); empty when it is not an address.</summary>
    public static string KeyOf(string address) => "";
}
