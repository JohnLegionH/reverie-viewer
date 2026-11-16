// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>
/// The outfit side of the protocol adapter. A change of the current outfit is made the way the official viewer makes it when
/// the grid bakes the look: links are created and removed in the current outfit folder (through the grid's inventory API when
/// it has one, else the older messages), objects are rezzed on or taken off the avatar, and then the region's server-side
/// baking is asked to rebuild the look from the folder (a request carrying the folder's version). The grid then sends the
/// finished look to everyone, this viewer included, and it is drawn from that message like any other avatar's. This viewer
/// never composes, bakes or sends a look of its own: the library's own wearing methods, which do, are not used.
/// </summary>
internal sealed partial class LmvTransport
{
    private const string RefreshCapability = "UpdateAvatarAppearance";
    private const string CreateFolderCapability = "CreateInventoryCategory";
    /// <summary>The most times a refused rebuild request is tried again (the official viewer's limit).</summary>
    internal const int RefreshMaxRetries = 5;

    private readonly RemovedLinkLedger _removedLinks = new();
    private readonly object _refreshGate = new();
    private bool _refreshRunning;
    private bool _refreshAgain;
    private bool _refreshAgainForChange;   // a change (not only the login rebuild) waits for the next request

    /// <summary>The wait before retry <paramref name="retry"/> (1 for the first): 2 to that power minus one seconds, as the official viewer waits.</summary>
    internal static TimeSpan RefreshRetryDelay(int retry) => TimeSpan.FromSeconds(Math.Pow(2, retry) - 1);

    /// <summary>The body of the rebuild request: the current outfit folder's version.</summary>
    internal static OSDMap RefreshBody(int cofVersion) => new() { ["cof_version"] = OSD.FromInteger(cofVersion) };

    /// <summary>Whether a change asks the region to rebuild the look once it is made: every change that does anything, objects put on
    /// or taken off alone included, as the official viewer asks after each (LLUpdateAppearanceOnDestroy and
    /// LLRequestServerAppearanceUpdateOnDestroy in llappearancemgr.cpp, LLRegisterAttachmentCallback in llattachmentsmgr.cpp).</summary>
    internal static bool RebuildAfter(OutfitChange change) => !change.IsEmpty;

    private void ExecuteOutfitCommand(TransportCommand command)
    {
        switch (command)
        {
            case ChangeOutfitCommand c:
                _ = Task.Run(() => ChangeOutfitAsync(c.Change));
                break;
            case SaveOutfitCommand s:
                _ = Task.Run(() => SaveOutfitAsync(s));
                break;
            case RebuildLookCommand r:
                _ = Task.Run(() => RefreshAppearanceAsync(new UUID(r.CofId), save: false, login: true));
                break;
        }
    }

    /// <summary>Who makes the look after an outfit change: the region (asked through its rebuild capability), the viewer itself
    /// (a region that does not bake), or nobody (a baking region without the capability).</summary>
    internal enum RebuildPlan { AskRegion, ViewerBakes, NoService }

    internal static RebuildPlan PlanRebuild(Viewer.Core.World.AppearanceMode mode, bool regionHasCapability) =>
        mode == Viewer.Core.World.AppearanceMode.Client ? RebuildPlan.ViewerBakes
        : regionHasCapability ? RebuildPlan.AskRegion
        : RebuildPlan.NoService;

    /// <summary>Runs inventory work that waits for a reply the grid may never send, for at most <paramref name="limit"/>: true when it
    /// finished, false when the limit passed first.</summary>
    internal static async Task<bool> WithinAsync(Func<CancellationToken, Task> work, TimeSpan limit)
    {
        using var cut = new CancellationTokenSource(limit);
        var running = work(cut.Token);
        var done = await Task.WhenAny(running, Task.Delay(Timeout.Infinite, cut.Token).ContinueWith(_ => { }, TaskScheduler.Default)).ConfigureAwait(false);
        if (done != running)
            return false;
        try { await running.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cut.IsCancellationRequested) { return false; }
        return true;
    }

    /// <summary>The longest an outfit's links are waited for (the older link message is answered one link at a time, and a grid may
    /// never answer one).</summary>
    private static readonly TimeSpan LinkReplyLimit = TimeSpan.FromSeconds(30);

    private InventoryBase? Stored(Guid id) =>
        _client.Inventory.Store is { } store && store.TryGetValue(new UUID(id), out var b) ? b : null;

    private async Task ChangeOutfitAsync(OutfitChange requested)
    {
        try
        {
            var inv = _client.Inventory;
            // A Library item is copied into the account's inventory first and the copy is worn (the official viewer does the same).
            var copyOf = new Dictionary<Guid, Guid>();
            var touched = new List<Guid>();
            foreach (var c in requested.Copies)
            {
                var made = await CopyLibraryItemAsync(c).ConfigureAwait(false);
                if (made == Guid.Empty)
                {
                    Raise(new OutfitCommandFinished(false, false, "copy_failed", -1, Touched: touched));
                    return;
                }
                copyOf[c.ItemId] = made;
                touched.Add(c.FolderId);
            }
            var change = requested.WithCopies(copyOf);
            var cof = new UUID(change.CofId);
            var newLinks = new List<(InventoryBase, string)>();
            foreach (var l in change.Create)
            {
                if (Stored(l.TargetId) is not { } target)
                {
                    Raise(new OutfitCommandFinished(false, false, "missing_item", -1));
                    return;
                }
                newLinks.Add((target, l.Description));
            }
            var toAttach = new List<(InventoryItem Item, AttachRequest Request)>();
            foreach (var a in change.Attach)
            {
                if (Stored(a.ItemId) is not InventoryItem item)
                {
                    Raise(new OutfitCommandFinished(false, false, "missing_item", -1));
                    return;
                }
                toAttach.Add((item, a));
            }

            foreach (var linkId in change.RemoveLinks)
                await RemoveLinkAsync(linkId).ConfigureAwait(false);
            foreach (var d in change.Renumbered)
                RewriteLinkNote(d);
            if (newLinks.Count > 0 && !await WithinAsync(ct => inv.CreateLinksAsync(cof, newLinks, null, ct), LinkReplyLimit).ConfigureAwait(false))
            {
                Raise(new OutfitCommandFinished(false, false, "failed", -1, Touched: touched));
                return;
            }
            foreach (var (item, request) in toAttach)
                _client.Appearance.Attach(item, (AttachmentPoint)request.Point, replace: !request.Add);
            foreach (var id in change.Detach)
                _client.Appearance.Detach(new UUID(id));

            if (RebuildAfter(change))
            {
                ClientBakeOutfitChanged(cof);   // a region that does not bake on the server needs a look baked here (nothing happens in one that does)
                await RefreshAppearanceAsync(cof, save: false, touched: touched).ConfigureAwait(false);
            }
            else
                Raise(new OutfitCommandFinished(false, true, "", -1, Touched: touched));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("outfit.change-failed", CoreLog.F("error", ex.GetType().Name));
            Raise(new OutfitCommandFinished(false, false, "failed", -1));
        }
    }

    /// <summary>Writes a new layer note on a link that stays in the current outfit folder (the library sends the update message and
    /// keeps its store in step).</summary>
    private void RewriteLinkNote(LinkDescription d)
    {
        if (Stored(d.LinkId) is InventoryItem link)
        {
            link.Description = d.Description;
            _client.Inventory.RequestUpdateItem(link);
        }
    }

    /// <summary>Copies a Library item into the folder for its kind and returns the copy's id (empty when the grid did not make it).</summary>
    private async Task<Guid> CopyLibraryItemAsync(LibraryCopy copy)
    {
        var inv = _client.Inventory;
        if (Stored(copy.ItemId) is not InventoryItem source)
            return Guid.Empty;
        var made = await inv.RequestCopyItemAsync(source.UUID, new UUID(copy.FolderId), source.Name, source.OwnerID, CancellationToken.None).ConfigureAwait(false);
        if (made is null || made.UUID == UUID.Zero)
            return Guid.Empty;
        // the links are made from the library's store, so the copy has to be in it
        if (inv.Store is { } store && !store.Contains(made.UUID))
            store.UpdateNodeFor(made);
        return made.UUID.Guid;
    }

    /// <summary>Removes one link (the library picks the grid's inventory API or the older removal message).</summary>
    private async Task RemoveLinkAsync(Guid linkId)
    {
        _removedLinks.Removed(linkId);   // from now on an answer that still lists it must not put it back
        await _client.Inventory.RemoveItemAsync(new UUID(linkId), CancellationToken.None).ConfigureAwait(false);
        // the library may leave the link in its own store; the core's view follows that store, so make sure it is gone there too
        if (_client.Inventory.Store is { } store && Stored(linkId) is { } still)
            store.RemoveNodeFor(still);
    }

    private async Task SaveOutfitAsync(SaveOutfitCommand s)
    {
        try
        {
            var inv = _client.Inventory;
            var store = inv.Store;
            if (store is null)
            {
                Raise(new OutfitCommandFinished(true, false, "failed", -1));
                return;
            }
            var cofChildren = store.GetContents(new UUID(s.CofId));
            var targets = new List<(InventoryBase, string)>();
            var oldBaseLinks = new List<UUID>();
            foreach (var child in cofChildren)
            {
                if (child is not InventoryItem link)
                    continue;
                if (link.AssetType == AssetType.LinkFolder)
                {
                    oldBaseLinks.Add(link.UUID);
                    continue;
                }
                if (link.AssetType != AssetType.Link || Stored(link.AssetUUID.Guid) is not InventoryItem target)
                {
                    Raise(new OutfitCommandFinished(true, false, "missing_item", -1));   // never save part of a look
                    return;
                }
                targets.Add((target, link.Description ?? ""));
            }
            if (targets.Count == 0)
            {
                Raise(new OutfitCommandFinished(true, false, "empty", -1));
                return;
            }
            var links = targets.Select(t => new SaveLink(t.Item1.UUID.Guid, t.Item2)).ToList();
            var outcome = await new OutfitSaveSequence(new LmvFolderOps(this)).RunAsync(s.RootId, s.MyOutfitsId, s.Name, links).ConfigureAwait(false);
            if (!outcome.Ok)
            {
                Raise(new OutfitCommandFinished(true, false, outcome.Reason, -1, outcome.Folder));
                return;
            }
            var folderId = new UUID(outcome.Folder);
            if (Stored(folderId.Guid) is InventoryFolder created)
            {
                foreach (var old in oldBaseLinks)
                    await RemoveLinkAsync(old.Guid).ConfigureAwait(false);
                if (!await WithinAsync(ct => inv.CreateLinksAsync(new UUID(s.CofId), new List<(InventoryBase, string)> { (created, "") }, null, ct), LinkReplyLimit).ConfigureAwait(false))
                    _log.Warn("outfit.base-link-unanswered", CoreLog.F("folder", "outfit"));
            }
            await RefreshAppearanceAsync(new UUID(s.CofId), save: true, folder: folderId.Guid).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("outfit.save-failed", CoreLog.F("error", ex.GetType().Name));
            Raise(new OutfitCommandFinished(true, false, "failed", -1));
        }
    }

    /// <summary>Reads the current outfit folder back (so its version is the grid's) and asks the region's baking service to rebuild
    /// the look from it. One request runs at a time; a change that lands while one runs makes it ask once more when it is done.</summary>
    private async Task RefreshAppearanceAsync(UUID cof, bool save, Guid folder = default, IReadOnlyList<Guid>? touched = null, bool login = false)
    {
        lock (_refreshGate)
        {
            if (_refreshRunning)
            {
                _refreshAgain = true;
                _refreshAgainForChange |= !login;
                return;
            }
            _refreshRunning = true;
        }
        try
        {
            do
            {
                lock (_refreshGate)
                {
                    if (_refreshAgain)
                        login &= !_refreshAgainForChange;   // a change that waited is answered as a change
                    _refreshAgain = false;
                    _refreshAgainForChange = false;
                }
                var result = await RequestRebuildAsync(cof).ConfigureAwait(false);
                if (login)
                    Raise(new LookRebuildFinished(result.Ok, result.Reason, result.Version));   // not an outfit command: the outfit window is not told
                else
                    Raise(new OutfitCommandFinished(save, result.Ok, result.Reason, result.Version, folder, touched));
            }
            while (ConsumeAgain());
        }
        finally
        {
            lock (_refreshGate) _refreshRunning = false;
        }
    }

    private bool ConsumeAgain()
    {
        lock (_refreshGate) return _refreshAgain;
    }

    private async Task<(bool Ok, string Reason, int Version)> RequestRebuildAsync(UUID cof)
    {
        var caps = _client.Network.CurrentSim?.Caps?.CapabilityURI(RefreshCapability);
        switch (PlanRebuild(_bakeController?.Mode ?? Viewer.Core.World.AppearanceMode.Unknown, caps is not null))
        {
            case RebuildPlan.NoService:
                return (false, "no_service", -1);
            case RebuildPlan.ViewerBakes:
            {
                // a region that does not bake: the look is the viewer's own bake (started with the change), so the change is done once
                // the current outfit folder is read back with the grid's version
                await _client.Inventory.RequestFolderContentsAsync(cof, _client.Self.AgentID, true, true, InventorySortOrder.ByName, CancellationToken.None).ConfigureAwait(false);
                return Stored(cof.Guid) is InventoryFolder held && held.Version >= 0 ? (true, "", held.Version) : (false, "no_version", -1);
            }
        }
        var http = _client.HttpCapsClient;
        for (int attempt = 0; attempt <= RefreshMaxRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(RefreshRetryDelay(attempt)).ConfigureAwait(false);
            if (_disposed || !_client.Network.Connected)
                return (false, "failed", -1);
            await _client.Inventory.RequestFolderContentsAsync(cof, _client.Self.AgentID, true, true, InventorySortOrder.ByName, CancellationToken.None).ConfigureAwait(false);
            if (Stored(cof.Guid) is not InventoryFolder folder || folder.Version < 0)
                return (false, "no_version", -1);
            int version = folder.Version;
            try
            {
                var (_, data) = await http.PostAsync(caps!, OSDFormat.Xml, RefreshBody(version), CancellationToken.None).ConfigureAwait(false);
                if (data is not null && OSDParser.Deserialize(data) is OSDMap reply && reply.TryGetValue("success", out var ok) && ok.AsBoolean())
                    return (true, "", version);
                // the region refused; the official viewer tries again only when the reply names the version it expected
                if (data is null || OSDParser.Deserialize(data) is not OSDMap refusal || !refusal.ContainsKey("expected"))
                    return (false, "refused", version);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("outfit.refresh-failed", CoreLog.F("error", ex.GetType().Name));
                return (false, "failed", version);
            }
        }
        return (false, "refused", -1);
    }

    /// <summary>The library's side of <see cref="OutfitSaveSequence"/>: folders are made through the library's asynchronous call and
    /// then asked for until the grid answers for them; links are made and read back through the library's inventory.</summary>
    private sealed class LmvFolderOps : IOutfitFolderOps
    {
        /// <summary>The longest a new folder is waited for before the save is given up.</summary>
        private static readonly TimeSpan FolderWait = TimeSpan.FromSeconds(20);
        private readonly LmvTransport _t;
        public LmvFolderOps(LmvTransport t) { _t = t; }

        public async Task<Guid> CreateFolderAsync(Guid parent, string name, bool isMyOutfits)
        {
            var inv = _t._client.Inventory;
            var type = isMyOutfits ? FolderType.MyOutfits : FolderType.Outfit;
            UUID id;
            var url = _t._client.Network.CurrentSim?.Caps?.CapabilityURI(CreateFolderCapability);
            if (url is not null)
            {
                // The official viewer's request for a new folder (LLInventoryModel::createNewCategory, no AIS): the reply names the
                // folder once the grid has made it.
                var body = new OSDMap
                {
                    ["folder_id"] = OSD.FromUUID(UUID.Random()),
                    ["parent_id"] = OSD.FromUUID(new UUID(parent)),
                    ["type"] = OSD.FromInteger((int)type),
                    ["name"] = OSD.FromString(name),
                };
                try
                {
                    var (_, data) = await _t._client.HttpCapsClient.PostAsync(url, OSDFormat.Xml, body, CancellationToken.None).ConfigureAwait(false);
                    if (data is null || OSDParser.Deserialize(data) is not OSDMap reply || !reply.TryGetValue("folder_id", out var named))
                        return Guid.Empty;
                    id = named.AsUUID();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _t._log.Warn("outfit.folder-create-failed", CoreLog.F("error", ex.GetType().Name));
                    return Guid.Empty;
                }
                if (id == UUID.Zero)
                    return Guid.Empty;
                if (inv.Store is { } store && !store.Contains(id))
                    store.UpdateNodeFor(new InventoryFolder(id) { ParentUUID = new UUID(parent), Name = name, PreferredType = type, OwnerID = _t._client.Self.AgentID, Version = 0 });
            }
            else
            {
                id = inv.CreateFolder(new UUID(parent), name, type);   // a grid without that capability: the older message, then wait for the grid's answer
                if (id == UUID.Zero)
                    return Guid.Empty;
            }
            // confirmed once the grid answers a request for the folder's contents and the folder is in the store with a version
            var until = DateTime.UtcNow + FolderWait;
            while (DateTime.UtcNow < until && !_t._disposed)
            {
                var answer = await inv.RequestFolderContentsAsync(id, _t._client.Self.AgentID, true, true, InventorySortOrder.ByName, CancellationToken.None).ConfigureAwait(false);
                if (answer is not null && _t.Stored(id.Guid) is InventoryFolder { Version: >= 0 })
                    return id.Guid;
                await Task.Delay(250).ConfigureAwait(false);
            }
            return Guid.Empty;
        }

        public async Task CreateLinksAsync(Guid folder, IReadOnlyList<SaveLink> links)
        {
            var items = new List<(InventoryBase, string)>();
            foreach (var l in links)
                if (_t.Stored(l.Target) is { } target)
                    items.Add((target, l.Description));
            if (items.Count > 0)
                await WithinAsync(ct => _t._client.Inventory.CreateLinksAsync(new UUID(folder), items, null, ct), LinkReplyLimit).ConfigureAwait(false);   // the save reads the links back to confirm
        }

        public async Task<IReadOnlySet<Guid>> ReadLinkTargetsAsync(Guid folder)
        {
            var inv = _t._client.Inventory;
            await inv.RequestFolderContentsAsync(new UUID(folder), _t._client.Self.AgentID, true, true, InventorySortOrder.ByName, CancellationToken.None).ConfigureAwait(false);
            var held = new HashSet<Guid>();
            if (inv.Store is { } store)
                foreach (var i in store.GetContents(new UUID(folder)).OfType<InventoryItem>())
                    held.Add(i.AssetUUID.Guid);
            return held;
        }
    }
}
