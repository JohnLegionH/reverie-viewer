// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Every launch of the viewer by a test, render case, S-1/S-2, crowd, live or measuring run goes through tools\godot.ps1, the one place
/// that puts the window on the monitor in use, at a quarter of its size, in a bottom corner, at the bottom of the stacking order and
/// without focus. A script or test that starts the viewer's executable (or the engine's) itself would open a full-size window on top
/// of whatever the person at the machine is doing, so this scan fails on any such launch.
/// </summary>
public class ViewerLaunchScanTests
{
    /// <summary>The only files that start the executable themselves: the launcher, and the small debugger the launcher runs it under.</summary>
    private static readonly string[] Launchers = ["tools/godot.ps1", "tools/crash-watch.ps1", "core/Viewer.Core.Tests/ViewerLaunchScanTests.cs" /* its negative controls spell the launches out */];

    // PowerShell: the call operator, Start-Process, or a process start's FileName given an executable variable (a name containing "exe") or the viewer's / engine's file name.
    private static readonly Regex[] ScriptLaunches =
    [
        new(@"(?:^|[;(|&{]|\s)&\s+[""']?\$\{?\w*exe\w*", RegexOptions.IgnoreCase),
        new(@"Start-Process\s+(?:-FilePath\s+)?[""']?\$\{?\w*exe\w*", RegexOptions.IgnoreCase),
        new(@"\.FileName\s*=\s*[""']?\$\{?\w*exe\w*", RegexOptions.IgnoreCase),
        new(@"(?:&|Start-Process\s+(?:-FilePath\s+)?)\s*[""']?[^|;\r\n]*(?:Reverie(?:\.console)?\.exe|Godot_v[\w.\-]*\.exe)", RegexOptions.IgnoreCase),
        new(@"\.FileName\s*=\s*[^;\r\n]*(?:Reverie(?:\.console)?\.exe|Godot_v[\w.\-]*\.exe)", RegexOptions.IgnoreCase),
    ];

    // C#: a process started from the viewer's or the engine's executable.
    private static readonly Regex[] CodeLaunches =
    [
        new(@"(?:ProcessStartInfo|Process\.Start)\s*\([^)\r\n]*(?:Reverie(?:\.console)?\.exe|Godot_v[\w.\-]*\.exe)", RegexOptions.IgnoreCase),
        new(@"FileName\s*=\s*[^;\r\n]*(?:Reverie(?:\.console)?\.exe|Godot_v[\w.\-]*\.exe)", RegexOptions.IgnoreCase),
    ];

    public static IEnumerable<string> Bypasses(string relativePath, string text)
    {
        var patterns = relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? CodeLaunches : ScriptLaunches;
        int number = 0;
        foreach (string raw in text.Split('\n'))
        {
            number++;
            string line = raw.TrimEnd('\r');
            string code = line.TrimStart();
            if (code.StartsWith('#') || code.StartsWith("//")) continue;   // a comment is not a launch
            // a call of godot.ps1 that is handed the executable is the launcher at work
            if (Regex.IsMatch(line, @"godot\.ps1|\$godot\b", RegexOptions.IgnoreCase)) continue;
            if (patterns.Any(p => p.IsMatch(line))) yield return $"{relativePath}:{number}: {code}";
        }
    }

    private static IEnumerable<string> Files()
    {
        string root = TestPaths.RepoRoot;
        foreach (string pattern in new[] { "*.ps1", "*.sh", "*.cs", "*.cmd", "*.bat" })
            foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Split('/').Any(part => part is "bin" or "obj" or ".godot" or "build" or ".git" or "test-results")) continue;
                if (Launchers.Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;
                yield return rel;
            }
    }

    [Fact]
    public void No_script_or_test_starts_the_viewer_or_the_engine_without_the_launcher()
    {
        var found = new List<string>();
        int scanned = 0;
        foreach (string rel in Files())
        {
            scanned++;
            found.AddRange(Bypasses(rel, File.ReadAllText(Path.Combine(TestPaths.RepoRoot, rel))));
        }
        Assert.True(scanned > 50, $"the scan looked at {scanned} files; it should see the whole repository");
        Assert.True(found.Count == 0, "launches that bypass tools/godot.ps1 (windows would open on top, with focus):\n" + string.Join("\n", found));
    }

    [Fact]
    public void The_scan_sees_the_launchers_and_the_scripts_that_use_them()
    {
        string root = TestPaths.RepoRoot;
        foreach (string rel in new[] { "tools/godot.ps1", "tools/render.ps1", "tools/s1.ps1", "tools/close-cycles.ps1", "tools/live-check.ps1", "tools/automation.ps1", "tools/contact-sheets.ps1" })
            Assert.True(File.Exists(Path.Combine(root, rel)), rel + " is part of what the scan covers");
        // the scripts that run the viewer name the launcher
        foreach (string rel in new[] { "tools/render.ps1", "tools/s1.ps1", "tools/close-cycles.ps1", "tools/live-check.ps1", "tools/automation.ps1", "tools/contact-sheets.ps1" })
            Assert.Contains("godot.ps1", File.ReadAllText(Path.Combine(root, rel)));
    }

    // Negative controls: the scan is only worth anything if it catches each way of bypassing the launcher.
    [Theory]
    [InlineData("tools/x.ps1", "& $exe --test-render --tr-out=D:\\o")]
    [InlineData("tools/x.ps1", "  & \"$exe\" -- --test-render")]
    [InlineData("tools/x.ps1", "$out = & $GodotExe --headless")]
    [InlineData("tools/x.ps1", "Start-Process -FilePath $exe -ArgumentList $args")]
    [InlineData("tools/x.ps1", "Start-Process $viewerExe")]
    [InlineData("tools/x.ps1", "$psi.FileName = $exe")]
    [InlineData("tools/x.ps1", "$psi.FileName = (Join-Path $build \"Reverie.console.exe\")")]
    [InlineData("tools/x.ps1", "& (Join-Path $build 'Reverie.exe') --test-render")]
    [InlineData("tools/x.ps1", "Start-Process D:\\tools\\godot\\Godot_v4.7.2-stable_mono_win64.exe")]
    [InlineData("core/Viewer.Core.Tests/X.cs", "var psi = new ProcessStartInfo(Path.Combine(build, \"Reverie.console.exe\"));")]
    [InlineData("core/Viewer.Core.Tests/X.cs", "Process.Start(\"D:\\\\build\\\\Reverie.exe\");")]
    [InlineData("core/Viewer.Core.Tests/X.cs", "psi.FileName = Path.Combine(dir, \"Godot_v4.7.2-stable_mono_win64_console.exe\");")]
    public void A_launch_that_bypasses_the_launcher_is_found(string path, string line)
    {
        Assert.Single(Bypasses(path, line + "\n"));
    }

    [Theory]
    [InlineData("tools/x.ps1", "& powershell -NoProfile -File $godot -Exe $exe -ArgLine $args")]
    [InlineData("tools/x.ps1", "$psi.Arguments = \"-NoProfile -ExecutionPolicy Bypass -File `\"$godot`\" -Exe `\"$exe`\"\"")]
    [InlineData("tools/x.ps1", "$exe = Join-Path $build \"Reverie.console.exe\"")]
    [InlineData("tools/x.ps1", "# & $exe would open a window on top")]
    [InlineData("tools/x.ps1", "$outputs = @(\"Reverie.exe\", \"Reverie.pck\") | ForEach-Object { Join-Path $build $_ }")]
    [InlineData("core/Viewer.Core.Tests/X.cs", "var psi = new ProcessStartInfo(WindowsPowerShell);")]
    [InlineData("core/Viewer.Core.Tests/X.cs", "File.WriteAllBytes(Path.Combine(build, \"Reverie.exe\"), new byte[] { 1 });")]
    [InlineData("core/Viewer.Core.Tests/X.cs", "// new ProcessStartInfo(\"Reverie.exe\")")]
    public void The_launcher_itself_and_plain_mentions_are_not_launches(string path, string line)
    {
        Assert.Empty(Bypasses(path, line + "\n"));
    }
}
