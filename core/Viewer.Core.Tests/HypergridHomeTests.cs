// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Notifications;
using Viewer.Shell.Reconnect;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Home while visiting another grid: the teleport home is tried first; if the home grid refuses it, the interface is told (with the
/// grid's words) so it can offer to log out and log back in at home. That offer's OK logs in again at "home" with the credentials held
/// in memory from this login; with none held, nothing is sent and the login screen asks.
/// </summary>
public class HypergridHomeTests
{
    private const string Password = "correct horse battery staple";
    private static readonly StringTable En = StringTable.English();

    private static (TeleportModel M, List<(string Key, string? Arg)> Refusals) Teleports()
    {
        var t = new FakeTransport();
        var pipeline = new ActionPipeline(() => t, () => true, new CoreLog(new ListSink(), "test"));
        var m = new TeleportModel(Guid.NewGuid(), new NameCache(), pipeline.Submit, () => DateTimeOffset.UnixEpoch);
        var refusals = new List<(string, string?)>();
        m.HomeFailed += refusals.Add;
        return (m, refusals);
    }

    [Fact]
    public void A_refused_teleport_home_is_reported_with_the_grids_words()
    {
        var (m, refusals) = Teleports();
        Assert.True(m.Home());
        m.Receive(new TeleportProgressed(TeleportPhase.Failed, "Teleport home is not allowed from this grid.", true));
        Assert.Equal(("teleport.error.raw", (string?)"Teleport home is not allowed from this grid."), Assert.Single(refusals));
    }

    [Fact]
    public void Another_teleports_failure_is_not_a_refused_home()
    {
        var (m, refusals) = Teleports();
        Assert.True(m.ToRegion("Alder", new Vector3(1, 2, 3)));
        m.Receive(new TeleportProgressed(TeleportPhase.Failed, "no_host", true));
        Assert.Empty(refusals);
        Assert.True(m.Home());
        m.Receive(new TeleportProgressed(TeleportPhase.Started, "", true));
        m.Receive(new TeleportProgressed(TeleportPhase.Finished, "", true));
        m.Receive(new TeleportProgressed(TeleportPhase.Failed, "late", true));   // a late failure after the home teleport ended is not it
        Assert.Empty(refusals);
    }

    [Fact]
    public void The_home_refused_dialog_offers_log_out_and_back_in_or_cancel()
    {
        bool relog = false;
        var n = Confirmations.HomeRefused("the reason", () => relog = true);
        Assert.Equal(2, n.Buttons.Count);
        Assert.Equal("home.refused.relog", n.Buttons[0].Label.Key);
        Assert.Equal("button.cancel", n.Buttons[1].Label.Key);
        Assert.Equal(Confirmations.No, n.CancelButton);
        Assert.False(relog);
    }

    private static (LoginModel Login, ReconnectModel Reconnect, FakeTransport Fake) Logins()
    {
        var fake = new FakeTransport();
        var log = new CoreLog(new ListSink(), "test");
        var login = new LoginModel(En, opts => new ViewerSession(opts, fake, log)) { LoginUri = "https://home.example.org:9000/", FirstName = "Test", LastName = "User" };
        var reconnect = new ReconnectModel(login, En, (_, _) => Task.CompletedTask);
        return (login, reconnect, fake);
    }

    [Fact]
    public async Task Log_out_and_back_in_at_home_uses_the_held_login_and_the_home_start()
    {
        var (login, reconnect, fake) = Logins();
        login.SetPassword(Password);
        login.StartLocation = "last";
        Assert.True(await login.LoginAsync());
        var first = login.Session!;
        await first.LogoutAsync(CancellationToken.None);
        Assert.True(await reconnect.HomeAsync(CancellationToken.None));
        Assert.Equal("home", fake.Logins[^1].StartLocation);
        Assert.Equal("https://home.example.org:9000/", fake.Logins[^1].LoginUri);
        Assert.Equal(Password, fake.Logins[^1].Password.Reveal());
        Assert.Equal("last", login.StartLocation);   // the login screen's own choice is left as it was
        login.Session!.Dispose();
        first.Dispose();
    }

    [Fact]
    public async Task With_no_held_login_nothing_is_sent_and_the_login_screen_asks()
    {
        var (_, reconnect, fake) = Logins();
        Assert.False(await reconnect.HomeAsync(CancellationToken.None));
        Assert.Empty(fake.Logins);
        Assert.Equal(ReconnectState.Failed, reconnect.State);
    }
}
