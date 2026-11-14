// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>
/// Building through the library: the create tool's box, rezzing from inventory, deleting to the Trash, and the texture and model uploads' two
/// capability steps (the official path: NewFileAgentInventory, then the uploader it names). The messages are built by <see cref="LmvBuild"/>.
/// </summary>
internal sealed partial class LmvTransport
{
    /// <summary>The capability of the upload's first step.</summary>
    internal const string UploadCapability = "NewFileAgentInventory";

    /// <summary>The longest each upload step is waited for.</summary>
    internal static readonly TimeSpan UploadStepWait = TimeSpan.FromSeconds(60);

    /// <summary>The largest answer an upload step may give.</summary>
    internal const int UploadAnswerBytes = 64 * 1024;

    private CommandDispatch ExecuteBuild(TransportCommand command)
    {
        if (_client.Network.CurrentSim is not { } sim)
            return CommandDispatch.NotSent("no_region");
        var self = _client.Self;
        switch (command)
        {
            case CreateObjectCommand create:
                _client.Network.SendPacket(LmvBuild.ObjectAdd(self.AgentID, self.SessionID, self.ActiveGroup, create.Placement), sim);
                break;
            case RezItemCommand rez:
            {
                if (_client.Inventory.Store is not { } store || !store.TryGetValue(new UUID(rez.ItemId), out var found) || found is not InventoryItem item)
                    return CommandDispatch.NotSent("no_item");
                var trash = _client.Inventory.FindFolderForType(FolderType.Trash);
                bool inTrash = InTrash(store, item, trash);
                bool copyable = (item.Permissions.OwnerMask & PermissionMask.Copy) != 0;
                // the item's folder is sent as null for an item in the Trash (lltooldraganddrop.cpp, behaviour)
                _client.Network.SendPacket(LmvBuild.RezObject(self.AgentID, self.SessionID, self.ActiveGroup, rez.Placement, item,
                    BuildRules.RezRemovesItem(copyable, inTrash), inTrash ? UUID.Zero : item.ParentUUID), sim);
                break;
            }
            case EditObjectCommand edit:
                return ExecuteEdit(sim, edit.Edit);
            case DeleteObjectCommand delete:
                _client.Network.SendPacket(LmvBuild.DeRez(self.AgentID, self.SessionID, delete.LocalId, BuildRules.DeRezTrash,
                    _client.Inventory.FindFolderForType(FolderType.Trash), UUID.Random()), sim);
                break;
        }
        return CommandDispatch.Handed;
    }

    private static bool InTrash(LibreMetaverse.Inventory store, InventoryBase item, UUID trash)
    {
        var at = item.ParentUUID;
        for (int depth = 0; depth < 64 && at != UUID.Zero; depth++)
        {
            if (at == trash)
                return true;
            if (!store.TryGetValue(at, out var parent))
                return false;
            at = parent.ParentUUID;
        }
        return false;
    }

    // ---------------- the texture upload ----------------

    public async Task<UploadTicket> RequestAsync(UploadRequest request, CancellationToken ct)
    {
        var cap = _client.Network.CurrentSim?.Caps?.CapabilityURI(UploadCapability);
        if (cap is null)
            return new UploadTicket(null, 0, "no_service");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(UploadStepWait);
        try
        {
            var (_, data) = await _client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, LmvBuild.UploadRequestBody(request), limit.Token).ConfigureAwait(false);
            if (data is null || data.Length > UploadAnswerBytes)
                return new UploadTicket(null, 0, "no_answer");
            return LmvBuild.ParseTicket(OSDParser.Deserialize(data));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("upload.request-failed", CoreLog.F("error", ex.GetType().Name));
            return new UploadTicket(null, 0, "failed");
        }
    }

    public async Task<UploadDone> SendAsync(Uri uploader, byte[] data, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(UploadStepWait);
        try
        {
            var (_, answer) = await _client.HttpCapsClient.PostAsync(uploader, "application/octet-stream", data, limit.Token).ConfigureAwait(false);
            if (answer is null || answer.Length > UploadAnswerBytes)
                return new UploadDone(Guid.Empty, Guid.Empty, "no_answer");
            var done = LmvBuild.ParseDone(OSDParser.Deserialize(answer));
            if (done.Ok)
            {
                // the new item shows in the inventory at once: it is fetched into the store, whose additions reach the inventory model
                try { await _client.Inventory.FetchItemAsync(new UUID(done.ItemId), _client.Self.AgentID, limit.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _log.Warn("upload.fetch-failed", CoreLog.F("error", ex.GetType().Name)); }
            }
            return done;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("upload.send-failed", CoreLog.F("error", ex.GetType().Name));
            return new UploadDone(Guid.Empty, Guid.Empty, "failed");
        }
    }

    // ---------------- the model upload and the account's prices ----------------

    /// <summary>The account's upload prices from the login reply, where the grid sends them (llagentbenefits.cpp reads the same keys).</summary>
    public UploadBenefits Benefits =>
        _client.Network.LoginResponseData?.AccountLevelBenefits is { } b
            ? new UploadBenefits(b.TextureUploadCost, b.LargeTextureUploadCost is { Count: > 0 } large ? large.ToArray() : null)
            : UploadBenefits.None;

    public async Task<ModelTicket> RequestModelAsync(ModelUploadPlan plan, CancellationToken ct)
    {
        var cap = _client.Network.CurrentSim?.Caps?.CapabilityURI(UploadCapability);
        if (cap is null)
            return new ModelTicket(null, 0, 0, 0, 0, 0, "no_service");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(UploadStepWait);
        try
        {
            var (_, data) = await _client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, LmvModelUpload.RequestBody(plan), limit.Token).ConfigureAwait(false);
            if (data is null || data.Length > UploadAnswerBytes)
                return new ModelTicket(null, 0, 0, 0, 0, 0, "no_answer");
            return LmvModelUpload.ParseTicket(OSDParser.Deserialize(data));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("upload.model-request-failed", CoreLog.F("error", ex.GetType().Name));
            return new ModelTicket(null, 0, 0, 0, 0, 0, "failed");
        }
    }

    public async Task<UploadDone> SendModelAsync(Uri uploader, ModelUploadPlan plan, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(UploadStepWait * 2);   // a model with its textures is larger than an image
        try
        {
            var (_, answer) = await _client.HttpCapsClient.PostAsync(uploader, OSDFormat.Xml, LmvModelUpload.SendBody(plan), limit.Token).ConfigureAwait(false);
            if (answer is null || answer.Length > UploadAnswerBytes)
                return new UploadDone(Guid.Empty, Guid.Empty, "no_answer");
            var done = LmvBuild.ParseDone(OSDParser.Deserialize(answer));
            if (done.Ok)
            {
                // the new object item shows in the inventory at once, as an image's does
                try { await _client.Inventory.FetchItemAsync(new UUID(done.ItemId), _client.Self.AgentID, limit.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _log.Warn("upload.fetch-failed", CoreLog.F("error", ex.GetType().Name)); }
            }
            return done;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("upload.model-send-failed", CoreLog.F("error", ex.GetType().Name));
            return new UploadDone(Guid.Empty, Guid.Empty, "failed");
        }
    }
}
