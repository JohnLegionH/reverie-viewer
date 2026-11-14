// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using System.Net.Sockets;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A neighbour the region announces (the enable-simulator message) gets its circuit opened at once, as the official viewer does on
/// receiving that message (llworld.cpp, process_enable_simulator: it enables the circuit, adds the region and sends UseCircuitCode in the
/// same handler), not when the agent reaches the border. The adapter leaves this to the library with child circuits switched on.</summary>
public class NeighbourAnnounceTests
{
    // UseCircuitCode is a "Low" frequency message, number 3: its id is FF FF 00 03 right after the packet header
    private static readonly byte[] UseCircuitCodeId = [0xFF, 0xFF, 0x00, 0x03];

    private static bool IsUseCircuitCode(byte[] datagram)
    {
        // header: flags (1), sequence (4), extra-header length (1) and that many bytes; then the message id
        if (datagram.Length < 10) return false;
        int at = 6 + datagram[5];
        return datagram.Length >= at + 4 && datagram.AsSpan(at, 4).SequenceEqual(UseCircuitCodeId);
    }

    private static EnableSimulatorMessage Announce(IPEndPoint to, ulong handle) => new()
    {
        Simulators = [new EnableSimulatorMessage.SimulatorInfoBlock { IP = to.Address, Port = to.Port, RegionHandle = handle, RegionSizeX = 256, RegionSizeY = 256 }],
    };

    private static void Deliver(LmvTransport t, IPEndPoint to, ulong handle)
    {
        var handler = typeof(NetworkManager).GetMethod("EnableSimulatorHandler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        handler.Invoke(t.Client.Network, ["EnableSimulator", Announce(to, handle), null]);
    }

    private static async Task<bool> GetsUseCircuitCodeAsync(bool neighbours)
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var to = (IPEndPoint)listener.Client.LocalEndPoint!;
        using var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        LmvTransport.ApplyNeighbours(t.Client, neighbours);
        // the library's handler blocks until the new circuit's region handshake comes (a minute without one): it runs off this thread
        _ = Task.Run(() => { try { Deliver(t, to, 1099511628032768UL + 256); } catch (ObjectDisposedException) { } });
        // the datagram is waited for (it ends the wait the moment it arrives); only the neighbours-off case, which proves that nothing comes, is a fixed window
        var deadline = DateTime.UtcNow.AddSeconds(neighbours ? 60 : 5);
        var read = listener.ReceiveAsync();
        while (DateTime.UtcNow < deadline)
        {
            if (await Task.WhenAny(read, Task.Delay(500)) != read)
                continue;
            if (IsUseCircuitCode(read.Result.Buffer))
                return true;
            read = listener.ReceiveAsync();   // another datagram (a keep-alive): keep reading
        }
        return false;
    }

    [Fact]
    public async Task With_child_circuits_on_an_announced_neighbour_gets_the_circuit_code_without_any_crossing()
    {
        Assert.True(await GetsUseCircuitCodeAsync(neighbours: true));
    }

    [Fact]
    public async Task With_child_circuits_off_an_announced_neighbour_gets_nothing()
    {
        Assert.False(await GetsUseCircuitCodeAsync(neighbours: false));
    }
}
