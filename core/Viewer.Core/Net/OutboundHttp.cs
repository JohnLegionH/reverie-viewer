// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net.Sockets;
using Viewer.Core.Diagnostics;

namespace Viewer.Core.Net;

/// <summary>
/// Copied from the web gateway (web-viewer bb91ba5, OutboundHttp.cs). WV-3d-1: the HTTP side of <see cref="OutboundPolicy"/>.
///
/// The check lives in <see cref="SocketsHttpHandler.ConnectCallback"/>, which is the one place that sees every
/// connection the handler makes. That matters for two reasons:
///
///  * <b>Redirects are covered for free.</b> HttpClient following a 302 to an internal address opens a new connection,
///    which comes back through this callback. There is no separate redirect policy to keep in step.
///  * <b>No TOCTOU.</b> The callback resolves the name, checks every answer, and then connects to those addresses.
///    Nothing re-resolves between the check and the connect, so a name that alternates between a public and a private
///    answer cannot slip a connection past the check.
///
/// The scheme check is a <see cref="DelegatingHandler"/> in front, because by the time ConnectCallback runs the scheme
/// is already gone.
/// </summary>
public static class OutboundHttp
{
    /// <summary>An HttpClient whose every connection goes through <paramref name="policy"/>.</summary>
    /// <param name="maxConnectionsPerServer">: a connection limit per server (the texture client's 8); none by default.</param>
    public static HttpClient CreateClient(OutboundPolicy policy, CoreLog log, TimeSpan timeout, int? maxConnectionsPerServer = null, CertificateRefusals? refusals = null)
    {
        var handler = CreateHandler(policy, log, refusals);
        if (maxConnectionsPerServer is int n) handler.MaxConnectionsPerServer = n;
        return new(new SchemeGuard(policy, handler)) { Timeout = timeout };
    }

    /// <summary>The handler alone, for injecting into something that builds its own client (LibreMetaverse's caps client).</summary>
    /// <remarks>Server certificates are checked by the platform (chain, name, expiry), in every mode: a connection whose certificate fails the check is refused, and the refusal (host, fingerprint, reason) goes to <paramref name="refusals"/> when given.</remarks>
    public static SocketsHttpHandler CreateHandler(OutboundPolicy policy, CoreLog log, CertificateRefusals? refusals = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        // The platform's own decision stands (accepted only when there are no policy errors); the callback adds the record
        // of what was refused, so a refusal can name the certificate.
        handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
        {
            if (errors == System.Net.Security.SslPolicyErrors.None) return true;
            string host = sender switch
            {
                System.Net.Security.SslStream s => s.TargetHostName,
                HttpRequestMessage m => m.RequestUri?.Host ?? "",
                _ => "",
            };
            var refusal = CertificateRefusal.From(host, certificate, errors, chain);
            // a certificate the person chose to trust (an unknown authority, pinned to this host and fingerprint) is accepted; nothing else is
            if (refusals is not null && refusals.Accepts(refusal))
            {
                log.For("net.tls").Info("tls.certificate-trusted", CoreLog.F("host", host), CoreLog.F("sha256", refusal.Sha256));
                return true;
            }
            log.For("net.tls").Warn("tls.certificate-refused", CoreLog.F("host", host), CoreLog.F("sha256", refusal.Sha256), CoreLog.F("errors", refusal.Errors));
            refusals?.Record(refusal);
            return false;
        };
        if (!policy.Strict) return handler;

        handler.ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = await policy.ResolveAllowedAsync(host, OutboundKind.Http, ct).ConfigureAwait(false);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Connect to the addresses we just checked -- deliberately not to the name.
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
        return handler;
    }

    /// <summary>Refuses a non-http(s) URL before the request is sent, and turns a denial into one clear log line.</summary>
    private sealed class SchemeGuard(OutboundPolicy policy, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is { } uri) policy.EnsureSchemeAllowed(uri, OutboundKind.Http);
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }
    }
}
