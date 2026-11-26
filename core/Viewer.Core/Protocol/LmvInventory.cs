// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LmvStore = LibreMetaverse.Inventory;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>
/// The inventory side of the protocol adapter: the login skeleton, folder fetches and the changes the library reports, as the
/// core's plain records. Folder fetches use the library's capability-based request (the grid's descendents capability); the
/// library applies what it receives to its own store, and this adapter reads the folder back from that store when the
/// library says the fetch is done. Names belong to the user and are never logged.
/// </summary>
internal sealed partial class LmvTransport
{
    private bool _inventoryAttached;

    /// <summary>Called once the login reply is in: the library has built its store from the reply's skeleton, so the skeleton
    /// goes to the core, and the library's later reports are forwarded.</summary>
    private void AttachInventory()
    {
        if (_inventoryAttached || _disposed)
            return;
        var store = _client.Inventory.Store;
        var data = _client.Network.LoginResponseData;
        if (store is null || data is null)
            return;
        _inventoryAttached = true;
        store.InventoryObjectAdded += OnInventoryAdded;
        store.InventoryObjectUpdated += OnInventoryUpdated;
        store.InventoryObjectRemoved += OnInventoryRemoved;
        _client.Inventory.FolderUpdated += OnFolderUpdated;
        Raise(BuildSkeleton(data, store));
    }

    private void DetachInventory()
    {
        if (!_inventoryAttached)
            return;
        _inventoryAttached = false;
        try
        {
            var store = _client.Inventory.Store;
            if (store is not null)
            {
                store.InventoryObjectAdded -= OnInventoryAdded;
                store.InventoryObjectUpdated -= OnInventoryUpdated;
                store.InventoryObjectRemoved -= OnInventoryRemoved;
            }
            _client.Inventory.FolderUpdated -= OnFolderUpdated;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("inventory.detach-failed", CoreLog.F("error", ex.GetType().Name));
        }
    }

    internal static FolderKind KindOf(InventoryFolder f) => InventoryKinds.FolderKindOf((int)f.PreferredType);

    internal static InventoryFolderData ToFolder(InventoryFolder f) =>
        new(f.UUID.Guid, f.ParentUUID.Guid, f.Name ?? "", KindOf(f), f.Version, f.DescendentCount);

    internal static InventoryItemData ToItem(InventoryItem i)
    {
        var kind = InventoryKinds.ItemKindOf((int)i.AssetType);
        bool link = kind is ItemKind.Link or ItemKind.LinkFolder;
        DateTimeOffset? at = i.CreationDate.Year >= 2000
            ? new DateTimeOffset(i.CreationDate.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(i.CreationDate, DateTimeKind.Utc) : i.CreationDate.ToUniversalTime())
            : null;
        return new(i.UUID.Guid, i.ParentUUID.Guid, i.Name ?? "", kind, (int)i.AssetType, (int)i.InventoryType, (uint)i.Flags, at,
            link ? i.AssetUUID.Guid : null, i.Description ?? "", link || i.AssetUUID == UUID.Zero ? null : i.AssetUUID.Guid, PermissionsOf(i));
    }

    /// <summary>The item's rights, owner and sale as the library holds them.</summary>
    internal static ItemPermissions PermissionsOf(InventoryItem i) => new(
        (uint)i.Permissions.BaseMask, (uint)i.Permissions.OwnerMask, (uint)i.Permissions.GroupMask, (uint)i.Permissions.EveryoneMask,
        (uint)i.Permissions.NextOwnerMask, i.CreatorID.Guid, i.OwnerID.Guid, i.GroupID.Guid, i.GroupOwned, (int)i.SaleType, i.SalePrice);

    internal static InventorySkeletonReceived BuildSkeleton(LoginResponseData d, LmvStore store)
    {
        var own = new List<InventoryFolderData> { new(d.InventoryRoot.Guid, Guid.Empty, "", FolderKind.Root, FolderVersionOf(store, d.InventoryRoot)) };
        var lib = new List<InventoryFolderData> { new(d.LibraryRoot.Guid, Guid.Empty, "", FolderKind.Root, FolderVersionOf(store, d.LibraryRoot)) };
        Walk(store, d.InventoryRoot, own);
        Walk(store, d.LibraryRoot, lib);
        // the grid's limit on worn objects, where the official viewer takes it; -1 when the grid sent no account benefits
        return new InventorySkeletonReceived(d.AgentID.Guid, d.InventoryRoot.Guid, d.LibraryOwner.Guid, d.LibraryRoot.Guid, own, lib, d.AccountLevelBenefits?.AttachmentLimit ?? -1);
    }

    /// <summary>Adds every folder below <paramref name="root"/> (the skeleton holds folders only), breadth first.</summary>
    private static void Walk(LmvStore store, UUID root, List<InventoryFolderData> into)
    {
        var queue = new Queue<UUID>();
        var seen = new HashSet<UUID> { root };
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            foreach (var child in store.GetContents(queue.Dequeue()))
            {
                if (child is not InventoryFolder f || !seen.Add(f.UUID))
                    continue;
                into.Add(ToFolder(f));
                queue.Enqueue(f.UUID);
            }
        }
    }

    private static int FolderVersionOf(LmvStore store, UUID id) =>
        store.TryGetValue(id, out var b) && b is InventoryFolder f ? f.Version : -1;

    // ---------------- reads ----------------

    /// <summary>Asks the library to fetch one folder's contents; the answer comes back as <see cref="InventoryFolderFetched"/>,
    /// also when the fetch fails (no capability, an error), so the core never waits forever.</summary>
    public void RequestFolderContents(Guid folderId, Guid ownerId)
    {
        if (_disposed || !_client.Network.Connected)
        {
            Raise(FetchFailed(folderId));
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await _client.Inventory.RequestFolderContentsAsync(new UUID(folderId), new UUID(ownerId), true, true,
                    InventorySortOrder.ByName, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("inventory.fetch-failed", CoreLog.F("error", ex.GetType().Name));
                Raise(FetchFailed(folderId));
            }
        });
    }

    private static InventoryFolderFetched FetchFailed(Guid folderId) =>
        new(folderId, false, -1, -1, Array.Empty<InventoryFolderData>(), Array.Empty<InventoryItemData>());

    /// <summary>The library says a folder fetch finished: its contents are read back from the library's store.</summary>
    private void OnFolderUpdated(object? sender, FolderUpdatedEventArgs e)
    {
        var id = e.FolderID.Guid;
        if (!e.Success)
        {
            Raise(FetchFailed(id));
            return;
        }
        var store = _client.Inventory.Store;
        if (store is null || !store.TryGetValue(e.FolderID, out var b) || b is not InventoryFolder folder)
        {
            Raise(FetchFailed(id));
            return;
        }
        var folders = new List<InventoryFolderData>();
        var items = new List<InventoryItemData>();
        foreach (var child in store.GetContents(folder))
        {
            if (DropIfRemoved(store, child))
                continue;
            if (child is InventoryFolder cf) folders.Add(ToFolder(cf));
            else if (child is InventoryItem ci) items.Add(ToItem(ci));
        }
        Raise(new InventoryFolderFetched(id, true, folder.Version, folder.DescendentCount, folders, items));
    }

    // ---------------- the library's own reports (pushes, and the effect of our commands) ----------------

    private static readonly IReadOnlyList<InventoryFolderData> NoFolders = Array.Empty<InventoryFolderData>();
    private static readonly IReadOnlyList<InventoryItemData> NoItems = Array.Empty<InventoryItemData>();
    private static readonly IReadOnlyList<Guid> NoIds = Array.Empty<Guid>();

    internal static InventoryChanged ChangedFor(InventoryBase b) => b switch
    {
        InventoryFolder f => new InventoryChanged(new[] { ToFolder(f) }, NoItems, NoIds, NoIds),
        InventoryItem i => new InventoryChanged(NoFolders, new[] { ToItem(i) }, NoIds, NoIds),
        _ => new InventoryChanged(NoFolders, NoItems, NoIds, NoIds),
    };

    internal static InventoryChanged RemovedFor(InventoryBase b) => b switch
    {
        InventoryFolder f => new InventoryChanged(NoFolders, NoItems, new[] { f.UUID.Guid }, NoIds),
        InventoryItem i => new InventoryChanged(NoFolders, NoItems, NoIds, new[] { i.UUID.Guid }),
        _ => new InventoryChanged(NoFolders, NoItems, NoIds, NoIds),
    };

    /// <summary>A link this viewer has just removed (see <see cref="RemovedLinkLedger"/>) is taken out of the library's store again
    /// if an answer or a late notice put it back; true when it was one.</summary>
    private bool DropIfRemoved(LmvStore store, InventoryBase b)
    {
        if (b is not InventoryItem || !_removedLinks.IsRemoved(b.UUID.Guid))
            return false;
        if (store.Contains(b.UUID))
            store.RemoveNodeFor(b);
        return true;
    }

    private void OnInventoryAdded(object? sender, InventoryObjectAddedEventArgs e)
    {
        if (_client.Inventory.Store is { } store && DropIfRemoved(store, e.Obj))
            return;
        Raise(ChangedFor(e.Obj));
    }

    private void OnInventoryUpdated(object? sender, InventoryObjectUpdatedEventArgs e)
    {
        if (_client.Inventory.Store is { } store && DropIfRemoved(store, e.NewObject))
            return;
        Raise(ChangedFor(e.NewObject));
    }

    private void OnInventoryRemoved(object? sender, InventoryObjectRemovedEventArgs e) => Raise(RemovedFor(e.Obj));

    // ---------------- folder commands ----------------

    /// <summary>The folder commands, already checked by the action pipeline. The library sends each at once (one packet).</summary>
    private void ExecuteFolderCommand(TransportCommand command)
    {
        var inv = _client.Inventory;
        switch (command)
        {
            case CreateInventoryFolderCommand c:
                inv.CreateFolder(new UUID(c.ParentId), c.Name);
                break;
            case CreateLandmarkCommand lm:
                // the region makes the landmark's asset from where the agent stands; the new item arrives as any created item does
                // no transaction: a non-zero one tells the region an upload is coming, and it would wait for one (the official viewer sends
                // LLTransactionID::tnull here); the region writes the landmark's asset itself
                _ = inv.CreateItemAsync(new UUID(lm.FolderId), lm.Name, lm.Description, AssetType.Landmark, UUID.Zero, InventoryType.Landmark,
                    PermissionMask.All, CancellationToken.None).ContinueWith(t => _log.Warn("inventory.landmark-failed", CoreLog.F("error", t.Exception?.InnerException?.GetType().Name ?? "")),
                    TaskContinuationOptions.OnlyOnFaulted);
                break;
            case RenameInventoryFolderCommand r:
                inv.UpdateFolderProperties(new UUID(r.FolderId), new UUID(r.ParentId), r.NewName, (FolderType)r.KindNumber);
                break;
            case MoveInventoryFolderCommand m:
                // the official viewer's message; the library's own folder move would go by the inventory capability
                _client.Network.SendPacket(InventoryMovePackets.Folder(_client.Self.AgentID, _client.Self.SessionID, m.FolderId, m.NewParentId));
                KeepLibraryStoreParent(m.FolderId, m.NewParentId, null);
                break;
        }
    }

    /// <summary>Gives the library's own copy of an updated item its new description, next-owner permissions and flags, so a later
    /// report from the library does not carry the old ones back to the core.</summary>
    private void KeepLibraryStoreProperties(InventoryItemData item)
    {
        try
        {
            var store = _client.Inventory.Store;
            if (store is null || item.Permissions is not { } rights || !store.TryGetValue(new UUID(item.Id), out var node) || node is not InventoryItem held)
                return;
            var permissions = held.Permissions;
            permissions.NextOwnerMask = (PermissionMask)rights.NextOwnerMask;
            held.Permissions = permissions;
            held.Description = item.Description;
            held.Flags = item.Flags;
            store.UpdateNodeFor(held);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("inventory.store-update-skipped", CoreLog.F("error", ex.GetType().Name));
        }
    }

    /// <summary>Gives the library's own copy of a moved item or folder its new parent (and name), as the library's move calls
    /// do, so a later report from the library about that entry does not carry the old parent back to the core.</summary>
    private void KeepLibraryStoreParent(Guid id, Guid newParent, string? newName)
    {
        try
        {
            var store = _client.Inventory.Store;
            if (store is null || !store.TryGetValue(new UUID(id), out var node) || node is null)
                return;
            if (!string.IsNullOrEmpty(newName) && node is InventoryItem)
                node.Name = newName;
            node.ParentUUID = new UUID(newParent);
            store.UpdateNodeFor(node);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // the core's model already shows the move; the library's copy is put right by its next read of the folder
            _log.Warn("inventory.store-move-skipped", CoreLog.F("error", ex.GetType().Name));
        }
    }
}
