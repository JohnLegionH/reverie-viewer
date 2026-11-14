// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using System.Text;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The grid field: tidying what was typed, resolving a base or grid-info address to the login address, remembering it,
/// and saying in plain words when an address is not a login service. The server is a loopback listener with routes.</summary>
public class GridAddressTests
{
    private const string GridInfo = "<gridinfo><login>{0}</login><gridname>Example Grid</gridname><gatekeeper>http://gk.example.org/</gatekeeper></gridinfo>";
    // LibreMetaverse posts the login as LLSD XML, and a grid answers in kind.
    private const string Refusal = "<llsd><map><key>login</key><string>false</string><key>reason</key><string>key</string><key>message</key><string>Could not authenticate your avatar.</string></map></llsd>";

    // ---------------- tidying ----------------

    [Theory]
    [InlineData("http://grid.example.org:8002/", "http://grid.example.org:8002/")]
    [InlineData("  http://grid.example.org:8002/  ", "http://grid.example.org:8002/")]
    [InlineData("http://grid.example.org:8002/get_grid_info`", "http://grid.example.org:8002/get_grid_info")]
    [InlineData("`http://grid.example.org:8002/`", "http://grid.example.org:8002/")]
    [InlineData("\"http://grid.example.org:8002/\"", "http://grid.example.org:8002/")]
    [InlineData("'http://grid.example.org:8002/'", "http://grid.example.org:8002/")]
    [InlineData("“http://grid.example.org:8002/”", "http://grid.example.org:8002/")]
    [InlineData("<https://grid.example.org/>", "https://grid.example.org/")]
    public void Stray_quotes_backticks_and_spaces_at_the_ends_are_removed(string typed, string want)
    {
        var t = GridAddress.Tidy(typed);
        Assert.Null(t.ProblemKey);
        Assert.Equal(want, t.Uri!.ToString());
    }

    [Theory]
    [InlineData("grid.example.org:8002")]
    [InlineData("grid.example.org")]
    [InlineData("  grid.example.org:8002/  ")]
    public void A_missing_scheme_is_said_plainly_not_guessed(string typed)
    {
        var t = GridAddress.Tidy(typed);
        Assert.Null(t.Uri);
        Assert.Equal("login.status.no_scheme", t.ProblemKey);
        Assert.Contains("http://", StringTable.English().Get("login.status.no_scheme"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://grid.example.org/")]
    [InlineData("http://grid.example.org/a`b")]
    [InlineData("http://grid.example.org/a b")]
    [InlineData("http://")]
    public void Anything_else_that_is_not_an_http_address_is_a_bad_address(string typed)
    {
        var t = GridAddress.Tidy(typed);
        Assert.Null(t.Uri);
        Assert.Equal("login.status.bad_uri", t.ProblemKey);
    }

    // ---------------- grid info ----------------

    [Theory]
    [InlineData("http://grid.example.org:8002/", "http://grid.example.org:8002/get_grid_info")]
    [InlineData("http://grid.example.org:8002", "http://grid.example.org:8002/get_grid_info")]
    [InlineData("http://grid.example.org:8002/get_grid_info", "http://grid.example.org:8002/get_grid_info")]
    [InlineData("http://grid.example.org/sub/", "http://grid.example.org/sub/get_grid_info")]
    public void Grid_info_is_asked_of_the_typed_address_only(string typed, string want) =>
        Assert.Equal(want, GridAddress.GridInfoOf(new Uri(typed)).ToString());

    [Fact]
    public void The_login_value_is_read_from_grid_info()
    {
        Assert.Equal("http://login.example.org:8002/", GridAddress.LoginOfGridInfo(string.Format(GridInfo, "http://login.example.org:8002/"))!.ToString());
        Assert.Null(GridAddress.LoginOfGridInfo("<html><body>Not found</body></html>"));
        Assert.Null(GridAddress.LoginOfGridInfo(""));
        Assert.Null(GridAddress.LoginOfGridInfo("<gridinfo><gridname>x</gridname></gridinfo>"));
        Assert.Null(GridAddress.LoginOfGridInfo("<gridinfo><login>not an address</login></gridinfo>"));
    }

    [Fact]
    public async Task A_base_address_resolves_through_the_grids_grid_info()
    {
        using var server = new RouteServer();
        server.Route("/get_grid_info", 200, "text/xml", string.Format(GridInfo, "http://login.example.org:8002/"));
        var r = await GridAddress.ResolveAsync(new Uri(server.Url), Fetch(), CancellationToken.None);
        Assert.Equal("http://login.example.org:8002/", r.Login!.ToString());
        Assert.True(r.FromGridInfo);
        Assert.Equal(["/get_grid_info"], server.Paths);
    }

    [Fact]
    public async Task A_grid_info_address_resolves_to_the_login_it_publishes_and_asks_nothing_else()
    {
        using var server = new RouteServer();
        server.Route("/get_grid_info", 200, "text/xml", string.Format(GridInfo, "http://login.example.org:8002/"));
        var r = await GridAddress.ResolveAsync(new Uri(server.Url + "get_grid_info"), Fetch(), CancellationToken.None);
        Assert.Equal("http://login.example.org:8002/", r.Login!.ToString());
        Assert.Equal(["/get_grid_info"], server.Paths);
    }

    [Fact]
    public async Task A_base_address_without_grid_info_is_taken_as_the_login_service_itself()
    {
        using var server = new RouteServer();
        var r = await GridAddress.ResolveAsync(new Uri(server.Url), Fetch(), CancellationToken.None);
        Assert.Equal(server.Url, r.Login!.ToString());
        Assert.False(r.FromGridInfo);
        Assert.Equal(404, r.Status);
    }

    [Theory]
    [InlineData(404, "", "text/plain")]
    [InlineData(200, "<html><body>Welcome</body></html>", "text/html")]
    [InlineData(200, "", "text/xml")]
    public async Task A_grid_info_address_that_is_not_grid_info_is_not_a_login_service(int status, string body, string type)
    {
        using var server = new RouteServer();
        server.Route("/get_grid_info", status, type, body);
        var r = await GridAddress.ResolveAsync(new Uri(server.Url + "get_grid_info"), Fetch(), CancellationToken.None);
        Assert.Null(r.Login);
        Assert.Equal("login.status.not_login_service", r.ProblemKey);
        Assert.Equal(status, r.Status);
    }

    // ---------------- the login screen's model ----------------

    private static LoginModel Model(string typed, Func<SessionOptions, ViewerSession>? factory = null, string? settings = null)
    {
        factory ??= opts =>
        {
            var log = new CoreLog(new ListSink(), "test");
            return new ViewerSession(opts, ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(opts.Outbound), log), log);
        };
        var m = new LoginModel(StringTable.English(), factory, Fetch(), settings) { LoginUri = typed, FirstName = "Test", LastName = "User" };
        m.SetPassword("pw");
        return m;
    }

    private static GridFetcher Fetch() =>
        GridAddress.HttpFetcher(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"), TimeSpan.FromSeconds(10));

    private static string NewDir() => TestTemp.NewFolderPath("grid-address");

    [Fact]
    public async Task The_pasted_address_with_a_stray_backtick_logs_in_at_the_grid_info_login()
    {
        using var server = new RouteServer();
        server.Route("/get_grid_info", 200, "text/xml", string.Format(GridInfo, server.Url));
        var fake = new FakeTransport();
        var m = Model(server.Url + "get_grid_info`", opts => new ViewerSession(opts, fake, new CoreLog(new ListSink(), "test")));
        Assert.True(await m.LoginAsync());
        Assert.Equal(server.Url, m.ResolvedLoginUri);
        Assert.Equal(server.Url, fake.Logins.Single().LoginUri);   // credentials went to that address only
        Assert.Equal(["/get_grid_info"], server.Paths);
    }

    [Fact]
    public async Task A_missing_scheme_never_reaches_the_network()
    {
        using var server = new RouteServer();
        var m = Model("127.0.0.1:" + new Uri(server.Url).Port);
        Assert.False(await m.LoginAsync());
        Assert.Equal(StringTable.English().Get("login.status.no_scheme"), m.Status);
        Assert.Empty(server.Paths);
    }

    [Theory]
    [InlineData(404, "text/plain", "", "HTTP 404")]
    [InlineData(200, "text/html", "<html><body>Hello</body></html>", "HTTP 200")]
    public async Task A_reply_that_is_not_a_login_reply_is_said_in_plain_words_with_its_status(int status, string type, string body, string said)
    {
        using var server = new RouteServer();
        server.RouteAll(status, type, body);
        var m = Model(server.Url);
        Assert.False(await m.LoginAsync());
        Assert.Equal($"That address didn't answer as a grid login service ({said}).", m.Status);
        Assert.DoesNotContain("deserialize", m.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unrecognized", m.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_real_login_refusal_is_passed_on_as_the_grids_own_message()
    {
        using var server = new RouteServer();
        server.RouteAll(200, "text/xml", Refusal);
        var m = Model(server.Url);
        Assert.False(await m.LoginAsync());
        Assert.Contains("Could not authenticate your avatar", m.Status);
        Assert.DoesNotContain("didn't answer", m.Status);
    }

    [Fact]
    public async Task A_successful_login_remembers_the_address_and_the_next_start_fills_it_in()
    {
        using var server = new RouteServer();
        server.Route("/get_grid_info", 200, "text/xml", string.Format(GridInfo, server.Url));
        string dir = NewDir();
        try
        {
            var fake = new FakeTransport();
            var m = Model("  " + server.Url + "`", opts => new ViewerSession(opts, fake, new CoreLog(new ListSink(), "test")), dir);
            Assert.True(await m.LoginAsync());
            Assert.Equal(server.Url, File.ReadAllText(Path.Combine(dir, LoginAddressStore.FileName)));
            Assert.Equal(server.Url, LoginModel.StartAddress(null, dir));
        }
        finally { if (Directory.Exists(dir)) TestTemp.Delete(dir); }
    }

    [Fact]
    public async Task A_failed_login_remembers_nothing()
    {
        using var server = new RouteServer();
        server.RouteAll(200, "text/xml", Refusal);
        string dir = NewDir();
        try
        {
            Assert.False(await Model(server.Url, settings: dir).LoginAsync());
            Assert.False(File.Exists(Path.Combine(dir, LoginAddressStore.FileName)));
            Assert.Equal("", LoginModel.StartAddress(null, dir));
        }
        finally { if (Directory.Exists(dir)) TestTemp.Delete(dir); }
    }

    [Fact]
    public void The_grid_option_wins_over_the_remembered_address_and_a_bad_option_is_ignored()
    {
        string dir = NewDir();
        try
        {
            LoginAddressStore.Save(dir, "http://remembered.example.org:8002/");
            Assert.Equal("http://remembered.example.org:8002/", LoginModel.StartAddress(null, dir));
            Assert.Equal("http://given.example.org:8002/", LoginModel.StartAddress("http://given.example.org:8002/`", dir));
            Assert.Equal("http://remembered.example.org:8002/", LoginModel.StartAddress("not an address", dir));
            Assert.Equal("", LoginModel.StartAddress(null, null));
        }
        finally { if (Directory.Exists(dir)) TestTemp.Delete(dir); }
    }

    [Fact]
    public void An_unreadable_remembered_file_is_no_address()
    {
        string dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, LoginAddressStore.FileName), "not an address at all");
            Assert.Null(LoginAddressStore.Load(dir));
            Assert.Null(LoginAddressStore.Load(Path.Combine(dir, "missing")));
        }
        finally { if (Directory.Exists(dir)) TestTemp.Delete(dir); }
    }

    /// <summary>A loopback HTTP server with routes; anything else answers an empty 404.</summary>
    private sealed class RouteServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, (int Status, string Type, string Body)> _routes = new();
        private (int Status, string Type, string Body)? _all;
        public string Url { get; }
        public List<string> Paths { get; } = new();

        public RouteServer()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    string path = ctx.Request.Url!.AbsolutePath;
                    lock (Paths) Paths.Add(path);
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) await reader.ReadToEndAsync();
                    (int Status, string Type, string Body) r = _all ?? (_routes.TryGetValue(path, out var x) ? x : (404, "text/plain", ""));
                    ctx.Response.StatusCode = r.Status;
                    ctx.Response.ContentType = r.Type;
                    var bytes = Encoding.UTF8.GetBytes(r.Body);
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Route(string path, int status, string type, string body) => _routes[path] = (status, type, body);
        public void RouteAll(int status, string type, string body) => _all = (status, type, body);

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* stopped */ }
            ((IDisposable)_listener).Dispose();
        }
    }
}
