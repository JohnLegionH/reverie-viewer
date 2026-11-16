// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;

namespace Viewer.Shell.Build;

/// <summary>
/// The rows of the Build window's texture and material lists: every item of the kind anywhere in the inventory (the account's and the
/// Library's) with an asset, out of the Trash, by name, narrowed by a search. Every folder not yet loaded is fetched, not only the folder of
/// that kind, since an item can sit in any folder (the official picker fetches the whole inventory before it lists; lltexturectrl.cpp, read
/// as behaviour). No engine types.
/// </summary>
public sealed class InventoryPickList
{
    /// <summary>The most rows listed at once.</summary>
    public const int MaxRows = 500;

    private readonly InventoryModel _model;
    private readonly ItemKind _kind;

    public InventoryPickList(InventoryModel model, ItemKind kind)
    {
        _model = model;
        _kind = kind;
    }

    /// <summary>Asks for every folder not yet loaded (a failed one again); the model keeps only a few requests in flight at once.</summary>
    public void Fetch()
    {
        foreach (var f in _model.AllFolders())
        {
            if (_model.LoadState(f.Id) == FolderLoad.Loaded)
                continue;
            if (!_model.EnsureLoaded(f.Id))
                _model.Retry(f.Id);
        }
    }

    /// <summary>True when every folder of the inventory has been loaded.</summary>
    public bool AllLoaded => _model.AllFolders().All(f => _model.LoadState(f.Id) == FolderLoad.Loaded);

    /// <summary>The items listed for <paramref name="search"/> (a part of the name, any case), by name.</summary>
    public IReadOnlyList<InventoryItemData> Rows(string search)
    {
        string needle = search.Trim();
        var trash = _model.FolderOfKind(FolderKind.Trash);
        return _model.AllItems()
            .Where(i => i.Kind == _kind && i.AssetId is { } a && a != Guid.Empty && (needle.Length == 0 || i.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                && !(trash != Guid.Empty && _model.IsDescendantOf(i.Id, trash)))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Take(MaxRows).ToList();
    }

    /// <summary>The string key of the text shown when the whole inventory holds none of the kind (materials only), or null.</summary>
    public string? EmptyKey => _kind == ItemKind.Material ? "edit.tex.material.none" : null;
}
