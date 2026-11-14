// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Appearance;

/// <summary>The grid-side steps of saving a wearable. The protocol adapter implements them; a test supplies a recording fake.</summary>
public interface IWearableSaveOps
{
    /// <summary>Makes a fresh link to the item in the current outfit folder with the old link's note and removes the old link, so the
    /// folder's version moves and the baking service sees a change. True also when the item is not worn (nothing to refresh).</summary>
    Task<bool> RefreshLinkAsync(Guid itemId);

    /// <summary>Uploads wearable file contents as a new asset. Returns the upload's transaction id (the empty id: failed).</summary>
    Task<Guid> UploadAsync(int assetType, byte[] data);

    /// <summary>Points the item at the asset uploaded under <paramref name="transactionId"/> and gives it <paramref name="name"/>.</summary>
    Task<bool> UpdateItemAsync(Guid itemId, Guid transactionId, string name);

    /// <summary>Copies an item into a folder under a name. Returns the copy's id (the empty id: failed).</summary>
    Task<Guid> CopyItemAsync(Guid sourceItemId, Guid folderId, string name);

    /// <summary>Replaces what is worn: the original's link leaves the current outfit folder and a link to the copy takes its place.</summary>
    Task<bool> SwapWornAsync(Guid originalItemId, Guid copyItemId);

    /// <summary>Asks the region's baking service to rebuild the look from the current outfit folder.</summary>
    Task<bool> RebakeAsync();
}

/// <summary>What to save: the item, the file contents, the name, and for Save As the folder the copy goes in.</summary>
public sealed record WearableSaveRequest(Guid ItemId, string Name, int AssetType, byte[] Data, bool AsCopy, Guid FolderId);

/// <summary>How a save ended. <paramref name="Saved"/>: the item now points at the new asset. <paramref name="Rebaked"/>: the baking
/// service accepted the rebuild request. <paramref name="Reason"/>: a short code when something failed.</summary>
public sealed record WearableSaveOutcome(bool Saved, bool Rebaked, string Reason, Guid ItemId);

/// <summary>Saves a wearable in the official viewer's order (indra/newview/llpaneleditwearable.cpp saveChanges, llagentwearables.cpp
/// saveWearable and saveWearableAs): a step that fails stops the sequence, and the rebuild request always comes last.</summary>
public static class WearableSaveSequence
{
    public static async Task<WearableSaveOutcome> RunAsync(IWearableSaveOps ops, WearableSaveRequest request)
    {
        if (request.ItemId == Guid.Empty)
            return new(false, false, "no_item", Guid.Empty);
        if (request.Data is not { Length: > 0 })
            return new(false, false, "no_data", request.ItemId);
        var item = request.ItemId;
        bool saved = false;
        try
        {
            if (request.AsCopy)
            {
                // Save As: the original is copied first (same folder as the original in the official viewer; the caller names the folder)
                item = await ops.CopyItemAsync(request.ItemId, request.FolderId, request.Name).ConfigureAwait(false);
                if (item == Guid.Empty)
                    return new(false, false, "copy_failed", Guid.Empty);
            }
            else if (!await ops.RefreshLinkAsync(item).ConfigureAwait(false))
                return new(false, false, "link_failed", item);

            var transaction = await ops.UploadAsync(request.AssetType, request.Data).ConfigureAwait(false);
            if (transaction == Guid.Empty)
                return new(false, false, "upload_failed", item);
            if (!await ops.UpdateItemAsync(item, transaction, request.Name).ConfigureAwait(false))
                return new(false, false, "update_failed", item);
            saved = true;

            if (request.AsCopy && !await ops.SwapWornAsync(request.ItemId, item).ConfigureAwait(false))
                return new(true, false, "wear_failed", item);
            return await ops.RebakeAsync().ConfigureAwait(false) ? new(true, true, "", item) : new(true, false, "rebuild_failed", item);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(saved, false, "failed", item);
        }
    }
}

/// <summary>A save has finished on the session (raised on the transport's thread).</summary>
public sealed record WearableSaveFinished(bool AsCopy, bool Saved, bool Rebaked, string Reason, Guid ItemId) : TransportEvent;
