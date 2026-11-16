// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Viewer.Automation;
using Viewer.Core.Diagnostics;
using Viewer.Core.Identity;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What the Third-Party Viewer Policy asks of this viewer on Second Life: the automation API is declared OpenSim only and turns itself
/// off before a login to Second Life sends anything; the multifactor hash is kept in the protected store only when Remember is ticked;
/// the viewer statistics report is sent on Second Life only, as the official viewer sends it.
/// </summary>
public class SecondLifePolicyTests
{
    private const string SecondLifeLogin = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";

    // ---- automation ----

    [Fact]
    public void Automation_is_declared_OpenSim_only()
    {
        Assert.Equal(FeatureReach.OpenSimOnly, GridPolicy.ReachOf(ViewerFeature.Automation));
        Assert.False(GridPolicy.Allows(ViewerFeature.Automation, GridKind.SecondLife));
        Assert.True(GridPolicy.Allows(ViewerFeature.Automation, GridKind.OpenSim));
    }

    [Fact]
    public void A_login_heading_for_Second_Life_turns_the_automation_API_off_for_good_and_it_refuses_every_call()
    {
        int handled = 0;
        using var server = new AutomationServer(_ => { Interlocked.Increment(ref handled); return Task.FromResult(AutomationReply.Ok(new { })); });
        string dir = TestTemp.NewFolderPath("auto-sl");
        string tokenFile = server.WriteTokenFile(dir);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(tokenFile));
            string token = doc.RootElement.GetProperty("token").GetString()!;
            Assert.Equal(200, Send(server.Port, token, "observe"));
            Assert.False(server.TurnOffFor(GridKind.OpenSim));   // an OpenSim grid leaves it on
            Assert.Equal(200, Send(server.Port, token, "observe"));

            Assert.True(server.TurnOffFor(GridKind.SecondLife));
            Assert.True(server.IsOff);
            Assert.True(server.TurnOffFor(GridKind.OpenSim));   // nothing turns it on again
            Assert.True(server.IsOff);
            int before = handled;
            foreach (var command in new[] { "observe", "login_screen", "session", "quit" })
                Assert.Equal(403, Send(server.Port, token, command));
            Assert.Equal(before, handled);   // refused before the handler
        }
        finally
        {
            TokenFile.Remove(tokenFile);
            TestTemp.Delete(dir);
        }
    }

    [Fact]
    public async Task The_login_model_names_Second_Life_before_anything_is_sent_to_it()
    {
        var f = LoginFlow.SecondLifeAt(SecondLifeLogin, LoginFlow.Fail("key", "Sorry! We couldn't log you in."));
        var seen = new List<(GridKind Grid, int RequestsSoFar)>();
        f.Model.GridChosen += g => seen.Add((g, f.Requests.Count));
        Assert.False(await f.Model.LoginAsync());
        Assert.Equal([(GridKind.SecondLife, 0)], seen);
    }

    [Fact]
    public async Task The_login_model_names_an_OpenSim_grid_too()
    {
        var f = new LoginFlow(null, null, LoginFlow.Ok());
        var seen = new List<GridKind>();
        f.Model.GridChosen += seen.Add;
        Assert.True(await f.Model.LoginAsync());
        Assert.Equal([GridKind.OpenSim], seen);
    }

    [Theory]
    [InlineData("https://login.agni.lindenlab.com/cgi-bin/login.cgi", true)]
    [InlineData("https://login.aditi.lindenlab.com/cgi-bin/login.cgi", true)]
    [InlineData("https://grid.example.org:8002/", false)]
    [InlineData(null, false)]
    public void The_automation_never_starts_a_login_to_Second_Life(string? loginUri, bool refused) =>
        Assert.Equal(refused, AutomationLogin.Refuses(loginUri));

    private static int Send(int port, string token, string command)
    {
        using var tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, port);
        var s = tcp.GetStream();
        s.Write(Encoding.ASCII.GetBytes($"POST /v1/{command} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nAuthorization: Bearer {token}\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{{}}"));
        using var r = new StreamReader(s);
        return int.Parse(r.ReadToEnd().Split(' ')[1]);
    }

    // ---- the multifactor hash ----

    private sealed class MemoryVault : ILoginVault
    {
        public readonly Dictionary<string, string> Kept = new();
        public readonly Dictionary<string, string> Mfa = new();
        public int MfaSaves;
        public bool Available => true;
        public string? UnavailableKey => null;
        public void Save(string grid, string name, Secret hash) => Kept[RememberPassword.EntryKey(grid, name)] = hash.Reveal();
        public Secret? Load(string grid, string name) =>
            Kept.TryGetValue(RememberPassword.EntryKey(grid, name), out var h) ? new Secret(h, SensitiveKind.PasswordDigest) : null;
        public bool Forget(string grid, string name)
        {
            Mfa.Remove(RememberPassword.EntryKey(grid, name));
            return Kept.Remove(RememberPassword.EntryKey(grid, name));
        }
        public void SaveMfaHash(string grid, string name, Secret hash)
        {
            MfaSaves++;
            Mfa[RememberPassword.EntryKey(grid, name)] = hash.Reveal();
        }
        public Secret? LoadMfaHash(string grid, string name) =>
            Mfa.TryGetValue(RememberPassword.EntryKey(grid, name), out var h) ? new Secret(h, SensitiveKind.Token) : null;
        public void ForgetMfaHash(string grid, string name) => Mfa.Remove(RememberPassword.EntryKey(grid, name));
    }

    private static LoginFlow WithVault(MemoryVault v, params Func<int, TransportLoginResult>[] script)
    {
        var f = LoginFlow.SecondLifeAt(SecondLifeLogin, script);
        f.Model.Vault = v;
        return f;
    }

    private static Func<int, TransportLoginResult> Challenge() =>
        LoginFlow.Fail("mfa_challenge", "Enter your code.", messageId: "LoginFailedAuthenticationMFARequired");

    [Fact]
    public async Task With_Remember_ticked_the_hash_the_grid_hands_out_is_kept_in_the_protected_store_and_sent_at_the_next_start()
    {
        var v = new MemoryVault();
        var first = WithVault(v, Challenge(), LoginFlow.Ok("grid-hash-sl")).Answer(new LoginDialogResult(true, "123456"));
        first.Model.SetRememberPassword(true);
        Assert.True(await first.Model.LoginAsync());
        Assert.Equal("grid-hash-sl", Assert.Single(v.Mfa).Value);

        // the next start of the viewer: a new model, the same store
        var next = WithVault(v, LoginFlow.Ok());
        Assert.True(next.Model.UseSavedPassword());
        Assert.True(await next.Model.LoginAsync());
        Assert.Equal("grid-hash-sl", Assert.Single(next.Requests).Extras!.MfaHash!.Reveal());
        Assert.Empty(next.Shown);   // no token asked
    }

    [Fact]
    public async Task With_Remember_unticked_the_hash_is_never_stored()
    {
        var v = new MemoryVault();
        var f = WithVault(v, Challenge(), LoginFlow.Ok("grid-hash-sl")).Answer(new LoginDialogResult(true, "123456"));
        f.Model.SetRememberPassword(false);
        Assert.True(await f.Model.LoginAsync());
        Assert.Empty(v.Mfa);
        Assert.Equal(0, v.MfaSaves);   // not even for a moment
    }

    [Fact]
    public async Task Forgetting_the_login_forgets_the_hash()
    {
        var v = new MemoryVault();
        var f = WithVault(v, Challenge(), LoginFlow.Ok("grid-hash-sl")).Answer(new LoginDialogResult(true, "123456"));
        f.Model.SetRememberPassword(true);
        Assert.True(await f.Model.LoginAsync());
        Assert.Single(v.Mfa);
        f.Model.SetRememberPassword(false);
        Assert.Empty(v.Mfa);
    }

    [Fact]
    public async Task A_hash_Second_Life_rejects_is_forgotten()
    {
        var v = new MemoryVault();
        v.Save(SecondLifeLogin, "Test User", new Secret(LoginHash.Of("pw"), SensitiveKind.PasswordDigest));
        v.SaveMfaHash(SecondLifeLogin, "Test User", new Secret("stale-hash", SensitiveKind.Token));
        // the grid answers the stale hash with a new challenge; the person cancels it
        var f = WithVault(v, Challenge());
        f.Model.SetRememberPassword(true);
        Assert.False(await f.Model.LoginAsync());
        Assert.Equal("stale-hash", Assert.Single(f.Requests).Extras?.MfaHash?.Reveal());
        Assert.Empty(v.Mfa);
    }

    [Fact]
    public void The_Windows_store_keeps_the_hash_protected_and_forgets_it_with_the_login()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = TestTemp.NewFolderPath("vault-mfa");
        try
        {
            var dpapi = new DpapiLoginVault(dir);
            ILoginVault vault = dpapi;
            vault.Save(SecondLifeLogin, "Test User", new Secret(LoginHash.Of("pw"), SensitiveKind.PasswordDigest));
            vault.SaveMfaHash(SecondLifeLogin, "Test User", new Secret("mfa-secret-value", SensitiveKind.Token));
            Assert.Equal("mfa-secret-value", vault.LoadMfaHash(SecondLifeLogin, "Test User")?.Reveal());
            Assert.DoesNotContain("mfa-secret-value", File.ReadAllText(dpapi.FilePath));   // never in a plain file
            Assert.Null(vault.LoadMfaHash(SecondLifeLogin, "Other User"));
            Assert.True(vault.Forget(SecondLifeLogin, "Test User"));
            Assert.Null(vault.LoadMfaHash(SecondLifeLogin, "Test User"));
        }
        finally { TestTemp.Delete(dir); }
    }

    // ---- viewer statistics ----

    [Fact]
    public void Viewer_statistics_are_declared_Second_Life_only()
    {
        Assert.Equal(FeatureReach.SecondLifeOnly, GridPolicy.ReachOf(ViewerFeature.ViewerStatistics));
        Assert.True(GridPolicy.Allows(ViewerFeature.ViewerStatistics, GridKind.SecondLife));
        Assert.False(GridPolicy.Allows(ViewerFeature.ViewerStatistics, GridKind.OpenSim));
    }

    [Fact]
    public void The_report_has_the_official_shape_and_carries_only_the_hashed_device_ids()
    {
        var identity = new ViewerIdentity("Reverie Viewer", "Reverie Viewer", "0.1.0.200");
        var report = ViewerStatsReport.Build(new ViewerStatsReport.Inputs(identity, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), 60, 55,
            new ViewerStatsFigures(59.5, "Example GPU", "Example", "1.0", "en", 3, true, false), 42, 10, 2, 512_000, 16_000_000,
            ("0123456789abcdef0123456789abcdef", "fedcba9876543210fedcba9876543210"), new ViewerStatsTraffic(100, 10, 50, 5, 1, 0), true));

        foreach (var key in new[] { "agent", "system", "downloads", "stats", "inventory", "DisplayNamesEnabled", "DisplayNamesShowUsername" })
            Assert.True(report.ContainsKey(key), key);
        var agent = (IDictionary<string, object?>)report["agent"]!;
        Assert.Equal("Reverie Viewer 0.1.0.200", agent["version"]);
        Assert.Equal(0.0, agent["run_time"]);   // under half a period: 0, as the official first report
        Assert.Equal(1_800_000_000 - 60, agent["start_time"]);
        Assert.Equal(59.5, agent["fps"]);
        Assert.Equal("en", agent["language"]);
        var system = (IDictionary<string, object?>)report["system"]!;
        Assert.Equal("0123456789abcdef0123456789abcdef", system["mac_address"]);
        Assert.Equal("fedcba9876543210fedcba9876543210", system["serial_number"]);
        Assert.Equal(Environment.ProcessorCount, system["hardware_concurrency"]);
        var net = (IDictionary<string, object?>)((IDictionary<string, object?>)report["stats"]!)["net"]!;
        Assert.Equal(10, ((IDictionary<string, object?>)net["in"]!)["packets"]);
        Assert.Equal(false, report["DisplayNamesShowUsername"]);
    }

    [Fact]
    public async Task On_Second_Life_a_report_is_posted_on_arrival_and_one_more_at_logout()
    {
        var t = new StatsTransport();
        using var s = new ViewerSession(new SessionOptions { LoginUri = SecondLifeLogin }, t, new CoreLog(new ListSink(), "test"));
        Assert.True((await s.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None)).Ok);
        Assert.NotNull(s.Statistics);
        await WaitFor(() => t.PostedCount >= 1);
        var first = Assert.Single(t.Posted);
        Assert.Equal(t.DeviceIds.Id0, ((IDictionary<string, object?>)first["system"]!)["serial_number"]);
        await s.LogoutAsync(CancellationToken.None);
        Assert.Equal(2, t.PostedCount);
        Assert.Null(s.Statistics);
    }

    [Fact]
    public async Task On_an_OpenSim_grid_no_report_is_ever_sent()
    {
        var t = new StatsTransport();
        using var s = new ViewerSession(new SessionOptions { LoginUri = "https://grid.example.org:8002/" }, t, new CoreLog(new ListSink(), "test"));
        Assert.True((await s.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None)).Ok);
        Assert.Null(s.Statistics);   // no sender is made, so nothing can be sent
        await s.LogoutAsync(CancellationToken.None);
        Assert.Empty(t.Posted);
    }

    [Fact]
    public async Task The_first_report_is_tried_again_until_the_region_has_its_capability()
    {
        var t = new StatsTransport { MissingFor = 3 };
        using var sender = new ViewerStatsSender(t, SampleInputs, TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(20));
        await WaitFor(() => sender.Sent == 1);
        Assert.Equal(4, t.Attempts);
    }

    [Fact]
    public async Task The_logout_report_waits_for_a_periodic_report_still_on_its_way()
    {
        var t = new StatsTransport { Hold = new TaskCompletionSource() };
        var sender = new ViewerStatsSender(t, SampleInputs, TimeSpan.FromHours(1));
        await WaitFor(() => t.Attempts == 1);   // the arrival report is being posted and held
        var final = sender.SendFinalAsync(TimeSpan.FromSeconds(20));
        t.Hold.SetResult();
        await final;
        Assert.Equal(2, t.PostedCount);
    }

    [Fact]
    public void The_report_carries_the_official_fixed_fields()
    {
        var report = ViewerStatsReport.Build(SampleInputs());
        Assert.Equal(false, report.GetValueOrDefault("MinimalSkin"));
        Assert.True(report.ContainsKey("AgentPositionSnaps"));
        var stats = (IDictionary<string, object?>)report["stats"]!;
        Assert.Equal(0, ((IDictionary<string, object?>)stats["failures"]!)["missing_updater"]);
        Assert.True(stats.ContainsKey("voice"));
        Assert.Equal(true, ((IDictionary<string, object?>)report["misc"]!)["Version"]);
        Assert.True(((IDictionary<string, object?>)report["system"]!).ContainsKey("gl"));
        Assert.False(report.ContainsKey("session_id"));   // added by the transport as it posts, never in anything that could be logged
    }

    [Fact]
    public void The_report_goes_out_as_LLSD_with_the_session_id_added_at_posting()
    {
        var osd = Viewer.Core.Protocol.LmvTransport.ForPosting(ViewerStatsReport.Build(SampleInputs()), Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        string xml = System.Text.Encoding.UTF8.GetString(LibreMetaverse.StructuredData.OSDParser.SerializeLLSDXmlBytes(osd));
        Assert.Contains("<key>session_id</key><uuid>aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee</uuid>", xml);
        Assert.Contains("<key>agent</key><map>", xml);
        Assert.Contains("<key>hardware_concurrency</key><integer>", xml);
    }

    private static ViewerStatsReport.Inputs SampleInputs() => new(new ViewerIdentity("Reverie Viewer", "Reverie Viewer", "0.1.0.1"), DateTimeOffset.UtcNow, 10, 10,
        ViewerStatsFigures.Unknown, 0, 0, 1, 1000, 1000, ("m", "i"), ViewerStatsTraffic.None, true);

    private static async Task WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!done() && DateTime.UtcNow < until)
            await Task.Delay(50);
        Assert.True(done());
    }

    /// <summary>A transport that logs in at once and keeps every statistics report posted to it.</summary>
    private sealed class StatsTransport : ISessionTransport, IViewerStatsTransport
    {
        public readonly List<IReadOnlyDictionary<string, object?>> Posted = [];
        public int MissingFor;
        public TaskCompletionSource? Hold;
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public int PostedCount { get { lock (Posted) return Posted.Count; } }
#pragma warning disable CS0067
        public event Action<TransportEvent>? Event;
#pragma warning restore CS0067
        public Task<TransportLoginResult> LoginAsync(TransportLoginRequest request, CancellationToken ct) =>
            Task.FromResult(FakeTransport.Ok(Guid.Parse("11111111-2222-3333-4444-555555555555")));
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
        public SimRef? CurrentSim => null;
        public int ConnectedSimulatorCount => 0;
        public void SetDrawDistance(float metres) { }
        public void Dispose() { }
        public async Task<bool> PostViewerStatsAsync(IReadOnlyDictionary<string, object?> report, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref _attempts);
            if (n <= MissingFor)
                return false;   // no ViewerStats capability yet
            if (Hold is { } hold && n == 1)
                await hold.Task.WaitAsync(ct);
            lock (Posted) Posted.Add(report);
            return true;
        }
        public (string Mac, string Id0) DeviceIds => ("0123456789abcdef0123456789abcdef", "fedcba9876543210fedcba9876543210");
        public ViewerStatsTraffic Traffic => ViewerStatsTraffic.None;
    }
}
