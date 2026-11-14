// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Viewer.Core.Diagnostics;
using Viewer.Core.Identity;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// A login to an HTTPS server whose certificate is self-signed is refused, and the refusal names the certificate's
/// SHA-256 fingerprint and the host: what a per-grid "trust this certificate" setting needs to show the user and to pin.
/// The server is local, on the loopback address; its certificate is generated in memory by the test and never
/// installed in any store.
/// </summary>
public class CertificateRefusalTests(ITestOutputHelper output)
{
    /// <summary>A loopback TLS server with a self-signed certificate. Answers any request that gets through the handshake
    /// with an empty LLSD map, and records whether a handshake completed.</summary>
    internal sealed class SelfSignedServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _cert;
        private int _handshakes;
        private int _requests;

        public string Host => "127.0.0.1";
        public int Port { get; }
        public string Url => $"https://{Host}:{Port}/login";
        public string Fingerprint { get; }
        public int CompletedHandshakes => Volatile.Read(ref _handshakes);
        public int Requests => Volatile.Read(ref _requests);
        public string? LastServerError;

        public SelfSignedServer()
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=test-grid.example.org", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            san.AddDnsName("test-grid.example.org");
            req.CertificateExtensions.Add(san.Build());
            using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            // SChannel cannot serve with an in-memory (ephemeral) key, so the key is imported from PKCS#12 in the default key set: .NET keeps it
            // as a temporary key that goes with the certificate object when it is disposed. No certificate is added to any store.
            _cert = X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
            Fingerprint = Convert.ToHexString(SHA256.HashData(_cert.RawData));
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(Accept);
        }

        private async Task Accept()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using var _ = client;
            try
            {
                using var ssl = new SslStream(client.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(_cert);
                Interlocked.Increment(ref _handshakes);
                var buf = new byte[8192];
                var seen = new StringBuilder();
                while (!seen.ToString().Contains("\r\n\r\n"))
                {
                    int n = await ssl.ReadAsync(buf);
                    if (n == 0) return;
                    seen.Append(Encoding.ASCII.GetString(buf, 0, n));
                }
                Interlocked.Increment(ref _requests);
                const string body = "<?xml version=\"1.0\"?><llsd><map></map></llsd>";
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/llsd+xml\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(head + body));
                await ssl.FlushAsync();
            }
            catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
            {
                LastServerError = e.GetType().Name + ": " + e.Message + (e.InnerException is null ? "" : " / " + e.InnerException.Message);
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _cert.Dispose();
        }
    }

    private static TransportLoginRequest Request(string url) =>
        new(url, "Test", "Resident", new Secret("pw", SensitiveKind.PasswordDigest), "last", ViewerIdentity.Current);

    [Fact]
    public async Task A_login_to_a_server_with_a_self_signed_certificate_is_refused_with_its_fingerprint_and_host()
    {
        using var server = new SelfSignedServer();
        using var transport = ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await transport.LoginAsync(Request(server.Url), cts.Token);

        output.WriteLine($"{result.FailureCode}: {result.FailureMessage}");
        Assert.False(result.Ok);
        Assert.Equal("certificate", result.FailureCode);
        Assert.Contains(server.Fingerprint, result.FailureMessage);
        Assert.Contains(server.Host, result.FailureMessage);
        Assert.Equal(0, server.Requests);   // the server finishes its half of a TLS 1.3 handshake before the client's refusal reaches it; no request is ever read

        // what a per-grid trusted-certificate choice needs, as data and not only as text
        var c = result.Certificate;
        Assert.NotNull(c);
        Assert.Equal(server.Host, c.Host);
        Assert.Equal(server.Fingerprint, c.Sha256);
        Assert.Contains("RemoteCertificateChainErrors", c.Errors);
        Assert.Equal("CN=test-grid.example.org", c.Subject);
        Assert.NotNull(c.NotAfter);
    }

    /// <summary>The control for the test above: the same server, the same login, with the refused certificate trusted through the
    /// viewer's own trust store. The handshake completes and the request arrives, so the refusal was the
    /// certificate and nothing else, and the fingerprint the refusal reports is the one that pins it.</summary>
    [Fact]
    public async Task The_same_login_reaches_the_server_once_the_refused_fingerprint_is_trusted()
    {
        using var server = new SelfSignedServer();
        var trust = new CertificateTrust(null);
        using var transport = (LmvTransport)ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"), trust);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var refused = await transport.LoginAsync(Request(server.Url), cts.Token);
        Assert.Equal("certificate", refused.FailureCode);

        trust.Trust(refused.Certificate!);
        var trusted = await transport.LoginAsync(Request(server.Url), cts.Token);

        output.WriteLine($"{trusted.FailureCode}: {trusted.FailureMessage}");
        Assert.NotEqual("certificate", trusted.FailureCode);

        Assert.Equal(1, server.Requests);
    }

    /// <summary>The viewer's own HTTPS clients (texture and asset fetches, the capability probes) refuse the certificate too,
    /// and record the same facts.</summary>
    [Fact]
    public async Task The_texture_fetch_client_refuses_the_certificate_and_records_it()
    {
        using var server = new SelfSignedServer();
        var refusals = new CertificateRefusals();
        using var textures = new TextureHttp(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"), refusals);

        var whole = await textures.WholeAsync(new Uri($"https://{server.Host}:{server.Port}/tex"), 1 << 20, CancellationToken.None);
        var range = await textures.RangeAsync(new Uri($"https://{server.Host}:{server.Port}/tex"), 0, 1023, 1 << 20, CancellationToken.None);

        Assert.Equal(Viewer.Core.Assets.AssetFetchStatus.Failed, whole.Status);
        Assert.Equal(RangeAnswerKind.Failed, range.Kind);
        Assert.Equal(0, server.Requests);   // the server finishes its half of a TLS 1.3 handshake before the client's refusal reaches it; no request is ever read
        Assert.Equal(server.Fingerprint, refusals.Latest(server.Host)?.Sha256);
    }

    [Fact]
    public async Task The_plain_outbound_client_refuses_the_certificate_in_every_outbound_mode_that_allows_the_address()
    {
        using var server = new SelfSignedServer();
        var refusals = new CertificateRefusals();
        using var client = OutboundHttp.CreateClient(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"), TimeSpan.FromSeconds(30), refusals: refusals);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://{server.Host}:{server.Port}/cap"));

        Assert.IsType<System.Security.Authentication.AuthenticationException>(ex.InnerException);
        Assert.Equal(0, server.Requests);   // the server finishes its half of a TLS 1.3 handshake before the client's refusal reaches it; no request is ever read
        Assert.Equal(server.Fingerprint, refusals.Latest(server.Host)?.Sha256);
    }

    /// <summary>The library's own client, which carries the capability, event-queue and login requests when the outbound policy
    /// is off, refuses it as well.</summary>
    [Fact]
    public async Task The_library_capability_client_refuses_the_certificate_too()
    {
        using var server = new SelfSignedServer();
        using var transport = (LmvTransport)ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));

        await Assert.ThrowsAnyAsync<Exception>(() => transport.Client.HttpCapsClient.GetStringAsync(new Uri($"https://{server.Host}:{server.Port}/cap")));

        Assert.Equal(0, server.Requests);   // the server finishes its half of a TLS 1.3 handshake before the client's refusal reaches it; no request is ever read
    }
}
