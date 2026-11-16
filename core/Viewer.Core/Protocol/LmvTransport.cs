// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Diagnostics;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using LibreMetaverse.Interfaces;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Session;
using Viewer.Core.World;
using N = System.Numerics;

namespace Viewer.Core.Protocol;

/// <summary>The one way to get a LibreMetaverse transport from outside Viewer.Core.Protocol: no LM type in the signature.</summary>
public static class ProtocolAdapters
{
    /// <summary>A new transport owning its own GridClient, for exactly one session.</summary>
    /// LibreMetaverse's own logging is routed into the redacted log first, before any GridClient exists.
    /// <param name="trust">The certificates the person chose to trust; every HTTPS connection of the transport consults it. Null: none.</param>
    public static ISessionTransport CreateLibreMetaverse(OutboundPolicy outbound, CoreLog log, CertificateTrust? trust = null, string? characterFolder = null)
    {
        LmvLogRoute.Install(log);
        var t = new LmvTransport(outbound, log, trust);
        t.UseCharacterFolder(characterFolder);
        return t;
    }

    /// <summary>Routes LibreMetaverse's process-global logger into <paramref name="log"/> (redacted); also for tools
    /// that use LibreMetaverse before creating a transport.</summary>
    public static void RouteLibraryLogging(CoreLog log) => LmvLogRoute.Install(log);
}

/// <summary>
/// The LibreMetaverse protocol adapter (ADR-005, ADR-007): owns one <see cref="GridClient"/> and translates its events
/// into plain <see cref="TransportEvent"/>s. The only code that touches LibreMetaverse lives in this namespace
/// (ArchitectureTests enforces it). Extracted from the web gateway's AgentSession (web-viewer bb91ba5).
/// Per instance, never static: the simulator map, the handlers, the client.
/// </summary>
internal sealed partial class LmvTransport : Viewer.Core.Inventory.IInventoryRequester, ISessionTransport, IMapBlockRequester, IAgentControlTransport, IAgentAnimationTransport, ICommandTransport, IAgentNameRequester, IOfflineMessageRequester, IGroupRequester, IMuteListRequester, Viewer.Core.Assets.IAnimationTransport, Viewer.Core.Assets.ISettingsTransport, Viewer.Core.Assets.ISoundTransport, Viewer.Core.Assets.ISoundProbe, Viewer.Core.Assets.IMeshTransport, Viewer.Core.Assets.ITextureTransport, Viewer.Core.Assets.ITextureRangeTransport,
    Viewer.Core.Assets.ITextureCapabilityProbe, Viewer.Core.Assets.IMaterialTransport, IMapRequester, IMapTileTransport
    , IInboundGate, Viewer.Core.Building.ITextureUploadService, Viewer.Core.Building.IModelUploadService
{
    private readonly GridClient _client = new();

    /// <summary>Mesh asset bytes through this session's own GridClient (the grid's mesh capability).</summary>
    public Task<Viewer.Core.Assets.AssetFetchResult> FetchMeshAsync(Guid id, CancellationToken ct) => LmvMesh.FetchAsync(_client, id, ct);

    /// <summary>A PBR material asset through the region's asset capability (the bytes as served).</summary>
    public Task<Viewer.Core.Assets.AssetFetchResult> FetchMaterialAsync(Guid id, CancellationToken ct) => LmvMaterials.FetchAsync(_client, id, ct);
    public Task<Viewer.Core.Assets.AssetFetchResult> FetchAnimationAsync(Guid id, CancellationToken ct) => LmvMaterials.FetchAnimationAsync(_client, id, ct);
    public Task<Viewer.Core.Assets.AssetFetchResult> FetchSettingsAsync(Guid id, CancellationToken ct) => LmvMaterials.FetchSettingsAsync(_client, id, ct);

    /// <summary>A sound asset (the interface sounds set to the grid's) through the region's asset capability.</summary>
    public Task<Viewer.Core.Assets.AssetFetchResult> FetchSoundAsync(Guid id, CancellationToken ct) => LmvMaterials.FetchSoundAsync(_client, id, ct);

    /// <summary>What the region's asset capability answers for a sound, for checks.</summary>
    public Task<Viewer.Core.Assets.SoundProbe> ProbeSoundAsync(Guid id, CancellationToken ct) => LmvMaterials.ProbeSoundAsync(_client, id, ct);

    /// <summary>
    /// Texture bytes (J2K, not decoded), the whole file. A plain bounded GET (no Range) on the
    /// region's texture capability through this session's texture client (<see cref="TextureHttp"/>): a 404 is NotFound at
    /// once, anything else that is not the file is Failed, and past the deadline it is Failed (the texture keeps its
    /// placeholder). LibreMetaverse's RequestImageAsync (which turns a 404 into a UDP request that may never end) is
    /// used only when the region has no texture capability, and then bounded by the same deadline.
    /// </summary>
    public async Task<Viewer.Core.Assets.AssetFetchResult> FetchTextureAsync(Guid id, CancellationToken ct)
    {
        CancellationToken life;
        lock (_lock)
        {
            if (_disposed) return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Cancelled);
            life = _textureFetches.Token;
        }
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, life);
        _textureHttp.RequestDeadline = TextureDeadline;
        if (TextureAddress(id) is { } address)
            return await _textureHttp.WholeAsync(address, Viewer.Core.Imaging.J2kLimits.Default.MaxBytes, both.Token, () => Timeline?.BeginRequest(id, "whole"), ClassOf(id)).ConfigureAwait(false);
        var timing = Timeline?.BeginRequest(id, "whole-library");
        long read = 0;
        try
        {
            var r = await TextureHttp.WithDeadline(async token =>
            {
                var t = await _client.Assets.RequestImageAsync(new UUID(id), ImageType.Normal, token).ConfigureAwait(false);
                return t?.AssetData is { Length: > 0 } b ? Viewer.Core.Assets.AssetFetchResult.Ok(b) : Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.NotFound);
            }, TextureDeadline, both.Token).ConfigureAwait(false);
            read = r.Bytes.Length;
            return r;
        }
        finally { timing?.Done(read); }
    }

    /// <summary>Every texture request is timed into this when it is set (measurement only).</summary>
    public Viewer.Core.Assets.TextureTimeline? Timeline { get; set; }

    /// <summary>Each texture's fetch class (a face's own texture, a PBR base colour or a detail map), the order its requests
    /// wait in; null: all base colour.</summary>
    public TextureFetchClasses? FetchClasses { get; set; }

    /// <summary>The texture client's request slots and their waits.</summary>
    public FetchQueue TextureQueue => _textureHttp.Queue;

    private TextureFetchClass ClassOf(Guid id) => FetchClasses?.Of(id) ?? TextureFetchClass.BaseColour;

    /// <summary>Tests only: the texture capability to use instead of the region's.</summary>
    internal Func<Uri?>? TextureCapabilityOverride { get; set; }

    /// <summary>Tests only: every texture request's deadline (<see cref="TextureHttp.Deadline"/> otherwise).</summary>
    internal TimeSpan TextureDeadline { get; set; } = TextureHttp.Deadline;

    /// <summary>The region's texture capability (ViewerAsset, else GetTexture), or null.</summary>
    private Uri? TextureCapability()
    {
        if (TextureCapabilityOverride is { } o) return o();
        try { return _client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset") ?? _client.Network.CurrentSim?.Caps?.CapabilityURI("GetTexture"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static Uri TextureUri(Uri cap, Guid id) => new(cap.ToString().TrimEnd('/') + "/?texture_id=" + id.ToString("D"));

    /// <summary>Which avatar and slot each server bake this session has seen belongs to (filled from every AvatarAppearance).</summary>
    private readonly BakeRoutes _bakeRoutes = new();

    /// <summary>Tests only: the appearance-service URL to use instead of the login reply's.</summary>
    internal Func<string?>? AppearanceServiceOverride { get; set; }

    /// <summary>Tests only: whether the region bakes avatars, instead of the current region's RegionProtocols bit 0.</summary>
    internal Func<bool>? RegionBakesOverride { get; set; }

    /// <summary>Tests only: records an appearance's bakes as an AvatarAppearance from the region does.</summary>
    internal void NoteAppearance(AvatarAppearanceData appearance) => _bakeRoutes.Note(appearance);

    /// <summary>
    /// Where a texture is fetched from: a server bake of any avatar from the login reply's appearance service, at
    /// texture/avatar/slot/bake (see <see cref="BakeRoutes"/>), when the login reply named one and the region bakes avatars;
    /// anything else by id from the region's texture capability, as before; null with neither.
    /// </summary>
    private Uri? TextureAddress(Guid id) => BakeAddress(id) ?? (TextureCapability() is { } cap ? TextureUri(cap, id) : null);

    private Uri? BakeAddress(Guid id)
    {
        try
        {
            var service = AppearanceServiceOverride is { } s ? s() : _client.Network.AgentAppearanceServiceURL;
            if (string.IsNullOrEmpty(service)) return null;
            bool regionBakes = RegionBakesOverride is { } b ? b() : _client.Network.CurrentSim?.Protocols.HasFlag(RegionProtocols.AgentAppearanceService) == true;
            return _bakeRoutes.UriFor(id, service, regionBakes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>
    /// The same textures straight from the region's texture capability with a plain bounded client
    /// of our own (the outbound policy's handler, NOT LibreMetaverse's caps client or its rate limiter), at a fixed
    /// concurrency (at most the official viewer's 8). Read-only GETs; cancelled with the session like every texture request.
    /// A measurement of the texture capability only: every id, a server bake too, is asked by id here.
    /// </summary>
    public async Task<Viewer.Core.Assets.TextureProbeResult?> ProbeTexturesAsync(IReadOnlyList<Guid> ids, int concurrency, long? to, CancellationToken ct)
    {
        CancellationToken life;
        lock (_lock)
        {
            if (_disposed) return null;
            life = _textureFetches.Token;
        }
        if (TextureCapability() is not { } cap) return null;
        using var client = OutboundHttp.CreateClient(_outbound, _log, TimeSpan.FromSeconds(60), refusals: _certificates);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, life);
        return await Viewer.Core.Assets.TextureProbe.RunAsync(client, id => TextureUri(cap, id), ids, concurrency, to, both.Token).ConfigureAwait(false);
    }
    /// <summary>
    /// Part of a texture, by a range GET on the region's texture capability (ViewerAsset, else
    /// GetTexture: OpenSim serves both from the same handler, <c>?texture_id=</c>), bounded and checked by <see cref="RangeHttp"/>.
    /// Through this session's texture client (<see cref="TextureHttp"/>: the outbound policy's handler,
    /// at most 8 in flight, a deadline), not LibreMetaverse's caps client, whose rate limiter queued every texture request. Cancelled with the session:
    /// logout, disconnect, a region change and disposal cancel every range request in flight. No capability: a failed
    /// partial fetch (the caller falls back to <see cref="FetchTextureAsync"/>).
    /// </summary>
    public async Task<RangeAnswer> GetTextureRangeAsync(Guid id, long from, long? to, int capBytes, CancellationToken ct)
    {
        CancellationToken life;
        lock (_lock)
        {
            if (_disposed) return RangeAnswer.Fail(null, "transport disposed");
            life = _textureFetches.Token;
        }
        var address = TextureAddress(id);
        if (address is null) return RangeAnswer.Fail(null, "no texture capability");
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, life);
        _textureHttp.RequestDeadline = TextureDeadline;
        return await _textureHttp.RangeAsync(address, from, to, capBytes, both.Token, () => Timeline?.BeginRequest(id, "range"), ClassOf(id)).ConfigureAwait(false);
    }

    /// <summary>Cancels every texture range request in flight (logout, disconnect, region change, dispose).</summary>
    private void CancelTextureFetches()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old = _textureFetches;
            _textureFetches = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }

    private CancellationTokenSource _textureFetches = new();
    /// <summary>This session's texture HTTP, on a client of its own.</summary>
    private readonly TextureHttp _textureHttp;
    private readonly CertificateRefusals _certificates = new();
    private readonly LmvLoginTap _loginTap;
    private readonly OutboundPolicy _outbound;
    private readonly CoreLog _log;
    private readonly object _lock = new();

    private readonly SimArrivals<Simulator> _arrivals = new();
    private bool _disposed;

    /// <summary>The region environment fetch, bounded and tied to this session and its region.</summary>
    private readonly EnvironmentFetcher<Simulator> _environment;
    // a parcel's own environment, asked when the agent's parcel changes; the newest ask replaces the one before
    private sealed record ParcelKey(Simulator Sim, int Id);
    private readonly EnvironmentFetcher<ParcelKey> _parcelEnvironment;
    private readonly LegacyMaterialFetcher<Simulator> _materials;

    /// <summary>The last render material entries raised per object, per simulator (raised again only when they change).</summary>
    private readonly Dictionary<Simulator, Dictionary<uint, IReadOnlyList<Viewer.Core.World.RenderMaterialEntry>>> _pbr = new();

    /// <summary>
    /// The visual-parameter wire order of the pinned LibreMetaverse (3.1.6): the parameter id at each position of an
    /// AvatarAppearance blob, ascending (the order its own AvatarAppearanceEventArgs.DecodeVisualParams reads). Copied per
    /// transport from the library's table, never re-derived from a LAD file or copied as indices.
    /// </summary>
    private readonly ImmutableArray<int> _wireOrder = VisualParams.Group0ParamIds.ToImmutableArray();

    public event Action<TransportEvent>? Event;

    internal GridClient Client => _client;

    /// <summary>How many simulators this transport has mapped (for the disposal test: zero afterwards).</summary>
    internal int MappedSimulators => _simRefs.Count;

    public LmvTransport(OutboundPolicy outbound, CoreLog log, CertificateTrust? trust = null)
    {
        _outbound = outbound;
        _log = log.For("protocol.lmv");
        _certificates.Trust = trust;
        _environment = new EnvironmentFetcher<Simulator>((sim, llsd, why) => Raise(new EnvironmentReceived(Map(sim), llsd, why)));
        _parcelEnvironment = new EnvironmentFetcher<ParcelKey>((k, llsd, why) => Raise(new ParcelEnvironmentReceived(Map(k.Sim), k.Id, llsd, why)));
        _materials = new LegacyMaterialFetcher<Simulator>(FetchLegacyMaterialsAsync, (sim, m) => Raise(new LegacyMaterialsReceived(Map(sim), m)));
        _simRefs.OnReplaced = OnSimRefReplaced;
        _textureHttp = new TextureHttp(outbound, _log, _certificates);
        ApplySessionSettings(_client);
        ApplyCertificateRecording(_client, _certificates, _log);
        InitClientBake();

        // The library's caps, event queue and login HTTP all go through HttpCapsClient, so its handler is ours in every mode: it checks
        // server certificates (and records what it refused, and honours the certificates the person chose to trust), and in strict mode
        // it also refuses internal addresses. The login tap in front of it adds the extended-errors field to the login request and keeps
        // the reply's fields. Simulator circuits are governed through the cancellable SimConnecting in strict mode.
        _loginTap = new LmvLoginTap(OutboundHttp.CreateHandler(_outbound, _log, _certificates), _log);
        _client.HttpCapsClient = new HttpCapsClient(_loginTap);
        // strict mode refuses internal addresses here; in every mode a second circuit to an address that already has one is refused
        _client.Network.SimConnecting += OnSimConnecting;

        _client.Network.LoginProgress += OnLoginProgress;
        _client.Network.SimConnected += OnSimConnected;
        _client.Network.SimChanged += OnSimChanged;
        _client.Network.SimDisconnected += OnSimDisconnected;
        _client.Network.RegisterCallback(PacketType.TeleportFailed, OnTeleportFailedPacket);
        _client.Network.RegisterEventCallback("TeleportFailed", OnTeleportFailedEvent);
        _client.Network.EventQueueRunning += OnEventQueueRunning;
        _client.Network.Disconnected += OnDisconnected;
        _client.Network.RegisterCallback(PacketType.AgentMovementComplete, OnAgentMovementComplete);
        _client.Network.RegisterCallback(PacketType.MapBlockReply, OnMapBlockReply);
        _client.Objects.ObjectUpdate += OnObjectUpdate;
        _client.Objects.AvatarUpdate += OnAvatarUpdate;
        _client.Objects.TerseObjectUpdate += OnTerseObjectUpdate;
        _client.Avatars.AvatarAppearance += OnAvatarAppearance;
        _client.Objects.KillObject += OnKillObject;
        _client.Objects.KillObjects += OnKillObjects;
        _client.Terrain.LandPatchReceived += OnLandPatchReceived;
        _client.Network.GenericStreamingMessage += OnGenericStreamingMessage;
        _client.Network.RegisterCallback(PacketType.ObjectUpdate, OnObjectUpdatePacket);
        _client.Network.RegisterCallback(PacketType.AvatarAnimation, OnAvatarAnimationPacket);   // the library's own event drops the simulator
        _client.Network.RegisterCallback(PacketType.CameraConstraint, OnCameraConstraintPacket);
        _client.Network.RegisterCallback(PacketType.LayerData, OnLayerDataPacket);   // the wind layer (the library keeps it only with its own land store on)
        _client.Network.RegisterCallback(PacketType.GrantGodlikePowers, OnGrantGodlikePowersPacket);
        _client.Network.RegisterCallback(PacketType.ScriptControlChange, OnScriptControlChangePacket);
        _client.Network.RegisterCallback(PacketType.AvatarAppearance, OnAvatarAppearancePacket);   // the AppearanceHover block, which the library event drops
        _client.Network.RegisterCallback(PacketType.ObjectUpdateCompressed, OnObjectUpdateCompressedPacket);
        _client.Network.RegisterCallback(PacketType.SoundTrigger, OnSoundPacket);
        _client.Network.RegisterCallback(PacketType.AttachedSound, OnSoundPacket);
        _client.Network.RegisterCallback(PacketType.AttachedSoundGainChange, OnSoundPacket);
        _client.Network.RegisterCallback(PacketType.PreloadSound, OnSoundPacket);
        _client.Network.RegisterCallback(PacketType.ParcelOverlay, OnSoundPacket);
        _client.Self.AvatarSitResponse += OnAvatarSitResponse;
        _client.Objects.ObjectProperties += OnObjectProperties;
        _client.Objects.PhysicsProperties += OnPhysicsProperties;
        _client.Objects.PayPriceReply += OnPayPriceReply;
        _client.Self.ChatFromSimulator += OnChat;
        _client.Self.IM += OnInstantMessage;
        _client.Self.AlertMessage += OnAlertMessage;
        _client.Self.MoneyBalance += OnMoneyBalance;
        _client.Self.ScriptDialog += OnScriptDialog;
        _client.Self.ScriptQuestion += OnScriptQuestion;
        _client.Self.LoadURL += OnLoadUrl;
        _client.Network.RegisterCallback(PacketType.ScriptTeleportRequest, OnScriptTeleportRequest);
        _client.Network.RegisterCallback(PacketType.GenericMessage, OnGenericMessage);
        _client.Parcels.ParcelProperties += OnParcelProperties;
        _client.Avatars.UUIDNameReply += OnNames;
        _client.Friends.friendsListReady += OnFriendsReady;
        _client.Friends.FriendOnline += OnFriendOnline;
        _client.Friends.FriendOffline += OnFriendOffline;
        _client.Friends.FriendRightsUpdate += OnFriendRights;
        _client.Friends.FriendshipTerminated += OnFriendshipTerminated;
        _client.Groups.CurrentGroups += OnCurrentGroups;
        _client.Self.TeleportProgress += OnTeleportProgress;
        _client.Grid.CoarseLocationUpdate += OnCoarseLocations;
        _client.Friends.FriendFoundReply += OnFriendFound;
        _client.Groups.GroupTitlesReply += OnGroupTitles;
        _client.Self.AgentDataReply += OnAgentData;
        _client.Self.GroupChatJoined += OnGroupChatJoined;
        _client.Self.MuteListUpdated += OnMuteListUpdated;
        _client.Self.Movement.Camera.Far = Math.Max(Interest.DefaultDrawDistance / 2f, 50f);   // arriving: half the draw distance (FarRamp)
        // the controls set by SendAgentUpdate stay set until the next one: the library's own periodic updates repeat them
        _client.Self.Movement.AutoResetControls = false;
        // The library keeps the control bits as one shared value that its property setters change a bit at a time, with
        // no lock, and sends it from its own timer thread: an update sent part-way through our seventeen writes could
        // carry a mix of the old and new bits. So its timer is turned off (it still makes one callback at login, which
        // sends nothing before the movement handshake). Nothing else resends the update: the viewer's send rule decides every
        // send, including the one-second keep-alive (llviewermessage.cpp send_agent_update has no timer either).
        _client.Self.Movement.UpdateInterval = 0;
    }

    private readonly object _movementLock = new();
    private byte _lastState, _lastFlags;   // the render state and flag bytes of the last update, for a send that only changes the far distance (under _movementLock)

    /// <summary>
    /// The settings every session's client gets (map section 9, item 1):
    /// APPEARANCE-PASSIVE (hazard 3): SendAppearance off, so LibreMetaverse never fetches
    /// wearables at login and never sends AgentSetAppearance (the gateway saw it persist a partial look once).
    /// No library asset cache (the content store is ours). StoreLandPatches off: LibreMetaverse's fixed 16x16
    /// patch array overflows on var regions (the var-region workaround). One simulator. Objects decoded and requested.
    /// </summary>
    internal static void ApplySessionSettings(GridClient client)
    {
        var s = client.Settings;
        s.Agent.SendAppearance = false;
        s.AssetCache.Enabled = false;
        s.World.StoreLandPatches = false;
        s.Agent.MultipleSims = false;
        s.World.AlwaysDecodeObjects = true;
        s.World.AlwaysRequestObjects = true;
        s.Logging.LogNames = false;
        // either of these puts every Simulator in the process into LibreMetaverse's process-global
        // SimulatorDataPool. In 3.1.6 the pool is looked up before Simulator.Handle is assigned, so ALL simulators of ALL
        // clients share one entry (key 0): one session's prim cache and parcel data would be every session's (ADR-006).
        // Both default to false; they are pinned here so no settings change can turn the shared pool on.
        s.Parcel.PoolParcelData = false;
        s.World.CachePrimitives = false;
        // The library's automatic throttle sends AgentThrottle to each region it connects to and then rebuilds its outgoing send buckets,
        // disposing the old ones; a packet already waiting on an old bucket throws inside the library's one send loop, which ends that loop
        // for the rest of the session (nothing more reaches any region). Off here; the transport sends the message itself (SendThrottle).
        s.Agent.SendThrottle = false;
    }

    /// <summary>Child circuits to the neighbouring regions the grid announces (the library's MultipleSims), on or off for this client.
    /// Off at construction: one region; the login request turns it on.</summary>
    internal static void ApplyNeighbours(GridClient client, bool neighbours) => client.Settings.Agent.MultipleSims = neighbours;

    /// <summary>The AgentThrottle message for a region, with the client's rates (what the library's AgentThrottle.Set sends, without its
    /// rebuilding of the send buckets).</summary>
    internal static AgentThrottlePacket? ThrottlePacket(GridClient client) => new()
    {
        AgentData = { AgentID = client.Self.AgentID, SessionID = client.Self.SessionID, CircuitCode = client.Network.CircuitCode },
        Throttle = { GenCounter = 0, Throttles = client.Throttle.ToBytes() },
    };

    /// <summary>Tells a region the bandwidth this viewer takes from it (llviewerthrottle.cpp, LLViewerThrottle::sendToSim, sent to each region
    /// connected). The library's own automatic throttle is off (see <see cref="ApplySessionSettings"/>).</summary>
    private void SendThrottle(Simulator sim)
    {
        try
        {
            if (ThrottlePacket(_client) is { } packet)
                _client.Network.SendPacket(packet, sim);
        }
        catch (Exception ex)
        {
            _log.Warn("throttle.send-failed", CoreLog.F("error", ex.GetType().Name));
        }
    }
    /// <summary>
    /// The library checks server certificates by default (chain, name, expiry) and rejects any it cannot trust; nothing here
    /// widens that. Its decision hook is used only to record what was refused (the host and the certificate's SHA-256
    /// fingerprint), then answers no, so a refusal can be shown with what a per-grid trusted-certificate choice would pin.
    /// Fingerprints the library was told to trust, and its CA bundle, are consulted before this hook.
    /// </summary>
    internal static void ApplyCertificateRecording(GridClient client, CertificateRefusals refusals, CoreLog log)
    {
        client.Settings.Security.VerifyServerCertificates = true;
        client.Settings.Security.CertificateValidationCallback = (host, certificate, errors) =>
        {
            var refusal = CertificateRefusal.From(host, certificate, errors);
            if (refusals.Accepts(refusal))
            {
                log.Info("tls.certificate-trusted", CoreLog.F("host", host), CoreLog.F("sha256", refusal.Sha256));
                return true;
            }
            log.Warn("tls.certificate-refused", CoreLog.F("host", host), CoreLog.F("sha256", refusal.Sha256), CoreLog.F("errors", refusal.Errors));
            refusals.Record(refusal);
            return false;
        };
    }

    public SimRef? CurrentSim
    {
        get
        {
            var sim = _client.Network.CurrentSim;
            return sim is null ? null : Map(sim);
        }
    }

    public int ConnectedSimulatorCount
    {
        get
        {
            var sims = _client.Network.Simulators;
            lock (sims) return sims.Count;
        }
    }

    public void SetDrawDistance(float metres)
    {
        if (_disposed)
            return;
        var mv = _client.Self.Movement;
        AgentUpdatePacket packet;
        lock (_movementLock)
        {
            mv.Camera.Far = metres;
            packet = BuildUpdate(mv, true, _lastState, _lastFlags);
        }
        Send(packet);
    }

    /// <summary>Tests: called with the control bits in force at each send.</summary>
    internal Action<uint>? SendProbe;

    /// <summary>Tests: called with each agent update message as it is built, before it is sent.</summary>
    internal Action<LibreMetaverse.Packets.AgentUpdatePacket>? PacketProbe;

    /// <summary>Tests: the lock that every change of the movement state takes.</summary>
    internal object MovementLockForTests => _movementLock;

    public void SendAgentUpdate(Viewer.Motion.AgentUpdate update)
    {
        if (_disposed)
            return;
        var mv = _client.Self.Movement;
        AgentUpdatePacket packet;
        lock (_movementLock)
        {
            ApplyControls(mv, update.Controls);
            mv.BodyRotation = L(update.BodyRotation);
            mv.HeadRotation = L(update.HeadRotation ?? update.BodyRotation);   // the head follows the body unless the caller gives its own (mouse-look)
            if (update.CameraEye != update.CameraTarget)
                mv.Camera.LookAt(L(update.CameraEye), L(update.CameraTarget));
            _lastState = update.State;
            _lastFlags = update.Flags;
            packet = BuildUpdate(mv, false, update.State, update.Flags);
        }
        Send(packet);   // the socket is written outside the lock: a slow send never holds up a change of the state
    }

    /// <summary>The agent update message for the movement state as it is, copied field by field; the caller holds <see cref="_movementLock"/>.</summary>
    private AgentUpdatePacket BuildUpdate(AgentManager.AgentMovement mv, bool reliable, byte state, byte flags)
    {
        if (_client.Network.Connected)
            TraceUpdate(mv);
        var self = _client.Self;
        var packet = new AgentUpdatePacket { Header = { Reliable = reliable } };
        var d = packet.AgentData;
        d.AgentID = self.AgentID;
        d.SessionID = self.SessionID;
        d.State = state;
        d.BodyRotation = mv.BodyRotation;
        d.HeadRotation = mv.HeadRotation;
        d.CameraCenter = mv.Camera.Position;
        d.CameraAtAxis = mv.Camera.AtAxis;
        d.CameraLeftAxis = mv.Camera.LeftAxis;
        d.CameraUpAxis = mv.Camera.UpAxis;
        d.Far = mv.Camera.Far;
        d.ControlFlags = mv.AgentControls;
        d.Flags = flags;
        return packet;
    }

    private void Send(AgentUpdatePacket packet)
    {
        SendProbe?.Invoke(packet.AgentData.ControlFlags);
        PacketProbe?.Invoke(packet);
        TraceAgentUpdate(packet, _client.Network.Connected);
        if (_client.Network.Connected)
            _client.Network.SendPacket(packet);
    }

    internal static LibreMetaverse.Packets.SetAlwaysRunPacket BuildSetAlwaysRun(UUID agent, UUID session, bool run) =>
        new() { Header = { Reliable = true }, AgentData = { AgentID = agent, SessionID = session, AlwaysRun = run } };

    /// <summary>The AgentAnimation message that asks the region to start or stop one animation on the agent's avatar.</summary>
    internal static LibreMetaverse.Packets.AgentAnimationPacket BuildAgentAnimation(UUID agent, UUID session, UUID animation, bool start) =>
        BuildAgentAnimations(agent, session, [(animation.Guid, start)]);

    /// <summary>One AgentAnimation message for several requests, in the order given.</summary>
    internal static LibreMetaverse.Packets.AgentAnimationPacket BuildAgentAnimations(UUID agent, UUID session, IReadOnlyList<(Guid Animation, bool Start)> requests) => new()
    {
        Header = { Reliable = true },
        AgentData = { AgentID = agent, SessionID = session },
        AnimationList = requests.Where(r => r.Animation != Guid.Empty)
            .Select(r => new LibreMetaverse.Packets.AgentAnimationPacket.AnimationListBlock { AnimID = new UUID(r.Animation), StartAnim = r.Start }).ToArray(),
        PhysicalAvatarEventList = [],
    };

    public void SendAnimations(IReadOnlyList<(Guid Animation, bool Start)> requests)
    {
        if (_disposed || !_client.Network.Connected)
            return;
        var packet = BuildAgentAnimations(_client.Self.AgentID, _client.Self.SessionID, requests);
        if (packet.AnimationList.Length > 0)
            _client.Network.SendPacket(packet);
    }

    public void SendAnimation(Guid animation, bool start)
    {
        SendAnimations([(animation, start)]);
    }

    private volatile bool _walkRun;

    /// <summary>The run state the transport was last given, told to a region again when the avatar arrives in it.</summary>
    internal bool WalkRun => _walkRun;

    public void SendAlwaysRun(bool run)
    {
        if (_disposed)
            return;
        _walkRun = run;
        if (_client.Network.Connected)
            _client.Network.SendPacket(BuildSetAlwaysRun(_client.Self.AgentID, _client.Self.SessionID, run));
    }

    internal int LibraryUpdateInterval => _client.Self.Movement.UpdateInterval;

    /// <summary>The object a sit request asked for and the region has not yet answered (zero: none).</summary>
    private Guid _pendingSit;

    /// <summary>
    /// The command side (ADR-015): one user command to the library, already checked by the action pipeline. Returns at once;
    /// a command with a long answer (teleport, inventory) runs on, and its failure is logged by type, never by content.
    /// Nothing is sent without a circuit.
    /// </summary>
    public CommandDispatch Execute(TransportCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_disposed)
            return CommandDispatch.NotSent("disposed");
        if (!IsKnownCommand(command))
            return CommandDispatch.NotSent("unknown_command");
        if (!_client.Network.Connected)
            return CommandDispatch.NotSent("not_connected");
        var self = _client.Self;
        switch (command)
        {
            case SayCommand s:
                self.Chat(s.Text, s.Channel, ChatTypeOf(s.Kind), false);
                break;
            case InstantMessageCommand im:
                self.InstantMessage(new UUID(im.Target), im.Text);
                break;
            case ChatTypingCommand typing:
                SendChatTyping(typing.Started);
                break;
            case ImTypingCommand imTyping:
                // an instant message of the typing kind, online, with the conversation's id and the protocol's fixed text
                self.InstantMessage(self.Name, new UUID(imTyping.Target), "typing",
                    new UUID(Viewer.Core.Social.ImSessionId.For(self.AgentID.Guid, imTyping.Target)),
                    imTyping.Started ? InstantMessageDialog.StartTyping : InstantMessageDialog.StopTyping,
                    InstantMessageOnline.Online, self.SimPosition, UUID.Zero, Utils.EmptyBytes);
                break;
            case TouchCommand t:
                self.Touch(t.LocalId);
                break;
            case SitCommand sit:
                lock (_lock) _pendingSit = sit.ObjectId;
                self.RequestSit(new UUID(sit.ObjectId), L(sit.Offset));
                break;
            case StandCommand:
                self.Stand();
                break;
            case GrabCommand grab:
                self.Grab(grab.LocalId, L(grab.GrabOffset), Surface3(grab.Surface.Uv), Surface3(grab.Surface.St), grab.Surface.Face,
                    L(grab.Surface.Position), L(grab.Surface.Normal), L(grab.Surface.Binormal));
                break;
            case ReleaseCommand release:
                self.DeGrab(release.LocalId, Surface3(release.Surface.Uv), Surface3(release.Surface.St), release.Surface.Face,
                    L(release.Surface.Position), L(release.Surface.Normal), L(release.Surface.Binormal));
                break;
            case SitGroundCommand:
                self.SitOnGround();
                break;
            case RequestObjectPropertiesCommand props:
                // the selection is what makes the region send the properties; a held one stays until an ObjectDeselect (the edit tools and the
                // right-click selection, as the official viewer's), any other is let go of again by the library once they arrive
                if (_client.Network.CurrentSim is { } propSim) _client.Objects.SelectObject(propSim, props.LocalId, !props.Hold);
                break;
            case TakeObjectCommand take:
                SendDeRez(take.LocalId, take.Copy);
                break;
            case RequestParcelCommand parcel:
                if (_client.Network.CurrentSim is { } parcelSim) _client.Parcels.RequestParcelProperties(parcelSim, parcel.Y + 1f, parcel.X + 1f, parcel.Y, parcel.X, ParcelInfoSequence, false);
                break;
            case TeleportRegionCommand tp:
                Run("teleport", () => TeleportToNamedRegionAsync(tp.RegionName, L(tp.Position)));
                break;
            case TeleportLandmarkCommand lm:
                self.RequestTeleport(new UUID(lm.Landmark));
                break;
            case TeleportHomeCommand:
                Run("teleport-home", () => self.GoHomeAsync(CancellationToken.None));
                break;
            case TeleportHandleCommand handle:
                // the library sends this one only once the region's event queue runs; say so rather than let it drop the request
                if (_client.Network.CurrentSim?.Caps?.IsEventQueueRunning != true)
                    return CommandDispatch.NotSent("no_event_queue");
                self.RequestTeleport(handle.RegionHandle, L(handle.Position));
                break;
            case TeleportCancelCommand:
                _namedTeleport.Cancel();
                _client.Network.SendPacket(new TeleportCancelPacket { Info = { AgentID = self.AgentID, SessionID = self.SessionID } });
                break;
            case TeleportLureAnswerCommand lure:
                self.TeleportLureRespond(new UUID(lure.From), new UUID(lure.LureId), lure.Accept);
                break;
            case TeleportOfferCommand offer:
                if (offer.Message.Length == 0)
                    self.SendTeleportLure(new UUID(offer.Target));
                else
                    self.SendTeleportLure(new UUID(offer.Target), offer.Message);
                break;
            case TeleportRequestCommand request:
            {
                var m = ToTeleportRequestMessage(request.Message);
                self.InstantMessage(self.Name, new UUID(request.Target), m.Text, m.Session, m.Dialog, m.Offline, self.SimPosition, UUID.Zero, Utils.EmptyBytes);
                break;
            }
            case SetHomeCommand:
            {
                // posted to the region's HomeLocation capability when it has one, as the official viewer does; the older
                // start-location message otherwise. Either way the region answers with an alert, which is where the words come from.
                var sim = _client.Network.CurrentSim;
                if (sim is null)
                    return CommandDispatch.NotSent("not_connected");
                var here = self.SimPosition;
                // the ground comes from the land patches this viewer kept: the library's own terrain array is switched off and answers nothing
                var at = HomePosition.For(V(here), sim.SizeX, (x, y) => _ground.HeightAt(sim, x, y) ?? 0f, 4096f);
                var look = _client.Self.Movement.Camera.AtAxis;
                var capability = sim.Caps?.CapabilityURI(HomeLocationCapability.Name);
                if (HomeLocationCapability.Choose(capability) == HomeRoute.Capability)
                {
                    string body = HomeLocationCapability.Body(at, V(look));
                    Run("set-home", async () =>
                    {
                        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/llsd+xml");
                        using var response = await _client.HttpCapsClient.PostAsync(capability!, content).ConfigureAwait(false);
                        var reply = HomeLocationCapability.ReadReply(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                        _log.Info("home.capability-answered", CoreLog.F("success", reply.Success));
                    });
                    break;
                }
                _client.Network.SendPacket(new SetStartLocationRequestPacket
                {
                    AgentData = { AgentID = self.AgentID, SessionID = self.SessionID },
                    StartLocationData = { LocationPos = L(at), LocationID = 1, SimName = Utils.StringToBytes(string.Empty), LocationLookAt = look },
                });
                break;
            }
            case CreateInventoryFolderCommand or RenameInventoryFolderCommand or MoveInventoryFolderCommand:
                ExecuteFolderCommand(command);
                break;
            case ChangeOutfitCommand or SaveOutfitCommand or RebuildLookCommand:
                ExecuteOutfitCommand(command);
                break;
            case SaveWearableCommand or SaveWearableAsCommand:
                ExecuteWearableCommand(command);
                break;
            case ScriptDialogReplyCommand reply:
                _client.Network.SendPacket(new ScriptDialogReplyPacket
                {
                    AgentData = { AgentID = self.AgentID, SessionID = self.SessionID },
                    Data = { ObjectID = new UUID(reply.ObjectId), ChatChannel = reply.Channel, ButtonIndex = reply.ButtonIndex, ButtonLabel = Utils.StringToBytes(reply.Label) },
                }, SimulatorOf(reply.Sim) ?? _client.Network.CurrentSim!);
                break;
            case ScriptPermissionAnswerCommand answer:
                self.ScriptQuestionReply(SimulatorOf(answer.Sim) ?? _client.Network.CurrentSim!, new UUID(answer.ItemId), new UUID(answer.TaskId), (ScriptPermission)answer.Granted);
                break;
            case MoveInventoryItemCommand mv:
                // the official viewer's message, sent at once (a single packet); a failure to send reaches the pipeline as a throw
                _client.Network.SendPacket(InventoryMovePackets.Item(self.AgentID, self.SessionID, mv.ItemId, mv.FolderId, mv.NewName));
                KeepLibraryStoreParent(mv.ItemId, mv.FolderId, mv.NewName);
                break;
            case RequestTaskInventoryCommand or CopyTaskContentsCommand or BuyObjectCommand or RequestPayPriceCommand or PayObjectCommand:
                return ExecuteTrade(command);
            case InventoryOfferAnswerCommand or AttachWorldObjectCommand or DropAttachmentCommand:
                return ExecuteWire(command);
            case CreateObjectCommand or RezItemCommand or DeleteObjectCommand or EditObjectCommand:
                return ExecuteBuild(command);
            case CopyFromNotecardCommand copy:
                return ExecuteNotecard(copy);
            default:
                return ExecuteSocial(command);
        }
        return CommandDispatch.Handed;
    }

    /// <summary>Friends and groups. Each case says when the library would silently send nothing, so the pipeline's answer is true.</summary>
    private CommandDispatch ExecuteSocial(TransportCommand command)
    {
        var self = _client.Self;
        var friends = _client.Friends;
        var groups = _client.Groups;
        switch (command)
        {
            case FriendshipOfferCommand offer:
                friends.OfferFriendship(new UUID(offer.Target), offer.Message);
                break;
            case FriendshipAnswerCommand answer:
            {
                // an offer stored while this agent was away is answered through the grid's capability when it has one; any other
                // offer (or a grid without the capability) is answered by the ordinary message that carries the offer's conversation id
                bool capability = answer.ViaCapability && _client.Network.CurrentSim?.Caps?.CapabilityURI(answer.Accept ? "AcceptFriendship" : "DeclineFriendship") is not null;
                if (capability)
                    Run("friendship-answer", () => answer.Accept
                        ? friends.AcceptFriendshipViaCapAsync(new UUID(answer.From))
                        : friends.DeclineFriendshipViaCapAsync(new UUID(answer.From)));
                else if (answer.OfferId != Guid.Empty)
                {
                    if (answer.Accept)
                        friends.AcceptFriendship(new UUID(answer.From), new UUID(answer.OfferId));
                    else
                        friends.DeclineFriendship(new UUID(answer.From), new UUID(answer.OfferId));
                }
                else
                    return CommandDispatch.NotSent("no_way_to_answer");
                break;
            }
            case FriendshipEndCommand end:
                if (!friends.FriendList.ContainsKey(new UUID(end.Friend)))
                    return CommandDispatch.NotSent("not_a_friend");   // the library sends nothing for someone it does not list
                friends.TerminateFriendship(new UUID(end.Friend));
                break;
            case FriendRightsCommand rights:
                friends.GrantRights(new UUID(rights.Friend), (FriendRights)(int)rights.Rights);
                break;
            case GroupChatJoinCommand join:
                self.RequestJoinGroupChat(new UUID(join.Group));
                break;
            case GroupChatLeaveCommand leave:
                self.RequestLeaveGroupChat(new UUID(leave.Group));
                break;
            case GroupMessageCommand message:
                if (!self.GroupChatSessions.ContainsKey(new UUID(message.Group)))
                    return CommandDispatch.NotSent("no_group_session");   // the library only logs and sends nothing without a joined session
                self.InstantMessageGroup(new UUID(message.Group), message.Text);
                break;
            case ActiveGroupCommand active:
                groups.ActivateGroup(new UUID(active.Group));
                break;
            case ActiveTitleCommand title:
                groups.ActivateTitle(new UUID(title.Group), new UUID(title.Role));
                break;
            case GroupNoticeAnswerCommand notice:
            {
                // dialog 33 (accepted) carries the folder the item should go to; 34 (declined) carries nothing
                byte[] bucket = Utils.EmptyBytes;
                if (notice.Accept)
                {
                    var folder = _client.Inventory.FindFolderForType((AssetType)notice.AssetType);
                    if (folder == UUID.Zero)
                        return CommandDispatch.NotSent("no_folder");
                    bucket = folder.GetBytes();
                }
                self.InstantMessage(self.Name, new UUID(notice.Sender), "", new UUID(notice.OfferId),
                    notice.Accept ? InstantMessageDialog.GroupNoticeInventoryAccepted : InstantMessageDialog.GroupNoticeInventoryDeclined,
                    InstantMessageOnline.Online, self.SimPosition, UUID.Zero, bucket);
                break;
            }
            case MuteAddCommand add:
                self.UpdateMuteListEntry((MuteType)(int)add.Entry.Kind, new UUID(add.Entry.Id), add.Entry.Name, (MuteFlags)(int)add.Entry.Flags);
                break;
            case MuteRemoveCommand remove:
                self.RemoveMuteListEntry(new UUID(remove.Id), remove.Name);
                break;
            default:
                return CommandDispatch.NotSent("unknown_command");
        }
        return CommandDispatch.Handed;
    }

    /// <summary>Stops taking in packets from the current circuit, so it goes quiet and the library times it out (see <see cref="IInboundGate"/>).</summary>
    public bool CloseInbound()
    {
        var sim = _client.Network.CurrentSim;
        if (_disposed || sim is null || !_client.Network.Connected)
            return false;
        _log.Warn("circuit.close-inbound", CoreLog.F("note", "the circuit takes in no more packets"));
        sim.Stop();   // the circuit's receive loop and socket close; nothing more arrives, so the library times the circuit out
        return true;
    }

    /// <summary>Asks the grid for the block list (the library sends the request and follows the transfer of the file the grid names). Nothing without a circuit.</summary>
    public void RequestMuteList()
    {
        if (_disposed || !_client.Network.Connected)
            return;
        _client.Self.RequestMuteList();
    }

    /// <summary>The library's block list as the core's records (the protocol's kind and flag numbers are the same).</summary>
    internal static IReadOnlyList<Session.MuteEntry> ToMuteEntries(IEnumerable<LibreMetaverse.MuteEntry> entries) =>
        entries.Select(e => new Session.MuteEntry((Session.MuteKind)(int)e.Type, e.ID.Guid, e.Name ?? "", (uint)(int)e.Flags)).ToArray();

    private void OnMuteListUpdated(object? sender, EventArgs e) =>
        Raise(new MuteListReceived(ToMuteEntries(_client.Self.MuteList.Values)));

    /// <summary>Starts a command whose answer takes a while; an exception is logged by type (the content of a command is never logged).</summary>
    private void Run(string what, Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("command.failed", CoreLog.F("command", what), CoreLog.F("error", ex.GetType().Name));
            }
        });
    }

    /// <summary>The region answered a sit request: the sit is completed for the object that was asked for, and for no other.</summary>
    private void OnAvatarSitResponse(object? sender, AvatarSitResponseEventArgs e)
    {
        bool ours;
        lock (_lock)
        {
            ours = _pendingSit != Guid.Empty && _pendingSit == e.ObjectID.Guid;
            if (ours) _pendingSit = Guid.Empty;
        }
        Raise(new SitResponded(e.ObjectID.Guid, V(e.CameraEyeOffset), V(e.CameraAtOffset), e.ForceMouselook, e.Autopilot, V(e.SitPosition), Q(e.SitRotation)));
        if (ours && !_disposed)
            _client.Self.Sit();
    }

    /// <summary>A library chat event as the core's record: the text wrapped as a message body, the kinds mapped.</summary>
    internal static ChatReceived ToChat(SimRef? sim, ChatEventArgs e) =>
        new(sim, e.SourceID.Guid, e.OwnerID.Guid, e.FromName ?? "",
            e.SourceType switch { ChatSourceType.Agent => ChatSource.Agent, ChatSourceType.Object => ChatSource.Object, _ => ChatSource.System },
            e.Type switch
            {
                ChatType.Whisper => ChatLineType.Whisper,
                ChatType.Normal => ChatLineType.Say,
                ChatType.Shout => ChatLineType.Shout,
                ChatType.StartTyping => ChatLineType.StartTyping,
                ChatType.StopTyping => ChatLineType.StopTyping,
                ChatType.Debug => ChatLineType.Debug,
                ChatType.OwnerSay => ChatLineType.OwnerSay,
                ChatType.RegionSayTo => ChatLineType.RegionSayTo,
                ChatType.RegionSay => ChatLineType.RegionSay,
                _ => ChatLineType.Other,
            },
            e.AudibleLevel switch { ChatAudibleLevel.Fully => ChatAudible.Fully, ChatAudibleLevel.Barely => ChatAudible.Barely, _ => ChatAudible.Not },
            V(e.Position), new MessageBody(e.Message ?? "", MessageChannel.LocalChat));

    /// <summary>The earliest stored-message time believed: the library's capability path builds the time from a seconds
    /// count as if it were ticks, which lands in year 1; such a time is dropped rather than shown wrong.</summary>
    private static readonly DateTime EarliestBelievable = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A library instant message as the core's record. Only a stored (offline) message keeps a time.</summary>
    internal static InstantMessageReceived ToInstantMessage(InstantMessage im, bool groupSession = false)
    {
        bool offline = im.Offline == InstantMessageOnline.Offline;
        DateTimeOffset? sentAt = offline && im.Timestamp >= EarliestBelievable
            ? new DateTimeOffset(im.Timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(im.Timestamp, DateTimeKind.Utc) : im.Timestamp)
            : null;
        return new(im.FromAgentID.Guid, im.FromAgentName ?? "", im.ToAgentID.Guid, im.IMSessionID.Guid,
            im.Dialog switch
            {
                InstantMessageDialog.MessageFromAgent => ImKind.Message,
                InstantMessageDialog.StartTyping => ImKind.StartTyping,
                InstantMessageDialog.StopTyping => ImKind.StopTyping,
                InstantMessageDialog.MessageBox => ImKind.MessageBox,
                // a session message in a group's chat session; one in an ad-hoc conference is not handled here
                InstantMessageDialog.SessionSend when im.GroupIM || groupSession => ImKind.GroupMessage,
                _ => ImKind.Other,
            },
            (int)im.Dialog, offline, sentAt, im.GroupIM || (groupSession && im.Dialog == InstantMessageDialog.SessionSend),
            new MessageBody(im.Message ?? "", MessageChannel.InstantMessage));
    }

    /// <summary>A friendship message (offered, accepted or declined) as the core's record; null for any other dialog. An offer
    /// that arrived while this agent was away (a stored message) is marked not online: it is answered through the capability.</summary>
    internal static TransportEvent? ToFriendEvent(InstantMessage im) => im.Dialog switch
    {
        InstantMessageDialog.FriendshipOffered => new FriendshipOffered(im.FromAgentID.Guid, im.FromAgentName ?? "", im.IMSessionID.Guid,
            im.Offline == InstantMessageOnline.Online, new MessageBody(im.Message ?? "", MessageChannel.InstantMessage)),
        InstantMessageDialog.FriendshipAccepted => new FriendshipAnswered(im.FromAgentID.Guid, im.FromAgentName ?? "", true),
        InstantMessageDialog.FriendshipDeclined => new FriendshipAnswered(im.FromAgentID.Guid, im.FromAgentName ?? "", false),
        _ => null,
    };

    /// <summary>A friend as the core's record. The library's "their rights" are what this agent gave the friend; "my rights" what the
    /// friend gave this agent (the bits are the protocol's, the same in both enums).</summary>
    internal static FriendEntry ToFriend(Guid id, string? name, bool online, FriendRights gaveThem, FriendRights gaveMe) =>
        new(id, name ?? "", online, (FriendRightsFlags)(int)gaveThem, (FriendRightsFlags)(int)gaveMe);

    /// <summary>A group notice message as the core's record; null for any other dialog or a notice that cannot be read. The text is
    /// "subject|body" split at the first bar; the binary bucket is: has attachment (1 byte), asset type (1 byte), the group id (16
    /// bytes), the attachment's name and a terminating zero. A bucket that is too short, not terminated or without a group is malformed.</summary>
    internal static GroupNoticeReceived? ToGroupNotice(InstantMessage im)
    {
        if (im.Dialog is not (InstantMessageDialog.GroupNotice or InstantMessageDialog.GroupNoticeRequested))
            return null;
        var bucket = im.BinaryBucket ?? [];
        if (bucket.Length < 19 || bucket[^1] != 0)
            return null;
        var group = new UUID(bucket, 2).Guid;
        if (group == Guid.Empty)
            return null;
        int nameEnd = Array.IndexOf(bucket, (byte)0, 18);
        string itemName = System.Text.Encoding.UTF8.GetString(bucket, 18, nameEnd - 18);
        string text = im.Message ?? "";
        int bar = text.IndexOf('|');
        string subject = bar < 0 ? text : text[..bar];
        string body = bar < 0 ? "" : text[(bar + 1)..];
        DateTimeOffset? sentAt = im.Timestamp >= EarliestBelievable
            ? new DateTimeOffset(im.Timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(im.Timestamp, DateTimeKind.Utc) : im.Timestamp)
            : null;
        return new GroupNoticeReceived(group, im.FromAgentID.Guid, im.FromAgentName ?? "",
            new MessageBody(subject, MessageChannel.InstantMessage), new MessageBody(body, MessageChannel.InstantMessage),
            bucket[0] != 0, itemName, bucket[1], im.IMSessionID.Guid, sentAt, im.Dialog == InstantMessageDialog.GroupNoticeRequested);
    }

    // ---- teleport, map, coarse locations (the library's messages as the core's records) ----

    /// <summary>A teleport message as the core's record; null for a status that carries nothing to show. The grid's flags can forbid
    /// cancelling; a finish with the same-region flag is a teleport inside the region.</summary>
    internal static TeleportProgressed? ToTeleportProgress(TeleportEventArgs e)
    {
        bool canCancel = !e.Flags.HasFlag(TeleportFlags.DisableCancel);
        return e.Status switch
        {
            TeleportStatus.Start => new TeleportProgressed(TeleportPhase.Started, "", canCancel),
            TeleportStatus.Progress => new TeleportProgressed(TeleportPhase.Progress, e.Message ?? "", canCancel),
            TeleportStatus.Failed => new TeleportProgressed(TeleportPhase.Failed, e.Message ?? "", canCancel),
            TeleportStatus.Finished => new TeleportProgressed(e.Flags.HasFlag(TeleportFlags.FinishedViaSameSim) ? TeleportPhase.Local : TeleportPhase.Finished, "", canCancel),
            TeleportStatus.Cancelled => new TeleportProgressed(TeleportPhase.Cancelled, "", canCancel),
            _ => null,
        };
    }

    // ---- teleport requests, grid alerts, parcel collisions ----

    /// <summary>A message asking this agent to offer a teleport (the request dialog, kind 26) as the core's record; null for any other
    /// dialog or a request from nobody. A request is not an offer and never starts anything.</summary>
    internal static TeleportRequested? ToTeleportRequest(InstantMessage im) =>
        im.Dialog == InstantMessageDialog.RequestLure && im.FromAgentID != UUID.Zero
            ? new TeleportRequested(im.FromAgentID.Guid, im.FromAgentName ?? "", new MessageBody(im.Message ?? "", MessageChannel.InstantMessage))
            : null;

    /// <summary>An alert message from the grid as the core's record.</summary>
    internal static ServerAlert ToServerAlert(string message, string? key, bool modal) => new(message ?? "", key ?? "", modal);

    /// <summary>The parcel-properties sequence numbers that mean a nearby parcel keeps this agent out (not in its group, banned, not on its
    /// access list); null for every other number.</summary>
    internal static ParcelCollision? ToParcelCollision(int sequenceId, byte[]? bitmap = null, bool usePassList = false, SimRef? sim = null) => sequenceId switch
    {
        (int)ParcelPropertiesStatus.CollisionNotInGroup => new ParcelCollision(ParcelDenial.NotInGroup, bitmap, usePassList, sim),
        (int)ParcelPropertiesStatus.CollisionBanned => new ParcelCollision(ParcelDenial.Banned, bitmap, usePassList, sim),
        (int)ParcelPropertiesStatus.CollisionNotOnAccessList => new ParcelCollision(ParcelDenial.NotOnList, bitmap, usePassList, sim),
        _ => null,
    };

    /// <summary>How a request to be offered a teleport goes out: an instant message of the request kind, sent online, with no conversation
    /// id and an empty bucket, carrying the typed text (the official viewer's request: llavataractions.cpp teleport_request_callback).</summary>
    internal readonly record struct TeleportRequestMessage(InstantMessageDialog Dialog, InstantMessageOnline Offline, UUID Session, string Text, int BucketLength);

    internal static TeleportRequestMessage ToTeleportRequestMessage(string text) =>
        new(InstantMessageDialog.RequestLure, InstantMessageOnline.Online, UUID.Zero, text ?? "", 0);

    /// <summary>A teleport offer message (a lure) as the core's record; null for any other dialog or an offer from nobody.</summary>
    internal static TeleportOffered? ToTeleportOffer(InstantMessage im) =>
        im.Dialog == InstantMessageDialog.RequestTeleport && im.FromAgentID != UUID.Zero
            ? new TeleportOffered(im.FromAgentID.Guid, im.FromAgentName ?? "", im.IMSessionID.Guid, new MessageBody(im.Message ?? "", MessageChannel.InstantMessage))
            : null;

    // ---- what scripts ask of the viewer (the library's messages as the core's records) ----

    /// <summary>Whether the adapter can run a command at all (the guard in <see cref="Execute"/>). A local command (one the viewer runs
    /// itself, such as opening a web address) never reaches the grid adapter.</summary>
    internal static bool IsKnownCommand(TransportCommand command) => command is SayCommand or InstantMessageCommand or TouchCommand or SitCommand or StandCommand or TeleportRegionCommand
        or TeleportLandmarkCommand or TeleportHomeCommand or TeleportHandleCommand or TeleportCancelCommand or TeleportLureAnswerCommand or TeleportOfferCommand or SetHomeCommand or TeleportRequestCommand
        or MoveInventoryItemCommand or ChatTypingCommand or ImTypingCommand
        or FriendshipOfferCommand or FriendshipAnswerCommand or FriendshipEndCommand or FriendRightsCommand
        or GroupChatJoinCommand or GroupChatLeaveCommand or GroupMessageCommand or ActiveGroupCommand or ActiveTitleCommand or GroupNoticeAnswerCommand
        or GrabCommand or ReleaseCommand or SitGroundCommand or RequestObjectPropertiesCommand or TakeObjectCommand or RequestParcelCommand
        or CreateInventoryFolderCommand or RenameInventoryFolderCommand or MoveInventoryFolderCommand or ChangeOutfitCommand or SaveOutfitCommand or RebuildLookCommand or SaveWearableCommand or SaveWearableAsCommand
        or MuteAddCommand or MuteRemoveCommand
        or ScriptDialogReplyCommand or ScriptPermissionAnswerCommand
        or InventoryOfferAnswerCommand or AttachWorldObjectCommand or DropAttachmentCommand
        or RequestTaskInventoryCommand or CopyTaskContentsCommand or BuyObjectCommand or RequestPayPriceCommand or PayObjectCommand
        or CreateObjectCommand or RezItemCommand or DeleteObjectCommand or EditObjectCommand or CopyFromNotecardCommand;

    /// <summary>A script dialog as the core's record: the owner is a group when the region gave no first name (the last name is then the group's).</summary>
    internal static ScriptDialogReceived? ToScriptDialog(SimRef? sim, ScriptDialogEventArgs e)
    {
        bool group = string.IsNullOrEmpty(e.FirstName);
        string owner = group ? e.LastName ?? "" : $"{e.FirstName} {e.LastName}".Trim();
        return new ScriptDialogReceived(sim, e.ObjectID.Guid, e.OwnerID.Guid, e.ObjectName ?? "", owner, group,
            new MessageBody(e.Message ?? "", MessageChannel.LocalChat), e.Channel, (e.ButtonLabels ?? []).ToArray());
    }

    /// <summary>A permission request as the core's record: the mask as the region sent it.</summary>
    internal static ScriptPermissionRequested? ToScriptPermission(SimRef? sim, ScriptQuestionEventArgs e) =>
        new(sim, e.TaskID.Guid, e.ItemID.Guid, e.ObjectName ?? "", e.ObjectOwnerName ?? "", (int)e.Questions);

    /// <summary>A load-URL request as the core's record.</summary>
    internal static LoadUrlRequested? ToLoadUrl(SimRef? sim, LoadUrlEventArgs e) =>
        new(sim, e.ObjectID.Guid, e.OwnerID.Guid, e.OwnerIsGroup, e.ObjectName ?? "", new MessageBody(e.Message ?? "", MessageChannel.LocalChat), e.URL ?? "");

    /// <summary>The option bits a map-destination request carries when it has no options block: show the map and focus it.</summary>
    private const uint DefaultMapDestinationFlags = 3;

    /// <summary>A script's map destination as the core's record.</summary>
    internal static MapDestinationRequested? ToMapDestination(ScriptTeleportRequestPacket p) =>
        new(Utils.BytesToString(p.Data.ObjectName ?? []), Utils.BytesToString(p.Data.SimName ?? []), V(p.Data.SimPosition), V(p.Data.LookAt),
            p.Options is { Length: > 0 } options ? options[0].Flags : DefaultMapDestinationFlags);

    /// <summary>The sender name the grid uses for its own messages.</summary>
    private const string SystemSender = "Second Life";

    /// <summary>An instant message from an object as the core's record; null for any other dialog. The place text is the message's
    /// bucket (a region name and position); a message from the grid's own sender, with no region and no position, is marked as the system's.</summary>
    internal static ObjectInstantMessage? ToObjectInstantMessage(InstantMessage im)
    {
        if (im.Dialog != InstantMessageDialog.MessageFromObject)
            return null;
        string place = System.Text.Encoding.UTF8.GetString(im.BinaryBucket ?? []).TrimEnd((char)0);
        bool system = im.FromAgentName == SystemSender && im.RegionID == UUID.Zero && im.Position == LibreMetaverse.Vector3.Zero;
        return new ObjectInstantMessage(system ? Guid.Empty : im.IMSessionID.Guid, im.FromAgentID.Guid, im.FromAgentName ?? "", false, place, system,
            new MessageBody(im.Message ?? "", MessageChannel.InstantMessage));
    }

    /// <summary>A teleport failure message as the core's record: the reason, or the alert's message key when it has one (the key is what the
    /// grid's table of reasons is indexed by), and the region the alert names.</summary>
    internal static TeleportProgressed ToTeleportFailed(TeleportFailedPacket p)
    {
        string reason = Utils.BytesToString(p.Info.Reason ?? []);
        string key = p.AlertInfo is { Length: > 0 } alerts && Utils.BytesToString(alerts[0].Message ?? []) is { Length: > 0 } k ? k : reason;
        string region = p.AlertInfo is { Length: > 0 } a ? RegionNameOf(Utils.BytesToString(a[0].ExtraParams ?? [])) : "";
        return new TeleportProgressed(TeleportPhase.Failed, key, true, region);
    }

    /// <summary>The region name an alert's extra block names (a map with REGION_NAME), or empty when there is none or it cannot be read.</summary>
    internal static string RegionNameOf(string extraParams)
    {
        if (string.IsNullOrWhiteSpace(extraParams))
            return "";
        try { return OSDParser.DeserializeLLSDXml(extraParams) is OSDMap map && map.TryGetValue("REGION_NAME", out var name) ? name.AsString() : ""; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ""; }
    }

    /// <summary>A region's coarse locations as the core's record.</summary>
    internal static CoarseLocationsUpdated ToCoarse(SimRef? sim, IReadOnlyDictionary<UUID, Vector3> positions) =>
        new(sim, positions.Select(kv => new CoarseAvatar(kv.Key.Guid, V(kv.Value))).ToArray());

    /// <summary>The grid's answer to a friend's location request as the core's record.</summary>
    internal static FriendLocated ToFriendLocated(UUID friend, ulong regionHandle, Vector3 position) => new(friend.Guid, regionHandle, V(position));

    /// <summary>The map blocks of a reply packet as the core's records (a legacy reply has no size block: size 0).</summary>
    internal static IReadOnlyList<MapBlockSize> ToMapBlocks(MapBlockReplyPacket p)
    {
        var blocks = new List<MapBlockSize>(p.Data?.Length ?? 0);
        for (int i = 0; i < (p.Data?.Length ?? 0); i++)
        {
            int sx = p.Size is not null && i < p.Size.Length ? p.Size[i].SizeX : 0;
            int sy = p.Size is not null && i < p.Size.Length ? p.Size[i].SizeY : 0;
            var d = p.Data![i];
            blocks.Add(new MapBlockSize(d.X, d.Y, sx, sy, Utils.BytesToString(d.Name), d.MapImageID.Guid, d.Access));
        }
        return blocks;
    }

    private void OnAlertMessage(object? sender, AlertMessageEventArgs e) =>
        Raise(ToServerAlert(e.Message, e.NotificationId, modal: false));

    /// <summary>The sequence number of a parcel request made for About Land: the reply carries it back, so other parcel replies are not mistaken for it.</summary>
    internal const int ParcelInfoSequence = 4242;

    private static Vector3 Surface3(N.Vector2 v) => new(v.X, v.Y, 0f);

    /// <summary>Take (or take a copy of) an object into the Objects folder of the agent's inventory.</summary>
    private void SendDeRez(uint localId, bool copy)
    {
        var sim = _client.Network.CurrentSim;
        var objects = _client.Inventory.FindFolderForType(FolderType.Object);
        var packet = LmvBuild.DeRez(_client.Self.AgentID, _client.Self.SessionID, localId,
            (byte)(copy ? DeRezDestination.AgentInventoryCopy : DeRezDestination.AgentInventoryTake), objects, UUID.Random());
        if (sim is not null) _client.Network.SendPacket(packet, sim);
    }

    private void OnObjectProperties(object? sender, ObjectPropertiesEventArgs e)
    {
        var p = e.Properties;
        Raise(new ObjectPropertiesReceived(p.ObjectID.Guid, p.Name ?? "", p.Description ?? "", p.OwnerID.Guid, p.CreatorID.Guid, p.GroupID.Guid,
            p.CreationDate == default ? null : new DateTimeOffset(p.CreationDate, TimeSpan.Zero),
            (uint)p.Permissions.OwnerMask, (uint)p.Permissions.EveryoneMask, (uint)p.Permissions.NextOwnerMask, p.SalePrice, p.SaleType != SaleType.Not, (byte)p.SaleType));
    }

    private void OnScriptDialog(object? sender, ScriptDialogEventArgs e)
    {
        if (ToScriptDialog(CurrentSimRef(), e) is { } dialog)
            Raise(dialog);
    }

    private void OnScriptQuestion(object? sender, ScriptQuestionEventArgs e)
    {
        if (ToScriptPermission(MapOrNull(e.Simulator) ?? CurrentSimRef(), e) is { } question)
            Raise(question);
    }

    private void OnLoadUrl(object? sender, LoadUrlEventArgs e)
    {
        if (ToLoadUrl(CurrentSimRef(), e) is { } url)
            Raise(url);
    }

    private void OnScriptTeleportRequest(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is ScriptTeleportRequestPacket p && ToMapDestination(p) is { } destination)
            Raise(destination);
    }

    /// <summary>The region the library says it is in; the script events carry none, and they come from the region the agent stands in.</summary>
    private SimRef? CurrentSimRef() => MapOrNull(_client.Network.CurrentSim);

    private void OnParcelProperties(object? sender, ParcelPropertiesEventArgs e)
    {
        if (e.SequenceID == ParcelInfoSequence && e.Parcel is { } pr)
            Raise(ToParcelDetails(pr));
        if (ToAgentParcel(e.SequenceID, e.Parcel?.Name, e.Parcel?.LocalID ?? 0, MapOrNull(e.Simulator), e.Parcel?.Flags.HasFlag(ParcelFlags.SoundLocal) == true, e.Parcel?.Bitmap) is { } here)
        {
            Raise(here);
            if (here.LocalId > 0 && e.Simulator is { } hereSim && IsAgentsRegion(hereSim))
                StartParcelEnvironment(hereSim, here.LocalId);
        }
        if (ToParcelCollision(e.SequenceID, e.Parcel?.Bitmap, e.Parcel?.Flags.HasFlag(ParcelFlags.UsePassList) == true, MapOrNull(e.Simulator)) is { } collision)
            Raise(collision);
    }

    /// <summary>The agent's own parcel from a parcel-properties message; null for the numbers that mean something else (a selected
    /// parcel, one that denies entry, the one under the pointer, About Land's request).</summary>
    internal static AgentParcelChanged? ToAgentParcel(int sequenceId, string? name, int localId, SimRef? sim, bool soundLocal = false, byte[]? bitmap = null) =>
        sequenceId >= 0 && sequenceId != ParcelInfoSequence && sim is not null ? new AgentParcelChanged(sim, name ?? "", localId, soundLocal, bitmap) : null;

    /// <summary>A parcel as About Land shows it.</summary>
    internal static ParcelDetailsReceived ToParcelDetails(Parcel pr) => new(pr.LocalID, pr.Name ?? "", pr.Desc ?? "", pr.OwnerID.Guid, pr.IsGroupOwned, pr.Area,
        pr.Flags.HasFlag(ParcelFlags.UseAccessList), pr.Flags.HasFlag(ParcelFlags.UseBanList), pr.Flags.HasFlag(ParcelFlags.UseAccessGroup),
        pr.Flags.HasFlag(ParcelFlags.DenyAnonymous), pr.Flags.HasFlag(ParcelFlags.DenyAgeUnverified), pr.Flags.HasFlag(ParcelFlags.UsePassList),
        pr.PassPrice, pr.PassHours, pr.Flags.HasFlag(ParcelFlags.ForSale), pr.SalePrice);

    private void OnChat(object? sender, ChatEventArgs e) => Raise(ToChat(MapOrNull(e.Simulator), e));

    private void OnInstantMessage(object? sender, InstantMessageEventArgs e)
    {
        var im = e.IM;
        if (ToFriendEvent(im) is { } friend)
        {
            Raise(friend);
            return;
        }
        if (ToInventoryOffer(im) is { } inventoryOffer)
        {
            Raise(inventoryOffer);
            return;
        }
        if (im.Dialog is InstantMessageDialog.InventoryOffered or InstantMessageDialog.TaskInventoryOffered)
            _log.Warn("im.offer-unreadable", CoreLog.F("dialog", (int)im.Dialog), CoreLog.F("bucket-bytes", im.BinaryBucket?.Length ?? 0));
        if (ToTeleportRequest(im) is { } request)
        {
            Raise(request);
            return;
        }
        if (ToTeleportOffer(im) is { } offer)
        {
            Raise(offer);
            return;
        }
        if (ToObjectInstantMessage(im) is { } objectMessage)
        {
            Raise(objectMessage);   // an object's message opens no conversation
            return;
        }
        if (im.Dialog is InstantMessageDialog.GroupNotice or InstantMessageDialog.GroupNoticeRequested)
        {
            if (ToGroupNotice(im) is { } notice)
                Raise(notice);   // a notice that cannot be read is dropped
            return;
        }
        Raise(ToInstantMessage(im, _client.Self.IsGroupMessage(im)));
    }

    // ---- friends and groups: the library keeps the lists; these hand the changes on as plain records ----

    private void OnFriendsReady(object? sender, FriendsReadyEventArgs e) =>
        Raise(new FriendsListed(_client.Friends.FriendList.Values
            .Select(f => ToFriend(f.UUID.Guid, f.Name, f.IsOnline, f.TheirFriendRights, f.MyFriendRights)).ToArray()));

    private void OnFriendOnline(object? sender, FriendInfoEventArgs e) => Raise(new FriendPresenceChanged(e.Friend.UUID.Guid, true));

    private void OnFriendOffline(object? sender, FriendInfoEventArgs e) => Raise(new FriendPresenceChanged(e.Friend.UUID.Guid, false));

    private void OnFriendRights(object? sender, FriendInfoEventArgs e) =>
        Raise(new FriendRightsChanged(e.Friend.UUID.Guid, (FriendRightsFlags)(int)e.Friend.TheirFriendRights, (FriendRightsFlags)(int)e.Friend.MyFriendRights));

    private void OnFriendshipTerminated(object? sender, FriendshipTerminatedEventArgs e) => Raise(new FriendshipEnded(e.AgentID.Guid));

    private void OnCurrentGroups(object? sender, CurrentGroupsEventArgs e) =>
        Raise(new GroupsListed(e.Groups.Values.Select(g => new GroupEntry(g.ID.Guid, g.Name ?? "", g.AcceptNotices, g.ListInProfile)).ToArray()));

    private void OnAgentData(object? sender, AgentDataReplyEventArgs e) =>
        Raise(new ActiveGroupChanged(e.ActiveGroupID.Guid, e.GroupName ?? "", e.GroupTitle ?? ""));

    private void OnGroupTitles(object? sender, GroupTitlesReplyEventArgs e) =>
        Raise(new GroupTitlesListed(e.GroupID.Guid, e.Titles.Values.Select(t => new GroupTitleEntry(t.RoleID.Guid, t.Title ?? "", t.Selected)).ToArray()));

    private void OnGroupChatJoined(object? sender, GroupChatJoinedEventArgs e) => Raise(new GroupChatJoined(e.SessionID.Guid, e.Success));

    private readonly RegionNameWaits _regionNames = new();

    /// <summary>How long a region name is given to be answered by the grid's map before the teleport is reported failed.</summary>
    internal static readonly TimeSpan RegionNameTimeout = TimeSpan.FromSeconds(15);

    private readonly PendingNamedTeleport _namedTeleport = new();

    /// <summary>Teleports to the region with this name: the map is asked which region it is (the answers also reach the world map), then the
    /// library teleports by handle and follows the teleport through to its end. The library's own lookup by name waits a fixed five seconds
    /// for a first answer and then another five, which made the first teleport by name slow; a name the map does not know is reported at once
    /// as a destination that was not found.</summary>
    private async Task TeleportToNamedRegionAsync(string regionName, Vector3 position)
    {
        var token = _namedTeleport.Begin(_mapFetches.Token);
        _client.Grid.RequestMapRegion(regionName, GridLayerType.Objects);
        var handle = await _regionNames.WaitAsync(regionName, RegionNameTimeout, token).ConfigureAwait(false);
        _namedTeleport.End(token);
        if (_disposed || token.IsCancellationRequested)
            return;   // the user cancelled while the region was being found (or the session ended): nothing is sent, nothing is reported
        if (handle is not { } found)
        {
            Raise(new TeleportProgressed(TeleportPhase.Failed, "no_host", true));
            return;
        }
        await _client.Self.TeleportAsync(found, position, CancellationToken.None).ConfigureAwait(false);
    }

    private void OnTeleportProgress(object? sender, TeleportEventArgs e)
    {
        if (ToTeleportProgress(e) is { } progress)
            Raise(progress);
    }

    /// <summary>The library ignores a teleport failure that comes before its own teleport status has left "none", which is how a failed go-home
    /// or landmark request (no start message ever comes) would vanish; the failure is reported here as well, straight from the message.</summary>
    private void OnTeleportFailedPacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is TeleportFailedPacket p)
            Raise(ToTeleportFailed(p));
    }

    private void OnTeleportFailedEvent(string capsKey, IMessage message, Simulator simulator)
    {
        if (message is TeleportFailedMessage m)
            Raise(new TeleportProgressed(TeleportPhase.Failed, string.IsNullOrEmpty(m.MessageKey) ? m.Reason ?? "" : m.MessageKey, true, RegionNameOf(m.ExtraParams ?? "")));
    }

    private void OnCoarseLocations(object? sender, CoarseLocationUpdateEventArgs e) => Raise(ToCoarse(MapOrNull(e.Simulator), e.Positions));

    private void OnFriendFound(object? sender, FriendFoundReplyEventArgs e) => Raise(ToFriendLocated(e.AgentID, e.RegionHandle, e.Location));

    // ---- map reads (not user commands) ----

    /// <summary>Asks the grid for the map blocks of the cells min..max, non-existent cells too (the answers are <see cref="MapBlockSize"/> events).</summary>
    public void RequestMapBlocks(int minX, int minY, int maxX, int maxY)
    {
        if (_disposed || !_client.Network.Connected)
            return;
        _client.Grid.RequestMapBlocks(GridLayerType.Objects, (ushort)minX, (ushort)minY, (ushort)maxX, (ushort)maxY, true);
    }

    /// <summary>Asks the grid for the regions matching a name (the answers are <see cref="MapBlockSize"/> events).</summary>
    public void RequestRegionNamed(string name)
    {
        if (_disposed || !_client.Network.Connected)
            return;
        _client.Grid.RequestMapRegion(name, GridLayerType.Objects);
    }

    /// <summary>Asks the grid where a friend is; the answer is a <see cref="FriendLocated"/> event (the grid answers only for a friend who allows it).</summary>
    public void RequestFriendLocation(Guid friend)
    {
        if (_disposed || !_client.Network.Connected)
            return;
        _client.Friends.MapFriend(new UUID(friend));
    }

    private HttpClient? _mapHttp;
    private readonly CancellationTokenSource _mapFetches = new();

    /// <summary>The largest map tile picture taken (the grid's tiles are a few kilobytes).</summary>
    public const int MapTileCapBytes = 1024 * 1024;

    /// <summary>A map tile picture from the grid's map server (the region's own setting, else the login reply's), through the outbound policy and bounded before it is held.
    /// A 404 is NotFound; a missing or unusable map server address, a refusal or any other failure is Failed.</summary>
    public async Task<Viewer.Core.Assets.AssetFetchResult> FetchMapTileAsync(int level, int gridX, int gridY, CancellationToken ct)
    {
        var uri = Viewer.Core.Map.MapMath.TileUri(GridFeatures().MapServerUrl, new Viewer.Core.Map.MapTileKey(level, gridX, gridY));
        if (uri is null)
            return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Failed);
        HttpClient http;
        lock (_lock)
        {
            if (_disposed)
                return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Cancelled);
            http = _mapHttp ??= OutboundHttp.CreateClient(_outbound, _log, TimeSpan.FromSeconds(20), 4, _certificates);
        }
        try
        {
            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, _mapFetches.Token);
            var r = await BoundedHttp.GetBoundedAsync(http, uri, MapTileCapBytes, both.Token).ConfigureAwait(false);
            if (r.Status == 404)
                return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.NotFound);
            return r.Body is { Length: > 0 } body ? Viewer.Core.Assets.AssetFetchResult.Ok(body) : Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Failed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _mapFetches.IsCancellationRequested)
        {
            return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Cancelled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("map.tile-failed", CoreLog.F("error", ex.GetType().Name));
            return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Failed);
        }
    }

    /// <summary>Asks the grid for the groups this agent is in (a read; the answer is a <see cref="GroupsListed"/> event).</summary>
    public void RequestGroups()
    {
        if (_disposed || !_client.Network.Connected)
            return;
        _client.Groups.RequestCurrentGroups();
    }

    /// <summary>Asks the grid for the titles a group offers (a read; the answer is a <see cref="GroupTitlesListed"/> event).</summary>
    public void RequestGroupTitles(Guid group)
    {
        if (_disposed || group == Guid.Empty || !_client.Network.Connected)
            return;
        _client.Groups.RequestGroupTitles(new UUID(group));
    }

    private void OnNames(object? sender, UUIDNameReplyEventArgs e) =>
        Raise(new AgentNamesReceived(e.Names.ToDictionary(kv => kv.Key.Guid, kv => kv.Value)));

    /// <summary>Asks the grid for agents' names (a read; the answer arrives as <see cref="AgentNamesReceived"/>). Nothing without a circuit.</summary>
    public void RequestAgentNames(IReadOnlyList<Guid> agents)
    {
        if (_disposed || agents.Count == 0 || !_client.Network.Connected)
            return;
        _client.Avatars.RequestAvatarNames(agents.Select(a => new UUID(a)).ToList());
    }

    /// <summary>Asks the grid for the messages it stored while the agent was away (the capability, or the older message
    /// when the region has none). Nothing without a circuit; a failure is logged by type.</summary>
    public void RequestOfflineMessages()
    {
        if (_disposed || !_client.Network.Connected)
            return;
        Run("offline-messages", () => _client.Self.RetrieveInstantMessagesAsync(CancellationToken.None));
    }

    /// <summary>A local typing start or stop: a chat line of that kind with no text on channel 0. Built here rather than through
    /// the library's chat call, whose splitting may send nothing for empty text.</summary>
    private void SendChatTyping(bool started)
    {
        var self = _client.Self;
        _client.Network.SendPacket(new ChatFromViewerPacket
        {
            AgentData = { AgentID = self.AgentID, SessionID = self.SessionID },
            ChatData = { Channel = 0, Message = Utils.StringToBytes(""), Type = (byte)(started ? ChatType.StartTyping : ChatType.StopTyping) },
        });
    }

    internal static ChatType ChatTypeOf(ChatKind kind) => kind switch
    {
        ChatKind.Whisper => ChatType.Whisper,
        ChatKind.Shout => ChatType.Shout,
        _ => ChatType.Normal,
    };

    /// <summary>Sets the movement state's control bits to exactly <paramref name="controls"/> (the bits
    /// <see cref="Viewer.Motion.AgentControlFlags"/> names; every other bit cleared), through the library's own
    /// properties, so its periodic updates send the same bits.</summary>
    internal static void ApplyControls(AgentManager.AgentMovement mv, uint controls)
    {
        bool B(uint bit) => (controls & bit) != 0;
        mv.AtPos = B(Viewer.Motion.AgentControlFlags.AtPos);
        mv.AtNeg = B(Viewer.Motion.AgentControlFlags.AtNeg);
        mv.LeftPos = B(Viewer.Motion.AgentControlFlags.LeftPos);
        mv.LeftNeg = B(Viewer.Motion.AgentControlFlags.LeftNeg);
        mv.UpPos = B(Viewer.Motion.AgentControlFlags.UpPos);
        mv.UpNeg = B(Viewer.Motion.AgentControlFlags.UpNeg);
        mv.YawPos = B(Viewer.Motion.AgentControlFlags.YawPos);
        mv.YawNeg = B(Viewer.Motion.AgentControlFlags.YawNeg);
        mv.FastAt = B(Viewer.Motion.AgentControlFlags.FastAt);
        mv.FastLeft = B(Viewer.Motion.AgentControlFlags.FastLeft);
        mv.FastUp = B(Viewer.Motion.AgentControlFlags.FastUp);
        mv.Fly = B(Viewer.Motion.AgentControlFlags.Fly);
        mv.Stop = B(Viewer.Motion.AgentControlFlags.Stop);
        mv.FinishAnim = B(Viewer.Motion.AgentControlFlags.FinishAnim);
        mv.NudgeAtPos = B(Viewer.Motion.AgentControlFlags.NudgeAtPos);
        mv.NudgeAtNeg = B(Viewer.Motion.AgentControlFlags.NudgeAtNeg);
        mv.NudgeLeftPos = B(Viewer.Motion.AgentControlFlags.NudgeLeftPos);
        mv.NudgeLeftNeg = B(Viewer.Motion.AgentControlFlags.NudgeLeftNeg);
        mv.Mouselook = B(Viewer.Motion.AgentControlFlags.Mouselook);
        mv.LButtonDown = B(Viewer.Motion.AgentControlFlags.LButtonDown);
        mv.LButtonUp = B(Viewer.Motion.AgentControlFlags.LButtonUp);
        mv.MLButtonDown = B(Viewer.Motion.AgentControlFlags.MlLButtonDown);
        mv.MLButtonUp = B(Viewer.Motion.AgentControlFlags.MlLButtonUp);
    }

    internal static LoginParams BuildLoginParams(GridClient client, TransportLoginRequest r)
    {
        // Honest identity (ADR-019): our channel and version, and our user agent as the HTTP user agent and author.
        var lp = client.Network.DefaultLoginParams(r.FirstName, r.LastName, r.Password.Reveal(), r.Identity.Channel, r.Identity.Version);
        lp.URI = r.LoginUri;
        lp.Start = r.StartLocation;
        lp.UserAgent = r.Identity.UserAgent;
        lp.Author = r.Identity.UserAgent;
        lp.Timeout = 60_000;
        // as the official viewer's first request: nothing agreed yet, an empty token and multifactor hash (always sent), the answers
        // of earlier dialogs when this is a retry (lllogininstance.cpp, constructAuthParams and handleTOSResponse)
        var extras = r.Extras ?? new LoginExtras();
        lp.AgreeToTos = extras.AgreeToTos;
        lp.ReadCritical = extras.ReadCritical;
        lp.MfaEnabled = true;
        lp.Token = extras.Token?.Reveal() ?? "";
        lp.MfaHash = extras.MfaHash?.Reveal() ?? "";
        return lp;
    }

    public async Task<TransportLoginResult> LoginAsync(TransportLoginRequest request, CancellationToken ct)
    {
        ApplyNeighbours(_client, request.Neighbours);
        TakeOverCrossing(request.Neighbours);
        string loginHost = Uri.TryCreate(request.LoginUri, UriKind.Absolute, out var loginUri) ? loginUri.Host : "";
        _certificates.Clear(loginHost);
        var lp = BuildLoginParams(_client, request);
        _loginTap.Begin();
        bool ok = await _client.Network.LoginAsync(lp, ct).ConfigureAwait(false);
        var reply = _loginTap.Reply;
        if (!ok)
        {
            // a refused certificate is the reason, whatever generic text the library gives for a connection that did not open
            if (_certificates.Latest(loginHost) is { } refused)
                return TransportLoginResult.Failed("certificate", refused.Message) with { Certificate = refused };
            var key = _client.Network.LoginErrorKey;
            return TransportLoginResult.Failed(string.IsNullOrEmpty(key) ? "failed" : key, _client.Network.LoginMessage) with { Reply = reply };
        }
        AttachInventory();
        var d = _client.Network.LoginResponseData!;
        return new TransportLoginResult(true, null, null, d.AgentID.Guid,
            new Secret(d.SessionID.ToString(), SensitiveKind.SessionId), (int)d.RegionSizeX, (int)d.RegionSizeY) { Reply = reply };
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        _environment.CancelAll();   // nothing of this session's environment fetch outlives the logout
        _parcelEnvironment.CancelAll();
        _materials.CancelAll();     // nor any legacy material request
        CancelTextureFetches();     // nor any texture range request
        if (!_client.Network.Connected)
            return;
        await _client.Network.LogoutAsync(ct).ConfigureAwait(false);
    }

    private readonly SimRefMap<Simulator> _simRefs = new();

    private SimRef Map(Simulator sim) => _simRefs.Get(sim, sim.Handle, sim.Name ?? "");

    /// <summary>A neighbour made from an agent-communication message has no region handle until its handshake: the library creates it with
    /// handle 0. The session placed it from that handle (at the corner of the world, with an empty world) and every later event carried a
    /// new reference, so the neighbour's real data was dropped. So the placeholder is withdrawn and the neighbour announced again with
    /// its handle. The agent's own region is never touched here.</summary>
    private void OnSimRefReplaced(Simulator sim, SimRef old, SimRef now)
    {
        if (_disposed || ReferenceEquals(sim, _client.Network.CurrentSim))
            return;
        _log.Info("circuit.placed", CoreLog.F("was", $"{old.GridX},{old.GridY}"), CoreLog.F("now", $"{now.GridX},{now.GridY}"));
        Raise(new Session.SimDisconnected(old));
        if (now.Handle != 0)
            Announce(sim);
    }

    private SimRef? MapOrNull(Simulator? sim) => sim is null ? null : Map(sim);

    /// <summary>The library's simulator for a reference this adapter handed out, or null when it is not one of ours.</summary>
    private Simulator? SimulatorOf(SimRef? sim)
    {
        return _simRefs.Find(sim);
    }

    private void Raise(TransportEvent e)
    {
        if (_disposed)
            return;
        Event?.Invoke(e);
    }

    // ---------------- LibreMetaverse events (network threads) ----------------

    private void OnLoginProgress(object? sender, LoginProgressEventArgs e)
    {
        Raise(new LoginProgressed(
            e.Status switch { LoginStatus.ConnectingToSim => LoginPhase.ConnectingToSim, LoginStatus.Failed => LoginPhase.Failed, _ => LoginPhase.Other },
            e.Message));
    }

    private void OnSimConnecting(object? sender, SimConnectingEventArgs e)
    {
        if (_outbound.Strict && !_outbound.AllowsSimulator(e.Simulator?.IPEndPoint))
        {
            _log.Warn("outbound.simulator-refused", CoreLog.F("reason", "internal address denied by strict policy"));
            e.Cancel = true;
            return;
        }
        if (e.Simulator is { IPEndPoint: { } held } && _heldBack.Refuses(held, _client.Network.CurrentSim is not null))
        {
            _log.Info("circuit.held-back-refused");   // a development switch: the crossing's own connect makes this one
            e.Cancel = true;
            return;
        }
        if (e.Simulator is { } connecting && DuplicateCircuit(connecting))
        {
            _log.Warn("circuit.duplicate-refused", CoreLog.F("handle", connecting.Handle));   // the earlier one in the list is the circuit
            e.Cancel = true;
        }
    }

    /// <summary>Whether another simulator for the same address is earlier in the library's list (the later of two is refused).</summary>
    private bool DuplicateCircuit(Simulator sim)
    {
        var sims = _client.Network.Simulators;
        System.Net.IPEndPoint[] listed;
        int index;
        lock (sims)
        {
            listed = sims.Select(s => s.IPEndPoint).ToArray();
            index = sims.IndexOf(sim);
        }
        return index >= 0 && CircuitDedupe.Refuses(listed, index);
    }

    /// <summary>The region handshake's terrain texturing settings, as LibreMetaverse keeps them on the simulator.</summary>
    internal static Viewer.Core.World.RegionTerrain TerrainOf(Simulator sim) => new(
        [sim.TerrainDetail0.Guid, sim.TerrainDetail1.Guid, sim.TerrainDetail2.Guid, sim.TerrainDetail3.Guid],
        [sim.TerrainStartHeight00, sim.TerrainStartHeight01, sim.TerrainStartHeight10, sim.TerrainStartHeight11],
        [sim.TerrainHeightRange00, sim.TerrainHeightRange01, sim.TerrainHeightRange10, sim.TerrainHeightRange11]);

    private void OnSimConnected(object? sender, SimConnectedEventArgs e)
    {
        SendThrottle(e.Simulator);
        if (e.Simulator.Handle == 0 && !IsAgentsRegion(e.Simulator))
        {
            _log.Info("circuit.unplaced");   // no handle yet: announced when the handshake gives it one (OnSimRefReplaced)
            return;
        }
        Announce(e.Simulator);
    }

    /// <summary>The library switched its current simulator. A teleport to a simulator it still holds raises no connect event, so the
    /// arrival is reported here, once, as the same record a first connect gives.</summary>
    private void OnSimChanged(object? sender, SimChangedEventArgs e)
    {
        if (_client.Network.CurrentSim is { } now && !ReferenceEquals(now, _promoting) && _arrivals.IsReArrival(now))
            Announce(now, reArrival: true);   // (a crossing this adapter takes over announces itself)
    }

    private void OnSimDisconnected(object? sender, SimDisconnectedEventArgs e)
    {
        _arrivals.Forget(e.Simulator);
        Raise(new Session.SimDisconnected(Map(e.Simulator)));   // the session drops a neighbour's world; the current region's end is the Disconnected event
    }

    /// <summary>Whether <paramref name="sim"/> is the simulator the library considers the agent's own (or none is set yet).</summary>
    private bool IsAgentsRegion(Simulator sim) => _client.Network.CurrentSim is not { } now || ReferenceEquals(now, sim);

    /// <summary>The agent is in <paramref name="sim"/>'s region: the connect record, and the map block that gives its authoritative size.</summary>
    private void Announce(Simulator sim, bool reArrival = false)
    {
        _arrivals.Announced(sim);
        // a different region the agent has arrived in: the previous region's environment fetch is cancelled and its answer dropped.
        // A neighbour's circuit coming up (the library's current simulator is another one) leaves the agent's region alone.
        if (_environment.CurrentRegion is { } fetching && !ReferenceEquals(fetching, sim) && IsAgentsRegion(sim))
        {
            _environment.CancelRegion();
            _parcelEnvironment.CancelRegion();   // and the parcel's
            _materials.CancelRegion();   // and its legacy material requests
            lock (_pbr) _pbr.Clear();   // and what its objects' render material parameters were
            CancelTextureFetches();   // the previous region's texture capability is gone
        }
        Raise(new SimConnected(Map(sim), (int)sim.SizeX, (int)sim.SizeY, sim.Protocols.HasFlag(RegionProtocols.AgentAppearanceService), TerrainOf(sim), sim.WaterHeight, sim.IsEstateManager));
        if (IsAgentsRegion(sim))
            ClientBakeRegion(sim);   // a region that does not bake on the server needs this viewer's own bakes
        // The map block carries the authoritative var-region size (asked again by the session while it has not come).
        RequestMapBlock(Map(sim));
        // A circuit that was a neighbour's has its event queue up already, so its environment was not fetched then: the crossing does it.
        if (reArrival && sim.Caps?.IsEventQueueRunning == true)
            StartEnvironment(sim);
    }

    /// <summary>Asks the grid for the map block of the region's own cell (one UDP request).</summary>
    public void RequestMapBlock(SimRef sim)
    {
        if (_disposed)
            return;
        _client.Grid.RequestMapBlocks(GridLayerType.Objects, (ushort)sim.GridX, (ushort)sim.GridY, (ushort)sim.GridX, (ushort)sim.GridY, true);
    }

    private void OnEventQueueRunning(object? sender, EventQueueRunningEventArgs e)
    {
        Raise(new EventQueueRunning(Map(e.Simulator)));
        if (!IsAgentsRegion(e.Simulator))
            return;   // the environment is the agent's region's; a neighbour's is not fetched
        StartEnvironment(e.Simulator);
    }

    /// <summary>The environment of the parcel the agent has come into: the region's capability asked with the parcel's local id, as the official
    /// viewer does on every change of the agent's parcel. A region without the capability has none to ask.</summary>
    private void StartParcelEnvironment(Simulator sim, int parcelId)
    {
        Uri? uri;
        try { uri = sim.Caps?.CapabilityURI("ExtEnvironment"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { uri = null; }
        if (uri is null) return;
        _ = _parcelEnvironment.Start(new ParcelKey(sim, parcelId), EnvironmentFetcher<ParcelKey>.ParcelUri(uri, parcelId),
            (u, ct) => BoundedHttp.GetBoundedAsync(_client.HttpCapsClient, u, EnvironmentFetcher<ParcelKey>.CapBytes, ct));
    }

    private void StartEnvironment(Simulator sim)
    {
        // the region's environment (EEP) from its ExtEnvironment capability, once its capabilities are known, through
        // the session's HTTP client (the outbound policy applies in strict mode); a region without it is reported, not
        // guessed. Bounded before it is held and cancelled with the session or the region.
        Uri? uri;
        try { uri = sim.Caps?.CapabilityURI("ExtEnvironment"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { uri = null; }
        _ = _environment.Start(sim, uri,
            (u, ct) => BoundedHttp.GetBoundedAsync(_client.HttpCapsClient, u, EnvironmentFetcher<Simulator>.CapBytes, ct));
    }

    private void OnAgentMovementComplete(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is AgentMovementCompletePacket)
        {
            // the region starts with its own default: tell it whether the avatar runs
            if (!_disposed && e.Simulator is { } sim)
                _client.Network.SendPacket(BuildSetAlwaysRun(_client.Self.AgentID, _client.Self.SessionID, _walkRun), sim);
            Raise(new MovementComplete(Map(e.Simulator)));
        }
    }

    /// <summary>The library's disconnect as the core's record: its kind, and the grid's text for a server-initiated end.</summary>
    internal static Session.Disconnected ToDisconnected(NetworkManager.DisconnectType reason, string? message) => new(reason.ToString(),
        reason switch
        {
            NetworkManager.DisconnectType.ClientInitiated => DisconnectKind.ClientInitiated,
            NetworkManager.DisconnectType.NetworkTimeout => DisconnectKind.NetworkTimeout,
            NetworkManager.DisconnectType.ServerInitiated => DisconnectKind.ServerInitiated,
            NetworkManager.DisconnectType.SimShutdown => DisconnectKind.SimShutdown,
            _ => DisconnectKind.Other,
        },
        reason == NetworkManager.DisconnectType.ServerInitiated ? message ?? "" : "");

    private void OnDisconnected(object? sender, DisconnectedEventArgs e)
    {
        _environment.CancelAll();   // no environment fetch outlives the session
        _parcelEnvironment.CancelAll();
        _materials.CancelAll();     // nor any legacy material request
        CancelTextureFetches();     // nor any texture range request
        Raise(ToDisconnected(e.Reason, e.Message));
    }

    /// <summary>A tree object (PCode Tree or NewTree) as the core's tree data; null for any other object.</summary>
    internal static Viewer.Core.World.TreeData? TreeFrom(Primitive prim) =>
        prim.PrimData.PCode is PCode.Tree or PCode.NewTree
            ? new Viewer.Core.World.TreeData(prim.LocalID, prim.ID.Guid, (int)prim.TreeSpecies,
                new System.Numerics.Vector3(prim.Position.X, prim.Position.Y, prim.Position.Z),
                new System.Numerics.Quaternion(prim.Rotation.X, prim.Rotation.Y, prim.Rotation.Z, prim.Rotation.W),
                new System.Numerics.Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z))
            : null;

    /// <summary>Every object update states its region's time dilation as a 16-bit fraction (65535 = real time); a change is passed on.</summary>
    private void NoteTimeDilation(Simulator? sim, ushort dilation)
    {
        if (sim is null) return;
        lock (_timeDilations)
        {
            if (_timeDilations.TryGetValue(sim.Handle, out ushort last) && last == dilation) return;
            _timeDilations[sim.Handle] = dilation;
        }
        Raise(new RegionTimeDilation(MapOrNull(sim), dilation / 65535f));
    }

    private readonly Dictionary<ulong, ushort> _timeDilations = new();

    private void OnObjectUpdate(object? sender, PrimEventArgs e)
    {
        var prim = e.Prim;
        NoteTimeDilation(e.Simulator, e.TimeDilation);
        if (prim is not null && TreeFrom(prim) is { } tree)
        {
            Raise(new TreeUpdated(MapOrNull(e.Simulator), tree));
            return;
        }
        if (prim?.PrimData.PCode != PCode.Prim)
            return;   // trees, grass, particle systems: later sessions
        Avatar? parent = null;
        if (prim.ParentID != 0)
            e.Simulator?.ObjectsAvatars.TryGetValue(prim.ParentID, out parent);
        var data = ToPrimData(prim);
        Raise(new ObjectUpdated(MapOrNull(e.Simulator), data, e.IsAttachment, parent is null ? null : ToAvatarData(parent)));
        if (ToObjectSound(MapOrNull(e.Simulator), prim) is { } sound)
            Raise(sound);   // the object's own sound as its update states it
        // the legacy materials its faces name, asked of the region (each id once per region)
        if (data.Materials is { } fm && e.Simulator is { } sim)
            _materials.Want(sim, fm.All.Where(f => f is { } x && x.MaterialId != Guid.Empty).Select(f => f!.Value.MaterialId));
    }

    /// <summary>The region's GLTF material overrides for one object (a GenericStreamingMessage); other methods are ignored.</summary>
    private void OnGenericStreamingMessage(object? sender, GenericStreamingMessageEventArgs e)
    {
        if (e.Method != GenericStreamingMethod.GltfMaterialOverride || e.Data is not { Length: > 0 } data)
            return;
        var faces = LmvMaterials.ParseOverrideMessage(System.Text.Encoding.UTF8.GetString(data), out uint localId);
        if (faces is null)
        {
            _log.Warn("material.override-malformed", CoreLog.F("bytes", data.Length));
            return;
        }
        Raise(new MaterialOverridesReceived(MapOrNull(e.Simulator), localId, faces));
    }

    /// <summary>One RenderMaterials request through the library (it posts the ids and unpacks the zipped answer).</summary>
    private async Task<IReadOnlyList<Viewer.Core.World.LegacyMaterial>> FetchLegacyMaterialsAsync(Simulator sim, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var answer = await _client.Objects.RequestMaterialsAsync(sim, ids.Select(i => new UUID(i)), ct).ConfigureAwait(false);
        return answer?.Select(LmvMaterials.ToLegacyMaterial).ToList() ?? [];
    }

    private void OnAvatarUpdate(object? sender, AvatarUpdateEventArgs e)
    {
        if (e.Avatar is { } a)
            Raise(new AvatarUpdated(MapOrNull(e.Simulator), ToAvatarData(a)));
    }

    private void OnTerseObjectUpdate(object? sender, TerseObjectUpdateEventArgs e)
    {
        // W-30: the arrival stamp is taken HERE, first, on the packet thread: the sample's age is measured from the
        // instant it arrived, and the movement flags are the ones in force at that instant.
        double arrivalMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        var u = e.Update;
        NoteTimeDilation(e.Simulator, e.TimeDilation);
        if (u.Avatar && u.LocalID == _client.Self.LocalID && u.LocalID != 0)
            Raise(new SelfMoved(MapOrNull(e.Simulator), ToSelfMotion(u, arrivalMs, MovementOf(_client.Self.Movement))));
        Raise(new ObjectMoved(MapOrNull(e.Simulator), u.LocalID, u.Avatar, V(u.Position), Q(u.Rotation), V(u.Velocity), V(u.Acceleration), V(u.AngularVelocity), u.Avatar ? Plane(u.CollisionPlane) : default));
        // a change of textures alone can come as a terse update carrying the texture entry: the object's faces follow it
        if (e.Prim is { } prim && TerseWithTextures(prim, u) is { } data)
        {
            Avatar? parent = null;
            if (prim.ParentID != 0)
                e.Simulator?.ObjectsAvatars.TryGetValue(prim.ParentID, out parent);
            Raise(new ObjectUpdated(MapOrNull(e.Simulator), data, prim.IsAttachment, parent is null ? null : ToAvatarData(parent)));
        }
    }

    /// <summary>The foot collision plane an avatar's update carries (normal in x, y, z and the plane's offset in w); all zero when the region sent none.</summary>
    internal static N.Vector4 Plane(LibreMetaverse.Vector4 p) => new(p.X, p.Y, p.Z, p.W);

    private void OnAvatarAppearance(object? sender, AvatarAppearanceEventArgs e)
    {
        var data = ToAppearanceData(e);
        _bakeRoutes.Note(data);   // before it is raised: the bakes it names may be asked for at once
        Raise(new AvatarAppearanceReceived(MapOrNull(e.Simulator), data));
    }

    /// <summary>An AvatarAnimation: the avatar and every animation it now plays, read from the packet with its simulator.</summary>
    /// <summary>A CameraConstraint message's plane (the official viewer's process_camera_constraint, llviewermessage.cpp).</summary>
    internal static CameraConstraintReceived ToCameraConstraint(SimRef? sim, CameraConstraintPacket p) =>
        new(sim, new N.Vector4(p.CameraCollidePlane.Plane.X, p.CameraCollidePlane.Plane.Y, p.CameraCollidePlane.Plane.Z, p.CameraCollidePlane.Plane.W));

    /// <summary>A GrantGodlikePowers message's level (the official viewer's process_grant_godlike_powers, llviewermessage.cpp): above zero the agent is godlike.</summary>
    internal static GodLevelReceived ToGodLevel(GrantGodlikePowersPacket p) => new(p.GrantData.GodLevel);

    private void OnGrantGodlikePowersPacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is GrantGodlikePowersPacket p)
            Raise(ToGodLevel(p));
    }

    // A region's wind layer: two 16 x 16 patches (x, then y velocity), coded as terrain patches are; decoded with the library's
    // terrain patch decoder, as its own wind handler does. Only the 256 m region form (layer type Wind) is read.
    private void OnLayerDataPacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not LayerDataPacket p || p.LayerID.Type != (byte)TerrainPatch.LayerType.Wind)
            return;
        try
        {
            var bits = new BitPack(p.LayerData.Data, 0);
            var group = new TerrainPatch.GroupHeader { Stride = bits.UnpackBits(16), PatchSize = bits.UnpackBits(8), Type = (TerrainPatch.LayerType)bits.UnpackBits(8) };
            group.Stride = group.PatchSize;
            float[] Next()
            {
                var header = TerrainCompressor.DecodePatchHeader(bits);
                var patch = new int[32 * 32];
                TerrainCompressor.DecodePatch(patch, bits, header, group.PatchSize);
                return TerrainCompressor.DecompressPatch(patch, header, group);
            }
            float[] x = Next(), y = Next();
            if (x.Length >= 256 && y.Length >= 256)
                Raise(new RegionWindReceived(MapOrNull(e.Simulator), x[..256], y[..256]));
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            // a malformed wind layer leaves the last wind in place
        }
    }

    private void OnCameraConstraintPacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is CameraConstraintPacket p)
            Raise(ToCameraConstraint(MapOrNull(e.Simulator), p));
    }

    /// <summary>A ScriptControlChange message's blocks (the official viewer's processScriptControlChange, llagent.cpp).</summary>
    internal static ScriptControlChanged ToScriptControlChange(SimRef? sim, ScriptControlChangePacket p) =>
        new(sim, (p.Data ?? []).Select(d => new ScriptControlEntry(d.TakeControls, d.Controls, d.PassToAgent)).ToArray());

    private void OnScriptControlChangePacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is ScriptControlChangePacket p)
            Raise(ToScriptControlChange(MapOrNull(e.Simulator), p));
    }

    private void OnAvatarAnimationPacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is AvatarAnimationPacket p)
            Raise(ToAnimations(MapOrNull(e.Simulator), p));
    }

    /// <summary>An AvatarAppearance message's AppearanceHover block (llvoavatar.cpp:9536-9544): the avatar and the block's height, or null when
    /// the message has no such block. The library's own appearance event carries no such field.</summary>
    internal static AvatarHoverReceived ToHover(SimRef? sim, AvatarAppearancePacket p) =>
        new(sim, p.Sender.ID.Guid, p.AppearanceHover is { Length: > 0 } h ? h[0].HoverHeight.Z : null);

    private void OnAvatarAppearancePacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is AvatarAppearancePacket p)
            Raise(ToHover(MapOrNull(e.Simulator), p));
    }

    // ---------------- sounds in the world ----------------

    private void OnSoundPacket(object? sender, PacketReceivedEventArgs e)
    {
        var sim = MapOrNull(e.Simulator);
        switch (e.Packet)
        {
            case SoundTriggerPacket t when ToSoundTrigger(sim, t) is { } trigger:
                Raise(trigger);
                break;
            case AttachedSoundPacket a when ToAttachedSound(sim, a) is { } attached:
                Raise(attached);
                break;
            case AttachedSoundGainChangePacket g when ToAttachedSoundGain(sim, g) is { } gain:
                Raise(gain);
                break;
            case PreloadSoundPacket pre:
                foreach (var one in ToPreloads(sim, pre))
                    Raise(one);
                break;
            case ParcelOverlayPacket o when ToParcelOverlay(sim, o) is { } overlay:
                Raise(overlay);
                break;
        }
    }

    /// <summary>A SoundTrigger message: a one-shot at a point local to the region the message names.</summary>
    internal static SoundTriggered? ToSoundTrigger(SimRef? sim, SoundTriggerPacket p) => p.SoundData is { } d
        ? new(sim, d.SoundID.Guid, d.OwnerID.Guid, d.ObjectID.Guid, d.ParentID.Guid, d.Handle, V(d.Position), d.Gain) : null;

    /// <summary>An AttachedSound message: an object's own sound and its flags.</summary>
    internal static AttachedSoundReceived? ToAttachedSound(SimRef? sim, AttachedSoundPacket p) => p.DataBlock is { } d
        ? new(sim, d.ObjectID.Guid, d.SoundID.Guid, d.OwnerID.Guid, d.Gain, d.Flags) : null;

    /// <summary>An AttachedSoundGainChange message.</summary>
    internal static AttachedSoundGainChanged? ToAttachedSoundGain(SimRef? sim, AttachedSoundGainChangePacket p) => p.DataBlock is { } d
        ? new(sim, d.ObjectID.Guid, d.Gain) : null;

    /// <summary>A PreloadSound message: each of its blocks names a sound an object will play.</summary>
    internal static IReadOnlyList<SoundPreloaded> ToPreloads(SimRef? sim, PreloadSoundPacket p) =>
        (p.DataBlock ?? []).Select(d => new SoundPreloaded(sim, d.ObjectID.Guid, d.SoundID.Guid, d.OwnerID.Guid)).ToArray();

    /// <summary>A ParcelOverlay message: one quarter of the region's parcel squares.</summary>
    internal static ParcelOverlayReceived? ToParcelOverlay(SimRef? sim, ParcelOverlayPacket p) => p.ParcelData is { Data: { } data } d
        ? new(sim, d.SequenceID, (byte[])data.Clone()) : null;

    /// <summary>The sound fields of an object update, as an object's own sound (null when the object has no sound and none to clear).</summary>
    internal static AttachedSoundReceived? ToObjectSound(SimRef? sim, Primitive p) => p.Sound == UUID.Zero
        ? null : new(sim, p.ID.Guid, p.Sound.Guid, p.OwnerID.Guid, p.SoundGain, (byte)p.SoundFlags, p.SoundRadius, FromObjectUpdate: true);

    internal static AvatarAnimationsReceived ToAnimations(SimRef? sim, AvatarAnimationPacket p) =>
        new(sim, p.Sender.ID.Guid, (p.AnimationList ?? []).Select(a => new Viewer.Core.World.PlayingAnimation(a.AnimID.Guid, a.AnimSequenceID)).ToArray());

    private void OnKillObject(object? sender, KillObjectEventArgs e)
    {
        ForgetPbr(e.Simulator, [e.ObjectLocalID]);
        Raise(new ObjectsKilled(MapOrNull(e.Simulator), [e.ObjectLocalID]));
    }

    private void OnKillObjects(object? sender, KillObjectsEventArgs e)
    {
        ForgetPbr(e.Simulator, e.ObjectLocalIDs);
        Raise(new ObjectsKilled(MapOrNull(e.Simulator), e.ObjectLocalIDs.ToArray()));
    }

    // ---------------- render material parameters (LibreMetaverse 3.1.6 does not read them) ----------------

    internal void OnObjectUpdatePacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdatePacket p || e.Simulator is not { } sim)
            return;
        foreach (var b in p.ObjectData)
            if (b.PCode == (byte)PCode.Prim)
                PbrSeen(sim, b.ID, b.ExtraParams is { Length: > 0 } x ? ExtraParams.RenderMaterials(x, 0) : null);
    }

    internal void OnObjectUpdateCompressedPacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdateCompressedPacket p || e.Simulator is not { } sim)
            return;
        foreach (var b in p.ObjectData)
            if (b.Data is { } d && ExtraParams.CompressedLayout(d, out uint local, out byte pcode, out int at) && pcode == (byte)PCode.Prim)
                PbrSeen(sim, local, ExtraParams.RenderMaterials(d, at));
    }

    /// <summary>Raises an object's render material entries when they differ from the last ones raised for it.</summary>
    internal void PbrSeen(Simulator sim, uint localId, IReadOnlyList<Viewer.Core.World.RenderMaterialEntry>? entries)
    {
        lock (_pbr)
        {
            if (!_pbr.TryGetValue(sim, out var bySim))
                _pbr[sim] = bySim = new();
            bool had = bySim.TryGetValue(localId, out var last);
            if (entries is null ? !had : had && last!.SequenceEqual(entries))
                return;
            if (entries is null) bySim.Remove(localId); else bySim[localId] = entries;
        }
        Raise(new RenderMaterialsReceived(Map(sim), localId, entries));
    }

    private void ForgetPbr(Simulator? sim, IEnumerable<uint> ids)
    {
        if (sim is null)
            return;
        lock (_pbr)
            if (_pbr.TryGetValue(sim, out var bySim))
                foreach (var id in ids)
                    bySim.Remove(id);
    }

    private readonly GroundHeights _ground = new();

    private void OnLandPatchReceived(object? sender, LandPatchReceivedEventArgs e)
    {
        if (e.Simulator is not null)
            _ground.Put(e.Simulator, e.X, e.Y, e.PatchSize, e.HeightMap);
        Raise(new LandPatch(MapOrNull(e.Simulator), e.X, e.Y, e.PatchSize, e.HeightMap));
    }

    // ---------------- LibreMetaverse -> core values (the only place these conversions live) ----------------

    /// <summary>A prim's data with the texture entry and place a terse update carries, or null when the update carries no texture entry.</summary>
    internal static PrimData? TerseWithTextures(Primitive prim, ObjectMovementUpdate u)
    {
        if (u.Avatar || u.Textures is null || prim.PrimData.PCode != PCode.Prim)
            return null;
        return ToPrimData(prim) with
        {
            Position = V(u.Position), Rotation = Q(u.Rotation), Velocity = V(u.Velocity), AngularVelocity = V(u.AngularVelocity), Acceleration = V(u.Acceleration),
            Colors = ToColors(u.Textures), Textures = ToTextures(u.Textures), Materials = LmvMaterials.ToMaterials(u.Textures),
        };
    }

    internal static PrimData ToPrimData(Primitive p) => new(
        p.LocalID, p.ID.Guid, p.ParentID, (int)p.PrimData.AttachmentPoint, V(p.Position), Q(p.Rotation), V(p.Scale), V(p.Velocity), V(p.AngularVelocity),
        V(p.Acceleration), ToTextureAnim(p.TextureAnim), ToShape(p), ToColors(p.Textures), ToTextures(p.Textures), LmvMaterials.ToMaterials(p.Textures), ToLight(p), ToProjector(p), ToInteraction(p), AttachItemOf(p), (int)p.PrimData.Material, ToFlexible(p));

    /// <summary>The inventory item a worn object was taken from: the object states it in its "AttachItemID" name-value pair (official viewer:
    /// llviewerobject.cpp, getAttachmentItemID). Empty when the object does not say.</summary>
    internal static Guid AttachItemOf(Primitive p)
    {
        if (p.NameValues is { } pairs)
            foreach (var pair in pairs)
                if (pair.Name == "AttachItemID" && Guid.TryParse(pair.Value?.ToString(), out var id))
                    return id;
        return Guid.Empty;
    }

    /// <summary>The prim's click action, object flags and floating text. Null when it has none of them (the usual prim: touch, no flags, no text).</summary>
    internal static PrimInteraction? ToInteraction(Primitive p)
    {
        const uint Interesting = Viewer.Core.Interaction.ObjectFlagBits.Physics | Viewer.Core.Interaction.ObjectFlagBits.CreateSelected | Viewer.Core.Interaction.ObjectFlagBits.Modify | Viewer.Core.Interaction.ObjectFlagBits.Copy
            | Viewer.Core.Interaction.ObjectFlagBits.YouOwner | Viewer.Core.Interaction.ObjectFlagBits.Scripted | Viewer.Core.Interaction.ObjectFlagBits.HandleTouch
            | Viewer.Core.Interaction.ObjectFlagBits.TakesMoney | Viewer.Core.Interaction.ObjectFlagBits.InventoryEmpty
            | Viewer.Core.Interaction.ObjectFlagBits.Move | Viewer.Core.Interaction.ObjectFlagBits.Transfer;
        uint flags = (uint)p.Flags & Interesting;
        bool hasText = !string.IsNullOrEmpty(p.Text);
        // An empty inventory alone is the usual case and says nothing about clicking: such a prim carries no block.
        if ((byte)p.ClickAction == 0 && !hasText && (flags & ~Viewer.Core.Interaction.ObjectFlagBits.InventoryEmpty) == 0)
            return null;
        static byte B(float v) => (byte)Math.Clamp((int)Math.Round(v * 255f, MidpointRounding.ToEven), 0, 255);
        uint colour = hasText ? Viewer.Core.Interaction.HoverTextRules.Pack(B(p.TextColor.R), B(p.TextColor.G), B(p.TextColor.B), B(p.TextColor.A)) : 0;
        return new PrimInteraction((byte)p.ClickAction, flags, hasText ? p.Text : null, colour);
    }

    /// <summary>The prim's light block (colour and intensity, radius, falloff, cutoff); null when it has none.</summary>
    internal static PrimLight? ToLight(Primitive p) =>
        p.Light is { Radius: > 0f, Intensity: > 0f } l ? new PrimLight(new System.Numerics.Vector3(l.Color.R, l.Color.G, l.Color.B), l.Intensity, l.Radius, l.Falloff, l.Cutoff) : null;

    /// <summary>The prim's flexible settings (its softness and forces) when it is flexible: its path is the flexible path, as the official
    /// viewer's isFlexible reads it (the library always holds a flexible block, filled or not); null otherwise.</summary>
    internal static Viewer.Core.Building.FlexibleParams? ToFlexible(Primitive p) =>
        p.PrimData.PathCurve == PathCurve.Flexible && p.Flexible is { } f ? new Viewer.Core.Building.FlexibleParams(f.Softness, f.Gravity, f.Drag, f.Wind, f.Tension, new System.Numerics.Vector3(f.Force.X, f.Force.Y, f.Force.Z)) : null;

    /// <summary>The prim's projector (light image block), only on a prim that also has a light and whose image names a texture.</summary>
    internal static PrimProjector? ToProjector(Primitive p) =>
        p.Light is { Radius: > 0f, Intensity: > 0f } && p.LightMap is { } m && m.LightTexture != UUID.Zero ? new PrimProjector(m.LightTexture.Guid, m.Params.X, m.Params.Y, m.Params.Z) : null;

    /// <summary>Test hook: the shape LibreMetaverse's sculpt block (16-byte map id + type byte) becomes.</summary>
    internal static Viewer.Core.Meshing.PrimShape ShapeFromSculptBlock(byte[] block) =>
        ToShape(new Primitive { Sculpt = new Primitive.SculptData(block, 0), PrimData = new Primitive.ConstructionData() });

    /// <summary>The construction data as the core's PrimShape (the web viewer's PrimShape fields, same ranges).</summary>
    internal static Viewer.Core.Meshing.PrimShape ToShape(Primitive p)
    {
        var d = p.PrimData;
        return new()
        {
            ProfileCurve = (int)d.ProfileCurve, ProfileHole = (int)d.ProfileHole, ProfileBegin = d.ProfileBegin, ProfileEnd = d.ProfileEnd,
            ProfileHollow = d.ProfileHollow, PathCurve = (int)d.PathCurve, PathBegin = d.PathBegin, PathEnd = d.PathEnd,
            PathScaleX = d.PathScaleX, PathScaleY = d.PathScaleY, PathShearX = d.PathShearX, PathShearY = d.PathShearY,
            PathTwist = d.PathTwist, PathTwistBegin = d.PathTwistBegin, PathRadiusOffset = d.PathRadiusOffset,
            PathTaperX = d.PathTaperX, PathTaperY = d.PathTaperY, PathRevolutions = d.PathRevolutions, PathSkew = d.PathSkew,
            SculptType = p.Sculpt is { } sc && sc.Type != SculptType.None ? (int)sc.Type : 0,
            SculptId = p.Sculpt is { } sm && sm.Type != SculptType.None ? sm.SculptTexture.Guid : Guid.Empty,
            SculptFlags = p.Sculpt is { } sf && sf.Type != SculptType.None
                ? (sf.Invert ? Viewer.Core.Meshing.SculptMesher.FlagInvert : 0) | (sf.Mirror ? Viewer.Core.Meshing.SculptMesher.FlagMirror : 0) : 0,
        };
    }

    /// <summary>The texture entry's per-face textures and their transforms (texgen, fullbright, glow).</summary>
    internal static FaceTextures? ToTextures(Primitive.TextureEntry? te)
    {
        if (te?.DefaultTexture is not { } def)
            return null;
        static FaceTexture T(Primitive.TextureEntryFace f) => new(f.TextureID.Guid, f.RepeatU, f.RepeatV, f.OffsetU, f.OffsetV, f.Rotation,
            f.TexMapType == MappingType.Planar ? TexGen.Planar : TexGen.Default, f.Fullbright, f.Glow);
        var faces = new FaceTexture?[te.FaceTextures.Length];
        int last = -1;
        for (int i = 0; i < faces.Length; i++)
            if (te.FaceTextures[i] is { } f) { faces[i] = T(f); last = i; }
        return new FaceTextures(T(def), faces.Take(last + 1));
    }

    /// <summary>The texture entry's legacy colours (default face + per-face overrides); textures are.</summary>
    internal static FaceColors? ToColors(Primitive.TextureEntry? te)
    {
        if (te?.DefaultTexture is not { } def)
            return null;
        static Rgba C(Color4 c) => new(c.R, c.G, c.B, c.A);
        var faces = new Rgba?[te.FaceTextures.Length];
        int last = -1;
        for (int i = 0; i < faces.Length; i++)
            if (te.FaceTextures[i] is { } f)
            {
                faces[i] = C(f.RGBA);
                last = i;
            }
        return new FaceColors(C(def.RGBA), faces.Take(last + 1));
    }

    internal static AvatarData ToAvatarData(Avatar a) => new(
        a.LocalID, a.ID.Guid, a.Name ?? "", V(a.Position), Q(a.Rotation), V(a.Scale), V(a.Velocity), a.ParentID, a.GroupName ?? "", Plane(a.CollisionPlane));

    internal static TextureAnim? ToTextureAnim(Primitive.TextureAnimation a) =>
        TextureAnim.FromWire(a.Face, (uint)a.Flags, a.SizeX, a.SizeY, a.Start, a.Length, a.Rate);

    internal static SelfMotion ToSelfMotion(ObjectMovementUpdate u, double arrivalMs, MovementFlags movement) =>
        new(V(u.Position), Q(u.Rotation), V(u.Velocity), V(u.AngularVelocity), V(u.Acceleration), arrivalMs, movement);

    /// <summary>What the agent was told to do at this instant (the gateway's MovementSnapshotOf).</summary>
    internal static MovementFlags MovementOf(AgentManager.AgentMovement mv) => new(mv.AtPos, mv.AtNeg, mv.AlwaysRun || mv.FastAt, mv.Fly);

    /// <summary>
    /// An AvatarAppearance as plain data: every face's texture id (unset and the library's default avatar texture become
    /// Guid.Empty), the visual-parameter bytes copied in wire order with that order attached, and the versions.
    /// </summary>
    internal AvatarAppearanceData ToAppearanceData(AvatarAppearanceEventArgs e) => ToAppearanceData(e, _wireOrder);

    internal static AvatarAppearanceData ToAppearanceData(AvatarAppearanceEventArgs e, ImmutableArray<int> wireOrder)
    {
        var faces = ImmutableArray.CreateBuilder<Guid>(e.FaceTextures?.Length ?? 0);
        if (e.FaceTextures is { } ft)
            foreach (var f in ft)
            {
                var id = f?.TextureID ?? UUID.Zero;
                faces.Add(id == UUID.Zero || id == AppearanceManager.DEFAULT_AVATAR_TEXTURE ? Guid.Empty : id.Guid);
            }
        var vp = e.VisualParams is { Count: > 0 } p ? new VisualParamSet(p, wireOrder) : VisualParamSet.Empty;
        return new AvatarAppearanceData(e.AvatarID.Guid, faces.MoveToImmutable(), vp, e.AppearanceVersion, e.COFVersion);
    }

    private static N.Vector3 V(Vector3 v) => new(v.X, v.Y, v.Z);

    private static N.Quaternion Q(Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    private static Vector3 L(N.Vector3 v) => new(v.X, v.Y, v.Z);

    private static Quaternion L(N.Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    private void OnMapBlockReply(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not MapBlockReplyPacket p)
            return;
        var blocks = ToMapBlocks(p);
        _regionNames.Offer(blocks);
        foreach (var block in blocks)
            Raise(block);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        try { _crossings.Cancel(); } catch (ObjectDisposedException) { }
        _environment.Dispose();   // cancels any environment fetch; nothing is delivered after this
        _parcelEnvironment.Dispose();
        _materials.Dispose();     // and any legacy material request
        _textureFetches.Cancel(); // and every texture range request
        _mapFetches.Cancel();     // and every map tile request
        StopClientBake();
        _client.Network.SimConnecting -= OnSimConnecting;
        _client.Network.LoginProgress -= OnLoginProgress;
        _client.Network.SimConnected -= OnSimConnected;
        _client.Network.SimChanged -= OnSimChanged;
        _client.Network.SimDisconnected -= OnSimDisconnected;
        _client.Network.UnregisterCallback(PacketType.TeleportFailed, OnTeleportFailedPacket);
        _client.Network.UnregisterEventCallback("TeleportFailed", OnTeleportFailedEvent);
        _client.Network.UnregisterEventCallback("EnableSimulator", OnEnableSimulator);
        _client.Network.UnregisterEventCallback("EstablishAgentCommunication", OnEstablishAgentCommunication);
        _arrivals.Clear();
        _client.Network.EventQueueRunning -= OnEventQueueRunning;
        _client.Network.Disconnected -= OnDisconnected;
        _client.Network.UnregisterCallback(PacketType.AgentMovementComplete, OnAgentMovementComplete);
        _client.Network.UnregisterCallback(PacketType.MapBlockReply, OnMapBlockReply);
        _client.Objects.ObjectUpdate -= OnObjectUpdate;
        _client.Objects.AvatarUpdate -= OnAvatarUpdate;
        _client.Objects.TerseObjectUpdate -= OnTerseObjectUpdate;
        _client.Avatars.AvatarAppearance -= OnAvatarAppearance;
        _client.Objects.KillObject -= OnKillObject;
        _client.Objects.KillObjects -= OnKillObjects;
        _client.Terrain.LandPatchReceived -= OnLandPatchReceived;
        _client.Network.GenericStreamingMessage -= OnGenericStreamingMessage;
        _client.Network.UnregisterCallback(PacketType.ObjectUpdate, OnObjectUpdatePacket);
        _client.Network.UnregisterCallback(PacketType.AvatarAnimation, OnAvatarAnimationPacket);
        _client.Network.UnregisterCallback(PacketType.AvatarAppearance, OnAvatarAppearancePacket);
        _client.Network.UnregisterCallback(PacketType.CameraConstraint, OnCameraConstraintPacket);
        _client.Network.UnregisterCallback(PacketType.LayerData, OnLayerDataPacket);
        _client.Network.UnregisterCallback(PacketType.GrantGodlikePowers, OnGrantGodlikePowersPacket);
        _client.Network.UnregisterCallback(PacketType.ScriptControlChange, OnScriptControlChangePacket);
        _client.Network.UnregisterCallback(PacketType.ObjectUpdateCompressed, OnObjectUpdateCompressedPacket);
        _client.Network.UnregisterCallback(PacketType.SoundTrigger, OnSoundPacket);
        _client.Network.UnregisterCallback(PacketType.AttachedSound, OnSoundPacket);
        _client.Network.UnregisterCallback(PacketType.AttachedSoundGainChange, OnSoundPacket);
        _client.Network.UnregisterCallback(PacketType.PreloadSound, OnSoundPacket);
        _client.Network.UnregisterCallback(PacketType.ParcelOverlay, OnSoundPacket);
        _client.Self.AvatarSitResponse -= OnAvatarSitResponse;
        _client.Objects.ObjectProperties -= OnObjectProperties;
        _client.Objects.PhysicsProperties -= OnPhysicsProperties;
        _client.Objects.PayPriceReply -= OnPayPriceReply;
        _client.Self.ChatFromSimulator -= OnChat;
        _client.Self.IM -= OnInstantMessage;
        _client.Self.AlertMessage -= OnAlertMessage;
        _client.Self.MoneyBalance -= OnMoneyBalance;
        _client.Self.ScriptDialog -= OnScriptDialog;
        _client.Self.ScriptQuestion -= OnScriptQuestion;
        _client.Self.LoadURL -= OnLoadUrl;
        _client.Network.UnregisterCallback(PacketType.ScriptTeleportRequest, OnScriptTeleportRequest);
        _client.Network.UnregisterCallback(PacketType.GenericMessage, OnGenericMessage);
        _client.Parcels.ParcelProperties -= OnParcelProperties;
        _client.Avatars.UUIDNameReply -= OnNames;
        _client.Friends.friendsListReady -= OnFriendsReady;
        _client.Friends.FriendOnline -= OnFriendOnline;
        _client.Friends.FriendOffline -= OnFriendOffline;
        _client.Friends.FriendRightsUpdate -= OnFriendRights;
        _client.Friends.FriendshipTerminated -= OnFriendshipTerminated;
        _client.Groups.CurrentGroups -= OnCurrentGroups;
        _client.Self.TeleportProgress -= OnTeleportProgress;
        _client.Grid.CoarseLocationUpdate -= OnCoarseLocations;
        _client.Friends.FriendFoundReply -= OnFriendFound;
        _client.Groups.GroupTitlesReply -= OnGroupTitles;
        _client.Self.AgentDataReply -= OnAgentData;
        _client.Self.GroupChatJoined -= OnGroupChatJoined;
        DetachInventory();
        _client.Self.MuteListUpdated -= OnMuteListUpdated;
        Event = null;
        _simRefs.Clear();
        lock (_pbr) _pbr.Clear();
        _client.Dispose();
        _textureHttp.Dispose();
        _mapHttp?.Dispose();
    }
}
