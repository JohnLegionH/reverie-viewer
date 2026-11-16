// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viewer.Shell.Grids;

/// <summary>One grid on the list: the address the person added it by, the name shown, and what its grid-info document said.</summary>
public sealed record GridEntry(string Address, string Name, GridInfo? Info, bool NameEdited = false, DateTimeOffset? Refreshed = null)
{
    /// <summary>The key the list knows the grid by: host, port and path of its address, without get_grid_info, in lower case.</summary>
    [JsonIgnore]
    public string Key => GridList.KeyOf(Address);

    /// <summary>Whether the entry comes with the viewer (Second Life): never saved to the list file, edited or removed.</summary>
    [JsonIgnore]
    public bool BuiltIn { get; init; }

    /// <summary>The name a grid has before its grid info is read: its host, with the port when it is not the scheme's own.</summary>
    public static string HostName(Uri address) => address.IsDefaultPort ? address.Host : address.Host + ":" + address.Port;
}

/// <summary>
/// The grids the person has added, kept in the viewer's own settings folder (grids.json), and which one is chosen. The person's
/// list starts empty; one grid comes with the viewer, Second Life's main grid, which is never written to the file, edited or
/// removed. Firestorm keeps its user grids the same way (fsgridhandler.cpp, grids.user.xml, written by saveGridList). A list file
/// that cannot be read is taken as an empty list, never as a failure.
/// </summary>
public sealed class GridList
{
    public const string FileName = "grids.json";

    /// <summary>Second Life's main grid login address, as the official viewer has it (llviewernetwork.cpp, MAIN_GRID_LOGIN_URI).
    /// Second Life serves no grid-info document, so the entry carries no grid info and none is asked for.</summary>
    public const string SecondLifeLoginUri = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";

    /// <summary>The built-in Second Life entry.</summary>
    public static GridEntry SecondLife { get; } = new(SecondLifeLoginUri, "Second Life", null) { BuiltIn = true };

    private static readonly GridEntry[] BuiltInEntries = [SecondLife];

    private readonly string? _dir;
    private readonly List<GridEntry> _entries = [];
    private string? _selected;

    private sealed record Stored(string? Selected, List<GridEntry>? Grids);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public GridList(string? settingsDir)
    {
        _dir = settingsDir;
        if (_dir is null)
            return;
        string path = Path.Combine(_dir, FileName);
        try
        {
            if (File.Exists(path))
            {
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json);
                foreach (var e in stored?.Grids ?? [])
                    if (e is { Address: not null, Name: not null } && e.Key.Length > 0 && !IsSecondLifeAddress(e.Address) && _entries.All(x => x.Key != e.Key))
                        _entries.Add(e);
                _selected = _entries.Any(e => e.Key == stored?.Selected) || BuiltInOf(stored?.Selected ?? "") is not null ? stored!.Selected : _entries.FirstOrDefault()?.Key;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _entries.Clear();
            _selected = null;
            return;
        }
        // an earlier version kept only the last address that logged in: it becomes the first grid on the list
        if (LoginAddressStore.Load(_dir) is { } last && GridAddress.Tidy(last).Uri is { } uri)
            Add(new GridEntry(uri.ToString(), GridEntry.HostName(uri), null));
    }

    public IReadOnlyList<GridEntry> Entries => _entries;

    /// <summary>The grids that come with the viewer.</summary>
    public IReadOnlyList<GridEntry> BuiltIn => BuiltInEntries;

    /// <summary>The person's grids, then the ones that come with the viewer: what the grid chooser shows.</summary>
    public IReadOnlyList<GridEntry> All => [.. _entries, .. BuiltInEntries];

    private static bool IsSecondLifeAddress(string address) =>
        Viewer.Core.Session.GridPolicy.KindOf(address) == Viewer.Core.Session.GridKind.SecondLife;

    private static GridEntry? BuiltInOf(string key) => key.Length == 0 ? null : Array.Find(BuiltInEntries, e => e.Key == key);

    /// <summary>The chosen grid, or null when none is.</summary>
    public GridEntry? Selected => _entries.FirstOrDefault(e => e.Key == _selected) ?? BuiltInOf(_selected ?? "");

    /// <summary>Adds a grid, or replaces the one with the same key, saves the list and chooses it.</summary>
    public GridEntry Add(GridEntry entry)
    {
        if (IsSecondLifeAddress(entry.Address))
        {
            // only the built-in entry stands for Second Life (its main grid, over https): adding any Linden Lab address chooses it
            Select(SecondLife.Key);
            return SecondLife;
        }
        int at = _entries.FindIndex(e => e.Key == entry.Key);
        if (at >= 0)
            _entries[at] = entry;
        else
            _entries.Add(entry);
        _selected = entry.Key;
        Save();
        return entry;
    }

    /// <summary>Takes the grid off the list (its saved names and passwords are not touched) and saves.</summary>
    public bool Remove(string key)
    {
        if (_entries.RemoveAll(e => e.Key == key) == 0)
            return false;
        if (_selected == key)
            _selected = _entries.FirstOrDefault()?.Key;
        Save();
        return true;
    }

    /// <summary>Chooses the grid with this key. False when the list has no such grid.</summary>
    public bool Select(string key)
    {
        if (_entries.All(e => e.Key != key) && BuiltInOf(key) is null)
            return false;
        _selected = key;
        Save();
        return true;
    }

    /// <summary>The grid with this address's key, or null.</summary>
    public GridEntry? Find(string address)
    {
        string key = KeyOf(address);
        return key.Length == 0 ? null : _entries.FirstOrDefault(e => e.Key == key) ?? BuiltInOf(key);
    }

    /// <summary>--grid=&lt;address&gt; on the command line: adds the grid when it is new and chooses it. Null when the option is not an address.</summary>
    public GridEntry? ApplyCommandLine(string? gridOption)
    {
        if (string.IsNullOrWhiteSpace(gridOption) || GridAddress.Tidy(gridOption).Uri is not { } uri)
            return null;
        if (IsSecondLifeAddress(uri.ToString()) && KeyOf(uri.ToString()) != SecondLife.Key)
            return null;   // another Linden Lab address is not a grid of its own: the list has Second Life's main grid built in
        if (Find(uri.ToString()) is { } known)
        {
            Select(known.Key);
            return known;
        }
        return Add(new GridEntry(uri.ToString(), GridEntry.HostName(uri), null));
    }

    /// <summary>The key of an address (host, port and path, without get_grid_info, lower case); empty when it is not an address.</summary>
    public static string KeyOf(string address)
    {
        if (GridAddress.Tidy(address).Uri is not { } uri)
            return "";
        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/get_grid_info", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/get_grid_info".Length];
        return (uri.Host + ":" + uri.Port + path.TrimEnd('/')).ToLowerInvariant();
    }

    private void Save()
    {
        if (_dir is null)
            return;
        try
        {
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, FileName), next = path + ".next";
            // written beside the file and moved over it, so a half-written list is never read
            File.WriteAllText(next, JsonSerializer.Serialize(new Stored(_selected, _entries), Json));
            File.Move(next, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a settings folder that cannot be written keeps the list for this run */ }
    }
}
