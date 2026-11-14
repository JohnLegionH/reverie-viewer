// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>
/// Someone gave this agent an inventory item. From a resident (the grid has already put a copy into this agent's inventory;
/// <paramref name="ItemId"/> is that copy) or from an object (<paramref name="FromObject"/>; the item is not in inventory until the
/// offer is accepted, so <paramref name="ItemId"/> is empty). <paramref name="TransactionId"/> is what the answer must carry.
/// <paramref name="AssetType"/> is the protocol's asset type number.
/// </summary>
public sealed record InventoryOffered(Guid FromId, string FromName, bool FromObject, Guid TransactionId, int AssetType, Guid ItemId, string Description) : TransportEvent;

/// <summary>Answers an inventory offer: accept (the item goes to <paramref name="FolderId"/>, empty for where the grid put it) or decline.</summary>
public sealed record InventoryOfferAnswerCommand(Guid FromId, Guid TransactionId, bool FromObject, bool Accept, Guid FolderId) : TransportCommand;

/// <summary>Attaches an object that is in the world (this agent owns it) to one of the avatar's attachment points.</summary>
public sealed record AttachWorldObjectCommand(uint LocalId, int Point) : TransportCommand;

/// <summary>Drop a worn object into the world (the ObjectDrop message).</summary>
public sealed record DropAttachmentCommand(uint LocalId) : TransportCommand;

/// <summary>Copies an item embedded in a notecard of the agent's inventory into the inventory (an empty folder: the grid chooses).</summary>
public sealed record CopyFromNotecardCommand(Guid NotecardItemId, Guid EmbeddedItemId, Guid FolderId) : TransportCommand;
