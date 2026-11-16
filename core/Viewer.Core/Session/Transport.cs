// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Identity;
using Viewer.Core.World;

namespace Viewer.Core.Session;

/// <summary>
/// The protocol adapter as the session core sees it (ADR-005): plain .NET types only. The LibreMetaverse
/// implementation lives in Viewer.Core.Protocol; tests drive the core through a fake. One transport per session,
/// never shared. Events arrive on the adapter's network threads.
/// </summary>
public interface ISessionTransport : IDisposable
{
    /// <summary>Everything the grid tells the session, as plain records.</summary>
    event Action<TransportEvent>? Event;

    /// <summary>Runs the grid login. The password is used for this call only.</summary>
    Task<TransportLoginResult> LoginAsync(TransportLoginRequest request, CancellationToken ct);

    Task LogoutAsync(CancellationToken ct);

    /// <summary>The simulator the library considers current; null until it sets one (the W-21 race).</summary>
    SimRef? CurrentSim { get; }

    int ConnectedSimulatorCount { get; }

    /// <summary>The draw distance reported to the simulator (AgentUpdate camera far), which it culls objects by. Already clamped.</summary>
    void SetDrawDistance(float metres);
}

/// <summary>A transport that can ask again for a region's map block (its authoritative size and map
/// image; the first request, at connect, is the transport's own).</summary>
public interface IMapBlockRequester
{
    void RequestMapBlock(SimRef sim);
}

/// <summary>A transport that sends the user's own avatar's controls (the AgentUpdate message). The state it is given
/// stays the agent's state: the transport's own periodic updates carry the same controls, rotation and camera.</summary>
public interface IAgentControlTransport
{
    void SendAgentUpdate(Viewer.Motion.AgentUpdate update);

    /// <summary>Tells the region whether the avatar runs (the reliable SetAlwaysRun message), and keeps the state to tell
    /// again after each region arrival.</summary>
    void SendAlwaysRun(bool run);
}

/// <summary>A transport that asks the region to start or stop an animation on the user's own avatar (the AgentAnimation
/// message). The region plays it for everyone and lists it in what the avatar plays.</summary>
public interface IAgentAnimationTransport
{
    void SendAnimation(Guid animation, bool start);

    /// <summary>Several requests in one message, in the order given: the region answers once, with the list as it is after all of them.</summary>
    void SendAnimations(IReadOnlyList<(Guid Animation, bool Start)> requests);
}

/// <summary>
/// A simulator, by reference identity (one instance per simulator per transport). Carries no LibreMetaverse type.
/// </summary>
public sealed class SimRef(ulong handle, string name)
{
    public ulong Handle { get; } = handle;
    public string Name { get; } = name;
    public int GridX => (int)((Handle >> 32) / 256);
    public int GridY => (int)((Handle & 0xFFFFFFFF) / 256);
    public override string ToString() => $"{Name}@{GridX},{GridY}";
}

/// <param name="Neighbours">Whether the session wants child circuits to the neighbouring regions the grid announces
/// (the library's MultipleSims setting). False keeps one connected region.</param>
public sealed record TransportLoginRequest(
    string LoginUri, string FirstName, string LastName, Secret Password, string StartLocation, ViewerIdentity Identity, bool Neighbours = false, LoginExtras? Extras = null);

public sealed record TransportLoginResult(
    bool Ok, string? FailureCode, string? FailureMessage, Guid AgentId, Secret? SessionId, int LoginRegionSizeX, int LoginRegionSizeY)
{
    /// <summary>What the login service said (reason, message id and arguments, error code, multifactor hash); null when the transport has nothing beyond the code and message.</summary>
    public LoginReply? Reply { get; init; }

    /// <summary>Set when the login was refused because the server certificate was not accepted (code "certificate").</summary>
    public Viewer.Core.Net.CertificateRefusal? Certificate { get; init; }

    public static TransportLoginResult Failed(string code, string? message) => new(false, code, message, Guid.Empty, null, 0, 0);
}

public enum LoginPhase { ConnectingToSim, Failed, Other }

/// <summary>A grid event, translated by the adapter.</summary>
public abstract record TransportEvent;

public sealed record LoginProgressed(LoginPhase Phase, string? Message) : TransportEvent;

/// <summary>The circuit to <paramref name="Sim"/> is up and its handshake parsed. Sizes are what the library reports.</summary>
/// <param name="ServerBakedAppearance">RegionProtocols bit 0 (AgentAppearanceService): the region bakes avatars.</param>
/// <param name="Terrain">: the handshake's legacy terrain texturing settings (null when unknown: older captures).</param>
/// <param name="WaterHeight">: the handshake's water height in metres (null when unknown: older captures).</param>
/// <param name="IsEstateManager">: whether the handshake says the agent can manage the region's estate (null when unknown).</param>
public sealed record SimConnected(SimRef Sim, int SimSizeX, int SimSizeY, bool ServerBakedAppearance = false, Viewer.Core.World.RegionTerrain? Terrain = null, float? WaterHeight = null, bool? IsEstateManager = null) : TransportEvent;

/// <summary>The agent's god level changed (the GrantGodlikePowers message): above zero the agent is godlike.</summary>
public sealed record GodLevelReceived(int Level) : TransportEvent;

/// <summary>
/// The region's environment as the grid serves it: the raw LLSD (XML) of the ExtEnvironment capability's answer,
/// kept raw so captures replay it exactly; <paramref name="Why"/> says why there is none (no capability, a refusal).
/// </summary>
public sealed record EnvironmentReceived(SimRef Sim, string? Llsd, string? Why) : TransportEvent;

/// <summary>
/// The environment of one parcel as the grid serves it (the ExtEnvironment capability asked with the parcel's local id): the raw LLSD, or why there is none.
/// A parcel that has no environment of its own answers without a day cycle.
/// </summary>
public sealed record ParcelEnvironmentReceived(SimRef Sim, int ParcelId, string? Llsd, string? Why) : TransportEvent;

/// <summary>The library dropped its circuit to <paramref name="Sim"/> (a neighbour out of range, or a region left behind).</summary>
public sealed record SimDisconnected(SimRef Sim) : TransportEvent;

public sealed record EventQueueRunning(SimRef Sim) : TransportEvent;

public sealed record MovementComplete(SimRef Sim) : TransportEvent;

/// <summary>Why a circuit ended, as the library reports it: our own logout, the circuit going quiet (a timeout), the grid ending the session
/// (its kick message carries the reason), the simulator shutting down, or anything else.</summary>
public enum DisconnectKind { Other, ClientInitiated, NetworkTimeout, ServerInitiated, SimShutdown }

/// <summary>The circuit ended. <paramref name="Message"/> is the grid's own text for a server-initiated end (a kick's reason), else empty.</summary>
public sealed record Disconnected(string Reason, DisconnectKind Kind = DisconnectKind.Other, string Message = "") : TransportEvent;

/// <summary>
/// A world update from a simulator, as a notification: the session raises one (Kind "object.update", "object.move",
/// "object.kill", "avatar.update") for every world input the first-burst rule accepts. The data travels in the records below.
/// </summary>
public sealed record SimWorldEvent(SimRef? Sim, string Kind) : TransportEvent;

/// <summary>A full object update (prims only; trees, grass and particles are not forwarded yet).</summary>
/// <param name="SimParentAvatar">The simulator's own avatar object for the prim's parent, if it holds one (the wearer).</param>
public sealed record ObjectUpdated(SimRef? Sim, PrimData Prim, bool EventIsAttachment, AvatarData? SimParentAvatar) : TransportEvent;

/// <summary>A full avatar update.</summary>
public sealed record AvatarUpdated(SimRef? Sim, AvatarData Avatar) : TransportEvent;

/// <summary>A terse (motion) update for a prim or an avatar.</summary>
public sealed record ObjectMoved(SimRef? Sim, uint LocalId, bool IsAvatar, Vector3 Position, Quaternion Rotation, Vector3 Velocity,
    Vector3 Acceleration, Vector3 AngularVelocity, Vector4 FootPlane = default) : TransportEvent;

/// <summary>The time dilation a region's object update carried (1 = real time), raised when it changes.</summary>
public sealed record RegionTimeDilation(SimRef? Sim, float Dilation) : TransportEvent;

/// <summary>A terse update for this session's own avatar, stamped on arrival (raised before its <see cref="ObjectMoved"/>).</summary>
public sealed record SelfMoved(SimRef? Sim, SelfMotion Motion) : TransportEvent;

/// <summary>An AvatarAppearance for any avatar (ours included). Received only: the core never sends one.</summary>
public sealed record AvatarAppearanceReceived(SimRef? Sim, AvatarAppearanceData Appearance) : TransportEvent;

/// <summary>The animations the region says an avatar is playing (the AvatarAnimation message): the whole list each time,
/// empty when it plays none.</summary>
/// <summary>The region's camera constraint: a plane (normal x, y, z and distance w) the third-person camera must stay on the avatar's side of; all zero: none.</summary>
public sealed record CameraConstraintReceived(SimRef? Sim, Vector4 Plane) : TransportEvent;

/// <summary>Scripts in the region took or gave back controls of the user's avatar (the ScriptControlChange message): each entry says whether the controls (a bit set) were taken, and whether the avatar still gets them too.</summary>
public sealed record ScriptControlChanged(SimRef? Sim, IReadOnlyList<ScriptControlEntry> Entries) : TransportEvent;

/// <summary>One block of a ScriptControlChange message.</summary>
public readonly record struct ScriptControlEntry(bool Take, uint Controls, bool PassToAgent);
public sealed record AvatarAnimationsReceived(SimRef? Sim, Guid AgentId, IReadOnlyList<PlayingAnimation> Playing) : TransportEvent;

/// <summary>Objects or avatars removed by the simulator.</summary>
public sealed record ObjectsKilled(SimRef? Sim, uint[] LocalIds) : TransportEvent;

/// <summary>A tree (one of the region's built-in trees) came or changed. The world draws it from its species table.</summary>
public sealed record TreeUpdated(SimRef? Sim, Viewer.Core.World.TreeData Tree) : TransportEvent;

/// <summary>
/// The region's glTF material overrides for one object (the GLTF override message): every face the message names, each
/// with its override. A message that names no face clears all of the object's overrides. May arrive before the object.
/// </summary>
public sealed record MaterialOverridesReceived(SimRef? Sim, uint LocalId, IReadOnlyList<Viewer.Core.World.FaceMaterialOverride> Faces) : TransportEvent;

/// <summary>
/// An object's render material parameter (its faces' PBR material ids), raised when it differs from what the object had
/// (null: the parameter was removed). Read from the object update itself; may come just before or after its update.
/// </summary>
public sealed record RenderMaterialsReceived(SimRef? Sim, uint LocalId, IReadOnlyList<Viewer.Core.World.RenderMaterialEntry>? Faces) : TransportEvent;

/// <summary>Legacy materials the region served for ids its objects' texture entries name (the RenderMaterials capability).</summary>
public sealed record LegacyMaterialsReceived(SimRef? Sim, IReadOnlyList<Viewer.Core.World.LegacyMaterial> Materials) : TransportEvent;

/// <summary>A terrain patch from a simulator: patch coordinates, edge length in metres and its heights (row-major, size x size).</summary>
public sealed record LandPatch(SimRef? Sim, int X, int Y, int PatchSize, float[]? Heights = null) : TransportEvent;

/// <summary>A region's wind layer: 16 x 16 velocities along x and along y (metres a second), row by row from the south-west corner.</summary>
public sealed record RegionWindReceived(SimRef? Sim, float[] X, float[] Y) : TransportEvent;

/// <summary>A map block for grid cell (GridX, GridY): SizeX/SizeY are 0 for a legacy region that carries no size. <paramref name="Name"/>
/// is the region's name as the map knows it (at SimConnected the library may not have the name yet).</summary>
/// <param name="MapImageId">: the region's map tile texture (a J2K asset), when the grid names one.</param>
/// <param name="Access">: the region's access level byte (13 PG, 21 mature, 42 adult, 255 for a cell with no region).</param>
public sealed record MapBlockSize(int GridX, int GridY, int SizeX, int SizeY, string? Name = null, Guid? MapImageId = null, int Access = 0) : TransportEvent;

/// <summary>The hover height an AvatarAppearance message states for an avatar (its AppearanceHover block, in metres), or null when the
/// message has no such block. Raised beside the <see cref="AvatarAppearanceReceived"/> of the same message.</summary>
public sealed record AvatarHoverReceived(SimRef? Sim, Guid AgentId, float? Z) : TransportEvent;

// ---------------- communication events (chat, instant messages, names) ----------------
// These carry what people write, so the text travels as a MessageBody (redacted by construction, ADR-019) and none of
// them is ever written to a capture (RecordingTransport skips them) or to a log.

/// <summary>Who a local chat line comes from.</summary>
public enum ChatSource { System, Agent, Object }

/// <summary>How well this agent heard a local chat line (the region decides by distance and the line's range).</summary>
public enum ChatAudible { Not, Barely, Fully }

/// <summary>The kind of a local chat line: its range, a typing start or stop, or a script's direct kinds.</summary>
public enum ChatLineType { Whisper, Say, Shout, StartTyping, StopTyping, Debug, OwnerSay, RegionSayTo, RegionSay, Other }

/// <summary>A local chat line (or a typing start/stop) from the region.</summary>
/// <param name="SourceId">The speaker: an agent's or an object's id (empty for the region itself).</param>
/// <param name="OwnerId">The owner of an object speaker (an agent speaker's own id).</param>
/// <param name="FromName">The speaker's name as the region gives it (an object's name for object chat).</param>
public sealed record ChatReceived(SimRef? Sim, Guid SourceId, Guid OwnerId, string FromName, ChatSource Source, ChatLineType Type,
    ChatAudible Audible, Vector3 Position, MessageBody Text) : TransportEvent;

/// <summary>The kinds of instant message this viewer acts on; everything else is <see cref="Other"/>.</summary>
public enum ImKind { Message, StartTyping, StopTyping, MessageBox, GroupMessage, Other }

/// <summary>An instant message (or typing start/stop) for this agent, live or delivered from the grid's offline store.</summary>
/// <param name="SessionId">The conversation id the sender gave.</param>
/// <param name="Dialog">The protocol's dialog number, for counting the kinds this viewer does not handle.</param>
/// <param name="Offline">True for a message the grid stored while this agent was away.</param>
/// <param name="SentAt">When the grid says it was sent, for a stored message (null when the grid gives none).</param>
/// <param name="Group">True for a group or conference message (not handled by one-to-one conversations).</param>
public sealed record InstantMessageReceived(Guid FromAgent, string FromName, Guid ToAgent, Guid SessionId, ImKind Kind, int Dialog,
    bool Offline, DateTimeOffset? SentAt, bool Group, MessageBody Text) : TransportEvent;

/// <summary>Agent names the grid answered with (a name request's reply).</summary>
public sealed record AgentNamesReceived(IReadOnlyDictionary<Guid, string> Names) : TransportEvent;

/// <summary>A transport that can ask the grid for agents' names (the answer comes back as <see cref="AgentNamesReceived"/>).
/// A read, not a user command, so it does not go through the action pipeline.</summary>
public interface IAgentNameRequester
{
    void RequestAgentNames(IReadOnlyList<Guid> agents);
}

/// <summary>A transport that can ask the grid for the instant messages it stored while the agent was away. They come back
/// as <see cref="InstantMessageReceived"/> with <c>Offline</c> set. A read, not a user command.</summary>
public interface IOfflineMessageRequester
{
    void RequestOfflineMessages();
}

// ---------------- friends and groups ----------------
// Like chat, these carry who people are and what they wrote (a friendship offer's message, a group notice), so none of them is
// ever written to a capture or a log with its content.

/// <summary>The three things a friend can be allowed to do, in either direction (the protocol's bit values).</summary>
[Flags]
public enum FriendRightsFlags { None = 0, SeeOnline = 1, SeeOnMap = 2, ModifyObjects = 4 }

/// <summary>One friend. <paramref name="Granted"/> is what this agent allows the friend; <paramref name="Received"/> is what the friend allows this agent.</summary>
public sealed record FriendEntry(Guid Id, string Name, bool Online, FriendRightsFlags Granted, FriendRightsFlags Received);

/// <summary>The friend list the login reply carried (everyone starts offline until the grid says otherwise).</summary>
public sealed record FriendsListed(IReadOnlyList<FriendEntry> Friends) : TransportEvent;

/// <summary>A friend came online or went offline.</summary>
public sealed record FriendPresenceChanged(Guid Friend, bool Online) : TransportEvent;

/// <summary>The rights between this agent and a friend changed (either direction).</summary>
public sealed record FriendRightsChanged(Guid Friend, FriendRightsFlags Granted, FriendRightsFlags Received) : TransportEvent;

/// <summary>Someone offered friendship. <paramref name="OfferId"/> is the conversation id the answer must carry; <paramref name="Online"/>
/// is false for an offer the grid stored while this agent was away (it is answered through the grid's capability).</summary>
public sealed record FriendshipOffered(Guid From, string FromName, Guid OfferId, bool Online, MessageBody Message) : TransportEvent;

/// <summary>Someone answered this agent's offer: accepted (they are now a friend) or declined.</summary>
public sealed record FriendshipAnswered(Guid From, string FromName, bool Accepted) : TransportEvent;

/// <summary>The other person ended a friendship.</summary>
public sealed record FriendshipEnded(Guid Friend) : TransportEvent;

/// <summary>One group this agent belongs to.</summary>
public sealed record GroupEntry(Guid Id, string Name, bool AcceptNotices, bool ListInProfile);

/// <summary>The groups this agent belongs to.</summary>
public sealed record GroupsListed(IReadOnlyList<GroupEntry> Groups) : TransportEvent;

/// <summary>The active group and the agent's title in it changed (an empty id: no active group).</summary>
public sealed record ActiveGroupChanged(Guid GroupId, string GroupName, string Title) : TransportEvent;

/// <summary>One title a group offers: the role it belongs to and whether it is the one in use.</summary>
public sealed record GroupTitleEntry(Guid Role, string Title, bool Selected);

/// <summary>The titles a group offers this agent.</summary>
public sealed record GroupTitlesListed(Guid GroupId, IReadOnlyList<GroupTitleEntry> Titles) : TransportEvent;

/// <summary>The grid answered a request to join a group's chat session.</summary>
public sealed record GroupChatJoined(Guid GroupId, bool Success) : TransportEvent;

/// <summary>A group notice. <paramref name="OfferId"/> is the conversation id an attachment answer must carry. <paramref name="Requested"/>
/// is true for a notice the user asked to see again.</summary>
public sealed record GroupNoticeReceived(Guid GroupId, Guid Sender, string FromName, MessageBody Subject, MessageBody Text, bool HasAttachment,
    string AttachmentName, int AssetType, Guid OfferId, DateTimeOffset? SentAt, bool Requested) : TransportEvent;

/// <summary>A transport that can ask the grid for the groups this agent is in and for a group's titles (answers come back as
/// <see cref="GroupsListed"/> and <see cref="GroupTitlesListed"/>). Reads, not user commands.</summary>
public interface IGroupRequester
{
    void RequestGroups();
    void RequestGroupTitles(Guid group);
}

/// <summary>The agent's money balance as the grid last gave it.</summary>
public sealed record BalanceChanged(int Balance) : TransportEvent;

/// <summary>A transport that reads the grid's own settings from the agent's region and can ask for the money balance (the answer is a
/// <see cref="BalanceChanged"/> event).</summary>
public interface IGridFeaturesSource
{
    /// <param name="gridInfo">What the grid's own grid-info document says, behind the login reply's values.</param>
    GridFeatures GridFeatures(GridLoginValues? gridInfo = null);
    void RequestBalance();

    /// <summary>Whether the agent's region offers a capability by this name.</summary>
    bool HasRegionCapability(string name);
}

// ---------------- teleport, the world map and the mini-map ----------------
// Offers and friends' positions say who people are and where, so those records are communication events: never written to a
// capture or a log with their content.

/// <summary>Where a teleport is: the grid said it began, gave a progress text, finished it, failed it, or the user's cancel was acknowledged.
/// <see cref="Local"/>: a teleport inside the region (no new simulator).</summary>
public enum TeleportPhase { Started, Progress, Finished, Local, Failed, Cancelled }

/// <summary>A teleport message from the grid. <paramref name="Message"/> is the progress text or the failure reason exactly as the grid
/// sent it (a short key or a sentence); <paramref name="CanCancel"/> is false when the grid forbids cancelling; <paramref name="RegionName"/>
/// is the region a failure names (empty when it names none).</summary>
public sealed record TeleportProgressed(TeleportPhase Phase, string Message, bool CanCancel, string RegionName = "") : TransportEvent;

/// <summary>Someone offered this agent a teleport. <paramref name="LureId"/> is the offer's conversation id; the answer must carry it.</summary>
public sealed record TeleportOffered(Guid From, string FromName, Guid LureId, MessageBody Message) : TransportEvent;

/// <summary>One avatar of a region's coarse-location list: its id and region position in metres (a height of 1,020 or more means unknown).</summary>
public readonly record struct CoarseAvatar(Guid Id, System.Numerics.Vector3 Position);

/// <summary>The region's coarse-location list: everyone it tells this agent about, replacing the list before.</summary>
public sealed record CoarseLocationsUpdated(SimRef? Sim, IReadOnlyList<CoarseAvatar> Avatars) : TransportEvent;

/// <summary>The grid's answer to "where is this friend": the region (by handle) and the position in it.</summary>
public sealed record FriendLocated(Guid Friend, ulong RegionHandle, System.Numerics.Vector3 Position) : TransportEvent;

/// <summary>A transport that can ask the grid for map data. Reads, not user commands; the answers come back as <see cref="MapBlockSize"/>
/// and <see cref="FriendLocated"/>.</summary>
public interface IMapRequester
{
    /// <summary>The map blocks for the cells min..max (grid coordinates, both ends included), non-existent cells too.</summary>
    void RequestMapBlocks(int minX, int minY, int maxX, int maxY);

    /// <summary>The regions whose name matches <paramref name="name"/>.</summary>
    void RequestRegionNamed(string name);

    /// <summary>Where a friend who allows it is on the map.</summary>
    void RequestFriendLocation(Guid friend);
}

/// <summary>A transport that can fetch a map tile picture (JPEG bytes) from the grid's map server. A read, through the outbound policy.</summary>
public interface IMapTileTransport
{
    Task<Viewer.Core.Assets.AssetFetchResult> FetchMapTileAsync(int level, int gridX, int gridY, CancellationToken ct);
}

/// <summary>Someone asks this agent to offer them a teleport. Never answered by itself.</summary>
public sealed record TeleportRequested(Guid From, string FromName, MessageBody Message) : TransportEvent;

/// <summary>An alert message from the grid: its text, its notification key when it has one, and whether it asked to be modal.</summary>
public sealed record ServerAlert(string Text, string Key, bool Modal) : TransportEvent;

/// <summary>Why a parcel keeps this agent out.</summary>
public enum ParcelDenial { Banned, NotOnList, NotInGroup }

/// <summary>The grid says a nearby parcel will not let this agent in (the parcel-properties message with a collision status). The bitmap is that
/// parcel's cells, one bit per 4 m cell, 64 by 64 for a 256 m region (the wall at its edge is drawn from it); null when the message had none.</summary>
public sealed record ParcelCollision(ParcelDenial Kind, byte[]? Bitmap = null, bool UsePassList = false, SimRef? Sim = null) : TransportEvent;

// ---------------- objects, seats and parcels ----------------

/// <summary>The region answered a sit request: where the seat's camera goes (offsets relative to the object), whether it forces mouselook,
/// and the seat's own position and rotation relative to the object.</summary>
public sealed record SitResponded(Guid ObjectId, System.Numerics.Vector3 EyeOffset, System.Numerics.Vector3 AtOffset, bool ForceMouselook, bool Autopilot,
    System.Numerics.Vector3 SitPosition, System.Numerics.Quaternion SitRotation) : TransportEvent;

/// <summary>An object's properties as the region sent them for a selection. Permission masks use the protocol's bits.</summary>
public sealed record ObjectPropertiesReceived(Guid ObjectId, string Name, string Description, Guid OwnerId, Guid CreatorId, Guid GroupId,
    DateTimeOffset? Created, uint OwnerMask, uint EveryoneMask, uint NextOwnerMask, int SalePrice, bool ForSale, byte SaleType = 0) : TransportEvent;

/// <summary>The parcel the agent stands in, as its region reports it when the agent arrives and whenever the agent crosses into another.</summary>
/// <param name="SoundLocal">The parcel lets sounds be heard only inside it (its "sounds only within parcel" flag).</param>
/// <param name="Bitmap">The parcel's cells (one bit per 4 m square of the region, row by row from the south-west corner), when the region sent them.</param>
public sealed record AgentParcelChanged(SimRef Sim, string Name, int LocalId, bool SoundLocal = false, byte[]? Bitmap = null) : TransportEvent;

// ---------------- sounds in the world ----------------

/// <summary>A one-shot sound at a fixed point (the SoundTrigger message): <paramref name="Position"/> is local to the region named by
/// <paramref name="RegionHandle"/>. Collision sounds, llTriggerSound and gesture sounds arrive this way.</summary>
public sealed record SoundTriggered(SimRef? Sim, Guid SoundId, Guid OwnerId, Guid ObjectId, Guid ParentId, ulong RegionHandle, Vector3 Position, float Gain) : TransportEvent;

/// <summary>An object's own sound (the AttachedSound message, or the sound fields of an object update): play, loop, queue, sync or stop,
/// by the protocol's flag bits (<see cref="SoundFlagBits"/>). A null sound clears or stops the object's sound.</summary>
/// <param name="CutoffRadius">How far from the listener the sound may be heard (0: no limit); sent only with object updates.</param>
/// <param name="FromObjectUpdate">The fields came with an object update, which repeats them: the same sound again only updates its level.</param>
public sealed record AttachedSoundReceived(SimRef? Sim, Guid ObjectId, Guid SoundId, Guid OwnerId, float Gain, byte Flags, float CutoffRadius = 0, bool FromObjectUpdate = false) : TransportEvent;

/// <summary>An object's sound changes its level (the AttachedSoundGainChange message).</summary>
public sealed record AttachedSoundGainChanged(SimRef? Sim, Guid ObjectId, float Gain) : TransportEvent;

/// <summary>A sound an object will play soon, to be fetched ahead (the PreloadSound message).</summary>
public sealed record SoundPreloaded(SimRef? Sim, Guid ObjectId, Guid SoundId, Guid OwnerId) : TransportEvent;

/// <summary>A quarter of a region's parcel overlay (the ParcelOverlay message): one byte per 4 m square, row by row, carrying the
/// square's ownership and its edge and sound flags.</summary>
public sealed record ParcelOverlayReceived(SimRef? Sim, int Chunk, byte[] Data) : TransportEvent;

/// <summary>The protocol's sound flag bits (the official viewer's lldefs.h names them the same way).</summary>
public static class SoundFlagBits
{
    public const byte Loop = 1, SyncMaster = 2, SyncSlave = 4, SyncPending = 8, Queue = 16, Stop = 32;
}

/// <summary>A parcel's properties for About Land (read-only).</summary>
public sealed record ParcelDetailsReceived(int LocalId, string Name, string Description, Guid OwnerId, bool GroupOwned, int Area,
    bool UseAccessList, bool UseBanList, bool GroupAccessOnly, bool DenyAnonymous, bool DenyAgeUnverified, bool UsePassList, int PassPrice, float PassHours,
    bool ForSale = false, int SalePrice = 0) : TransportEvent;

// ---------------- what scripts ask of the viewer ----------------
// These carry what scripts and objects write (a dialog's message, a web address, an object's words), so they are communication
// events: never written to a capture, and logged by kind and count only.

/// <summary>A script dialog (llDialog or llTextBox). <paramref name="GroupOwned"/> is set when the object's owner is a group (the
/// region sends no first name; the last name is the group's). A text box is a dialog whose buttons include the text-box marker.</summary>
public sealed record ScriptDialogReceived(SimRef? Sim, Guid ObjectId, Guid OwnerId, string ObjectName, string OwnerName, bool GroupOwned,
    MessageBody Message, int Channel, IReadOnlyList<string> Buttons) : TransportEvent;

/// <summary>A script asks for permissions (llRequestPermissions). <paramref name="Questions"/> is the permission bit mask as sent.</summary>
public sealed record ScriptPermissionRequested(SimRef? Sim, Guid TaskId, Guid ItemId, string ObjectName, string OwnerName, int Questions) : TransportEvent;

/// <summary>A script offers to open a web address (llLoadURL). Nothing is opened by this event.</summary>
public sealed record LoadUrlRequested(SimRef? Sim, Guid ObjectId, Guid OwnerId, bool OwnerIsGroup, string ObjectName, MessageBody Message, string Url) : TransportEvent;

/// <summary>A script asks the viewer to show a place on the world map (llMapDestination). <paramref name="Flags"/> are the request's option bits.</summary>
public sealed record MapDestinationRequested(string ObjectName, string RegionName, Vector3 Position, Vector3 LookAt, uint Flags) : TransportEvent;

/// <summary>An instant message from an object (llInstantMessage and the region's own notices). It opens no conversation.
/// <paramref name="Location"/> is the place text the region puts in the message ("Region/x/y/z"), empty when it gave none;
/// <paramref name="FromSystem"/> is set for the grid's own sender (no object).</summary>
public sealed record ObjectInstantMessage(Guid ObjectId, Guid OwnerId, string ObjectName, bool GroupOwned, string Location, bool FromSystem, MessageBody Text) : TransportEvent;
