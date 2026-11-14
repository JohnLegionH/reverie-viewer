// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Reflection;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>L0: the session stage machine, login/logout/disconnect, resume and grace, against the fake protocol layer.</summary>
public class SessionLifecycleTests
{
    private static readonly SimRef Sim = new(1000UL * 256 << 32 | 1000UL * 256, "Home");
    private static LoginCredentials Creds => new("Test", "Resident", new Secret("correct horse", SensitiveKind.PasswordDigest));

    private static (ViewerSession S, FakeTransport T, ListSink Log) New(SessionOptions? o = null)
    {
        var t = new FakeTransport();
        var sink = new ListSink();
        return (new ViewerSession(o ?? new SessionOptions { LoginUri = "http://grid.example:8002/" }, t, new CoreLog(sink, "test")), t, sink);
    }

    [Fact]
    public async Task Stages_advance_in_order_and_reach_InWorld()
    {
        var (s, t, _) = New();
        using var _s = s;
        var stages = new List<SessionStage>();
        s.StageChanged += st => stages.Add(st);
        Assert.Equal(SessionStage.Idle, s.Stage);

        var outcome = await s.LoginAsync(Creds, CancellationToken.None);
        Assert.True(outcome.Ok);
        t.DriveToInWorld(Sim);

        Assert.Equal(new[] { SessionStage.LoggingIn, SessionStage.LoggedIn, SessionStage.SimConnected, SessionStage.EventQueueRunning, SessionStage.InWorld }, stages);
        Assert.Equal(SessionStage.InWorld, s.Stage);
    }

    [Fact]
    public async Task InWorld_needs_both_event_queue_and_movement_in_either_order()
    {
        var (s, t, _) = New();
        using var _s = s;
        await s.LoginAsync(Creds, CancellationToken.None);
        t.Raise(new LoginProgressed(LoginPhase.ConnectingToSim, null));
        t.Raise(new SimConnected(Sim, 256, 256));
        t.Raise(new MovementComplete(Sim));
        Assert.Equal(SessionStage.MovementComplete, s.Stage);
        t.Raise(new EventQueueRunning(Sim));
        Assert.Equal(SessionStage.InWorld, s.Stage);
    }

    [Fact]
    public async Task The_stage_never_goes_backward()
    {
        var (s, t, _) = New();
        using var _s = s;
        await s.LoginAsync(Creds, CancellationToken.None);
        t.DriveToInWorld(Sim);
        t.Raise(new LoginProgressed(LoginPhase.ConnectingToSim, null));   // a teleport's second login-ish event
        t.Raise(new SimConnected(Sim, 256, 256));
        Assert.Equal(SessionStage.InWorld, s.Stage);
    }

    [Fact]
    public async Task A_failed_login_ends_the_session_with_the_grids_first_reason()
    {
        var (s, t, _) = New();
        using var _s = s;
        SessionEndReason? ended = null;
        s.Ended += r => ended = r;
        t.OnLogin = (_, _) =>
        {
            t.Raise(new LoginProgressed(LoginPhase.Failed, "You are banned from this grid"));
            t.Raise(new LoginProgressed(LoginPhase.Failed, "Canceled"));       // must not overwrite the real reason
            return Task.FromResult(TransportLoginResult.Failed("key", "later message"));
        };

        var outcome = await s.LoginAsync(Creds, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal("You are banned from this grid", outcome.Message);
        Assert.Equal(SessionEndReason.LoginFailed, ended);
        Assert.True(s.IsEnded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.LoginAsync(Creds, CancellationToken.None));
    }

    [Fact]
    public async Task Logout_ends_the_session_and_the_disconnect_that_follows_is_expected()
    {
        var (s, t, _) = New();
        using var _s = s;
        var ends = new List<SessionEndReason>();
        s.Ended += r => ends.Add(r);
        await s.LoginAsync(Creds, CancellationToken.None);
        t.DriveToInWorld(Sim);

        await s.LogoutAsync(CancellationToken.None);
        t.Raise(new Disconnected("ClientInitiated"));

        Assert.Equal(1, t.Logouts);
        Assert.Equal(new[] { SessionEndReason.LoggedOut }, ends);
    }

    [Fact]
    public async Task An_unexpected_disconnect_ends_the_session_once()
    {
        var (s, t, _) = New();
        using var _s = s;
        var ends = new List<SessionEndReason>();
        s.Ended += r => ends.Add(r);
        await s.LoginAsync(Creds, CancellationToken.None);
        t.DriveToInWorld(Sim);

        t.Raise(new Disconnected("SimShutdown"));
        t.Raise(new Disconnected("SimShutdown"));

        Assert.Equal(new[] { SessionEndReason.Disconnected }, ends);
    }

    [Fact]
    public async Task Strict_policy_refuses_an_internal_login_uri_before_the_transport_is_called()
    {
        var (s, t, _) = New(new SessionOptions { LoginUri = "http://127.0.0.1:8002/", Outbound = new() { Policy = "strict" } });
        using var _s = s;
        var outcome = await s.LoginAsync(Creds, CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Equal("outboundDenied", outcome.Code);
        Assert.Empty(t.Logins);
    }

    [Fact]
    public void An_unknown_outbound_policy_is_refused_at_construction()
    {
        Assert.Throws<ArgumentException>(() => New(new SessionOptions { LoginUri = "http://g/", Outbound = new() { Policy = "strictish" } }));
    }

    [Fact]
    public async Task The_password_reaches_the_transport_and_is_not_kept_by_the_session()
    {
        var (s, t, _) = New();
        using var _s = s;
        await s.LoginAsync(Creds, CancellationToken.None);
        Assert.Equal("correct horse", t.Logins.Single().Password.Reveal());

        foreach (var f in typeof(ViewerSession).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            var v = f.GetValue(s);
            Assert.False(v is Sensitive, $"ViewerSession.{f.Name} holds a {v?.GetType().Name}");
            Assert.False(v is string str && str.Contains("correct horse"), $"ViewerSession.{f.Name} holds the password");
        }
    }

    [Fact]
    public async Task Resume_within_grace_keeps_the_session_and_cancels_the_timer()
    {
        var delay = new ManualDelay();
        var (s, t, _) = New(new SessionOptions { LoginUri = "http://grid.example:8002/", Delay = delay.Delay });
        using var _s = s;
        await s.LoginAsync(Creds, CancellationToken.None);
        t.DriveToInWorld(Sim);

        s.DetachPresentation();
        Wait.Until(() => delay.Requested.Count == 1, "grace timer started");
        Assert.Equal(TimeSpan.FromSeconds(120), delay.Requested.Single());   // in world: the full window
        Assert.True(s.ResumePresentation());

        Assert.True(delay.LastWasCancelled);
        Assert.False(s.IsEnded);
        Assert.Equal(0, t.Logouts);
    }

    [Fact]
    public async Task Grace_expiry_logs_out_and_ends_the_session()
    {
        var delay = new ManualDelay();
        var (s, t, _) = New(new SessionOptions { LoginUri = "http://grid.example:8002/", Delay = delay.Delay });
        using var _s = s;
        var endedSignal = new TaskCompletionSource<SessionEndReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Ended += r => endedSignal.TrySetResult(r);
        await s.LoginAsync(Creds, CancellationToken.None);
        t.Raise(new LoginProgressed(LoginPhase.ConnectingToSim, null));   // never reaches InWorld

        // explicit signals, not polling with a short cap: the timer is started and the session ended on the thread pool, which a busy machine
        // (the whole suite running in parallel) can leave waiting for longer than a fixed few seconds; the limits are only hang guards
        s.DetachPresentation();
        await delay.WhenRequested(1).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(15), delay.Requested.Single());    // pre-InWorld: the short window
        delay.ElapseLast();

        var ended = await endedSignal.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(SessionEndReason.GraceExpired, ended);
        Assert.Equal(1, t.Logouts);
        Assert.False(s.ResumePresentation());
    }

    [Fact]
    public async Task Dispose_cancels_an_in_flight_login()
    {
        var (s, t, _) = New();
        var entered = new TaskCompletionSource();
        t.OnLogin = async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return TransportLoginResult.Failed("unreachable", null);
        };
        var login = s.LoginAsync(Creds, CancellationToken.None);
        await entered.Task;

        s.Dispose();
        var outcome = await login.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(t.LastLoginToken.IsCancellationRequested);
        Assert.Equal("cancelled", outcome.Code);
        Assert.True(t.Disposed);
        Assert.Equal(0, t.Subscribers);
    }

    // ---------------- var-region size (ADR-007 patch layer; the gateway's rules) ----------------

    [Fact]
    public async Task Var_region_size_takes_the_login_reply_when_the_library_says_256()
    {
        var (s, t, _) = New();
        using var _s = s;
        t.OnLogin = (_, _) => Task.FromResult(FakeTransport.Ok(Guid.NewGuid(), sizeX: 1024, sizeY: 1024));
        await s.LoginAsync(Creds, CancellationToken.None);
        t.DriveToInWorld(Sim, simSizeX: 256, simSizeY: 256);

        Assert.Equal((1024, 1024, false), (s.Region!.SizeX, s.Region.SizeY, s.Region.SizeAuthoritative));
    }

    [Fact]
    public async Task With_no_size_from_anyone_a_region_is_256()
    {
        var (s, t, _) = New();
        using var _s = s;
        await s.LoginAsync(Creds, CancellationToken.None);
        t.DriveToInWorld(Sim, simSizeX: 0, simSizeY: 0);
        Assert.Equal((256, 256), (s.Region!.SizeX, s.Region.SizeY));
    }

    [Fact]
    public async Task A_patch_outside_the_region_grows_it_in_256_m_steps_until_the_map_block_is_authoritative()
    {
        var (s, t, _) = New();
        using var _s = s;
        await s.LoginAsync(Creds, CancellationToken.None);
        t.DriveToInWorld(Sim, 256, 256);

        t.Raise(new LandPatch(Sim, X: 20, Y: 3, PatchSize: 16));        // x edge 336 m -> 512
        Assert.Equal((512, 256), (s.Region!.SizeX, s.Region.SizeY));

        t.Raise(new MapBlockSize(Sim.GridX, Sim.GridY, 768, 768));       // our cell: authoritative
        Assert.Equal((768, 768, true), (s.Region!.SizeX, s.Region.SizeY, s.Region.SizeAuthoritative));

        t.Raise(new LandPatch(Sim, X: 60, Y: 60, PatchSize: 16));       // would need 1024; no longer grows
        Assert.Equal((768, 768), (s.Region!.SizeX, s.Region.SizeY));
    }

    [Fact]
    public async Task A_map_block_for_another_cell_changes_nothing_and_a_legacy_block_means_256()
    {
        var (s, t, _) = New();
        using var _s = s;
        t.OnLogin = (_, _) => Task.FromResult(FakeTransport.Ok(Guid.NewGuid(), 512, 512));
        await s.LoginAsync(Creds, CancellationToken.None);
        t.DriveToInWorld(Sim, 256, 256);

        t.Raise(new MapBlockSize(Sim.GridX + 4, Sim.GridY, 2048, 2048));
        Assert.Equal((512, 512, false), (s.Region!.SizeX, s.Region.SizeY, s.Region.SizeAuthoritative));

        t.Raise(new MapBlockSize(Sim.GridX, Sim.GridY, 0, 0));
        Assert.Equal((256, 256, true), (s.Region!.SizeX, s.Region.SizeY, s.Region.SizeAuthoritative));
    }

    [Fact]
    public async Task A_patch_from_a_neighbour_does_not_grow_our_region()
    {
        var (s, t, _) = New();
        using var _s = s;
        await s.LoginAsync(Creds, CancellationToken.None);
        t.DriveToInWorld(Sim, 256, 256);
        t.ConnectedSimulatorCount = 2;
        t.Raise(new LandPatch(new SimRef(Sim.Handle, "Neighbour"), X: 40, Y: 0, PatchSize: 16));
        Assert.Equal((256, 256), (s.Region!.SizeX, s.Region.SizeY));
    }
}
