// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools/package.ps1 works on a fresh clone with no steps beforehand: it makes its own exports folder, and it restores the solution
/// (NuGet locked mode) before the import and the exports, which build the add-on projects and fail on a clone that was never restored.
/// Scratch folders in unique temp folders; no Godot is started (the editor path given does not exist).
/// </summary>
public class PackageFreshCloneTests(ITestOutputHelper output)
{
    private static string Script => Path.Combine(TestPaths.RepoRoot, "tools", "package.ps1");

    [Fact]
    public void A_missing_exports_folder_is_made_by_the_script()
    {
        string root = TestTemp.NewFolder("pack-fresh");
        try
        {
            string exports = Path.Combine(root, "exports");
            string outDir = Path.Combine(root, "out");
            string noGodot = Path.Combine(root, "no-godot.exe");
            var r = ChildProcess.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script,
                "-Exports", exports, "-OutDir", outDir, "-Platforms", "windows-x86_64", "-GodotExe", noGodot], TimeSpan.FromMinutes(2),
                new Dictionary<string, string?> { ["NV_CLAIM_CHECK_SCRIPT"] = null });
            output.WriteLine($"exit {r.ExitCode}\n{r.Output}");
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("no Godot editor", r.Output, StringComparison.Ordinal);   // it stopped where it should, with nothing exported
            Assert.True(Directory.Exists(exports), "the exports folder was not made");
            Assert.Empty(Directory.EnumerateFileSystemEntries(exports));
        }
        finally
        {
            TestTemp.Delete(root);
        }
    }

    [Fact]
    public void The_solution_is_restored_in_locked_mode_before_the_import()
    {
        var script = File.ReadAllText(Script);
        int restore = script.IndexOf("& dotnet restore (Join-Path $repo \"Viewer.sln\") --locked-mode", StringComparison.Ordinal);
        int import = script.IndexOf("--headless --path `\"$project`\" --import", StringComparison.Ordinal);
        Assert.True(restore >= 0, "package.ps1 does not restore Viewer.sln in locked mode");
        Assert.True(import >= 0, "the import call was not found");
        Assert.True(restore < import, "the restore comes after the import");
        Assert.Contains("package: restore failed", script, StringComparison.Ordinal);   // a failed restore stops the run
    }
}
