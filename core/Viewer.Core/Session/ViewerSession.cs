// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Security.Cryptography;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Identity;
using Viewer.Core.Net;
using Viewer.Core.World;

namespace Viewer.Core.Session;

/// <summary>Monotonic login stages (from the web gateway). The reported stage never goes backward.</summary>
public enum SessionStage
{
    Idle = 0,
    LoggingIn = 1,
    LoggedIn = 2,           // login reply accepted, connecting to the sim
    SimConnected = 3,
    EventQueueRunning = 4,
    MovementComplete = 5,
    InWorld = 6,            // both EventQueueRunning and MovementComplete seen, in either order
}

public enum SessionEndReason { None, LoginFailed, LoggedOut, Disconnected, GraceExpired, Disposed }

/// <param name="Extras">The fields that change between attempts (agreed terms, a multifactor token); null on a first attempt.</param>
public sealed record LoginCredentials(string FirstName, string LastName, Secret Password, LoginExtras? Extras = null);

public sealed record LoginOutcome(bool Ok, string? Code, string? Message)
{
    /// <summary>What the login service said (reason, message id and arguments, error code, multifactor hash), when the transport reported it.</summary>
    public LoginReply? Reply { get; init; }

    /// <summary>The server certificate that was refused, when that is why the login failed (code "certificate").</summary>
    public Viewer.Core.Net.CertificateRefusal? Certificate { get; init; }
}

/// <summary>Per-session settings. Defaults reproduce the gateway's where it has one (new settings keep the earlier behaviour).</summary>
public sealed record SessionOptions
{
    public required string LoginUri { get; init; }
    public string StartLocation { get; init; } = "last";
    public OutboundOptions Outbound { get; init; } = new();

    /// <summary>Whether the session connects to the neighbouring regions the grid announces and keeps a world for each. Off by
    /// default: one region, as before.</summary>
    public bool Neighbours { get; init; }

    /// <summary>The draw distance in metres the session starts with (what the preference says), so the first agent updates already carry it (half of it while arriving).</summary>
    public float DrawDistance { get; init; } = Interest.DefaultDrawDistance;
    public ViewerIdentity Identity { get; init; } = ViewerIdentity.Current;

    /// <summary>Whether the log names the login service's host and port (for debugging a connection). Off by default: the
    /// login lines then say only the scheme and path, so a log a user shares does not say which grid they use.</summary>
    public bool LogLoginHost { get; init; }

    /// <summary>How long a session seen in world stays logged in with no presentation attached (gateway: 120 s).</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>The same for a session that never reached InWorld (gateway: 15 s).</summary>
    public TimeSpan GracePreInWorld { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan LogoutTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The wait used for grace (tests pass a controllable one).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>The wait the block list request is given before the list is called unavailable (tests pass a controllable one; the grace's <see cref="Delay"/> is not shared with it).</summary>
    public Func<TimeSpan, CancellationToken, Task> MuteListDelay { get; init; } = Task.Delay;

    /// <summary>The folder that holds the block list's file for each account. Null (the default) keeps the list in memory only.</summary>
    public string? MuteCacheDirectory { get; init; }
}

/// <summary>
/// One agent's session on one grid: login, monotonic stages, resume, logout, disconnect (extracted from the web
/// gateway's AgentSession, web-viewer bb91ba5). Owns its transport, its cancellation and its log.
/// Multi-session invariant (ADR-006): all state is per instance; nothing static holds session data; <see cref="Dispose"/>
/// cancels every in-flight operation, unsubscribes from the transport, disposes it and drops every subscriber.
/// Appearance-passive (hazard 3): the session has no path that bakes or sends appearance; the
/// adapter keeps LibreMetaverse's automatic appearance off.
/// Resume: a native host can detach its presentation (UI reload) and re-attach within a grace window; the session
/// stays logged in meanwhile and ends on expiry, as the gateway does for a browser.
/// </summary>
public sealed class ViewerSession : IDisposable
{
    private readonly SessionOptions _opts;

    /// <summary>The login address this session logged in through (which grid it is on).</summary>
    public string LoginUri => _opts.LoginUri;

    /// <summary>Which kind of grid this session is on, from its login address's host (<see cref="GridPolicy.KindOf"/>).</summary>
    public GridKind Grid { get; }

    /// <summary>The viewer statistics sender, while one runs: only on a grid whose policy allows the report
    /// (<see cref="ViewerFeature.ViewerStatistics"/>) and whose transport can post it; from arrival until logout.</summary>
    public ViewerStatsSender? Statistics { get; private set; }

    /// <summary>The host's figures for the statistics report (frame rate, graphics adapter, language). Read on a worker thread.</summary>
    public Func<ViewerStatsFigures>? StatsFigures { get; set; }

    /// <summary>The period between statistics reports (the official five minutes unless a test sets it).</summary>
    public TimeSpan? StatsPeriod { get; init; }

    /// <summary>Whether a feature beyond the official viewer may run on this session's grid (<see cref="GridPolicy.Allows"/>).</summary>
    public bool Allows(ViewerFeature feature) => GridPolicy.Allows(feature, Grid);

    /// <summary>Whether <paramref name="self"/> may save objects whose parts <paramref name="partCreators"/> made to a file on this
    /// session's grid (<see cref="GridPolicy.MayBackUp"/>).</summary>
    public bool MayBackUp(Guid self, IReadOnlyCollection<Guid> partCreators) => GridPolicy.MayBackUp(Grid, self, partCreators);
    private readonly ISessionTransport _transport;
    private readonly OutboundPolicy _outbound;
    private readonly CoreLog _log;
    private readonly CancellationTokenSource _life = new();
    private readonly object _lock = new();

    private SessionStage _stage = SessionStage.Idle;
    private bool _eqSeen;
    private bool _movementSeen;
    private string? _firstFailReason;
    private int _loginSizeX, _loginSizeY;
    private SimRef? _loginSim;   // the region the login reply's size belongs to: the first one connected
    private RegionInfo? _region;
    private bool _presentationAttached = true;
    private CancellationTokenSource? _grace;
    private bool _ended;
    private bool _disposed;
    private float _drawDistance = Interest.DefaultDrawDistance;
    private WorldState _world;
    /// <summary>Which region's world holds each object and avatar, and how many have been handed from one world to another.</summary>
    public IdentityHub Identity => _identity;
    private readonly IdentityHub _identity = new();   // which region's world holds each object and avatar, by full id (the worlds hand objects to one another)
    private readonly WorldState _anchorWorld;   // the first world: what the presentation is built on; it is never disposed before the session, and takes every landing
    private SimRef? _currentSim;
    private SimRef? _originSim;   // the region the presentation is anchored on: the one logged in to, or last teleported to
    private readonly Dictionary<SimRef, Neighbour> _neighbours = new();

    /// <summary>A neighbouring region the session holds a circuit to: what is known of it and the world its events go into.</summary>
    private sealed class Neighbour(SimRef sim, RegionInfo region, WorldState world)
    {
        public SimRef Sim { get; } = sim;
        public RegionInfo Region { get; set; } = region;
        public WorldState World { get; } = world;
    }

    /// <summary>Short random tag for this session's log entries. Not a credential and not derived from one.</summary>
    public string LogTag { get; }
    public Guid AgentId { get; private set; }
    public SessionEndReason EndReason { get; private set; }

    /// <summary>Set when the grid ended the session unasked (the circuit went quiet, a kick, a simulator shutdown): why and where the agent was. Null otherwise.</summary>
    public DropInfo? Drop { get; private set; }

    public SessionStage Stage { get { lock (_lock) return _stage; } }
    public bool IsEnded { get { lock (_lock) return _ended; } }
    public RegionInfo? Region { get { lock (_lock) return _region; } }

    /// <summary>The name of the parcel the agent stands in, as its current region last said; null before it has.</summary>
    public string? AgentParcelName { get { lock (_lock) return _agentParcel; } }

    private string? _agentParcel;
    private int? _agentParcelId;   // the local id of the parcel the agent stands in, when the region has said
    private string _parcelMusic = "";

    /// <summary>The music address of the parcel the agent stands in, as the region last said it (empty when the parcel has none or the region has not said yet).
    /// It changes when the agent moves to a parcel, and when the region reports a new address for that parcel whatever the update's sequence number was.</summary>
    public string ParcelMusicUrl { get { lock (_lock) return _parcelMusic; } }

    /// <summary>The camera constraint plane the region last sent (normal x, y, z, distance w; all zero: none).</summary>
    public System.Numerics.Vector4 CameraCollidePlane { get; private set; }

    /// <summary>Whether the agent has god powers (the GrantGodlikePowers message gave a level above zero).</summary>
    public bool AgentGodlike { get; private set; }

    /// <summary>The controls scripts in the region have taken from the user's avatar (the left mouse button among them).</summary>
    public Viewer.Motion.ScriptControls ScriptControls { get; } = new();
    public bool PresentationAttached { get { lock (_lock) return _presentationAttached; } }
    public OutboundPolicy Outbound => _outbound;

    /// <summary>This session's world: objects, linksets, avatars, attachments and terrain of the region the agent is in. With
    /// <see cref="SessionOptions.Neighbours"/> on it is another instance after a crossing (<see cref="Worlds"/> lists them all).</summary>
    public WorldState World { get { lock (_lock) return _world; } }

    /// <summary>The one way the interface asks the grid to do something (ADR-015): chat, IM, touch, sit, teleport, inventory
    /// operations and held movement all go through it. The session exposes no transport.</summary>
    public ActionPipeline Actions { get; }

    /// <summary>The draw distance reported to the simulator (64-1024 m, default 1024).</summary>
    public float DrawDistance { get { lock (_lock) return _drawDistance; } }

    /// <summary>Raised (outside the lock) when the stage moves forward.</summary>
    public event Action<SessionStage>? StageChanged;

    /// <summary>Raised once when the session ends for any reason other than disposal.</summary>
    public event Action<SessionEndReason>? Ended;

    /// <summary>A world event accepted by the first-burst rule, raised after <see cref="World"/> has applied it.</summary>
    public event Action<SimWorldEvent>? WorldEvent;

    public event Action<RegionInfo>? RegionChanged;

    /// <summary>Whether this session connects to neighbouring regions (<see cref="SessionOptions.Neighbours"/>).</summary>
    public bool NeighboursEnabled => _opts.Neighbours;

    /// <summary>The development aids for neighbour work, when the transport has them (only the development automation uses them).</summary>
    public INeighbourControl? NeighbourControl => _transport as INeighbourControl;

    /// <summary>The development aid that makes plain folders and links in the agent's inventory; null when the transport has none.</summary>
    public IInventoryBuilder? InventoryBuilder => _transport as IInventoryBuilder;

    /// <summary>The grid's texture upload (its two capability steps), when the transport has one.</summary>
    public Viewer.Core.Building.ITextureUploadService? BuildService => _transport as Viewer.Core.Building.ITextureUploadService;

    /// <summary>The edit limits of the agent's region: the official ones with the grid's own from its simulator features.</summary>
    /// <summary>Whether the agent's region takes a hover height (false when the transport cannot say).</summary>
    public bool HoverHeightSupported => (_transport as IHoverHeightTransport)?.HoverHeightSupported ?? false;

    /// <summary>States the agent's hover height to its region; false when the region does not take it or did not accept it.</summary>
    public Task<bool> SetHoverHeightAsync(float metres, CancellationToken ct = default) =>
        (_transport as IHoverHeightTransport)?.SendHoverHeightAsync(metres, ct) ?? Task.FromResult(false);

    /// <summary>The grid's own settings (chat ranges, map server, search and guide addresses, currency, export flag) from the agent's
    /// region, or Second Life's defaults while the region has sent none.</summary>
    public GridFeatures GridFeatures
    {
        get
        {
            var features = (_transport as IGridFeaturesSource)?.GridFeatures(GridInfoValues) ?? GridFeatures.From(null, GridInfoValues);
            return Grid == GridKind.SecondLife ? features.OnSecondLife() : features;
        }
    }

    /// <summary>What the grid's own grid-info document says (its search page, its currency helper), set by the interface from the grid
    /// it logged in to; the region's settings and the login reply come first.</summary>
    public GridLoginValues? GridInfoValues { get; set; }

    /// <summary>Whether the agent's region offers a capability by this name (false when the transport cannot say).</summary>
    public bool RegionHas(string capability) => (_transport as IGridFeaturesSource)?.HasRegionCapability(capability) ?? false;

    public Viewer.Core.Building.EditLimits EditLimits => (_transport as Viewer.Core.Protocol.LmvTransport)?.EditLimits() ?? Viewer.Core.Building.EditLimits.Official;

    /// <summary>The grid's model upload (the same capability as images, with a model request), or null when the transport has none.</summary>
    public Viewer.Core.Building.IModelUploadService? ModelService => _transport as Viewer.Core.Building.IModelUploadService;

    /// <summary>Every connected region with its world: the current one first. One entry unless <see cref="SessionOptions.Neighbours"/> is on.</summary>
    public IReadOnlyList<RegionWorld> Worlds
    {
        get
        {
            lock (_lock)
            {
                var all = new List<RegionWorld>(1 + _neighbours.Count);
                if (_currentSim is not { } here || _region is not { } region)
                    return all;
                all.Add(new RegionWorld(here, region, _world, true, System.Numerics.Vector3.Zero, _world.OriginOffset));
                foreach (var n in _neighbours.Values.OrderBy(n => n.Sim.Handle))
                    all.Add(new RegionWorld(n.Sim, n.Region, n.World, false, RegionPlacement.Offset(here, n.Sim), n.World.OriginOffset));
                return all;
            }
        }
    }

    /// <summary>A region was added, removed or became the current one (raised outside the lock, on the adapter's thread).</summary>
    public event Action<RegionWorldChange>? WorldsChanged;

    private void RaiseWorlds(RegionWorldChangeKind kind, SimRef sim) => WorldsChanged?.Invoke(new RegionWorldChange(kind, sim));

    /// <summary>Raised on the adapter's thread for every communication event the session accepts: local chat from its
    /// region (<see cref="ChatReceived"/>), instant messages (<see cref="InstantMessageReceived"/>) and name replies
    /// (<see cref="AgentNamesReceived"/>), after <see cref="Names"/> has learned from it. Never logged with its content.</summary>
    public event Action<TransportEvent>? Communication
    {
        add
        {
            IReadOnlyList<TransportEvent> kept;
            lock (_communicationLock)
            {
                _communication += value;
                kept = _late.Current();
            }
            // the friend list comes with the login reply and the group list soon after, before the interface has attached: whoever
            // subscribes later still gets them, as they stand now
            if (value is not null)
                foreach (var e in kept)
                    value(e);
        }
        remove
        {
            lock (_communicationLock)
                _communication -= value;
        }
    }

    private readonly object _communicationLock = new();
    private Action<TransportEvent>? _communication;
    private readonly LateSubscriberSnapshot _late = new();

    /// <summary>This session's inventory: the login skeleton, folders fetched when first opened, and the grid's pushes. Never
    /// written to a capture or a log (folder and item names are the user's own).</summary>
    public Viewer.Core.Inventory.InventoryModel Inventory { get; }

    private void RequestInventoryFolder(Guid folder, Guid owner) =>
        (_transport as Viewer.Core.Inventory.IInventoryRequester ?? throw new InvalidOperationException("no inventory requester")).RequestFolderContents(folder, owner);

    /// <summary>This session's agent names (chat, instant messages and the grid's name replies feed it).</summary>
    public Viewer.Core.Social.NameCache Names { get; } = new();

    /// <summary>This session's block list: the grid's own list once it answers, then every add and remove. The session drops what a blocked
    /// resident, object or group sends before any listener sees it (<see cref="Viewer.Core.Social.MuteFilter"/>).</summary>
    public Viewer.Core.Social.MuteList Mutes { get; } = new(Guid.Empty);

    /// <summary>How many things the block list has dropped this session (a count; no content is kept).</summary>
    public int MutedDrops => Volatile.Read(ref _mutedDrops);
    private int _mutedDrops;

    /// <summary>How long the grid is given to answer the block list request before the list is called unavailable.</summary>
    public static readonly TimeSpan MuteListWait = TimeSpan.FromSeconds(30);

    /// <summary>Asks the grid for the names <see cref="Names"/> has queued; returns how many were asked for (0 when the transport cannot ask).</summary>
    public int RequestQueuedNames()
    {
        lock (_lock)
        {
            if (_disposed || _ended)
                return 0;
        }
        return Names.Flush(_transport as IAgentNameRequester);
    }

    /// <summary>Asks the grid for the titles a group offers this agent (a read; the answer is a <see cref="GroupTitlesListed"/> event).</summary>
    public void RequestGroupTitles(Guid group)
    {
        lock (_lock)
        {
            if (_disposed || _ended)
                return;
        }
        if (_transport is IGroupRequester groups)
        {
            try { groups.RequestGroupTitles(group); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log.Warn("group.titles-request-failed", CoreLog.F("error", ex.GetType().Name)); }
        }
    }

    private IReadOnlyList<CoarseAvatar> _coarse = [];

    /// <summary>The region's coarse-location list (everyone the region tells this agent about), replaced by each update and emptied when a new region connects.</summary>
    public IReadOnlyList<CoarseAvatar> CoarseLocations { get { lock (_lock) return _coarse; } }

    /// <summary>What is known of the session at the moment the grid ended it: the grid's words and the place the agent stood (the soles of its body box).</summary>
    private DropInfo DropOf(Disconnected d)
    {
        var region = Region;
        var self = AgentId == Guid.Empty ? null : World.AvatarOf(AgentId);
        bool place = region is not null && self is not null;
        var soles = self is null ? default : self.Position - new System.Numerics.Vector3(0, 0, self.Scale.Z / 2);
        return new DropInfo(d.Kind, d.Message, region?.Name ?? "", region?.SizeX ?? 0, region?.SizeY ?? 0, soles, place);
    }

    /// <summary>Makes the circuit take in no more packets, so it times out. Only the development build's automation command calls it. False when the transport cannot or has no circuit.</summary>
    public bool CloseInboundCircuit()
    {
        if (_transport is not IInboundGate gate)
            return false;
        try { return gate.CloseInbound(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("circuit.close-inbound-failed", CoreLog.F("error", ex.GetType().Name));
            return false;
        }
    }

    private bool Running()
    {
        lock (_lock) return !_disposed && !_ended;
    }

    /// <summary>The map cells the grid can be asked for: a non-empty rectangle inside the 16-bit grid.</summary>
    private static bool SensibleBlocks(int minX, int minY, int maxX, int maxY) =>
        minX >= 0 && minY >= 0 && maxX <= ushort.MaxValue && maxY <= ushort.MaxValue && minX <= maxX && minY <= maxY;

    /// <summary>Asks the grid for map blocks (a read). False when the transport cannot, the request is not sensible or the session is not running.</summary>
    public bool RequestMapBlocks(int minX, int minY, int maxX, int maxY) =>
        Read(t => SensibleBlocks(minX, minY, maxX, maxY) && t is IMapRequester m ? Do(() => m.RequestMapBlocks(minX, minY, maxX, maxY)) : false, "map.blocks-request-failed");

    /// <summary>Asks the grid for the regions matching a name (a read). False when the transport cannot, the name is empty or the session is not running.</summary>
    public bool RequestRegionNamed(string name)
    {
        string n = (name ?? "").Trim();
        return Read(t => n.Length > 0 && n.Length <= 128 && t is IMapRequester m ? Do(() => m.RequestRegionNamed(n)) : false, "map.name-request-failed");
    }

    /// <summary>Asks the grid where a friend is (a read). False when the transport cannot or the session is not running.</summary>
    public bool RequestFriendLocation(Guid friend) =>
        Read(t => friend != Guid.Empty && t is IMapRequester m ? Do(() => m.RequestFriendLocation(friend)) : false, "map.friend-request-failed");

    private static bool Do(Action action)
    {
        action();
        return true;
    }

    private bool Read(Func<ISessionTransport, bool> ask, string failure)
    {
        if (!Running())
            return false;
        try { return ask(_transport); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn(failure, CoreLog.F("error", ex.GetType().Name));
            return false;
        }
    }

    /// <summary>A map tile picture from the grid's map server (a read through the outbound policy). Failed when the session is not running or the transport cannot.</summary>
    public async Task<Viewer.Core.Assets.AssetFetchResult> FetchMapTileAsync(Viewer.Core.Map.MapTileKey key, CancellationToken ct)
    {
        if (!Running() || _transport is not IMapTileTransport tiles)
            return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Failed);
        try { return await tiles.FetchMapTileAsync(key.Level, key.X, key.Y, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Cancelled); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("map.tile-failed", CoreLog.F("error", ex.GetType().Name));
            return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Failed);
        }
    }

    public ViewerSession(SessionOptions options, ISessionTransport transport, CoreLog log)
    {
        var bad = options.Outbound.Validate().ToList();
        if (bad.Count > 0)
            throw new ArgumentException("invalid outbound options: " + string.Join(", ", bad), nameof(options));
        _opts = options;
        Grid = GridPolicy.KindOf(options.LoginUri);
        _transport = transport;
        _outbound = new OutboundPolicy(options.Outbound);
        _drawDistance = Interest.ClampDrawDistance(options.DrawDistance);
        LogTag = "s" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        _log = log.For("session." + LogTag);
        _anchorWorld = _world = new WorldState(_log);
        _world.AttachIdentity(_identity);
        _identity.Current = _world;
        Actions = new ActionPipeline(() => _transport as ICommandTransport, InWorldNow, _log);
        Inventory = new Viewer.Core.Inventory.InventoryModel(RequestInventoryFolder);
        _transport.Event += OnTransportEvent;
    }

    // ---------------- login / logout ----------------

    /// <summary>Text about the login service as the log may carry it: unchanged when <see cref="SessionOptions.LogLoginHost"/>
    /// is on, otherwise with the login URI's host and port replaced by "(host)".</summary>
    private string ForLog(string text) => LoginHostLeftOut(text, _opts.LoginUri, _opts.LogLoginHost);

    /// <summary><paramref name="text"/> with the host and port of <paramref name="loginUri"/> replaced by "(host)", unless
    /// <paramref name="includeHost"/>; a login URI that cannot be read leaves nothing of itself.</summary>
    public static string LoginHostLeftOut(string text, string loginUri, bool includeHost)
    {
        if (includeHost) return text;
        if (!Uri.TryCreate(loginUri, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return text.Replace(loginUri, "(login uri)", StringComparison.OrdinalIgnoreCase);
        return text.Replace(uri.Authority, "(host)", StringComparison.OrdinalIgnoreCase)
            .Replace(uri.Host, "(host)", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs the grid login. The password is passed to the transport for this call and never kept.</summary>
    public async Task<LoginOutcome> LoginAsync(LoginCredentials credentials, CancellationToken ct)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stage != SessionStage.Idle || _ended)
                throw new InvalidOperationException("a session logs in once");
        }
        Advance(SessionStage.LoggingIn);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);

        // WV-3d-1: the login URI is checked before anything is sent.
        if (_outbound.Strict)
        {
            try
            {
                await _outbound.EnsureUriAllowedAsync(new Uri(_opts.LoginUri), OutboundKind.Login, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OutboundDeniedException or UriFormatException)
            {
                _log.Warn("login.refused", CoreLog.F("code", "outboundDenied"), CoreLog.F("reason", ForLog(ex.Message)));
                End(SessionEndReason.LoginFailed);
                return new LoginOutcome(false, "outboundDenied", "The viewer is not permitted to reach this grid's login service");
            }
        }

        _log.Info("login.start", CoreLog.F("uri", ForLog(_opts.LoginUri)), CoreLog.F("channel", _opts.Identity.Channel), CoreLog.F("version", _opts.Identity.Version));
        TransportLoginResult r;
        try
        {
            r = await _transport.LoginAsync(
                new TransportLoginRequest(_opts.LoginUri, credentials.FirstName, credentials.LastName, credentials.Password, _opts.StartLocation, _opts.Identity, _opts.Neighbours, credentials.Extras),
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("login.cancelled");
            End(SessionEndReason.LoginFailed);
            return new LoginOutcome(false, "cancelled", "Login cancelled");
        }
        catch (Exception ex)
        {
            _log.Error("login.exception", ex);
            End(SessionEndReason.LoginFailed);
            return new LoginOutcome(false, "exception", ex.Message);
        }

        if (!r.Ok)
        {
            string reason;
            lock (_lock) reason = _firstFailReason ?? r.FailureMessage ?? "";   // the grid's own words; none invented here
            // the grid's words go in the log beside the code: "key" is also the code of a region that refused the launch, so the
            // code alone reads as a wrong password
            _log.Warn("login.failed", CoreLog.F("code", r.FailureCode ?? "failed"), CoreLog.F("message", reason.Trim()));
            End(SessionEndReason.LoginFailed);
            return new LoginOutcome(false, r.FailureCode ?? "failed", reason.Trim()) { Reply = r.Reply, Certificate = r.Certificate };
        }

        lock (_lock)
        {
            AgentId = r.AgentId;
            _identity.SelfAgent = r.AgentId;
            Mutes.SetSelf(r.AgentId);
            _loginSizeX = r.LoginRegionSizeX;
            _loginSizeY = r.LoginRegionSizeY;
        }
        // The block list's file for this account is read now, so blocks apply before the grid answers; the list keeps it up to date from here.
        if (_opts.MuteCacheDirectory is { Length: > 0 } cacheDirectory)
            Mutes.AttachCache(Viewer.Core.Social.MuteCache.FileFor(cacheDirectory, r.AgentId), problem => _log.Warn(problem));
        // The session id is not kept: only the adapter needs it, and it holds its own.
        _log.Info("login.ok", CoreLog.F("agent", r.AgentId), CoreLog.F("login-region-size", $"{r.LoginRegionSizeX}x{r.LoginRegionSizeY}"));
        StartStatistics();
        return new LoginOutcome(true, null, null) { Reply = r.Reply };
    }

    // the statistics report: only where the grid policy asks for it and the transport can post it (llviewerstats.cpp send_viewer_stats)
    private void StartStatistics()
    {
        if (!Allows(ViewerFeature.ViewerStatistics) || _transport is not IViewerStatsTransport stats)
            return;
        lock (_lock)
            if (_disposed || _ended)
                return;
        var started = DateTimeOffset.UtcNow;
        Statistics = new ViewerStatsSender(stats, () =>
        {
            var (run, memoryKb, ramKb) = ViewerStatsSender.ProcessFigures();
            var figures = StatsFigures?.Invoke() ?? ViewerStatsFigures.Unknown;
            return new ViewerStatsReport.Inputs(_opts.Identity, DateTimeOffset.UtcNow, run, (DateTimeOffset.UtcNow - started).TotalSeconds, figures,
                0, 0, 1, memoryKb, ramKb, stats.DeviceIds, stats.Traffic, true);
        }, StatsPeriod);
        _log.Info("stats.started");
    }

    public async Task LogoutAsync(CancellationToken ct) => await LogoutCoreAsync(SessionEndReason.LoggedOut, ct).ConfigureAwait(false);

    private async Task LogoutCoreAsync(SessionEndReason reason, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_ended)
                return;
            _ended = true;          // the grid's Disconnected that follows is expected
            CancelGraceLocked();
        }
        Actions.EndAllHolds(HoldEnd.SessionEnded);   // nothing held outlives the session's time in world
        _log.Info("logout.start", CoreLog.F("reason", reason));
        if (Statistics is { } stats)
        {
            Statistics = null;
            await stats.SendFinalAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);   // the last report, as the official viewer's quit sends it
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
            timeout.CancelAfter(_opts.LogoutTimeout);
            await _transport.LogoutAsync(timeout.Token).ConfigureAwait(false);
            _log.Info("logout.complete");
        }
        catch (Exception ex)
        {
            _log.Warn("logout.unclean", CoreLog.F("error", ex));
        }
        RaiseEnded(reason);
    }

    private bool InWorldNow()
    {
        lock (_lock) return !_disposed && !_ended && _stage == SessionStage.InWorld;
    }

    /// <summary>Sets the draw distance (clamped 64-1024 m); the simulator culls the objects it sends by it.</summary>
    public void SetDrawDistance(float metres)
    {
        float d = Interest.ClampDrawDistance(metres);
        lock (_lock)
        {
            if (_disposed || Math.Abs(_drawDistance - d) < 0.5f)
                return;
            _drawDistance = d;
        }
        _log.Info("agent.draw-distance", CoreLog.F("metres", d));
        AdvanceFar();
    }

    private readonly FarRamp _farRamp = new(TimeSpan.FromMilliseconds(Environment.TickCount64));
    private float _farSent = -1f;
    private long _teleportStartedMs = -1;   // when the teleport in progress began (TickCount64); -1: none
    private const long TeleportGateMs = 60_000;   // a teleport whose end never came does not silence the agent for good
    private bool Teleporting() => _teleportStartedMs >= 0 && Environment.TickCount64 - _teleportStartedMs < TeleportGateMs;   // the caller holds _lock

    /// <summary>Main thread, once a frame: the far distance sent in the agent's updates follows <see cref="FarRamp"/> toward the draw distance
    /// (half of it while arriving). The transport sends nothing until it is connected.</summary>
    public void AdvanceFar()
    {
        float target;
        bool inWorld;
        lock (_lock)
        {
            if (_disposed || _ended || Teleporting())
                return;   // (the far distance travels in an agent update, so it waits out a teleport too)
            target = _drawDistance;
            inWorld = _stage == SessionStage.InWorld;
        }
        float far = _farRamp.Next(TimeSpan.FromMilliseconds(Environment.TickCount64), arriving: !inWorld, target);
        if (Math.Abs(far - _farSent) < 0.5f)
            return;
        _farSent = far;
        _transport.SetDrawDistance(far);
        _log.Info("agent.far", CoreLog.F("metres", far));
    }

    /// <summary>Sends the user's own avatar's controls to the region (in world only); false when nothing was sent.</summary>
    public bool SendAgentUpdate(in Viewer.Motion.AgentUpdate update)
    {
        lock (_lock)
        {
            if (_disposed || _ended || _stage != SessionStage.InWorld || Teleporting())
                return false;
        }
        if (_transport is not IAgentControlTransport controls)
            return false;
        controls.SendAgentUpdate(update);
        return true;
    }

    /// <summary>Tells the region whether the user's own avatar runs (in world only); false when nothing was sent.</summary>
    public bool SendAlwaysRun(bool run)
    {
        lock (_lock)
        {
            if (_disposed || _ended || _stage != SessionStage.InWorld)
                return false;
        }
        if (_transport is not IAgentControlTransport controls)
            return false;
        controls.SendAlwaysRun(run);
        return true;
    }

    /// <summary>Asks the region to start or stop an animation on the user's own avatar (in world only); false when nothing was sent.</summary>
    public bool SendAnimation(Guid animation, bool start)
    {
        lock (_lock)
        {
            if (_disposed || _ended || _stage != SessionStage.InWorld)
                return false;
        }
        if (animation == Guid.Empty || _transport is not IAgentAnimationTransport animations)
            return false;
        animations.SendAnimation(animation, start);
        return true;
    }

    /// <summary>Asks the region to start and stop several animations on the user's own avatar in one message, in the order given
    /// (in world only); false when nothing was sent.</summary>
    public bool SendAnimations(IReadOnlyList<(Guid Animation, bool Start)> requests)
    {
        lock (_lock)
        {
            if (_disposed || _ended || _stage != SessionStage.InWorld)
                return false;
        }
        if (requests.Count == 0 || _transport is not IAgentAnimationTransport animations)
            return false;
        animations.SendAnimations(requests);
        return true;
    }

    // ---------------- resume (presentation attach/detach with grace) ----------------

    /// <summary>The presentation went away (UI reload, host restart of the scene). The session waits for a resume.</summary>
    public void DetachPresentation()
    {
        TimeSpan wait;
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_ended || _disposed || !_presentationAttached)
                return;
            _presentationAttached = false;
            CancelGraceLocked();
            cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            _grace = cts;
            wait = _stage == SessionStage.InWorld ? _opts.Grace : _opts.GracePreInWorld;
        }
        _log.Info("resume.waiting", CoreLog.F("grace-seconds", wait.TotalSeconds), CoreLog.F("stage", Stage));
        _ = Task.Run(async () =>
        {
            try { await _opts.Delay(wait, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            lock (_lock)
            {
                if (!ReferenceEquals(_grace, cts) || _presentationAttached || _ended)
                    return;
                _grace = null;
            }
            _log.Info("resume.expired");
            await LogoutCoreAsync(SessionEndReason.GraceExpired, CancellationToken.None).ConfigureAwait(false);
        });
    }

    /// <summary>Re-attach a presentation. True if the session is still alive (the grace window had not run out).</summary>
    public bool ResumePresentation()
    {
        lock (_lock)
        {
            if (_ended || _disposed)
                return false;
            _presentationAttached = true;
            CancelGraceLocked();
        }
        _log.Info("resume.attached");
        return true;
    }

    private void CancelGraceLocked()
    {
        if (_grace is null)
            return;
        _grace.Cancel();
        _grace.Dispose();
        _grace = null;
    }

    /// <summary>Raised on the adapter's thread for what sounds in the world need: the sound messages, the parcel overlay and the agent's
    /// parcel (the parcel rule), and avatars' animations (someone starting to type).</summary>
    public event Action<TransportEvent>? Sound;

    // ---------------- transport events (adapter network threads) ----------------

    private void OnTransportEvent(TransportEvent e)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
        }
        if (MuteGate(e))
            return;
        if (e is SoundTriggered or AttachedSoundReceived or AttachedSoundGainChanged or SoundPreloaded or ParcelOverlayReceived
            or AgentParcelChanged or AvatarAnimationsReceived)
            Sound?.Invoke(e);
        switch (e)
        {
            case LoginProgressed { Phase: LoginPhase.ConnectingToSim }:
                Advance(SessionStage.LoggedIn);
                break;
            case LoginProgressed { Phase: LoginPhase.Failed } f:
                lock (_lock)
                {
                    if (_firstFailReason is null && !string.Equals(f.Message, "Canceled", StringComparison.OrdinalIgnoreCase))
                        _firstFailReason = f.Message;
                }
                break;
            case SimConnected c:
                OnSimConnected(c);
                break;
            case EnvironmentReceived env when IsCurrent(env.Sim):
                // the answer's size against the fetch's cap, or why there is none
                if (env.Llsd is { } answer)
                {
                    var parsed = Viewer.Core.World.RegionEnvironment.FromLlsd(answer, out _);
                    _log.Info("environment.received", CoreLog.F("bytes", System.Text.Encoding.UTF8.GetByteCount(answer)), CoreLog.F("cap_bytes", EnvironmentFetcher<object>.CapBytes),
                        CoreLog.F("sky-frames", parsed?.Sky.Length ?? 0), CoreLog.F("probe-ambiance-max", parsed is null ? 0f : parsed.Sky.Max(x => x.ProbeAmbiance)),
                        CoreLog.F("haze-per-metre-noon", parsed is null ? 0f : Viewer.Core.World.SkyLighting.FogDensity(parsed.SkyAt(0.5))));
                }
                else
                    _log.Info("environment.missing", CoreLog.F("why", env.Why ?? "none"));
                UpdateRegion(r => r with { EnvironmentLlsd = env.Llsd, EnvironmentMissing = env.Llsd is null ? env.Why ?? "none" : null }, "environment");
                break;
            case EventQueueRunning eqRunning:
                if (_opts.Neighbours && !IsCurrent(eqRunning.Sim))
                    break;   // a neighbour's event queue says nothing about the login
                bool bothEq;
                lock (_lock) { _eqSeen = true; bothEq = _eqSeen && _movementSeen; }
                Advance(bothEq ? SessionStage.InWorld : SessionStage.EventQueueRunning);
                break;
            case MovementComplete moved:
                if (_opts.Neighbours && !IsCurrent(moved.Sim))
                {
                    // The login stage is the current region's alone, but a teleport ends on this message whichever region it names, and
                    // on the wire it can arrive before the session has made the new region its current one.
                    _log.Info("movement.complete.not_current", CoreLog.F("region", moved.Sim.Name));
                    _communication?.Invoke(moved);
                    break;
                }
                bool bothMc;
                lock (_lock) { _movementSeen = true; bothMc = _eqSeen && _movementSeen; }
                Advance(bothMc ? SessionStage.InWorld : SessionStage.MovementComplete);
                _communication?.Invoke(moved);   // a teleport ends when the agent is in place in the new region
                break;
            case Disconnected d:
                bool expected;
                lock (_lock) expected = _ended;
                _log.Info("net.disconnected", CoreLog.F("reason", d.Reason), CoreLog.F("expected", expected));
                if (!expected)
                {
                    lock (_lock) Drop ??= DropOf(d);
                    RaiseEnded(SessionEndReason.Disconnected);
                }
                break;
            case SimWorldEvent w:
                if (IsCurrent(w.Sim))
                    WorldEvent?.Invoke(w);
                break;
            case ObjectUpdated o:
                if (WorldFor(o.Sim, out var ow, out bool oCurrent))
                {
                    ow.ApplyObject(o.Sim, o.Prim, o.EventIsAttachment, o.SimParentAvatar);
                    if (oCurrent) WorldEvent?.Invoke(new SimWorldEvent(o.Sim, "object.update"));
                }
                break;
            case TreeUpdated t:
                if (WorldFor(t.Sim, out var tw, out bool tCurrent))
                {
                    tw.ApplyTree(t.Sim, t.Tree);
                    if (tCurrent) WorldEvent?.Invoke(new SimWorldEvent(t.Sim, "tree.update"));
                }
                break;
            case AvatarUpdated a:
                if (WorldFor(a.Sim, out var aw, out bool aCurrent))
                {
                    aw.ApplyAvatar(a.Sim, a.Avatar);
                    if (aCurrent) WorldEvent?.Invoke(new SimWorldEvent(a.Sim, "avatar.update"));
                }
                break;
            case RegionTimeDilation td:
                if (WorldFor(td.Sim, out var dilationWorld, out _))
                    dilationWorld.SetTimeDilation(td.Sim, td.Dilation);
                break;
            case ObjectMoved m:
                if (WorldFor(m.Sim, out var mw, out bool mCurrent))
                {
                    mw.ApplyMove(m.Sim, m.LocalId, m.IsAvatar, m.Position, m.Rotation, m.Velocity, m.Acceleration, m.AngularVelocity, m.FootPlane);
                    if (mCurrent) WorldEvent?.Invoke(new SimWorldEvent(m.Sim, "object.move"));
                }
                break;
            case SelfMoved s:
                if (IsCurrent(s.Sim))
                    World.ApplySelfMotion(s.Sim, s.Motion);
                break;
            case AvatarAppearanceReceived ap:
                if (WorldFor(ap.Sim, out var apw, out bool apCurrent))
                {
                    Guid self;
                    lock (_lock) self = AgentId;
                    apw.ApplyAppearance(ap.Sim, ap.Appearance, self);
                    if (apCurrent) WorldEvent?.Invoke(new SimWorldEvent(ap.Sim, "avatar.appearance"));
                }
                break;
            case AvatarHoverReceived hv:
                if (WorldFor(hv.Sim, out var hvw, out _))
                    hvw.ApplyHover(hv.Sim, hv.AgentId, hv.Z);
                break;
            case AvatarAnimationsReceived an:
                if (WorldFor(an.Sim, out var anw, out _))
                    anw.ApplyAnimations(an.Sim, an.AgentId, an.Playing);
                break;
            case GodLevelReceived god:
                AgentGodlike = god.Level > 0;
                break;
            case CameraConstraintReceived cc:
                if (IsCurrent(cc.Sim))
                    CameraCollidePlane = cc.Plane;
                break;
            case ScriptControlChanged sc:
                if (IsCurrent(sc.Sim))
                    foreach (var entry in sc.Entries)
                    {
                        if (entry.Take) ScriptControls.Take(entry.Controls, entry.PassToAgent);
                        else ScriptControls.Release(entry.Controls, entry.PassToAgent);
                    }
                break;
            case ObjectsKilled k:
                if (WorldFor(k.Sim, out var kw, out bool kCurrent))
                {
                    kw.Kill(k.Sim, k.LocalIds);
                    if (kCurrent) WorldEvent?.Invoke(new SimWorldEvent(k.Sim, "object.kill"));
                }
                break;
            case LandPatch p:
                if (WorldFor(p.Sim, out var pw, out bool pCurrent))
                {
                    if (pCurrent)
                        UpdateRegion(r => RegionSize.GrowForPatch(r, p.X, p.Y, p.PatchSize), "patch");
                    else
                        UpdateNeighbour(p.Sim!, r => RegionSize.GrowForPatch(r, p.X, p.Y, p.PatchSize));
                    if (p.Heights is not null)
                        pw.ApplyPatch(p.Sim, p.X, p.Y, p.PatchSize, p.Heights);
                }
                break;
            case RegionWindReceived w:
                if (WorldFor(w.Sim, out var ww, out bool wCurrent) && wCurrent)
                    ww.Wind = new RegionWind(w.X, w.Y);
                break;
            case RegionGridLearned learned:
                RegionGridChanged(learned.Sim);
                break;
            case MapBlockSize m:
                UpdateRegion(r => RegionSize.FromMapBlock(r with { Grid = CurrentGrid.Length > 0 ? CurrentGrid : r.Grid }, m), "mapBlock");
                UpdateNeighbours(r => RegionSize.FromMapBlock(r, m));
                Communicate(m);   // the world map learns every region the grid names
                break;
            case SimDisconnected gone:
                RemoveNeighbour(gone.Sim);
                break;
            case AgentParcelChanged parcel:
                if (IsCurrent(parcel.Sim))
                    lock (_lock) { _agentParcel = parcel.Name; _agentParcelId = parcel.LocalId; _parcelMusic = parcel.MusicUrl; }
                break;
            case ParcelMusicReceived music:
                // the agent's own parcel only: another parcel's message (a selection, a neighbour) says nothing about what plays here
                if (IsCurrent(music.Sim))
                    lock (_lock) { if (_agentParcelId == music.LocalId) _parcelMusic = music.Url; }
                break;
            case ParcelEnvironmentReceived parcelEnv when IsCurrent(parcelEnv.Sim):
                if (parcelEnv.Llsd is not { } parcelAnswer)
                {
                    _log.Info("environment.parcel-missing", CoreLog.F("parcel", parcelEnv.ParcelId), CoreLog.F("why", parcelEnv.Why ?? "none"));
                    break;   // a failed answer changes nothing
                }
                bool here;
                lock (_lock) here = _agentParcelId is not { } now || now == parcelEnv.ParcelId;   // only for the parcel the agent is in now
                if (!here) break;
                var parcelParsed = Viewer.Core.World.RegionEnvironment.FromLlsd(parcelAnswer, out _);
                _log.Info("environment.parcel", CoreLog.F("parcel", parcelEnv.ParcelId), CoreLog.F("bytes", System.Text.Encoding.UTF8.GetByteCount(parcelAnswer)),
                    CoreLog.F("own-day-cycle", parcelParsed is not null), CoreLog.F("sky-frames", parcelParsed?.Sky.Length ?? 0),
                    CoreLog.F("day-length", parcelParsed?.DayLength ?? 0), CoreLog.F("day-offset", parcelParsed?.DayOffset ?? 0));
                UpdateRegion(r => r with { ParcelEnvironmentLlsd = parcelAnswer, ParcelEnvironmentId = parcelEnv.ParcelId }, "parcel environment");
                break;
            case CoarseLocationsUpdated coarse:
                if (IsCurrent(coarse.Sim))
                    lock (_lock) _coarse = coarse.Avatars;
                break;
            case TeleportProgressed tp:
                // llviewermessage.cpp send_agent_update: no update goes while a teleport is under way (any state but none or arriving)
                bool wasTeleporting;
                lock (_lock)
                {
                    wasTeleporting = Teleporting();
                    _teleportStartedMs = tp.Phase is TeleportPhase.Started ? Environment.TickCount64 : tp.Phase is TeleportPhase.Progress && _teleportStartedMs >= 0 ? _teleportStartedMs : -1;
                }
                if (wasTeleporting != (_teleportStartedMs >= 0))
                    _log.Info("agent.updates", CoreLog.F("sending", wasTeleporting));   // held for a teleport (false), or sent again (true)
                Communicate(e);
                break;
            case TeleportOffered or TeleportRequested or ServerAlert or ParcelCollision or FriendLocated or SitResponded or ObjectPropertiesReceived or ObjectCostReceived or TaskInventoryReceived or ContentsCopied or PayPriceReceived or ParcelDetailsReceived
                or ScriptDialogReceived or ScriptPermissionRequested or LoadUrlRequested or MapDestinationRequested or ObjectInstantMessage or InventoryOffered:
                Communicate(e);
                break;
            case MaterialOverridesReceived mo:
                if (WorldFor(mo.Sim, out var mow, out _))
                    mow.ApplyMaterialOverrides(mo.Sim, mo.LocalId, mo.Faces);
                break;
            case RenderMaterialsReceived rm:
                if (WorldFor(rm.Sim, out var rmw, out _))
                    rmw.ApplyRenderMaterials(rm.Sim, rm.LocalId, rm.Faces);
                break;
            case LegacyMaterialsReceived lm:
                if (WorldFor(lm.Sim, out var lmw, out _))
                    lmw.ApplyLegacyMaterials(lm.Sim, lm.Materials);
                break;
            case ChatReceived chat:
                if (IsCurrent(chat.Sim))
                    Communicate(chat);
                break;
            case BalanceChanged:
                Communicate(e);
                break;
            case MuteListReceived mutes:
                Mutes.Load(mutes.Entries);
                Communicate(e);
                break;
            case InstantMessageReceived or AgentNamesReceived:
            case FriendsListed or FriendPresenceChanged or FriendRightsChanged or FriendshipOffered or FriendshipAnswered or FriendshipEnded:
            case GroupsListed or ActiveGroupChanged or GroupTitlesListed or GroupChatJoined or GroupNoticeReceived:
                Communicate(e);
                break;
            case Viewer.Core.Inventory.InventorySkeletonReceived or Viewer.Core.Inventory.InventoryFolderFetched or Viewer.Core.Inventory.InventoryChanged:
                Inventory.Apply(e);
                RebuildLookAtLogin();
                break;
            case Viewer.Core.Inventory.LookRebuildFinished rebuilt:
                LastLookRebuild = rebuilt;
                _log.Info("appearance.login-rebuild", CoreLog.F("ok", rebuilt.Ok), CoreLog.F("cof", rebuilt.CofVersion), CoreLog.F("reason", rebuilt.Reason));
                break;
            case Viewer.Core.Inventory.OutfitCommandFinished done:
                OutfitFinished(done);
                break;
            case Viewer.Core.Appearance.WearableSaveFinished saved:
                EditedItemSaved(saved);
                break;
        }
    }

    // The look rebuild asked for once per login: 0 until the login region is known, 1 while a region that bakes on the server waits
    // for the current outfit folder to be known, 2 once asked (or never to be asked: the login region bakes on the client).
    private int _loginRebuild;

    /// <summary>The login region is known: in one that bakes on the server the look is rebuilt from the current outfit folder, as the
    /// official viewer does at every login (llstartup.cpp set_flags_and_update_appearance, LLAppearanceMgr::updateAppearanceFromCOF,
    /// which asks for a server appearance update whenever the region has a central bake version). A look the region still holds
    /// from an older outfit is then made again from the folder. Later regions (teleports, crossings) do not ask again.</summary>
    private void LoginRegionKnown(bool serverBaked) => Interlocked.CompareExchange(ref _loginRebuild, serverBaked ? 1 : 2, 0);

    /// <summary>Asks for the login rebuild once the agent is in the world, the login region bakes on the server and the current outfit
    /// folder is known (called when the session reaches the world and whenever the inventory changes).</summary>
    private void RebuildLookAtLogin()
    {
        if (Volatile.Read(ref _loginRebuild) != 1 || !InWorldNow())
            return;
        var cof = Inventory.FolderOfKind(Viewer.Core.Inventory.FolderKind.CurrentOutfit);
        if (cof == Guid.Empty)
            return;
        if (Interlocked.CompareExchange(ref _loginRebuild, 2, 1) != 1)
            return;
        var sent = Actions.Submit(new Viewer.Core.Actions.LookRebuildIntent(cof));
        _log.Info("appearance.login-rebuild-request", CoreLog.F("status", sent.Status), CoreLog.F("reason", sent.Code ?? ""));
    }

    /// <summary>The outcome of the look rebuild asked for at login (null until it has finished).</summary>
    public Viewer.Core.Inventory.LookRebuildFinished? LastLookRebuild { get; private set; }

    /// <summary>The newest outfit command's outcome (null until one has finished). A finished command settles the folders it touched
    /// with the grid: the current outfit folder is read again, and My Outfits after a save.</summary>
    public Viewer.Core.Inventory.OutfitCommandFinished? LastOutfitResult { get; private set; }

    /// <summary>Raised on the transport's thread when an outfit command has finished.</summary>
    public event Action<Viewer.Core.Inventory.OutfitCommandFinished>? OutfitCommandDone;

    private int _outfitResults;

    /// <summary>How many outfit commands have finished in this session.</summary>
    public int OutfitResults => Volatile.Read(ref _outfitResults);

    private void OutfitFinished(Viewer.Core.Inventory.OutfitCommandFinished done)
    {
        LastOutfitResult = done;
        Interlocked.Increment(ref _outfitResults);
        Inventory.Invalidate(Inventory.FolderOfKind(Viewer.Core.Inventory.FolderKind.CurrentOutfit));
        if (done.Touched is { } touched)
            foreach (var folder in touched)
                Inventory.Invalidate(folder);   // where copies of Library items landed
        if (done.Save)
        {
            // a new folder looks empty until it is read, and a My Outfits folder made just now is only known from its parent
            Inventory.Invalidate(Inventory.FolderOfKind(Viewer.Core.Inventory.FolderKind.MyOutfits));
            Inventory.Invalidate(Inventory.RootId);
            if (done.Folder != Guid.Empty)
                Inventory.Invalidate(done.Folder);
        }
        OutfitCommandDone?.Invoke(done);
    }

    /// <summary>The newest wearable save's outcome (null until one has finished).</summary>
    public Viewer.Core.Appearance.WearableSaveFinished? LastEditedItemSave { get; private set; }

    /// <summary>Raised on the transport's thread when a wearable save has finished.</summary>
    public event Action<Viewer.Core.Appearance.WearableSaveFinished>? EditedItemSaveDone;

    /// <summary>Reads a wearable asset (the file an inventory wearable points at).</summary>
    public Task<Viewer.Core.Assets.AssetFetchResult> FetchEditAssetAsync(Guid assetId, int assetType, CancellationToken ct) =>
        _transport is Viewer.Core.Appearance.IWearableTransport w
            ? w.FetchWearableAsync(assetId, assetType, ct)
            : Task.FromResult(Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.NotFound));

    /// <summary>What the grid's copy of a wearable item says (null when unknown or the transport cannot say).</summary>
    public Viewer.Core.Appearance.WearableItemInfo? EditItemInfo(Guid itemId) =>
        (_transport as Viewer.Core.Appearance.IWearableTransport)?.ItemInfo(itemId);

    /// <summary>The grid the agent's region is on ("host:port"; empty before a region, or when the transport does not say).</summary>
    public string CurrentGrid
    {
        get
        {
            SimRef? here;
            lock (_lock) here = _currentSim;
            return here?.Grid ?? Region?.Grid ?? "";
        }
    }

    /// <summary>The grid the agent's region is on when that is not the home grid (a Hypergrid visit); null at home.</summary>
    public string? VisitedGrid => CurrentGrid is { Length: > 0 } g && _transport is IGridKeys keys && !keys.IsHomeGrid(g) ? g : null;

    /// <summary>The grid logged in to ("host:port"), as the transport keeps it; empty when it does not say.</summary>
    public string HomeGrid => (_transport as IGridKeys)?.HomeGrid ?? "";

    /// <summary>The transport's notecard reading, when it has one.</summary>
    public Viewer.Core.Inventory.INotecardTransport? Notecards => _transport as Viewer.Core.Inventory.INotecardTransport;

    /// <summary>Reads and saves scripts (the script editor), where the transport can.</summary>
    public Viewer.Core.Inventory.IScriptTransport? Scripts => _transport as Viewer.Core.Inventory.IScriptTransport;

    /// <summary>Saves notecards (the notecard editor), where the transport can.</summary>
    public Viewer.Core.Inventory.INotecardSaveTransport? NotecardSaves => _transport as Viewer.Core.Inventory.INotecardSaveTransport;

    /// <summary>Makes new inventory items (Inventory › New), where the transport can.</summary>
    public Viewer.Core.Inventory.IItemCreateTransport? ItemCreation => _transport as Viewer.Core.Inventory.IItemCreateTransport;

    /// <summary>Active gestures, their files and playing them, where the transport can.</summary>
    public Viewer.Core.Inventory.IGestureTransport? Gestures => _transport as Viewer.Core.Inventory.IGestureTransport;

    /// <summary>The texture asset behind an inventory texture item (the empty id when unknown).</summary>
    public Guid TextureAssetOf(Guid itemId) =>
        (_transport as Viewer.Core.Appearance.IWearableTransport)?.TextureAssetOf(itemId) ?? Guid.Empty;

    private void EditedItemSaved(Viewer.Core.Appearance.WearableSaveFinished done)
    {
        LastEditedItemSave = done;
        // the grid's folders are the authority: the current outfit folder (links changed, version moved) and the item's own folder are read again
        Inventory.Invalidate(Inventory.FolderOfKind(Viewer.Core.Inventory.FolderKind.CurrentOutfit));
        if (done.ItemId != Guid.Empty && Inventory.Item(done.ItemId) is { } item)
            Inventory.Invalidate(item.ParentId);
        EditedItemSaveDone?.Invoke(done);
    }

    /// <summary>Names learn from the event first, so a listener that looks the speaker up finds them.</summary>
    private void Communicate(TransportEvent e)
    {
        Names.Learn(e);
        lock (_communicationLock) _late.Record(e);
        _communication?.Invoke(e);
    }

    private bool Ours(SimRef? sim) => FirstBurst.FromOurSim(sim, _transport.CurrentSim, _transport.ConnectedSimulatorCount);

    /// <summary>Whether <paramref name="sim"/> is the region the agent is in. With neighbours on, the region the session has recorded (so a
    /// crossing the library has made but the session has not yet recorded still counts the old region as current); otherwise, and
    /// before any region is recorded, the library's current simulator (the first-burst rule).</summary>
    private bool IsCurrent(SimRef? sim)
    {
        if (_opts.Neighbours)
            lock (_lock)
                if (_currentSim is { } here)
                    return ReferenceEquals(here, sim);
        return Ours(sim);
    }

    /// <summary>The world an event from <paramref name="sim"/> belongs in: the current one, a neighbour's, or none (an event from a
    /// simulator the session does not hold is dropped). <paramref name="current"/> says whether it is the current region's.</summary>
    private bool WorldFor(SimRef? sim, out WorldState world, out bool current)
    {
        world = null!;
        current = false;
        if (sim is null)
            return false;
        if (_opts.Neighbours)
        {
            lock (_lock)
            {
                if (_currentSim is { } here && ReferenceEquals(here, sim))
                {
                    world = _world;
                    current = true;
                    return true;
                }
                if (_neighbours.TryGetValue(sim, out var n))
                {
                    world = n.World;
                    return true;
                }
            }
        }
        if (!Ours(sim))
            return false;
        world = World;
        current = true;
        return true;
    }

    private void UpdateNeighbour(SimRef sim, Func<RegionInfo, RegionInfo> change)
    {
        Neighbour? n;
        RegionInfo next;
        lock (_lock)
        {
            if (!_neighbours.TryGetValue(sim, out n))
                return;
            next = change(n.Region);
            if (next == n.Region)
                return;
            n.Region = next;
        }
        n.World.UpdateRegion(next);
    }

    private void UpdateNeighbours(Func<RegionInfo, RegionInfo> change)
    {
        SimRef[] sims;
        lock (_lock) sims = _neighbours.Keys.ToArray();
        foreach (var sim in sims)
            UpdateNeighbour(sim, change);
    }

    private (WorldState World, DateTime At)? _leftBehind;   // the world an agent crossed out of while it still held the agent's avatar, and when

    /// <summary>The world the agent crossed out of still holds its avatar after <see cref="LeftAvatarGrace"/>: no region claimed it, so it goes.</summary>
    private void DropUnclaimedAvatar(DateTime now)
    {
        WorldState? drop = null;
        Guid self;
        lock (_lock)
        {
            self = AgentId;
            if (_leftBehind is not { } left || now - left.At < LeftAvatarGrace)
                return;
            _leftBehind = null;
            if (!ReferenceEquals(left.World, _world))   // the agent may have crossed back: then it is the current world's avatar
                drop = left.World;
        }
        drop?.DropAgent(self);
    }

    private readonly SilentNeighbourWatch _silent = new();
    private int _watchLoopStarted;

    /// <summary>How long the world an agent crossed out of may keep the agent's avatar when the new region has not sent it: the official viewer keeps the one
    /// avatar object until the region it moved to updates it, and a region that never does would leave a stale copy behind.</summary>
    internal static readonly TimeSpan LeftAvatarGrace = TimeSpan.FromSeconds(10);

    /// <summary>How often the neighbours are looked at for silence.</summary>
    private static readonly TimeSpan SilentCheckEvery = TimeSpan.FromSeconds(5);

    private async Task WatchNeighboursAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(SilentCheckEvery, ct).ConfigureAwait(false);
                CheckNeighbours(DateTime.UtcNow);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Looks for neighbours that have delivered nothing and prods their circuits (called on a timer while neighbours are on). A
    /// neighbour counts as having delivered once its world holds terrain or an object; each prod is logged by name.</summary>
    internal void CheckNeighbours(DateTime now)
    {
        DropUnclaimedAvatar(now);
        Neighbour[] held;
        lock (_lock) held = _neighbours.Values.ToArray();
        foreach (var n in held)
            if (n.World.TerrainPatchCount > 0 || n.World.Snapshot().Objects.Count > 0)
                _silent.Heard(n.Sim);
        foreach (var (sim, nudge) in _silent.Due(now))
        {
            bool known;
            lock (_lock) known = _neighbours.ContainsKey(sim);
            if (!known)
                continue;
            _log.Warn("region.neighbour-silent", CoreLog.F("region", sim.Name), CoreLog.F("grid", $"{sim.GridX},{sim.GridY}"), CoreLog.F("nudge", nudge));
            (_transport as IChildCircuitNudger)?.Nudge(sim);
        }
    }

    private void RemoveNeighbour(SimRef sim)
    {
        Neighbour? gone;
        lock (_lock)
        {
            if (!_neighbours.Remove(sim, out gone))
                return;
        }
        _silent.Heard(sim);
        if (ReferenceEquals(gone.World, _anchorWorld))
            gone.World.Retire();   // what is drawn from it goes, the world stays for the next landing
        else
            gone.World.Dispose();
        _log.Info("region.neighbour-removed", CoreLog.F("region", sim.Name));
        RaiseWorlds(RegionWorldChangeKind.Removed, sim);
    }

    private void ClearNeighbours()
    {
        SimRef[] sims;
        lock (_lock) sims = _neighbours.Keys.ToArray();
        foreach (var sim in sims)
            RemoveNeighbour(sim);
    }

    /// <summary>A circuit to a simulator that is not the agent's own is up: keep a world for it. A repeat of a known neighbour
    /// refreshes what the handshake says (terrain texturing, water).</summary>
    private void AddNeighbour(SimConnected c)
    {
        Neighbour? known;
        lock (_lock)
        {
            if (_disposed || ReferenceEquals(_currentSim, c.Sim))
                return;
            _neighbours.TryGetValue(c.Sim, out known);
        }
        if (known is not null)
        {
            UpdateNeighbour(c.Sim, r => r with { Terrain = c.Terrain ?? r.Terrain, WaterHeight = c.WaterHeight ?? r.WaterHeight, IsEstateManager = c.IsEstateManager ?? r.IsEstateManager });
            return;
        }
        SimRef? anchor;
        lock (_lock) anchor = _originSim;
        if (anchor is null)
        {
            _log.Warn("region.neighbour-unplaced", CoreLog.F("region", c.Sim.Name));   // no region to place it from yet
            return;
        }
        var region = RegionSize.AtConnect(c.Sim, c.SimSizeX, c.SimSizeY, 0, 0) with { Terrain = c.Terrain, WaterHeight = c.WaterHeight, IsEstateManager = c.IsEstateManager ?? false };
        var world = new WorldState(_log);
        world.AttachIdentity(_identity);
        world.SetOriginOffset(RegionPlacement.Offset(anchor, c.Sim));
        world.BeginRegion(c.Sim, region);
        world.ChooseAppearanceMode(c.Sim.Name, c.ServerBakedAppearance);
        lock (_lock)
        {
            if (_disposed)
            {
                world.Dispose();
                return;
            }
            _neighbours[c.Sim] = new Neighbour(c.Sim, region, world);
        }
        _log.Info("region.neighbour-added", CoreLog.F("region", c.Sim.Name), CoreLog.F("sim", $"{c.SimSizeX}x{c.SimSizeY}"));
        _silent.Added(c.Sim, DateTime.UtcNow);
        if (Interlocked.Exchange(ref _watchLoopStarted, 1) == 0)
            _ = WatchNeighboursAsync(_life.Token);
        RaiseWorlds(RegionWorldChangeKind.Added, c.Sim);
    }

    /// <summary>The agent walked into a neighbour whose world is already filled: it becomes the current world, the one left behind a
    /// neighbour, and nothing of either is cleared. The agent is one avatar that moves into the new region: when the new region sends it and what
    /// it wears, those objects move over by their full ids (<see cref="IdentityHub"/>), as the official viewer keeps the object and changes its region
    /// (<c>llviewerobjectlist.cpp</c> <c>processObjectUpdate</c>); the old region's kills for them then find nothing, and it never kills its own avatar.</summary>
    private void CrossInto(SimConnected c)
    {
        RegionInfo region;
        WorldState? leftWorld = null;
        SimRef? leftSim = null;
        lock (_lock)
        {
            if (_disposed || !_neighbours.Remove(c.Sim, out var entering))
                return;
            if (_currentSim is { } left && _region is { } leftRegion)
            {
                _neighbours[left] = new Neighbour(left, leftRegion, _world);
                leftWorld = _world;
                leftSim = left;
            }
            region = entering.Region with { Terrain = c.Terrain ?? entering.Region.Terrain, WaterHeight = c.WaterHeight ?? entering.Region.WaterHeight, IsEstateManager = c.IsEstateManager ?? entering.Region.IsEstateManager };
            _world = entering.World;
            _identity.Current = _world;
            _currentSim = c.Sim;
            _region = region;
            _coarse = [];
            _agentParcel = null;   // the new region names the parcel the agent lands on
            _agentParcelId = null;
            _parcelMusic = "";   // and says what plays there
        }
        _world.UpdateRegion(region);
        _world.ChooseAppearanceMode(c.Sim.Name, c.ServerBakedAppearance);
        if (leftWorld is not null && leftSim is not null)
        {
            Guid self;
            lock (_lock) self = AgentId;
            _world.CarryOwnLook(leftWorld, self);   // the look goes with the agent, and what it wears when this region already holds its avatar
            var left = leftWorld.AvatarOf(self);
            if (left is not null && _world.HasAvatar(self))
                leftWorld.ReleaseMoved(self, left.Id.LocalId, true);   // the avatar this region holds is the agent now; otherwise it moves when this region sends it
            else if (left is not null)
                lock (_lock) _leftBehind = (leftWorld, DateTime.UtcNow);   // and if it never does, the copy is dropped after a while
        }
        _log.Info("region.crossed", CoreLog.F("region", c.Sim.Name));
        RaiseWorlds(RegionWorldChangeKind.Crossed, c.Sim);
        RegionChanged?.Invoke(region);
        _communication?.Invoke(c);
    }

    private (int X, int Y) _connectSize;

    /// <summary>The agent's region turned out to be on another grid than it was taken to be on when it connected (a teleport whose destination
    /// grid was not known): a size taken from the login reply because it seemed to be the login region is dropped for its own message's, unless
    /// the grid's map has already said.</summary>
    private void RegionGridChanged(SimRef sim)
    {
        (int X, int Y) own;
        bool login;
        lock (_lock)
        {
            if (!ReferenceEquals(sim, _currentSim))
                return;
            own = _connectSize;
            login = _loginSim is { } l && l.SamePlace(sim);
        }
        UpdateRegion(r =>
        {
            if (r.SizeAuthoritative)
                return r with { Grid = sim.Grid };
            var sized = RegionSize.AtConnect(sim, own.X, own.Y, login ? _loginSizeX : 0, login ? _loginSizeY : 0);
            return r with { Grid = sim.Grid, SizeX = sized.SizeX, SizeY = sized.SizeY };
        }, "gridLearned");
    }

    private void OnSimConnected(SimConnected c)
    {
        if (_opts.Neighbours)
        {
            var library = _transport.CurrentSim;
            if (library is not null && !ReferenceEquals(library, c.Sim))
            {
                AddNeighbour(c);   // a child circuit: the agent is not in this region
                return;
            }
            bool crossing;
            lock (_lock) crossing = _currentSim is { } here && !ReferenceEquals(here, c.Sim) && _neighbours.ContainsKey(c.Sim);
            if (crossing)
            {
                CrossInto(c);
                return;
            }
            bool arrival;
            lock (_lock) arrival = _currentSim is { } there && !ReferenceEquals(there, c.Sim);
            if (arrival)
            {
                // a teleport: the regions around the old place are not around the new one, and the landing goes into the first world
                // (what the presentation was built on) whichever region the agent had walked into
                WorldState? leaving = null;
                SimRef? leftSim = null;
                lock (_lock)
                {
                    if (!ReferenceEquals(_world, _anchorWorld))
                    {
                        leaving = _world;
                        leftSim = _currentSim;
                        var held = _neighbours.FirstOrDefault(n => ReferenceEquals(n.Value.World, _anchorWorld)).Key;
                        if (held is not null)
                            _neighbours.Remove(held);   // kept, not disposed: it is the world again
                        _world = _anchorWorld;
                        _identity.Current = _world;
                    }
                }
                ClearNeighbours();
                if (leaving is not null)
                {
                    leaving.Dispose();
                    RaiseWorlds(RegionWorldChangeKind.Removed, leftSim!);
                }
            }
        }
        Advance(SessionStage.SimConnected);
        RegionInfo region;
        lock (_lock)
        {
            // Every SimConnected (login or a teleport landing) starts the region afresh from its own sources.
            // the login reply's size is the login region's: a later landing (a teleport) is sized by its own message, as Firestorm sizes
            // each region from the message that brings it (llviewermessage.cpp, process_teleport_finish)
            _loginSim ??= c.Sim;
            bool loginRegion = _loginSim.SamePlace(c.Sim);   // the same map spot on another grid is another region (Hypergrid)
            _connectSize = (c.SimSizeX, c.SimSizeY);
            region = RegionSize.AtConnect(c.Sim, c.SimSizeX, c.SimSizeY, loginRegion ? _loginSizeX : 0, loginRegion ? _loginSizeY : 0) with { Terrain = c.Terrain, WaterHeight = c.WaterHeight, IsEstateManager = c.IsEstateManager ?? false };
            _region = region;
            if (!ReferenceEquals(_currentSim, c.Sim) || _originSim is null)
                _originSim = c.Sim;   // a landing anchors the frame on the new region; a repeat connect keeps it
            _currentSim = c.Sim;
        }
        lock (_lock) { _coarse = []; _agentParcel = null; _parcelMusic = ""; }   // the new region starts its own list and says which parcel the agent is on
        if (ReferenceEquals(_originSim, c.Sim))
            World.SetOriginOffset(System.Numerics.Vector3.Zero);
        World.BeginRegion(c.Sim, region);
        World.ChooseAppearanceMode(c.Sim.Name, c.ServerBakedAppearance);
        LoginRegionKnown(c.ServerBakedAppearance);
        _log.Info("region.connect", CoreLog.F("region", c.Sim.Name), CoreLog.F("sim", $"{c.SimSizeX}x{c.SimSizeY}"),
            CoreLog.F("login-reply", $"{_loginSizeX}x{_loginSizeY}"), CoreLog.F("size", $"{region.SizeX}x{region.SizeY}"));
        RegionChanged?.Invoke(region);
        _communication?.Invoke(c);   // a teleport lands here
        if (_transport is IMapBlockRequester requester)
            _ = RetryMapBlockAsync(requester, c.Sim);
    }

    /// <summary>The waits before asking again for the map block, from connect: 5 s, then 10 s more
    /// (15 s after connect). Two retries at most; none once the size is authoritative, the region changed or the session ended.</summary>
    public static readonly System.Collections.Immutable.ImmutableArray<TimeSpan> MapBlockRetries = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private async Task RetryMapBlockAsync(IMapBlockRequester requester, SimRef sim)
    {
        CancellationToken token;
        lock (_lock)
        {
            if (_disposed) return;
            token = _life.Token;
        }
        int attempt = 0;
        foreach (var wait in MapBlockRetries)
        {
            try { await _opts.Delay(wait, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            lock (_lock)
            {
                if (_disposed || _ended || _region is not { } r || r.SizeAuthoritative || r.GridX != sim.GridX || r.GridY != sim.GridY)
                    return;
            }
            attempt++;
            _log.Info("region.map-block.retry", CoreLog.F("attempt", attempt), CoreLog.F("region", sim.Name));
            try { requester.RequestMapBlock(sim); }
            catch (Exception e) when (e is not OutOfMemoryException) { _log.Warn("region.map-block.retry-failed", CoreLog.F("error", e.GetType().Name)); return; }
        }
    }

    private void UpdateRegion(Func<RegionInfo, RegionInfo> change, string why)
    {
        RegionInfo? changed = null;
        string? previousName = null;
        lock (_lock)
        {
            if (_region is null)
                return;
            previousName = _region.Name;
            var next = change(_region);
            if (next != _region)
                changed = _region = next;
        }
        if (changed is null)
            return;
        World.UpdateRegion(changed);
        if (string.IsNullOrEmpty(previousName) && !string.IsNullOrEmpty(changed.Name))
            _log.Info("region.named", CoreLog.F("region", changed.Name), CoreLog.F("why", why));   // the name arrived after connect
        _log.Info("region.size", CoreLog.F("why", why), CoreLog.F("size", $"{changed.SizeX}x{changed.SizeY}"), CoreLog.F("authoritative", changed.SizeAuthoritative));
        RegionChanged?.Invoke(changed);
    }

    private void Advance(SessionStage stage)
    {
        lock (_lock)
        {
            if (stage <= _stage || _disposed)
                return;
            _stage = stage;
        }
        _log.Info("session.stage", CoreLog.F("stage", stage));
        if (stage == SessionStage.InWorld && _transport is IOfflineMessageRequester stored)
        {
            // the messages the grid kept while the agent was away, asked for once per login (stages only move forward)
            try { stored.RequestOfflineMessages(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log.Warn("im.offline-request-failed", CoreLog.F("error", ex.GetType().Name)); }
        }
        if (stage == SessionStage.InWorld && _transport is IGroupRequester groupList)
        {
            // the groups this agent is in, asked for once per login (the region usually sends them unasked; this makes sure)
            try { groupList.RequestGroups(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log.Warn("group.list-request-failed", CoreLog.F("error", ex.GetType().Name)); }
        }
        if (stage == SessionStage.InWorld && _transport is IGridFeaturesSource money)
        {
            // the balance, asked for once per login as the official viewer does (llstartup.cpp, sendMoneyBalanceRequest)
            try { money.RequestBalance(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log.Warn("money.balance-request-failed", CoreLog.F("error", ex.GetType().Name)); }
        }
        if (stage == SessionStage.InWorld && _transport is IMuteListRequester muteRequester)
            AskForMuteList(muteRequester);
        if (stage == SessionStage.InWorld)
            RebuildLookAtLogin();
        StageChanged?.Invoke(stage);
    }

    /// <summary>The block list, asked for once per login (stages only move forward). The grid may never answer (no service): after
    /// <see cref="MuteListWait"/> the list is called unavailable and holds this session's entries only.</summary>
    private void AskForMuteList(IMuteListRequester requester)
    {
        Mutes.SetState(Viewer.Core.Social.MuteListState.Requested);
        try { requester.RequestMuteList(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("mute.list-request-failed", CoreLog.F("error", ex.GetType().Name));
            Mutes.SetState(Viewer.Core.Social.MuteListState.Unavailable);
            return;
        }
        CancellationToken token;
        lock (_lock) token = _life.Token;
        var wait = _opts.MuteListDelay(MuteListWait, token);   // asked for here, so the wait is in place before the request can be answered
        _ = Task.Run(async () =>
        {
            try { await wait.ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (Mutes.State == Viewer.Core.Social.MuteListState.Requested)
            {
                _log.Info("mute.list-unavailable");
                Mutes.SetState(Viewer.Core.Social.MuteListState.Unavailable);
            }
        });
    }

    /// <summary>True when the block list dropped (or answered) <paramref name="e"/> and it goes no further. Names of people and objects stay out of the log.</summary>
    private bool MuteGate(TransportEvent e)
    {
        if (e is not (ChatReceived or InstantMessageReceived or FriendshipOffered or TeleportOffered or TeleportRequested or GroupNoticeReceived))
            return false;
        var verdict = Viewer.Core.Social.MuteFilter.Judge(e, Mutes, AvatarInView);
        if (verdict == Viewer.Core.Social.MuteVerdict.Pass)
            return false;
        Interlocked.Increment(ref _mutedDrops);
        _log.Info("mute.dropped", CoreLog.F("event", e.GetType().Name), CoreLog.F("verdict", verdict));
        if (verdict == Viewer.Core.Social.MuteVerdict.DeclineFriendship && e is FriendshipOffered o)
        {
            // the sender is told, as the official viewer does; an answer goes through the pipeline like any send
            var result = Actions.Submit(new FriendshipAnswerIntent(o.From, o.OfferId, false, ViaCapability: !o.Online));
            if (!result.Sent)
                _log.Warn("mute.decline-not-sent", CoreLog.F("code", result.Code ?? ""));
        }
        return true;
    }

    private bool AvatarInView(Guid agent) => agent != Guid.Empty && World.HasAvatar(agent);

    /// <summary>Blocks a resident, object, group or object name: the pipeline sends it (the grid's list takes it), and on success the local
    /// list holds it from then on. Returns what the list said about it first (a refusal sends nothing) or the pipeline's refusal.</summary>
    public BlockOutcome Block(MuteKind kind, Guid id, string name, uint flags = 0)
    {
        var entry = new MuteEntry(kind, id, (name ?? "").Trim(), flags);
        var check = Mutes.Check(entry);
        if (check is not (Viewer.Core.Social.MuteAddResult.Added or Viewer.Core.Social.MuteAddResult.Changed))
            return new BlockOutcome(check, null);
        var sent = Actions.Submit(new MuteIntent(kind, id, entry.Name, flags));
        if (sent.Sent)
            Mutes.Add(entry);
        return new BlockOutcome(sent.Sent ? check : Viewer.Core.Social.MuteAddResult.RefusedInvalid, sent);
    }

    /// <summary>Removes an entry: the pipeline sends it, and on success the local list drops it. False when the pipeline refused it or nothing matched.</summary>
    public bool Unblock(Guid id, string name)
    {
        string n = (name ?? "").Trim();
        if (!Mutes.Entries.Any(e => (id != Guid.Empty && e.Id == id) || (e.Kind == MuteKind.ByName && e.Name == n)))
            return false;
        var sent = Actions.Submit(new UnmuteIntent(id, n));
        if (!sent.Sent) return false;
        Mutes.Remove(id, n);
        return true;
    }

    private void End(SessionEndReason reason)
    {
        lock (_lock)
        {
            if (_ended)
                return;
            _ended = true;
            CancelGraceLocked();
        }
        Statistics?.Dispose();   // a session that ends without a logout (dropped, kicked) sends no more reports
        Statistics = null;
        RaiseEnded(reason);
    }

    private void RaiseEnded(SessionEndReason reason)
    {
        Action<SessionEndReason>? handler;
        lock (_lock)
        {
            if (EndReason != SessionEndReason.None || _disposed)
                return;
            _ended = true;
            EndReason = reason;
            handler = Ended;
        }
        Actions.EndAllHolds(HoldEnd.SessionEnded);
        _log.Info("session.ended", CoreLog.F("reason", reason));
        handler?.Invoke(reason);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _ended = true;
            if (EndReason == SessionEndReason.None)
                EndReason = SessionEndReason.Disposed;
            CancelGraceLocked();
        }
        Statistics?.Dispose();
        Statistics = null;
        _life.Cancel();
        Actions.Dispose();   // ends any hold still running
        _transport.Event -= OnTransportEvent;
        StageChanged = null;
        Ended = null;
        WorldEvent = null;
        RegionChanged = null;
        WorldsChanged = null;
        lock (_communicationLock) { _communication = null; _late.Clear(); }
        WorldsChanged = null;
        World.Dispose();
        Neighbour[] held;
        lock (_lock) held = _neighbours.Values.ToArray();
        foreach (var n in held)
            n.World.Dispose();
        _anchorWorld.Dispose();
        _transport.Dispose();
        _life.Dispose();
        _log.Info("session.disposed");
    }
}
