// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The background fetch prepares its queue of folders in slices over several ticks, so no single frame pays for the whole
/// tree (a first tick that built the queue for a 2,000-folder account cost several milliseconds), starts that shortly before
/// the start delay ends, and still asks for the folders in the same order. Every member builds its own objects and uses a
/// test clock, so the class runs in parallel.
/// </summary>
public class InventoryFetchSpreadTests
{
    private static Guid G(int n) => Guid.Parse($"40000000-0000-4000-8000-{n:x12}");
    private static readonly Guid Owner = G(900001), Root = G(0), LibOwner = G(900002), LibRoot = G(900003), LibSub = G(900004);

    private sealed class Rig
    {
        public readonly List<Guid> Asked = new();
        public readonly InventoryModel Model;
        public readonly InventoryBackgroundFetch Fetch;
        public TimeSpan Now;

        public Rig(int folders, BackgroundFetchOptions options)
        {
            Model = new InventoryModel((f, _) => { lock (Asked) Asked.Add(f); }, 100000);
            var list = new List<InventoryFolderData> { new(Root, Guid.Empty, "", FolderKind.Root, 1) };
            for (int i = 1; i <= folders; i++)
            {
                var kind = i == 1 ? FolderKind.CurrentOutfit : i == 2 ? FolderKind.MyOutfits : i == 3 ? FolderKind.Texture : i == 4 ? FolderKind.Trash : FolderKind.None;
                // names that sort differently by case and by number, parents spread over the first few folders
                list.Add(new(G(i), i < 10 ? Root : G(i % 9 + 1), (i % 3 == 0 ? "b" : "B") + ((i * 7919) % 1000), kind, 1));
            }
            Model.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot, list,
                new[] { new InventoryFolderData(LibRoot, Guid.Empty, "", FolderKind.Root, 1), new InventoryFolderData(LibSub, LibRoot, "Objects", FolderKind.Object, 1) }));
            Fetch = new InventoryBackgroundFetch(Model, () => Now, options);
        }
    }

    private static readonly BackgroundFetchOptions Immediate = new(TimeSpan.Zero, TimeSpan.Zero, 100000);

    [Fact]
    public void Preparing_a_large_tree_takes_several_ticks_and_none_looks_at_more_than_one_slice()
    {
        var r = new Rig(2000, Immediate);
        r.Fetch.Begin();
        int ticksBeforeFirstAsk = 0, lookups = 0, asked = 0;
        for (int guard = 0; guard < 1000 && asked == 0; guard++)
        {
            r.Now += TimeSpan.FromMilliseconds(16);
            asked = r.Fetch.Tick();
            Assert.InRange(r.Fetch.LastTickLookups, 0, InventoryBackgroundFetch.WorkPerTick);
            lookups += r.Fetch.LastTickLookups;
            if (asked == 0) ticksBeforeFirstAsk++;
        }
        Assert.Equal(1, asked);
        Assert.True(ticksBeforeFirstAsk >= 2000 / InventoryBackgroundFetch.WorkPerTick, $"the queue was ready after {ticksBeforeFirstAsk} ticks");
        Assert.True(lookups >= 2000, $"only {lookups} folders were looked at");
    }

    [Fact]
    public void The_folders_are_asked_in_the_order_of_the_rank_then_the_name_then_the_id()
    {
        var r = new Rig(700, Immediate);
        r.Fetch.Begin();
        for (int guard = 0; guard < 100000 && !r.Fetch.Done; guard++)
        {
            r.Now += TimeSpan.FromMilliseconds(16);
            r.Fetch.Tick();
            if (r.Fetch.Preparing) continue;
            if (r.Asked.Count >= 703) break;
        }
        int Rank(InventoryFolderData f) =>
            r.Model.IsInLibrary(f.Id) ? (f.Id == r.Model.LibraryRootId ? 40 : 50)
            : f.Id == r.Model.RootId ? 0
            : f.Kind == FolderKind.CurrentOutfit ? 1
            : f.Kind == FolderKind.MyOutfits ? 2
            : r.Model.IsSystemFolder(f.Id) ? 10
            : 20;
        var expected = r.Model.AllFolders().OrderBy(Rank).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Id).Select(f => f.Id).ToList();
        Assert.Equal(703, expected.Count);
        Assert.Equal(expected, r.Asked);
    }

    [Fact]
    public void Preparing_starts_shortly_before_the_start_delay_ends_and_not_during_the_first_frames()
    {
        var r = new Rig(2000, new BackgroundFetchOptions(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250), 2));
        r.Fetch.Begin();
        for (int s = 1; s <= 10; s++)
        {
            r.Now = TimeSpan.FromSeconds(s);
            Assert.Equal(0, r.Fetch.Tick());
            Assert.Equal(0, r.Fetch.LastTickLookups);
            Assert.False(r.Fetch.Preparing);
        }
        r.Now = TimeSpan.FromSeconds(19);
        r.Fetch.Tick();
        Assert.True(r.Fetch.Preparing, "the queue was not being prepared one second before the delay ended");
        Assert.Empty(r.Asked);

        // once prepared, the first folder goes out in the first tick after the delay
        for (int guard = 0; guard < 1000 && r.Fetch.Preparing; guard++) { r.Now += TimeSpan.FromMilliseconds(16); r.Fetch.Tick(); }
        Assert.True(r.Now < TimeSpan.FromSeconds(20), "the queue was not ready before the delay ended");
        r.Now = TimeSpan.FromSeconds(20);
        Assert.Equal(1, r.Fetch.Tick());
    }
}
