// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools/package.ps1 refuses to pack an export that holds the saved-login file or any file with a login hash in it, before anything is
/// packed, and says why; a clean export is not refused for that reason. Scratch exports in unique temp folders; nothing is exported.
/// </summary>
public class PackageSavedLoginTests(ITestOutputHelper output)
{
    private static string Script => Path.Combine(TestPaths.RepoRoot, "tools", "package.ps1");

    private (int Exit, string Output) Pack(Action<string> plant)
    {
        string exports = TestTemp.NewFolder("pack-exports");
        string outDir = TestTemp.NewFolderPath("pack-out");
        try
        {
            string export = Path.Combine(exports, "windows-x86_64");
            Directory.CreateDirectory(Path.Combine(export, "data_Viewer.Godot_windows_x86_64"));
            File.WriteAllText(Path.Combine(export, "Reverie.exe"), "not a real program");
            plant(export);
            var r = ChildProcess.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script,
                "-Exports", exports, "-OutDir", outDir, "-Platforms", "windows-x86_64", "-NoExport"], TimeSpan.FromMinutes(2),
                new Dictionary<string, string?> { ["NV_CHARACTER_DIR"] = null });
            output.WriteLine($"exit {r.ExitCode}\n{r.Output}");
            Assert.False(Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any(), "something was packed");
            return r;
        }
        finally
        {
            TestTemp.Delete(exports);
            TestTemp.Delete(outDir);
        }
    }

    [Fact]
    public void The_saved_login_file_in_an_export_stops_the_package()
    {
        var r = Pack(export =>
        {
            string appdata = Path.Combine(export, "appdata", "ReverieViewer");
            Directory.CreateDirectory(appdata);
            File.WriteAllBytes(Path.Combine(appdata, Viewer.Shell.DpapiLoginVault.FileName), [1, 2, 3, 4]);
        });
        Assert.Equal(1, r.Exit);
        Assert.Contains("saved login", r.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Viewer.Shell.DpapiLoginVault.FileName, r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_login_hash_in_any_file_of_an_export_stops_the_package()
    {
        var r = Pack(export => File.WriteAllText(Path.Combine(export, "data_Viewer.Godot_windows_x86_64", "notes.cfg"),
            "user=Test User\npass=$1$" + "0123456789abcdef0123456789abcdef\n"));
        Assert.Equal(1, r.Exit);
        Assert.Contains("saved login", r.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("notes.cfg", r.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", r.Output, StringComparison.Ordinal);   // the hash itself is never printed
    }

    [Fact]
    public void A_clean_export_is_not_refused_for_saved_login_data()
    {
        var r = Pack(_ => { });
        Assert.DoesNotContain("saved login", r.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("avatar  windows-x86_64", r.Output, StringComparison.Ordinal);   // it went on to the next steps (the avatar data folder,
        Assert.Contains("joltc guard failed", r.Output, StringComparison.Ordinal);       // then the physics library, which the stand-in export lacks)
    }
}
