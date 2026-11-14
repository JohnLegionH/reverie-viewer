// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Viewer.Core.Net;

/// <summary>
/// A server certificate that was not accepted, with what a "trust this certificate for this grid" choice needs: the host it
/// was presented for, the SHA-256 fingerprint of the certificate (uppercase hex, no separators: the form the protocol
/// library's trusted-certificate list takes), why the platform refused it, and its subject and expiry to show the user.
/// </summary>
public sealed record CertificateRefusal(string Host, string Sha256, string Errors, string Subject, DateTimeOffset? NotAfter)
{
    /// <summary>Why the certificate was not accepted. Only <see cref="CertificateProblem.UnknownAuthority"/> can be turned into a trust choice.</summary>
    public CertificateProblem Kind { get; init; } = CertificateProblem.Other;
    public string Issuer { get; init; } = "";
    public DateTimeOffset? NotBefore { get; init; }
    /// <summary>MD5 and SHA-1 fingerprints (uppercase hex), shown under their own labels in the certificate dialog.</summary>
    public string Md5 { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public string KeyUsage { get; init; } = "";
    public string ExtendedKeyUsage { get; init; } = "";
    public string SubjectKeyIdentifier { get; init; } = "";

    /// <param name="chain">The platform's chain for the certificate when the caller has one; otherwise one is built here, without revocation checks.</param>
    public static CertificateRefusal From(string host, X509Certificate? certificate, SslPolicyErrors errors, X509Chain? chain = null)
    {
        if (certificate is null)
            return new(host, "", errors.ToString(), "", null) { Kind = CertificateProblem.Unreadable };
        using var cert = new X509Certificate2(certificate);
        return new(host, Convert.ToHexString(SHA256.HashData(cert.RawData)), errors.ToString(), cert.Subject, new DateTimeOffset(cert.NotAfter))
        {
            Kind = Classify(cert, errors, chain),
            Issuer = cert.Issuer,
            NotBefore = new DateTimeOffset(cert.NotBefore),
            Md5 = Convert.ToHexString(MD5.HashData(cert.RawData)),
            Sha1 = Convert.ToHexString(SHA1.HashData(cert.RawData)),
            KeyUsage = KeyUsageOf(cert),
            ExtendedKeyUsage = ExtendedKeyUsageOf(cert),
            SubjectKeyIdentifier = cert.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifier ?? "",
        };
    }

    /// <summary>
    /// A name that does not match the host is a wrong-host certificate; a certificate outside its dates is an expired one; a chain
    /// that ends at a root the platform does not trust (a self-signed certificate, a private authority) with nothing else wrong is an
    /// unknown authority, the one problem a person can choose to accept. Anything else (a bad signature, a revocation, a constraint)
    /// is some other problem.
    /// </summary>
    private static CertificateProblem Classify(X509Certificate2 cert, SslPolicyErrors errors, X509Chain? chain)
    {
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) return CertificateProblem.WrongHost;
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return CertificateProblem.Unreadable;
        X509ChainStatusFlags flags;
        if (chain is not null)
            flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
        else
        {
            using var own = new X509Chain { ChainPolicy = { RevocationMode = X509RevocationMode.NoCheck } };
            own.Build(cert);
            flags = own.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
        }
        if (flags.HasFlag(X509ChainStatusFlags.NotTimeValid)) return CertificateProblem.Expired;
        const X509ChainStatusFlags unknownRoot = X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain;
        return flags != X509ChainStatusFlags.NoError && (flags & ~unknownRoot) == 0 ? CertificateProblem.UnknownAuthority : CertificateProblem.Other;
    }

    private static string KeyUsageOf(X509Certificate2 cert) =>
        cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is { } ku ? ku.KeyUsages.ToString() : "";

    private static string ExtendedKeyUsageOf(X509Certificate2 cert) =>
        cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault() is { } eku
            ? string.Join(", ", eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.FriendlyName is { Length: > 0 } n ? n : o.Value ?? ""))
            : "";

    /// <summary>The sentence shown to the user and logged: names the host and the fingerprint.</summary>
    public string Message =>
        Sha256.Length == 0
            ? $"The server {Host} presented no certificate ({Errors})."
            : $"The certificate of {Host} was not accepted ({Errors}). Certificate SHA-256: {Sha256}";
}

public enum CertificateProblem { UnknownAuthority, Expired, WrongHost, Unreadable, Other }

/// <summary>The latest refusal for each host, filled by the certificate checks of every HTTPS connection of a session.</summary>
public sealed class CertificateRefusals
{
    /// <summary>The certificates the person chose to trust, consulted by every certificate check that records into this object; null: none are trusted.</summary>
    public CertificateTrust? Trust { get; set; }

    private readonly ConcurrentDictionary<string, CertificateRefusal> _latest = new(StringComparer.OrdinalIgnoreCase);

    public void Record(CertificateRefusal refusal) => _latest[refusal.Host] = refusal;

    public CertificateRefusal? Latest(string host) => _latest.TryGetValue(host, out var r) ? r : null;

    public void Clear(string host) => _latest.TryRemove(host, out _);

    /// <summary>Whether the person chose to trust this refused certificate: an unknown authority, pinned to this host and this fingerprint.
    /// Expired, wrong-host and other problems are never accepted.</summary>
    public bool Accepts(CertificateRefusal refusal) =>
        Trust is { } trust && refusal.Kind == CertificateProblem.UnknownAuthority && refusal.Sha256.Length > 0 && trust.IsTrusted(refusal.Host, refusal.Sha256);
}
