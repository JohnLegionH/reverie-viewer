// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Hazard 2 / ADR-006: shared asset bytes never bypass a session's authorisation, and one session's failure is
/// never served to another. The gateway's 60 s negative cache was keyed by asset id alone and shared by every session.
/// </summary>
public class SessionAssetsTests
{
    private static readonly Guid Asset = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly byte[] Bytes = [1, 2, 3];

    private static Func<Guid, CancellationToken, Task<AssetFetchResult>> Fails(Action? onCall = null) =>
        (_, _) => { onCall?.Invoke(); return Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Failed)); };

    private static Func<Guid, CancellationToken, Task<AssetFetchResult>> Serves(Action? onCall = null) =>
        (_, _) => { onCall?.Invoke(); return Task.FromResult(AssetFetchResult.Ok(Bytes)); };

    [Fact]
    public async Task Session_As_failed_fetch_does_not_fail_session_Bs_fetch_of_the_same_asset()
    {
        var store = new SharedAssetStore();
        int bFetches = 0;
        using var a = new SessionAssets(store, _ => true, Fails());                          // A's agent cannot get it
        using var b = new SessionAssets(store, _ => true, Serves(() => bFetches++));         // B's can

        Assert.Equal(AssetFetchStatus.Failed, (await a.GetAsync(Asset, CancellationToken.None)).Status);
        Assert.Equal(AssetFetchStatus.Failed, (await a.GetAsync(Asset, CancellationToken.None)).Status);   // remembered for A
        Assert.Equal(1, a.RememberedFailures);

        var rb = await b.GetAsync(Asset, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Ok, rb.Status);
        Assert.Equal(Bytes, rb.Bytes.ToArray());
        Assert.Equal(1, bFetches);                     // B fetched through its own agent
        Assert.Equal(0, b.RememberedFailures);
    }

    /// <summary>
    /// Negative control: the same scenario with ONE negative cache handed to both sessions (the gateway's design) makes
    /// B fail, so the test above would catch a shared negative cache.
    /// </summary>
    [Fact]
    public async Task Negative_control_a_shared_negative_cache_serves_As_failure_to_B()
    {
        var store = new SharedAssetStore();
        var shared = new NegativeCache(TimeSpan.FromSeconds(60), () => DateTimeOffset.UtcNow);
        int bFetches = 0;
        using var a = new SessionAssets(store, _ => true, Fails(), shared);
        using var b = new SessionAssets(store, _ => true, Serves(() => bFetches++), shared);

        await a.GetAsync(Asset, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Failed, (await b.GetAsync(Asset, CancellationToken.None)).Status);
        Assert.Equal(0, bFetches);
    }

    [Fact]
    public async Task A_hit_in_the_shared_store_never_bypasses_the_requesting_sessions_authorisation()
    {
        var store = new SharedAssetStore();
        using var a = new SessionAssets(store, _ => true, Serves());
        using var b = new SessionAssets(store, id => id != Asset, Serves());

        Assert.Equal(AssetFetchStatus.Ok, (await a.GetAsync(Asset, CancellationToken.None)).Status);
        Assert.Equal(1, store.Count);
        var rb = await b.GetAsync(Asset, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Denied, rb.Status);
        Assert.True(rb.Bytes.IsEmpty);
    }

    [Fact]
    public async Task A_remembered_failure_expires_after_its_ttl()
    {
        var now = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
        int calls = 0;
        using var a = new SessionAssets(new SharedAssetStore(), _ => true, Fails(() => calls++), TimeSpan.FromSeconds(60), () => now);
        await a.GetAsync(Asset, CancellationToken.None);
        await a.GetAsync(Asset, CancellationToken.None);
        Assert.Equal(1, calls);
        now = now.AddSeconds(61);
        await a.GetAsync(Asset, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    /// <summary>ADR-006 disposal hazard 2c: A's disconnect cancels A's in-flight fetch and never B's.</summary>
    [Fact]
    public async Task Disposing_one_session_cancels_its_in_flight_fetch_and_not_the_others()
    {
        var store = new SharedAssetStore();
        var aStarted = new TaskCompletionSource();
        var bGate = new TaskCompletionSource<AssetFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int bFetches = 0;
        var a = new SessionAssets(store, _ => true, async (_, ct) => { aStarted.SetResult(); await Task.Delay(Timeout.Infinite, ct); return AssetFetchResult.Of(AssetFetchStatus.Failed); });
        using var b = new SessionAssets(store, _ => true, (_, _) => { Interlocked.Increment(ref bFetches); return bGate.Task; });

        var ra = a.GetAsync(Asset, CancellationToken.None);
        await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var rb = b.GetAsync(Asset, CancellationToken.None);   // does not join A's fetch
        a.Dispose();

        Assert.Equal(AssetFetchStatus.Cancelled, (await ra.WaitAsync(TimeSpan.FromSeconds(60))).Status);
        Assert.Equal(AssetFetchStatus.Cancelled, (await a.GetAsync(Asset, CancellationToken.None)).Status);
        bGate.SetResult(AssetFetchResult.Ok(Bytes));
        Assert.Equal(AssetFetchStatus.Ok, (await rb.WaitAsync(TimeSpan.FromSeconds(60))).Status);
        Assert.Equal(1, bFetches);
    }

    [Fact]
    public async Task Concurrent_requests_within_one_session_share_one_fetch()
    {
        var gate = new TaskCompletionSource<AssetFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var a = new SessionAssets(new SharedAssetStore(), _ => true, (_, _) => { Interlocked.Increment(ref calls); return gate.Task; });
        var r1 = a.GetAsync(Asset, CancellationToken.None);
        var r2 = a.GetAsync(Asset, CancellationToken.None);
        gate.SetResult(AssetFetchResult.Ok(Bytes));
        Assert.Equal(AssetFetchStatus.Ok, (await r1).Status);
        Assert.Equal(AssetFetchStatus.Ok, (await r2).Status);
        Assert.Equal(1, calls);
    }
}
