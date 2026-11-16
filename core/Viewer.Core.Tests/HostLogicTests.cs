// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Viewer.Automation;
using Viewer.Core.Diagnostics;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The automation API v1 is safe by construction (the test strategy 2.1).</summary>
public class AutomationSafetyTests : IDisposable
{
    private readonly string _dir = TestTemp.NewFolderPath("auto");
    private readonly string _dir2 = TestTemp.NewFolderPath("auto-2");
    private readonly string _dir3 = TestTemp.NewFolderPath("auto-3");
    private readonly List<string> _log = new();
    private readonly AutomationServer _server;
    private readonly string _tokenFile;

    public AutomationSafetyTests()
    {
        _server = new AutomationServer(c => Task.FromResult(AutomationReply.Ok(new { command = c.Command })), e => { lock (_log) _log.Add(e); });
        _tokenFile = _server.WriteTokenFile(_dir);
    }

    public void Dispose()
    {
        _server.Dispose();
        TokenFile.Remove(_tokenFile);
        TestTemp.Delete(_dir);   // only the unique folders this instance was handed
        TestTemp.Delete(_dir2);
        TestTemp.Delete(_dir3);
    }

    private (int Port, string Token) ReadTokenFile()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(_tokenFile));
        return (doc.RootElement.GetProperty("port").GetInt32(), doc.RootElement.GetProperty("token").GetString()!);
    }

    /// <summary>Raw HTTP over a socket, so every header (Host, Origin) is exactly what the test says.</summary>
    private static (int Status, string Body) Send(int port, string head, string body = "{}", int? declaredLength = null)
    {
        using var tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, port);
        var s = tcp.GetStream();
        var bytes = Encoding.UTF8.GetBytes(body);
        s.Write(Encoding.ASCII.GetBytes(head + $"Content-Length: {declaredLength ?? bytes.Length}\r\n\r\n"));
        s.Write(bytes);
        using var r = new StreamReader(s);
        string all = r.ReadToEnd();
        int status = int.Parse(all.Split(' ')[1]);
        return (status, all[(all.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
    }

    private static string Head(int port, string? token, string command = "session", string method = "POST", string? host = null, string? extra = null, string type = "application/json") =>
        $"{method} /v1/{command} HTTP/1.1\r\nHost: {host ?? $"127.0.0.1:{port}"}\r\n" +
        (token is null ? "" : $"Authorization: Bearer {token}\r\n") + $"Content-Type: {type}\r\n" + (extra ?? "");

    [Fact]
    public void It_is_off_unless_the_explicit_flag_is_given()
    {
        Assert.False(AutomationArgs.Enabled([]));
        Assert.False(AutomationArgs.Enabled(["--automation=1", "--automationx", "automation", "--s1-out=x"]));
        Environment.SetEnvironmentVariable("NV_AUTOMATION", "1");
        try { Assert.False(AutomationArgs.Enabled(["--pseudo-loc"])); }   // no environment enable path
        finally { Environment.SetEnvironmentVariable("NV_AUTOMATION", null); }
        Assert.True(AutomationArgs.Enabled(["--automation"]));
    }

    [Fact]
    public void It_binds_loopback_only_on_a_random_port()
    {
        Assert.Equal(IPAddress.Loopback, _server.Address);
        using var other = new AutomationServer(c => Task.FromResult(AutomationReply.Ok(new { })));
        Assert.NotEqual(0, _server.Port);
        Assert.NotEqual(_server.Port, other.Port);
        Assert.Equal(_server.Port, ReadTokenFile().Port);
    }

    [LanAddressFact]
    public void It_is_not_reachable_on_a_non_loopback_address()
    {
        using var tcp = new TcpClient();
        var ex = Assert.ThrowsAny<SocketException>(() => tcp.Connect(LanAddressFactAttribute.Lan()!, _server.Port));
        Assert.Equal(SocketError.ConnectionRefused, ex.SocketErrorCode);
    }

    [Fact]
    public void A_missing_or_wrong_token_is_refused_and_the_right_one_is_accepted()
    {
        var (port, token) = ReadTokenFile();
        Assert.Equal(401, Send(port, Head(port, null)).Status);
        Assert.Equal(401, Send(port, Head(port, new string('0', 64))).Status);
        Assert.Equal(401, Send(port, Head(port, token[..^1])).Status);
        Assert.Equal(401, Send(port, Head(port, null, extra: $"Authorization: Basic {token}\r\n")).Status);
        var ok = Send(port, Head(port, token));
        Assert.Equal(200, ok.Status);
        Assert.Contains("\"command\":\"session\"", ok.Body);
        Assert.DoesNotContain(token, ok.Body);
    }

    [Theory]
    [InlineData("Origin: http://evil.example\r\n")]
    [InlineData("Origin: null\r\n")]
    [InlineData("Origin: http://127.0.0.1\r\n")]
    [InlineData("Sec-Fetch-Site: cross-site\r\n")]
    [InlineData("Sec-Fetch-Mode: no-cors\r\n")]
    public void A_browser_request_is_refused_even_with_the_token(string header)
    {
        var (port, token) = ReadTokenFile();
        var r = Send(port, Head(port, token, extra: header));
        Assert.Equal(403, r.Status);
        Assert.Contains("origin", r.Body);
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("127.0.0.1")]          // no port: not the listener's own authority
    [InlineData("198.51.100.10:{port}")]
    public void A_foreign_host_header_is_refused_dns_rebinding(string host)
    {
        var (port, token) = ReadTokenFile();
        var r = Send(port, Head(port, token, host: host.Replace("{port}", port.ToString())));
        Assert.Equal(403, r.Status);
        Assert.Contains("host", r.Body);
    }

    [Fact]
    public void Only_json_posts_to_known_commands_run()
    {
        var (port, token) = ReadTokenFile();
        Assert.Equal(405, Send(port, Head(port, token, method: "GET")).Status);
        Assert.Equal(415, Send(port, Head(port, token, type: "text/plain")).Status);
        Assert.Equal(404, Send(port, Head(port, token, command: "eval")).Status);
        Assert.Equal(413, Send(port, Head(port, token), "", declaredLength: AutomationServer.MaxBodyBytes + 1).Status);   // refused on the declared length, before any body is read
        foreach (var c in AutomationServer.Commands)
            Assert.Equal(200, Send(port, Head(port, token, command: c)).Status);
        Assert.Equal(["session", "observe", "screenshot", "camera", "orbit", "layers", "neighbours", "terrain", "neighbour_debug", "texture_probe", "look_probe", "plain_ground", "mesh_probe", "moving_probe", "presented_audit", "avatar_probe", "frame_burst", "keys", "mouselook", "motion", "chat", "friends", "groups", "teleport", "map", "inventory", "nametags", "prefs", "fault", "mute", "reconnect", "login_dialog", "login_screen", "menus", "prefs_layout", "upload_notice", "texture_preview", "windows", "inventory_view", "interact", "build", "edit", "aim", "body_mask", "dialog", "open_window", "sky", "personal", "overrider", "notecard", "script", "addons", "expression", "sample", "engine", "input", "typing", "ui_sounds", "world_sounds", "sound_tuner", "topbar", "dock", "quit"], AutomationServer.Commands);   // camera, orbit, layers; neighbours (the regions and what is drawn for each); texture_probe; look_probe (water, trees, textured objects and texture levels for the live look check); keys; mouselook; motion; chat; friends; groups; teleport; map; inventory; nametags, fault, mute and reconnect; interact; dialog; sky (time of day) and sample (memory and engine figures for a long run)
    }

    [Fact]
    public void The_token_file_is_owner_only()
    {
        var (prot, allowed) = TokenFile.Describe(_tokenFile);
        Assert.True(prot, "inherited access rules must be cut off");
        Assert.Equal([TokenFile.CurrentUserSid()], allowed);
        Assert.Equal(TokenFile.PathFor(_dir, Environment.ProcessId), _tokenFile);
    }

    [Fact]
    public void The_token_is_never_in_the_log_or_on_the_console()
    {
        var (port, token) = ReadTokenFile();
        var stdout = Console.Out;
        var stderr = Console.Error;
        var capture = new StringWriter();
        Console.SetOut(capture);
        Console.SetError(capture);
        try
        {
            using var s = new AutomationServer(c => Task.FromResult(AutomationReply.Ok(new { })), e => Console.Error.WriteLine(e));
            string f = s.WriteTokenFile(_dir2);
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            string t2 = doc.RootElement.GetProperty("token").GetString()!;
            Send(s.Port, Head(s.Port, t2));
            Send(s.Port, Head(s.Port, "wrong"));
            s.Dispose();
            TokenFile.Remove(f);
            Send(port, Head(port, token));
            Assert.DoesNotContain(t2, capture.ToString());
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
        Assert.DoesNotContain(token, capture.ToString());
        lock (_log) Assert.DoesNotContain(_log, l => l.Contains(token));
        Assert.Contains("automation.refused.token", capture.ToString());   // the log saw the refusal, by name only
    }

    [Fact]
    public void The_token_file_is_removed_by_its_owner()
    {
        using var s = new AutomationServer(c => Task.FromResult(AutomationReply.Ok(new { })));
        string f = s.WriteTokenFile(_dir3);
        Assert.True(File.Exists(f));
        TokenFile.Remove(f);
        Assert.False(File.Exists(f));
        Assert.Throws<ArgumentException>(() => s.WriteTokenFile("relative\\dir"));
    }

    [Fact]
    public void Only_approved_accounts_can_be_used_and_the_password_stays_a_secret()
    {
        string f = Path.Combine(_dir, "accounts.json");
        File.WriteAllText(f, """{"accounts":[{"alias":"tester1","first":"Test","last":"User","password":"pw-not-real"},{"alias":"someone","first":"S","last":"O","password":"x"}]}""");
        var (first, last, pw) = AccountsFile.Load(f, "tester1");
        Assert.Equal(("Test", "User"), (first, last));
        Assert.Equal("[redacted:PasswordDigest]", pw.ToString());
        Assert.Throws<InvalidOperationException>(() => AccountsFile.Load(f, "someone"));
    }
}

/// <summary>A real SKIP, with its reason, on a machine with no non-loopback IPv4 address to try.</summary>
public sealed class LanAddressFactAttribute : FactAttribute
{
    public LanAddressFactAttribute()
    {
        if (Lan() is null)
            Skip = "no non-loopback IPv4 address on this machine to try";
    }

    public static IPAddress? Lan() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
}

/// <summary>The presentation coordinator drains within a per-frame budget and reports its backlog age.</summary>
public class PresentationCoordinatorTests
{
    private sealed class FakeSink(Action onApply, Action onStep, int steps) : IPresentationSink
    {
        public readonly List<WorldChange> Applied = new();
        public int StepsLeft = steps;
        public void Apply(WorldChange change) { Applied.Add(change); onApply(); }
        public bool Step() { if (StepsLeft == 0) return false; StepsLeft--; onStep(); return true; }
    }

    private static WorldChange Patch(int x) => new(WorldChangeKind.TerrainPatch, new ObjectId(1, 0), PatchX: x, PatchY: 0);

    [Fact]
    public void A_frame_stops_at_its_budget_and_the_rest_waits_in_order()
    {
        long now = 0;
        var sink = new FakeSink(() => now += 1, () => now += 3, steps: 10);   // 1 ms per change, 3 ms per chunk rebuild
        var c = new PresentationCoordinator(sink, budgetMs: 4, clock: () => now, ticksPerSecond: 1000);
        for (int i = 0; i < 10; i++) c.Enqueue(Patch(i));
        now += 50;
        var f1 = c.RunFrame();
        Assert.Equal(4, f1.Applied);
        Assert.Equal(0, f1.Steps);
        Assert.Equal(6, f1.Backlog);
        Assert.Equal(54, f1.OldestAgeMs);   // waiting since t=0, now t=54
        Assert.False(f1.OverBudget);
        while (c.Backlog > 0) c.RunFrame();
        Assert.Equal(Enumerable.Range(0, 10), sink.Applied.Select(a => a.PatchX));
        while (sink.StepsLeft > 0) c.RunFrame();
        Assert.Equal(0, c.BacklogAgeMs);
        Assert.Equal(10, c.Applied);
    }

    [Fact]
    public void A_frame_always_makes_progress_even_when_one_unit_exceeds_the_budget()
    {
        long now = 0;
        var sink = new FakeSink(() => now += 10, () => now += 10, steps: 1);
        var c = new PresentationCoordinator(sink, budgetMs: 2, clock: () => now, ticksPerSecond: 1000);
        c.Enqueue(Patch(0));
        c.Enqueue(Patch(1));
        var f = c.RunFrame();
        Assert.Equal(1, f.Applied);
        Assert.True(f.OverBudget);
        Assert.Equal(1, c.FramesOverBudget);
        c.RunFrame();
        var f3 = c.RunFrame();   // queue empty: the deferred step runs
        Assert.Equal(1, f3.Steps);
    }

    [Fact]
    public void Changes_from_network_threads_are_all_delivered_on_the_draining_thread()
    {
        var sink = new FakeSink(() => { }, () => { }, 0);
        var c = new PresentationCoordinator(sink, budgetMs: double.MaxValue);
        Parallel.For(0, 8, t => { for (int i = 0; i < 1000; i++) c.Enqueue(Patch(t * 1000 + i)); });
        c.RunFrame();
        Assert.Equal(8000, sink.Applied.Count);
        Assert.Equal(8000, sink.Applied.Select(a => a.PatchX).Distinct().Count());
    }

    [Fact]
    public void Terrain_changes_name_their_patch()
    {
        using var w = new WorldState(new CoreLog(new ListSink(), "t"));
        var sim = new object();
        w.BeginRegion(sim, new RegionInfo("r", 1, 1, 256, 256, true));
        var seen = new List<WorldChange>();
        w.Changed += seen.Add;
        w.ApplyPatch(sim, 3, 7, 16, new float[256]);
        Assert.Equal((3, 7), (seen.Single().PatchX, seen.Single().PatchY));
        Assert.Equal(-1, new WorldChange(WorldChangeKind.ObjectAdded, new ObjectId(1, 5)).PatchX);
    }
}

/// <summary>Terrain chunks from the core's patches, for any region size.</summary>
public class TerrainGridTests
{
    private static PatchSource From(TerrainFixture t, Func<int, int, bool>? present = null)
    {
        var map = t.Patches().ToDictionary(p => (p.X, p.Y), p => p.Heights);
        return (int px, int py, out float[] h) =>
        {
            h = null!;
            return (present?.Invoke(px, py) ?? true) && map.TryGetValue((px, py), out h!);
        };
    }

    [Theory]
    [InlineData(256, 256, 4, 4)]
    [InlineData(512, 256, 8, 4)]
    [InlineData(1024, 1024, 16, 16)]
    public void Chunks_cover_the_region_for_any_size(int sx, int sy, int cx, int cy)
    {
        var t = SyntheticTerrain.Make("t", sx, sy);
        var g = new TerrainGrid(sx, sy, 16, From(t));
        Assert.Equal((cx, cy), (g.ChunksX, g.ChunksY));
        var c = g.Build(cx - 1, cy - 1)!;
        Assert.Equal(65 * 65, c.VertexCount);
        Assert.Equal(64 * 64 * 2, c.TriangleCount);
        Assert.Null(g.Build(cx, cy));
        // Heights are the store's, in engine space (x east, y up, z = -north).
        int v = 10 * 65 + 20;
        Assert.Equal((cx - 1) * 64 + 20, c.Positions[v * 3]);
        Assert.Equal(t.At((cx - 1) * 64 + 20, (cy - 1) * 64 + 10), c.Positions[v * 3 + 1]);
        Assert.Equal(-((cy - 1) * 64 + 10), c.Positions[v * 3 + 2]);
    }

    [Fact]
    public void Neighbouring_chunks_share_their_edge_heights()
    {
        var t = SyntheticTerrain.Make("t", 256, 256);
        var g = new TerrainGrid(256, 256, 16, From(t));
        var a = g.Build(0, 0)!;
        var b = g.Build(1, 0)!;
        for (int j = 0; j < 65; j++)
            Assert.Equal(a.Positions[(j * 65 + 64) * 3 + 1], b.Positions[(j * 65) * 3 + 1]);
    }

    [Fact]
    public void A_missing_patch_is_a_hole_not_a_spike()
    {
        var t = SyntheticTerrain.Make("t", 256, 256);
        var g = new TerrainGrid(256, 256, 16, From(t, (px, py) => (px, py) != (1, 0)));
        var c = g.Build(0, 0)!;
        Assert.Equal(64 * 64 * 2 - 16 * 16 * 2, c.TriangleCount);
        // The edge column shared with the missing patch keeps the present patch's heights.
        for (int j = 0; j < 16; j++)
            Assert.Equal(t.At(15, j), c.Positions[(j * 65 + 16) * 3 + 1]);
        var none = new TerrainGrid(256, 256, 16, (int x, int y, out float[] h) => { h = null!; return false; });
        Assert.Null(none.Build(0, 0));
    }

    [Fact]
    public void Chunk_building_is_deterministic()
    {
        var t = SyntheticTerrain.Make("t", 512, 256);
        var a = new TerrainGrid(512, 256, 16, From(t)).Build(3, 2)!;
        var b = new TerrainGrid(512, 256, 16, From(t)).Build(3, 2)!;
        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Normals, b.Normals);
        Assert.Equal(a.Indices, b.Indices);
    }
}

/// <summary>L4: the committed synthetic fixture, and the image diff with its declared tolerance.</summary>
public class RenderFixtureTests
{
    public static string FixturePath => Path.Combine(TestPaths.RepoRoot, "fixtures", "terrain", "synthetic-512x256.nvterrain.gz");
    public static string ManifestPath => Path.Combine(TestPaths.RepoRoot, "fixtures", "terrain", "manifest.json");

    [Fact]
    public void The_committed_synthetic_fixture_matches_its_generator_and_its_manifest()
    {
        var expected = SyntheticTerrain.Make("synthetic-512x256", 512, 256).Serialise();
        if (Environment.GetEnvironmentVariable("NV_WRITE_TERRAIN_FIXTURE") == "1")
            File.WriteAllBytes(FixturePath, expected);   // proposal only; the manifest records who approved what
        var file = File.ReadAllBytes(FixturePath);
        Assert.Equal(TerrainFixture.Sha256(expected), TerrainFixture.Sha256(file));
        using var m = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        var f = m.RootElement.GetProperty("fixtures").GetProperty("synthetic-512x256");
        Assert.Equal(TerrainFixture.Sha256(file), f.GetProperty("sha256").GetString());
        var t = TerrainFixture.Parse(file);
        Assert.Equal((512, 256, 16), (t.SizeX, t.SizeY, t.PatchSize));
        Assert.Equal(32 * 16, t.Patches().Count());
        var tol = m.RootElement.GetProperty("tolerance");
        Assert.Equal(DiffTolerance.Terrain, new DiffTolerance(tol.GetProperty("channel_threshold").GetInt32(), tol.GetProperty("max_differing_fraction").GetDouble(), tol.GetProperty("max_mean_abs_error").GetDouble()));
    }

    [Fact]
    public void Every_golden_in_the_manifest_is_committed_with_its_backend()
    {
        using var m = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        var goldens = m.RootElement.GetProperty("goldens").EnumerateArray().ToList();
        Assert.Equal(["d3d12", "vulkan"], goldens.Select(g => g.GetProperty("backend").GetString()).Order());
        foreach (var g in goldens)
            Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot, "fixtures", "terrain", g.GetProperty("file").GetString()!)));
    }

    private static byte[] Solid(int w, int h, byte v) => Enumerable.Repeat(v, w * h * 4).ToArray();

    [Fact]
    public void Identical_and_noisy_images_pass_a_changed_region_fails()
    {
        var g = Solid(100, 100, 120);
        Assert.True(ImageDiff.Compare(g, g, 100, 100, 4, DiffTolerance.Terrain).Pass);
        var noisy = (byte[])g.Clone();
        for (int i = 0; i < noisy.Length; i += 37) noisy[i] = (byte)(noisy[i] + 6);   // below the channel threshold
        for (int p = 0; p < 15; p++) noisy[p * 4 * 97] = 255;                        // 15 of 10,000 pixels: 0.15 %
        var d = ImageDiff.Compare(noisy, g, 100, 100, 4, DiffTolerance.Terrain);
        Assert.True(d.Pass, d.ToString());
        var hill = (byte[])g.Clone();
        for (int y = 40; y < 60; y++) for (int x = 40; x < 60; x++) hill[(y * 100 + x) * 4 + 1] = 200;   // 4 %
        var f = ImageDiff.Compare(hill, g, 100, 100, 4, DiffTolerance.Terrain);
        Assert.False(f.Pass);
        Assert.Equal(400, f.Differing);
        Assert.False(ImageDiff.Compare(g, Solid(100, 99, 120), 100, 100, 4, DiffTolerance.Terrain).Pass);
    }
}

/// <summary>A replay can hand its live session over (the region B terrain is rendered from it).</summary>
public class ReplayLoadTests
{
    [Fact]
    public void A_loaded_replay_is_the_same_world_as_a_run()
    {
        var (path, live, _) = CaptureReplayTests.Record("load.jsonl.gz");
        var c = CaptureFile.Read(path);
        using var session = ReplayRunner.Load(c);
        Assert.Equal(ReplayRunner.Run(c).Summary, StateSummary.Of(session.World));
        Assert.Equal(live, StateSummary.Of(session.World));
        Assert.True(session.World.TerrainComplete);
        Assert.False(session.IsEnded);
    }
}
