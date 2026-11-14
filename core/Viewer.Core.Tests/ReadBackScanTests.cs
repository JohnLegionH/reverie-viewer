// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The host does not read an engine resource back from the engine when it holds, or can hold, the object itself. Reading a
/// resource through a getter (<c>MeshInstance3D.Mesh</c>, <c>WorldEnvironment.Environment</c>, <c>Viewport.GetTexture()</c>,
/// <c>Viewport.FindWorld3D()</c>) makes the binding hand out a managed wrapper for the engine's object; when the wrapper from
/// an earlier read is being finalized at that moment the call fails ("Handle is not initialized"), which the lifecycle checks
/// see as an engine error (3 of 26 runs of the nodes view). A source scan of the host keeps each such read to the one place
/// that makes and keeps the value.
/// </summary>
public class ReadBackScanTests
{
    private static IEnumerable<(string File, int Line, string Text)> HostLines()
    {
        string root = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "build" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].TrimStart();
                if (t.StartsWith("//") || t.StartsWith("///")) continue;
                yield return (Path.GetRelativePath(root, file).Replace('\\', '/'), i + 1, lines[i]);
            }
        }
    }

    private static List<string> Hits(string pattern) =>
        HostLines().Where(l => Regex.IsMatch(l.Text, pattern)).Select(l => $"{l.File}:{l.Line}").ToList();

    [Fact]
    public void A_viewports_texture_is_read_once_and_kept()
    {
        // Rig (the harness's stage), the automation host's screenshot, the glow pass, the sky probe's capture and the proportions case's window picture (one read for each viewport they make) keep theirs; nothing chains GetTexture().GetImage()
        Assert.Empty(Hits(@"GetTexture\(\)\.GetImage\(\)"));
        Assert.Equal(6, Hits(@"\bGetTexture\(\)").Where(h => !h.StartsWith("Main.FrameBurst.cs:")).Count());
    }

    [Fact]
    public void The_development_frame_bursts_read_happens_only_while_a_burst_runs()
    {
        // burst off: the one extra read (5 in all, 4 without it) is never reached. It is made by the capture handler, which is subscribed only by
        // FrameBurstStart after its "aid is off" return, and is unsubscribed when the burst ends, so with the burst off nothing runs each frame.
        Assert.Single(Hits(@"\bGetTexture\(\)").Where(h => h.StartsWith("Main.FrameBurst.cs:")).ToList());
        string text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "Main.FrameBurst.cs"));
        int start = text.IndexOf("internal string? FrameBurstStart", StringComparison.Ordinal);
        int guard = text.IndexOf("if (!FrameBurstOn) return null;", start, StringComparison.Ordinal);
        int subscribe = text.IndexOf("FramePostDraw += FrameBurstCapture", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && guard > start && subscribe > guard, "the capture handler is subscribed only after the burst-off return");
        Assert.Equal(1, Regex.Matches(text, @"FramePostDraw \+= FrameBurstCapture").Count);
        Assert.Equal(1, Regex.Matches(text, @"FramePostDraw -= FrameBurstCapture").Count);
        Assert.True(text.IndexOf("_burstTexture ??=", StringComparison.Ordinal) > text.IndexOf("private void FrameBurstCapture", StringComparison.Ordinal));
    }

    [Fact]
    public void A_viewports_world_is_read_once_and_its_scenario_kept()
    {
        // Main.Scenario(), the harness's Rig, the pipeline warm-up's own viewport, the arrival warm-up's own viewport
        Assert.Equal(4, Hits(@"\bFindWorld3D\(\)").Count);
        // the start-up smoke check reads the world's space once, in a process that has read nothing else from it
        Assert.All(Hits(@"\bWorld3D\b\.(Scenario|Space)"), h => Assert.StartsWith("S1Smoke.cs:", h));
    }

    [Fact]
    public void A_world_environment_nodes_resource_comes_from_the_stage_not_from_the_node()
    {
        // only Stage.EnvironmentOf falls back to the node's getter, for a node the stage did not make
        Assert.Single(Hits(@"\bnode\.Environment\b"));
        Assert.Empty(Hits(@"\b(world|rig\.Environment)\.Environment\b"));
    }

    [Fact]
    public void The_node_terrain_view_never_reads_a_chunks_mesh_back_from_its_node()
    {
        var reads = HostLines().Where(l => l.File == "Presentation/NodeTerrainView.cs" && Regex.IsMatch(l.Text, @"\.Mesh\b(?!\s*=[^=])") && !l.Text.Contains("kept.Mesh") && !l.Text.Contains("Mesh Mesh") && !l.Text.Contains("(mi, mesh)")).ToList();
        Assert.Empty(reads);
    }
}
