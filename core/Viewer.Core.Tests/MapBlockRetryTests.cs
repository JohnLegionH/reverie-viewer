// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The map block carries the region's authoritative size and map image. It is asked for once
/// over UDP at connect, and in one of three logins none came. While the size is not authoritative the session asks
/// again, a bounded number of times: 5 s and 15 s after connect.</summary>
public sealed class MapBlockRetryTests
{
    private static readonly SimRef Sim = new(1000UL * 256 << 32 | 1000UL * 256, "Home");
    private static LoginCredentials Creds => new("Test", "Resident", new Secret("correct horse", SensitiveKind.PasswordDigest));

    /// <summary>The fake transport, plus the map block request the session may repeat.</summary>
    private sealed class MapFake : ISessionTransport, IMapBlockRequester
    {
        public readonly FakeTransport Inner = new();
        public List<SimRef> Requests { get; } = new();
        public event Action<TransportEvent>? Event { add => Inner.Event += value; remove => Inner.Event -= value; }
        public Task<TransportLoginResult> LoginAsync(TransportLoginRequest request, CancellationToken ct) => Inner.LoginAsync(request, ct);
        public Task LogoutAsync(CancellationToken ct) => Inner.LogoutAsync(ct);
        public SimRef? CurrentSim => Inner.CurrentSim;
        public int ConnectedSimulatorCount => Inner.ConnectedSimulatorCount;
        public void SetDrawDistance(float metres) => Inner.SetDrawDistance(metres);
        public void RequestMapBlock(SimRef sim) { lock (Requests) Requests.Add(sim); }
        public int Count { get { lock (Requests) return Requests.Count; } }
        public void Dispose() => Inner.Dispose();
    }

    private static async Task<(ViewerSession S, MapFake T, ManualDelay D, ListSink Log)> InWorld()
    {
        var d = new ManualDelay();
        var t = new MapFake();
        var sink = new ListSink();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/", Delay = d.Delay }, t, new CoreLog(sink, "test"));
        await s.LoginAsync(Creds, CancellationToken.None);
        t.Inner.DriveToInWorld(Sim);
        return (s, t, d, sink);
    }

    [Fact]
    public async Task A_lost_map_block_is_asked_for_again_after_5_s_and_the_answer_makes_the_size_authoritative()
    {
        var (s, t, d, log) = await InWorld();
        using var _ = s;
        Assert.False(s.Region!.SizeAuthoritative);
        Wait.Until(() => d.Requested.Count == 1, "first retry scheduled");
        Assert.Equal(TimeSpan.FromSeconds(5), d.Requested[0]);
        d.ElapseLast();   // the first request was dropped: 5 s pass without an answer
        Wait.Until(() => t.Count == 1, "map block asked for again");
        Assert.Same(Sim, t.Requests[0]);
        Wait.Until(() => d.Requested.Count == 2, "second retry scheduled");
        Assert.Equal(TimeSpan.FromSeconds(10), d.Requested[1]);   // 15 s after connect
        t.Inner.Raise(new MapBlockSize(Sim.GridX, Sim.GridY, 512, 512, "Home", Guid.NewGuid()));
        Assert.True(s.Region!.SizeAuthoritative);
        d.ElapseLast();
        await Task.Delay(100);
        Assert.Equal(1, t.Count);   // authoritative now: no third request
        Assert.Contains("region.map-block.retry", log.Text);
    }

    [Fact]
    public async Task At_most_two_retries_when_no_map_block_ever_comes()
    {
        var (s, t, d, _) = await InWorld();
        using var _s = s;
        Wait.Until(() => d.Requested.Count == 1, "first retry scheduled");
        d.ElapseLast();
        Wait.Until(() => d.Requested.Count == 2, "second retry scheduled");
        d.ElapseLast();
        Wait.Until(() => t.Count == 2, "two retries");
        await Task.Delay(100);
        Assert.Equal(2, d.Requested.Count);
        Assert.Equal(2, t.Count);
    }

    [Fact]
    public async Task A_map_block_that_arrives_in_time_means_no_retry()
    {
        var (s, t, d, _) = await InWorld();
        using var _s = s;
        t.Inner.Raise(new MapBlockSize(Sim.GridX, Sim.GridY, 256, 256, "Home"));
        Wait.Until(() => d.Requested.Count == 1, "first retry scheduled");
        d.ElapseLast();
        await Task.Delay(100);
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public async Task Logout_stops_the_retries()
    {
        var (s, t, d, _) = await InWorld();
        Wait.Until(() => d.Requested.Count == 1, "first retry scheduled");
        s.Dispose();
        Assert.True(d.LastWasCancelled);
        Assert.Equal(0, t.Count);
    }
}
