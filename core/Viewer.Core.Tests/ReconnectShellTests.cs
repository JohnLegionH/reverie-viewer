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
/// The drop notice and the reconnect as the interface drives them: the words of the notice, what a click on Reconnect does (the
/// start locations, the attempt limit, the pause, the refusals that end it, one click at a time, never by itself), and that the
/// credentials are held in memory only. A real login model on a fake transport. No grid. Every member builds its own objects, so the
/// class runs in parallel.
/// </summary>
public class ReconnectShellTests
{
    private const string Password = "correct horse battery staple";
    private static readonly StringTable En = StringTable.English();
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private sealed class Rig
    {
        public readonly FakeTransport Fake = new();
        public readonly LoginModel Login;
        public readonly ReconnectModel Reconnect;
        public readonly List<TimeSpan> Pauses = new();
        public readonly List<ViewerSession> Sessions = new();

        public Rig(string? settingsDir = null)
        {
            var log = new CoreLog(new ListSink(), "test");
            Login = new LoginModel(En, opts => { var s = new ViewerSession(opts, Fake, log); Sessions.Add(s); return s; }, null, settingsDir)
            {
                LoginUri = "https://grid.example.org:8002/", FirstName = "Test", LastName = "User",
            };
            Reconnect = new ReconnectModel(Login, En, (t, ct) => { lock (Pauses) Pauses.Add(t); return Task.CompletedTask; });
        }

        public async Task<ViewerSession> LogInAndDrop(bool withAvatar = true)
        {
            Login.SetPassword(Password);
            Assert.True(await Login.LoginAsync());
            Fake.DriveToInWorld(Here);
            var session = Login.Session!;
            if (withAvatar)
                session.World.ApplyAvatar(Here, new Viewer.Core.World.AvatarData(5, Self, "Test User", new Vector3(100f, 50f, 31f), Quaternion.Identity, new Vector3(0.45f, 0.6f, 2f)));
            Fake.Raise(new Disconnected("NetworkTimeout", DisconnectKind.NetworkTimeout));
            return session;
        }
    }

    // ---------------- the login model's part ----------------

    [Fact]
    public async Task A_successful_login_reports_what_it_used_once_and_a_refused_one_reports_its_code()
    {
        var fake = new FakeTransport();
        var log = new CoreLog(new ListSink(), "test");
        var m = new LoginModel(En, opts => new ViewerSession(opts, fake, log)) { LoginUri = "https://grid.example.org:8002/", FirstName = " Test ", LastName = "User" };
        var seen = new List<LoginRecord>();
        m.Succeeded += seen.Add;
        fake.OnLogin = (_, _) => Task.FromResult(TransportLoginResult.Failed("key", "bad credentials"));
        m.SetPassword(Password);
        Assert.False(await m.LoginAsync());
        Assert.Equal("key", m.LastFailureCode);
        Assert.Empty(seen);
        fake.OnLogin = (_, _) => Task.FromResult(FakeTransport.Ok(Self));
        m.SetPassword(Password);
        Assert.True(await m.LoginAsync());
        Assert.Null(m.LastFailureCode);
        var record = Assert.Single(seen);
        Assert.Equal(("Test", "User", "https://grid.example.org:8002/"), (record.FirstName, record.LastName, record.LoginUri));
        Assert.Equal(Password, record.Password.Reveal());
        m.Session!.Dispose();
    }

    // ---------------- the notice ----------------

    private static async Task<DropNotice?> NoticeFor(Disconnected d, bool canReconnect = true)
    {
        var rig = new Rig();
        rig.Login.SetPassword(Password);
        await rig.Login.LoginAsync();
        rig.Fake.DriveToInWorld(Here);
        rig.Fake.Raise(d);
        return DropNotices.For(rig.Login.Session!, En, canReconnect);
    }

    [Fact]
    public async Task A_circuit_that_went_quiet_is_told_in_the_viewers_words_with_a_reconnect_button()
    {
        var n = await NoticeFor(new Disconnected("NetworkTimeout", DisconnectKind.NetworkTimeout));
        Assert.NotNull(n);
        Assert.Equal(En.Get("drop.title"), n!.Title);
        Assert.Equal(En.Get("drop.body"), n.Body);
        Assert.Equal(En.Get("drop.reason.timeout"), n.Reason);
        Assert.True(n.CanReconnect);
    }

    [Fact]
    public async Task A_kick_shows_the_grids_own_reason_a_known_key_as_words_and_an_empty_one_as_the_generic_line()
    {
        Assert.Equal("You were removed for being idle.", (await NoticeFor(new Disconnected("ServerInitiated", DisconnectKind.ServerInitiated, "You were removed for being idle.")))?.Reason);
        Assert.Equal(En.Get("teleport.error.no_host"), (await NoticeFor(new Disconnected("ServerInitiated", DisconnectKind.ServerInitiated, "no_host")))?.Reason);
        Assert.Equal(En.Get("drop.reason.unknown"), (await NoticeFor(new Disconnected("ServerInitiated", DisconnectKind.ServerInitiated, "  ")))?.Reason);
        Assert.Equal(En.Get("drop.reason.shutdown"), (await NoticeFor(new Disconnected("SimShutdown", DisconnectKind.SimShutdown)))?.Reason);
        Assert.Equal(En.Get("drop.reason.unknown"), (await NoticeFor(new Disconnected("x", DisconnectKind.Other)))?.Reason);
    }

    [Fact]
    public async Task A_notice_has_no_reconnect_button_when_the_viewer_holds_no_login_and_there_is_none_for_a_logout_the_user_asked_for()
    {
        Assert.False(true == (await NoticeFor(new Disconnected("NetworkTimeout", DisconnectKind.NetworkTimeout), canReconnect: false))?.CanReconnect);
        var rig = new Rig();
        rig.Login.SetPassword(Password);
        await rig.Login.LoginAsync();
        rig.Fake.DriveToInWorld(Here);
        await rig.Login.Session!.LogoutAsync(CancellationToken.None);
        Assert.Null(DropNotices.For(rig.Login.Session!, En, true));
    }

    // ---------------- the reconnect ----------------

    [Fact]
    public async Task The_credentials_are_held_after_a_login_and_forgotten_on_request_and_without_them_a_click_does_nothing_but_say_so()
    {
        var rig = new Rig();
        Assert.False(rig.Reconnect.HasLogin);
        var session = await rig.LogInAndDrop();
        Assert.True(rig.Reconnect.HasLogin);
        rig.Reconnect.Forget();
        Assert.False(rig.Reconnect.HasLogin);
        int before = rig.Fake.Logins.Count;
        Assert.False(await rig.Reconnect.RunAsync(session.Drop, CancellationToken.None));
        Assert.Equal(before, rig.Fake.Logins.Count);
        Assert.Equal(ReconnectState.Failed, rig.Reconnect.State);
        Assert.Equal(En.Get("drop.no_credentials"), rig.Reconnect.Status);
    }

    [Fact]
    public async Task A_click_logs_in_again_at_the_same_spot_with_the_same_login_and_stops_after_the_first_success()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        Assert.True(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal(2, rig.Fake.Logins.Count);
        var again = rig.Fake.Logins[1];
        Assert.Equal("uri:Example Region&100&50&30", again.StartLocation);
        Assert.Equal(("Test", "User"), (again.FirstName, again.LastName));
        Assert.Equal(Password, again.Password.Reveal());
        Assert.Equal(ReconnectState.Succeeded, rig.Reconnect.State);
        Assert.Equal(1, rig.Reconnect.Attempts);
        Assert.NotSame(old, rig.Login.Session);
        Assert.True(rig.Reconnect.HasLogin);   // the new session can drop in its turn
        Assert.Empty(rig.Pauses);
    }

    [Fact]
    public async Task A_failed_first_attempt_waits_three_seconds_then_tries_the_last_location_and_no_more()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        rig.Fake.OnLogin = (r, _) => Task.FromResult(r.StartLocation == "last" ? FakeTransport.Ok(Self) : TransportLoginResult.Failed("connect", "You appear to be already logged in."));
        Assert.True(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal(["last"], rig.Fake.Logins.Skip(1).Select(l => l.StartLocation).Skip(1));
        Assert.Equal(3, rig.Fake.Logins.Count);
        Assert.Equal([TimeSpan.FromSeconds(3)], rig.Pauses);
        Assert.Equal(2, rig.Reconnect.Attempts);
    }

    [Fact]
    public async Task Two_failures_end_the_click_with_the_reason_shown_and_a_second_click_is_a_new_pair_of_attempts_never_a_loop()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        rig.Fake.OnLogin = (_, _) => Task.FromResult(TransportLoginResult.Failed("connect", "The grid is not ready."));
        Assert.False(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal(3, rig.Fake.Logins.Count);   // the first login and two attempts
        Assert.Equal(ReconnectState.Failed, rig.Reconnect.State);
        Assert.Contains("The grid is not ready.", rig.Reconnect.Status);
        await Task.Delay(50);
        Assert.Equal(3, rig.Fake.Logins.Count);   // nothing tries again by itself
        Assert.False(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal(5, rig.Fake.Logins.Count);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("tos")]
    [InlineData("update")]
    public async Task A_refusal_of_the_credentials_or_the_terms_ends_it_after_one_attempt(string code)
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        rig.Fake.OnLogin = (_, _) => Task.FromResult(TransportLoginResult.Failed(code, "refused"));
        Assert.False(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal(2, rig.Fake.Logins.Count);
        Assert.Equal(1, rig.Reconnect.Attempts);
        Assert.Empty(rig.Pauses);
    }

    [Fact]
    public async Task With_no_known_place_the_only_attempt_starts_at_the_last_location()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop(withAvatar: false);
        Assert.True(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal("last", rig.Fake.Logins.ElementAtOrDefault(1)?.StartLocation);
    }

    [Fact]
    public async Task A_second_click_while_one_is_going_on_does_nothing()
    {
        var rig = new Rig();
        var old = await rig.LogInAndDrop();
        var release = new TaskCompletionSource<TransportLoginResult>();
        var started = new TaskCompletionSource();
        rig.Fake.OnLogin = (_, _) => { started.TrySetResult(); return release.Task; };
        var first = rig.Reconnect.RunAsync(old.Drop, CancellationToken.None);
        Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(60))));   // the login began
        Assert.Equal(ReconnectState.Trying, rig.Reconnect.State);
        Assert.False(await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None));
        Assert.Equal(2, rig.Fake.Logins.Count);
        release.SetResult(FakeTransport.Ok(Self));
        Assert.True(await first);
    }

    [Fact]
    public async Task A_drop_alone_never_logs_in_again_and_the_login_screens_fields_are_as_they_were()
    {
        var rig = new Rig();
        await rig.LogInAndDrop();
        await Task.Delay(50);
        Assert.Single(rig.Fake.Logins);
        Assert.Equal(("Test", "User", "https://grid.example.org:8002/"), (rig.Login.FirstName, rig.Login.LastName, rig.Login.LoginUri));
    }

    [Fact]
    public async Task The_password_is_in_memory_only_nothing_under_the_settings_folder_holds_it_and_the_login_model_itself_keeps_none()
    {
        string dir = TestTemp.NewFolder("reconnect");
        try
        {
            var rig = new Rig(dir);
            var old = await rig.LogInAndDrop();
            await rig.Reconnect.RunAsync(old.Drop, CancellationToken.None);
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain(Password, File.ReadAllText(file));
            Assert.False(rig.Login.HasPassword);
            foreach (var f in typeof(LoginModel).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                Assert.False(f.GetValue(rig.Login) is Secret, $"{f.Name} holds a Secret");
        }
        finally { TestTemp.Delete(dir); }
    }
}
