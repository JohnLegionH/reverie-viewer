// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using System.Net.Http;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The library's own crossing, forced, with the seed capability failing every time. In library 3.1.6 the seed capability's retry called
/// itself until the process died in a stack overflow (which no test can survive). With the pinned build (a bounded retry that then
/// disconnects the simulator) the process stays up and the session is told the simulator is gone.
/// </summary>
public class SeedFailureTests
{
    private sealed class FailingHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new HttpRequestException("seed capability unreachable");
        }
    }

    private static void SetConnected(GridClient client)
    {
        var prop = typeof(NetworkManager).GetProperty("Connected")!;
        prop.GetSetMethod(true)!.Invoke(client.Network, [true]);
    }

    /// <summary>
    /// A seed capability that never answers must not crash the process (the 3.1.6 library called itself from its error
    /// handler until the stack overflowed). LibreMetaverse 3.3.0 retries on a growing delay, six attempts in all, then logs that
    /// it gives up and leaves the simulator connected without capabilities; it does not raise a disconnect.
    /// </summary>
    [Fact]
    public async Task A_simulator_whose_seed_capability_always_fails_is_asked_a_bounded_number_of_times_and_the_process_stays_up()
    {
        using var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        var client = t.Client;
        var handler = new FailingHandler();
        client.HttpCapsClient = new HttpCapsClient(handler);
        SetConnected(client);

        int disconnects = 0;
        t.Event += e => { if (e is Viewer.Core.Session.SimDisconnected) Interlocked.Increment(ref disconnects); };

        var handle = 1001UL * 256 << 32 | 1000UL * 256;
        var home = new Simulator(client, new IPEndPoint(IPAddress.Loopback, 9), 1000UL * 256 << 32 | 1000UL * 256);
        var dest = new Simulator(client, new IPEndPoint(IPAddress.Loopback, 10), handle);
        lock (client.Network.Simulators) { client.Network.Simulators.Add(home); client.Network.Simulators.Add(dest); }
        client.Network.CurrentSim = dest;

        dest.SetSeedCaps(new Uri("http://seed.example.org/seed"), true);

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (Volatile.Read(ref handler.Calls) < 6 && DateTime.UtcNow < deadline) await Task.Delay(250);
        Assert.Equal(6, handler.Calls);                       // the allowed number of attempts
        await Task.Delay(TimeSpan.FromSeconds(20));            // a window in which no further attempt may come
        Assert.Equal(6, handler.Calls);                       // then it stops
        Assert.Equal(0, Volatile.Read(ref disconnects));       // 3.3.0 leaves the simulator connected
    }
}
