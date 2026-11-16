// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Grids;

/// <summary>
/// The names saved for each grid (several per grid, the latest first), kept in the viewer's own settings folder
/// (saved-names.json). Forgetting a name also forgets its saved password for that grid. Firestorm keeps one credential per
/// name and grid and removes the saved password with it (fspanellogin.cpp, onRemoveCallback).
/// </summary>
public sealed class SavedNames
{
    public const string FileName = "saved-names.json";

    public SavedNames(string? settingsDir) { }

    /// <summary>The names saved for the grid at this address, the latest first.</summary>
    public IReadOnlyList<string> Names(string gridAddress) => [];

    /// <summary>Saves the name for the grid (moved to the front when it is already saved).</summary>
    public void Remember(string gridAddress, string name) { }

    /// <summary>Forgets the name for the grid, and its saved password in <paramref name="vault"/>. True when it was saved.</summary>
    public bool Forget(string gridAddress, string name, ILoginVault? vault) => false;

    /// <summary>Forgets every saved name on every grid. True when there was one.</summary>
    public bool ClearAll() => false;
}
