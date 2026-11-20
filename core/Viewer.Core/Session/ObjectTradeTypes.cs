// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>One item inside an object (its task inventory). The masks use the protocol's permission bits; the owner is the object's owner.
/// The asset, creator and other masks are what the region lists for the item (the asset is empty where the region does not give it).
/// The description, group, flags, sale and creation date are kept so that an update of the item (a rename) sends them back unchanged.</summary>
public sealed record TaskItem(Guid ItemId, string Name, int AssetType, int InvType, Guid OwnerId, uint OwnerMask, uint EveryoneMask,
    Guid AssetId = default, Guid CreatorId = default, uint BaseMask = 0, uint GroupMask = 0, uint NextOwnerMask = 0,
    string Description = "", Guid GroupId = default, bool GroupOwned = false, uint Flags = 0, int SaleType = 0, int SalePrice = 0, int CreationDate = 0);

/// <summary>The region's answer to a contents request: the items inside the object, or nothing when the request failed or timed out.</summary>
public sealed record TaskInventoryReceived(Guid ObjectId, IReadOnlyList<TaskItem> Items, bool Ok = true) : TransportEvent;

/// <summary>The contents of an object were copied into a new inventory folder (its id is the one the viewer made).</summary>
public sealed record ContentsCopied(Guid ObjectId, Guid FolderId, int ItemCount, bool Wear) : TransportEvent;

/// <summary>The region answered a pay-price request: the price an object asks by default and its quick-pay buttons (protocol values:
/// -1 means "the payer types the amount", -2 means "no default").</summary>
public sealed record PayPriceReceived(Guid ObjectId, int DefaultPrice, IReadOnlyList<int> ButtonPrices) : TransportEvent;

/// <summary>Ask for the items inside an object (the task inventory request and its transfer, or the capability where the region has it).</summary>
public sealed record RequestTaskInventoryCommand(uint LocalId, Guid ObjectId) : TransportCommand;

/// <summary>Copy the given items of an object into a new folder named <paramref name="FolderName"/>, made under <paramref name="Wear"/>'s parent:
/// the clothing folder when the items are to be worn, otherwise the inventory root.</summary>
public sealed record CopyTaskContentsCommand(uint LocalId, Guid ObjectId, string FolderName, bool Wear, IReadOnlyList<Guid> ItemIds) : TransportCommand;

/// <summary>Buy an object: its sale type (1 original, 2 copy, 3 contents) and the price the viewer showed (the region cancels the sale if it differs).</summary>
public sealed record BuyObjectCommand(uint LocalId, byte SaleType, int Price) : TransportCommand;

/// <summary>Ask what an object charges when paid.</summary>
public sealed record RequestPayPriceCommand(uint LocalId, Guid ObjectId) : TransportCommand;

/// <summary>Pay an object.</summary>
public sealed record PayObjectCommand(Guid ObjectId, int Amount, string ObjectName) : TransportCommand;

/// <summary>The region's resource cost for one object (its answer to GetObjectCost): the object's own cost and the cost of its whole linkset,
/// which is the land impact the Build window shows for a selected root.</summary>
public sealed record ObjectCostReceived(Guid ObjectId, float ObjectCost, float LinksetCost) : TransportEvent;

/// <summary>The region's physics settings for one object (its answer to GetObjectPhysicsData, or the ObjectPhysicsProperties it sends after a change).</summary>
public sealed record ObjectPhysicsReceived(Guid ObjectId, uint LocalId, Viewer.Core.Building.PhysicsParams Physics) : TransportEvent;
