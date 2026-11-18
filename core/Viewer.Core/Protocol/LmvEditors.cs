// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;

namespace Viewer.Core.Protocol;

/// <summary>
/// Saving notecards and gestures, making new inventory items, and gestures on the grid, the protocol adapter's side.
/// <list type="bullet">
/// <item>A notecard is saved as the official viewer saves one (llpreviewnotecard.cpp saveIfNeeded; llviewerassetupload.cpp
/// LLBufferedAssetUploadInfo::generatePostBody): a POST to UpdateNotecardAgentInventory with item_id, or to UpdateNotecardTaskInventory with
/// task_id and item_id for a card inside an object, whose answer names an uploader the file is then posted to; the second answer's
/// "state" is "complete" and "new_asset" the stored file. A gesture goes the same way through UpdateGestureAgentInventory.</item>
/// <item>A new item is made with the inventory's create message (create_inventory_item in llviewerinventory.cpp): no transaction for a
/// script, notecard or gesture, which the grid fills with its default; a wearable's file is uploaded first and the item made from that
/// upload's transaction (create_inventory_wearable).</item>
/// </list>
/// </summary>
internal sealed partial class LmvTransport : INotecardSaveTransport, IItemCreateTransport, IGestureTransport
{
    internal const string NotecardAgentCapability = "UpdateNotecardAgentInventory", NotecardTaskCapability = "UpdateNotecardTaskInventory",
        GestureAgentCapability = "UpdateGestureAgentInventory";
    private static readonly TimeSpan EditorWait = TimeSpan.FromSeconds(30);

    /// <summary>The first request's body: item_id, and task_id for an item inside an object.</summary>
    internal static OSDMap UploadBody(Guid itemId, Guid objectId)
    {
        var body = new OSDMap { ["item_id"] = OSD.FromUUID(new UUID(itemId)) };
        if (objectId != Guid.Empty)
            body["task_id"] = OSD.FromUUID(new UUID(objectId));
        return body;
    }

    /// <summary>The uploader's answer as a save result: saved only when "state" is "complete".</summary>
    internal static NotecardSaveResult ParseUploadAnswer(OSD answer)
    {
        if (answer is not OSDMap m)
            return NotecardSaveResult.Of(NotecardSaveStatus.Failed, "no_answer");
        if (m["state"].AsString() != "complete")
            return NotecardSaveResult.Of(NotecardSaveStatus.Failed, m.ContainsKey("message") ? m["message"].AsString() : "refused");
        return NotecardSaveResult.Saved(m["new_asset"].AsUUID().Guid);
    }

    public Task<NotecardSaveResult> SaveNotecardAsync(NotecardAccess access, byte[] file, CancellationToken ct) =>
        UploadAsync(access.ObjectId == Guid.Empty ? NotecardAgentCapability : NotecardTaskCapability, access.ItemId, access.ObjectId, file, "notecard", ct);

    public Task<NotecardSaveResult> SaveGestureAsync(Guid itemId, byte[] file, CancellationToken ct) =>
        UploadAsync(GestureAgentCapability, itemId, Guid.Empty, file, "gesture", ct);

    private async Task<NotecardSaveResult> UploadAsync(string capability, Guid itemId, Guid objectId, byte[] file, string what, CancellationToken ct)
    {
        Uri? cap;
        try { cap = _client.Network.CurrentSim?.Caps?.CapabilityURI(capability); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { cap = null; }
        if (cap is null)
            return NotecardSaveResult.Of(NotecardSaveStatus.NoService);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(EditorWait * 2);
        try
        {
            var (_, data) = await _client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, UploadBody(itemId, objectId), limit.Token).ConfigureAwait(false);
            if (data is null || data.Length > UploadAnswerBytes || ScriptUploader(OSDParser.Deserialize(data)) is not { } uploader)
                return NotecardSaveResult.Of(NotecardSaveStatus.Failed, "no_uploader");
            var (_, answer) = await _client.HttpCapsClient.PostAsync(uploader, "application/octet-stream", file, limit.Token).ConfigureAwait(false);
            if (answer is null || answer.Length > UploadAnswerBytes)
                return NotecardSaveResult.Of(NotecardSaveStatus.Failed, "no_answer");
            var result = ParseUploadAnswer(OSDParser.Deserialize(answer));
            // the inventory item now holds the new file (llviewerassetupload.cpp finishUpload updates the item's asset)
            if (result.Status == NotecardSaveStatus.Saved && objectId == Guid.Empty && result.NewAssetId != Guid.Empty && Stored(itemId) is InventoryItem item)
                item.AssetUUID = new UUID(result.NewAssetId);
            _log.Info(what + ".saved", CoreLog.F("in_object", objectId != Guid.Empty), CoreLog.F("status", result.Status.ToString()));
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn(what + ".save-failed", CoreLog.F("error", ex.GetType().Name));
            return NotecardSaveResult.Of(NotecardSaveStatus.Failed, "failed");
        }
    }

    public NotecardEmbeddedItem? EmbeddableItem(Guid itemId)
    {
        if (Stored(itemId) is not InventoryItem item || item.AssetType is AssetType.Link or AssetType.LinkFolder)
            return null;
        var p = item.Permissions;
        return new NotecardEmbeddedItem(0, item.UUID.Guid, item.AssetUUID.Guid, item.Name, item.Description ?? "", (int)item.AssetType, (int)item.InventoryType,
            InventoryKinds.ItemKindOf((int)item.AssetType),
            new NotecardPermissions(item.CreatorID.Guid, item.OwnerID.Guid, (uint)p.BaseMask, (uint)p.OwnerMask, (uint)p.GroupMask, (uint)p.EveryoneMask, (uint)p.NextOwnerMask));
    }

    public bool SetItemDescription(Guid itemId, string description)
    {
        if (_disposed || !_client.Network.Connected || Stored(itemId) is not InventoryItem item)
            return false;
        item.Description = description;
        _client.Inventory.RequestUpdateItem(item);
        return true;
    }

    public async Task<Guid> CreateItemAsync(NewItemRequest r, CancellationToken ct)
    {
        if (_disposed || !_client.Network.Connected)
            return Guid.Empty;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(EditorWait * 2);
            var transaction = UUID.Zero;
            if (r.Data is { Length: > 0 } data)
            {
                transaction = UUID.Random();
                await _client.Assets.RequestUploadAsync((AssetType)r.AssetType, data, false, transaction, limit.Token).ConfigureAwait(false);
            }
            var made = await _client.Inventory.CreateItemAsync(new UUID(r.FolderId), r.Name, r.Description, (AssetType)r.AssetType, transaction,
                (InventoryType)r.InventoryType, r.Wearable == Viewer.Core.Inventory.WearableType.None ? LibreMetaverse.WearableType.Invalid : (LibreMetaverse.WearableType)(int)r.Wearable,
                (PermissionMask)r.NextOwnerMask, limit.Token).ConfigureAwait(false);
            Guid id = made?.UUID.Guid ?? Guid.Empty;
            _log.Info("inventory.item-created", CoreLog.F("type", r.AssetType), CoreLog.F("ok", id != Guid.Empty));
            return id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("inventory.create-failed", CoreLog.F("error", ex.GetType().Name));
            return Guid.Empty;
        }
    }

    public Guid FolderForAssetType(int assetType)
    {
        try { return _client.Inventory.FindFolderForType((AssetType)assetType).Guid; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Guid.Empty; }
    }

    public IReadOnlyList<(Guid ItemId, Guid AssetId)> ActiveGestures() =>
        _client.Self.ActiveGestures.Select(kv => (kv.Key.Guid, kv.Value.Guid)).ToArray();

    public bool SetGestureActive(Guid itemId, Guid assetId, bool active)
    {
        if (_disposed || !_client.Network.Connected)
            return false;
        if (active)
            _client.Self.ActivateGesture(new UUID(itemId), new UUID(assetId));
        else
            _client.Self.DeactivateGesture(new UUID(itemId));
        return true;
    }

    public Guid GestureAssetOf(Guid itemId) => Stored(itemId) is InventoryItem { AssetType: AssetType.Gesture } g ? g.AssetUUID.Guid : Guid.Empty;

    public async Task<AssetFetchResult> FetchGestureAsync(Guid assetId, CancellationToken ct)
    {
        if (_disposed || !_client.Network.Connected || assetId == Guid.Empty)
            return AssetFetchResult.Of(AssetFetchStatus.Failed);
        Uri? cap;
        try { cap = _client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { cap = null; }
        if (cap is null)
            return AssetFetchResult.Of(AssetFetchStatus.Failed);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(EditorWait);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(cap + (cap.Query.Length > 0 ? "&" : "?") + "gesture_id=" + assetId.ToString("D")));
            using var response = await _client.HttpCapsClient.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return AssetFetchResult.Of(response.StatusCode == System.Net.HttpStatusCode.NotFound ? AssetFetchStatus.NotFound : AssetFetchStatus.Failed);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
            return bytes.Length is > 0 and <= GestureFormat.MaxBytes ? AssetFetchResult.Ok(bytes) : AssetFetchResult.Of(AssetFetchStatus.NotFound);
        }
        catch (OperationCanceledException)
        {
            return AssetFetchResult.Of(ct.IsCancellationRequested ? AssetFetchStatus.Cancelled : AssetFetchStatus.Failed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("gesture.fetch-failed", CoreLog.F("error", ex.GetType().Name));
            return AssetFetchResult.Of(AssetFetchStatus.Failed);
        }
    }

    public bool TriggerSound(Guid sound)
    {
        if (_disposed || !_client.Network.Connected || sound == Guid.Empty)
            return false;
        _client.Sound.SendSoundTrigger(new UUID(sound), _client.Self.SimPosition, 1f);
        return true;
    }

    public async Task PlayGestureAsync(Guid assetId, CancellationToken ct)
    {
        if (_disposed || !_client.Network.Connected || assetId == Guid.Empty)
            return;
        try
        {
            await _client.Self.PlayGestureAsync(new UUID(assetId), ct).ConfigureAwait(false);
            _log.Info("gesture.played");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("gesture.play-failed", CoreLog.F("error", ex.GetType().Name));
        }
    }
}
