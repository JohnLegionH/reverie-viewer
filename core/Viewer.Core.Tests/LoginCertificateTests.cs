// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// HTTPS certificates at login. They are checked by default; a certificate from an unknown authority is trusted only when the
/// person confirms it, pinned to that certificate and host, and the choice lasts. Expired and wrong-host certificates are
/// never offered for trust. Stub servers on the loopback address present certificates the tests make in memory.
/// </summary>
public class LoginCertificateTests
{
    private static string NewStore() => TestTemp.NewFolderPath("trust");

    private sealed class Prompts(Func<LoginDialog, LoginDialogResult> answer) : ILoginPrompts
    {
        public readonly List<LoginDialog> Shown = [];
        public Task<LoginDialogResult> AskAsync(LoginDialog dialog, CancellationToken ct) { Shown.Add(dialog); return Task.FromResult(answer(dialog)); }
    }

    /// <summary>A model that logs in through the real protocol adapter, with this trust store.</summary>
    private static (LoginModel Model, Prompts Prompts) RealModel(LoginStubServer server, CertificateTrust trust, Func<LoginDialog, LoginDialogResult> answer)
    {
        var prompts = new Prompts(answer);
        var model = new LoginModel(StringTable.English(), opts =>
        {
            var log = new CoreLog(new ListSink(), "test");
            return new ViewerSession(opts, ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(opts.Outbound), log, trust), log);
        }, null, null)
        { LoginUri = server.Url, UserName = "Test User", Prompts = prompts, Trust = trust };
        model.SetPassword("pw");
        return (model, prompts);
    }

    /// <summary>The status of a GET, or null when the connection itself failed (a refused certificate).</summary>
    private static async Task<System.Net.HttpStatusCode?> StatusOf(HttpClient client, Uri uri)
    {
        try { return (await client.GetAsync(uri)).StatusCode; }
        catch (HttpRequestException) { return null; }
    }

    private static string Field(LoginDialog d, string label) => d.Fields.Single(f => f.Label == label).Value;
    private static string Bare(string fingerprint) => fingerprint.Replace(":", "").Replace(" ", "").ToUpperInvariant();

    private static LoginDialogResult Trust(LoginDialog _) => new(true);
    private static LoginDialogResult Cancel(LoginDialog _) => LoginDialogResult.Cancelled;

    [Fact]
    public async Task An_unknown_authority_shows_the_trust_dialog_with_each_fingerprint_under_its_own_label()
    {
        using var server = new LoginStubServer(StubCertificate.SelfSigned, (_, _) => LoginStubServer.Failure("key", "No."));
        var (model, prompts) = RealModel(server, new CertificateTrust(NewStore()), Cancel);

        Assert.False(await model.LoginAsync());

        var d = Assert.Single(prompts.Shown);
        Assert.Equal(LoginDialogKind.TrustCertificate, d.Kind);
        Assert.Equal("Trust", d.ConfirmLabel);
        Assert.Equal("Cancel", d.CancelLabel);
        Assert.Contains("The certification authority for this server is not known.", d.Body);
        Assert.Contains("Would you like to trust this authority?", d.Body);
        Assert.Equal("CN=test-grid.example.org", Field(d, "Subject Name"));
        Assert.Equal("CN=test-grid.example.org", Field(d, "Issuer Name"));
        Assert.Equal(server.Md5, Bare(Field(d, "MD5 Fingerprint")));      // the official text prints the SHA-1 value under this label
        Assert.Equal(server.Sha1, Bare(Field(d, "SHA1 Fingerprint")));    // and the MD5 value under this one
        Assert.Equal(server.Sha256, Bare(Field(d, "SHA-256 Fingerprint")));
        Assert.Contains(d.Fields, f => f.Label == "Valid From");
        Assert.Contains(d.Fields, f => f.Label == "Valid To");
        Assert.Contains(d.Fields, f => f.Label == "Key Usage");
        Assert.Contains(d.Fields, f => f.Label == "Extended Key Usage");
        Assert.Contains(d.Fields, f => f.Label == "Subject Key Identifier");
    }

    [Fact]
    public async Task Trust_adds_the_certificate_saves_it_and_logs_in_again_and_the_next_run_does_not_ask()
    {
        using var server = new LoginStubServer(StubCertificate.SelfSigned, (_, _) => LoginStubServer.Failure("key", "No."));
        string store = NewStore();
        var trust = new CertificateTrust(store);
        var (model, prompts) = RealModel(server, trust, Trust);

        Assert.False(await model.LoginAsync());

        Assert.Single(prompts.Shown);
        Assert.Equal(1, trust.Count);
        Assert.Equal("key", model.LastFailureCode);   // the grid's own answer, so the retry got through the handshake
        Assert.Single(server.Requests);

        // a later run reads the saved choice and goes straight through
        var (again, asked) = RealModel(server, new CertificateTrust(store), Cancel);
        Assert.False(await again.LoginAsync());
        Assert.Empty(asked.Shown);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("key", again.LastFailureCode);
    }

    [Fact]
    public async Task Cancel_returns_to_the_login_screen_and_trusts_nothing()
    {
        using var server = new LoginStubServer(StubCertificate.SelfSigned, (_, _) => LoginStubServer.Failure("key", "No."));
        var trust = new CertificateTrust(NewStore());
        var (model, prompts) = RealModel(server, trust, Cancel);

        Assert.False(await model.LoginAsync());

        Assert.Single(prompts.Shown);
        Assert.Equal(0, trust.Count);
        Assert.Empty(server.Requests);
        Assert.False(model.Busy);
        Assert.Contains("canceled", model.Status);
    }

    [Theory]
    [InlineData(StubCertificate.Expired, CertificateProblem.Expired, "appears to be expired")]
    [InlineData(StubCertificate.WrongHost, CertificateProblem.WrongHost, "An invalid hostname was used")]
    public async Task An_expired_or_wrong_host_certificate_gives_the_error_dialog_with_the_reason_and_no_trust_button(StubCertificate kind, CertificateProblem problem, string reason)
    {
        using var server = new LoginStubServer(kind, (_, _) => LoginStubServer.Failure("key", "No."));
        var trust = new CertificateTrust(NewStore());
        var (model, prompts) = RealModel(server, trust, Trust);   // a person who would say Trust is never asked

        Assert.False(await model.LoginAsync());

        var d = Assert.Single(prompts.Shown);
        Assert.Equal(LoginDialogKind.CertificateError, d.Kind);
        Assert.Equal("OK", d.ConfirmLabel);
        Assert.Null(d.CancelLabel);
        Assert.Contains("Could not connect to the server.", d.Body);
        Assert.Contains(reason, d.Body);
        Assert.Equal(server.Sha256, Bare(Field(d, "SHA-256 Fingerprint")));
        Assert.Equal(0, trust.Count);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_certificate_that_cannot_be_read_gives_the_short_error_dialog()
    {
        var prompts = new Prompts(Cancel);
        var model = new LoginModel(StringTable.English(), opts =>
        {
            var fake = new FakeTransport
            {
                OnLogin = (_, _) => Task.FromResult(TransportLoginResult.Failed("certificate", "no certificate") with
                {
                    Certificate = new CertificateRefusal("grid.example.org", "", "RemoteCertificateNotAvailable", "", null) { Kind = CertificateProblem.Unreadable },
                }),
            };
            return new ViewerSession(opts, fake, new CoreLog(new ListSink(), "test"));
        }, null, null) { LoginUri = "https://grid.example.org/", UserName = "Test User", Prompts = prompts };
        model.SetPassword("pw");

        Assert.False(await model.LoginAsync());

        var d = Assert.Single(prompts.Shown);
        Assert.Equal(LoginDialogKind.CertificateError, d.Kind);
        Assert.Empty(d.Fields);
        Assert.Contains("Could not connect to the server.", d.Body);
        Assert.Contains("Could not load certificate.", d.Body);
    }

    [Fact]
    public async Task The_transport_reports_which_kind_of_problem_each_certificate_has()
    {
        foreach (var (kind, expected) in new[] { (StubCertificate.SelfSigned, CertificateProblem.UnknownAuthority), (StubCertificate.Expired, CertificateProblem.Expired), (StubCertificate.WrongHost, CertificateProblem.WrongHost) })
        {
            using var server = new LoginStubServer(kind, (_, _) => LoginStubServer.Failure("key", "No."));
            using var transport = ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
            var result = await transport.LoginAsync(new TransportLoginRequest(server.Url, "Test", "Resident", new Secret("pw", SensitiveKind.PasswordDigest), "last", Identity.ViewerIdentity.Current), CancellationToken.None);

            Assert.Equal("certificate", result.FailureCode);
            Assert.Equal(expected, result.Certificate?.Kind);
            Assert.Equal(server.Sha1, result.Certificate?.Sha1);
            Assert.Equal(server.Md5, result.Certificate?.Md5);
        }
    }

    // ---- every HTTPS path honours the choice, and only for the pinned certificate and host ----

    private static CertificateRefusal Pinned(LoginStubServer s, string host = "127.0.0.1") =>
        new(host, s.Sha256, "RemoteCertificateChainErrors", "CN=test-grid.example.org", null) { Kind = CertificateProblem.UnknownAuthority };

    [Fact]
    public async Task The_plain_outbound_client_the_texture_client_and_the_library_client_accept_a_trusted_certificate_and_refuse_it_otherwise()
    {
        using var server = new LoginStubServer(StubCertificate.SelfSigned, (_, _) => LoginStubServer.Failure("key", "No."));
        var uri = new Uri($"https://{server.Host}:{server.Port}/cap");
        var trust = new CertificateTrust(NewStore());
        var log = new CoreLog(new ListSink(), "test");

        // refused before the choice, on each path
        var none = new CertificateRefusals();
        using (var client = OutboundHttp.CreateClient(new OutboundPolicy(new OutboundOptions()), log, TimeSpan.FromSeconds(30), refusals: none))
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(uri));
        Assert.NotNull(none.Latest(server.Host));

        trust.Trust(Pinned(server));
        var trusted = new CertificateRefusals { Trust = trust };

        using (var client = OutboundHttp.CreateClient(new OutboundPolicy(new OutboundOptions()), log, TimeSpan.FromSeconds(30), refusals: trusted))
            Assert.Equal(System.Net.HttpStatusCode.NotFound, await StatusOf(client, uri));   // the stub answers a GET with 404: the handshake passed
        using (var client = OutboundHttp.CreateClient(new OutboundPolicy(new OutboundOptions { Policy = "strict", AllowedInternalEndpoints = [server.Host] }), log, TimeSpan.FromSeconds(30), refusals: trusted))
            Assert.Equal(System.Net.HttpStatusCode.NotFound, await StatusOf(client, uri));
        using (var textures = new TextureHttp(new OutboundPolicy(new OutboundOptions()), log, trusted))
        {
            await textures.WholeAsync(uri, 1 << 20, CancellationToken.None);
            Assert.Null(trusted.Latest(server.Host));   // nothing was refused
        }
        using (var transport = (LmvTransport)ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(new OutboundOptions()), log, trust))
        {
            var ex = await Record.ExceptionAsync(() => transport.Client.HttpCapsClient.GetStringAsync(uri));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (ex as HttpRequestException)?.StatusCode);
        }
    }

    [Fact]
    public async Task The_choice_is_pinned_to_the_certificate_and_the_host()
    {
        using var server = new LoginStubServer(StubCertificate.SelfSigned, (_, _) => LoginStubServer.Failure("key", "No."));
        var trust = new CertificateTrust(NewStore());
        trust.Trust(Pinned(server, host: "other.example.org"));
        Assert.False(trust.IsTrusted(server.Host, server.Sha256));            // another host
        Assert.True(trust.IsTrusted("OTHER.example.org", server.Sha256));      // host names compare without case
        Assert.False(trust.IsTrusted("other.example.org", "00" + server.Sha256[2..]));   // another certificate

        var refusals = new CertificateRefusals { Trust = trust };
        using var client = OutboundHttp.CreateClient(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"), TimeSpan.FromSeconds(30), refusals: refusals);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://{server.Host}:{server.Port}/cap"));
    }

    [Theory]
    [InlineData(StubCertificate.Expired)]
    [InlineData(StubCertificate.WrongHost)]
    public async Task A_pinned_choice_does_not_make_an_expired_or_wrong_host_certificate_acceptable(StubCertificate kind)
    {
        using var server = new LoginStubServer(kind, (_, _) => LoginStubServer.Failure("key", "No."));
        var trust = new CertificateTrust(NewStore());
        trust.Trust(Pinned(server));
        var refusals = new CertificateRefusals { Trust = trust };
        using var client = OutboundHttp.CreateClient(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"), TimeSpan.FromSeconds(30), refusals: refusals);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://{server.Host}:{server.Port}/cap"));
    }

    [Fact]
    public async Task The_grid_info_fetch_of_the_login_screen_honours_the_choice()
    {
        using var server = new LoginStubServer(StubCertificate.SelfSigned, (_, _) => LoginStubServer.Failure("key", "No."));
        var trust = new CertificateTrust(NewStore());
        var log = new CoreLog(new ListSink(), "test");
        var uri = new Uri($"https://{server.Host}:{server.Port}/get_grid_info");

        var before = await GridAddress.HttpFetcher(new OutboundPolicy(new OutboundOptions()), log, null, trust)(uri, CancellationToken.None);
        Assert.Null(before.Status);

        trust.Trust(Pinned(server));
        var after = await GridAddress.HttpFetcher(new OutboundPolicy(new OutboundOptions()), log, null, trust)(uri, CancellationToken.None);
        Assert.Equal(404, after.Status);
    }

    [Fact]
    public async Task At_login_a_certificate_that_differs_from_the_one_pinned_is_refused_with_its_own_message_and_not_offered()
    {
        using var server = new LoginStubServer(StubCertificate.SelfSigned, (_, _) => LoginStubServer.Failure("key", "No."));
        var trust = new CertificateTrust(NewStore());
        trust.Trust(new CertificateRefusal(server.Host, "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF", "RemoteCertificateChainErrors", "CN=old", null)
            { Kind = CertificateProblem.UnknownAuthority });
        var (model, prompts) = RealModel(server, trust, Trust);

        Assert.False(await model.LoginAsync());

        var d = Assert.Single(prompts.Shown);
        Assert.Equal(LoginDialogKind.CertificateError, d.Kind);
        Assert.NotEqual("login.cert.changed", StringTable.English().Get("login.cert.changed"));
        Assert.Contains(StringTable.English().Get("login.cert.changed"), d.Body);
        Assert.False(trust.IsTrusted(server.Host, server.Sha256));
        Assert.Equal(server.Sha256, Bare(d.Fields.Single(f => f.Label == "SHA-256 Fingerprint").Value));
    }
}

/// <summary>The saved choice: where it lives, that a later run reads it, and that it holds nothing but the pinned certificates.</summary>
public class CertificateTrustTests
{
    private static string NewStore() => TestTemp.NewFolderPath("trust");

    private static CertificateRefusal Cert(string host = "grid.example.org", string sha = "AB12") =>
        new(host, sha, "RemoteCertificateChainErrors", "CN=x", null) { Kind = CertificateProblem.UnknownAuthority };

    [Fact]
    public void A_choice_is_saved_and_a_later_run_reads_it()
    {
        string store = NewStore();
        var first = new CertificateTrust(store);
        Assert.False(first.IsTrusted("grid.example.org", "AB12"));
        first.Trust(Cert());
        Assert.True(first.IsTrusted("grid.example.org", "AB12"));

        var later = new CertificateTrust(store);
        Assert.True(later.IsTrusted("grid.example.org", "AB12"));
        Assert.Equal(1, later.Count);
    }

    [Fact]
    public void Trusting_the_same_certificate_twice_keeps_one_entry_and_a_folderless_store_keeps_choices_in_memory_only()
    {
        var t = new CertificateTrust(NewStore());
        t.Trust(Cert());
        t.Trust(Cert());
        Assert.Equal(1, t.Count);

        var memory = new CertificateTrust(null);
        memory.Trust(Cert());
        Assert.True(memory.IsTrusted("grid.example.org", "AB12"));
    }

    [Fact]
    public void The_store_file_holds_the_pinned_certificates_and_nothing_else_of_the_login()
    {
        string store = NewStore();
        new CertificateTrust(store).Trust(Cert());
        Assert.True(Directory.Exists(store));
        var files = Directory.GetFiles(store);
        Assert.Single(files);
        string text = File.ReadAllText(files[0]);
        Assert.Contains("grid.example.org", text);
        Assert.Contains("AB12", text);
    }

    [Fact]
    public void A_store_file_that_cannot_be_read_trusts_nothing_and_does_not_throw()
    {
        string store = NewStore();
        new CertificateTrust(store).Trust(Cert());
        Assert.True(Directory.Exists(store));
        File.WriteAllText(Directory.GetFiles(store)[0], "{ not json");
        var t = new CertificateTrust(store);
        Assert.Equal(0, t.Count);
        Assert.False(t.IsTrusted("grid.example.org", "AB12"));
    }

    [Fact]
    public void The_store_folder_can_be_overridden_for_tests_and_defaults_to_the_settings_folder()
    {
        Assert.Equal("/over", CertificateTrust.FolderFor("/settings", name => name == "NV_CERT_STORE" ? "/over" : null));
        Assert.Equal("/settings", CertificateTrust.FolderFor("/settings", _ => null));
        Assert.Null(CertificateTrust.FolderFor(null, _ => null));
    }
}
