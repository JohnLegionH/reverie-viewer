// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The animation and material caches drop what nothing uses, on the official viewer's schedule (llmotioncontroller.cpp purgeExcessMotions;
/// llgltfmateriallist.cpp flushMaterials) and never what is still in use. Each test builds its own sources; waits poll for the result.
/// </summary>
public class CacheEvictionTests
{
    // ---------------- animations: past 32 loaded, every clip nobody plays goes ----------------

    private static AnimationSource Loaded(int clips, int unreadable, out List<Guid> ids, out int[] fetches)
    {
        var counter = new int[1];
        fetches = counter;
        var bad = new HashSet<Guid>();
        var list = new List<Guid>();
        ids = list;
        var source = new AnimationSource((id, _) =>
        {
            Interlocked.Increment(ref counter[0]);
            return Task.FromResult(bad.Contains(id) ? AssetFetchResult.Of(AssetFetchStatus.NotFound) : AssetFetchResult.Ok(AvatarLiveTests.MinimalClip()));
        });
        for (int i = 0; i < clips + unreadable; i++)
        {
            var id = Guid.NewGuid();
            if (i >= clips) bad.Add(id);
            list.Add(id);
            source.TryGet(id, out _);
        }
        Assert.True(SpinWait.SpinUntil(() => { source.Pump(); return source.Ready == clips && source.Unavailable == unreadable; }, TimeSpan.FromSeconds(30)));
        return source;
    }

    [Fact]
    public void Past_the_limit_every_clip_nobody_plays_is_dropped_and_a_dropped_clip_is_fetched_again_when_asked_for()
    {
        using var source = Loaded(AnimationSource.MaxLoaded + 8, 0, out var ids, out var fetches);
        int before = fetches[0];
        Assert.Equal(AnimationSource.MaxLoaded + 8, source.Purge());
        Assert.Equal(0, source.Ready);
        Assert.Equal(MeshAvailability.Pending, source.TryGet(ids[0], out _));
        Assert.True(SpinWait.SpinUntil(() => { source.Pump(); return source.Ready == 1; }, TimeSpan.FromSeconds(30)));
        Assert.Equal(before + 1, fetches[0]);
    }

    [Fact]
    public void A_clip_any_avatar_plays_is_never_dropped()
    {
        using var source = Loaded(AnimationSource.MaxLoaded + 8, 0, out var ids, out _);
        object nearAvatars = new(), farAvatars = new();
        source.Uses.Report(nearAvatars, ids.Take(3));
        source.Uses.Report(farAvatars, ids.Skip(3).Take(2));
        Assert.Equal(AnimationSource.MaxLoaded + 8 - 5, source.Purge());
        Assert.Equal(5, source.Ready);
        foreach (var id in ids.Take(5)) Assert.Equal(MeshAvailability.Ready, source.TryGet(id, out _));
        // the far user goes: what only it played is now free to go, at the next purge past the limit
        source.Uses.Release(farAvatars);
        Assert.Equal(0, source.Purge());   // 5 loaded is under the limit: nothing is dropped
    }

    [Fact]
    public void At_or_under_the_limit_nothing_is_dropped_even_when_nobody_plays_it()
    {
        using var source = Loaded(AnimationSource.MaxLoaded, 0, out _, out _);
        Assert.Equal(0, source.Purge());
        Assert.Equal(AnimationSource.MaxLoaded, source.Ready);
    }

    [Fact]
    public void A_clip_that_could_not_be_read_stays_as_a_marker_so_it_is_not_fetched_again_and_again()
    {
        using var source = Loaded(AnimationSource.MaxLoaded + 1, 4, out var ids, out var fetches);
        int before = fetches[0];
        source.Purge();
        Assert.Equal(4, source.Unavailable);
        Assert.Equal(MeshAvailability.Unavailable, source.TryGet(ids[^1], out _));
        Assert.Equal(before, fetches[0]);
    }

    // ---------------- materials: unused for 30 s, found on the scan, then dropped ----------------

    private static IReadOnlyList<Guid> Keys(int n) => Enumerable.Range(0, n).Select(i => new Guid(i, 0, 0, new byte[8])).OrderBy(k => k).ToList();

    [Fact]
    public void An_unused_material_is_dropped_only_after_the_inactive_time_and_not_before()
    {
        var flush = new MaterialFlush();
        var keys = Keys(100);
        var dropped = new HashSet<Guid>();
        for (double now = 0; now <= MaterialFlush.InactiveSeconds; now += 1)
            foreach (var k in flush.Scan(now, keys, _ => false)) dropped.Add(k);
        Assert.Empty(dropped);   // every entry was marked inactive at its first look, and its 30 s had not passed at any look up to 30
        for (double now = 31; now < 60 && dropped.Count < keys.Count; now += 1)
            foreach (var k in flush.Scan(now, keys.Where(k => !dropped.Contains(k)).ToList(), _ => false)) dropped.Add(k);
        Assert.Equal(keys.Count, dropped.Count);
    }

    [Fact]
    public void A_material_in_use_is_never_dropped_however_long_the_scan_runs()
    {
        var flush = new MaterialFlush();
        var keys = Keys(100);
        var used = keys.Where((_, i) => i % 2 == 0).ToHashSet();
        var live = keys.ToList();
        for (double now = 0; now < 600; now += 1)
            foreach (var k in flush.Scan(now, live, used.Contains)) live.Remove(k);
        Assert.Equal(used.Count, live.Count);
        Assert.All(live, k => Assert.Contains(k, used));
    }

    [Fact]
    public void A_material_used_again_before_its_time_is_up_gets_a_fresh_inactive_period()
    {
        var flush = new MaterialFlush();
        var keys = Keys(1);
        bool inUse = false;
        Assert.Empty(flush.Scan(0, keys, _ => inUse));     // unused: marked inactive, dropped no earlier than 30
        inUse = true;
        Assert.Empty(flush.Scan(10, keys, _ => inUse));    // used: active again
        inUse = false;
        Assert.Empty(flush.Scan(20, keys, _ => inUse));    // unused again: a new 30 s from here
        Assert.Empty(flush.Scan(40, keys, _ => inUse));    // 20 s after that, still kept (the first period would have ended at 30)
        Assert.Equal(keys, flush.Scan(51, keys, _ => inUse));
    }

    [Fact]
    public void Each_scan_looks_at_the_larger_of_32_entries_and_a_twentieth_and_carries_on_where_it_stopped()
    {
        var flush = new MaterialFlush();
        var keys = Keys(1000);
        var looked = new HashSet<Guid>();
        int calls = 0;
        // an entry's state is read through its in-use test: count the distinct entries each scan asks about
        while (looked.Count < keys.Count && calls < 100)
        {
            var thisScan = new List<Guid>();
            flush.Scan(calls, keys, k => { thisScan.Add(k); return true; });
            Assert.Equal(50, thisScan.Count);              // 1000 / 20
            Assert.Empty(thisScan.Intersect(looked));      // not the ones the last scans saw, until the list has been gone round
            foreach (var k in thisScan) looked.Add(k);
            calls++;
        }
        Assert.Equal(20, calls);
        var small = new MaterialFlush();
        int asked = 0;
        small.Scan(0, Keys(100), _ => { asked++; return true; });
        Assert.Equal(MaterialFlush.MinScan, asked);        // 100 / 20 is 5: the minimum of 32 stands
    }

    [Fact]
    public void The_material_source_drops_what_nothing_draws_on_that_schedule_and_asks_for_it_again_when_needed()
    {
        int fetches = 0;
        using var source = new MaterialSource((_, _) => { Interlocked.Increment(ref fetches); return Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.NotFound)); });
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids) source.TryGet(id, out _);
        Assert.True(SpinWait.SpinUntil(() => source.Pending == 0, TimeSpan.FromSeconds(30)));
        Assert.Equal(3, source.Unavailable);
        source.Uses.Report(new object(), [ids[0]]);
        Assert.Equal(0, source.Flush(0));       // the two unused ones are marked, not dropped
        Assert.Equal(0, source.Flush(29));
        Assert.Equal(2, source.Flush(31));      // dropped once their 30 s have passed
        Assert.Equal(1, source.Unavailable);
        Assert.Equal(MeshAvailability.Unavailable, source.TryGet(ids[0], out _));   // the one in use is still there
        int before = fetches;
        Assert.Equal(MeshAvailability.Pending, source.TryGet(ids[1], out _));
        Assert.True(SpinWait.SpinUntil(() => source.Pending == 0, TimeSpan.FromSeconds(30)));
        Assert.Equal(before + 1, fetches);
    }
}
