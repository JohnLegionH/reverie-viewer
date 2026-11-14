// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// The close-out fresh clone reads the working repo's local-only fixtures through NV_FIXTURES_LOCAL, read-only.
/// The .NET tests (TestPaths / LiveCaptures), render.ps1 and contact-sheets.ps1 all honour it; unset means the repo's
/// own fixtures-local\ exactly as before; with it set, render.ps1 refuses -Propose (the only switch that writes there).
/// The scripts run as child processes with the variable set or removed for the child only.
/// </summary>
public class FixturesLocalOverrideTests
{
    private readonly ITestOutputHelper _out;
    public FixturesLocalOverrideTests(ITestOutputHelper output) { _out = output; }

    private static string Own => Path.Combine(TestPaths.RepoRoot, "fixtures-local");

    [Fact]
    public void Unset_or_empty_gives_the_repos_own_folder()
    {
        Assert.Equal(Own, TestPaths.ResolveFixturesLocal(null, TestPaths.RepoRoot));
        Assert.Equal(Own, TestPaths.ResolveFixturesLocal("", TestPaths.RepoRoot));
        Assert.Equal(Own, TestPaths.ResolveFixturesLocal("   ", TestPaths.RepoRoot));
    }

    [Fact]
    public void An_override_is_honoured()
    {
        string dir = TestTemp.NewFolder("fixtures-override");
        Assert.Equal(Path.GetFullPath(dir), TestPaths.ResolveFixturesLocal(dir, TestPaths.RepoRoot));
        TestTemp.Delete(dir);
    }

    [Fact]
    public void A_wrong_override_fails_instead_of_skipping_every_local_test()
    {
        Assert.Throws<InvalidOperationException>(() => TestPaths.ResolveFixturesLocal("fixtures-local", TestPaths.RepoRoot));
        string missing = TestTemp.NewFolderPath("fixtures-missing");   // never created
        Assert.Throws<InvalidOperationException>(() => TestPaths.ResolveFixturesLocal(missing, TestPaths.RepoRoot));
    }

    [Fact]
    public void Live_captures_resolve_through_the_variable_of_this_run()
    {
        string expected = TestPaths.ResolveFixturesLocal(Environment.GetEnvironmentVariable(TestPaths.FixturesLocalVariable), TestPaths.RepoRoot);
        Assert.Equal(expected, TestPaths.FixturesLocal);
        Assert.Equal(Path.Combine(expected, "capture-region-b.jsonl.gz"), LiveCaptures.PathOf("capture-region-b.jsonl.gz"));
    }

    private (int Exit, string Output) Script(string script, string? variable, params string[] extra)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(TestPaths.RepoRoot, "tools", script) };
        args.AddRange(extra);
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(1), new Dictionary<string, string?> { [TestPaths.FixturesLocalVariable] = variable });
        _out.WriteLine($"{script} {string.Join(' ', extra)} ({TestPaths.FixturesLocalVariable}={variable ?? "<unset>"}): exit {r.ExitCode}\n{r.Output}");
        return r;
    }

    [Theory]
    [InlineData("render.ps1")]
    [InlineData("contact-sheets.ps1")]
    public void Scripts_honour_the_override_and_default_to_the_repos_folder(string script)
    {
        string dir = TestTemp.NewFolder("fixtures-override");
        var set = Script(script, dir, "-ShowPaths");
        Assert.Equal(0, set.Exit);
        Assert.Contains("fixtures-local: " + Path.GetFullPath(dir), set.Output);
        var unset = Script(script, null, "-ShowPaths");
        Assert.Equal(0, unset.Exit);
        Assert.Contains("fixtures-local: " + Own, unset.Output);
        TestTemp.Delete(dir);
    }

    [Fact]
    public void Render_refuses_Propose_while_the_override_is_set()
    {
        string dir = TestTemp.NewFolder("fixtures-override");
        var refused = Script("render.ps1", dir, "-Propose", "-ShowPaths");
        Assert.Equal(2, refused.Exit);
        Assert.Contains("REFUSED: -Propose", refused.Output);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));   // nothing written into the read-only folder
        // negative control: the same arguments without the variable are not refused (they stop at -ShowPaths)
        var allowed = Script("render.ps1", null, "-Propose", "-ShowPaths");
        Assert.Equal(0, allowed.Exit);
        Assert.DoesNotContain("REFUSED", allowed.Output);
        TestTemp.Delete(dir);
    }
}
