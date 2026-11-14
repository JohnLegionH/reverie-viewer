// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Overrider;

namespace Viewer.Shell.Overrider;

/// <summary>
/// The session's inventory as the animation overrider reads it. Nothing is changed in the inventory. A link's animation
/// is known once the folder the animation itself lies in has been fetched; when a link is met whose animation is not
/// known, the whole inventory is asked for once, so that the link can lead somewhere without the user opening folders.
/// </summary>
public sealed class OverriderInventory(InventoryModel model) : IAoInventory
{
    /// <summary>The protocol's inventory type number of an animation.</summary>
    private const int AnimationInventoryType = 19;

    private readonly InventoryModel _model = model;
    private bool _askedForAll;

    public Guid RootId => _model.HasSkeleton ? _model.RootId : Guid.Empty;

    public bool IsComplete(Guid folder) => _model.LoadState(folder) == FolderLoad.Loaded;

    public void Fetch(Guid folder)
    {
        if (!_model.EnsureLoaded(folder) && _model.LoadState(folder) == FolderLoad.Failed)
            _model.Retry(folder);
    }

    public IReadOnlyList<AoFolder> Folders(Guid folder) => _model.ChildFolders(folder).Select(f => new AoFolder(f.Id, f.Name)).ToArray();

    public IReadOnlyList<AoItem> Items(Guid folder) => _model.ChildItems(folder).Select(ToItem).ToArray();

    public Guid AssetOf(Guid linkItemId) => _model.Item(linkItemId) is { } link ? AssetOf(link) : Guid.Empty;

    private AoItem ToItem(InventoryItemData item)
    {
        bool link = item.Kind == ItemKind.Link;
        var target = link && item.LinkedId is { } id ? _model.Item(id) : null;
        bool animation = link
            ? item.InventoryType == AnimationInventoryType || target?.Kind == ItemKind.Animation
            : item.Kind == ItemKind.Animation;
        return new AoItem(item.Id, item.Name, item.Description, link, animation, item.LinkedId ?? Guid.Empty, AssetOf(item));
    }

    private Guid AssetOf(InventoryItemData item)
    {
        if (item.Kind != ItemKind.Link)
            return item.AssetId ?? Guid.Empty;
        if (item.LinkedId is not { } id)
            return Guid.Empty;
        if (_model.Item(id) is { } target)
            return target.AssetId ?? Guid.Empty;
        if (!_askedForAll)
        {
            _askedForAll = true;
            _model.EnsureAllLoaded();
        }
        return Guid.Empty;
    }
}
