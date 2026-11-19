// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools\stock-smoke.ps1, the live smoke test against a stock upstream OpenSim, checks its inputs before anything starts: the two
/// accounts, the region, the server's grid-info address and control script, and the claim command it runs itself under. These tests
/// run it as a child process in its dry-run mode with stand-in files; nothing is started or logged in. Every test uses its own temp
/// folder and child process, so they run in parallel.
/// </summary>
public sealed class StockSmokeScriptTests
{
    private readonly ITestOutputHelper _out;
    public StockSmokeScriptTests(ITestOutputHelper output) { _out = output; }

    private static string Script => Path.Combine(TestPaths.RepoRoot, "tools", "stock-smoke.ps1");
    private static readonly string[] SettingNames =
        ["NV_TEST_ACCOUNTS", "NV_STOCK_ACCOUNT_A", "NV_STOCK_ACCOUNT_B", "NV_STOCK_REGION", "NV_STOCK_GRID_INFO_URL", "NV_STOCK_SERVER_CONTROL",
         "NV_CLAIM_WRAPPER", "NV_CLAIM_LANE", "NV_CLAIM_ORDER", "NV_STOCK_SMOKE_CLAIMED", "CLAIM_TOKEN", "NV_CLAIM_CHECK_SCRIPT", "NV_CLAIM_KINDS", "NV_AVATAR_POOL"];

    private sealed record Setup(string Root, string Sandbox, string Accounts, string Exe, string Control, string GridInfo);

    private static Setup NewSetup()
    {
        string root = TestTemp.NewFolder("stock-smoke");
        string accounts = Path.Combine(root, "accounts.json");
        File.WriteAllText(accounts, "{}");   // never read by the dry run
        string exe = Path.Combine(root, "Reverie.console.exe");
        File.WriteAllText(exe, "");
        string control = Path.Combine(root, "control.sh");
        File.WriteAllText(control, "#!/usr/bin/env bash\nexit 0\n");
        string gridInfo = Path.Combine(root, "get_grid_info.xml");
        File.WriteAllText(gridInfo, "<gridinfo><login>http://login.example.org:9400/</login></gridinfo>");
        return new Setup(root, Path.Combine(root, "evidence"), accounts, exe, control, new Uri(gridInfo).AbsoluteUri);
    }

    private (int Exit, string Output) Run(Setup s, Dictionary<string, string?>? env = null, string[]? replace = null)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Sandbox", s.Sandbox,
            "-Credentials", s.Accounts, "-Exe", s.Exe, "-GridInfoUrl", s.GridInfo, "-ServerControl", s.Control,
            "-Region", "Example Region", "-AccountA", "alpha1", "-AccountB", "beta1", "-DryRun" };
        var environment = SettingNames.ToDictionary(n => n, _ => (string?)null);
        if (env is not null) foreach (var (k, v) in env) environment[k] = v;
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), environment);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        return r;
    }

    [Fact]
    public void With_every_input_in_place_the_dry_run_passes_and_starts_nothing()
    {
        var s = NewSetup();
        var r = Run(s);
        Assert.Equal(0, r.Exit);
        Assert.Contains("preconditions ok", r.Output);
        Assert.Contains("dry run: nothing started", r.Output);
        Assert.False(Directory.Exists(s.Sandbox), "a dry run writes no evidence folder");
    }

    [Fact]
    public void A_control_script_that_does_not_exist_blocks_the_run()
    {
        var s = NewSetup();
        var r = Run(s with { Control = Path.Combine(s.Root, "missing.sh") });
        Assert.Equal(3, r.Exit);
        Assert.Contains("BLOCKED", r.Output);
        Assert.Contains("-ServerControl", r.Output);
    }

    [Fact]
    public void Two_runs_of_the_same_account_are_refused()
    {
        var s = NewSetup();
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Sandbox", s.Sandbox,
            "-Credentials", s.Accounts, "-Exe", s.Exe, "-GridInfoUrl", s.GridInfo, "-ServerControl", s.Control,
            "-Region", "Example Region", "-AccountA", "alpha1", "-AccountB", "alpha1", "-DryRun" };
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), SettingNames.ToDictionary(n => n, _ => (string?)null));
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("two different accounts", r.Output);
    }

    [Fact]
    public void The_settings_come_from_the_environment_when_no_parameter_names_them()
    {
        var s = NewSetup();
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Sandbox", s.Sandbox, "-Exe", s.Exe, "-DryRun" };
        var environment = SettingNames.ToDictionary(n => n, _ => (string?)null);
        environment["NV_TEST_ACCOUNTS"] = s.Accounts; environment["NV_STOCK_ACCOUNT_A"] = "alpha1"; environment["NV_STOCK_ACCOUNT_B"] = "beta1";
        environment["NV_STOCK_REGION"] = "Example Region"; environment["NV_STOCK_GRID_INFO_URL"] = s.GridInfo; environment["NV_STOCK_SERVER_CONTROL"] = s.Control;
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), environment);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("preconditions ok: region Example Region, accounts alpha1 and beta1", r.Output);
    }

    [Fact]
    public void The_settings_come_from_a_settings_file_named_by_the_environment()
    {
        var s = NewSetup();
        string settings = Path.Combine(s.Root, "stock.env");
        File.WriteAllText(settings, string.Join("\n",
            "# a comment line", "",
            $"NV_TEST_ACCOUNTS={s.Accounts}", "NV_STOCK_ACCOUNT_A=alpha1", "NV_STOCK_ACCOUNT_B=beta1", "NV_STOCK_REGION=Example Region",
            $"NV_STOCK_GRID_INFO_URL={s.GridInfo}", $"NV_STOCK_SERVER_CONTROL={s.Control}", ""));
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Sandbox", s.Sandbox, "-Exe", s.Exe, "-DryRun" };
        var environment = SettingNames.ToDictionary(n => n, _ => (string?)null);
        environment["NV_STOCK_SETTINGS"] = settings;
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), environment);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("preconditions ok: region Example Region, accounts alpha1 and beta1", r.Output);
    }

    [Fact]
    public void An_environment_variable_wins_over_the_settings_file()
    {
        var s = NewSetup();
        string settings = Path.Combine(s.Root, "stock.env");
        File.WriteAllText(settings, string.Join("\n",
            $"NV_TEST_ACCOUNTS={s.Accounts}", "NV_STOCK_ACCOUNT_A=fromfile1", "NV_STOCK_ACCOUNT_B=beta1", "NV_STOCK_REGION=Example Region",
            $"NV_STOCK_GRID_INFO_URL={s.GridInfo}", $"NV_STOCK_SERVER_CONTROL={s.Control}", ""));
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Sandbox", s.Sandbox, "-Exe", s.Exe, "-DryRun" };
        var environment = SettingNames.ToDictionary(n => n, _ => (string?)null);
        environment["NV_STOCK_SETTINGS"] = settings; environment["NV_STOCK_ACCOUNT_A"] = "fromenv1";
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), environment);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("accounts fromenv1 and beta1", r.Output);
    }

    [Fact]
    public void With_no_settings_anywhere_the_run_is_blocked_and_names_each_missing_setting_and_where_it_looked()
    {
        var s = NewSetup();
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Sandbox", s.Sandbox, "-Exe", s.Exe, "-DryRun" };
        var environment = SettingNames.ToDictionary(n => n, _ => (string?)null);
        environment["NV_STOCK_SETTINGS"] = Path.Combine(s.Root, "no-such-settings.env");
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), environment);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        Assert.Equal(3, r.ExitCode);
        foreach (var name in new[] { "NV_TEST_ACCOUNTS", "NV_STOCK_ACCOUNT_A", "NV_STOCK_ACCOUNT_B", "NV_STOCK_REGION", "NV_STOCK_GRID_INFO_URL", "NV_STOCK_SERVER_CONTROL" })
            Assert.Contains(name, r.Output);
        Assert.Contains("no-such-settings.env", r.Output);   // where it looked
        Assert.Contains("NV_STOCK_SETTINGS", r.Output);
    }

    [Fact]
    public void Under_a_claim_wrapper_the_dry_run_names_an_avatar_claim_for_each_account()
    {
        var s = NewSetup();
        string wrapper = Path.Combine(s.Root, "claim.sh");
        File.WriteAllText(wrapper, "#!/usr/bin/env bash\nexit 0\n");
        var r = Run(s, new() { ["NV_CLAIM_WRAPPER"] = wrapper, ["NV_CLAIM_LANE"] = "example-lane" });
        Assert.Equal(0, r.Exit);
        Assert.Contains("would run under claims", r.Output);
        Assert.Contains("--resource alpha1 example-lane avatar", r.Output);
        Assert.Contains("--resource beta1 example-lane avatar", r.Output);
    }

    [Fact]
    public void The_claims_end_in_a_kind_the_viewer_guard_accepts()
    {
        var s = NewSetup();
        string wrapper = Path.Combine(s.Root, "claim.sh");
        File.WriteAllText(wrapper, "#!/usr/bin/env bash\nexit 0\n");
        var r = Run(s, new() { ["NV_CLAIM_WRAPPER"] = wrapper, ["NV_CLAIM_LANE"] = "example-lane" });
        Assert.Equal(0, r.Exit);
        // the claim the viewer is started under is the innermost one, the last before the script itself
        Assert.Matches(@"example-lane (build|live-login|live-test|measure) stock smoke -- powershell", r.Output);
    }

    [Fact]
    public void Run_under_stand_ins_for_the_claim_wrapper_and_the_guard_the_script_is_not_refused()
    {
        // the wrapper stand-in gives the command the kind of the claim it was asked for as its token, as the real one gives a token;
        // the guard stand-in accepts a token only when it names one of the kinds the machine's guard lists
        var s = NewSetup();
        string wrapper = Path.Combine(s.Root, "claim.sh");
        File.WriteAllText(wrapper, string.Join("\n",
            "#!/usr/bin/env bash",
            "kind=''",
            "args=(\"$@\")",
            "for ((i = 0; i < ${#args[@]}; i++)); do",
            "  if [ \"${args[$i]}\" = '--resource' ]; then kind=\"${args[$((i + 3))]}\"; fi",
            "  if [ \"${args[$i]}\" = '--' ]; then shift $((i + 1)); break; fi",
            "done",
            "export CLAIM_TOKEN=\"$kind\"",
            "exec \"$@\"", ""));
        string guard = Path.Combine(s.Root, "check.sh");
        File.WriteAllText(guard, string.Join("\n",
            "#!/usr/bin/env bash",
            "shift",
            "token=\"$1\"; shift",
            "for k in \"$@\"; do [ \"$k\" = \"$token\" ] && exit 0; done",
            "echo \"a claim of kind '$token' does not start a viewer\"",
            "exit 6", ""));
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Sandbox", s.Sandbox,
            "-Credentials", s.Accounts, "-Exe", s.Exe, "-GridInfoUrl", s.GridInfo, "-ServerControl", s.Control,
            "-Region", "Example Region", "-AccountA", "alpha1", "-AccountB", "beta1", "-CheckClaimsOnly" };
        var environment = SettingNames.ToDictionary(n => n, _ => (string?)null);
        environment["NV_CLAIM_WRAPPER"] = wrapper; environment["NV_CLAIM_LANE"] = "example-lane";
        environment["NV_CLAIM_CHECK_SCRIPT"] = guard; environment["NV_CLAIM_KINDS"] = "build measure live-test live-login";
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), environment);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("claims accepted", r.Output);
        Assert.False(Directory.Exists(s.Sandbox), "the check starts nothing and writes no evidence folder");
    }

    /// <summary>A program name no process on the machine carries, for the script's check that no viewer that can log in is running:
    /// these tests must not depend on the viewers other sessions on the same machine run.</summary>
    private static string NoViewerName() => "nv-no-viewer-" + Guid.NewGuid().ToString("N") + "*.exe";

    /// <summary>The script refuses to start while a viewer that can log in (its program name matched, --automation on its command line)
    /// is running. The running viewer is a stand-in: a copy of the command interpreter under a name of this test's own, so no other
    /// session's viewer counts and no other session's check sees this one.</summary>
    [Fact]
    public void A_viewer_that_can_log_in_already_running_blocks_the_run()
    {
        if (!OperatingSystem.IsWindows()) return;
        var s = NewSetup();
        string name = "nv-standin-" + Guid.NewGuid().ToString("N");
        string standin = Path.Combine(s.Root, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), standin);
        // the viewer the script would start were the check to miss the running one: a script that only waits, no program and no window
        string viewer = Path.Combine(s.Root, "standin-viewer.cmd");
        File.WriteAllText(viewer, "@echo off\r\nping -n 20 127.0.0.1 >nul\r\n");
        using var running = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(standin, "/c ping -n 60 127.0.0.1 >nul & rem --automation")
            { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Sandbox", s.Sandbox,
                "-Credentials", s.Accounts, "-Exe", viewer, "-GridInfoUrl", s.GridInfo, "-ServerControl", s.Control,
                "-Region", "Example Region", "-AccountA", "alpha1", "-AccountB", "beta1", "-StartSeconds", "3", "-ViewerProcessName", name + "*.exe" };
            var environment = SettingNames.ToDictionary(n => n, _ => (string?)null);
            environment["NV_STOCK_SETTINGS"] = null;
            var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), environment);
            _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
            Assert.Equal(3, r.ExitCode);
            Assert.Contains("BLOCKED: a viewer that can log in is already running (1)", r.Output);
            Assert.False(Directory.Exists(s.Sandbox), "a blocked run starts nothing and writes no evidence folder");
        }
        finally { running.Kill(entireProcessTree: true); }
    }

    /// <summary>A run that fails early (viewer A never writes its automation token) still stops every viewer it started. The viewer is
    /// a stand-in that only waits, and the server a stand-in control script; the claim guard is off (its settings are cleared). The
    /// check for a viewer already running looks for a name nothing carries, so other sessions' viewers on the machine do not block it.</summary>
    [Fact]
    public void A_run_that_fails_while_a_viewer_starts_leaves_no_viewer_running()
    {
        var s = NewSetup();
        string viewer = Path.Combine(s.Root, "standin-viewer.cmd");
        File.WriteAllText(viewer, "@echo off\r\nping -n 40 127.0.0.1 >nul\r\n");
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Sandbox", s.Sandbox,
            "-Credentials", s.Accounts, "-Exe", viewer, "-GridInfoUrl", s.GridInfo, "-ServerControl", s.Control,
            "-Region", "Example Region", "-AccountA", "alpha1", "-AccountB", "beta1", "-StartSeconds", "3", "-ViewerProcessName", NoViewerName() };
        var environment = SettingNames.ToDictionary(n => n, _ => (string?)null);
        environment["NV_STOCK_SETTINGS"] = null;
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), environment);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        Assert.Contains("STEP 1 server: PASS", r.Output);
        Assert.Contains("viewer A has no token file", r.Output);
        Assert.Equal(1, r.ExitCode);
        // the stand-in's processes carry its folder in their command lines (godot.ps1's -Exe, cmd's script path)
        var left = ChildProcess.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
            $"@(Get-CimInstance Win32_Process | Where-Object {{ $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine.Contains('{viewer.Replace("'", "''")}') }} | ForEach-Object {{ \"$($_.ProcessId) $($_.Name)\" }}) -join ', '"],
            TimeSpan.FromMinutes(1));
        _out.WriteLine($"still running: {left.Output}");
        Assert.Equal("", left.Output.Trim());
    }
}
