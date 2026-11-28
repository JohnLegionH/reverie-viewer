// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The login screen takes one username field, read the way the official viewer reads it: the text is trimmed, split at the
/// first space, dot or underscore into first and last name, and a single word gets the last name "Resident".
/// A last name that still holds a space is refused before any login is sent. The saved name is one text, never the password.
/// </summary>
public class FirstMinuteLoginTests
{
    private static readonly StringTable En = StringTable.English();

    [Theory]
    [InlineData("Test User", "Test", "User")]
    [InlineData("  Test User  ", "Test", "User")]
    [InlineData("test.user", "test", "user")]
    [InlineData("Test_User", "Test", "User")]
    [InlineData("Test  User", "Test", "User")]
    [InlineData("Test", "Test", "Resident")]
    [InlineData("  Test ", "Test", "Resident")]
    public void A_typed_name_is_split_the_way_the_official_viewer_splits_it(string typed, string first, string last)
    {
        var name = LoginName.Parse(typed);
        Assert.NotNull(name);
        Assert.Equal((first, last), (name!.First, name.Last));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Test User Extra")]
    [InlineData("Test.")]
    [InlineData(".User")]
    public void A_name_that_cannot_be_split_into_first_and_last_is_refused(string? typed) => Assert.Null(LoginName.Parse(typed));

    [Theory]
    [InlineData("Test", "User", "Test User")]
    [InlineData("Test", "Resident", "Test")]
    [InlineData("Test", "resident", "Test")]
    public void The_name_shown_again_drops_the_last_name_Resident(string first, string last, string shown) =>
        Assert.Equal(shown, LoginName.Display(first, last));

    [Fact]
    public void The_login_screen_has_one_username_field_where_the_two_name_fields_were()
    {
        Assert.Equal(["grid", "username", "password", "remember", "remember_password", "start", "start_region", "login", "cancel", "status"], LoginScreenSpec.Controls.Select(c => c.Id));
        var field = Assert.Single(LoginScreenSpec.Controls, c => c.Id == "username");
        Assert.Equal(LoginControlKind.Text, field.Kind);
        Assert.False(string.IsNullOrWhiteSpace(En.Get(field.LabelKey)));
        Assert.False(string.IsNullOrWhiteSpace(En.Get(field.HintKey!)));
    }

    private static (LoginModel Model, FakeTransport Fake) Model()
    {
        var fake = new FakeTransport();
        var m = new LoginModel(En, opts => new ViewerSession(opts, fake, new CoreLog(new ListSink(), "test")))
        {
            LoginUri = "https://grid.invalid:8002/",
        };
        m.SetPassword("pw-not-real");
        return (m, fake);
    }

    [Fact]
    public async Task The_username_field_reaches_the_login_as_first_and_last_name()
    {
        var (m, fake) = Model();
        m.UserName = " Alice.Test ";
        Assert.True(await m.LoginAsync());
        var login = Assert.Single(fake.Logins);
        Assert.Equal(("Alice", "Test"), (login.FirstName, login.LastName));
        m.Session!.Dispose();
    }

    [Fact]
    public async Task A_single_name_is_sent_with_the_last_name_Resident()
    {
        var (m, fake) = Model();
        m.UserName = "Solo";
        Assert.True(await m.LoginAsync());
        var login = Assert.Single(fake.Logins);
        Assert.Equal(("Solo", "Resident"), (login.FirstName, login.LastName));
        m.Session!.Dispose();
    }

    [Fact]
    public async Task An_empty_username_is_reported_by_its_own_label_and_nothing_is_sent()
    {
        var (m, fake) = Model();
        m.UserName = "  ";
        Assert.False(await m.LoginAsync());
        Assert.Equal(En.Get("login.status.missing", En.Get("login.username.label")), m.Status);
        Assert.Empty(fake.Logins);
    }

    [Fact]
    public async Task A_name_with_too_many_words_says_so_and_nothing_is_sent()
    {
        var (m, fake) = Model();
        m.UserName = "One Two Three";
        Assert.False(await m.LoginAsync());
        Assert.Equal(En.Get("login.status.bad_name"), m.Status);
        Assert.Equal("invalid", m.LastFailureCode);
        Assert.Empty(fake.Logins);
        Assert.True(m.HasPassword);   // not consumed: the person corrects the field and retries
    }

    [Fact]
    public async Task After_a_logout_the_login_screen_no_longer_says_you_are_in_world()
    {
        var (m, _) = Model();
        m.UserName = "Test User";
        Assert.True(await m.LoginAsync());
        m.Announce(En.Get("login.status.in_world"));   // as a login that reached the world leaves the line; the fake stops short of it
        var told = new List<string>();
        m.StatusChanged += told.Add;
        await m.Session!.LogoutAsync(CancellationToken.None);
        Assert.Equal(En.Get("login.status.ready"), m.Status);
        Assert.Contains(En.Get("login.status.ready"), told);   // the screen is told, not only the property changed
        m.Session!.Dispose();
    }

    // The engine host cannot run in the unit tests, so this checks the wiring in the source: the tags are drawn over the main view, which
    // outlives the world, and the frame loop that plans them stops with the world.
    [Fact]
    public void The_name_tags_are_hidden_as_the_world_is_taken_down()
    {
        string main = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "Main.cs"));
        var teardown = System.Text.RegularExpressions.Regex.Match(main, @"private void Unpresent\(\)\s*\{(.*?)\r?\n    \}", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(teardown.Success, "Unpresent was not found");
        Assert.Contains("_nameTags?.HideAll()", teardown.Groups[1].Value);
    }

    [Fact]
    public void The_saved_name_is_one_text_kept_only_when_asked_for()
    {
        var s = new SettingsService(null);
        LoginNames.Save(s, remember: true, "Test User");
        Assert.Equal("Test User", s.GetText(Settings.LoginUserNameKey));
        Assert.Equal("Test User", LoginNames.Load(s));
        LoginNames.Save(s, remember: false, "Test User");
        Assert.Equal("", s.GetText(Settings.LoginUserNameKey));
        Assert.Equal("", LoginNames.Load(s));
    }

    [Fact]
    public void A_name_saved_as_first_and_last_by_an_earlier_run_is_read_as_one_username()
    {
        var s = new SettingsService(null);
        s.SetText(Settings.LoginFirstNameKey, "Test");
        s.SetText(Settings.LoginLastNameKey, "User");
        Assert.Equal("Test User", LoginNames.Load(s));
        s.SetText(Settings.LoginLastNameKey, "Resident");
        Assert.Equal("Test", LoginNames.Load(s));
    }

    [Fact]
    public void Forgetting_the_saved_name_clears_the_username_and_the_older_pair()
    {
        var s = new SettingsService(null);
        s.SetText(Settings.LoginFirstNameKey, "Test");
        s.SetText(Settings.LoginLastNameKey, "User");
        s.SetText(Settings.LoginUserNameKey, "Test User");
        Assert.True(LoginNames.Clear(s));
        Assert.Equal("", LoginNames.Load(s));
        Assert.Equal("", s.GetText(Settings.LoginUserNameKey));
        Assert.Equal("", s.GetText(Settings.LoginFirstNameKey));
    }
}
