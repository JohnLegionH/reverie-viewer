// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using LibreMetaverse;
using LibreMetaverse.Packets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The bandwidth throttle each region is told about. The library's own automatic throttle (its Settings.Agent.SendThrottle) sends the
/// AgentThrottle message to every region it connects to and, each time, rebuilds its outgoing send buckets and disposes the old ones; a
/// packet waiting on an old bucket then throws inside the library's single send loop, which ends that loop for the rest of the session,
/// so nothing more is sent to any region (no movement, no chat) while the session looks connected. The transport turns the automatic
/// throttle off and sends the same message itself to each region it connects to, as the official viewer does (llviewerthrottle.cpp,
/// LLViewerThrottle::sendToSim), without touching the send buckets. Offline; each test builds its own transport.
/// </summary>
public class LibraryThrottleTests
{
    private static LmvTransport NewTransport() =>
        new(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));

    [Fact]
    public void The_librarys_automatic_throttle_is_off_so_it_never_rebuilds_its_send_buckets_in_a_session()
    {
        using var t = NewTransport();
        Assert.False(t.Client.Settings.Agent.SendThrottle);
    }

    [Fact]
    public void The_throttle_message_carries_the_clients_rates_and_the_agents_circuit()
    {
        using var t = NewTransport();
        var packet = LmvTransport.ThrottlePacket(t.Client);
        Assert.NotNull(packet);
        Assert.Equal(t.Client.Throttle.ToBytes(), packet!.Throttle.Throttles);
        Assert.Equal(t.Client.Self.AgentID, packet.AgentData.AgentID);
        Assert.Equal(t.Client.Self.SessionID, packet.AgentData.SessionID);
        Assert.Equal(t.Client.Network.CircuitCode, packet.AgentData.CircuitCode);
        Assert.Equal(0u, packet.Throttle.GenCounter);
    }

    [Fact]
    public void Each_region_connected_is_sent_the_throttle_message_by_the_transport()
    {
        // the wiring: the region-connected handler sends the message (the library no longer does)
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "core", "Viewer.Core", "Protocol", "LmvTransport.cs"));
        int at = src.IndexOf("private void OnSimConnected(", StringComparison.Ordinal);
        Assert.True(at >= 0);
        int end = src.IndexOf("\n    }", at, StringComparison.Ordinal);
        Assert.Contains("SendThrottle(e.Simulator)", src[at..end]);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Viewer.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
