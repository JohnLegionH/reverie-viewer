// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>
/// Notecards, the protocol adapter's side: what an inventory notecard's permissions allow, fetching its file the way the official
/// viewer's asset storage does (indra/newview/llviewerassetstorage.cpp: an HTTP GET on the region's ViewerAsset capability with
/// "notecard_id"; a region without that capability is asked for the inventory item's asset over the older transfer), and copying an embedded item into
/// the inventory through the region's CopyInventoryFromNotecard capability with the body the official viewer posts
/// (indra/newview/llviewerinventory.cpp copy_inventory_from_notecard: notecard-id, object-id, item-id, folder-id, callback-id).
/// </summary>
internal sealed partial class LmvTransport : INotecardTransport
{
    /// <summary>The longest a notecard fetch is waited for.</summary>
    private static readonly TimeSpan NotecardWait = TimeSpan.FromSeconds(30);

    public NotecardAccess? NotecardAccessOf(Guid itemId)
    {
        if (Stored(itemId) is not InventoryItem { AssetType: AssetType.Notecard } item)
            return null;
        var p = item.Permissions;
        var agent = _client.Self.AgentID;
        return new NotecardAccess(itemId, item.AssetUUID.Guid, agent.Guid,
            new NotecardPermissions(item.CreatorID.Guid, item.OwnerID.Guid, (uint)p.BaseMask, (uint)p.OwnerMask, (uint)p.GroupMask, (uint)p.EveryoneMask, (uint)p.NextOwnerMask),
            InLibrary: item.OwnerID != agent);
    }

    public async Task<AssetFetchResult> FetchNotecardAsync(NotecardAccess access, CancellationToken ct)
    {
        if (_disposed || !_client.Network.Connected || access.AssetId == Guid.Empty)
            return AssetFetchResult.Of(AssetFetchStatus.Failed);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(NotecardWait);
            Uri? cap = null;
            try { cap = _client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { cap = null; }
            if (cap is not null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, NotecardAssetUrl(cap, access.AssetId));
                using var response = await _client.HttpCapsClient.SendAsync(request, limit.Token).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return AssetFetchResult.Of(AssetFetchStatus.NotFound);
                if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
                    return AssetFetchResult.Of(AssetFetchStatus.Denied);
                if (!response.IsSuccessStatusCode)
                {
                    _log.Warn("notecard.fetch-refused", CoreLog.F("status", (int)response.StatusCode));
                    return AssetFetchResult.Of(AssetFetchStatus.Failed);
                }
                byte[] data = await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
                return data.Length > 0 ? AssetFetchResult.Ok(data) : AssetFetchResult.Of(AssetFetchStatus.NotFound);
            }
            // a card inside an object is asked for with the object's id (llpreviewnotecard.cpp loadAsset, the task id)
            var asset = await _client.Assets.RequestAssetAsync(new UUID(access.AssetId), new UUID(access.ItemId), new UUID(access.ObjectId), AssetType.Notecard, true,
                SourceType.SimInventoryItem, new UUID(access.Permissions.Owner), limit.Token).ConfigureAwait(false);
            return asset?.AssetData is { Length: > 0 } bytes ? AssetFetchResult.Ok(bytes) : AssetFetchResult.Of(AssetFetchStatus.NotFound);
        }
        catch (OperationCanceledException)
        {
            return AssetFetchResult.Of(ct.IsCancellationRequested ? AssetFetchStatus.Cancelled : AssetFetchStatus.Failed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("notecard.fetch-failed", CoreLog.F("error", ex.GetType().Name));
            return AssetFetchResult.Of(AssetFetchStatus.Failed);
        }
    }

    /// <summary>The address of a notecard's file under the region's ViewerAsset capability.</summary>
    internal static Uri NotecardAssetUrl(Uri cap, Guid asset) => new(cap + (cap.Query.Length > 0 ? "&" : "?") + "notecard_id=" + asset.ToString("D"));

    /// <summary>The body of the copy request (no object: the card is in the agent's inventory; an empty folder lets the grid choose).</summary>
    internal static OSDMap CopyFromNotecardBody(CopyFromNotecardCommand c) => new()
    {
        ["notecard-id"] = OSD.FromUUID(new UUID(c.NotecardItemId)),
        ["object-id"] = OSD.FromUUID(UUID.Zero),
        ["item-id"] = OSD.FromUUID(new UUID(c.EmbeddedItemId)),
        ["folder-id"] = OSD.FromUUID(new UUID(c.FolderId)),
        ["callback-id"] = OSD.FromInteger(0),
    };

    private CommandDispatch ExecuteNotecard(CopyFromNotecardCommand copy)
    {
        Uri? url;
        try { url = _client.Network.CurrentSim?.Caps?.CapabilityURI("CopyInventoryFromNotecard"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { url = null; }
        if (url is null)
            return CommandDispatch.NotSent("no_capability");
        var body = CopyFromNotecardBody(copy);
        Run("notecard-copy", async () =>
        {
            await _client.HttpCapsClient.PostAsync(url, OSDFormat.Xml, body, CancellationToken.None).ConfigureAwait(false);
            _log.Info("notecard.copy-sent");
        });
        return CommandDispatch.Handed;
    }
}
