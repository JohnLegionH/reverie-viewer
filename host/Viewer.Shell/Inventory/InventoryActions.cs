// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Inventory;

namespace Viewer.Shell.Inventory;

/// <summary>What became of a request to change the inventory: refused here (nothing submitted), or submitted to the pipeline.</summary>
/// <param name="Code">The refusal's code (a string-table suffix) when refused; the pipeline's code otherwise (may be null).</param>
public sealed record InventoryActionResult(bool Submitted, string? Code, ActionResult? Pipeline);

/// <summary>
/// The folder actions of the inventory window: new folder, rename, move (drag and drop) and move to Trash. Each is checked
/// against the rules the official viewer applies before it is offered (system folders are never renamed, moved or trashed;
/// nothing goes into or out of the Library; a folder does not go into itself or its own contents; Lost And Found and Stock
/// folders take no drops; Favorites takes landmarks only), then submitted through the action pipeline, then the affected folders
/// are read again so the grid's answer settles what they hold. Nothing here purges or empties the Trash.
/// </summary>
public sealed class InventoryActions
{
    private readonly InventoryModel _model;
    private readonly Func<ActionIntent, ActionResult> _submit;

    public InventoryActions(InventoryModel model, Func<ActionIntent, ActionResult> submit)
    {
        _model = model;
        _submit = submit;
    }

    // ---------------- the rules ----------------

    /// <summary>The folder a new folder would go in: the selection if a folder, an item's folder, or My Inventory.</summary>
    private Guid ParentFor(Guid selected) =>
        selected == Guid.Empty ? _model.RootId : _model.Folder(selected) is not null ? selected : _model.Item(selected)?.ParentId ?? Guid.Empty;

    public string? WhyNotCreate(Guid selected)
    {
        var parent = ParentFor(selected);
        if (parent == Guid.Empty || _model.Folder(parent) is null)
            return "inventory.unknown";
        return _model.IsInLibrary(parent) ? "inventory.library" : null;
    }

    public string? WhyNotRename(Guid id)
    {
        var folder = _model.Folder(id);
        var item = folder is null ? _model.Item(id) : null;
        if (folder is null && item is null)
            return "inventory.unknown";
        if (_model.IsInLibrary(id))
            return "inventory.library";
        if (folder is not null)
            return _model.IsSystemFolder(id) || folder.Kind == FolderKind.Other ? "inventory.protected" : null;
        return item!.Kind == ItemKind.CallingCard ? "inventory.not_renameable" : null;
    }

    public string? WhyNotMove(Guid id, Guid newParent)
    {
        var folder = _model.Folder(id);
        var item = folder is null ? _model.Item(id) : null;
        if (folder is null && item is null)
            return "inventory.unknown";
        var dest = _model.Folder(newParent);
        if (dest is null)
            return _model.Item(newParent) is not null ? "inventory.bad_destination" : "inventory.unknown";
        if (_model.IsInLibrary(id) || _model.IsInLibrary(newParent))
            return "inventory.library";
        if (dest.Kind is FolderKind.LostAndFound or FolderKind.MarketplaceStock)
            return "inventory.bad_destination";
        if (folder is not null)
        {
            if (id == newParent)
                return "inventory.into_self";
            if (_model.IsSystemFolder(id))
                return "inventory.protected";
            if (_model.IsDescendantOf(newParent, id))
                return "inventory.into_own_child";
            if (folder.ParentId == newParent)
                return "inventory.same_parent";
            if (dest.Kind == FolderKind.Favorites)
                return "inventory.bad_destination";
            return HasSystemFolderInside(id) ? "inventory.protected_inside" : null;
        }
        if (item!.ParentId == newParent)
            return "inventory.same_parent";
        if (dest.Kind == FolderKind.Favorites && item.Kind != ItemKind.Landmark)
            return "inventory.bad_destination";
        return null;
    }

    public string? WhyNotTrash(Guid id)
    {
        var folder = _model.Folder(id);
        var item = folder is null ? _model.Item(id) : null;
        if (folder is null && item is null)
            return "inventory.unknown";
        if (_model.IsInLibrary(id))
            return "inventory.library";
        if (folder is not null && (_model.IsSystemFolder(id) || folder.Kind == FolderKind.Other))
            return "inventory.protected";
        // a worn item stays where it is until it is taken off, and nothing in the current outfit folder is deleted by hand
        if (item is not null && (OutfitPlanner.IsWorn(_model, id) || _model.IsDescendantOf(id, _model.FolderOfKind(FolderKind.CurrentOutfit))))
            return "inventory.worn";
        var trash = _model.FolderOfKind(FolderKind.Trash);
        if (trash == Guid.Empty)
            return "inventory.no_trash";
        if (_model.IsDescendantOf(id, trash))
            return "inventory.already_trash";
        return folder is not null && HasSystemFolderInside(id) ? "inventory.protected_inside" : null;
    }

    /// <summary>True when any folder known below <paramref name="id"/> is a system folder.</summary>
    private bool HasSystemFolderInside(Guid id)
    {
        var stack = new Stack<Guid>();
        stack.Push(id);
        int guard = 0;
        while (stack.Count > 0 && guard++ < 100_000)
            foreach (var sub in _model.ChildFolders(stack.Pop()))
            {
                if (_model.IsSystemFolder(sub.Id))
                    return true;
                stack.Push(sub.Id);
            }
        return false;
    }

    // ---------------- the changes ----------------

    private static InventoryActionResult Refused(string code) => new(false, code, null);

    public InventoryActionResult NewFolder(Guid selected, string name)
    {
        if (WhyNotCreate(selected) is { } why)
            return Refused(why);
        var parent = ParentFor(selected);
        return Run(new InventoryFolderCreateIntent(parent, name), parent);
    }

    public InventoryActionResult Rename(Guid id, string newName)
    {
        if (WhyNotRename(id) is { } why)
            return Refused(why);
        if (_model.Folder(id) is { } f)
            return Run(new InventoryFolderRenameIntent(id, f.ParentId, (int)f.Kind, newName), f.ParentId);
        var item = _model.Item(id)!;
        return Run(new InventoryMoveIntent(id, item.ParentId, newName), item.ParentId);
    }

    public InventoryActionResult Move(Guid id, Guid newParent)
    {
        if (WhyNotMove(id, newParent) is { } why)
            return Refused(why);
        if (_model.Folder(id) is { } f)
            return Run(new InventoryFolderMoveIntent(id, newParent), f.ParentId, newParent);
        var item = _model.Item(id)!;
        return Run(new InventoryMoveIntent(id, newParent), item.ParentId, newParent);
    }

    /// <summary>Moves to the Trash folder (it never purges). Asks for the user's confirmation first: <paramref name="confirmed"/>.</summary>
    public InventoryActionResult Trash(Guid id, bool confirmed)
    {
        if (WhyNotTrash(id) is { } why)
            return Refused(why);
        if (!confirmed)
            return Refused("inventory.not_confirmed");
        var trash = _model.FolderOfKind(FolderKind.Trash);
        if (_model.Folder(id) is { } f)
            return Run(new InventoryFolderTrashIntent(id, trash, true), f.ParentId, trash);
        var item = _model.Item(id)!;
        return Run(new InventoryTrashIntent(id, trash, true), item.ParentId, trash);
    }

    /// <summary>Submits one intent; when it was sent (or may have been), the folders it touches are read again.</summary>
    private InventoryActionResult Run(ActionIntent intent, params Guid[] touched)
    {
        var result = _submit(intent);
        bool sent = result.Status is ActionStatus.Sent or ActionStatus.OutcomeUnknown;
        if (sent)
            foreach (var folder in touched.Distinct())
                if (_model.LoadState(folder) is FolderLoad.Loaded or FolderLoad.Loading)
                    _model.Invalidate(folder);
        return new InventoryActionResult(sent, result.Code ?? (result.Issues is { Count: > 0 } issues ? issues[0].Code : null), result);
    }
}
