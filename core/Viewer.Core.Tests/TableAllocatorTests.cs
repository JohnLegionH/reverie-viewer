// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The slots of the bind-table atlas: growth with demand, freeing by reference count, reuse, the limit and the memory it stands for.
/// Pure, parallel.
/// </summary>
public class TableAllocatorTests
{
    private static Guid[] Assets(int n) => Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToArray();

    private static TableAllocator Make() => new();

    [Fact]
    public void Growing_in_halves_takes_fewer_and_fewer_large_growths_to_hold_five_hundred_tables()
    {
        int GrowthsFor(TableAllocator a)
        {
            int growths = 0, last = a.Capacity;
            foreach (var g in Assets(500))
            {
                Assert.True(a.Acquire(g, out _) >= 0);
                if (a.Capacity != last) { growths++; last = a.Capacity; }
            }
            return growths;
        }
        var quarters = new TableAllocator();
        var halves = new TableAllocator(growthDivisor: 2);
        int byQuarters = GrowthsFor(quarters), byHalves = GrowthsFor(halves);
        Assert.True(byHalves < byQuarters, $"{byHalves} growths in halves, {byQuarters} in quarters");
        Assert.Equal(500, halves.InUse);
        Assert.InRange(halves.Capacity, 500, 500 * 3 / 2 + TableAllocator.GrowStep);   // at most a step of half the capacity over the demand
    }

    [Fact]
    public void Starts_small_and_grows_to_hold_what_is_worn()
    {
        var a = Make();
        Assert.Equal(TableAllocator.StartTables, a.Capacity);
        foreach (var g in Assets(300)) Assert.True(a.Acquire(g, out _) >= 0);
        Assert.Equal(300, a.InUse);
        Assert.InRange(a.Capacity, 300, 300 + 63);   // no more than a step over the demand
    }

    [Fact]
    public void A_hundred_distinct_outfits_of_twenty_meshes_all_get_a_table()
    {
        var a = Make();
        int refused = 0;
        foreach (var g in Assets(100 * 20)) if (a.Acquire(g, out _) < 0) refused++;
        Assert.Equal(0, refused);
        Assert.Equal(2000, a.InUse);
    }

    [Fact]
    public void Five_thousand_tables_all_get_a_slot_and_no_two_share_one()
    {
        var a = Make();
        var slots = new HashSet<int>();
        foreach (var g in Assets(5000)) Assert.True(slots.Add(a.Acquire(g, out _)));
        Assert.Equal(5000, a.InUse);
        Assert.All(slots, s => Assert.InRange(s, 0, a.Capacity - 1));
    }

    [Fact]
    public void A_shared_mesh_has_one_slot_and_goes_with_its_last_wearer()
    {
        var a = Make();
        var g = Guid.NewGuid();
        int s = a.Acquire(g, out var first);
        Assert.True(first);
        Assert.Equal(s, a.Acquire(g, out var second));
        Assert.False(second);
        Assert.Equal(2, a.Users(g));
        Assert.False(a.Release(g));
        Assert.True(a.Contains(g));
        Assert.True(a.Release(g));
        Assert.False(a.Contains(g));
        Assert.Equal(0, a.InUse);
    }

    [Fact]
    public void Avatars_leaving_free_their_tables_for_the_next_arrivals()
    {
        var a = Make();
        var outfitA = Assets(1000);
        foreach (var g in outfitA) a.Acquire(g, out _);
        int capacity = a.Capacity;
        foreach (var g in outfitA) Assert.True(a.Release(g));
        Assert.Equal(0, a.InUse);
        foreach (var g in Assets(1000)) Assert.True(a.Acquire(g, out _) >= 0);
        Assert.Equal(capacity, a.Capacity);   // the freed slots were used, the atlas did not grow
    }

    [Fact]
    public void Past_the_limit_a_new_mesh_is_refused_and_a_worn_one_is_still_served()
    {
        var a = new TableAllocator(maxTables: 64);
        var worn = Assets(64);
        foreach (var g in worn) Assert.True(a.Acquire(g, out _) >= 0);
        var extra = Guid.NewGuid();
        Assert.Equal(-1, a.Acquire(extra, out var isNew));
        Assert.False(isNew);
        Assert.False(a.Contains(extra));
        Assert.True(a.Acquire(worn[7], out _) >= 0);
        Assert.Equal(64, a.Capacity);
        Assert.True(a.Release(worn[3]));
        Assert.True(a.Acquire(extra, out _) >= 0);   // the one that waited gets the freed slot
    }

    [Fact]
    public void Release_of_an_unknown_asset_changes_nothing()
    {
        var a = Make();
        Assert.False(a.Release(Guid.NewGuid()));
        Assert.Equal(0, a.InUse);
    }

    [Fact]
    public void Freed_low_slots_are_reused_first_and_the_atlas_shrinks_when_the_top_is_empty()
    {
        var a = Make();
        var all = Assets(2000);
        foreach (var g in all) a.Acquire(g, out _);
        int big = a.Capacity;
        for (int i = 100; i < 2000; i++) a.Release(all[i]);   // the last ones leave
        Assert.Equal(100, a.InUse);
        int trimmed = a.Trim();
        Assert.True(trimmed < big / 4, $"{big} -> {trimmed}");
        Assert.True(a.Capacity >= 100);
        foreach (var g in Assets(500)) Assert.True(a.Acquire(g, out _) >= 0);   // and it grows again
    }

    [Fact]
    public void Trim_never_cuts_below_a_slot_in_use()
    {
        var a = Make();
        var all = Assets(500);
        foreach (var g in all) a.Acquire(g, out _);
        for (int i = 0; i < 499; i++) a.Release(all[i]);   // only the last slot stays
        Assert.Equal(499, a.Acquire(all[499], out _));
        Assert.True(a.Trim() >= 500);
    }

    [Fact]
    public void Memory_follows_demand_to_within_a_quarter_and_a_step()
    {
        foreach (int outfits in new[] { 25, 50, 100 })
        {
            var a = Make();
            int tables = outfits * 20;
            foreach (var g in Assets(tables)) a.Acquire(g, out _);
            Assert.InRange(a.Capacity, tables, tables + tables / 4 + TableAllocator.GrowStep);
            Assert.Equal(a.Capacity * 2L * 330 * 16, TableAllocator.Bytes(a.Capacity));
        }
    }

    [Fact]
    public void The_limit_in_memory_is_under_a_hundred_mebibytes()
    {
        Assert.True(TableAllocator.Bytes(TableAllocator.DefaultMaxTables) < 100L << 20);
    }
}
