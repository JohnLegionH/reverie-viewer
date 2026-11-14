// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Parity oracle ported from the web gateway (web-viewer bb91ba5, FirstBurstTests, all 5), unchanged except that the
/// function now lives in <see cref="FirstBurst"/>. Plus the same rule end to end through a session.
/// W-21: during login the simulator's first burst -- the self avatar object among it -- can arrive before
/// LibreMetaverse sets Network.CurrentSim. The guard must reject only OTHER simulators, never the one we log into.
/// </summary>
public class FirstBurstTests
{
    private static readonly object LoginSim = new();
    private static readonly object OtherSim = new();

    [Fact]
    public void Before_CurrentSim_is_set_the_only_connected_simulator_is_ours()
    {
        Assert.True(FirstBurst.FromOurSim(LoginSim, currentSim: null, connectedSimulators: 1));
    }

    [Fact]
    public void Before_CurrentSim_is_set_nothing_is_taken_once_several_simulators_are_connected()
    {
        Assert.False(FirstBurst.FromOurSim(LoginSim, currentSim: null, connectedSimulators: 2));
        Assert.False(FirstBurst.FromOurSim(LoginSim, currentSim: null, connectedSimulators: 0));
    }

    [Fact]
    public void Once_CurrentSim_is_set_only_that_simulator_is_ours()
    {
        Assert.True(FirstBurst.FromOurSim(LoginSim, currentSim: LoginSim, connectedSimulators: 1));
        Assert.False(FirstBurst.FromOurSim(OtherSim, currentSim: LoginSim, connectedSimulators: 2));
        Assert.False(FirstBurst.FromOurSim(OtherSim, currentSim: LoginSim, connectedSimulators: 1));
    }

    [Fact]
    public void A_null_event_simulator_is_never_ours()
    {
        Assert.False(FirstBurst.FromOurSim(null, currentSim: null, connectedSimulators: 1));
        Assert.False(FirstBurst.FromOurSim(null, currentSim: LoginSim, connectedSimulators: 1));
    }

    [Fact]
    public void The_login_burst_then_that_sim_becoming_current_keeps_everything_from_it()
    {
        object? current = null;
        var kept = 0;

        foreach (var _ in new[] { "avatar.update", "object.update" })
            if (FirstBurst.FromOurSim(LoginSim, current, connectedSimulators: 1)) kept++;

        current = LoginSim;   // SimConnected

        foreach (var _ in new[] { "avatar.update", "object.update" })
            if (FirstBurst.FromOurSim(LoginSim, current, connectedSimulators: 1)) kept++;

        Assert.Equal(4, kept);
        Assert.False(FirstBurst.FromOurSim(OtherSim, current, connectedSimulators: 2));
    }

    [Fact]
    public async Task Through_a_session_the_pre_CurrentSim_burst_is_delivered_and_a_neighbour_is_not()
    {
        var t = new FakeTransport();
        using var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/" }, t, new CoreLog(new ListSink(), "test"));
        var seen = new List<string>();
        s.WorldEvent += e => { lock (seen) seen.Add(e.Kind + "@" + e.Sim!.Name); };
        await s.LoginAsync(new LoginCredentials("A", "B", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None);

        var login = new SimRef(1000UL << 32 | 1000UL, "LoginSim");
        var neighbour = new SimRef(1256UL << 32 | 1000UL, "Neighbour");
        t.ConnectedSimulatorCount = 1;                        // CurrentSim still null: the race
        t.Raise(new SimWorldEvent(login, "avatar.update"));
        t.Raise(new SimWorldEvent(login, "object.update"));
        t.CurrentSim = login;
        t.ConnectedSimulatorCount = 2;
        t.Raise(new SimWorldEvent(neighbour, "object.update"));
        t.Raise(new SimWorldEvent(login, "object.update"));

        Assert.Equal(new[] { "avatar.update@LoginSim", "object.update@LoginSim", "object.update@LoginSim" }, seen);
    }
}
