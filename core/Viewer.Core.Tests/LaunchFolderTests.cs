// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A test or measurement run of an exported build never writes into the build's folder: it starts from a launch folder
/// under the diagnostics folder (hard links to the build's files, plus the run's own override.cfg), and a sandbox inside
/// the build folder moves to the diagnostics folder. A run stopped from outside therefore cannot leave a test setting
/// behind for the next normal start of the build. The scripts are a Windows facility, so the tests return at once elsewhere.
/// </summary>
public class LaunchFolderTests : IDisposable
{
    private readonly string _dir = TestTemp.NewFolder("launch-folder");
    private readonly List<int> _pids = new();

    public void Dispose()
    {
        foreach (int pid in _pids)
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch (ArgumentException) { } catch (InvalidOperationException) { }
        try { TestTemp.Delete(_dir); } catch (IOException) { }
    }

    private static string WindowsPowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string CmdExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    // The scripts under test; NV_TOOLS_DIR points the tests at another copy of tools\ (to run them against older scripts).
    private static string Tool(string name) => Path.Combine(Environment.GetEnvironmentVariable("NV_TOOLS_DIR") is { Length: > 0 } d ? d : Path.Combine(TestPaths.RepoRoot, "tools"), name);

    /// <summary>A made-up exported build: the stand-in executable (a copy of cmd.exe, so a run ends by itself), a pck, a data folder, a sandbox and a development export.</summary>
    private string MakeBuild(string name = "build", bool dataFolder = true, bool hostAssembly = true)
    {
        string build = Path.Combine(_dir, name);
        Directory.CreateDirectory(build);
        if (dataFolder)
            Directory.CreateDirectory(Path.Combine(build, "data_Viewer.Godot_windows_x86_64"));
        Directory.CreateDirectory(Path.Combine(build, "appdata"));
        Directory.CreateDirectory(Path.Combine(build, "dev"));
        File.Copy(CmdExe, Path.Combine(build, "Reverie.console.exe"));
        File.WriteAllBytes(Path.Combine(build, "Reverie.exe"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(build, "Reverie.pck"), new byte[] { 5, 6, 7 });
        if (dataFolder && hostAssembly)
            File.WriteAllText(Path.Combine(build, "data_Viewer.Godot_windows_x86_64", "Viewer.Godot.dll"), "dll");
        File.WriteAllText(Path.Combine(build, "dev", "Reverie.exe"), "dev");
        return build;
    }

    private static string Snapshot(string folder) =>
        string.Join("\n", Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(folder, p) + "|" + (File.Exists(p) ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir")));

    private string RunFunctions(string body)
    {
        string script = Path.Combine(_dir, "calls-" + Guid.NewGuid().ToString("N")[..6] + ".ps1");
        File.WriteAllText(script, ". '" + Tool("test-launch.ps1") + "'\n" + body + "\n");
        var psi = new ProcessStartInfo(WindowsPowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(60000), "the script did not end");
        Assert.True(p.ExitCode == 0, "script failed: " + err.Result + output);
        return output.Trim();
    }

    [Fact]
    public void A_launch_folder_links_the_builds_files_and_holds_the_runs_own_override()
    {
        if (!OperatingSystem.IsWindows()) return;
        string build = MakeBuild(), diag = Path.Combine(_dir, "diag");
        Directory.CreateDirectory(diag);
        string launch = RunFunctions($"New-TestLaunchFolder '{build}' '{diag}'");
        Assert.StartsWith(diag, launch);
        foreach (var f in new[] { "Reverie.console.exe", "Reverie.exe", "Reverie.pck" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(build, f)), File.ReadAllBytes(Path.Combine(launch, f)));
        Assert.Equal("dll", File.ReadAllText(Path.Combine(launch, "data_Viewer.Godot_windows_x86_64", "Viewer.Godot.dll")));
        string overrides = File.ReadAllText(Path.Combine(launch, "override.cfg"));
        Assert.Contains("window/size/no_focus=true", overrides);
        // the shipping export keeps the engine's shader and pipeline caches on; a test run keeps them off
        foreach (var setting in new[] { "shader_compiler/shader_cache/enabled", "rendering_device/pipeline_cache/enable" })
        {
            Assert.Contains(setting + "=false", overrides);
            Assert.Contains(setting + ".shipping=false", overrides);
        }
        Assert.False(Directory.Exists(Path.Combine(launch, "appdata")), "the sandbox is not part of a launch folder");
        Assert.False(Directory.Exists(Path.Combine(launch, "dev")), "the development export is not part of a launch folder");
        // a link, not a copy: the same file seen from both folders
        File.AppendAllText(Path.Combine(build, "Reverie.pck"), "x");
        Assert.Equal(4, new FileInfo(Path.Combine(launch, "Reverie.pck")).Length);
    }

    // An off-screen test window opens minimised (the engine keeps a new window partly on a monitor whatever position it is
    // given); a quiet normal start (S-1's shipping-start checks) opens minimised with nothing else changed; the corner window
    // and the default keep the settings they had. Each kind has its own folder.
    [Theory]
    [InlineData("offscreen", true, true, true)]
    [InlineData("quiet", true, false, false)]
    [InlineData("test", false, true, true)]
    public void Each_kind_of_launch_folder_has_its_own_settings(string kind, bool minimised, bool noFocus, bool cachesOff)
    {
        if (!OperatingSystem.IsWindows()) return;
        string build = MakeBuild(), diag = Path.Combine(_dir, "diag");
        Directory.CreateDirectory(diag);
        string launch = RunFunctions($"New-TestLaunchFolder '{build}' '{diag}' '{kind}'");
        string overrides = File.ReadAllText(Path.Combine(launch, "override.cfg"));
        Assert.Equal(minimised, overrides.Contains("window/size/mode=1"));
        Assert.Equal(noFocus, overrides.Contains("window/size/no_focus=true"));
        Assert.Equal(cachesOff, overrides.Contains("rendering_device/pipeline_cache/enable=false"));
        Assert.Equal(launch, RunFunctions($"New-TestLaunchFolder '{build}' '{diag}' '{kind}'"));
    }

    [Fact]
    public void The_kinds_of_launch_folder_never_share_a_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        string build = MakeBuild(), diag = Path.Combine(_dir, "diag");
        Directory.CreateDirectory(diag);
        var folders = new[] { "offscreen", "quiet", "test" }.Select(k => RunFunctions($"New-TestLaunchFolder '{build}' '{diag}' '{k}'")).ToList();
        Assert.Equal(3, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(folders[2], RunFunctions($"New-TestLaunchFolder '{build}' '{diag}'"));   // the default is the test kind
    }

    [Fact]
    public void A_launch_folder_holds_no_junction_or_symbolic_link_and_its_data_files_are_hard_links()
    {
        if (!OperatingSystem.IsWindows()) return;
        string build = MakeBuild(), diag = Path.Combine(_dir, "diag");
        string nested = Path.Combine(build, "data_Viewer.Godot_windows_x86_64", "runtimes", "win-x64");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "native.dll"), "native");
        Directory.CreateDirectory(diag);
        string launch = RunFunctions($"New-TestLaunchFolder '{build}' '{diag}'");
        var links = Directory.EnumerateFileSystemEntries(launch, "*", SearchOption.AllDirectories)
            .Where(p => new FileInfo(p).Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList();
        Assert.Empty(links);
        Assert.Equal("native", File.ReadAllText(Path.Combine(launch, "data_Viewer.Godot_windows_x86_64", "runtimes", "win-x64", "native.dll")));
        // a hard link: the same file seen from both folders
        File.AppendAllText(Path.Combine(nested, "native.dll"), "+");
        Assert.Equal("native+", File.ReadAllText(Path.Combine(launch, "data_Viewer.Godot_windows_x86_64", "runtimes", "win-x64", "native.dll")));
    }

    [Fact]
    public void Making_a_launch_folder_does_not_write_the_build_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        string build = MakeBuild(), diag = Path.Combine(_dir, "diag");
        Directory.CreateDirectory(diag);
        string before = Snapshot(build);
        Assert.True(Directory.Exists(RunFunctions($"New-TestLaunchFolder '{build}' '{diag}'")));
        Assert.Equal(before, Snapshot(build));
        Assert.False(File.Exists(Path.Combine(build, "override.cfg")));
    }

    [Fact]
    public void The_same_build_gets_the_same_folder_and_a_changed_build_gets_a_new_one()
    {
        if (!OperatingSystem.IsWindows()) return;
        string build = MakeBuild(), diag = Path.Combine(_dir, "diag");
        Directory.CreateDirectory(diag);
        string first = RunFunctions($"New-TestLaunchFolder '{build}' '{diag}'");
        string again = RunFunctions($"New-TestLaunchFolder '{build}' '{diag}'");
        Assert.Equal(first, again);
        File.WriteAllBytes(Path.Combine(build, "Reverie.exe"), new byte[] { 9, 9, 9, 9, 9 });   // a new export
        string changed = RunFunctions($"New-TestLaunchFolder '{build}' '{diag}'");
        Assert.NotEqual(first, changed);
        Assert.Equal(5, new FileInfo(Path.Combine(changed, "Reverie.exe")).Length);
    }

    [Fact]
    public void A_stale_override_in_the_build_folder_is_not_linked_into_the_launch_folder_nor_touched()
    {
        if (!OperatingSystem.IsWindows()) return;
        string build = MakeBuild(), diag = Path.Combine(_dir, "diag");
        Directory.CreateDirectory(diag);
        string stale = Path.Combine(build, "override.cfg");
        File.WriteAllText(stale, "[display]\r\n\r\nwindow/size/no_focus=true\r\n");
        var written = File.GetLastWriteTimeUtc(stale);
        string launch = RunFunctions($"New-TestLaunchFolder '{build}' '{diag}'");
        Assert.True(File.Exists(stale));
        Assert.Equal(written, File.GetLastWriteTimeUtc(stale));
        File.AppendAllText(stale, "; changed after\r\n");
        Assert.DoesNotContain("changed after", File.ReadAllText(Path.Combine(launch, "override.cfg")));   // its own file, not a link to the stale one
    }

    [Fact]
    public void A_sandbox_inside_the_build_folder_moves_to_the_diagnostics_folder_one_per_build()
    {
        if (!OperatingSystem.IsWindows()) return;
        string a = MakeBuild("build-a"), b = MakeBuild("build-b"), diag = Path.Combine(_dir, "diag");
        string inA = RunFunctions($"Get-RunAppDataDir '{Path.Combine(a, "appdata")}' '{a}' '{diag}'");
        string inB = RunFunctions($"Get-RunAppDataDir '{Path.Combine(b, "appdata")}' '{b}' '{diag}'");
        string inAAgain = RunFunctions($"Get-RunAppDataDir '{Path.Combine(a, "appdata")}' '{a}' '{diag}'");
        Assert.StartsWith(Path.Combine(diag, "appdata"), inA);
        Assert.Equal(inA, inAAgain);
        Assert.NotEqual(inA, inB);
        string outside = Path.Combine(_dir, "elsewhere");
        Assert.Equal(outside, RunFunctions($"Get-RunAppDataDir '{outside}' '{a}' '{diag}'"));
    }

    private (int Exit, string Output) RunGodotScript(string build, string argLine, int timeoutSeconds = 60)
    {
        var psi = new ProcessStartInfo(WindowsPowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Tool("godot.ps1"), "-Exe", Path.Combine(build, "Reverie.console.exe"),
                     "-AppDataDir", Path.Combine(build, "appdata"), "-DiagDir", Path.Combine(_dir, "diag"), "-TimeoutSeconds", timeoutSeconds.ToString(), "-ArgLine", argLine })
            psi.ArgumentList.Add(a);
        psi.Environment["NV_CRASH_DUMPS"] = "0";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds + 60)), "godot.ps1 did not end");
        return (p.ExitCode, output + err.Result);
    }

    [Fact]
    public void An_export_is_complete_only_with_its_data_folder_and_the_host_assembly_in_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("True", RunFunctions($"Test-ExportComplete '{MakeBuild("whole")}'"));
        Assert.Equal("False", RunFunctions($"Test-ExportComplete '{MakeBuild("no-data", dataFolder: false)}'"));
        Assert.Equal("False", RunFunctions($"Test-ExportComplete '{MakeBuild("no-dll", hostAssembly: false)}'"));
    }

    /// <summary>Every program godot.ps1 starts needs its data_ folder, whether or not a .pck lies beside it (an export can carry its pack
    /// inside the program), except the Godot editor itself (its GodotSharp folder beside it), a program of Windows, and a script.</summary>
    [Fact]
    public void Every_program_but_the_editor_a_Windows_program_or_a_script_needs_its_data_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        string embedded = Path.Combine(_dir, "embedded");
        Directory.CreateDirectory(embedded);
        File.WriteAllBytes(Path.Combine(embedded, "Example.exe"), new byte[] { 1, 2, 3, 4 });
        string editor = Path.Combine(_dir, "editor");
        Directory.CreateDirectory(Path.Combine(editor, "GodotSharp"));
        File.WriteAllBytes(Path.Combine(editor, "Editor_console.exe"), new byte[] { 1, 2, 3, 4 });
        string script = Path.Combine(_dir, "standin.cmd");
        File.WriteAllText(script, "@exit 0\r\n");
        Assert.Equal("True", RunFunctions($"Test-NeedsDataFolder '{Path.Combine(embedded, "Example.exe")}'"));
        Assert.Equal("True", RunFunctions($"Test-NeedsDataFolder '{Path.Combine(MakeBuild("with-pck"), "Reverie.console.exe")}'"));
        Assert.Equal("False", RunFunctions($"Test-NeedsDataFolder '{Path.Combine(editor, "Editor_console.exe")}'"));
        Assert.Equal("False", RunFunctions($"Test-NeedsDataFolder '{WindowsPowerShell}'"));
        Assert.Equal("False", RunFunctions($"Test-NeedsDataFolder '{script}'"));
    }

    [Fact]
    public void A_run_of_an_export_that_carries_its_pack_inside_and_has_no_data_folder_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        // no .pck beside the program (the pack is inside it) and no data_ folder: the .NET host would show its dialog
        string build = Path.Combine(_dir, "embedded-run");
        Directory.CreateDirectory(build);
        File.Copy(CmdExe, Path.Combine(build, "Reverie.console.exe"));
        var (exit, output) = RunGodotScript(build, "/c exit 3 --test-render");
        Assert.Equal(127, exit);   // the stand-in would have ended with 3 had it started
        Assert.Contains("incomplete export", output);
    }

    [Fact]
    public void A_run_of_an_export_without_its_data_folder_is_refused_and_nothing_starts()
    {
        if (!OperatingSystem.IsWindows()) return;
        string build = MakeBuild("no-data", dataFolder: false);
        var (exit, output) = RunGodotScript(build, "/c exit 3 --test-render");
        Assert.Equal(127, exit);   // the stand-in would have ended with 3 had it started
        Assert.Contains("incomplete export", output);
        Assert.False(Directory.Exists(Path.Combine(_dir, "diag")) && Directory.GetDirectories(Path.Combine(_dir, "diag"), "launch-*").Length > 0, "no launch folder is made for an incomplete export");
    }

    [Fact]
    public void A_test_window_run_starts_from_the_launch_folder_and_leaves_the_build_folder_as_it_was()
    {
        if (!OperatingSystem.IsWindows()) return;
        string build = MakeBuild();
        string before = Snapshot(build);
        var (exit, output) = RunGodotScript(build, "/c exit 3 --test-render");
        Assert.Equal(3, exit);
        Assert.Contains("test launch folder", output);
        Assert.Equal(before, Snapshot(build));
        Assert.False(File.Exists(Path.Combine(build, "override.cfg")));
        Assert.Single(Directory.GetDirectories(Path.Combine(_dir, "diag"), "launch-*"));
        Assert.True(Directory.Exists(Path.Combine(_dir, "diag", "appdata")), "the run's sandbox is under the diagnostics folder");
    }

    [Fact]
    public void A_run_stopped_from_outside_leaves_no_test_setting_in_the_build_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        string build = MakeBuild();
        string before = Snapshot(build);
        var psi = new ProcessStartInfo(WindowsPowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Tool("godot.ps1"), "-Exe", Path.Combine(build, "Reverie.console.exe"),
                     "-AppDataDir", Path.Combine(build, "appdata"), "-DiagDir", Path.Combine(_dir, "diag"), "-TimeoutSeconds", "120", "-ArgLine", "/c ping -n 100 127.0.0.1 --test-render" })
            psi.ArgumentList.Add(a);
        psi.Environment["NV_CRASH_DUMPS"] = "0";
        using var script = Process.Start(psi)!;
        _pids.Add(script.Id);
        _ = script.StandardOutput.ReadToEndAsync();
        _ = script.StandardError.ReadToEndAsync();
        Directory.CreateDirectory(Path.Combine(_dir, "diag"));
        // wait until the run has set itself up (a launch folder, or a test setting written into the build)
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed.TotalSeconds < 60 && Directory.GetDirectories(Path.Combine(_dir, "diag"), "launch-*").Length == 0 && !File.Exists(Path.Combine(build, "override.cfg")))
            Thread.Sleep(200);
        Thread.Sleep(2000);   // the stand-in is up
        script.Kill(entireProcessTree: true);   // stopped from outside: nothing in the script runs again
        script.WaitForExit(30000);
        Assert.Equal(before, Snapshot(build));
        Assert.False(File.Exists(Path.Combine(build, "override.cfg")), "a test setting was left in the build folder");
    }
}
