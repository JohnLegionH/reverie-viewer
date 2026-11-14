// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools\live-smoke.ps1, the two-account smoke test, checks its inputs before anything starts: the settings that name the
/// accounts, the region and the grid, the check that the person's own avatar is offline, and the claim command it runs itself
/// under. These tests run the script as a child process in its dry-run mode (preconditions only, nothing is started or logged in)
/// with stand-in files; the live steps themselves are exercised by the script's own run. Every test uses its own temp folder
/// and its own child process, so they run in parallel.
/// </summary>
public sealed class LiveSmokeScriptTests
{
    // A live-settings file that does not exist, so the scripts see only the settings each test gives them.
    private static readonly string NoLiveEnvFile = TestTemp.NewFilePath("no-such-live", ".env");
    private readonly ITestOutputHelper _out;
    public LiveSmokeScriptTests(ITestOutputHelper output) { _out = output; }

    private static string Script => Path.Combine(TestPaths.RepoRoot, "tools", "live-smoke.ps1");

    private sealed record Setup(string Root, string Sandbox, string Accounts, string Exe, string GridInfo, string Presence);

    private static Setup NewSetup(string presenceWord)
    {
        string root = TestTemp.NewFolder("live-smoke");
        string accounts = Path.Combine(root, "accounts.ini");
        File.WriteAllText(accounts, "[alpha1]\nfirst=Test\nlast=Alpha\n");   // never read by the dry run
        string exe = Path.Combine(root, "Reverie.console.exe");
        File.WriteAllText(exe, "");
        string gridInfo = Path.Combine(root, "get_grid_info.xml");
        File.WriteAllText(gridInfo, "<gridinfo><login>http://login.example.org:8002/</login></gridinfo>");
        string presence = Path.Combine(root, "presence.sh");
        // like the real check: the word on stdout, the reason on stderr
        File.WriteAllText(presence, $"#!/usr/bin/env bash\necho {presenceWord}\necho \"the reason goes to the error stream\" >&2\n");
        return new Setup(root, Path.Combine(root, "evidence"), accounts, exe, new Uri(gridInfo).AbsoluteUri, presence);
    }

    private (int Exit, string Output) Run(Setup s, Dictionary<string, string?>? env = null, params string[] extra)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script,
            "-Sandbox", s.Sandbox, "-Credentials", s.Accounts, "-Exe", s.Exe, "-GridInfoUrl", s.GridInfo,
            "-Region", "Example Region", "-AccountA", "alpha1", "-AccountB", "beta1", "-OtherClientPattern", "none", "-PresenceCheck", s.Presence, "-DryRun" };
        args.AddRange(extra);
        var environment = new Dictionary<string, string?>
        {
            ["NV_LIVE_ENV_FILE"] = NoLiveEnvFile, ["NV_TEST_ACCOUNTS"] = null, ["NV_LIVE_START"] = null, ["NV_GRID_INFO_URL"] = null, ["NV_OTHER_CLIENT_PATTERN"] = null,
            ["NV_PRESENCE_CHECK"] = null, ["NV_CLAIM_WRAPPER"] = null, ["NV_CLAIM_LANE"] = null, ["NV_SMOKE_REGION"] = null, ["CLAIM_TOKEN"] = null,
            ["NV_SMOKE_ACCOUNT_A"] = null, ["NV_SMOKE_ACCOUNT_B"] = null, ["NV_SMOKE_CLAIMED"] = null,
        };
        if (env is not null) foreach (var (k, v) in env) environment[k] = v;
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), environment);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        return r;
    }

    [Fact]
    public void With_every_input_in_place_and_the_avatar_offline_the_dry_run_passes_and_starts_nothing()
    {
        var s = NewSetup("offline");
        var r = Run(s);
        Assert.Equal(0, r.Exit);
        Assert.Contains("preconditions ok", r.Output);
        Assert.Contains("dry run: nothing started", r.Output);
        Assert.False(Directory.Exists(s.Sandbox), "a dry run writes no evidence folder");
    }

    [Theory]
    [InlineData("online")]
    [InlineData("unknown")]
    [InlineData("")]
    public void An_avatar_that_is_online_or_unknown_skips_the_live_run(string word)
    {
        var s = NewSetup(word);
        var r = Run(s);
        Assert.Equal(4, r.Exit);
        Assert.Contains("SKIPPED", r.Output);
        Assert.DoesNotContain("preconditions ok", r.Output);
    }

    [Fact]
    public void A_missing_presence_check_blocks_the_run()
    {
        var s = NewSetup("offline") with { Presence = "missing-presence-check.sh" };   // a path with no file behind it
        var r = Run(s);
        Assert.Equal(3, r.Exit);
        Assert.Contains("BLOCKED", r.Output);
        Assert.Contains("presence", r.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_come_from_the_environment_when_the_parameters_are_left_out()
    {
        var s = NewSetup("offline");
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Sandbox", s.Sandbox, "-Exe", s.Exe, "-DryRun" };
        var env = new Dictionary<string, string?>
        {
            ["NV_LIVE_ENV_FILE"] = NoLiveEnvFile, ["NV_TEST_ACCOUNTS"] = s.Accounts, ["NV_SMOKE_ACCOUNT_A"] = "alpha1", ["NV_SMOKE_ACCOUNT_B"] = "beta1", ["NV_LIVE_START"] = "uri:Example Region&128&128&25", ["NV_GRID_INFO_URL"] = s.GridInfo,
            ["NV_OTHER_CLIENT_PATTERN"] = "none", ["NV_PRESENCE_CHECK"] = s.Presence, ["NV_CLAIM_WRAPPER"] = null, ["CLAIM_TOKEN"] = null, ["NV_SMOKE_REGION"] = null,
        };
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), env);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("region Example Region", r.Output);
    }

    [Fact]
    public void Nothing_configured_names_every_missing_input()
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Sandbox", Path.Combine(TestTemp.NewFolder("live-smoke"), "evidence"), "-DryRun" };
        var env = new Dictionary<string, string?>
        {
            ["NV_LIVE_ENV_FILE"] = NoLiveEnvFile, ["NV_TEST_ACCOUNTS"] = null, ["NV_LIVE_START"] = null, ["NV_GRID_INFO_URL"] = null, ["NV_OTHER_CLIENT_PATTERN"] = null,
            ["NV_PRESENCE_CHECK"] = null, ["NV_CLAIM_WRAPPER"] = null, ["CLAIM_TOKEN"] = null, ["NV_SMOKE_REGION"] = null,
            ["NV_SMOKE_ACCOUNT_A"] = null, ["NV_SMOKE_ACCOUNT_B"] = null,
        };
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), env);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        Assert.Equal(3, r.ExitCode);
        foreach (var what in new[] { "Credentials", "AccountA", "AccountB", "Region", "GridInfoUrl", "OtherClientPattern", "PresenceCheck" })
            Assert.Contains(what, r.Output);
    }

    [Fact]
    public void An_evidence_folder_that_exists_is_refused()
    {
        var s = NewSetup("offline");
        Directory.CreateDirectory(s.Sandbox);
        var r = Run(s);
        Assert.Equal(3, r.Exit);
        Assert.Contains("Sandbox exists", r.Output);
    }

    [Fact]
    public void With_a_claim_wrapper_the_script_runs_itself_under_a_login_claim_and_an_avatar_claim_for_each_account()
    {
        var s = NewSetup("offline");
        var r = Run(s, new Dictionary<string, string?> { ["NV_CLAIM_WRAPPER"] = s.Presence, ["NV_CLAIM_LANE"] = "examplelane" });
        Assert.Equal(0, r.Exit);
        Assert.Contains("would run under claims", r.Output);
        Assert.Contains("live-login", r.Output);
        Assert.Contains("--resource alpha1", r.Output);
        Assert.Contains("--resource beta1", r.Output);
        Assert.Contains("examplelane", r.Output);
    }

    [Fact]
    public void Already_under_a_claim_the_script_does_not_wrap_itself_again()
    {
        var s = NewSetup("offline");
        var r = Run(s, new Dictionary<string, string?> { ["NV_CLAIM_WRAPPER"] = s.Presence, ["NV_SMOKE_CLAIMED"] = "1" });
        Assert.Equal(0, r.Exit);
        Assert.DoesNotContain("would run under claims", r.Output);
    }
}
