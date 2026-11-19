// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;

namespace Viewer.Core.Protocol;

/// <summary>
/// Scripts, the protocol adapter's side, as the official viewer does them (indra/newview/llpreviewscript.cpp, llviewerassetupload.cpp):
/// the text is fetched as the viewer's asset storage fetches an item's asset (indra/newview/llviewerassetstorage.cpp: an HTTP GET on the region's
/// ViewerAsset capability, "lsltext_id", when the item's asset is known; otherwise, and on a region without that capability, the inventory-item
/// transfer with owner, object and item ids, LLAssetStorage::getInvItemAsset), saved through the
/// region's UpdateScriptAgent capability (item_id, target) or UpdateScriptTask (task_id, item_id, is_script_running, target), each answering
/// with an uploader the text is then posted to (AssetInventoryUploadCoproc); the running state is asked with GetScriptRunning, whose answer
/// ScriptRunningReply comes on the event queue, set with SetScriptRunning and a script restarted with ScriptReset.
/// </summary>
internal sealed partial class LmvTransport : IScriptTransport
{
    internal const string ScriptAgentCapability = "UpdateScriptAgent", ScriptTaskCapability = "UpdateScriptTask";

    /// <summary>The longest a script fetch, each upload step or a running-state answer is waited for.</summary>
    private static readonly TimeSpan ScriptWait = TimeSpan.FromSeconds(30);

    public ScriptAccess? ScriptAccessOf(Guid itemId)
    {
        if (Stored(itemId) is not InventoryItem { AssetType: AssetType.LSLText } item)
            return null;
        var p = item.Permissions;
        var agent = _client.Self.AgentID;
        return new ScriptAccess(itemId, item.AssetUUID.Guid, agent.Guid,
            new NotecardPermissions(item.CreatorID.Guid, item.OwnerID.Guid, (uint)p.BaseMask, (uint)p.OwnerMask, (uint)p.GroupMask, (uint)p.EveryoneMask, (uint)p.NextOwnerMask),
            InLibrary: item.OwnerID != agent, LuaSubtype: (item.Flags & 0xFF) == 1,
            LslOnLuau: ((uint)item.Flags & ScriptAccess.LslOnLuauFlag) != 0, Runtime: _itemRuntimes.TryGetValue(itemId, out var runtime) ? runtime : "");
    }

    public async Task<AssetFetchResult> FetchScriptAsync(ScriptAccess access, CancellationToken ct)
    {
        if (_disposed || !_client.Network.Connected)
            return AssetFetchResult.Of(AssetFetchStatus.Failed);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(ScriptWait);
            Uri? cap = null;
            try { cap = access.AssetId == Guid.Empty ? null : _client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { cap = null; }
            if (cap is not null && ScriptAssetUrl(cap, access.AssetId) is { } url)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _client.HttpCapsClient.SendAsync(request, limit.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    byte[] data = await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
                    if (data.Length > 0)
                        return AssetFetchResult.Ok(data);
                }
                else if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
                    return AssetFetchResult.Of(AssetFetchStatus.Denied);
                _log.Info("script.fetch-cap", CoreLog.F("status", (int)response.StatusCode));   // the transfer is tried next
            }
            // the item's own transfer: the region checks the agent's rights on the item (and on the object for one inside it)
            var asset = await _client.Assets.RequestAssetAsync(new UUID(access.AssetId), new UUID(access.ItemId), new UUID(access.ObjectId), AssetType.LSLText, true,
                SourceType.SimInventoryItem, new UUID(access.Permissions.Owner), limit.Token).ConfigureAwait(false);
            return asset?.AssetData is { Length: > 0 } bytes ? AssetFetchResult.Ok(bytes) : AssetFetchResult.Of(AssetFetchStatus.NotFound);
        }
        catch (OperationCanceledException)
        {
            return AssetFetchResult.Of(ct.IsCancellationRequested ? AssetFetchStatus.Cancelled : AssetFetchStatus.Failed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("script.fetch-failed", CoreLog.F("error", ex.GetType().Name));
            return AssetFetchResult.Of(AssetFetchStatus.Failed);
        }
    }

    /// <summary>The address of a script's text under the region's ViewerAsset capability ("lsltext_id", as the notecard's "notecard_id").</summary>
    internal static Uri? ScriptAssetUrl(Uri cap, Guid asset) => new(cap + (cap.Query.Length > 0 ? "&" : "?") + "lsltext_id=" + asset.ToString("D"));

    /// <summary>The first request's body (llviewerassetupload.cpp LLScriptAssetUpload::generatePostBody): item_id and target for a script
    /// in the inventory; task_id, item_id, is_script_running and target for one inside an object.</summary>
    internal static OSDMap ScriptUploadBody(ScriptAccess access, ScriptTarget target, bool running)
    {
        var body = new OSDMap
        {
            ["item_id"] = OSD.FromUUID(new UUID(access.ItemId)),
        };
        if (!target.IsNone)
            body["target"] = OSD.FromString(target.ToString());   // no target known: none sent, and a region keeps the script's own
        if (access.InObject)
        {
            body["task_id"] = OSD.FromUUID(new UUID(access.ObjectId));
            body["is_script_running"] = OSD.FromBoolean(running);
        }
        return body;
    }

    /// <summary>The uploader named by the first answer ("uploader"), or null.</summary>
    internal static Uri? ScriptUploader(OSD answer) =>
        answer is OSDMap m && m["uploader"].AsString() is { Length: > 0 } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>
    /// The second answer as the editor's result. Saved only when "state" is "complete" (AssetInventoryUploadCoproc); "new_asset" is the stored
    /// text; "compiled" and "errors" as LLPreviewLSL::finishedLSLUpload and LLLiveLSLEditor's read them. OpenSim's answer for a script in the
    /// inventory has neither (it stores the text without compiling it): compiled is then unknown. A failure's "message" is its reason.
    /// </summary>
    internal static ScriptSaveResult ParseScriptAnswer(OSD answer)
    {
        if (answer is not OSDMap m)
            return ScriptSaveResult.Of(ScriptSaveStatus.Failed);
        if (m["state"].AsString() != "complete")
            return ScriptSaveResult.Of(ScriptSaveStatus.Failed, m.ContainsKey("message") ? m["message"].AsString() : null);
        bool? compiled = m.ContainsKey("compiled") ? m["compiled"].AsBoolean() : null;
        var messages = m["errors"] is OSDArray errors ? errors.Select(e => ScriptMessage.Parse(e.AsString())).ToArray() : [];
        if (compiled is null && messages.Length > 0)
            compiled = false;
        return new ScriptSaveResult(ScriptSaveStatus.Saved, compiled, m["new_asset"].AsUUID().Guid, messages);
    }

    public async Task<ScriptSaveResult> SaveScriptAsync(ScriptAccess access, byte[] text, ScriptTarget target, bool running, CancellationToken ct)
    {
        Uri? cap;
        try { cap = _client.Network.CurrentSim?.Caps?.CapabilityURI(access.InObject ? ScriptTaskCapability : ScriptAgentCapability); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { cap = null; }
        if (cap is null)
            return ScriptSaveResult.Of(ScriptSaveStatus.NoService);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ScriptWait * 2);
        try
        {
            var (_, data) = await _client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, ScriptUploadBody(access, target, running), limit.Token).ConfigureAwait(false);
            if (data is null || data.Length > UploadAnswerBytes || ScriptUploader(OSDParser.Deserialize(data)) is not { } uploader)
                return ScriptSaveResult.Of(ScriptSaveStatus.Failed, "no_uploader");
            var (_, answer) = await _client.HttpCapsClient.PostAsync(uploader, "application/octet-stream", text, limit.Token).ConfigureAwait(false);
            if (answer is null || answer.Length > UploadAnswerBytes)
                return ScriptSaveResult.Of(ScriptSaveStatus.Failed, "no_answer");
            var result = ParseScriptAnswer(OSDParser.Deserialize(answer));
            // the inventory item now holds the new text (llviewerassetupload.cpp finishUpload updates the item's asset)
            if (result.Status == ScriptSaveStatus.Saved && !access.InObject && result.NewAssetId != Guid.Empty && Stored(access.ItemId) is InventoryItem item)
                item.AssetUUID = new UUID(result.NewAssetId);
            _log.Info("script.saved", CoreLog.F("in_object", access.InObject), CoreLog.F("status", result.Status.ToString()), CoreLog.F("messages", result.Messages.Count));
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("script.save-failed", CoreLog.F("error", ex.GetType().Name));
            return ScriptSaveResult.Of(ScriptSaveStatus.Failed, "failed");
        }
    }

    private readonly LmvScriptReplyKeeper _scriptReplies = new();

    internal void NoteScriptReplies(OSD answer) => _scriptReplies.Note(answer);

    /// <summary>The runtime each inventory item named in an answer the tap has seen, by item.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _itemRuntimes = new();

    /// <summary>Keeps the runtimes an inventory answer names for its items (called by the tap before the library reads the answer).</summary>
    internal void NoteInventoryAnswer(OSD answer)
    {
        if (_itemRuntimes.Count > 20_000)
            _itemRuntimes.Clear();
        foreach (var (item, runtime) in LmvInventoryRuntime.Read(answer))
            _itemRuntimes[item] = runtime;
    }

    public async Task<ScriptRuntime?> GetScriptRunningAsync(ScriptAccess access, CancellationToken ct)
    {
        if (!access.InObject || _disposed || !_client.Network.Connected)
            return null;
        var done = new TaskCompletionSource<ScriptRuntime>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReply(object? sender, ScriptRunningReplyEventArgs e)
        {
            if (e.ObjectID.Guid != access.ObjectId || e.ScriptID.Guid != access.ItemId)
                return;
            // the tap saw the whole reply first; without it (no event-queue answer passed through it) the library's flags are all there is
            done.TrySetResult(_scriptReplies.Take(access.ObjectId, access.ItemId, () => new ScriptRuntime(e.IsRunning, e.IsMono, e.IsLuau, e.IsLuaLanguage)));
        }
        _client.Inventory.ScriptRunningReply += OnReply;
        try
        {
            _client.Inventory.RequestGetScriptRunning(new UUID(access.ObjectId), new UUID(access.ItemId));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(ScriptWait);
            return await done.Task.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        finally { _client.Inventory.ScriptRunningReply -= OnReply; }
    }

    internal const string ScriptEngineCapability = "SetScriptEngine";

    /// <summary>The body of the SetScriptEngine request: the object, the script item and the target; an empty target clears the choice.</summary>
    internal static OSDMap ScriptEngineBody(ScriptAccess access, ScriptTarget? target) => new()
    {
        ["task_id"] = OSD.FromUUID(new UUID(access.ObjectId)),
        ["item_id"] = OSD.FromUUID(new UUID(access.ItemId)),
        ["target"] = OSD.FromString(target?.ToString() ?? ""),
    };

    public ScriptRegion Region
    {
        get
        {
            try
            {
                var features = _client.Network.CurrentSim?.Features;
                bool lua = features?.Get("LuaScriptsEnabled") is { } l && l.AsBoolean();
                return ScriptRegion.From(LmvGridFeatures.Extras(features?.Get("OpenSimExtras")), lua);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return ScriptRegion.None;
            }
        }
    }

    public async Task<bool> SetScriptEngineAsync(ScriptAccess access, ScriptTarget? target, CancellationToken ct)
    {
        if (!access.InObject || _disposed || !_client.Network.Connected)
            return false;
        Uri? cap;
        try { cap = _client.Network.CurrentSim?.Caps?.CapabilityURI(ScriptEngineCapability); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { cap = null; }
        if (cap is null)
            return false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ScriptWait);
        try
        {
            var (response, _) = await _client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, ScriptEngineBody(access, target), limit.Token).ConfigureAwait(false);
            bool ok = response?.IsSuccessStatusCode == true;
            _log.Info("script.engine-set", CoreLog.F("ok", ok));
            return ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("script.engine-set-failed", CoreLog.F("error", ex.GetType().Name));
            return false;
        }
    }

    public bool SetScriptRunning(ScriptAccess access, bool running)
    {
        if (!access.InObject || _disposed || !_client.Network.Connected)
            return false;
        _client.Inventory.RequestSetScriptRunning(new UUID(access.ObjectId), new UUID(access.ItemId), running);
        return true;
    }

    /// <summary>ScriptReset: AgentData and Script {ObjectID, ItemID} (llpreviewscript.cpp LLLiveLSLEditor::onReset).</summary>
    internal static ScriptResetPacket ScriptReset(UUID agent, UUID session, Guid objectId, Guid itemId) => new()
    {
        AgentData = new ScriptResetPacket.AgentDataBlock { AgentID = agent, SessionID = session },
        Script = new ScriptResetPacket.ScriptBlock { ObjectID = new UUID(objectId), ItemID = new UUID(itemId) },
    };

    public bool ResetScript(ScriptAccess access)
    {
        if (!access.InObject || _disposed || !_client.Network.Connected || _client.Network.CurrentSim is not { } sim)
            return false;
        _client.Network.SendPacket(ScriptReset(_client.Self.AgentID, _client.Self.SessionID, access.ObjectId, access.ItemId), sim);
        return true;
    }
}
