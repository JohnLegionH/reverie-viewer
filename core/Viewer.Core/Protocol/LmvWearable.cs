// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Viewer.Core.Appearance;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>
/// The wearable side of the protocol adapter: reading a wearable asset, and saving an edited one the way the official viewer does
/// (indra/newview/llviewerwearable.cpp saveNewAsset, llagentwearables.cpp saveWearable): the file goes up as a new asset under a
/// transaction id, the inventory item is updated with that transaction, and the region's server-side baking is asked to rebuild the
/// look from the current outfit folder. The viewer never bakes or sends a look of its own.
/// </summary>
internal sealed partial class LmvTransport : IWearableTransport
{
    /// <summary>The longest an upload is waited for.</summary>
    private static readonly TimeSpan UploadWait = TimeSpan.FromSeconds(60);
    /// <summary>The grid's inventory API (its address is the capability's, the items are under it).</summary>
    private const string InventoryCapability = "InventoryAPIv3";
    /// <summary>How long the grid is asked whether an item took its new asset, and how often.</summary>
    private static readonly TimeSpan ItemConfirmWait = TimeSpan.FromSeconds(15), ItemConfirmEvery = TimeSpan.FromMilliseconds(750);

    public async Task<AssetFetchResult> FetchWearableAsync(Guid assetId, int assetType, CancellationToken ct)
    {
        if (_disposed || !_client.Network.Connected)
            return AssetFetchResult.Of(AssetFetchStatus.Failed);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(UploadWait);
            var asset = await _client.Assets.RequestAssetAsync(new UUID(assetId), (AssetType)assetType, true, limit.Token).ConfigureAwait(false);
            return asset?.AssetData is { Length: > 0 } bytes ? AssetFetchResult.Ok(bytes) : AssetFetchResult.Of(AssetFetchStatus.NotFound);
        }
        catch (OperationCanceledException)
        {
            return AssetFetchResult.Of(AssetFetchStatus.Cancelled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("wearable.fetch-failed", CoreLog.F("error", ex.GetType().Name));
            return AssetFetchResult.Of(AssetFetchStatus.Failed);
        }
    }

    /// <summary>The change to an item that makes it point at a wearable just uploaded: the upload's transaction id stands where the asset id
    /// would (the official viewer's form, indra/newview/llviewerinventory.cpp), and the name goes with it.</summary>
    internal static OSDMap ItemUpdateBody(Guid transaction, string name) => new() { ["name"] = OSD.FromString(name), ["hash_id"] = OSD.FromUUID(new UUID(transaction)) };

    /// <summary>The address of one item under the inventory capability.</summary>
    internal static Uri ItemUpdateUrl(Uri capability, Guid item) => new(capability.ToString().TrimEnd('/') + "/item/" + item.ToString("D"));

    /// <summary>The asset the grid says an item now points at, from its answer to the change (the empty id when the answer names none).</summary>
    internal static Guid ReadUpdatedAsset(byte[] answer)
    {
        try
        {
            return OSDParser.Deserialize(answer) is OSDMap map && map.TryGetValue("asset_id", out var id) ? id.AsUUID().Guid : Guid.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Guid.Empty;
        }
    }

    /// <summary>Whether the item's asset moved: a new id is a change; the same id may still be right (the same file again) and is checked by reading it; none cannot be told.</summary>
    /// <summary>The item as the older item update message sends it after a wearable save: the new name, and the upload's transaction
    /// on the item itself, which is where the region reads it.</summary>
    internal static InventoryItem LegacyItemUpdate(InventoryItem item, string name, Guid transactionId) => new(item.UUID);

    internal static ItemUpdateOutcome JudgeUpdate(Guid before, Guid after) =>
        after == Guid.Empty ? ItemUpdateOutcome.Unknown : after == before ? ItemUpdateOutcome.Same : ItemUpdateOutcome.Changed;

    public WearableItemInfo? ItemInfo(Guid itemId)
    {
        if (Stored(itemId) is not InventoryItem item)
            return null;
        uint owner = (uint)item.Permissions.OwnerMask;
        return new WearableItemInfo(item.AssetUUID.Guid, (int)item.AssetType, WearablePermissions.CanModify(owner), WearablePermissions.CanCopy(owner), WearablePermissions.CanTransfer(owner));
    }

    public Guid TextureAssetOf(Guid itemId) =>
        Stored(itemId) is InventoryItem { AssetType: AssetType.Texture } item ? item.AssetUUID.Guid : Guid.Empty;

    private void ExecuteWearableCommand(TransportCommand command)
    {
        var request = command switch
        {
            SaveWearableCommand s => new WearableSaveRequest(s.ItemId, s.Name, s.AssetType, s.Data, false, Guid.Empty),
            SaveWearableAsCommand a => new WearableSaveRequest(a.SourceItemId, a.Name, a.AssetType, a.Data, true, a.FolderId),
            _ => null,
        };
        if (request is null)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await WearableSaveSequence.RunAsync(new LmvWearableOps(this), request).ConfigureAwait(false);
                Raise(new WearableSaveFinished(request.AsCopy, outcome.Saved, outcome.Rebaked, outcome.Reason, outcome.ItemId));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("wearable.save-failed", CoreLog.F("error", ex.GetType().Name));
                Raise(new WearableSaveFinished(request.AsCopy, false, false, "failed", request.ItemId));
            }
        });
    }

    /// <summary>The library's side of <see cref="WearableSaveSequence"/>.</summary>
    private sealed class LmvWearableOps : IWearableSaveOps
    {
        private readonly LmvTransport _t;
        public LmvWearableOps(LmvTransport t) { _t = t; }

        private InventoryManager Inv => _t._client.Inventory;

        private UUID Cof() => Inv.FindFolderForType(FolderType.CurrentOutfit);

        /// <summary>The links in the current outfit folder, read from the grid first so the folder's contents and version are current.</summary>
        private async Task<(UUID Cof, List<CofLink> Links)> ReadLinksAsync()
        {
            var cof = Cof();
            var links = new List<CofLink>();
            if (cof == UUID.Zero)
                return (cof, links);
            await Inv.RequestFolderContentsAsync(cof, _t._client.Self.AgentID, true, true, InventorySortOrder.ByName, CancellationToken.None).ConfigureAwait(false);
            if (Inv.Store is { } store)
                foreach (var i in store.GetContents(cof).OfType<InventoryItem>())
                    if (i.AssetType == AssetType.Link)
                        links.Add(new CofLink(i.UUID.Guid, i.AssetUUID.Guid, i.Description ?? ""));
            return (cof, links);
        }

        public async Task<bool> RefreshLinkAsync(Guid itemId)
        {
            var (cof, links) = await ReadLinksAsync().ConfigureAwait(false);
            if (cof == UUID.Zero)
                return false;
            if (WearableLinks.LinkTo(links, itemId) is not { } link)
                return true;   // not worn: there is no link to refresh
            if (_t.Stored(itemId) is not { } target)
                return false;
            // a new link with the same note first, then the old one goes: the folder's version moves and the baking service sees a change
            await Inv.CreateLinksAsync(cof, new List<(InventoryBase, string)> { (target, link.Note) }, null, CancellationToken.None).ConfigureAwait(false);
            await _t.RemoveLinkAsync(link.LinkId).ConfigureAwait(false);
            return true;
        }

        public async Task<Guid> UploadAsync(int assetType, byte[] data)
        {
            _uploaded = data;
            var transaction = UUID.Random();
            using var limit = new CancellationTokenSource(UploadWait);
            await _t._client.Assets.RequestUploadAsync((AssetType)assetType, data, false, transaction, limit.Token).ConfigureAwait(false);
            return transaction.Guid;
        }

        private byte[] _uploaded = [];

        public async Task<bool> UpdateItemAsync(Guid itemId, Guid transactionId, string name)
        {
            if (_t.Stored(itemId) is not InventoryItem item)
                return false;
            var before = item.AssetUUID.Guid;
            var parent = item.ParentUUID;
            var capability = _t._client.Network.CurrentSim?.Caps?.CapabilityURI(InventoryCapability);
            Guid after;
            if (capability is not null)
            {
                // the grid's inventory API, as the official viewer asks it: the change to the item names the upload's transaction
                using var limit = new CancellationTokenSource(UploadWait);
                using var request = new HttpRequestMessage(HttpMethod.Patch, ItemUpdateUrl(capability, itemId)) { Content = new ByteArrayContent(OSDParser.SerializeLLSDXmlBytes(ItemUpdateBody(transactionId, name))) };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/llsd+xml");
                using var response = await _t._client.HttpCapsClient.SendAsync(request, limit.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _t._log.Warn("wearable.item-update-refused", CoreLog.F("status", (int)response.StatusCode));
                    return false;
                }
                after = ReadUpdatedAsset(await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false));
            }
            else
            {
                // a grid without that API: the older item update message, with the transaction; the grid's own copy of the item is then read until it names a new asset
                var update = new InventoryItem(item.UUID)
                {
                    ParentUUID = item.ParentUUID, Name = name, Description = item.Description, AssetType = item.AssetType, InventoryType = item.InventoryType, AssetUUID = item.AssetUUID,
                    Permissions = item.Permissions, OwnerID = item.OwnerID, CreatorID = item.CreatorID, GroupID = item.GroupID, GroupOwned = item.GroupOwned,
                    SaleType = item.SaleType, SalePrice = item.SalePrice, Flags = item.Flags, CreationDate = item.CreationDate,
                };
                Inv.RequestUpdateItems(new List<InventoryItem> { update }, new UUID(transactionId));
                after = Guid.Empty;
                var until = DateTime.UtcNow + ItemConfirmWait;
                while (DateTime.UtcNow < until && !_t._disposed && after == Guid.Empty)
                {
                    await Task.Delay(ItemConfirmEvery).ConfigureAwait(false);
                    await Inv.RequestFolderContentsAsync(parent, _t._client.Self.AgentID, true, true, InventorySortOrder.ByName, CancellationToken.None).ConfigureAwait(false);
                    if (_t.Stored(itemId) is InventoryItem now && now.AssetUUID.Guid != before)
                        after = now.AssetUUID.Guid;
                }
            }
            // the viewer's own copy of the folder follows what the grid holds now
            await Inv.RequestFolderContentsAsync(parent, _t._client.Self.AgentID, true, true, InventorySortOrder.ByName, CancellationToken.None).ConfigureAwait(false);
            switch (JudgeUpdate(before, after))
            {
                case ItemUpdateOutcome.Changed:
                    return true;
                case ItemUpdateOutcome.Same:
                {
                    // the same asset id after a save: right only when it is the file just sent (the same file again), so read it back
                    var read = await _t.FetchWearableAsync(after, (int)item.AssetType, CancellationToken.None).ConfigureAwait(false);
                    return read.Status == AssetFetchStatus.Ok && read.Bytes.Span.SequenceEqual(_uploaded);
                }
                default:
                    return false;
            }
        }

        public async Task<Guid> CopyItemAsync(Guid sourceItemId, Guid folderId, string name)
        {
            if (_t.Stored(sourceItemId) is not InventoryItem source)
                return Guid.Empty;
            var made = await Inv.RequestCopyItemAsync(source.UUID, new UUID(folderId), name, source.OwnerID, CancellationToken.None).ConfigureAwait(false);
            if (made is null || made.UUID == UUID.Zero)
                return Guid.Empty;
            if (Inv.Store is { } store && !store.Contains(made.UUID))
                store.UpdateNodeFor(made);
            return made.UUID.Guid;
        }

        public async Task<bool> SwapWornAsync(Guid originalItemId, Guid copyItemId)
        {
            var (cof, links) = await ReadLinksAsync().ConfigureAwait(false);
            if (cof == UUID.Zero || WearableLinks.LinkTo(links, originalItemId) is not { } link || _t.Stored(copyItemId) is not { } copy)
                return false;
            await Inv.CreateLinksAsync(cof, new List<(InventoryBase, string)> { (copy, link.Note) }, null, CancellationToken.None).ConfigureAwait(false);
            await _t.RemoveLinkAsync(link.LinkId).ConfigureAwait(false);
            return true;
        }

        public async Task<bool> RebakeAsync()
        {
            var cof = Cof();
            if (cof == UUID.Zero)
                return false;
            var result = await _t.RequestRebuildAsync(cof).ConfigureAwait(false);
            return result.Ok;
        }
    }
}

/// <summary>How the grid's answer to a wearable save compares with the item before it.</summary>
internal enum ItemUpdateOutcome { Changed, Same, Unknown }
