// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The log lines that show what happens to the agent's worn pieces across a crossing are wired in the host's source: the rigged-piece lines on the
/// anchor's prim layer and on every neighbour's, and the count of worn and drawn pieces after a crossing.</summary>
public class CrossingAttachmentLogTests
{
    private static string Source(string file)
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Viewer.sln"))) dir = Path.GetDirectoryName(dir)!;
        return File.ReadAllText(Path.Combine(dir ?? throw new InvalidOperationException("repository root not found"), file.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void A_neighbours_prim_layer_reports_its_rigged_pieces_like_the_anchors()
    {
        string text = Source("host/Viewer.Godot/Main.Neighbours.cs");
        int add = text.IndexOf("private void AddStack(", StringComparison.Ordinal);
        Assert.True(add >= 0);
        Assert.Contains("HookRiggedLogs(stack.Prims", text[add..Math.Min(text.Length, add + 1500)]);
        Assert.Contains("HookRiggedLogs(prims", Source("host/Viewer.Godot/Main.cs"));
    }

    [Fact]
    public void A_crossing_queues_a_count_of_the_worn_and_the_drawn_pieces()
    {
        string text = Source("host/Viewer.Godot/Main.Neighbours.cs");
        int cross = text.IndexOf("\"neighbour.crossed\"", StringComparison.Ordinal);
        Assert.True(cross >= 0);
        Assert.Contains("QueueAttachmentChecks(", text[cross..Math.Min(text.Length, cross + 400)]);
        Assert.Contains("\"attachments.after_crossing\"", text);
        Assert.Contains("RunAttachmentChecks(session)", text);
    }
}
