// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Viewer.Core.Building;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Saving a snapshot to the inventory: the price first, the upload only when asked for, the grid's reason on a refusal.</summary>
public class SnapshotSaveTests
{
    private sealed class FakeGrid : ITextureUploadService
    {
        public List<UploadRequest> Requests { get; } = new();
        public List<byte[]> Sent { get; } = new();
        public int Fee;
        public string? RequestError;
        public string? SendError;
        public static readonly Guid Item = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

        public Task<UploadTicket> RequestAsync(UploadRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(RequestError is null ? new UploadTicket(new Uri("http://grid.example.org/up"), Fee, null) : new UploadTicket(null, 0, RequestError));
        }

        public Task<UploadDone> SendAsync(Uri uploader, byte[] data, CancellationToken ct)
        {
            Sent.Add(data);
            return Task.FromResult(SendError is null ? new UploadDone(Guid.NewGuid(), Item, null) : new UploadDone(Guid.Empty, Guid.Empty, SendError));
        }
    }

    private static readonly Guid Folder = Guid.Parse("00000000-0000-0000-0000-0000000000f0");
    private static UploadJob Job() => new("Snapshot", "", () => [1, 2, 3]);

    [Fact]
    public async Task The_price_is_shown_and_nothing_is_sent_before_it_is_agreed_to()
    {
        var grid = new FakeGrid { Fee = 10 };
        var save = new SnapshotSave(grid, Folder);
        await save.AskAsync(Job(), default);
        Assert.Equal((SnapshotSaveState.Priced, (int?)10), (save.State, save.Price));
        Assert.Empty(grid.Sent);
        Assert.Equal(Folder, Assert.Single(grid.Requests).FolderId);
    }

    [Fact]
    public async Task Agreeing_uploads_once_and_gives_the_new_item()
    {
        var grid = new FakeGrid();
        var save = new SnapshotSave(grid, Folder);
        await save.AskAsync(Job(), default);
        await save.ConfirmAsync(default);
        await save.ConfirmAsync(default);
        Assert.Equal((SnapshotSaveState.Saved, FakeGrid.Item), (save.State, save.ItemId));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(grid.Sent));
    }

    [Fact]
    public async Task A_refused_price_shows_the_grids_reason()
    {
        var grid = new FakeGrid { RequestError = "You do not have enough money." };
        var save = new SnapshotSave(grid, Folder);
        await save.AskAsync(Job(), default);
        Assert.Equal((SnapshotSaveState.Refused, "You do not have enough money."), (save.State, save.Reason));
        await save.ConfirmAsync(default);
        Assert.Empty(grid.Sent);
    }

    [Fact]
    public async Task A_refused_upload_shows_the_grids_reason()
    {
        var grid = new FakeGrid { SendError = "The asset was rejected." };
        var save = new SnapshotSave(grid, Folder);
        await save.AskAsync(Job(), default);
        await save.ConfirmAsync(default);
        Assert.Equal((SnapshotSaveState.Refused, "The asset was rejected."), (save.State, save.Reason));
    }

    [Fact]
    public async Task After_cancelling_nothing_is_sent()
    {
        var grid = new FakeGrid();
        var save = new SnapshotSave(grid, Folder);
        await save.AskAsync(Job(), default);
        save.Cancel();
        await save.ConfirmAsync(default);
        Assert.Equal(SnapshotSaveState.Idle, save.State);
        Assert.Empty(grid.Sent);
    }

    [Fact]
    public async Task Without_a_folder_nothing_is_asked_of_the_grid()
    {
        var grid = new FakeGrid();
        var save = new SnapshotSave(grid, Guid.Empty);
        await save.AskAsync(Job(), default);
        Assert.Equal((SnapshotSaveState.Refused, "no_folder"), (save.State, save.Reason));
        Assert.Empty(grid.Requests);
    }

    [Theory]
    [InlineData("failed", true)]
    [InlineData("no_answer", true)]
    [InlineData("HttpRequestException", true)]
    [InlineData("You do not have enough money.", false)]
    [InlineData("no_folder", false)]
    [InlineData("price_changed", false)]
    public void A_lost_answer_is_told_from_a_refusal(string reason, bool unanswered) =>
        Assert.Equal(unanswered, SnapshotSave.Unanswered(reason));

    [Fact]
    public void The_item_is_named_with_the_time_the_picture_was_taken() =>
        Assert.Equal("Snapshot 2026-11-26 14:05:09", SnapshotSave.NameFor(new DateTime(2026, 11, 26, 14, 5, 9)));
}
