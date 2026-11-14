// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A neighbour the library connected from the establish-agent-communication message before the enable-simulator message learns its handle and size.</summary>
public class LateHandlesTests
{
    private sealed class Sim(IPEndPoint address, ulong handle = 0, uint size = 256)
    {
        public IPEndPoint Address { get; } = address;
        public ulong Handle { get; set; } = handle;
        public uint SizeX { get; set; } = size;
        public uint SizeY { get; set; } = size;
    }

    private static IPEndPoint At(int port) => new(IPAddress.Parse("192.0.2.10"), port);

    private static IReadOnlyList<Sim> Run(IEnumerable<Sim> held, params EnabledSim[] blocks) =>
        LateHandles.Apply(blocks, a => held.FirstOrDefault(s => s.Address.Equals(a)), s => s.Handle, (s, b) => { s.Handle = b.Handle; s.SizeX = b.SizeX; s.SizeY = b.SizeY; });

    [Fact]
    public void A_simulator_with_no_handle_gets_the_handle_and_size_the_message_states()
    {
        var sim = new Sim(At(9001));

        var changed = Run([sim], new EnabledSim(At(9001), 1099511628031744UL, 1024, 1024));

        Assert.Same(sim, Assert.Single(changed));
        Assert.Equal(1099511628031744UL, sim.Handle);
        Assert.Equal(1024u, sim.SizeX);
        Assert.Equal(1024u, sim.SizeY);
    }

    [Fact]
    public void A_simulator_that_has_its_handle_is_left_alone()
    {
        var sim = new Sim(At(9001), handle: 77, size: 512);

        var changed = Run([sim], new EnabledSim(At(9001), 99, 1024, 1024));

        Assert.Empty(changed);
        Assert.Equal(77UL, sim.Handle);
        Assert.Equal(512u, sim.SizeX);
    }

    [Fact]
    public void An_address_no_simulator_is_held_at_is_ignored_and_the_others_are_still_fixed()
    {
        var a = new Sim(At(9001));
        var b = new Sim(At(9002));

        var changed = Run([a, b], new EnabledSim(At(9005), 5, 256, 256), new EnabledSim(At(9002), 22, 1024, 1024));

        Assert.Same(b, Assert.Single(changed));
        Assert.Equal(0UL, a.Handle);
        Assert.Equal(22UL, b.Handle);
    }

    [Fact]
    public void A_message_that_states_no_handle_changes_nothing()
    {
        var sim = new Sim(At(9001));

        var changed = Run([sim], new EnabledSim(At(9001), 0, 1024, 1024));

        Assert.Empty(changed);
        Assert.Equal(256u, sim.SizeX);
    }
}
