// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The scripts that start the viewer or the engine can refuse to start without a granted claim (tools/claim-guard.ps1). The guard is off
/// unless NV_CLAIM_CHECK_SCRIPT names a bash script that verifies a claim token (called as "check token kind...") and NV_CLAIM_KINDS names
/// the kinds that allow a viewer. godot.ps1 is the one place a process is started, so its tests run it for real, pointed at an executable
/// that does not exist: past the claim check it ends with 127 (executable not found), and nothing is started either way. The other launchers
/// are covered by a scan of their text, because running one of them without the check would run the whole suite, a render or an export.
/// Each test writes its own claim list and check script into its own folder and passes both variables explicitly to the child process, so the
/// tests run in parallel, touch no real claim, and give the same result whatever the test runner's own environment holds.
/// </summary>
public class LauncherClaimTests : IDisposable
{
    private const int Refused = 125, ExecutableNotFound = 127;
    private const string AllowedKinds = "alpha,beta";
    private readonly string _dir = TestTemp.NewFolder("launcher-claim");

    public void Dispose() { try { TestTemp.Delete(_dir); } catch (IOException) { } }

    // A stand-in for a real claim checker. It honours the contract in claim-guard.ps1: "check <token> <kind>..." ends with 0 when the token is
    // a granted claim of one of the kinds. The claims are the lines "token|state|kind" of claims.txt next to it, state being granted, pending,
    // released or expired.
    private const string CheckScript = """
        #!/bin/bash
        list="$(dirname "$0")/claims.txt"
        [ "$1" = check ] || { echo "usage: check <token> <kind>..."; exit 2; }
        tok="$2"; shift 2
        line=$(grep -F "$tok|" "$list" | tail -n 1)
        [ -n "$line" ] || { echo "token $tok is not known"; exit 1; }
        IFS='|' read -r t state kind <<< "$line"
        case "$state" in
            pending) echo "token $tok is pending"; exit 1 ;;
            released) echo "token $tok is released"; exit 1 ;;
            expired) echo "token $tok is expired"; exit 1 ;;
        esac
        for k in "$@"; do [ "$k" = "$kind" ] && exit 0; done
        echo "claim kind $kind is not one of: $*"
        exit 1
        """;

    /// <summary>Writes the claim list and the check script, and returns the script's path.</summary>
    private string Claims(params string[] lines)
    {
        File.WriteAllText(Path.Combine(_dir, "claims.txt"), string.Concat(lines.Select(l => l + "\n")));
        string script = Path.Combine(_dir, "check-claim.sh");
        File.WriteAllText(script, CheckScript.Replace("\r\n", "\n") + "\n");
        return script;
    }

    private (int Exit, string Output) RunGodot(string? token, string? checkScript, string? kinds = AllowedKinds)
    {
        string script = Path.Combine(TestPaths.RepoRoot, "tools", "godot.ps1");
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-Exe", Path.Combine(_dir, "no-such-viewer.exe"), "-DiagDir", Path.Combine(_dir, "diag"), "-AppDataDir", Path.Combine(_dir, "appdata"), "-ArgLine", "--headless" })
            psi.ArgumentList.Add(a);
        foreach (string name in new[] { "CLAIM_TOKEN", "NV_CLAIM_CHECK_SCRIPT", "NV_CLAIM_KINDS" }) psi.Environment.Remove(name);
        if (token is not null) psi.Environment["CLAIM_TOKEN"] = token;
        if (checkScript is not null) psi.Environment["NV_CLAIM_CHECK_SCRIPT"] = checkScript;
        if (kinds is not null) psi.Environment["NV_CLAIM_KINDS"] = kinds;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string text = p.StandardOutput.ReadToEnd() + err.Result;
        if (!p.WaitForExit(120_000)) { p.Kill(true); throw new TimeoutException("godot.ps1 did not end: " + text); }
        return (p.ExitCode, text);
    }

    [Fact]
    public void No_claim_at_all_is_refused()
    {
        var r = RunGodot(null, Claims());
        Assert.Equal(Refused, r.Exit);
        Assert.Contains("claim", r.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_token_the_checker_does_not_know_is_refused()
    {
        Assert.Equal(Refused, RunGodot("tok-unknown", Claims("other|granted|alpha")).Exit);
    }

    [Fact]
    public void A_pending_claim_is_refused()
    {
        // a waiter's own token: it is waiting its turn and has not been granted anything
        var r = RunGodot("tok-pending", Claims("tok-pending|pending|alpha"));
        Assert.Equal(Refused, r.Exit);
        Assert.Contains("pending", r.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_released_claim_is_refused()
    {
        Assert.Equal(Refused, RunGodot("tok-rel", Claims("tok-rel|released|alpha")).Exit);
    }

    [Fact]
    public void An_expired_claim_is_refused()
    {
        Assert.Equal(Refused, RunGodot("tok-old", Claims("tok-old|expired|alpha")).Exit);
    }

    [Theory]
    [InlineData("gamma")]
    [InlineData("delta")]
    public void A_claim_of_a_kind_that_does_not_allow_a_viewer_is_refused(string kind)
    {
        var r = RunGodot("tok-kind", Claims($"tok-kind|granted|{kind}"));
        Assert.Equal(Refused, r.Exit);
        Assert.Contains(kind, r.Output);
    }

    [Theory]
    [InlineData("alpha", "alpha,beta")]
    [InlineData("beta", "alpha,beta")]
    [InlineData("beta", "alpha beta")]
    public void A_granted_claim_of_an_allowing_kind_lets_the_launcher_go_on(string kind, string allowed)
    {
        // past the check the launcher looks for the executable, which does not exist: 127, and no process was started
        var r = RunGodot("tok-ok", Claims($"tok-ok|granted|{kind}"), allowed);
        Assert.Equal(ExecutableNotFound, r.Exit);
        Assert.Contains("executable not found", r.Output);
    }

    [Fact]
    public void A_check_script_that_is_not_there_is_refused()
    {
        var r = RunGodot("tok-ok", Path.Combine(_dir, "missing", "check-claim.sh"));
        Assert.Equal(Refused, r.Exit);
        Assert.Contains("not found", r.Output);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_check_script_with_no_kinds_named_is_refused(string? kinds)
    {
        var r = RunGodot("tok-ok", Claims("tok-ok|granted|alpha"), kinds);
        Assert.Equal(Refused, r.Exit);
        Assert.Contains("NV_CLAIM_KINDS", r.Output);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_check_script_the_launcher_starts_as_if_there_were_no_guard(string? checkScript)
    {
        // no token and no script: nothing is asked of the caller, and the launcher goes on to look for the executable
        var r = RunGodot(null, checkScript, kinds: null);
        Assert.Equal(ExecutableNotFound, r.Exit);
        Assert.Contains("executable not found", r.Output);
        Assert.DoesNotContain("CLAIM", r.Output);
    }

    /// <summary>The scripts that start the viewer or the engine, or run a script that does.</summary>
    private static IEnumerable<string> LauncherScripts()
    {
        string tools = Path.Combine(TestPaths.RepoRoot, "tools");
        foreach (string file in Directory.EnumerateFiles(tools, "*.ps1", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            // job-guard.ps1, claim-guard.ps1 and test-launch.ps1 are libraries that the launchers load; they start nothing themselves
            if (name is "godot.ps1" or "job-guard.ps1" or "claim-guard.ps1" or "test-launch.ps1") { if (name == "godot.ps1") yield return name; continue; }
            if (Regex.IsMatch(File.ReadAllText(file), @"godot\.ps1|\$godot\b", RegexOptions.IgnoreCase)) yield return name;
        }
    }

    [Fact]
    public void The_scan_finds_the_launchers()
    {
        var found = LauncherScripts().ToList();
        foreach (var expected in new[] { "godot.ps1", "render.ps1", "s1.ps1", "test.ps1", "close-cycles.ps1", "live-check.ps1", "automation.ps1", "contact-sheets.ps1", "login-proof.ps1" })
            Assert.Contains(expected, found);
    }

    [Fact]
    public void Every_script_that_starts_a_viewer_checks_the_claim_before_it_does_anything_else()
    {
        var missing = new List<string>();
        foreach (string name in LauncherScripts())
        {
            string text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tools", name));
            int call = text.IndexOf("Assert-ClaimGranted", StringComparison.Ordinal);
            if (call < 0) { missing.Add(name + ": no claim check"); continue; }
            // the check comes before the first process start or the first call of another script
            var early = Regex.Match(text, @"Process\]::Start|Start-Process|&\s+powershell|&\s+\$godot|Set-Location|dotnet\s+test");
            if (early.Success && early.Index < call) missing.Add(name + ": the claim check comes after '" + early.Value + "'");
        }
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    public void No_launcher_has_a_switch_of_its_own_that_skips_the_check()
    {
        // the check is configured by NV_CLAIM_CHECK_SCRIPT alone: no switch, parameter or other variable of any launcher skips it
        foreach (string name in LauncherScripts().Append("claim-guard.ps1"))
        {
            string text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tools", name));
            Assert.DoesNotMatch(@"(?i)(-|NV_)(skip|bypass|ignore|override|no)\w*claim", text);
        }
        string guard = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tools", "claim-guard.ps1"));
        Assert.Contains("NV_CLAIM_CHECK_SCRIPT", guard);
        Assert.Contains("CLAIM_TOKEN", guard);
    }
}
