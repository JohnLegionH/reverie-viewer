// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools\stock-server.ps1 starts, checks and stops an upstream OpenSim standalone that exists only while a test uses it. It does
/// not know how the server is run: a control script (start &lt;lane&gt;, stop, status) does that, and this script adds the
/// readiness check against the server's get_grid_info answer and the rule that a start which did not end ready is stopped again.
/// These tests give it a stand-in control script that records each call, so no server is started. Every test has its own temp
/// folder and child process, so they run in parallel.
/// </summary>
public sealed class StockServerScriptTests
{
    private readonly ITestOutputHelper _out;
    public StockServerScriptTests(ITestOutputHelper output) { _out = output; }

    private static string Script => Path.Combine(TestPaths.RepoRoot, "tools", "stock-server.ps1");

    private sealed record Setup(string Root, string Control, string CallLog, string GridInfoFile, bool HoldsOutput = false)
    {
        public string GridInfoUrl => new Uri(GridInfoFile).AbsoluteUri;
        public string Calls => File.Exists(CallLog) ? File.ReadAllText(CallLog) : "";
    }

    // The stand-in control script: every call is appended to a log, and its answers come from the two exit codes given.
    private static Setup NewSetup(int startExit = 0, int stopExit = 0, int statusExit = 0, bool gridInfoPresent = true, bool leaveBackgroundProcess = false)
    {
        string root = TestTemp.NewFolder("stock-server");
        string callLog = Path.Combine(root, "calls.log");
        string control = Path.Combine(root, "control.sh");
        string unixLog = callLog.Replace('\\', '/');
        File.WriteAllText(control,
            "#!/usr/bin/env bash\n" +
            $"echo \"$*\" >> \"{unixLog}\"\n" +
            "case \"$1\" in\n" +
            // a server left running in the background inherits the script's output, as the real one does
            (leaveBackgroundProcess ? "  start) echo \"[control] start\"; (sleep 12 &) ; exit 0 ;;\n" : $"  start) echo \"[control] start\"; exit {startExit} ;;\n") +
            $"  stop) echo \"[control] stop\"; exit {stopExit} ;;\n" +
            $"  status) echo \"[control] status\"; exit {statusExit} ;;\n" +
            "esac\nexit 64\n");
        string gridInfo = Path.Combine(root, "get_grid_info.xml");
        if (gridInfoPresent) File.WriteAllText(gridInfo, "<gridinfo><login>http://login.example.org:9400/</login></gridinfo>");
        return new Setup(root, control, callLog, gridInfo);
    }

    [Fact]
    public void The_control_script_and_grid_info_address_come_from_a_settings_file()
    {
        var s = NewSetup();
        string settings = Path.Combine(s.Root, "stock.env");
        File.WriteAllText(settings, $"NV_STOCK_SERVER_CONTROL={s.Control}\nNV_STOCK_GRID_INFO_URL={s.GridInfoUrl}\n");
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Action", "start", "-LogFolder", s.Root };
        var environment = new Dictionary<string, string?>
        {
            ["NV_STOCK_SERVER_CONTROL"] = null, ["NV_STOCK_GRID_INFO_URL"] = null, ["NV_STOCK_LANE"] = null, ["NV_STOCK_SETTINGS"] = settings,
        };
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), environment);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("READY", r.Output);
    }

    [Fact]
    public void With_no_settings_the_script_is_blocked_and_says_where_it_looked()
    {
        string root = TestTemp.NewFolder("stock-server-nosettings");
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Action", "start", "-LogFolder", root };
        var environment = new Dictionary<string, string?>
        {
            ["NV_STOCK_SERVER_CONTROL"] = null, ["NV_STOCK_GRID_INFO_URL"] = null, ["NV_STOCK_LANE"] = null,
            ["NV_STOCK_SETTINGS"] = Path.Combine(root, "no-such-settings.env"),
        };
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), environment);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("NV_STOCK_SERVER_CONTROL", r.Output);
        Assert.Contains("NV_STOCK_GRID_INFO_URL", r.Output);
        Assert.Contains("no-such-settings.env", r.Output);
    }

    private (int Exit, string Output) Run(Setup s, string action, Dictionary<string, string?>? env = null, params string[] extra)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Action", action, "-LogFolder", s.Root };
        args.AddRange(extra);
        var environment = new Dictionary<string, string?>
        {
            ["NV_STOCK_SERVER_CONTROL"] = s.Control, ["NV_STOCK_GRID_INFO_URL"] = s.GridInfoUrl, ["NV_STOCK_LANE"] = null,
            // a settings file named in the machine's own environment must not fill in what a test leaves out on purpose
            ["NV_STOCK_SETTINGS"] = Path.Combine(s.Root, "no-such-settings.env"),
        };
        if (env is not null) foreach (var (k, v) in env) environment[k] = v;
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), environment);
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        return r;
    }

    [Fact]
    public void A_dry_run_checks_the_inputs_and_calls_nothing()
    {
        var s = NewSetup();
        var r = Run(s, "start", null, "-DryRun");
        Assert.Equal(0, r.Exit);
        Assert.Contains("preconditions ok", r.Output);
        Assert.Equal("", s.Calls);
    }

    [Fact]
    public void A_server_left_running_in_the_background_does_not_hold_the_call_open()
    {
        // the real server keeps running after its start returns and inherits the start's output; capturing that output through a pipe
        // would wait until the server ended. The script reports how long the control call took; the test harness's own capture
        // of this child's output may still wait for the background process, so the figure the script prints is what counts.
        var s = NewSetup(leaveBackgroundProcess: true);
        var r = Run(s, "start");
        Assert.Equal(0, r.Exit);
        var line = r.Output.Split('\n').FirstOrDefault(l => l.Contains("control start exited 0 after "));
        Assert.NotNull(line);
        double seconds = double.Parse(line!.Split("after ")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(seconds < 9, $"the control call took {seconds} s while a background process was still running");
        Assert.Contains("[control] start", r.Output);
    }

    [Fact]
    public void A_control_script_that_does_not_exist_blocks_the_run()
    {
        var s = NewSetup();
        var r = Run(s, "start", new() { ["NV_STOCK_SERVER_CONTROL"] = Path.Combine(s.Root, "missing.sh") });
        Assert.Equal(3, r.Exit);
        Assert.Contains("BLOCKED", r.Output);
    }

    [Fact]
    public void A_start_needs_the_grid_info_address()
    {
        var s = NewSetup();
        var r = Run(s, "start", new() { ["NV_STOCK_GRID_INFO_URL"] = "" });
        Assert.Equal(3, r.Exit);
        Assert.Contains("BLOCKED", r.Output);
        Assert.Equal("", s.Calls);
    }

    [Fact]
    public void A_start_that_becomes_ready_succeeds_and_does_not_stop_the_server()
    {
        var s = NewSetup();
        var r = Run(s, "start", null, "-Lane", "example-lane");
        Assert.Equal(0, r.Exit);
        Assert.Contains("READY", r.Output);
        Assert.Contains("login.example.org:9400", r.Output);
        Assert.Contains("start example-lane", s.Calls);
        Assert.DoesNotContain("stop", s.Calls);
    }

    [Fact]
    public void A_start_the_control_script_refuses_is_not_followed_by_a_stop()
    {
        // exit 1 means nothing was started (the server is held by someone else, or already up): stopping would end their server
        var s = NewSetup(startExit: 1);
        var r = Run(s, "start");
        Assert.Equal(1, r.Exit);
        Assert.Contains("start", s.Calls);
        Assert.DoesNotContain("stop", s.Calls);
    }

    [Fact]
    public void A_start_that_never_becomes_ready_is_stopped_again()
    {
        var s = NewSetup(gridInfoPresent: false);
        var r = Run(s, "start", null, "-ReadySeconds", "2");
        Assert.Equal(2, r.Exit);
        Assert.Contains("NOT READY", r.Output);
        var calls = s.Calls.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, calls.Length);
        Assert.StartsWith("start", calls[0]);
        Assert.Equal("stop", calls[1]);
    }

    [Fact]
    public void A_start_that_fails_part_way_is_stopped_again()
    {
        // any other non-zero exit (the control script's "not ready in time") may have left a process: it is stopped
        var s = NewSetup(startExit: 2);
        var r = Run(s, "start");
        Assert.Equal(2, r.Exit);
        Assert.Contains("stop", s.Calls);
    }

    [Fact]
    public void A_stop_calls_the_control_script_and_reports_it_gone()
    {
        var s = NewSetup();
        var r = Run(s, "stop");
        Assert.Equal(0, r.Exit);
        Assert.Equal("stop", s.Calls.Trim());
    }

    [Fact]
    public void A_stop_that_fails_exits_with_one_so_the_caller_reports_it()
    {
        var s = NewSetup(stopExit: 2);
        var r = Run(s, "stop");
        Assert.Equal(1, r.Exit);
        Assert.Contains("STOP FAILED", r.Output);
    }

    [Theory]
    [InlineData(0, 0, "UP")]
    [InlineData(1, 1, "DOWN")]
    public void Status_is_the_control_scripts_answer(int controlExit, int expectedExit, string word)
    {
        var s = NewSetup(statusExit: controlExit);
        var r = Run(s, "status");
        Assert.Equal(expectedExit, r.Exit);
        Assert.Contains(word, r.Output);
    }
}
