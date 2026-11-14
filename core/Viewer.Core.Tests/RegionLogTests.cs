// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The region-connect log line wrote a field name the logger rejects ("invalid-field"), and
/// Alpha's region name was empty at connect because the library had not learned it yet. The name now comes from the map
/// block for our own cell when the region had none.
/// </summary>
public class RegionLogTests
{
    private static readonly SimRef Unnamed = new(1099511628032000UL, "");   // Alpha's cell, name not known at connect

    private static (ViewerSession, FakeTransport, ListSink) InWorld(SimRef sim)
    {
        var t = new FakeTransport();
        var sink = new ListSink();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, t, new CoreLog(sink, "test"));
        Assert.True(s.LoginAsync(new LoginCredentials("A", "B", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        t.DriveToInWorld(sim);
        return (s, t, sink);
    }

    [Fact]
    public void A_whole_session_logs_no_rejected_field_or_event_name()
    {
        var (s, t, sink) = InWorld(Unnamed);
        t.Raise(new MapBlockSize(Unnamed.GridX, Unnamed.GridY, 256, 256, "Alpha"));
        s.LogoutAsync(CancellationToken.None).Wait();
        Assert.Contains(sink.Entries, e => e.Event == "region.connect");
        Assert.Contains(sink.Entries, e => e.Event == "region.connect" && e.Fields.Any(f => f.Key == "login-reply"));
        Assert.DoesNotContain(sink.Entries, e => e.Event == "invalid-event" || e.Fields.Any(f => f.Key == "invalid-field"));
    }

    [Fact]
    public void Negative_control_a_rejected_field_name_is_visible_to_the_guard()
    {
        var sink = new ListSink();
        new CoreLog(sink, "test").Info("region.connect", CoreLog.F("loginReply", "0x0"));   // the defect
        Assert.Contains(sink.Entries, e => e.Fields.Any(f => f.Key == "invalid-field"));
    }

    [Fact]
    public void The_region_name_arrives_with_the_map_block_and_is_logged_once()
    {
        var (s, t, sink) = InWorld(Unnamed);
        Assert.Equal("", s.Region!.Name);

        t.Raise(new MapBlockSize(Unnamed.GridX, Unnamed.GridY, 256, 256, "Alpha"));
        Assert.Equal("Alpha", s.Region!.Name);
        Assert.Equal("Alpha", s.World.Region!.Name);
        var named = Assert.Single(sink.Entries, e => e.Event == "region.named");
        Assert.Contains(named.Fields, f => f.Key == "region" && f.Value == "Alpha");

        // A later block (another name, or the same) never renames a region that has one; and a block for another cell is ignored.
        t.Raise(new MapBlockSize(Unnamed.GridX, Unnamed.GridY, 256, 256, "Other"));
        t.Raise(new MapBlockSize(Unnamed.GridX + 1, Unnamed.GridY, 256, 256, "Neighbour"));
        Assert.Equal("Alpha", s.Region!.Name);
        Assert.Single(sink.Entries, e => e.Event == "region.named");
    }

    [Fact]
    public void A_region_named_at_connect_keeps_its_name_and_logs_no_rename()
    {
        var (s, t, sink) = InWorld(new SimRef(1099511628032000UL, "Alpha"));
        t.Raise(new MapBlockSize(1000, 1000, 256, 256, "Something else"));
        Assert.Equal("Alpha", s.Region!.Name);
        Assert.DoesNotContain(sink.Entries, e => e.Event == "region.named");
    }
}

/// <summary>
/// Generalised: every log event and field name written as a literal in the core satisfies CoreLog's identifier rule
/// (lower case, dot/dash/underscore separated), so none is replaced by "invalid-field" / "invalid-event" at run time.
/// </summary>
public class LogNameTests
{
    private static readonly System.Text.RegularExpressions.Regex Identifier = new(@"^[a-z0-9]+([._-][a-z0-9]+)*$");
    private static readonly System.Text.RegularExpressions.Regex Uses = new(@"CoreLog\.F\(""(?<n>[^""]*)""|\.(?:Info|Warn|Error|Debug)\(""(?<n>[^""]*)""");

    internal static List<string> BadNames(string source) =>
        Uses.Matches(source).Select(m => m.Groups["n"].Value).Where(n => !Identifier.IsMatch(n)).Distinct().ToList();

    [Fact]
    public void Every_log_name_literal_in_the_core_is_a_valid_identifier()
    {
        var root = Path.Combine(TestPaths.RepoRoot, "core", "Viewer.Core");
        var bad = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => BadNames(File.ReadAllText(f)).Select(n => $"{Path.GetFileName(f)}: {n}")).ToList();
        Assert.Empty(bad);
    }

    /// <summary>The host's residency.budget line wrote "invalid-field" for every field (camel
    /// case), so the budget was unreadable in the live log. The host projects are scanned too.</summary>
    [Fact]
    public void Every_log_name_literal_in_the_host_is_a_valid_identifier()
    {
        var bad = new[] { "Viewer.Godot", "Viewer.Shell", "Viewer.Automation" }
            .Select(p => Path.Combine(TestPaths.RepoRoot, "host", p)).Where(Directory.Exists)
            .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}.godot{Path.DirectorySeparatorChar}"))
            .SelectMany(f => BadNames(File.ReadAllText(f)).Select(n => $"{Path.GetFileName(f)}: {n}")).ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void Negative_control_the_scan_finds_a_camel_case_name() =>
        Assert.Equal(["loginReply", "Bad.Event"], BadNames("x.Info(\"Bad.Event\", CoreLog.F(\"loginReply\", 1)); CoreLog.F(\"ok-name\", 2);").OrderByDescending(n => n).ToList());
}
