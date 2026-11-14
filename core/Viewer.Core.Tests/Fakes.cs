// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Core.Tests;

/// <summary>Collects log entries in memory.</summary>
internal sealed class ListSink : ILogSink
{
    private readonly List<LogEntry> _entries = new();

    public void Write(LogEntry entry)
    {
        lock (_entries) _entries.Add(entry);
    }

    public IReadOnlyList<LogEntry> Entries { get { lock (_entries) return _entries.ToList(); } }

    public string Text => string.Join("\n", Entries.Select(e => e.Format()));
}

/// <summary>The fake protocol layer: the session core is driven through it with no grid and no LibreMetaverse.</summary>
internal sealed class FakeTransport : ISessionTransport, ICommandTransport, IAgentNameRequester, IOfflineMessageRequester, IGroupRequester, IMapRequester, IMapTileTransport, Viewer.Core.Inventory.IInventoryRequester, IMuteListRequester, IInboundGate, IChildCircuitNudger, Viewer.Core.Appearance.IWearableTransport
{
    /// <summary>The wearable assets the fake can read (by asset id) and the item facts it reports.</summary>
    public Dictionary<Guid, byte[]> WearableAssets { get; } = new();
    public Dictionary<Guid, Viewer.Core.Appearance.WearableItemInfo> WearableItems { get; } = new();
    public Task<Viewer.Core.Assets.AssetFetchResult> FetchWearableAsync(Guid assetId, int assetType, CancellationToken ct) =>
        Task.FromResult(WearableAssets.TryGetValue(assetId, out var b) ? Viewer.Core.Assets.AssetFetchResult.Ok(b) : Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.NotFound));
    public Viewer.Core.Appearance.WearableItemInfo? ItemInfo(Guid itemId) => WearableItems.GetValueOrDefault(itemId);
    public Dictionary<Guid, Guid> TextureAssets { get; } = new();
    public Guid TextureAssetOf(Guid itemId) => TextureAssets.GetValueOrDefault(itemId);

    /// <summary>How many times the fault hook was asked to drop inbound packets, and what it answers.</summary>
    public int DropCalls;
    public bool DropResult = true;
    public bool CloseInbound() { Interlocked.Increment(ref DropCalls); return DropResult; }

    /// <summary>How many times the block list was asked for.</summary>
    public int MuteRequests;
    public void RequestMuteList() => Interlocked.Increment(ref MuteRequests);

    /// <summary>Every child circuit the session asked to be prodded.</summary>
    public List<SimRef> Nudges { get; } = new();
    public void Nudge(SimRef sim) { lock (Nudges) Nudges.Add(sim); }

    /// <summary>Every map read the session asked for.</summary>
    public List<(int MinX, int MinY, int MaxX, int MaxY)> BlockRequests { get; } = new();
    public List<string> NameSearches { get; } = new();
    public List<Guid> FriendLocations { get; } = new();
    public List<(int Level, int X, int Y)> TileRequests { get; } = new();
    public Func<int, int, int, Viewer.Core.Assets.AssetFetchResult> OnTile { get; set; } = (_, _, _) => Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.NotFound);
    public void RequestMapBlocks(int minX, int minY, int maxX, int maxY) { lock (BlockRequests) BlockRequests.Add((minX, minY, maxX, maxY)); }
    public void RequestRegionNamed(string name) { lock (NameSearches) NameSearches.Add(name); }
    public void RequestFriendLocation(Guid friend) { lock (FriendLocations) FriendLocations.Add(friend); }
    public Task<Viewer.Core.Assets.AssetFetchResult> FetchMapTileAsync(int level, int gridX, int gridY, CancellationToken ct)
    {
        lock (TileRequests) TileRequests.Add((level, gridX, gridY));
        return Task.FromResult(OnTile(level, gridX, gridY));
    }

    /// <summary>How many times the group list was asked for, and the groups whose titles were asked for.</summary>
    public int GroupRequests;
    public List<Guid> TitleRequests { get; } = new();
    public void RequestGroups() => Interlocked.Increment(ref GroupRequests);
    public void RequestGroupTitles(Guid group) { lock (TitleRequests) TitleRequests.Add(group); }

    /// <summary>Every name request, in order (each a batch of agent ids).</summary>
    public List<IReadOnlyList<Guid>> NameRequests { get; } = new();
    public void RequestAgentNames(IReadOnlyList<Guid> agents) { lock (NameRequests) NameRequests.Add(agents.ToArray()); }

    /// <summary>Every folder the session asked the grid for, in order (folder, owner).</summary>
    public List<(Guid Folder, Guid Owner)> FolderRequests { get; } = new();
    public void RequestFolderContents(Guid folderId, Guid ownerId) { lock (FolderRequests) FolderRequests.Add((folderId, ownerId)); }

    /// <summary>How many times the stored instant messages were asked for.</summary>
    public int OfflineRequests;
    public void RequestOfflineMessages() => Interlocked.Increment(ref OfflineRequests);

    public event Action<TransportEvent>? Event;

    /// <summary>Every command the pipeline handed over, in order.</summary>
    public List<TransportCommand> Commands { get; } = new();
    public int ExecuteCalls;

    /// <summary>What the protocol layer answers a command with (null: handed over); it may throw.</summary>
    public Func<TransportCommand, CommandDispatch>? OnCommand { get; set; }

    public CommandDispatch Execute(TransportCommand command)
    {
        Interlocked.Increment(ref ExecuteCalls);
        lock (Commands) Commands.Add(command);
        return OnCommand?.Invoke(command) ?? CommandDispatch.Handed;
    }

    public int Subscribers => Event?.GetInvocationList().Length ?? 0;
    public List<TransportLoginRequest> Logins { get; } = new();
    public int Logouts;
    public bool Disposed;
    public CancellationToken LastLoginToken;

    public SimRef? CurrentSim { get; set; }
    public int ConnectedSimulatorCount { get; set; }

    public Func<TransportLoginRequest, CancellationToken, Task<TransportLoginResult>> OnLogin { get; set; } =
        (_, _) => Task.FromResult(Ok(Guid.Parse("11111111-2222-3333-4444-555555555555")));

    public static TransportLoginResult Ok(Guid agent, int sizeX = 0, int sizeY = 0, string sessionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") =>
        new(true, null, null, agent, new Secret(sessionId, SensitiveKind.SessionId), sizeX, sizeY);

    public Task<TransportLoginResult> LoginAsync(TransportLoginRequest request, CancellationToken ct)
    {
        Logins.Add(request);
        LastLoginToken = ct;
        return OnLogin(request, ct);
    }

    /// <summary>The grid's side of a logout (null: it answers at once).</summary>
    public Func<CancellationToken, Task>? OnLogout { get; set; }
    public CancellationToken LastLogoutToken;

    public Task LogoutAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Logouts);
        LastLogoutToken = ct;
        return OnLogout?.Invoke(ct) ?? Task.CompletedTask;
    }

    public void Raise(TransportEvent e) => Event?.Invoke(e);

    public List<float> DrawDistances { get; } = new();

    public void SetDrawDistance(float metres) => DrawDistances.Add(metres);

    public void Dispose() => Disposed = true;

    /// <summary>The login path to InWorld: reply accepted, sim connected, event queue + movement complete.</summary>
    public void DriveToInWorld(SimRef sim, int simSizeX = 256, int simSizeY = 256)
    {
        Raise(new LoginProgressed(LoginPhase.ConnectingToSim, "Connecting to region"));
        ConnectedSimulatorCount = 1;
        Raise(new SimConnected(sim, simSizeX, simSizeY));
        CurrentSim = sim;
        Raise(new EventQueueRunning(sim));
        Raise(new MovementComplete(sim));
    }
}

/// <summary>A grace delay the test completes by hand.</summary>
internal sealed class ManualDelay
{
    private readonly List<(TimeSpan Wait, TaskCompletionSource Tcs)> _pending = new();

    public IReadOnlyList<TimeSpan> Requested { get { lock (_pending) return _pending.Select(p => p.Wait).ToList(); } }

    private readonly List<(int Count, TaskCompletionSource Tcs)> _waiters = new();

    public Task Delay(TimeSpan wait, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        lock (_pending)
        {
            _pending.Add((wait, tcs));
            foreach (var w in _waiters.Where(w => w.Count <= _pending.Count).ToList())
            {
                w.Tcs.TrySetResult();
                _waiters.Remove(w);
            }
        }
        return tcs.Task;
    }

    /// <summary>Completes once at least <paramref name="count"/> delays have been asked for (an explicit signal in place of polling <see cref="Requested"/>).</summary>
    public Task WhenRequested(int count)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            if (_pending.Count >= count) tcs.SetResult();
            else _waiters.Add((count, tcs));
        }
        return tcs.Task;
    }

    public void ElapseLast()
    {
        TaskCompletionSource tcs;
        lock (_pending) tcs = _pending[^1].Tcs;
        tcs.TrySetResult();
    }

    public bool LastWasCancelled { get { lock (_pending) return _pending[^1].Tcs.Task.IsCanceled; } }
}

internal static class Wait
{
    public const int DefaultTimeoutMs = 60_000;

    public static void Until(Func<bool> condition, string what, int timeoutMs = DefaultTimeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("timed out waiting for: " + what);
            Thread.Sleep(5);
        }
    }
}
