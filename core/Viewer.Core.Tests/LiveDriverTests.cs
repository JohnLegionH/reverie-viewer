// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// tools\live-driver.ps1, the shared driver of the live checks, starts a viewer only from a complete export: the program with its data_
/// folder (the .NET assemblies) beside it. An exported .NET build started without that folder stops on an error dialog about the .NET
/// assemblies directory, on the screen of whoever is at the machine; the driver refuses instead and writes why into the sandbox's log.
/// These tests never start a viewer: every program here is an empty file. Each test has its own folders and child process.
/// </summary>
public sealed class LiveDriverTests
{
    private static string Driver => Path.Combine(TestPaths.RepoRoot, "tools", "live-driver.ps1");

    private static (int Code, string Output) Ps(string script) =>
        ChildProcess.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], TimeSpan.FromSeconds(90), new Dictionary<string, string?>());

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>An export folder with an (empty) program; with <paramref name="assemblies"/> a data_ folder holding an assembly beside it,
    /// with <paramref name="emptyData"/> a data_ folder holding none.</summary>
    private static string Export(bool assemblies, bool emptyData = false)
    {
        string dir = TestTemp.NewFolder("live-driver-export");
        string exe = Path.Combine(dir, "Example.console.exe");
        File.WriteAllText(exe, "");
        if (assemblies || emptyData)
        {
            string data = Directory.CreateDirectory(Path.Combine(dir, "data_Example_windows_x86_64")).FullName;
            if (assemblies)
                File.WriteAllText(Path.Combine(data, "GodotSharp.dll"), "");
        }
        return exe;
    }

    [Fact]
    public void An_export_is_complete_only_with_a_data_folder_of_assemblies_beside_the_program()
    {
        if (!OperatingSystem.IsWindows()) return;
        string complete = Export(assemblies: true), bare = Export(assemblies: false), empty = Export(assemblies: false, emptyData: true);
        string missing = Path.Combine(TestTemp.NewFolder("live-driver-missing"), "Example.console.exe");
        var r = Ps($". {Quote(Driver)}; foreach ($e in @({Quote(complete)}, {Quote(bare)}, {Quote(empty)}, {Quote(missing)})) {{ $why = Test-LiveExport $e; Write-Output (\"[\" + $(if ($why) {{ $why }} else {{ 'complete' }}) + \"]\") }}");
        var lines = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.StartsWith('[')).ToArray();
        Assert.Equal(4, lines.Length);
        Assert.Equal("[complete]", lines[0]);
        Assert.Contains("no data_ folder", lines[1]);
        Assert.Contains("no data_ folder", lines[2]);
        Assert.Contains("not found", lines[3]);
    }

    [Fact]
    public void Start_refuses_an_export_without_its_data_folder_and_writes_why_into_the_log()
    {
        if (!OperatingSystem.IsWindows()) return;
        string bare = Export(assemblies: false);
        string sandbox = Path.Combine(TestTemp.NewFolder("live-driver-sandbox"), "run");
        var r = Ps($". {Quote(Driver)}; $ok = Start-LiveViewer -Sandbox {Quote(sandbox)} -Exe {Quote(bare)}; Write-Output \"started=$ok proc=$([bool]$script:LdProc)\"");
        Assert.Contains("started=False proc=False", r.Output);
        string log = Path.Combine(sandbox, "live-driver.log");
        Assert.True(File.Exists(log), "the refusal is written into the sandbox's log");
        string text = File.ReadAllText(log);
        Assert.Contains("not started", text);
        Assert.Contains("no data_ folder", text);
        Assert.False(File.Exists(Path.Combine(sandbox, "run.log")), "godot.ps1 was never run");
    }
}
