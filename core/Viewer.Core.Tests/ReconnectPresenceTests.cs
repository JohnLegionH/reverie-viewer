// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Reconnect;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A reconnect that the grid refuses because it still holds the old session (the login refusal "presence"). The official viewer ends
/// that attempt and shows the grid's text (indra/newview/lllogininstance.cpp, "presence" among the reasons already communicated; the base
/// wording is LoginFailedAlreadyLoggedIn in skins/default/xui/en/strings.xml). This viewer waits and retries the same spot a bounded number
/// of times with a countdown the user can cancel. A real login model on a fake transport, a fake delay and a fake clock; no grid. Every
/// member builds its own objects, so the class runs in parallel.
/// </summary>
public class ReconnectPresenceTests
{
    private const string Password = "correct horse battery staple";
    private const string SameSpot = "uri:Example Region&100&50&30";
    private static readonly StringTable En = StringTable.English();
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private sealed class Rig
    {
        public readonly FakeTransport Fake = new();
        public readonly LoginModel Login;
        public readonly ReconnectModel Reconnect;
        public readonly List<TimeSpan> Pauses = new();
        public readonly List<int> Countdowns = new();
        public readonly List<string> Statuses = new();
        public DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        public Action<int>? OnPause;   // called with the number of pauses so far, inside each pause

        public Rig()
        {
            var log = new CoreLog(new ListSink(), "test");
            Login = new LoginModel(En, opts => new ViewerSession(opts, Fake, log), null, null)
            {
                LoginUri = "https://grid.example.org:8002/", FirstName = "Test", LastName = "User",
            };
            Reconnect = new ReconnectModel(Login, En, (t, ct) =>
            {
                int n;
                lock (Pauses) { Pauses.Add(t); n = Pauses.Count; }
                Now += t;
                OnPause?.Invoke(n);
                return Task.CompletedTask;
            }, () => Now);
            Reconnect.Changed += () =>
            {
                Countdowns.Add(Reconnect.Countdown);
                Statuses.Add(Reconnect.Status);
            };
        }

        public async Task<ViewerSession> LogInAndDrop()
        {
            Login.SetPassword(Password);
            Assert.True(await Login.LoginAsync());
            Fake.DriveToInWorld(Here);
            var session = Login.Session!;
            session.World.ApplyAvatar(Here, new Viewer.Core.World.AvatarData(5, Self, "Test User", new Vector3(100f, 50f, 31f), Quaternion.Identity, new Vector3(0.45f, 0.6f, 2f)));
            Fake.Raise(new Disconnected("NetworkTimeout", DisconnectKind.NetworkTimeout));
            return session;
        }

        /// <summary>The grid refuses the next <paramref name="refusals"/> logins as a held session, taking <paramref name="loginTime"/> each, then accepts.</summary>
        public void RefusePresence(int refusals, TimeSpan loginTime = default)
        {
            int seen = 0;
            Fake.OnLogin = (_, _) =>
            {
                Now += loginTime;
                return Task.FromResult(Interlocked.Increment(ref seen) <= refusals
                    ? TransportLoginResult.Failed(ReconnectPlan.PresenceCode, "This agent is already logged in.")
                    : FakeTransport.Ok(Self));
            };
        }
    }

    [Fact]
    public async Task The_limits_are_three_retries_eight_seconds_apart_inside_a_minute()
    {
        Assert.Equal("presence", ReconnectPlan.PresenceCode);
        Assert.Equal(3, ReconnectPlan.PresenceRetries);
        Assert.Equal(TimeSpan.FromSeconds(8), ReconnectPlan.PresenceWait);
        Assert.Equal(TimeSpan.FromSeconds(60), ReconnectPlan.PresenceBudget);
        Assert.True(ReconnectPlan.PresenceRetries * ReconnectPlan.PresenceWait < ReconnectPlan.PresenceBudget);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_held_session_is_waited_out_with_a_countdown_and_the_same_spot_is_tried_again()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        rig.RefusePresence(1);
        Assert.True(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal(ReconnectState.Succeeded, rig.Reconnect.State);
        // the first login of the session, the refused try and the retry; both tries at the spot the agent was in, never the last location
        Assert.Equal([SameSpot, SameSpot], rig.Fake.Logins.Skip(1).Select(l => l.StartLocation));
        Assert.Equal(2, rig.Reconnect.Attempts);
        // the wait is eight one-second ticks, and the countdown the interface shows runs 8 down to 1
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromSeconds(1), 8), rig.Pauses);
        var shown = rig.Countdowns.Where(c => c > 0).Distinct().ToArray();
        Assert.Equal([8, 7, 6, 5, 4, 3, 2, 1], shown);
        Assert.Equal(0, rig.Reconnect.Countdown);
    }

    [Fact]
    public async Task While_waiting_the_status_says_how_long_and_which_retry()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        rig.RefusePresence(1);
        await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None);
        Assert.Contains(En.Get("drop.presence_wait", 8, 1, ReconnectPlan.PresenceRetries), rig.Statuses);
        Assert.Contains(En.Get("drop.presence_wait", 1, 1, ReconnectPlan.PresenceRetries), rig.Statuses);
    }

    [Fact]
    public async Task A_grid_that_keeps_refusing_is_retried_three_times_and_then_the_click_ends_with_the_grids_words()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        rig.RefusePresence(int.MaxValue);
        Assert.False(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal(ReconnectState.Failed, rig.Reconnect.State);
        var tries = rig.Fake.Logins.Skip(1).Select(l => l.StartLocation).ToArray();
        Assert.Equal(1 + ReconnectPlan.PresenceRetries, tries.Length);   // the first try and three retries, not a fourth
        Assert.All(tries, t => Assert.Equal(SameSpot, t));
        Assert.Equal(3 * 8, rig.Pauses.Count);                              // three waits of eight ticks
        Assert.Contains("This agent is already logged in.", rig.Reconnect.Status);
        await Task.Delay(50);
        Assert.Equal(1 + tries.Length, rig.Fake.Logins.Count);              // nothing tries again by itself
    }

    [Fact]
    public async Task Slow_logins_stop_the_retries_at_the_time_budget()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        rig.RefusePresence(int.MaxValue, loginTime: TimeSpan.FromSeconds(20));   // each refusal takes 20 s to come back
        Assert.False(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        // 20 s login, 8 s wait, 20 s login, 8 s wait, 20 s login = 76 s: a third wait would pass the minute, so it is not started
        Assert.Equal(3, rig.Fake.Logins.Count - 1);
        Assert.Equal(2 * 8, rig.Pauses.Count);
        Assert.Equal(ReconnectState.Failed, rig.Reconnect.State);
    }

    [Fact]
    public async Task Cancel_during_the_countdown_stops_at_once_and_nothing_logs_in_after_it()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        rig.RefusePresence(int.MaxValue);
        rig.OnPause = n => { if (n == 3) rig.Reconnect.Cancel(); };
        Assert.False(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal(ReconnectState.Failed, rig.Reconnect.State);
        Assert.Equal(En.Get("drop.cancelled"), rig.Reconnect.Status);
        Assert.Equal(3, rig.Pauses.Count);                       // no tick after the cancel
        Assert.Equal(2, rig.Fake.Logins.Count);                  // the first login and the one refused try
        Assert.Equal(0, rig.Reconnect.Countdown);
        await Task.Delay(50);
        Assert.Equal(2, rig.Fake.Logins.Count);
    }

    [Fact]
    public async Task A_cancelled_click_can_be_followed_by_a_fresh_one()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        rig.RefusePresence(int.MaxValue);
        rig.OnPause = n => { if (n == 1) rig.Reconnect.Cancel(); };
        Assert.False(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        rig.OnPause = null;
        rig.RefusePresence(0);
        Assert.True(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal(ReconnectState.Succeeded, rig.Reconnect.State);
    }

    [Fact]
    public async Task The_callers_own_cancellation_also_ends_the_countdown()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        rig.RefusePresence(int.MaxValue);
        using var cts = new CancellationTokenSource();
        rig.OnPause = n => { if (n == 2) cts.Cancel(); };
        Assert.False(await rig.Reconnect.RunAsync(old.Drop, cts.Token));
        Assert.Equal(2, rig.Pauses.Count);
        Assert.Equal(2, rig.Fake.Logins.Count);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("tos")]
    [InlineData("update")]
    [InlineData("mfa_challenge")]
    public async Task A_credential_refusal_is_never_waited_for(string code)
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        rig.Fake.OnLogin = (_, _) => Task.FromResult(TransportLoginResult.Failed(code, "refused"));
        Assert.False(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Empty(rig.Pauses);
        Assert.Equal(2, rig.Fake.Logins.Count);
    }

    [Fact]
    public async Task Another_failure_still_falls_back_to_the_last_location_after_the_short_pause()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        rig.Fake.OnLogin = (r, _) => Task.FromResult(r.StartLocation == "last" ? FakeTransport.Ok(Self) : TransportLoginResult.Failed("connect", "no route"));
        Assert.True(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal([SameSpot, "last"], rig.Fake.Logins.Skip(1).Select(l => l.StartLocation));
        Assert.Equal([TimeSpan.FromSeconds(3)], rig.Pauses);
    }

    [Fact]
    public async Task A_refusal_for_a_held_session_after_a_failed_try_elsewhere_is_still_waited_for()
    {
        // first try fails some other way (so the next start location is the last one), and that one meets the held session
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        int n = 0;
        rig.Fake.OnLogin = (r, _) => Task.FromResult(Interlocked.Increment(ref n) switch
        {
            1 => TransportLoginResult.Failed("connect", "no route"),
            2 => TransportLoginResult.Failed(ReconnectPlan.PresenceCode, "held"),
            _ => FakeTransport.Ok(Self),
        });
        Assert.True(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal([SameSpot, "last", "last"], rig.Fake.Logins.Skip(1).Select(l => l.StartLocation));
    }

    [Fact]
    public void The_words_for_the_countdown_and_the_cancel_exist()
    {
        Assert.True(En.Has("drop.presence_wait"));
        Assert.True(En.Has("drop.cancelled"));
        Assert.True(En.Has("drop.cancel"));
    }
}
