// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Session;

/// <summary>
/// The command side of the protocol adapter (ADR-015): the things the user asks the grid to do. One method, one record
/// per command, so a new command is a new record and not a new interface member. Nothing calls this except the action
/// pipeline (<c>Viewer.Core.Actions.ActionPipeline</c>), which validates, lets the veto points refuse, and only then
/// hands a command over; an architecture test fails the build for any other caller. Transport maintenance
/// (acknowledgements, retransmission, pings, circuit upkeep, the repeated AgentUpdate traffic of a held movement) is
/// not a command: the adapter owns it and it never waits on the pipeline.
/// </summary>
public interface ICommandTransport
{
    /// <summary>Hands one command to the protocol layer. Returns at once; the grid's answer, if any, comes back as an event.
    /// <see cref="CommandDispatch.Sent"/> false means nothing left the viewer. May throw: the command's outcome is then unknown.</summary>
    CommandDispatch Execute(TransportCommand command);
}

/// <summary>Whether a command was handed to the protocol layer, and if not, why (a short code, never user text).</summary>
public readonly record struct CommandDispatch(bool Sent, string? Reason = null)
{
    public static CommandDispatch Handed => new(true);
    public static CommandDispatch NotSent(string reason) => new(false, reason);
}

/// <summary>A user command in the form the adapter needs: plain values, already checked by the pipeline.</summary>
public abstract record TransportCommand;

/// <summary>How far local chat carries: a whisper, normal speech or a shout.</summary>
public enum ChatKind { Whisper, Say, Shout }

public sealed record SayCommand(string Text, int Channel, ChatKind Kind) : TransportCommand;

public sealed record InstantMessageCommand(Guid Target, string Text) : TransportCommand;

public sealed record TouchCommand(uint LocalId) : TransportCommand;

public sealed record SitCommand(Guid ObjectId, Vector3 Offset) : TransportCommand;

public sealed record StandCommand : TransportCommand;

public sealed record TeleportRegionCommand(string RegionName, Vector3 Position) : TransportCommand;

public sealed record TeleportLandmarkCommand(Guid Landmark) : TransportCommand;

public sealed record TeleportHomeCommand : TransportCommand;

/// <summary>Teleports to a position in the region with this handle (the world map's choice).</summary>
public sealed record TeleportHandleCommand(ulong RegionHandle, Vector3 Position) : TransportCommand;

/// <summary>Asks the grid to stop the teleport in progress.</summary>
public sealed record TeleportCancelCommand : TransportCommand;

/// <summary>Answers a teleport offer: accept (teleport via the offer) or decline (tell the sender).</summary>
public sealed record TeleportLureAnswerCommand(Guid From, Guid LureId, bool Accept) : TransportCommand;

/// <summary>Offers a teleport to one agent, with a message.</summary>
public sealed record TeleportOfferCommand(Guid Target, string Message) : TransportCommand;

/// <summary>Moves an inventory item into a folder, renaming it when <paramref name="NewName"/> is given. The grid's folder
/// contents are the authority afterwards: the caller reconciles with a read, it does not assume.</summary>
public sealed record MoveInventoryItemCommand(Guid ItemId, Guid FolderId, string? NewName) : TransportCommand;

/// <summary>Sends an agent-inventory item whole (UpdateInventoryItem): its new name, description and the rights it already had.</summary>
public sealed record UpdateInventoryItemCommand(Viewer.Core.Inventory.InventoryItemData Item) : TransportCommand;

/// <summary>Starts an ad-hoc conference (a group instant message) with these agents under a session id the caller made.</summary>
public sealed record StartConferenceCommand(Guid SessionId, IReadOnlyList<Guid> Participants) : TransportCommand;

/// <summary>Tells the region this agent started or stopped typing in local chat (a chat line of the typing kind, no text).</summary>
public sealed record ChatTypingCommand(bool Started) : TransportCommand;

/// <summary>Tells one agent this agent started or stopped typing in their one-to-one conversation.</summary>
public sealed record ImTypingCommand(Guid Target, bool Started) : TransportCommand;

/// <summary>Offers friendship to one agent, with a message.</summary>
public sealed record FriendshipOfferCommand(Guid Target, string Message) : TransportCommand;

/// <summary>Accepts or declines a friendship offer. <paramref name="ViaCapability"/>: the offer was stored while this agent was away,
/// so the grid's capability answers it; otherwise the ordinary message carries <paramref name="OfferId"/>.</summary>
public sealed record FriendshipAnswerCommand(Guid From, Guid OfferId, bool Accept, bool ViaCapability) : TransportCommand;

/// <summary>Ends a friendship.</summary>
public sealed record FriendshipEndCommand(Guid Friend) : TransportCommand;

/// <summary>Sets the complete set of rights this agent gives a friend.</summary>
public sealed record FriendRightsCommand(Guid Friend, FriendRightsFlags Rights) : TransportCommand;

/// <summary>Asks to join a group's chat session.</summary>
public sealed record GroupChatJoinCommand(Guid Group) : TransportCommand;

/// <summary>Leaves a group's chat session.</summary>
public sealed record GroupChatLeaveCommand(Guid Group) : TransportCommand;

/// <summary>Says something in a group's chat session.</summary>
public sealed record GroupMessageCommand(Guid Group, string Text) : TransportCommand;

/// <summary>Makes a group the active one (the empty id: none).</summary>
public sealed record ActiveGroupCommand(Guid Group) : TransportCommand;

/// <summary>Chooses the title (the role) this agent uses in a group.</summary>
public sealed record ActiveTitleCommand(Guid Group, Guid Role) : TransportCommand;

/// <summary>Answers a group notice's attachment offer: accept (it goes to the default folder for its asset type) or decline.</summary>
public sealed record GroupNoticeAnswerCommand(Guid Sender, Guid OfferId, bool Accept, int AssetType) : TransportCommand;

/// <summary>Sets the home location to the agent's place.</summary>
public sealed record SetHomeCommand : TransportCommand;

/// <summary>Asks one agent to offer this agent a teleport.</summary>
public sealed record TeleportRequestCommand(Guid Target, string Message) : TransportCommand;

/// <summary>Makes a new folder of the plain kind under <paramref name="ParentId"/>. The grid's folder contents are the
/// authority afterwards: the caller reconciles with a read.</summary>
public sealed record CreateInventoryFolderCommand(Guid ParentId, string Name) : TransportCommand;

/// <summary>Renames a folder (the grid's folder update carries the folder's parent and kind too, which stay as they are).</summary>
public sealed record RenameInventoryFolderCommand(Guid FolderId, Guid ParentId, int KindNumber, string NewName) : TransportCommand;

/// <summary>Moves a folder (with everything in it) under another folder.</summary>
public sealed record MoveInventoryFolderCommand(Guid FolderId, Guid NewParentId) : TransportCommand;

/// <summary>Changes the current outfit (links and attachments) and then asks the grid's server-side baking to rebuild the look.
/// The inventory is the authority afterwards: the caller reconciles with a read.</summary>
public sealed record ChangeOutfitCommand(Viewer.Core.Inventory.OutfitChange Change) : TransportCommand;

/// <summary>Asks the region's server-side baking to rebuild the look from the current outfit folder as it stands, without changing it
/// (sent once at login in a region that bakes on the server, as the official viewer does).</summary>
public sealed record RebuildLookCommand(Guid CofId) : TransportCommand;

/// <summary>Makes a new outfit folder under My Outfits holding links to everything in the current outfit folder.</summary>
public sealed record SaveOutfitCommand(Guid CofId, Guid MyOutfitsId, string Name, Guid RootId = default) : TransportCommand;
/// <summary>Saves an edited wearable over its own inventory item: the file is uploaded as a new asset, the item is pointed at it
/// and renamed, and the grid's baking service is asked to rebuild the look.</summary>
public sealed record SaveWearableCommand(Guid ItemId, string Name, int AssetType, byte[] Data) : TransportCommand;

/// <summary>Saves an edited wearable as a copy of an inventory item (in <paramref name="FolderId"/>, under <paramref name="Name"/>), wears the copy in the original's place and asks for a rebuild.</summary>
public sealed record SaveWearableAsCommand(Guid SourceItemId, Guid FolderId, string Name, int AssetType, byte[] Data) : TransportCommand;

/// <summary>The mouse went down on an object: the grab message, with where the click landed.</summary>
public sealed record GrabCommand(uint LocalId, Vector3 GrabOffset, Viewer.Core.Actions.TouchSurface Surface) : TransportCommand;

/// <summary>The mouse went up: the release message.</summary>
public sealed record ReleaseCommand(uint LocalId, Viewer.Core.Actions.TouchSurface Surface) : TransportCommand;

/// <summary>Sit on the ground where the avatar is.</summary>
public sealed record SitGroundCommand : TransportCommand;

/// <summary>Select an object so the region sends its properties, then let go of the selection once they arrive.</summary>
public sealed record RequestObjectPropertiesCommand(uint LocalId, bool Hold = false) : TransportCommand;

/// <summary>Take an object (or a copy) into the agent's inventory.</summary>
public sealed record TakeObjectCommand(uint LocalId, bool Copy) : TransportCommand;

/// <summary>Ask for the parcel at a point.</summary>
public sealed record RequestParcelCommand(float X, float Y) : TransportCommand;

/// <summary>A command the viewer runs itself, not the grid: the pipeline hands it to its local executor, after the same checks and veto points.</summary>
public interface ILocalCommand;

/// <summary>Answers a script dialog: the button chosen (its index in the script's list) and its label, or for a text box the typed text with index 0.
/// Sent to the region the dialog came from, on the script's chat channel.</summary>
public sealed record ScriptDialogReplyCommand(SimRef? Sim, Guid ObjectId, int Channel, int ButtonIndex, string Label) : TransportCommand;

/// <summary>Answers a permission request: <paramref name="Granted"/> is the mask of permissions given (0: none).</summary>
public sealed record ScriptPermissionAnswerCommand(SimRef? Sim, Guid TaskId, Guid ItemId, int Granted) : TransportCommand;

/// <summary>Opens a web address in the system's browser (local).</summary>
public sealed record OpenUrlCommand(string Url) : TransportCommand, ILocalCommand;

/// <summary>Make the default box (the create tool's ObjectAdd) where the placement's ray says.</summary>
public sealed record CreateObjectCommand(Viewer.Core.Building.Placement Placement, System.Numerics.Vector3 Scale, byte Material,
    Viewer.Core.Building.CreateShape Shape = Viewer.Core.Building.CreateShape.Cube, byte Species = 0) : TransportCommand;

/// <summary>Rez an object from the agent's inventory (RezObject) where the placement's ray says.</summary>
public sealed record RezItemCommand(Guid ItemId, Viewer.Core.Building.Placement Placement) : TransportCommand;

/// <summary>Delete an object to the agent's Trash (DeRezObject to the trash).</summary>
public sealed record DeleteObjectCommand(uint LocalId) : TransportCommand;

/// <summary>An edit of objects in the world (name, description, permissions, place, flags, shape, link, unlink, duplicate): the intent itself.</summary>
public sealed record EditObjectCommand(Viewer.Core.Actions.ActionIntent Edit) : TransportCommand;
