// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using Viewer.Core.Net;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Parity oracle ported from the web gateway (web-viewer bb91ba5, OutboundPolicyTests, all 17), adapted to OutboundOptions.
/// Fixture addresses are neutral (no grid addresses).
/// WV-3d-1: the outbound destination policy. The gateway connects where the grid tells it to -- login URI, simulator
/// address, capability URLs, teleport destinations -- so on a home-hosted deployment that is an SSRF path into the
/// operator's LAN unless something says no.
/// </summary>
public class OutboundPolicyTests
{
    private static OutboundPolicy Strict(params string[] exempt) =>
        new(new OutboundOptions { Policy = "strict", AllowedInternalEndpoints = exempt });

    private static OutboundPolicy Off(params string[] exempt) =>
        new(new OutboundOptions { AllowedInternalEndpoints = exempt });

    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    private static readonly Viewer.Core.Diagnostics.CoreLog Log = new(new ListSink(), "test");

    [Theory]
    // loopback
    [InlineData("127.0.0.1")]
    [InlineData("127.9.9.9")]
    [InlineData("::1")]
    // RFC1918
    [InlineData("10.0.0.7")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.0.10")]
    // link-local
    [InlineData("169.254.169.254")]      // the cloud metadata address, the classic SSRF target
    [InlineData("fe80::1")]
    // CGNAT
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    // multicast, unspecified, broadcast, ULA
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]      // SSDP: every UPnP device on the LAN
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    // IPv4-mapped IPv6 must not be a way around the IPv4 rules
    [InlineData("::ffff:192.168.0.10")]
    [InlineData("::ffff:127.0.0.1")]
    public void Strict_denies_internal_destinations(string address)
    {
        Assert.True(OutboundPolicy.IsInternal(Ip(address)), $"{address} should classify as internal");
        Assert.False(Strict().IsAllowed(Ip(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("9.9.9.9")]
    [InlineData("172.15.0.1")]           // just below the RFC1918 block
    [InlineData("172.32.0.1")]           // just above it
    [InlineData("100.63.255.255")]       // just below CGNAT
    [InlineData("100.128.0.0")]          // just above it
    [InlineData("2606:4700:4700::1111")]
    public void Strict_allows_public_destinations(string address)
    {
        Assert.False(OutboundPolicy.IsInternal(Ip(address)));
        Assert.True(Strict().IsAllowed(Ip(address)));
    }

    [Fact]
    public void An_exempt_address_is_allowed_though_it_is_internal()
    {
        // A grid on the same LAN as the viewer: without this it cannot log in at all.
        var policy = Strict("192.168.0.10");
        Assert.True(OutboundPolicy.IsInternal(Ip("192.168.0.10")));
        Assert.True(policy.IsAllowed(Ip("192.168.0.10")));
        Assert.False(policy.IsAllowed(Ip("192.168.0.11")));   // the exemption is that host, not the subnet
        Assert.False(policy.IsAllowed(Ip("127.0.0.1")));
    }

    [Fact]
    public void An_exempt_CIDR_covers_its_range_and_nothing_else()
    {
        var policy = Strict("192.168.0.0/24");
        Assert.True(policy.IsAllowed(Ip("192.168.0.1")));
        Assert.True(policy.IsAllowed(Ip("192.168.0.255")));
        Assert.False(policy.IsAllowed(Ip("192.168.2.1")));
        Assert.False(policy.IsAllowed(Ip("10.0.0.1")));
    }

    [Fact]
    public void Blank_exemption_slots_are_ignored()
    {
        // A settings file can carry unused, empty slots.
        var policy = Strict("192.168.0.10", "", "   ");
        Assert.True(policy.IsAllowed(Ip("192.168.0.10")));
        Assert.False(policy.IsAllowed(Ip("10.0.0.1")));
    }

    [Fact]
    public void Off_mode_changes_nothing()
    {
        var policy = Off();
        Assert.Equal(OutboundMode.Off, policy.Mode);
        foreach (var a in new[] { "127.0.0.1", "192.168.0.10", "169.254.169.254", "100.64.0.1", "8.8.8.8" })
            Assert.True(policy.IsAllowed(Ip(a)), $"off mode must allow {a}");
        Assert.True(policy.AllowsSimulator(new IPEndPoint(Ip("192.168.0.10"), 9000)));
    }

    [Fact]
    public void The_default_is_off()
    {
        Assert.Equal(OutboundMode.Off, new OutboundPolicy(new OutboundOptions()).Mode);
        Assert.Equal("off", new OutboundOptions().Policy);
        Assert.Empty(new OutboundOptions().AllowedInternalEndpoints);
    }

    [Fact]
    public void An_unknown_policy_value_fails_validation_rather_than_silently_allowing_everything()
    {
        var bad = new OutboundOptions { Policy = "strictish" };
        Assert.Contains("Policy", bad.Validate());
        foreach (var good in new[] { "off", "strict", "STRICT", "Off" })
            Assert.DoesNotContain("Policy", new OutboundOptions { Policy = good }.Validate());
    }

    // ---------------- simulator circuits ----------------

    [Fact]
    public void A_simulator_on_an_internal_address_is_refused()
    {
        // This is the hypergrid-teleport case: the address comes from the grid, not from us.
        var policy = Strict("192.168.0.10");
        Assert.True(policy.AllowsSimulator(new IPEndPoint(Ip("192.168.0.10"), 9000)));   // our own grid
        Assert.False(policy.AllowsSimulator(new IPEndPoint(Ip("192.168.0.1"), 9000)));    // the router
        Assert.False(policy.AllowsSimulator(new IPEndPoint(Ip("127.0.0.1"), 9000)));
        Assert.False(policy.AllowsSimulator(new IPEndPoint(Ip("169.254.169.254"), 80)));
        Assert.True(policy.AllowsSimulator(new IPEndPoint(Ip("8.8.8.8"), 9000)));         // a real remote grid
        Assert.True(policy.AllowsSimulator(null));                                        // nothing to judge
    }

    // ---------------- DNS ----------------

    [Fact]
    public async Task A_name_resolving_to_an_internal_address_is_denied()
    {
        // localhost is a real DNS answer that contains only internal addresses.
        var ex = await Assert.ThrowsAsync<OutboundDeniedException>(
            () => Strict().ResolveAllowedAsync("localhost", OutboundKind.Http, CancellationToken.None));
        Assert.Contains("localhost", ex.Message);
        Assert.Contains("AllowedInternalEndpoints", ex.Message);
    }

    [Fact]
    public async Task A_literal_internal_address_is_denied_without_any_lookup()
    {
        await Assert.ThrowsAsync<OutboundDeniedException>(
            () => Strict().ResolveAllowedAsync("192.168.0.10", OutboundKind.Login, CancellationToken.None));
    }

    [Fact]
    public async Task An_exempt_name_resolving_internally_is_allowed()
    {
        var addresses = await Strict("localhost").ResolveAllowedAsync("localhost", OutboundKind.Http, CancellationToken.None);
        Assert.NotEmpty(addresses);
    }

    [Fact]
    public async Task Off_mode_resolves_without_judging()
    {
        var addresses = await Off().ResolveAllowedAsync("localhost", OutboundKind.Http, CancellationToken.None);
        Assert.NotEmpty(addresses);
    }

    // ---------------- schemes ----------------

    [Theory]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("ftp://grid.example/x")]
    [InlineData("gopher://grid.example/1")]
    public void Non_http_schemes_are_refused(string uri)
    {
        Assert.Throws<OutboundDeniedException>(() => Strict().EnsureSchemeAllowed(new Uri(uri), OutboundKind.Http));
        Strict().EnsureSchemeAllowed(new Uri("https://grid.example/caps"), OutboundKind.Http);   // does not throw
        Off().EnsureSchemeAllowed(new Uri(uri), OutboundKind.Http);                              // off mode: unchanged
    }

    // ---------------- the HTTP handler, including redirects ----------------

    [Fact]
    public async Task The_http_client_refuses_an_internal_destination()
    {
        using var client = OutboundHttp.CreateClient(Strict(), Log, TimeSpan.FromSeconds(5));
        var ex = await Record.ExceptionAsync(() => client.GetAsync("http://192.168.0.10:9000/", CancellationToken.None));
        Assert.NotNull(ex);
        Assert.Contains("internal address", Flatten(ex!));
    }

    [Fact]
    public async Task A_redirect_to_an_internal_destination_is_refused()
    {
        // The redirect is what a capability URL can do to us: the first hop looks fine, the second is the LAN.
        // The check lives in ConnectCallback, so the redirected connection is checked exactly like the first one.
        using var server = new RedirectServer("http://10.255.255.1/secrets");
        // Loopback is exempt so the FIRST hop is reachable; the redirect target is not exempt.
        using var client = OutboundHttp.CreateClient(Strict("127.0.0.1", "::1"), Log, TimeSpan.FromSeconds(5));

        var ex = await Record.ExceptionAsync(() => client.GetAsync(server.Url, CancellationToken.None));

        Assert.True(server.WasHit, "the first hop should have been allowed and served");
        Assert.NotNull(ex);
        Assert.Contains("10.255.255.1", Flatten(ex!));
        Assert.Contains("internal address", Flatten(ex!));
    }

    [Fact]
    public async Task The_same_redirect_is_followed_when_the_policy_is_off()
    {
        // Proves the refusal above comes from the policy and not from the test's own plumbing: the same kind of
        // redirect (first hop on loopback, second hop on another address the strict policy classes as internal) is
        // followed to its end when the policy is off. The second hop is a second loopback server, so the outcome does
        // not depend on a connection attempt to an unreachable LAN address timing out inside a fixed window, which
        // the full suite's many parallel tests (a starved thread pool) could outrun.
        using var target = new RedirectServer(null);
        using var server = new RedirectServer(target.Url);
        using var client = OutboundHttp.CreateClient(Off(), Log, TimeSpan.FromSeconds(60));

        using var response = await client.GetAsync(server.Url, CancellationToken.None);

        Assert.True(server.WasHit, "the first hop should have been served");
        Assert.True(target.WasHit, "the redirect should have been followed to the second hop");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string Flatten(Exception ex)
    {
        var text = "";
        for (Exception? e = ex; e is not null; e = e.InnerException) text += e.Message + " | ";
        return text;
    }

    /// <summary>
    /// A one-request loopback server that answers 302 to a fixed location, or 200 when given none. It serves on its own
    /// thread, not the thread pool, so a busy pool cannot delay it.
    /// </summary>
    private sealed class RedirectServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public volatile bool WasHit;

        public RedirectServer(string? redirectTo)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/start";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            var thread = new Thread(() =>
            {
                try
                {
                    var ctx = _listener.GetContext();
                    WasHit = true;
                    if (redirectTo is null)
                    {
                        ctx.Response.StatusCode = 200;
                    }
                    else
                    {
                        ctx.Response.StatusCode = 302;
                        ctx.Response.RedirectLocation = redirectTo;
                    }
                    ctx.Response.Close();
                }
                catch
                {
                    // listener stopped
                }
            })
            { IsBackground = true };
            thread.Start();
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* already stopped */ }
            ((IDisposable)_listener).Dispose();
        }
    }
}
