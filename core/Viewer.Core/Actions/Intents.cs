// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Avatars;
using Viewer.Core.Session;

namespace Viewer.Core.Actions;

/// <summary>The kinds of intent the pipeline authorises (ADR-015: domain intents, not packets).</summary>
public enum ActionKind
{
    Say, InstantMessage, Touch, Sit, Stand, TeleportRegion, TeleportLandmark, TeleportHome, InventoryMove, InventoryTrash, HoldMovement,
    ChatTyping, ImTyping,
    FriendshipOffer, FriendshipAnswer, FriendshipEnd, FriendRights,
    GroupChatJoin, GroupChatLeave, GroupMessage, ActiveGroup, ActiveTitle, GroupNoticeAnswer,
    TeleportHandle, TeleportCancel, TeleportLureAnswer, TeleportOffer,
    SetHome, TeleportRequest,
    InventoryFolderCreate, InventoryFolderRename, InventoryFolderMove, InventoryFolderTrash, OutfitChange, OutfitSave,
    WearableSave, WearableSaveAs,
    Mute, Unmute,
    Grab, Release, SitGround, ObjectProperties, Take, ParcelInfo,
    ScriptDialogReply, ScriptPermissionAnswer, OpenUrl,
    InventoryOfferAnswer, AttachWorldObject, DropAttachment,
    RequestContents, CopyContents, BuyObject, RequestPayPrice, PayObject,
    LookRebuild,
    CreateObject, RezItem, DeleteObject, EditObject,
    NotecardCopyItem,
    InventoryItemUpdate, ConferenceStart,
}

/// <summary>What the user wants done, in the interface's terms. Checks itself (<see cref="Validate"/>) before anything is sent.</summary>
public abstract record ActionIntent
{
    public abstract ActionKind Kind { get; }

    /// <summary>Everything wrong with this intent; empty when it is fit to send. Pure: reads nothing but the intent.</summary>
    public abstract IReadOnlyList<ActionIssue> Validate();

    /// <summary>True for an intent that continues over time (it is begun, continued and ended, not submitted).</summary>
    public virtual bool IsHeld => false;

    /// <summary>The command this intent means, with its text normalised. Only called for an intent that validated.</summary>
    internal abstract TransportCommand? ToCommand();
}

/// <summary>The limits the protocol puts on what an intent may carry, and the checks that apply them.</summary>
internal static class IntentRules
{
    /// <summary>The most bytes (UTF-8) one chat or instant message line may carry: the protocol's message field is 1,024 bytes
    /// including its terminator, and an over-long line is refused here, not cut or split.</summary>
    public const int MaxMessageBytes = 1023;

    /// <summary>The most bytes a line may carry on a negative channel: the protocol sends it as a script-dialog reply whose
    /// label field has a one-byte length, and the library cuts it at 254.</summary>
    public const int MaxNegativeChannelBytes = 254;

    /// <summary>The most characters in an inventory item's name (the grid's database limit is 63).</summary>
    public const int MaxInventoryNameChars = 63;

    /// <summary>The longest region name teleport accepts.</summary>
    public const int MaxRegionNameChars = 128;

    /// <summary>The largest region coordinate teleport accepts, in metres (the biggest variable region is 4,096 across).</summary>
    public const float MaxCoordinate = 4096f;

    public static string Normalised(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    public static bool HasControlChars(string text, bool allowLineBreaks) =>
        text.Any(c => char.IsControl(c) && !(allowLineBreaks && c is '\n' or '\t'));

    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public static void CheckMessage(List<ActionIssue> issues, string prefix, string? text, bool allowLineBreaks)
    {
        string t = Normalised(text ?? "");
        if (t.Length == 0)
            issues.Add(new($"{prefix}.empty"));
        else if (Encoding.UTF8.GetByteCount(t) > MaxMessageBytes)
            issues.Add(new($"{prefix}.too_long", $"{Encoding.UTF8.GetByteCount(t)} bytes, most {MaxMessageBytes}"));
        if (t.Length > 0 && HasControlChars(t, allowLineBreaks))
            issues.Add(new($"{prefix}.control_chars"));
    }

    public static bool NameOk(string? name) =>
        name is not null && Normalised(name) is { Length: > 0 and <= MaxInventoryNameChars } n && !HasControlChars(n, false);
}

public sealed record SayIntent(string Text, int Channel = 0, ChatKind Chat = ChatKind.Say) : ActionIntent
{
    public override ActionKind Kind => ActionKind.Say;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        IntentRules.CheckMessage(issues, "say", Text, allowLineBreaks: false);
        if (Channel < 0 && !issues.Any(i => i.Code == "say.too_long") && Encoding.UTF8.GetByteCount(IntentRules.Normalised(Text ?? "")) is var bytes && bytes > IntentRules.MaxNegativeChannelBytes)
            issues.Add(new("say.too_long", $"{bytes} bytes, most {IntentRules.MaxNegativeChannelBytes} on a negative channel"));
        if (!Enum.IsDefined(Chat))
            issues.Add(new("say.bad_kind"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new SayCommand(IntentRules.Normalised(Text), Channel, Chat);
}

public sealed record InstantMessageIntent(Guid Target, string Text) : ActionIntent
{
    public override ActionKind Kind => ActionKind.InstantMessage;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Target == Guid.Empty)
            issues.Add(new("im.no_target"));
        IntentRules.CheckMessage(issues, "im", Text, allowLineBreaks: true);
        return issues;
    }

    internal override TransportCommand? ToCommand() => new InstantMessageCommand(Target, IntentRules.Normalised(Text));
}

/// <summary>This agent started or stopped typing in local chat (the region shows others a typing mark).</summary>
public sealed record ChatTypingIntent(bool Started) : ActionIntent
{
    public override ActionKind Kind => ActionKind.ChatTyping;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new ChatTypingCommand(Started);
}

/// <summary>This agent started or stopped typing in its one-to-one conversation with <paramref name="Target"/>.</summary>
public sealed record ImTypingIntent(Guid Target, bool Started) : ActionIntent
{
    public override ActionKind Kind => ActionKind.ImTyping;
    public override IReadOnlyList<ActionIssue> Validate() => Target == Guid.Empty ? [new("im.no_target")] : [];
    internal override TransportCommand? ToCommand() => new ImTypingCommand(Target, Started);
}

public sealed record TouchIntent(uint LocalId) : ActionIntent
{
    public override ActionKind Kind => ActionKind.Touch;
    public override IReadOnlyList<ActionIssue> Validate() => LocalId == 0 ? [new("touch.no_object")] : [];
    internal override TransportCommand? ToCommand() => new TouchCommand(LocalId);
}

public sealed record SitIntent(Guid ObjectId, Vector3 Offset = default) : ActionIntent
{
    public override ActionKind Kind => ActionKind.Sit;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (ObjectId == Guid.Empty)
            issues.Add(new("sit.no_object"));
        if (!IntentRules.Finite(Offset))
            issues.Add(new("sit.bad_offset"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new SitCommand(ObjectId, Offset);
}

public sealed record StandIntent : ActionIntent
{
    public override ActionKind Kind => ActionKind.Stand;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new StandCommand();
}

public sealed record TeleportRegionIntent(string RegionName, Vector3 Position) : ActionIntent
{
    public override ActionKind Kind => ActionKind.TeleportRegion;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        string name = IntentRules.Normalised(RegionName ?? "");
        if (name.Length == 0)
            issues.Add(new("teleport.no_region"));
        else if (name.Length > IntentRules.MaxRegionNameChars)
            issues.Add(new("teleport.region_too_long"));
        else if (IntentRules.HasControlChars(name, false))
            issues.Add(new("teleport.bad_region"));
        static bool InRegion(float v) => float.IsFinite(v) && v >= 0 && v <= IntentRules.MaxCoordinate;
        if (!InRegion(Position.X) || !InRegion(Position.Y) || !InRegion(Position.Z))
            issues.Add(new("teleport.bad_position"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new TeleportRegionCommand(IntentRules.Normalised(RegionName), Position);
}

public sealed record TeleportLandmarkIntent(Guid Landmark) : ActionIntent
{
    public override ActionKind Kind => ActionKind.TeleportLandmark;
    public override IReadOnlyList<ActionIssue> Validate() => Landmark == Guid.Empty ? [new("teleport.no_landmark")] : [];
    internal override TransportCommand? ToCommand() => new TeleportLandmarkCommand(Landmark);
}

public sealed record TeleportHomeIntent : ActionIntent
{
    public override ActionKind Kind => ActionKind.TeleportHome;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new TeleportHomeCommand();
}

public sealed record InventoryMoveIntent(Guid ItemId, Guid FolderId, string? NewName = null) : ActionIntent
{
    public override ActionKind Kind => ActionKind.InventoryMove;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (ItemId == Guid.Empty)
            issues.Add(new("inventory.no_item"));
        if (FolderId == Guid.Empty)
            issues.Add(new("inventory.no_folder"));
        else if (FolderId == ItemId)
            issues.Add(new("inventory.same_id"));
        if (NewName is not null && !IntentRules.NameOk(NewName))
            issues.Add(new("inventory.bad_name"));
        return issues;
    }

    internal override TransportCommand? ToCommand() =>
        new MoveInventoryItemCommand(ItemId, FolderId, NewName is null ? null : IntentRules.Normalised(NewName));
}

/// <summary>
/// Changes an agent-inventory item's name (and description) the way the official viewer does: an update of the whole item
/// (UpdateInventoryItem, llviewerinventory.cpp updateServer), carrying its rights, owner and sale as the grid described them.
/// <paramref name="Item"/> is the item with its new name already in it; an item without <see cref="InventoryItemData.Permissions"/>
/// cannot be updated this way (a rename then goes by <see cref="InventoryMoveIntent"/>).
/// </summary>
public sealed record InventoryItemUpdateIntent(Viewer.Core.Inventory.InventoryItemData Item) : ActionIntent
{
    public override ActionKind Kind => ActionKind.InventoryItemUpdate;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Item.Id == Guid.Empty)
            issues.Add(new("inventory.no_item"));
        if (Item.Permissions is null)
            issues.Add(new("inventory.no_permissions"));
        if (!IntentRules.NameOk(Item.Name))
            issues.Add(new("inventory.bad_name"));
        return issues;
    }

    internal override TransportCommand? ToCommand() =>
        new UpdateInventoryItemCommand(Item with { Name = IntentRules.Normalised(Item.Name) });
}

/// <summary>Moves an item to the Trash folder. A protected action: it is refused unless <paramref name="Confirmed"/> says the user confirmed it.</summary>
public sealed record InventoryTrashIntent(Guid ItemId, Guid TrashFolderId, bool Confirmed) : ActionIntent
{
    public override ActionKind Kind => ActionKind.InventoryTrash;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (ItemId == Guid.Empty)
            issues.Add(new("inventory.no_item"));
        if (TrashFolderId == Guid.Empty)
            issues.Add(new("inventory.no_trash"));
        else if (TrashFolderId == ItemId)
            issues.Add(new("inventory.same_id"));
        if (!Confirmed)
            issues.Add(new("inventory.not_confirmed"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new MoveInventoryItemCommand(ItemId, TrashFolderId, null);
}

/// <summary>An intent that continues until it is released, revoked or the session ends.</summary>
public abstract record HeldIntent : ActionIntent
{
    public override bool IsHeld => true;

    /// <summary>A held intent sends no command of its own: the owner of its repeated traffic (the <see cref="IHeldSink"/>) does.</summary>
    internal override TransportCommand? ToCommand() => null;
}

/// <summary>Move the own avatar with these AgentControlFlags bits, for as long as the hold lasts. The movement sender is the
/// <see cref="IHeldSink"/>: it begins when the first key goes down, continues on every frame the keys are held (the pipeline
/// re-checks the veto points each time and ends the hold the moment one refuses, so a restriction that lands mid-hold stops
/// the movement at once) and ends on release. The AgentUpdate traffic itself, including its repeats, stays with the
/// protocol adapter and never waits on the pipeline.</summary>
public sealed record HoldMovementIntent(uint Controls) : HeldIntent
{
    private static readonly uint KnownBits = AgentControlFlags.All.Aggregate(0u, (all, bit) => all | bit);

    public override ActionKind Kind => ActionKind.HoldMovement;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Controls == 0)
            issues.Add(new("move.no_controls"));
        if ((Controls & ~KnownBits) != 0)
            issues.Add(new("move.unknown_bits"));
        return issues;
    }
}

// ---------------- friends and groups ----------------

/// <summary>Offers friendship to <paramref name="Target"/> with a message.</summary>
public sealed record FriendshipOfferIntent(Guid Target, string Message) : ActionIntent
{
    public override ActionKind Kind => ActionKind.FriendshipOffer;
    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Target == Guid.Empty)
            issues.Add(new("friend.no_target"));
        string m = IntentRules.Normalised(Message ?? "");
        if (Encoding.UTF8.GetByteCount(m) > IntentRules.MaxMessageBytes)
            issues.Add(new("friend.message_too_long", $"{Encoding.UTF8.GetByteCount(m)} bytes, most {IntentRules.MaxMessageBytes}"));
        if (IntentRules.HasControlChars(m, allowLineBreaks: true))
            issues.Add(new("friend.message_control_chars"));
        return issues;
    }
    internal override TransportCommand? ToCommand() => new FriendshipOfferCommand(Target, IntentRules.Normalised(Message ?? ""));
}

/// <summary>Accepts or declines the friendship offer from <paramref name="From"/>.</summary>
public sealed record FriendshipAnswerIntent(Guid From, Guid OfferId, bool Accept, bool ViaCapability) : ActionIntent
{
    public override ActionKind Kind => ActionKind.FriendshipAnswer;
    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (From == Guid.Empty)
            issues.Add(new("friend.no_target"));
        if (OfferId == Guid.Empty && !ViaCapability)
            issues.Add(new("friend.no_offer"));   // the capability needs no conversation id; the ordinary message does
        return issues;
    }
    internal override TransportCommand? ToCommand() => new FriendshipAnswerCommand(From, OfferId, Accept, ViaCapability);
}

/// <summary>Ends the friendship with <paramref name="Friend"/>. A protected action: refused unless the user confirmed.</summary>
public sealed record FriendshipEndIntent(Guid Friend, bool Confirmed) : ActionIntent
{
    public override ActionKind Kind => ActionKind.FriendshipEnd;
    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Friend == Guid.Empty)
            issues.Add(new("friend.no_target"));
        if (!Confirmed)
            issues.Add(new("friend.not_confirmed"));
        return issues;
    }
    internal override TransportCommand? ToCommand() => new FriendshipEndCommand(Friend);
}

/// <summary>Sets the rights this agent gives <paramref name="Friend"/>. <paramref name="ModifyConfirmed"/>: the user confirmed giving the modify-objects right.</summary>
public sealed record FriendRightsIntent(Guid Friend, FriendRightsFlags Rights, bool ModifyConfirmed) : ActionIntent
{
    public override ActionKind Kind => ActionKind.FriendRights;
    private const FriendRightsFlags AllRights = FriendRightsFlags.SeeOnline | FriendRightsFlags.SeeOnMap | FriendRightsFlags.ModifyObjects;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Friend == Guid.Empty)
            issues.Add(new("friend.no_target"));
        if ((Rights & ~AllRights) != 0)
            issues.Add(new("friend.bad_rights"));
        if (Rights.HasFlag(FriendRightsFlags.SeeOnMap) && !Rights.HasFlag(FriendRightsFlags.SeeOnline))
            issues.Add(new("friend.map_needs_online"));
        if (Rights.HasFlag(FriendRightsFlags.ModifyObjects) && !ModifyConfirmed)
            issues.Add(new("friend.modify_not_confirmed"));
        return issues;
    }
    internal override TransportCommand? ToCommand() => new FriendRightsCommand(Friend, Rights);
}

public sealed record GroupChatJoinIntent(Guid Group) : ActionIntent
{
    public override ActionKind Kind => ActionKind.GroupChatJoin;
    public override IReadOnlyList<ActionIssue> Validate() => Group == Guid.Empty ? [new("group.no_group")] : [];
    internal override TransportCommand? ToCommand() => new GroupChatJoinCommand(Group);
}

/// <summary>Starts a conference: one group instant message with several agents (the official viewer's "start conference" on several chosen
/// friends). <paramref name="Session"/> is the id the caller made for it; it needs at least two others.</summary>
public sealed record ConferenceStartIntent(Guid Session, IReadOnlyList<Guid> Participants) : ActionIntent
{
    public override ActionKind Kind => ActionKind.ConferenceStart;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Session == Guid.Empty)
            issues.Add(new("conference.no_session"));
        var others = Participants.Where(p => p != Guid.Empty).Distinct().ToList();
        if (others.Count < 2)
            issues.Add(new("conference.too_few"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new StartConferenceCommand(Session, Participants.Where(p => p != Guid.Empty).Distinct().ToList());
}

public sealed record GroupChatLeaveIntent(Guid Group) : ActionIntent
{
    public override ActionKind Kind => ActionKind.GroupChatLeave;
    public override IReadOnlyList<ActionIssue> Validate() => Group == Guid.Empty ? [new("group.no_group")] : [];
    internal override TransportCommand? ToCommand() => new GroupChatLeaveCommand(Group);
}

public sealed record GroupMessageIntent(Guid Group, string Text) : ActionIntent
{
    public override ActionKind Kind => ActionKind.GroupMessage;
    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Group == Guid.Empty)
            issues.Add(new("group.no_group"));
        IntentRules.CheckMessage(issues, "group", Text, allowLineBreaks: true);
        return issues;
    }
    internal override TransportCommand? ToCommand() => new GroupMessageCommand(Group, IntentRules.Normalised(Text));
}

/// <summary>Makes <paramref name="Group"/> the active group (the empty id: none).</summary>
public sealed record ActiveGroupIntent(Guid Group) : ActionIntent
{
    public override ActionKind Kind => ActionKind.ActiveGroup;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new ActiveGroupCommand(Group);
}

public sealed record ActiveTitleIntent(Guid Group, Guid Role) : ActionIntent
{
    public override ActionKind Kind => ActionKind.ActiveTitle;
    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Group == Guid.Empty)
            issues.Add(new("group.no_group"));
        if (Role == Guid.Empty)
            issues.Add(new("group.no_role"));
        return issues;
    }
    internal override TransportCommand? ToCommand() => new ActiveTitleCommand(Group, Role);
}

// ---------------- teleport (by position on the map, cancel, offers) ----------------

/// <summary>Teleports to a position in the region with this handle (the world map's spot).</summary>
public sealed record TeleportHandleIntent(ulong RegionHandle, Vector3 Position) : ActionIntent
{
    public override ActionKind Kind => ActionKind.TeleportHandle;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (RegionHandle == 0)
            issues.Add(new("teleport.no_region"));
        static bool InRegion(float v) => float.IsFinite(v) && v >= 0 && v <= IntentRules.MaxCoordinate;
        if (!InRegion(Position.X) || !InRegion(Position.Y) || !InRegion(Position.Z))
            issues.Add(new("teleport.bad_position"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new TeleportHandleCommand(RegionHandle, Position);
}

/// <summary>Stops the teleport in progress.</summary>
public sealed record TeleportCancelIntent : ActionIntent
{
    public override ActionKind Kind => ActionKind.TeleportCancel;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new TeleportCancelCommand();
}

/// <summary>Accepts (teleports via the offer) or declines a teleport offer. Never sent unless the user chose.</summary>
public sealed record TeleportLureAnswerIntent(Guid From, Guid LureId, bool Accept) : ActionIntent
{
    public override ActionKind Kind => ActionKind.TeleportLureAnswer;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (From == Guid.Empty)
            issues.Add(new("teleport.no_target"));
        if (LureId == Guid.Empty)
            issues.Add(new("teleport.no_lure"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new TeleportLureAnswerCommand(From, LureId, Accept);
}

/// <summary>Offers a teleport to <paramref name="Target"/> with a message (the empty message sends the default text).</summary>
public sealed record TeleportOfferIntent(Guid Target, string Message) : ActionIntent
{
    public override ActionKind Kind => ActionKind.TeleportOffer;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Target == Guid.Empty)
            issues.Add(new("teleport.no_target"));
        string m = IntentRules.Normalised(Message ?? "");
        if (Encoding.UTF8.GetByteCount(m) > IntentRules.MaxMessageBytes)
            issues.Add(new("teleport.message_too_long", $"{Encoding.UTF8.GetByteCount(m)} bytes, most {IntentRules.MaxMessageBytes}"));
        if (IntentRules.HasControlChars(m, allowLineBreaks: true))
            issues.Add(new("teleport.message_control_chars"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new TeleportOfferCommand(Target, IntentRules.Normalised(Message ?? ""));
}

/// <summary>Accepts or declines a group notice's attachment. Never sent unless the user chose.</summary>
public sealed record GroupNoticeAnswerIntent(Guid Sender, Guid OfferId, bool Accept, int AssetType) : ActionIntent
{
    public override ActionKind Kind => ActionKind.GroupNoticeAnswer;
    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Sender == Guid.Empty)
            issues.Add(new("group.no_sender"));
        if (OfferId == Guid.Empty)
            issues.Add(new("group.no_offer"));
        if (AssetType is < 0 or > 255)
            issues.Add(new("group.bad_asset_type"));
        return issues;
    }
    internal override TransportCommand? ToCommand() => new GroupNoticeAnswerCommand(Sender, OfferId, Accept, AssetType);
}

// ---------------- set home, ask to be teleported ----------------

/// <summary>Sets the home location to where the agent stands. The grid decides and answers with an alert.</summary>
public sealed record SetHomeIntent : ActionIntent
{
    public override ActionKind Kind => ActionKind.SetHome;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new SetHomeCommand();
}

/// <summary>Asks <paramref name="Target"/> to offer this agent a teleport, with a message (may be empty).</summary>
public sealed record TeleportRequestIntent(Guid Target, string Message) : ActionIntent
{
    public override ActionKind Kind => ActionKind.TeleportRequest;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Target == Guid.Empty)
            issues.Add(new("teleport.no_target"));
        string m = IntentRules.Normalised(Message ?? "");
        if (Encoding.UTF8.GetByteCount(m) > IntentRules.MaxMessageBytes)
            issues.Add(new("teleport.message_too_long", $"{Encoding.UTF8.GetByteCount(m)} bytes, most {IntentRules.MaxMessageBytes}"));
        if (IntentRules.HasControlChars(m, allowLineBreaks: true))
            issues.Add(new("teleport.message_control_chars"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new TeleportRequestCommand(Target, IntentRules.Normalised(Message ?? ""));
}

// ---------------- the block list ----------------

/// <summary>Adds an entry to the block list (a resident, an object, an object by name, a group), or changes the flags of one already there.
/// External entries stay on this side and are never sent, so they are not an intent. <paramref name="Id"/> is empty for a by-name entry.</summary>
public sealed record MuteIntent(MuteKind BlockKind, Guid Id, string Name, uint Flags = 0) : ActionIntent
{
    /// <summary>The longest name the grid's list keeps (the protocol's name field).</summary>
    public const int MaxNameChars = 254;

    public override ActionKind Kind => ActionKind.Mute;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (!Enum.IsDefined(BlockKind) || BlockKind == MuteKind.External)
            issues.Add(new("mute.bad_kind"));
        if (BlockKind == MuteKind.ByName)
        {
            if (Id != Guid.Empty)
                issues.Add(new("mute.name_with_id"));
            if (string.IsNullOrWhiteSpace(Name))
                issues.Add(new("mute.no_name"));
        }
        else if (Id == Guid.Empty)
            issues.Add(new("mute.no_target"));
        if ((Name ?? "").Length > MaxNameChars)
            issues.Add(new("mute.name_too_long", $"{Name!.Length} characters, most {MaxNameChars}"));
        if (IntentRules.HasControlChars(Name ?? "", allowLineBreaks: false) || (Name ?? "").Contains('|'))
            issues.Add(new("mute.bad_name"));   // the grid's list file ends a name at a bar
        if ((Flags & ~MuteFlag.All) != 0)
            issues.Add(new("mute.bad_flags"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new MuteAddCommand(new MuteEntry(BlockKind, Id, Name.Trim(), Flags));
}

/// <summary>Removes an entry from the block list.</summary>
public sealed record UnmuteIntent(Guid Id, string Name) : ActionIntent
{
    public override ActionKind Kind => ActionKind.Unmute;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (Id == Guid.Empty && string.IsNullOrWhiteSpace(Name))
            issues.Add(new("mute.no_target"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new MuteRemoveCommand(Id, (Name ?? "").Trim());
}

/// <summary>Where on an object a click landed, as the region wants it in a grab or release: surface coordinates (texture and surface),
/// the face, the point in region coordinates, and the surface's normal and tangent along U.</summary>
public readonly record struct TouchSurface(System.Numerics.Vector2 Uv, System.Numerics.Vector2 St, int Face, Vector3 Position, Vector3 Normal, Vector3 Binormal)
{
    /// <summary>What the official viewer sends when a click found no surface: face -1 and zero vectors.</summary>
    public static TouchSurface None => new(new(-1f, -1f), new(-1f, -1f), -1, default, default, default);

    public bool IsFinite => float.IsFinite(Uv.X) && float.IsFinite(Uv.Y) && float.IsFinite(St.X) && float.IsFinite(St.Y)
        && IntentRules.Finite(Position) && IntentRules.Finite(Normal) && IntentRules.Finite(Binormal);
}

/// <summary>The mouse went down on an object: the start of a touch (the region tells the object's scripts).</summary>
public sealed record GrabIntent(uint LocalId, TouchSurface Surface, Vector3 GrabOffset = default) : ActionIntent
{
    public override ActionKind Kind => ActionKind.Grab;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (LocalId == 0)
            issues.Add(new("grab.no_object"));
        if (!Surface.IsFinite || !IntentRules.Finite(GrabOffset))
            issues.Add(new("grab.bad_surface"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new GrabCommand(LocalId, GrabOffset, Surface);
}

/// <summary>The mouse went up: the end of the touch started by <see cref="GrabIntent"/>.</summary>
public sealed record ReleaseIntent(uint LocalId, TouchSurface Surface) : ActionIntent
{
    public override ActionKind Kind => ActionKind.Release;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (LocalId == 0)
            issues.Add(new("release.no_object"));
        if (!Surface.IsFinite)
            issues.Add(new("release.bad_surface"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new ReleaseCommand(LocalId, Surface);
}

/// <summary>Sit on the ground where the avatar stands.</summary>
public sealed record SitGroundIntent : ActionIntent
{
    public override ActionKind Kind => ActionKind.SitGround;
    public override IReadOnlyList<ActionIssue> Validate() => [];
    internal override TransportCommand? ToCommand() => new SitGroundCommand();
}

/// <summary>Ask the region for an object's name, description, owner and creator. The request selects the object on the region;
/// <paramref name="Hold"/> keeps it selected (until an ObjectDeselect) instead of letting go once the properties arrive.</summary>
public sealed record ObjectPropertiesIntent(uint LocalId, bool Hold = false) : ActionIntent
{
    public override ActionKind Kind => ActionKind.ObjectProperties;
    public override IReadOnlyList<ActionIssue> Validate() => LocalId == 0 ? [new("properties.no_object")] : [];
    internal override TransportCommand? ToCommand() => new RequestObjectPropertiesCommand(LocalId, Hold);
}

/// <summary>Take an object into inventory (its Objects folder), or take a copy of it.</summary>
public sealed record TakeIntent(uint LocalId, bool Copy) : ActionIntent
{
    public override ActionKind Kind => ActionKind.Take;
    public override IReadOnlyList<ActionIssue> Validate() => LocalId == 0 ? [new("take.no_object")] : [];
    internal override TransportCommand? ToCommand() => new TakeObjectCommand(LocalId, Copy);
}

/// <summary>Ask the region for the parcel at a point in the region (for About Land).</summary>
public sealed record ParcelInfoIntent(float X, float Y) : ActionIntent
{
    public override ActionKind Kind => ActionKind.ParcelInfo;

    public override IReadOnlyList<ActionIssue> Validate() =>
        float.IsFinite(X) && float.IsFinite(Y) && X >= 0f && Y >= 0f && X <= IntentRules.MaxCoordinate && Y <= IntentRules.MaxCoordinate ? [] : [new("parcel.bad_position")];

    internal override TransportCommand? ToCommand() => new RequestParcelCommand(X, Y);
}

// ---------------- what scripts ask of the viewer ----------------

/// <summary>Answers a script dialog with a button (its index in the script's list, 0 to 11), or (a text box) with typed text and index 0.
/// The label is the button's text or the typed line; it may be empty, and holds no control characters.</summary>
public sealed record ScriptDialogReplyIntent(SimRef? Sim, Guid ObjectId, int Channel, int ButtonIndex, string Label) : ActionIntent
{
    /// <summary>The most buttons a dialog has, and so the highest index plus one.</summary>
    public const int MaxButtons = 12;

    /// <summary>The most bytes (UTF-8) the reply's label may carry: its length field in the message is one byte.</summary>
    public const int MaxLabelBytes = 254;

    public override ActionKind Kind => ActionKind.ScriptDialogReply;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (ObjectId == Guid.Empty)
            issues.Add(new("dialog.no_object"));
        if (ButtonIndex is < 0 or >= MaxButtons)
            issues.Add(new("dialog.bad_button"));
        string label = Label ?? "";
        if (Encoding.UTF8.GetByteCount(label) > MaxLabelBytes)
            issues.Add(new("dialog.label_too_long", $"{Encoding.UTF8.GetByteCount(label)} bytes, most {MaxLabelBytes}"));
        if (IntentRules.HasControlChars(label, allowLineBreaks: true))
            issues.Add(new("dialog.label_control_chars"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new ScriptDialogReplyCommand(Sim, ObjectId, Channel, ButtonIndex, Label ?? "");
}

/// <summary>Answers a permission request: the mask of permissions given, 0 for none.</summary>
public sealed record ScriptPermissionAnswerIntent(SimRef? Sim, Guid TaskId, Guid ItemId, int Granted) : ActionIntent
{
    public override ActionKind Kind => ActionKind.ScriptPermissionAnswer;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (TaskId == Guid.Empty)
            issues.Add(new("permission.no_task"));
        if (ItemId == Guid.Empty)
            issues.Add(new("permission.no_item"));
        if (Granted < 0)
            issues.Add(new("permission.bad_mask"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new ScriptPermissionAnswerCommand(Sim, TaskId, ItemId, Granted);
}

/// <summary>Opens a web address a script offered, after the user chose to. Only http and https addresses of at most 256 characters.</summary>
public sealed record OpenUrlIntent(string Url) : ActionIntent
{
    /// <summary>The longest address a script can offer: the request's field holds 256 characters.</summary>
    public const int MaxChars = 256;

    public override ActionKind Kind => ActionKind.OpenUrl;

    /// <summary>True for an absolute http or https address with a host and no whitespace or control characters.</summary>
    public static bool IsOpenable(string? url) =>
        !string.IsNullOrEmpty(url) && url.Length <= MaxChars && !url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);

    public override IReadOnlyList<ActionIssue> Validate()
    {
        if (string.IsNullOrEmpty(Url))
            return [new("url.empty")];
        if (Url.Length > MaxChars)
            return [new("url.too_long", $"{Url.Length} characters, most {MaxChars}")];
        return IsOpenable(Url) ? [] : [new("url.not_http")];
    }

    internal override TransportCommand? ToCommand() => new OpenUrlCommand(Url);
}
