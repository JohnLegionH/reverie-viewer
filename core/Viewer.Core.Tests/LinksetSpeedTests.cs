// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Asking for a link set must cost what the link set is, not what the region is. The presentation asks for the link set of every object
/// that is added, changed or moved, once per queued change; a region of 160 thousand objects that scanned all of them for each ask took
/// about 50 ms a change on the main thread, which is a frame stall of 100 ms or more every time two changes are applied. Each test builds
/// its own world, so the class runs in parallel; the time limit is wide (the scan takes more than a second here, the index a few
/// milliseconds) so a busy machine does not decide it.
/// </summary>
[Collection("TimingTests")]
public class LinksetSpeedTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");

    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);

    private static PrimData Prim(uint local, uint parent = 0) =>
        new(local, G(local), parent, 0, new Vector3(10, 20, 30), Quaternion.Identity, Vector3.One);

    [Fact]
    public void Asking_for_link_sets_in_a_big_region_does_not_scan_the_region_each_time()
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        const int roots = 20_000, asks = 1_500;
        for (uint r = 0; r < roots; r++)
        {
            uint root = 1 + r * 3;
            w.ApplyObject(Sim, Prim(root));
            w.ApplyObject(Sim, Prim(root + 1, parent: root));
            w.ApplyObject(Sim, Prim(root + 2, parent: root + 1));   // a grandchild, so the walk goes two levels down
        }
        var sw = Stopwatch.StartNew();
        int found = 0;
        for (int i = 0; i < asks; i++)
            found += w.Linkset(new ObjectId(w.Epoch, 1 + (uint)i * 3 * 13)).Count;
        sw.Stop();
        Assert.Equal(asks * 2, found);   // the answers are right: two prims under each root asked
        Assert.True(sw.ElapsedMilliseconds < 250, $"{asks} link-set asks in a region of {roots * 3} objects took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_killed_child_leaves_its_link_set_and_the_rest_stays()
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.ApplyObject(Sim, Prim(10));
        w.ApplyObject(Sim, Prim(11, parent: 10));
        w.ApplyObject(Sim, Prim(12, parent: 10));
        w.Kill(Sim, [11]);
        Assert.Equal([12u], w.Linkset(new ObjectId(w.Epoch, 10)).Select(o => o.Id.LocalId));
    }

    [Fact]
    public void A_prim_linked_to_another_root_moves_between_link_sets()
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.ApplyObject(Sim, Prim(10));
        w.ApplyObject(Sim, Prim(20));
        w.ApplyObject(Sim, Prim(11, parent: 10));
        w.ApplyObject(Sim, Prim(11, parent: 20));
        Assert.Empty(w.Linkset(new ObjectId(w.Epoch, 10)));
        Assert.Equal([11u], w.Linkset(new ObjectId(w.Epoch, 20)).Select(o => o.Id.LocalId));
    }

    [Fact]
    public void Link_set_asks_in_a_region_of_163000_objects_stay_far_inside_the_frame_budget()
    {
        // the largest link set a region allows (a root and 255 prims, the last ones nested three deep) among 163,000 other objects: one ask is the most the
        // presentation does for one queued change, and it has to fit well inside a frame's budget, so a frame can always hold one
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        for (uint r = 0; r < 54_000; r++)
        {
            uint root = 1000 + r * 3;
            w.ApplyObject(Sim, Prim(root));
            w.ApplyObject(Sim, Prim(root + 1, parent: root));
            w.ApplyObject(Sim, Prim(root + 2, parent: root + 1));
        }
        const uint big = 1;
        w.ApplyObject(Sim, Prim(big));
        for (uint c = 2; c <= 256; c++)
            w.ApplyObject(Sim, Prim(c, parent: c % 3 == 0 && c > 4 ? c - 1 : big));
        Assert.Equal(255, w.Linkset(new ObjectId(w.Epoch, big)).Count);   // warms the path, and the answer is the whole link set
        var times = new List<double>();
        for (int i = 0; i < 300; i++)
        {
            var sw = Stopwatch.StartNew();
            var set = w.Linkset(new ObjectId(w.Epoch, i % 2 == 0 ? big : 1000 + (uint)i * 3));
            times.Add(sw.Elapsed.TotalMilliseconds);
            Assert.NotEmpty(set);
        }
        times.Sort();
        // 9 asks in 10 are well inside the budget (the index takes a fraction of a millisecond, a scan of the region 800 ms); the very slowest is
        // not asserted, since a busy machine can hold one thread for longer than the ask takes
        double worst = times[269];
        Assert.True(worst < 2, $"the 270th fastest of 300 asks took {worst:F2} ms");
    }
}
