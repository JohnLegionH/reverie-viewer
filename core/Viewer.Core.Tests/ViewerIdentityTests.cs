// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using System.Text;
using LibreMetaverse;
using Viewer.Core.Diagnostics;
using Viewer.Core.Identity;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// ADR-019 honest identity. Ports the web gateway's ViewerIdentityTests (version adapted to the core assembly; the
/// channel test inverted: our own channel, not "web-viewer-dev") and LoginTransportTests (the login request travels on
/// HttpCapsClient, so the outbound policy covers it), then checks the actual login request on the wire against a
/// loopback fake login service: no grid is contacted.
/// </summary>
public class ViewerIdentityTests(ITestOutputHelper output)
{
    /// <summary>Channels other viewers (and the web gateway, and LibreMetaverse's defaults) send. Never ours.</summary>
    private static readonly string[] OtherViewers =
    [
        "Second Life Release", "Second Life", "Firestorm", "Singularity", "Alchemy", "Cool VL Viewer", "Kokua",
        "Catznip", "Black Dragon", "web-viewer-dev", "libopenmetaverse", "OpenMetaverse", "LibreMetaverse",
    ];

    [Fact]
    public void ViewerVersion_is_the_core_version_and_a_build_number_unique_to_each_commit()
    {
        // Third-Party Viewer Policy 1.b: a unique identifier for each version. The official viewer's version is
        // major.minor.patch.build (llversioninfo.cpp); ours is the core's three-part version and the number of commits
        // in the history the build was made from, so each commit on main has its own.
        var threePart = typeof(ViewerIdentity).Assembly.GetName().Version!.ToString(3);
        string v = ViewerIdentity.Current.Version;
        output.WriteLine("version: " + v);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", v);
        Assert.StartsWith(threePart + ".", v);
        Assert.Equal(CommitCount(), v[(threePart.Length + 1)..]);
    }

    /// <summary>The commit count of the checkout these tests were built from ("0" outside a git checkout, as the build does).</summary>
    private static string CommitCount()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        if (dir is null)
            return "0";
        var psi = new System.Diagnostics.ProcessStartInfo("git", $"-C \"{dir.FullName}\" rev-list --count HEAD")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var git = System.Diagnostics.Process.Start(psi)!;
        string count = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        return git.ExitCode == 0 && count.Length > 0 ? count : "0";
    }

    [Fact]
    public void ViewerChannel_is_our_own_and_never_another_viewers()
    {
        var id = ViewerIdentity.Current;
        Assert.StartsWith(ViewerIdentity.ProductName, id.Channel);
        Assert.DoesNotContain(OtherViewers, o => string.Equals(o, id.Channel, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ViewerIdentity.ProductName.Replace(" ", "") + "/" + id.Version, id.UserAgent);   // a product token has no spaces
        Assert.Equal(id, new SessionOptions { LoginUri = "http://g/" }.Identity);
    }

    [Fact]
    public void Login_params_carry_our_identity()
    {
        using var client = new GridClient();
        var lp = LmvTransport.BuildLoginParams(client, new TransportLoginRequest(
            "http://login.example:8002/", "Test", "Resident", new Secret("pw", SensitiveKind.PasswordDigest), "last", ViewerIdentity.Current));
        Assert.Equal(ViewerIdentity.Current.Channel, lp.Channel);
        Assert.Equal(ViewerIdentity.Current.Version, lp.Version);
        Assert.Equal(ViewerIdentity.Current.UserAgent, lp.UserAgent);
        Assert.Equal(ViewerIdentity.Current.UserAgent, lp.Author);
    }

    /// <summary>
    /// A real session over the real adapter (strict policy, loopback exempt) logs in against a loopback listener that
    /// answers with a login fault. The request that arrived carries our channel, version and user agent, and none of
    /// the other viewers'; and it travelled through the adapter's policy handler (LoginTransportTests' question).
    /// </summary>
    [Fact]
    public async Task The_login_request_on_the_wire_carries_our_identity_and_travels_on_the_policy_handler()
    {
        using var server = new FakeLoginService();
        var sink = new ListSink();
        var options = new SessionOptions
        {
            LoginUri = server.Url,
            Outbound = new OutboundOptions { Policy = "strict", AllowedInternalEndpoints = ["127.0.0.1"] },
        };
        var log = new CoreLog(sink, "test");
        using (var session = new ViewerSession(options, ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(options.Outbound), log), log))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var outcome = await session.LoginAsync(new("Test", "Resident", new Secret("pw", SensitiveKind.PasswordDigest)), cts.Token);
            Assert.False(outcome.Ok);   // the fake service refuses
        }

        Assert.True(server.Requests.Count > 0, "the fake login service was never contacted, so this test proves nothing");
        var (headers, body) = server.Requests[0];
        output.WriteLine(headers);
        output.WriteLine(body.Length > 2000 ? body[..2000] : body);

        var id = ViewerIdentity.Current;
        // LibreMetaverse 3.1.6 sends the login as LLSD XML (<key>name</key><string>value</string>), not XML-RPC.
        Assert.Contains($"<key>channel</key><string>{id.Channel}</string>", Compact(body));
        Assert.Contains($"<key>version</key><string>{id.Version}</string>", Compact(body));
        // LibreMetaverse 3.1.6 does not put LoginParams.UserAgent into the HTTP header (measured); whatever
        // header it sends must at least not name another viewer.
        foreach (var other in OtherViewers.Where(o => o is not ("LibreMetaverse" or "OpenMetaverse" or "libopenmetaverse")))
            Assert.DoesNotContain(other, headers, StringComparison.OrdinalIgnoreCase);
        foreach (var other in OtherViewers)
            Assert.DoesNotContain($"<string>{other}</string>", Compact(body), StringComparison.OrdinalIgnoreCase);

        // Whether this request travelled on the adapter's policy handler (LoginTransportTests' question) is answered
        // by the next test: with loopback NOT exempt, the same request never arrives.
    }

    /// <summary>
    /// The password is hashed whole on a grid that is not Second Life: an OpenSim account may have a password of any length,
    /// and the login service hashes all of it. LibreMetaverse 3.2.1 hashed only the first 16 characters on every grid, which
    /// locked such accounts out; 3.3.0 does that on Second Life only.
    /// </summary>
    [Theory]
    [InlineData("Abcdefghij1234567")]                      // 17 characters, one past the Second Life limit
    [InlineData("Parting3-Regally-Unending-Stock-Pass")]   // 36 characters
    public async Task A_long_password_is_hashed_whole_on_a_grid_that_is_not_Second_Life(string password)
    {
        using var server = new FakeLoginService();
        var log = new CoreLog(new ListSink(), "test");
        var options = new SessionOptions
        {
            LoginUri = server.Url,
            Outbound = new OutboundOptions { Policy = "strict", AllowedInternalEndpoints = ["127.0.0.1"] },
        };
        using (var session = new ViewerSession(options, ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(options.Outbound), log), log))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.LoginAsync(new("Test", "Resident", new Secret(password, SensitiveKind.PasswordDigest)), cts.Token);
        }

        Assert.True(server.Requests.Count > 0, "the fake login service was never contacted, so this test proves nothing");
        var body = Compact(server.Requests[0].Body);
        static string Md5(string s) => "$1$" + Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        Assert.Contains($"<key>passwd</key><string>{Md5(password)}</string>", body);
        Assert.DoesNotContain(Md5(password[..16]), body);
    }

    [Fact]
    public async Task With_loopback_not_exempt_the_policy_handler_refuses_the_login_transport_itself()
    {
        // The session's own pre-check would refuse 127.0.0.1 first, so call the adapter directly: this isolates the
        // HTTP path LibreMetaverse uses for the login request.
        using var server = new FakeLoginService();
        var log = new CoreLog(new ListSink(), "test");
        using var transport = ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(new OutboundOptions { Policy = "strict" }), log);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await transport.LoginAsync(new TransportLoginRequest(server.Url, "Test", "Resident",
                new Secret("pw", SensitiveKind.PasswordDigest), "last", ViewerIdentity.Current), cts.Token);
        }
        catch (Exception ex)
        {
            output.WriteLine("login threw: " + ex.GetType().Name);
        }
        Assert.Empty(server.Requests);
    }

    private static string Compact(string xml) => string.Concat(xml.Where(c => !char.IsWhiteSpace(c) || c == ' ')).Replace("> <", "><");

    /// <summary>A loopback XML-RPC login service that records each request and answers with a login fault.</summary>
    private sealed class FakeLoginService : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public List<(string Headers, string Body)> Requests { get; } = new();

        public FakeLoginService()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
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
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var headers = string.Join("\n", ctx.Request.Headers.AllKeys.Select(k => $"{k}: {ctx.Request.Headers[k]}"));
                    lock (Requests) Requests.Add((headers, body));
                    var reply = Encoding.UTF8.GetBytes(
                        "<?xml version=\"1.0\"?><methodResponse><params><param><value><struct>" +
                        "<member><name>login</name><value><string>false</string></value></member>" +
                        "<member><name>reason</name><value><string>key</string></value></member>" +
                        "<member><name>message</name><value><string>test refusal</string></value></member>" +
                        "</struct></value></param></params></methodResponse>");
                    ctx.Response.ContentType = "text/xml";
                    await ctx.Response.OutputStream.WriteAsync(reply);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* stopped */ }
            ((IDisposable)_listener).Dispose();
        }
    }
}
