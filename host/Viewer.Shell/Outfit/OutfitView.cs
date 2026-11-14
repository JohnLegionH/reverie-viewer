// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;

namespace Viewer.Shell.Outfit;

/// <summary>
/// What the outfit window lists, without the engine: the current outfit (attachments, body parts, then clothing by type with
/// the highest layer first, as the official viewer groups them), the saved outfits under My Outfits by name, and the outfit the
/// current look was taken from (the current outfit folder's link to an outfit folder). Reading asks for the folders it needs.
/// </summary>
public sealed class OutfitView
{
    private readonly InventoryModel _model;

    public OutfitView(InventoryModel model) => _model = model;

    /// <summary>Asks for the current outfit folder and My Outfits when they are not here yet. True when both are loaded.</summary>
    public bool Prepare()
    {
        bool ready = true;
        foreach (var kind in new[] { FolderKind.CurrentOutfit, FolderKind.MyOutfits })
        {
            var id = _model.FolderOfKind(kind);
            if (id == Guid.Empty)
                continue;
            if (_model.LoadState(id) != FolderLoad.Loaded)
            {
                ready = false;
                if (!_model.EnsureLoaded(id))
                    _model.Retry(id);
            }
        }
        return ready && _model.FolderOfKind(FolderKind.CurrentOutfit) != Guid.Empty;
    }

    public IReadOnlyList<OutfitEntry> Current() => OutfitPlanner.Current(_model);

    /// <summary>The saved outfits (folders of the outfit kind under My Outfits), by name.</summary>
    public IReadOnlyList<InventoryFolderData> Saved()
    {
        var mine = _model.FolderOfKind(FolderKind.MyOutfits);
        return mine == Guid.Empty
            ? Array.Empty<InventoryFolderData>()
            : _model.ChildFolders(mine).Where(f => f.Kind == FolderKind.Outfit).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The name of the outfit the current look came from, or null.</summary>
    public string? BaseOutfitName()
    {
        var cof = _model.FolderOfKind(FolderKind.CurrentOutfit);
        if (cof == Guid.Empty)
            return null;
        var link = _model.ChildItems(cof).FirstOrDefault(i => i.Kind == ItemKind.LinkFolder && i.LinkedId is not null);
        return link?.LinkedId is { } id ? _model.Folder(id)?.Name ?? link.Name : null;
    }
}
