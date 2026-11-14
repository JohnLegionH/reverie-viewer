// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// tools\ship-export.ps1 copies an export into the folder the viewer is run from. A file the folder has and the export lacks is moved aside
/// (never deleted), the copy is checked file by file, and nothing is copied while a viewer from that folder is running. These tests run the
/// script on small folders with a stand-in for the move-aside tool; each uses its own temp folder, so they run in parallel.
/// </summary>
public sealed class ShipExportScriptTests
{
    private static string Script => Path.Combine(TestPaths.RepoRoot, "tools", "ship-export.ps1");

    private sealed class Rig
    {
        public string Root = TestTemp.NewFolder("ship-export");
        public string Src => Path.Combine(Root, "export");
        public string Dst => Path.Combine(Root, "shipping");
        public string Aside => Path.Combine(Root, "aside");
        public string Mover => Path.Combine(Root, "mover.ps1");

        public Rig()
        {
            Directory.CreateDirectory(Src);
            Directory.CreateDirectory(Dst);
            // stands in for the move-aside tool: -Label then absolute paths; each path goes under the aside folder, keeping its folders
            File.WriteAllText(Mover, """
                param([string]$Label, [Parameter(ValueFromRemainingArguments=$true)][string[]]$Paths)
                foreach ($p in $Paths) {
                    $rel = $p.Substring(3)
                    $to = Join-Path $env:ASIDE_ROOT $rel
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
                    Move-Item -LiteralPath $p -Destination $to
                }
                """);
        }

        public void Write(string folder, string rel, string text)
        {
            string p = Path.Combine(folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        public (int ExitCode, string Output) Ship(params string[] extra)
        {
            var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Source", Src, "-Destination", Dst, "-MoveAsideTool", Mover };
            args.AddRange(extra);
            return ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), new Dictionary<string, string?> { ["ASIDE_ROOT"] = Aside });
        }
    }

    [Fact]
    public void The_export_is_copied_and_every_file_matches()
    {
        var r = new Rig();
        r.Write(r.Src, "Reverie.pck", "new pck");
        r.Write(r.Src, "data/Viewer.Core.dll", "new core");
        r.Write(r.Dst, "Reverie.pck", "old pck");
        var run = r.Ship();
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("new pck", File.ReadAllText(Path.Combine(r.Dst, "Reverie.pck")));
        Assert.Equal("new core", File.ReadAllText(Path.Combine(r.Dst, "data", "Viewer.Core.dll")));
    }

    [Fact]
    public void A_file_the_export_lacks_is_moved_aside_with_its_path_and_not_deleted()
    {
        var r = new Rig();
        r.Write(r.Src, "Reverie.pck", "new pck");
        r.Write(r.Dst, "Reverie.pck", "old pck");
        r.Write(r.Dst, "data/Old.dll", "an old library");
        r.Write(r.Dst, "Reverie.exe~RF1234.TMP", "a half copy");
        var run = r.Ship();
        Assert.Equal(0, run.ExitCode);
        Assert.False(File.Exists(Path.Combine(r.Dst, "data", "Old.dll")));
        Assert.False(File.Exists(Path.Combine(r.Dst, "Reverie.exe~RF1234.TMP")));
        var moved = Directory.GetFiles(r.Aside, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "Old.dll", "Reverie.exe~RF1234.TMP" }, moved);
        Assert.Equal("an old library", File.ReadAllText(Directory.GetFiles(r.Aside, "Old.dll", SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public void The_shipping_folders_own_test_state_is_not_part_of_the_export_and_is_moved_aside_too()
    {
        var r = new Rig();
        r.Write(r.Src, "Reverie.pck", "new pck");
        r.Write(r.Dst, "appdata/override-cfg-1.moved", "x");
        r.Write(r.Dst, "s1-result.json", "{}");
        var run = r.Ship();
        Assert.Equal(0, run.ExitCode);
        Assert.False(File.Exists(Path.Combine(r.Dst, "s1-result.json")));
        Assert.False(File.Exists(Path.Combine(r.Dst, "appdata", "override-cfg-1.moved")));
    }

    [Fact]
    public void Nothing_is_copied_or_moved_without_a_move_aside_tool()
    {
        var r = new Rig();
        r.Write(r.Src, "Reverie.pck", "new pck");
        r.Write(r.Dst, "Reverie.pck", "old pck");
        r.Write(r.Dst, "Old.dll", "an old library");
        var args = new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Source", r.Src, "-Destination", r.Dst };
        var run = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2), new Dictionary<string, string?>());
        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal("old pck", File.ReadAllText(Path.Combine(r.Dst, "Reverie.pck")));
        Assert.True(File.Exists(Path.Combine(r.Dst, "Old.dll")));
    }
}
