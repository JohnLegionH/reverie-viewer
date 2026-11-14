// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What the login screen says when the grid refuses a login, against the official viewer: the text shown is the login failure header, a
/// line break, and the message the grid sent, exactly (llstartup.cpp 1153-1211: "LoginFailedHeader\n" then the grid's message, when it
/// names no template of its own; strings.xml: LoginFailedHeader is "Login failed."). The reason code (key, presence, connect) picks no
/// text of its own, and a refusal that carries no message shows the header alone. Plain .NET; every member builds its own objects.
/// </summary>
public class LoginFailureMessageTests
{
    private static readonly StringTable En = StringTable.English();

    private static LoginModel Model(TransportLoginResult result)
    {
        var fake = new FakeTransport { OnLogin = (_, _) => Task.FromResult(result) };
        var m = new LoginModel(En, opts => new ViewerSession(opts, fake, new CoreLog(new ListSink(), "test")))
        {
            LoginUri = "https://grid.invalid:8002/", FirstName = "Test", LastName = "User", StartLocation = "last",
        };
        m.SetPassword("a password");
        return m;
    }

    [Fact]
    public async Task A_refusal_for_a_destination_that_is_down_shows_the_grids_own_words_under_the_header()
    {
        const string message = "The region you are trying to log into is not available. Please try another start location.";
        var m = Model(TransportLoginResult.Failed("key", message));
        Assert.False(await m.LoginAsync());
        Assert.Equal("Login failed.\n" + message, m.Status);
        Assert.Equal("key", m.LastFailureCode);
    }

    [Fact]
    public async Task A_region_that_refuses_the_launch_shows_and_logs_the_grids_words_so_it_does_not_read_as_a_wrong_password()
    {
        // the reply a grid sends when the login region's server does not answer: reason "key", as for a refused password
        // (OpenSim LLLoginService: a failed launch answers LLFailedLoginResponse("key", reason, "false"))
        const string message = "Service request failed: [499] No connection could be made because the target machine actively refused it. (region.example.org:9000)";
        var sink = new ListSink();
        var fake = new FakeTransport { OnLogin = (_, _) => Task.FromResult(TransportLoginResult.Failed("key", message)) };
        var m = new LoginModel(En, opts => new ViewerSession(opts, fake, new CoreLog(sink, "test")))
        {
            LoginUri = "https://grid.invalid:8002/", FirstName = "Test", LastName = "User", StartLocation = "last",
        };
        m.SetPassword("a password");

        Assert.False(await m.LoginAsync());

        Assert.Equal("Login failed.\n" + message, m.Status);
        var failed = Assert.Single(sink.Entries, e => e.Event == "login.failed");
        string line = failed.Format();
        Assert.Contains("code=key", line);
        Assert.Contains("[499] No connection could be made because the target machine actively refused it.", line);
        Assert.DoesNotContain("a password", sink.Text);
    }

    [Fact]
    public async Task A_refusal_for_a_wrong_password_shows_the_grids_own_words()
    {
        const string message = "Could not authenticate your avatar. Please check your username and password, and check the grid if problems persist.";
        var m = Model(TransportLoginResult.Failed("key", message));
        Assert.False(await m.LoginAsync());
        Assert.Equal("Login failed.\n" + message, m.Status);
    }

    [Fact]
    public async Task A_presence_refusal_shows_the_grids_own_words()
    {
        var m = Model(TransportLoginResult.Failed("presence", "You appear to be already logged in."));
        Assert.False(await m.LoginAsync());
        Assert.Equal("Login failed.\nYou appear to be already logged in.", m.Status);
        Assert.Equal("presence", m.LastFailureCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_refusal_without_a_message_shows_the_header_alone_and_never_the_reason_code(string? message)
    {
        var m = Model(TransportLoginResult.Failed("key", message));
        Assert.False(await m.LoginAsync());
        Assert.Equal("Login failed.", m.Status);
    }

    [Fact]
    public async Task The_message_is_not_reworded_or_trimmed_inside()
    {
        var m = Model(TransportLoginResult.Failed("connect", "Line one.\nLine two, with  two spaces."));
        Assert.False(await m.LoginAsync());
        Assert.Equal("Login failed.\nLine one.\nLine two, with  two spaces.", m.Status);
    }
}

/// <summary>The login screen's "Connecting to" line carries no remark about the address's scheme: neither the official viewer nor
/// Firestorm shows one (their login panels, llpanellogin.cpp and panel_login.xml, hold none).</summary>
public class LoginConnectingLineTests
{
    private static readonly StringTable En = StringTable.English();

    [Fact]
    public void The_line_says_where_it_is_connecting_and_nothing_about_security()
    {
        Assert.Equal("Connecting to grid.example.org", En.Get("login.connecting", "grid.example.org"));
    }

    [Theory]
    [InlineData("login.connecting.secure")]
    [InlineData("login.connecting.insecure")]
    public void The_scheme_remarks_are_gone(string key)
    {
        Assert.StartsWith("[[", En.Get(key, "x"));   // a missing key shows as [[key]]
    }
}
