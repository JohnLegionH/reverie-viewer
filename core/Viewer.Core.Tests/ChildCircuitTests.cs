// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A neighbour's circuit must deliver. Two announcements of one neighbour can start two connects to the same address in the same
/// millisecond (one of them failed with a socket error in live runs, and the neighbour then delivered nothing for the whole run); the
/// connecting hook keeps one. A circuit that still delivers nothing after it came up is logged by name and prodded.
/// </summary>
public class ChildCircuitTests
{
    private static IPEndPoint Ep(int last, int port = 9000) => new(IPAddress.Parse($"192.0.2.{last}"), port);

    // ---------------- one circuit per address ----------------

    [Fact]
    public void The_first_entry_for_an_address_is_kept_and_the_second_is_refused()
    {
        var listed = new[] { Ep(10), Ep(10) };
        Assert.False(CircuitDedupe.Refuses(listed, 0));
        Assert.True(CircuitDedupe.Refuses(listed, 1));
    }

    [Fact]
    public void Different_addresses_are_not_refused()
    {
        var listed = new[] { Ep(10), Ep(11), Ep(10, 9001) };
        for (int i = 0; i < listed.Length; i++)
            Assert.False(CircuitDedupe.Refuses(listed, i));
    }

    [Fact]
    public void Exactly_one_of_n_entries_for_an_address_survives_whatever_order_they_are_asked_in()
    {
        var listed = new[] { Ep(10), Ep(20), Ep(10), Ep(10) };
        var survivors = Enumerable.Range(0, listed.Length).Where(i => !CircuitDedupe.Refuses(listed, i)).ToArray();
        Assert.Equal([0, 1], survivors);
    }

    // ---------------- the silent neighbour watch ----------------

    private static readonly SimRef East = new(1001UL * 256 << 32 | 1000UL * 256, "East");
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_neighbour_is_not_due_before_the_first_wait()
    {
        var w = new SilentNeighbourWatch();
        w.Added(East, T0);
        Assert.Empty(w.Due(T0 + SilentNeighbourWatch.First - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_silent_neighbour_is_due_after_the_first_wait_with_its_first_prod()
    {
        var w = new SilentNeighbourWatch();
        w.Added(East, T0);
        var due = w.Due(T0 + SilentNeighbourWatch.First);
        Assert.Single(due);
        Assert.Same(East, due[0].Sim);
        Assert.Equal(1, due[0].Nudge);
    }

    [Fact]
    public void A_prod_is_not_repeated_at_once_and_the_waits_double()
    {
        var w = new SilentNeighbourWatch();
        w.Added(East, T0);
        Assert.Single(w.Due(T0 + SilentNeighbourWatch.First));
        Assert.Empty(w.Due(T0 + SilentNeighbourWatch.First + TimeSpan.FromSeconds(1)));
        var second = w.Due(T0 + SilentNeighbourWatch.First + SilentNeighbourWatch.First * 2);
        Assert.Single(second);
        Assert.Equal(2, second[0].Nudge);
    }

    [Fact]
    public void A_neighbour_is_prodded_at_most_the_allowed_number_of_times()
    {
        var w = new SilentNeighbourWatch();
        w.Added(East, T0);
        int total = 0;
        for (int minutes = 1; minutes <= 30; minutes++)
            total += w.Due(T0 + TimeSpan.FromMinutes(minutes)).Count;
        Assert.Equal(SilentNeighbourWatch.MaxNudges, total);
    }

    [Fact]
    public void A_neighbour_that_has_delivered_is_not_prodded()
    {
        var w = new SilentNeighbourWatch();
        w.Added(East, T0);
        w.Heard(East);
        Assert.Empty(w.Due(T0 + TimeSpan.FromMinutes(5)));
    }

    // ---------------- in the session ----------------

    private static readonly SimRef Home = new(1000UL * 256 << 32 | 1000UL * 256, "Home");

    private static async Task<(ViewerSession S, FakeTransport T)> InWorld()
    {
        var t = new FakeTransport();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/", Neighbours = true }, t, new CoreLog(new ListSink(), "test"));
        await s.LoginAsync(new LoginCredentials("Test", "Resident", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None);
        t.DriveToInWorld(Home);
        t.ConnectedSimulatorCount++;
        t.Raise(new SimConnected(East, 256, 256));
        return (s, t);
    }

    [Fact]
    public async Task A_neighbour_that_stays_silent_has_its_circuit_prodded()
    {
        var (s, t) = await InWorld();
        using var _ = s;
        s.CheckNeighbours(DateTime.UtcNow + SilentNeighbourWatch.First + TimeSpan.FromSeconds(1));
        Assert.Equal([East], t.Nudges);
    }

    [Fact]
    public async Task A_neighbour_that_has_sent_an_object_is_not_prodded()
    {
        var (s, t) = await InWorld();
        using var _ = s;
        t.Raise(new ObjectUpdated(East, new PrimData(1, Guid.NewGuid(), 0, 0, new Vector3(10, 20, 30), Quaternion.Identity, Vector3.One), false, null));
        s.CheckNeighbours(DateTime.UtcNow + TimeSpan.FromMinutes(5));
        Assert.Empty(t.Nudges);
    }
}
