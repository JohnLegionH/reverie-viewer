// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Viewer.Shell.Grids;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Add a grid's Look up on an https grid whose certificate is self-signed or from a private authority: the login's trust dialog is
/// raised with the certificate's fingerprint; trusting it pins that fingerprint to that grid's server and the look-up then finds the
/// grid; a certificate that differs from the one pinned for the server is refused with its own message and never offered again;
/// other certificate problems are never offered for trust; Second Life addresses are never asked. Stub servers on the loopback
/// address present certificates made in memory. Every member is independent, so the class runs in parallel.
/// </summary>
public class GridLookupCertificateTests
{
    private const string Info = "<gridinfo><gridname>Example Grid</gridname><login>https://grid.example.org:8002/</login></gridinfo>";

    private sealed class Prompts(Func<LoginDialog, LoginDialogResult> answer) : ILoginPrompts
    {
        public readonly List<LoginDialog> Shown = [];
        public Task<LoginDialogResult> AskAsync(LoginDialog dialog, CancellationToken ct) { Shown.Add(dialog); return Task.FromResult(answer(dialog)); }
    }

    private static (LoginModel Model, Prompts Prompts) Model(CertificateTrust trust, Func<LoginDialog, LoginDialogResult> answer, GridFetcher? fetcher = null)
    {
        var prompts = new Prompts(answer);
        var fetch = fetcher ?? GridAddress.HttpFetcher(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"), null, trust);
        var model = new LoginModel(StringTable.English(), _ => throw new InvalidOperationException("no login in these tests"), fetch, null)
        { Prompts = prompts, Trust = trust };
        return (model, prompts);
    }

    private static LoginDialogResult Trust(LoginDialog _) => new(true);
    private static LoginDialogResult Cancel(LoginDialog _) => LoginDialogResult.Cancelled;
    private static string Bare(string fingerprint) => fingerprint.Replace(":", "").Replace(" ", "").ToUpperInvariant();
    private static string Address(LoginStubServer s) => $"https://{s.Host}:{s.Port}/";

    [Fact]
    public async Task Look_up_raises_the_trust_dialog_with_the_fingerprint_and_trusting_pins_it_to_the_grid_and_finds_the_grid()
    {
        using var server = new LoginStubServer(StubCertificate.SelfSigned, (_, _) => "") { GridInfo = Info };
        var trust = new CertificateTrust(TestTemp.NewFolderPath("trust"));
        var (model, prompts) = Model(trust, Trust);

        var found = await model.LookUpGridAsync(Address(server));

        var d = Assert.Single(prompts.Shown);
        Assert.Equal(LoginDialogKind.TrustCertificate, d.Kind);
        Assert.Equal(server.Sha256, Bare(d.Fields.Single(f => f.Label == "SHA-256 Fingerprint").Value));
        Assert.True(trust.IsTrusted(server.Host, server.Sha256));
        Assert.Equal(1, trust.Count);
        Assert.NotNull(found.Entry);
        Assert.Equal("Example Grid", found.Entry!.Name);
        Assert.Null(found.ProblemKey);
    }

    [Fact]
    public async Task Cancelling_the_trust_dialog_pins_nothing_and_says_the_certificate_was_not_trusted()
    {
        using var server = new LoginStubServer(StubCertificate.SelfSigned, (_, _) => "") { GridInfo = Info };
        var trust = new CertificateTrust(TestTemp.NewFolderPath("trust"));
        var (model, prompts) = Model(trust, Cancel);

        var found = await model.LookUpGridAsync(Address(server));

        Assert.Single(prompts.Shown);
        Assert.Null(found.Entry);
        Assert.Equal("grid.add.certificate_not_trusted", found.ProblemKey);
        Assert.Equal(0, trust.Count);
        Assert.NotEqual("grid.add.certificate_not_trusted", StringTable.English().Get("grid.add.certificate_not_trusted"));   // the key has its words
    }

    [Fact]
    public async Task A_certificate_that_differs_from_the_one_pinned_for_the_server_is_refused_with_its_own_message_and_not_offered()
    {
        using var server = new LoginStubServer(StubCertificate.SelfSigned, (_, _) => "") { GridInfo = Info };
        var trust = new CertificateTrust(TestTemp.NewFolderPath("trust"));
        trust.Trust(new CertificateRefusal(server.Host, "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF", "RemoteCertificateChainErrors", "CN=old", null)
            { Kind = CertificateProblem.UnknownAuthority });
        var (model, prompts) = Model(trust, Trust);

        var found = await model.LookUpGridAsync(Address(server));

        Assert.Empty(prompts.Shown);
        Assert.Null(found.Entry);
        Assert.Equal("grid.add.certificate_changed", found.ProblemKey);
        Assert.Equal(server.Sha256, found.Certificate?.Sha256);
        Assert.False(trust.IsTrusted(server.Host, server.Sha256));
        Assert.True(trust.IsChanged(server.Host, server.Sha256));
        Assert.Contains("changed", StringTable.English().Get("grid.add.certificate_changed"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_expired_certificate_is_refused_and_never_offered_for_trust()
    {
        using var server = new LoginStubServer(StubCertificate.Expired, (_, _) => "") { GridInfo = Info };
        var trust = new CertificateTrust(TestTemp.NewFolderPath("trust"));
        var (model, prompts) = Model(trust, Trust);

        var found = await model.LookUpGridAsync(Address(server));

        Assert.DoesNotContain(prompts.Shown, d => d.Kind == LoginDialogKind.TrustCertificate);
        Assert.Null(found.Entry);
        Assert.Equal("grid.add.certificate_refused", found.ProblemKey);
        Assert.Equal(0, trust.Count);
    }

    [Fact]
    public async Task The_fetch_reports_the_refused_certificate()
    {
        using var server = new LoginStubServer(StubCertificate.SelfSigned, (_, _) => "") { GridInfo = Info };
        var fetch = GridAddress.HttpFetcher(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"), null, new CertificateTrust(null));

        var got = await fetch(new Uri(Address(server) + "get_grid_info"), CancellationToken.None);

        Assert.Null(got.Status);
        Assert.Equal(server.Sha256, got.Refused?.Sha256);
        Assert.Equal(CertificateProblem.UnknownAuthority, got.Refused?.Kind);
    }

    [Fact]
    public async Task A_Second_Life_address_is_never_asked_and_never_offered_a_certificate()
    {
        int asked = 0;
        GridFetcher refusing = (uri, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(new GridFetch(null, null, "refused",
                new CertificateRefusal(uri.Host, "AB12", "RemoteCertificateChainErrors", "CN=x", null) { Kind = CertificateProblem.UnknownAuthority }));
        };
        var trust = new CertificateTrust(null);
        var (model, prompts) = Model(trust, Trust, refusing);

        await model.LookUpGridAsync("https://login.agni.lindenlab.com/cgi-bin/login.cgi");

        Assert.Equal(0, asked);
        Assert.Empty(prompts.Shown);
        Assert.Equal(0, trust.Count);
    }

    [Fact]
    public void Forgetting_a_grid_forgets_its_pinned_certificate_in_a_later_run_too()
    {
        string store = TestTemp.NewFolderPath("trust");
        var trust = new CertificateTrust(store);
        trust.Trust(new CertificateRefusal("grid.example.org", "AB12", "RemoteCertificateChainErrors", "CN=x", null) { Kind = CertificateProblem.UnknownAuthority });
        trust.Trust(new CertificateRefusal("other.example.org", "CD34", "RemoteCertificateChainErrors", "CN=y", null) { Kind = CertificateProblem.UnknownAuthority });
        var (model, _) = Model(trust, Trust, (_, _) => Task.FromResult(new GridFetch(null, null, null)));

        model.ForgetGridCertificates("https://grid.example.org:8002/");

        Assert.False(trust.IsTrusted("grid.example.org", "AB12"));
        Assert.False(trust.IsChanged("grid.example.org", "EF56"));
        Assert.True(trust.IsTrusted("other.example.org", "CD34"));
        Assert.False(new CertificateTrust(store).IsTrusted("grid.example.org", "AB12"));
    }
}
