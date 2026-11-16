// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Net;
using Viewer.Core.Session;

namespace Viewer.Shell;

/// <summary>
/// Example content for each login-flow dialog, with neutral values (example.org names, a made-up certificate). A development build's
/// automation shows them so each dialog can be looked at and pictured without a grid that asks for it.
/// </summary>
public static class LoginDialogSamples
{
    public static readonly IReadOnlyList<string> Kinds = ["terms", "terms_page", "critical", "update", "mfa", "trust", "certificate_error", "certificate_unreadable", "unknown", "disclosure_first_run", "disclosure_second_life"];

    private static CertificateRefusal SampleCertificate(CertificateProblem kind) =>
        new("grid.example.org", "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08", "RemoteCertificateChainErrors", "CN=grid.example.org, O=Example Grid", new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero))
        {
            Kind = kind,
            Issuer = "CN=Example Grid Private CA",
            NotBefore = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            Md5 = "D41D8CD98F00B204E9800998ECF8427E",
            Sha1 = "DA39A3EE5E6B4B0D3255BFEF95601890AFD80709",
            KeyUsage = "DigitalSignature, KeyEncipherment",
            ExtendedKeyUsage = "Server Authentication",
            SubjectKeyIdentifier = "5C3E8A1F0B7D4E92A6C1D83F",
        };

    /// <summary>The dialog for a sample kind, or null for a name that is not one.</summary>
    public static LoginDialog? For(StringTable s, string kind) => kind switch
    {
        "terms" => LoginDialogs.Terms(s, "1. Be kind to other residents.\n2. Do not misuse the service.\n3. The operators may change these terms with notice."),
        "terms_page" => LoginDialogs.Terms(s, "https://grid.example.org/terms"),
        "critical" => LoginDialogs.Critical(s, new LoginReply("critical", "The grid will be down for maintenance on Friday from 02:00 to 04:00 UTC.", "", new Dictionary<string, string>(), "MAINT-1", null, "")),
        "update" => LoginDialogs.Update(s, new LoginReply("update", "Please get the current release.", "", new Dictionary<string, string> { ["VERSION"] = "9.9.9" }, "", null, "")),
        "mfa" => LoginDialogs.Token(s, LoginDialogs.TokenBody(s, null)),
        "trust" => LoginDialogs.TrustCertificate(s, SampleCertificate(CertificateProblem.UnknownAuthority)),
        "certificate_error" => LoginDialogs.CertificateError(s, SampleCertificate(CertificateProblem.Expired)),
        "certificate_unreadable" => LoginDialogs.CertificateError(s, new CertificateRefusal("grid.example.org", "", "RemoteCertificateNotAvailable", "", null) { Kind = CertificateProblem.Unreadable }),
        "unknown" => LoginDialogs.Unknown(s),
        "disclosure_first_run" => Disclosures.FirstRun(s),
        "disclosure_second_life" => Disclosures.BeforeSecondLife(s),
        _ => null,
    };
}
