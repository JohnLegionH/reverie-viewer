// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Inventory;

/// <summary>
/// The preferred type of an inventory folder: the grid's own numbering, which says what a system folder is for. The
/// values are the protocol's; a value this viewer does not know is kept as an <see cref="Other"/> folder and shown as an
/// ordinary one.
/// </summary>
public enum FolderKind
{
    None = -1,
    Texture = 0,
    Sound = 1,
    CallingCard = 2,
    Landmark = 3,
    Clothing = 5,
    Object = 6,
    Notecard = 7,
    Root = 8,
    Script = 10,
    Bodypart = 13,
    Trash = 14,
    Snapshot = 15,
    LostAndFound = 16,
    Animation = 20,
    Gesture = 21,
    Favorites = 23,
    CurrentOutfit = 46,
    Outfit = 47,
    MyOutfits = 48,
    Mesh = 49,
    Inbox = 50,
    Outbox = 51,
    BasicRoot = 52,
    MarketplaceListings = 53,
    MarketplaceStock = 54,
    MarketplaceVersion = 55,
    Settings = 56,
    Material = 57,
    Other = 1000,
}

/// <summary>What an inventory item is, by the asset it holds. A link keeps its own kind; the item it points to is its
/// <see cref="InventoryItemData.LinkedId"/>.</summary>
public enum ItemKind
{
    Texture, Sound, CallingCard, Landmark, Clothing, Object, Notecard, Script, Bodypart, Animation, Gesture,
    Mesh, Settings, Material, Link, LinkFolder, Other,
}

/// <summary>One inventory folder as the grid describes it. Folder names are user content: they are never logged.</summary>
/// <param name="Version">The folder's version number; it changes whenever its contents change. Negative: unknown.</param>
/// <param name="DescendentCount">How many items and folders the grid says the folder holds directly. Negative: unknown.</param>
public sealed record InventoryFolderData(Guid Id, Guid ParentId, string Name, FolderKind Kind, int Version = -1, int DescendentCount = -1);

/// <summary>One inventory item as the grid describes it. Item names are user content: they are never logged.</summary>
/// <param name="AssetType">The protocol's asset type number.</param>
/// <param name="InventoryType">The protocol's inventory type number (it says, with the flags, whether an object is worn).</param>
/// <param name="LinkedId">For a link, the id of the item or folder it points to.</param>
/// <param name="Description">The item's description; on a clothing link in the current outfit folder it holds the layer order.</param>
/// <param name="AssetId">The asset the item holds, when the grid names it (not for a link: see <paramref name="LinkedId"/>).</param>
public sealed record InventoryItemData(Guid Id, Guid ParentId, string Name, ItemKind Kind, int AssetType, int InventoryType, uint Flags,
    DateTimeOffset? CreatedAt, Guid? LinkedId = null, string Description = "", Guid? AssetId = null);

/// <summary>The inventory skeleton the login reply carries: the agent's own folders and the shared library's, with versions
/// but no items. Everything else is fetched folder by folder.</summary>
/// <param name="LibraryOwnerId">The agent that owns the shared library (folder requests for it name this owner).</param>
/// <param name="AttachmentLimit">The most objects the grid lets the avatar wear, from the login reply's account benefits; -1 when the grid sent none.</param>
public sealed record InventorySkeletonReceived(Guid OwnerId, Guid RootId, Guid LibraryOwnerId, Guid LibraryRootId,
    IReadOnlyList<InventoryFolderData> Folders, IReadOnlyList<InventoryFolderData> LibraryFolders, int AttachmentLimit = -1) : TransportEvent;

/// <summary>The answer to a request for one folder's contents: <paramref name="Ok"/> false when the grid could not give them
/// (no capability, an error). On success the lists are the folder's direct children as the grid has them now.</summary>
public sealed record InventoryFolderFetched(Guid FolderId, bool Ok, int Version, int DescendentCount,
    IReadOnlyList<InventoryFolderData> Folders, IReadOnlyList<InventoryItemData> Items) : TransportEvent;

/// <summary>Changes the grid pushed while the agent is logged in (new, changed, moved or removed objects), or changes the
/// library reports after a command. Applying the same change twice is harmless.</summary>
public sealed record InventoryChanged(IReadOnlyList<InventoryFolderData> Folders, IReadOnlyList<InventoryItemData> Items,
    IReadOnlyList<Guid> RemovedFolders, IReadOnlyList<Guid> RemovedItems) : TransportEvent;

/// <summary>A transport that can ask the grid for one folder's contents (the answer comes back as
/// <see cref="InventoryFolderFetched"/>). A read, not a user command, so it does not go through the action pipeline.</summary>
public interface IInventoryRequester
{
    /// <param name="ownerId">The folder's owner: the agent for its own folders, the library's owner for the library.</param>
    void RequestFolderContents(Guid folderId, Guid ownerId);
}
