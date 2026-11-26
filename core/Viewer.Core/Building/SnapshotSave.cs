// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Building;

public enum SnapshotSaveState { Idle, AskingPrice, Priced, Uploading, Saved, Refused }

/// <summary>
/// Saving a snapshot to the inventory: the price is asked of the grid and shown, nothing is sent until the person agrees, and a
/// refusal keeps the grid's own words. One picture at a time.
/// </summary>
public sealed class SnapshotSave
{
    private readonly ITextureUploadService _service;
    private readonly Guid _folder;
    private readonly Func<DateTimeOffset> _clock;

    public SnapshotSave(ITextureUploadService service, Guid folderId, Func<DateTimeOffset>? clock = null)
    {
        _service = service;
        _folder = folderId;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private UploadBatch? _batch;
    private readonly object _gate = new();   // Cancel comes from the interface's thread, the grid's answers from another

    public SnapshotSaveState State { get; private set; }

    /// <summary>The fee the grid stated for this picture, once it has.</summary>
    public int? Price { get; private set; }

    /// <summary>Why it was refused: the grid's own words, or one of the viewer's short codes.</summary>
    public string? Reason { get; private set; }

    public Guid ItemId { get; private set; }

    /// <summary>Whether a reason means no answer came, rather than a refusal: the upload may then have happened all the same.</summary>
    public static bool Unanswered(string reason) => reason is "failed" or "no_answer" || reason.EndsWith("Exception", StringComparison.Ordinal);

    /// <summary>The new item's name: when the picture was taken.</summary>
    public static string NameFor(DateTime taken) => "Snapshot " + taken.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Asks the grid what saving this picture costs. Nothing of the picture is sent.</summary>
    public async Task AskAsync(UploadJob job, CancellationToken ct)
    {
        if (State is SnapshotSaveState.AskingPrice or SnapshotSaveState.Uploading)
            return;
        Price = null;
        Reason = null;
        ItemId = default;
        if (_folder == Guid.Empty)
        {
            Refuse("no_folder");
            return;
        }
        var batch = _batch = new UploadBatch(_service, _folder, [job], _clock);
        State = SnapshotSaveState.AskingPrice;
        await batch.CheckPriceAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_batch != batch)
                return;   // cancelled meanwhile
            if (batch.PricePerItem is { } price)
            {
                Price = price;
                State = SnapshotSaveState.Priced;
            }
            else
                Refuse(batch.PriceError);
        }
    }

    /// <summary>Uploads the picture at the price shown; does nothing unless a price is shown.</summary>
    public async Task ConfirmAsync(CancellationToken ct)
    {
        if (State != SnapshotSaveState.Priced || _batch is not { } batch)
            return;
        State = SnapshotSaveState.Uploading;
        await batch.RunAsync(ct).ConfigureAwait(false);
        var done = batch.States[0];
        if (done.State == UploadItemState.Done)
        {
            ItemId = done.ItemId;
            State = SnapshotSaveState.Saved;
        }
        else
            Refuse(done.Error);
    }

    /// <summary>Drops a question not yet agreed to. An upload under way goes on.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (State == SnapshotSaveState.Uploading)
                return;
            _batch = null;
            Price = null;
            Reason = null;
            State = SnapshotSaveState.Idle;
        }
    }

    private void Refuse(string? reason)
    {
        Reason = reason ?? "refused";
        State = SnapshotSaveState.Refused;
    }
}
