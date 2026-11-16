// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;

namespace Viewer.Shell.Grids;

/// <summary>
/// The names saved for each grid (several per grid, the latest first), kept in the viewer's own settings folder
/// (saved-names.json). Only names are kept here, never a password: a remembered password stays in the protected vault.
/// Forgetting a name also forgets its saved password for that grid. Firestorm keeps one credential per name and grid and removes
/// the saved password with it (fspanellogin.cpp, onRemoveCallback).
/// </summary>
public sealed class SavedNames
{
    public const string FileName = "saved-names.json";

    /// <summary>The most names kept for one grid.</summary>
    public const int MaxPerGrid = 20;

    private readonly string? _dir;
    private readonly Dictionary<string, List<string>> _byGrid = new(StringComparer.Ordinal);

    public SavedNames(string? settingsDir)
    {
        _dir = settingsDir;
        if (_dir is null)
            return;
        try
        {
            string path = Path.Combine(_dir, FileName);
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path)) is { } read)
                foreach (var (grid, names) in read)
                    if (grid.Length > 0 && names is not null)
                        _byGrid[grid] = names.Where(n => !string.IsNullOrWhiteSpace(n)).Take(MaxPerGrid).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _byGrid.Clear(); }
    }

    /// <summary>The names saved for the grid at this address, the latest first.</summary>
    public IReadOnlyList<string> Names(string gridAddress) =>
        _byGrid.TryGetValue(GridList.KeyOf(gridAddress), out var names) ? names.ToList() : [];

    /// <summary>Saves the name for the grid (moved to the front when it is already saved).</summary>
    public void Remember(string gridAddress, string name)
    {
        string key = GridList.KeyOf(gridAddress), tidy = Tidy(name);
        if (key.Length == 0 || tidy.Length == 0)
            return;
        var names = _byGrid.TryGetValue(key, out var had) ? had : _byGrid[key] = [];
        names.RemoveAll(n => Same(n, tidy));
        names.Insert(0, tidy);
        if (names.Count > MaxPerGrid)
            names.RemoveRange(MaxPerGrid, names.Count - MaxPerGrid);
        Save();
    }

    /// <summary>Forgets the name for the grid, and its saved password in <paramref name="vault"/>. True when it was saved.</summary>
    public bool Forget(string gridAddress, string name, ILoginVault? vault)
    {
        string key = GridList.KeyOf(gridAddress);
        if (!_byGrid.TryGetValue(key, out var names) || names.RemoveAll(n => Same(n, name)) == 0)
            return false;
        if (names.Count == 0)
            _byGrid.Remove(key);
        vault?.Forget(gridAddress, Tidy(name));
        Save();
        return true;
    }

    /// <summary>Forgets every saved name on every grid. True when there was one.</summary>
    public bool ClearAll()
    {
        bool had = _byGrid.Values.Any(n => n.Count > 0);
        _byGrid.Clear();
        Save();
        return had;
    }

    private static string Tidy(string name) => string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool Same(string a, string b) => string.Equals(Tidy(a), Tidy(b), StringComparison.OrdinalIgnoreCase);

    private void Save()
    {
        if (_dir is null)
            return;
        try
        {
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, FileName), next = path + ".next";
            File.WriteAllText(next, JsonSerializer.Serialize(_byGrid));
            File.Move(next, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* kept for this run only */ }
    }
}
