// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Runtime.CompilerServices;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// ADR-006 multi-session invariant slice: two sessions in one process with overlapping ids and different
/// permissions; one disposes; the survivor is unaffected and nothing of the first remains (callbacks, state, entries).
/// The asset-authorisation half of the ADR's first extraction test needs the fetch path.
/// </summary>
public class IsolationTests
{
    // Overlapping on purpose: the same agent id, the same region handle and name in both sessions.
    private static readonly Guid Agent = Guid.Parse("0f0f0f0f-0000-0000-0000-000000000001");
    private const ulong Handle = 256000UL << 32 | 256000UL;

    [Fact]
    public async Task Disposing_one_session_leaves_the_other_untouched_and_nothing_of_it_remains()
    {
        var sinkA = new ListSink();
        var sinkB = new ListSink();
        var tA = new FakeTransport { OnLogin = (_, _) => Task.FromResult(FakeTransport.Ok(Agent, 1024, 1024)) };
        var tB = new FakeTransport { OnLogin = (_, _) => Task.FromResult(FakeTransport.Ok(Agent, 1024, 1024)) };

        // Different permissions: A may reach internal addresses only through an exemption, B is off.
        var a = new ViewerSession(new SessionOptions { LoginUri = "http://127.0.0.1:8002/", Outbound = new() { Policy = "strict", AllowedInternalEndpoints = ["127.0.0.1"] } }, tA, new CoreLog(sinkA, "a"));
        using var b = new ViewerSession(new SessionOptions { LoginUri = "http://127.0.0.1:8002/" }, tB, new CoreLog(sinkB, "b"));

        var aEvents = 0;
        var bEvents = new List<string>();
        a.WorldEvent += _ => Interlocked.Increment(ref aEvents);
        b.WorldEvent += e => { lock (bEvents) bEvents.Add(e.Kind); };

        Assert.True((await a.LoginAsync(new("Same", "Name", new Secret("pw-a", SensitiveKind.PasswordDigest)), CancellationToken.None)).Ok);
        Assert.True((await b.LoginAsync(new("Same", "Name", new Secret("pw-b", SensitiveKind.PasswordDigest)), CancellationToken.None)).Ok);
        var simA = new SimRef(Handle, "Shared");
        var simB = new SimRef(Handle, "Shared");
        tA.DriveToInWorld(simA);
        tB.DriveToInWorld(simB);
        tA.Raise(new LandPatch(simA, 70, 0, 16));    // grows A's region only
        Assert.Equal(1024 + 256, a.Region!.SizeX);
        Assert.Equal(1024, b.Region!.SizeX);
        Assert.True(a.Outbound.Strict);
        Assert.False(b.Outbound.Strict);
        Assert.NotEqual(a.LogTag, b.LogTag);
        string aTag = a.LogTag, bTag = b.LogTag;

        var weakA = DisposeAndForget(a, tA);
        a = null!;   // the async state machine would otherwise keep the local alive

        // Nothing of A remains: its transport has no subscribers and is disposed; its events reach nobody.
        Assert.True(tA.Disposed);
        Assert.Equal(0, tA.Subscribers);
        tA.Raise(new SimWorldEvent(simA, "object.update"));
        Assert.Equal(0, aEvents);
        GC.KeepAlive(weakA);   // reachability after disposal: A_disposed_session_becomes_unreachable_while_its_transport_lives_on

        // B is unaffected: same stage, same region, still receiving, still able to log out normally.
        Assert.Equal(SessionStage.InWorld, b.Stage);
        Assert.Equal(1024, b.Region!.SizeX);
        tB.Raise(new SimWorldEvent(simB, "object.update"));
        Assert.Equal(new[] { "object.update" }, bEvents);
        Assert.Equal(1, tB.Subscribers);
        Assert.False(b.IsEnded);

        // Logs never cross: A's entries carry only A's source, B's only B's.
        Assert.All(sinkA.Entries, e => Assert.Equal("session." + aTag, e.Source));
        Assert.All(sinkB.Entries, e => Assert.Equal("session." + bTag, e.Source));
        Assert.DoesNotContain(bTag, sinkA.Text);
        Assert.DoesNotContain(aTag, sinkB.Text);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DisposeAndForget(ViewerSession s, FakeTransport t)
    {
        var weak = new WeakReference(s);
        s.Dispose();
        return weak;
    }

    [Fact]
    public async Task Disposing_one_session_cancels_its_login_and_not_the_others()
    {
        var gateA = new TaskCompletionSource();
        var gateB = new TaskCompletionSource<TransportLoginResult>();
        var tA = new FakeTransport { OnLogin = async (_, ct) => { gateA.SetResult(); await Task.Delay(Timeout.Infinite, ct); return TransportLoginResult.Failed("x", null); } };
        var tB = new FakeTransport { OnLogin = (_, _) => gateB.Task };
        var a = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example/" }, tA, new CoreLog(new ListSink(), "a"));
        using var b = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example/" }, tB, new CoreLog(new ListSink(), "b"));

        var la = a.LoginAsync(new("A", "A", new Secret("p", SensitiveKind.PasswordDigest)), CancellationToken.None);
        var lb = b.LoginAsync(new("B", "B", new Secret("p", SensitiveKind.PasswordDigest)), CancellationToken.None);
        await gateA.Task;

        a.Dispose();
        Assert.Equal("cancelled", (await la.WaitAsync(TimeSpan.FromSeconds(60))).Code);
        Assert.False(tB.LastLoginToken.IsCancellationRequested);

        gateB.SetResult(FakeTransport.Ok(Agent));
        Assert.True((await lb.WaitAsync(TimeSpan.FromSeconds(60))).Ok);
    }

    /// <summary>
    /// Map hazard 3: LibreMetaverse's own statics were never audited. Two real GridClients in one process: settings are
    /// per client, and disposing one leaves the other's handlers and settings in place and frees the first.
    /// </summary>
    [Fact]
    public void Two_LibreMetaverse_transports_in_one_process_are_independent()
    {
        var log = new CoreLog(new ListSink(), "test");
        var strict = new OutboundPolicy(new OutboundOptions { Policy = "strict" });
        var off = new OutboundPolicy(new OutboundOptions());
        var a = new LmvTransport(strict, log);
        using var b = new LmvTransport(off, log);
        Assert.NotSame(a.Client, b.Client);
        Assert.NotSame(a.Client.Settings, b.Client.Settings);

        // A setting changed on one client does not reach the other (no library-level static behind it).
        a.Client.Settings.Agent.SendAppearance = true;
        Assert.False(b.Client.Settings.Agent.SendAppearance);
        a.Client.Settings.Agent.SendAppearance = false;
        // Strict mode replaced A's caps HTTP client only.
        Assert.NotSame(a.Client.HttpCapsClient, b.Client.HttpCapsClient);

        DisposeTransport(a);
        a = null!;
        Assert.Equal(0, b.MappedSimulators);
        Assert.False(b.Client.Settings.Agent.SendAppearance);
        Assert.NotNull(b.Client.Network);
    }

    /// <summary>
    /// Saw a disposed session and transport survive forced collections. The root was the
    /// test's own stack frame: an object created in the test method is spilled to a JIT temporary that unoptimised code
    /// (Debug, and tier-0 in Release, which is what a test method runs at on its only call) reports as live until the
    /// method returns. Measured : the same objects built inside a non-inlined helper are collected in Debug and
    /// Release, in the test host and in a separate process; no static in LibreMetaverse or Viewer.Core references a
    /// GridClient, Simulator, NetworkManager or any of our types. So every object under test is built in a helper, and
    /// what the test keeps alive (the transport, the GridClient) is exactly what would root the object if disposal
    /// left a subscription behind. The negative controls prove the check can fail.
    /// </summary>
    [Fact]
    public void A_disposed_session_becomes_unreachable_while_its_transport_lives_on()
    {
        var transport = new FakeTransport();
        var weak = BuildSession(transport, dispose: true);
        // Dispose cancels the session's background waits, but a continuation that is already queued unwinds on the thread pool and holds the
        // session until it has run, later on a busy machine: collect until the session is gone, with a cap for a session that never goes
        // (the undisposed control below shows a live subscription is still seen)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Collect();
        while (weak.IsAlive && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Yield();
            Collect();
        }
        Assert.False(weak.IsAlive, "a disposed session is still reachable");
        Assert.Equal(0, transport.Subscribers);
        GC.KeepAlive(transport);
    }

    [Fact]
    public void Negative_control_an_undisposed_session_is_kept_alive_by_its_transport()
    {
        var transport = new FakeTransport();
        var weak = BuildSession(transport, dispose: false);
        Collect();
        Assert.True(weak.IsAlive, "the reachability check cannot see a live subscription");
        GC.KeepAlive(transport);
    }

    [Fact]
    public void A_disposed_transport_becomes_unreachable_while_its_GridClient_lives_on()
    {
        foreach (var policy in new[] { "off", "strict" })
        {
            var (weak, client) = BuildTransport(policy, dispose: true);
            Collect();
            Assert.False(weak.IsAlive, $"a disposed {policy} transport is still reachable from its GridClient");
            GC.KeepAlive(client);
        }
    }

    [Fact]
    public void Negative_control_an_undisposed_transport_is_kept_alive_by_its_GridClient()
    {
        var (weak, client) = BuildTransport("off", dispose: false);
        Collect();
        Assert.True(weak.IsAlive, "the reachability check cannot see the GridClient's handlers");
        GC.KeepAlive(client);
        ((IDisposable)client).Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BuildSession(FakeTransport transport, bool dispose)
    {
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example/" }, transport, new CoreLog(new ListSink(), "gc"));
        Assert.True(s.LoginAsync(new("A", "B", new Secret("p", SensitiveKind.PasswordDigest)), CancellationToken.None).GetAwaiter().GetResult().Ok);
        transport.DriveToInWorld(new SimRef(Handle, "Shared"));
        s.WorldEvent += _ => { };
        if (dispose)
            s.Dispose();
        return new WeakReference(s);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Transport, object Client) BuildTransport(string policy, bool dispose)
    {
        var t = new LmvTransport(new OutboundPolicy(new OutboundOptions { Policy = policy }), new CoreLog(new ListSink(), "gc"));
        t.Event += _ => { };
        object client = t.Client;
        if (dispose)
            t.Dispose();
        return (new WeakReference(t), client);
    }

    private static void Collect()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DisposeTransport(LmvTransport t)
    {
        var weak = new WeakReference(t);
        t.Dispose();
        Assert.Equal(0, t.MappedSimulators);
        return weak;
    }
}
