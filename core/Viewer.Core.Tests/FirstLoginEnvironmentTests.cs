// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse.StructuredData;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The official viewer switches SwitchToSharedEnvAfterTeleport on, once and for good, when the login reply says the account has never
/// logged in before (login-flags, ever_logged_in "N": llstartup.cpp, then LLAgent::setFirstLogin). Any other reply leaves the
/// setting as it was; it is never switched off by a login. Plain .NET.
/// </summary>
public class FirstLoginEnvironmentTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string t) { Text = t; return true; }
    }

    private static OSDMap ReplyWith(string? everLoggedIn)
    {
        var map = new OSDMap { ["login"] = OSD.FromString("true") };
        if (everLoggedIn is not null)
            map["login-flags"] = new OSDArray { new OSDMap { ["ever_logged_in"] = OSD.FromString(everLoggedIn), ["gendered"] = OSD.FromString("Y") } };
        return map;
    }

    [Theory]
    [InlineData("N", true)]
    [InlineData("Y", false)]
    public void The_login_reply_flag_ever_logged_in_gives_the_first_login_state(string flag, bool first)
    {
        Assert.Equal(first, LmvLoginTap.ReplyOf(ReplyWith(flag)).FirstLogin);
    }

    [Fact]
    public void A_reply_without_the_flag_or_with_an_empty_one_says_nothing_about_a_first_login()
    {
        Assert.Null(LmvLoginTap.ReplyOf(ReplyWith(null)).FirstLogin);
        Assert.Null(LmvLoginTap.ReplyOf(ReplyWith("")).FirstLogin);
        var emptyList = ReplyWith(null);
        emptyList["login-flags"] = new OSDArray();
        Assert.Null(LmvLoginTap.ReplyOf(emptyList).FirstLogin);
    }

    private static async Task<SettingsService> LoginWith(bool? firstLogin, bool settingBefore)
    {
        var settings = new SettingsService(new MemoryStore());
        if (settingBefore) settings.Set(Settings.SwitchToSharedEnvAfterTeleport, true);
        var fake = new FakeTransport
        {
            OnLogin = (_, _) => Task.FromResult(FakeTransport.Ok(Guid.NewGuid()) with { Reply = LoginReply.Empty with { FirstLogin = firstLogin } }),
        };
        var m = new LoginModel(StringTable.English(), opts => new ViewerSession(opts, fake, new CoreLog(new ListSink(), "test")))
        {
            LoginUri = "https://grid.invalid:8002/",
            FirstName = "Test",
            LastName = "User",
            UserSettings = settings,
        };
        m.SetPassword("pw-not-real");
        Assert.True(await m.LoginAsync());
        m.Session!.Dispose();
        return settings;
    }

    [Fact]
    public async Task A_first_login_switches_the_shared_environment_after_teleport_on()
    {
        Assert.True((await LoginWith(firstLogin: true, settingBefore: false)).Get(Settings.SwitchToSharedEnvAfterTeleport));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(null, false)]
    [InlineData(false, true)]
    [InlineData(null, true)]
    public async Task A_login_that_is_not_a_first_one_leaves_the_setting_as_it_was(bool? firstLogin, bool before)
    {
        Assert.Equal(before, (await LoginWith(firstLogin, before)).Get(Settings.SwitchToSharedEnvAfterTeleport));
    }
}
