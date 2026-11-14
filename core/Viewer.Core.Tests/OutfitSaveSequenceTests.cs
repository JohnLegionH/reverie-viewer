// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Saving the look as an outfit: the links are made only after the grid has confirmed the new folder (never on a timer), a
/// read-back that is slow is waited for without making a link twice, a link the grid lost is made again, and a save that still
/// lacks links fails with a reason. A fake grid answers; no network.
/// </summary>
public class OutfitSaveSequenceTests
{
    private static readonly Guid Root = Guid.Parse("00000000-0000-4000-8000-000000000001");
    private static readonly Guid Mine = Guid.Parse("00000000-0000-4000-8000-0000000000e2");
    private static readonly Guid NewFolder = Guid.Parse("00000000-0000-4000-8000-0000000000f1");
    private static readonly Guid NewMine = Guid.Parse("00000000-0000-4000-8000-0000000000f2");

    private static readonly SaveLink[] Links =
    {
        new(Guid.Parse("00000000-0000-4000-8000-0000000000a1"), ""),
        new(Guid.Parse("00000000-0000-4000-8000-0000000000a2"), "@400"),
        new(Guid.Parse("00000000-0000-4000-8000-0000000000a3"), ""),
    };

    /// <summary>A grid that answers as told. Every call is recorded in order.</summary>
    private sealed class FakeGrid : IOutfitFolderOps
    {
        public readonly List<string> Calls = new();
        public readonly List<IReadOnlyList<SaveLink>> LinkBatches = new();
        public TaskCompletionSource<Guid>? FolderConfirmation;   // when set, the first folder is confirmed only when the test completes it
        public Guid FolderToReturn = NewFolder;
        public int ReadsBeforeLinksShow;                          // reads that still show nothing after the links were made
        public int BatchesToLose;                                 // how many link batches the grid loses
        public bool LoseAlways;
        private readonly HashSet<Guid> _held = new();
        private int _reads;
        private int _batches;
        private bool _firstFolder = true;

        public Task<Guid> CreateFolderAsync(Guid parent, string name, bool isMyOutfits)
        {
            lock (Calls) Calls.Add($"folder:{(isMyOutfits ? "my" : "outfit")}:{parent}");
            if (isMyOutfits) return Task.FromResult(NewMine);
            if (_firstFolder && FolderConfirmation is { } tcs) { _firstFolder = false; return tcs.Task; }
            return Task.FromResult(FolderToReturn);
        }

        public Task CreateLinksAsync(Guid folder, IReadOnlyList<SaveLink> links)
        {
            lock (Calls) { Calls.Add($"links:{folder}:{links.Count}"); LinkBatches.Add(links); }
            _batches++;
            bool lost = LoseAlways || _batches <= BatchesToLose;
            if (!lost) foreach (var l in links) _held.Add(l.Target);
            return Task.CompletedTask;
        }

        public Task<IReadOnlySet<Guid>> ReadLinkTargetsAsync(Guid folder)
        {
            lock (Calls) Calls.Add("read");
            _reads++;
            IReadOnlySet<Guid> shown = _reads > ReadsBeforeLinksShow ? new HashSet<Guid>(_held) : new HashSet<Guid>();
            return Task.FromResult(shown);
        }
    }

    // A wait that returns at once, so the tests do not sit through the sequence's own spacing.
    private static Task NoWait(TimeSpan _) => Task.CompletedTask;

    [Fact]
    public async Task Links_are_made_only_after_the_grid_confirms_the_folder()
    {
        var grid = new FakeGrid { FolderConfirmation = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var run = new OutfitSaveSequence(grid, NoWait).RunAsync(Root, Mine, "Look", Links);

        // a window in which nothing may happen: longer than the 700 ms the earlier fixed wait allowed
        await Task.Delay(1500);
        lock (grid.Calls) Assert.DoesNotContain(grid.Calls, c => c.StartsWith("links:"));

        grid.FolderConfirmation.SetResult(NewFolder);
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(outcome.Ok);
        Assert.Equal(NewFolder, outcome.Folder);
        Assert.Equal(new[] { $"links:{NewFolder}:3" }, grid.Calls.Where(c => c.StartsWith("links:")).ToArray());
    }

    [Fact]
    public async Task A_folder_the_grid_cannot_make_fails_with_a_reason_and_makes_no_links()
    {
        var grid = new FakeGrid { FolderToReturn = Guid.Empty };
        var outcome = await new OutfitSaveSequence(grid, NoWait).RunAsync(Root, Mine, "Look", Links).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(outcome.Ok);
        Assert.Equal("failed", outcome.Reason);
        Assert.DoesNotContain(grid.Calls, c => c.StartsWith("links:"));
    }

    [Fact]
    public async Task A_slow_read_back_is_waited_for_and_no_link_is_made_twice()
    {
        var grid = new FakeGrid { ReadsBeforeLinksShow = 3 };
        var outcome = await new OutfitSaveSequence(grid, NoWait).RunAsync(Root, Mine, "Look", Links).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(outcome.Ok);
        Assert.Single(grid.LinkBatches);
        Assert.True(grid.Calls.Count(c => c == "read") >= 4, "the read-back was not repeated while the folder still looked empty");
    }

    [Fact]
    public async Task Links_the_grid_lost_are_made_again_and_only_the_missing_ones()
    {
        var grid = new FakeGrid { BatchesToLose = 1 };
        var outcome = await new OutfitSaveSequence(grid, NoWait).RunAsync(Root, Mine, "Look", Links).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(outcome.Ok);
        Assert.Equal(2, grid.LinkBatches.Count);
        Assert.Equal(Links.Select(l => l.Target), grid.LinkBatches[1].Select(l => l.Target));
    }

    [Fact]
    public async Task Links_that_stay_missing_fail_with_links_missing_and_name_the_folder()
    {
        var grid = new FakeGrid { LoseAlways = true };
        var outcome = await new OutfitSaveSequence(grid, NoWait).RunAsync(Root, Mine, "Look", Links).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(outcome.Ok);
        Assert.Equal("links_missing", outcome.Reason);
        Assert.Equal(NewFolder, outcome.Folder);
    }

    [Fact]
    public async Task A_missing_My_Outfits_folder_is_made_and_confirmed_before_the_outfit_folder_goes_in_it()
    {
        var grid = new FakeGrid();
        var outcome = await new OutfitSaveSequence(grid, NoWait).RunAsync(Root, Guid.Empty, "Look", Links).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(outcome.Ok);
        Assert.Equal(new[] { $"folder:my:{Root}", $"folder:outfit:{NewMine}" }, grid.Calls.Where(c => c.StartsWith("folder:")).ToArray());
    }
}
