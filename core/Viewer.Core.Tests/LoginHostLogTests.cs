// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The login service's host and port are left out of the log unless the user turns them on for debugging a connection,
/// so a log someone shares does not say which grid they use.
/// </summary>
public class LoginHostLogTests
{
    private const string Uri = "http://grid.example.org:8002/";

    private static string LoginLog(bool includeHost)
    {
        var sink = new ListSink();
        var s = new ViewerSession(new SessionOptions { LoginUri = Uri, LogLoginHost = includeHost }, new FakeTransport(), new CoreLog(sink, "test"));
        Assert.True(s.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None).GetAwaiter().GetResult().Ok);
        return sink.Text;
    }

    [Fact]
    public void By_default_the_login_line_leaves_out_the_host_and_port()
    {
        string log = LoginLog(includeHost: false);
        Assert.Contains("login.start", log);
        Assert.Contains("(host)", log);
        Assert.DoesNotContain("grid.example.org", log);
        Assert.DoesNotContain("8002", log);
    }

    [Fact]
    public void With_the_switch_on_the_login_line_names_the_host_and_port()
    {
        string log = LoginLog(includeHost: true);
        Assert.Contains("grid.example.org:8002", log);
        Assert.DoesNotContain("(host)", log);
    }

    [Fact]
    public void A_refusal_message_naming_the_host_is_written_without_it()
    {
        Assert.Equal("Login: (host) resolved to nothing", ViewerSession.LoginHostLeftOut("Login: grid.example.org resolved to nothing", Uri, false));
        Assert.Equal("Login: scheme 'ftp' is not allowed (ftp://(host))", ViewerSession.LoginHostLeftOut("Login: scheme 'ftp' is not allowed (ftp://grid.example.org:8002)", Uri, false));
        Assert.Equal("Login: grid.example.org resolved to nothing", ViewerSession.LoginHostLeftOut("Login: grid.example.org resolved to nothing", Uri, true));
        Assert.Equal("bad (login uri) here", ViewerSession.LoginHostLeftOut("bad not a uri here", "not a uri", false));
    }

    [Fact]
    public void The_login_screen_passes_the_switch_to_the_session()
    {
        foreach (bool on in new[] { false, true })
        {
            SessionOptions? seen = null;
            var fake = new FakeTransport();
            var m = new LoginModel(StringTable.English(), opts => { seen = opts; return new ViewerSession(opts, fake, new CoreLog(new ListSink(), "test")); })
            {
                LoginUri = Uri, FirstName = "Test", LastName = "User", LogLoginHost = on,
            };
            m.SetPassword("pw");
            Assert.True(m.LoginAsync().GetAwaiter().GetResult());
            Assert.Equal(on, seen!.LogLoginHost);
        }
    }
}
