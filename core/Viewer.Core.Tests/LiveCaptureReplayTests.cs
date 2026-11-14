// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// L2 replays of the live read-mostly captures (region A, region B). The captures are local-only (fixtures-local\, ADR-011);
/// each test is a real SKIP with its reason when its capture is absent. What IS committed is the derived expectation
/// (counts, completeness, hashes) in fixtures\expectations\live-captures.json, checked against the capture's footer (the
/// live session's final snapshot) and against the replay.
/// </summary>
public class LiveCaptureReplayTests
{
    private static Capture Load(string file) => CaptureFile.Read(LiveCaptures.PathOf(file));

    private static StateSummary? Expected(string tag)
    {
        var path = Path.Combine(TestPaths.RepoRoot, "fixtures", "expectations", "live-captures.json");
        if (!File.Exists(path))
            return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty(tag, out var e) ? e.Deserialize<StateSummary>() : null;
    }

    private static void ReplaysToTheLiveState(string file, string tag)
    {
        var c = Load(file);
        Assert.NotNull(c.Footer);
        var live = c.Footer!.Live;
        var r = ReplayRunner.Run(c);
        Assert.Equal(SessionStage.InWorld, r.Stage);
        Assert.Equal(live.Objects, r.Summary.Objects);
        Assert.Equal(live.Avatars, r.Summary.Avatars);
        Assert.Equal(live.TerrainPatches, r.Summary.TerrainPatches);
        Assert.Equal(live.StructuralHash, r.Summary.StructuralHash);
        Assert.Equal(live.FullHash, r.Summary.FullHash);
        // Attachments bound to wearers: every attachment root has its wearer, none left pending.
        Assert.Equal(r.Summary.Attachments, r.Summary.AttachmentsBound);
        Assert.Equal(0, r.Summary.PendingAttachmentsUnbound);
        // The committed derived expectation matches this capture.
        var expected = Expected(tag);
        Assert.NotNull(expected);
        Assert.Equal(expected, live);
    }

    [CaptureFact("capture-region-a.jsonl.gz")]
    public void RegionA_replay_matches_the_live_final_snapshot() => ReplaysToTheLiveState("capture-region-a.jsonl.gz", "region-a");

    [CaptureFact("capture-region-b.jsonl.gz")]
    public void RegionB_replay_matches_the_live_final_snapshot() => ReplaysToTheLiveState("capture-region-b.jsonl.gz", "region-b");

    [CaptureFact("capture-region-b.jsonl.gz")]
    public void RegionB_terrain_is_complete_for_its_real_size()
    {
        var r = ReplayRunner.Run(Load("capture-region-b.jsonl.gz"));
        Assert.Equal(1024, r.Summary.RegionSizeX);
        Assert.Equal(1024, r.Summary.RegionSizeY);
        Assert.Equal(4096, r.Summary.TerrainExpected);   // 64 x 64 patches of 16 m
        Assert.True(r.Summary.TerrainComplete, $"{r.Summary.TerrainPatches}/{r.Summary.TerrainExpected} patches");
    }

    [CaptureFact("capture-region-a.jsonl.gz")]
    public void RegionA_reordered_and_duplicated_delivery_reaches_the_same_state() => FaultsKeepTheState("capture-region-a.jsonl.gz");

    [CaptureFact("capture-region-b.jsonl.gz")]
    public void RegionB_reordered_and_duplicated_delivery_reaches_the_same_state() => FaultsKeepTheState("capture-region-b.jsonl.gz");

    private static void FaultsKeepTheState(string file)
    {
        var c = Load(file);
        var clean = ReplayRunner.Run(c).Summary;
        for (int seed = 1; seed <= 5; seed++)
        {
            var reordered = ReplayRunner.Run(c, new FaultPlan(seed, ReorderWindow: 32));
            Assert.True(reordered.Reordered > 0);
            Assert.Equal(clean.StructuralHash, reordered.Summary.StructuralHash);
            var duplicated = ReplayRunner.Run(c, new FaultPlan(seed, DuplicateRate: 0.2));
            Assert.Equal(clean.FullHash, duplicated.Summary.FullHash);
        }
    }

    [CaptureFact("capture-region-a.jsonl.gz")]
    public void Two_replays_of_the_same_capture_produce_identical_state_hashes()
    {
        var c = Load("capture-region-a.jsonl.gz");
        var plan = new FaultPlan(Seed: 11, ReorderWindow: 32, DuplicateRate: 0.1, DropRate: 0.05);
        Assert.Equal(ReplayRunner.Run(c).Summary, ReplayRunner.Run(c).Summary);
        Assert.Equal(ReplayRunner.Run(c, plan), ReplayRunner.Run(c, plan));
    }
}
