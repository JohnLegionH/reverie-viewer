// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Net;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The two development aids for neighbour work: the crossing record and the list of neighbours held back from an advance connect.</summary>
public class NeighbourDebugTests
{
    private sealed class Clock { public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); }

    private static (CrossingTrace Trace, ListSink Sink, Clock Clock) Make(bool on)
    {
        var sink = new ListSink();
        var clock = new Clock();
        return (new CrossingTrace(new CoreLog(sink, "test"), () => clock.Now) { Enabled = on }, sink, clock);
    }

    [Fact]
    public void A_crossing_message_is_written_with_its_handle_and_position_when_the_record_is_on()
    {
        var (trace, sink, _) = Make(true);

        trace.Message(1234, new Vector3(0.5f, 100, 22), new Vector3(1, 0, 0));

        var line = Assert.Single(sink.Entries, e => e.Event == "trace.cross.message");
        Assert.Contains(line.Fields, f => f.Key == "handle" && f.Value == "1234");
        Assert.Contains(line.Fields, f => f.Key == "pos" && f.Value.Contains("0.5"));
    }

    [Fact]
    public void The_two_seeds_of_a_crossing_are_written_as_fingerprints_with_whether_they_are_the_same()
    {
        var (trace, sink, _) = Make(true);
        var a = new Uri("https://sim.example.org:12043/cap/aaaa-secret");
        var b = new Uri("https://sim.example.org:12043/cap/bbbb-secret");

        trace.Seeds(77, a, b);
        trace.Seeds(78, a, a);

        var lines = sink.Entries.Where(e => e.Event == "trace.cross.seeds").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines[0].Fields, f => f.Key == "same" && f.Value == "False");
        Assert.Contains(lines[1].Fields, f => f.Key == "same" && f.Value == "True");
        var child = lines[0].Fields.Single(f => f.Key == "child").Value;
        var message = lines[0].Fields.Single(f => f.Key == "message").Value;
        Assert.NotEqual(child, message);
        Assert.Equal(child, lines[1].Fields.Single(f => f.Key == "child").Value);
        Assert.All(lines.SelectMany(l => l.Fields), f => Assert.DoesNotContain("secret", f.Value));
    }

    [Fact]
    public void A_missing_seed_is_written_as_none_and_counts_as_different()
    {
        var (trace, sink, _) = Make(true);
        trace.Seeds(5, null, new Uri("https://sim.example.org:12043/cap/x"));
        var line = Assert.Single(sink.Entries, e => e.Event == "trace.cross.seeds");
        Assert.Contains(line.Fields, f => f.Key == "child" && f.Value == "none");
        Assert.Contains(line.Fields, f => f.Key == "same" && f.Value == "False");
    }

    [Fact]
    public void Seeds_are_not_written_when_the_record_is_off()
    {
        var (trace, sink, _) = Make(false);
        trace.Seeds(5, new Uri("https://sim.example.org/cap/x"), new Uri("https://sim.example.org/cap/y"));
        Assert.DoesNotContain(sink.Entries, e => e.Event == "trace.cross.seeds");
    }

    [Fact]
    public void Nothing_is_written_when_the_record_is_off()
    {
        var (trace, sink, _) = Make(false);

        trace.Message(1, Vector3.Zero, Vector3.UnitX);
        trace.Promoted(1, "Region", Vector3.Zero, 2);
        bool logged = trace.Update(1, true, 1, 0, Vector3.Zero, Vector3.Zero);

        Assert.False(logged);
        Assert.DoesNotContain(sink.Entries, e => e.Event.StartsWith("trace.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_promotion_names_the_region_and_where_the_agent_is_in_it()
    {
        var (trace, sink, _) = Make(true);

        trace.Promoted(99, "There", new Vector3(3, 100, 22), 4);

        var line = Assert.Single(sink.Entries, e => e.Event == "trace.cross.promoted");
        Assert.Contains(line.Fields, f => f.Key == "region" && f.Value == "There");
        Assert.Contains(line.Fields, f => f.Key == "circuits" && f.Value == "4");
    }

    [Fact]
    public void Agent_updates_to_one_circuit_are_written_four_times_a_second_at_most()
    {
        var (trace, sink, clock) = Make(true);

        Assert.True(trace.Update(7, true, 1, 0, Vector3.Zero, Vector3.Zero));
        clock.Now += TimeSpan.FromMilliseconds(50);
        Assert.False(trace.Update(7, true, 1, 0, Vector3.Zero, Vector3.Zero));
        clock.Now += TimeSpan.FromMilliseconds(300);
        Assert.True(trace.Update(7, true, 1, 0, Vector3.Zero, Vector3.Zero));

        Assert.Equal(2, sink.Entries.Count(e => e.Event == "trace.cross.update"));
    }

    [Fact]
    public void An_update_to_another_circuit_or_with_other_controls_is_always_written()
    {
        var (trace, _, clock) = Make(true);
        Assert.True(trace.Update(7, true, 1, 0, Vector3.Zero, Vector3.Zero));
        clock.Now += TimeSpan.FromMilliseconds(10);

        Assert.True(trace.Update(8, true, 1, 0, Vector3.Zero, Vector3.Zero));   // another circuit
        clock.Now += TimeSpan.FromMilliseconds(10);
        Assert.True(trace.Update(8, true, 0, 0, Vector3.Zero, Vector3.Zero));   // other controls
    }

    [Fact]
    public void A_held_back_address_is_refused_until_a_crossing_lets_it_through()
    {
        var held = new HeldBackCircuits();
        var a = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9001);
        var b = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9002);

        held.Hold(a);
        Assert.True(held.Refuses(a, true));
        Assert.False(held.Refuses(b, true));
        Assert.Equal(1, held.Count);

        held.Allow(a);
        Assert.False(held.Refuses(a, true));
    }

    [Fact]
    public void A_held_back_address_is_not_refused_before_the_agent_is_in_a_region_so_the_login_itself_connects()
    {
        var held = new HeldBackCircuits();
        var a = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9001);
        held.Hold(a);

        Assert.True(held.Refuses(a, true));    // the positive state first: it is held
        Assert.False(held.Refuses(a, false));  // but the first connect (the agent's own region at login) is let through
    }

    [Fact]
    public void A_transport_given_addresses_before_the_login_holds_exactly_those_back()
    {
        using var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));

        Assert.True(t.HoldAtLogin("192.0.2.10:9003"));
        Assert.True(t.HoldAtLogin("192.0.2.10:9000"));
        Assert.False(t.HoldAtLogin("not an address"));

        Assert.True(t.IsHeldBack(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9003)));
        Assert.True(t.IsHeldBack(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9000)));
        Assert.False(t.IsHeldBack(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9001)));
    }

    [Fact]
    public void A_masked_address_is_taken_once()
    {
        var held = new HeldBackCircuits();
        var a = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9001);
        var b = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9002);
        Assert.False(held.TakeMask(a));   // nothing masked yet

        held.Mask(a);

        Assert.False(held.TakeMask(b));
        Assert.True(held.TakeMask(a));
        Assert.False(held.TakeMask(a));
    }

    [Fact]
    public void Clearing_lifts_every_mask_too()
    {
        var held = new HeldBackCircuits();
        var a = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9001);
        held.Mask(a);
        held.Clear();
        Assert.False(held.TakeMask(a));
    }

    [Fact]
    public void Clearing_lifts_every_hold()
    {
        var held = new HeldBackCircuits();
        held.Hold(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9001));
        held.Hold(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9002));
        Assert.Equal(2, held.Count);

        held.Clear();

        Assert.Equal(0, held.Count);
        Assert.False(held.Refuses(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9001), true));
    }
}
