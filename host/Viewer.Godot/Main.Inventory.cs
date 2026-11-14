// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Viewer.Core.Session;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The inventory in the running viewer: the shell gains the Content menu, a toolbar button and the floater when the first
/// session logs in; each session's inventory is shown while it lives and dropped when it ends.
/// </summary>
public partial class Main
{
    private InventoryPanel? _inventoryPanel;
    private ViewerSession? _inventoryFor;
    private OutfitPanel? _outfitPanel;
    private Guid _inventoryAgent;
    private Viewer.Core.Inventory.InventoryBackgroundFetch? _inventoryFetch;
    private readonly System.Diagnostics.Stopwatch _inventoryClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>The viewer is closing while logged in: keep the time for the next login's Recent tab.</summary>
    private void RecordInventoryLogout()
    {
        if (_inventoryAgent != Guid.Empty)
            LogoutTime.Record(UserSettings(null), _inventoryAgent, DateTimeOffset.UtcNow);
        _inventoryAgent = Guid.Empty;
    }

    /// <summary>Every frame: shows a new session's inventory, or empties the window when its session has ended.</summary>
    private void UpdateInventory()
    {
        var session = Model?.Session;
        if (_inventoryFor is not null && _inventoryFetch is not null && !_inventoryFor.IsEnded)
        {
            if (_inventoryFor.Stage == SessionStage.InWorld)
                _inventoryFetch.Begin();   // the delay counts from arrival, so the first frames are left alone
            _inventoryFetch.Tick();
        }
        bool attach = session is not null && session != _inventoryFor && !session.IsEnded && session.AgentId != Guid.Empty;
        bool drop = _inventoryFor is not null && (_inventoryFor != session || _inventoryFor.IsEnded);
        if (!attach && !drop)
            return;
        if (drop && _inventoryAgent != Guid.Empty)
            LogoutTime.Record(UserSettings(null), _inventoryAgent, DateTimeOffset.UtcNow);   // the next login's Recent tab starts here
        if (_inventoryFor is not null && _outfitPanel is not null)
            _inventoryFor.OutfitCommandDone -= _outfitPanel.ShowResult;
        DetachAppearance(_inventoryFor);
        _inventoryFor = null;
        _inventoryFetch = null;
        _inventoryAgent = Guid.Empty;
        if (attach && _shell is not null)
        {
            _inventoryPanel ??= InventoryFeature.Install(_shell, settings: UserSettings(null));
            var worn = new Viewer.Shell.Outfit.WornObjects(session!.World, session.AgentId, () => System.Environment.TickCount64);
            _inventoryPanel.CanDetachItem = RlvCanDetach;
            _inventoryPanel.Bind(session.Inventory, session.Actions.Submit, LogoutTime.Previous(UserSettings(null), session.AgentId), worn.Items);
            _outfitPanel ??= OutfitFeature.Install(_shell);
            _outfitPanel.CanDetachItem = RlvCanDetach;
            _outfitPanel.Bind(session.Inventory, session.Actions.Submit);
            session.OutfitCommandDone += _outfitPanel.ShowResult;
            AttachAppearance(session);
            AttachNotecards(session);
            _inventoryFor = session;
            _inventoryAgent = session.AgentId;
            _inventoryFetch = UserSettings(null).Get(Settings.InventoryBackgroundFetch)
                ? new Viewer.Core.Inventory.InventoryBackgroundFetch(session.Inventory, () => _inventoryClock.Elapsed) : null;
        }
        else
        {
            _inventoryPanel?.Bind(null, null);
            _outfitPanel?.Bind(null, null);
            DetachNotecards();
        }
    }

#if NV_AUTOMATION
    /// <summary>What the background inventory fetch is doing (development builds only): whether it exists for this session, whether it is preparing or done, and how many folders are not loaded yet.</summary>
    public System.Collections.Generic.Dictionary<string, object> InventoryFetchState() => new()
    {
        ["fetch_on"] = _inventoryFetch is not null, ["fetch_preparing"] = _inventoryFetch?.Preparing ?? false, ["fetch_done"] = _inventoryFetch?.Done ?? false,
        ["folders_unloaded"] = _inventoryFor?.Inventory.UnloadedFolderCount ?? -1,
    };
#endif

#if NV_AUTOMATION
    /// <summary>The inventory command (development builds only), read only: counts of what is loaded, opening a system folder by
    /// its kind, a search and a sort. It answers counts, kinds and load states, never a folder or item name.</summary>
    public object? InventoryCall(string action, string? kind, string? text, string? sort, string? tab = null)
    {
        if (_inventoryPanel is not { Model: { } m, View: { } v } p || _inventoryFor is null)
            return null;
        if (Viewer.Shell.Inventory.InventoryTabNames.TryParse(tab, out var chosenTab))   // "all" or "recent": the window shows that tab, as a click on it does
        {
            p.Tabs.CurrentTab = chosenTab == Viewer.Shell.Inventory.InventoryTab.Recent ? 1 : 0;
            v.Tab = chosenTab;
        }
        switch (action)
        {
            case "open":
            {
                Guid id = string.Equals(kind, "root", StringComparison.OrdinalIgnoreCase) ? m.RootId
                    : Enum.TryParse<Viewer.Core.Inventory.FolderKind>(kind, true, out var k) ? m.FolderOfKind(k) : Guid.Empty;
                if (id != Guid.Empty)
                    p.Expand(id);
                return new { opened = id != Guid.Empty, load = m.LoadState(id).ToString() };
            }
            case "search":
            {
                p.Search.Text = text ?? "";
                p.Search.EmitSignal(global::Godot.LineEdit.SignalName.TextChanged, text ?? "");
                var rows = v.Rows();
                return new { rows = rows.Count, items = rows.Count(r => !r.IsFolder), status = v.Status().Key, tab = v.Tab.ToString() };
            }
            case "sort":
            {
                v.Sort = string.Equals(sort, "date", StringComparison.OrdinalIgnoreCase) ? Viewer.Shell.Inventory.InventorySort.ByDate : Viewer.Shell.Inventory.InventorySort.ByName;
                var rows = v.Rows();
                // two item lines next to each other at one depth share a folder, and must be in the sort order
                var names = StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, true);
                bool ordered = true;
                for (int i = 1; i < rows.Count; i++)
                {
                    var a = rows[i - 1];
                    var b = rows[i];
                    if (a.IsFolder || b.IsFolder || a.Depth != b.Depth)
                        continue;
                    bool wrong = v.Sort == Viewer.Shell.Inventory.InventorySort.ByName
                        ? names.Compare(a.Name, b.Name) > 0
                        : (a.Date ?? DateTimeOffset.MinValue) < (b.Date ?? DateTimeOffset.MinValue);
                    ordered &= !wrong;
                }
                return new { sort = v.Sort.ToString(), items = rows.Count(r => !r.IsFolder), ordered };
            }
            default:
                return new
                {
                    skeleton = m.HasSkeleton, folders = m.AllFolders().Count, items = m.AllItems().Count, unloaded = m.UnloadedFolderCount, in_flight = m.InFlight,
                    root_load = m.LoadState(m.RootId).ToString(), library_load = m.LoadState(m.LibraryRootId).ToString(),
                    system = Enum.GetValues<Viewer.Core.Inventory.FolderKind>().Where(k => m.FolderOfKind(k) != Guid.Empty && k != Viewer.Core.Inventory.FolderKind.Root)
                        .Select(k => new { kind = k.ToString(), load = m.LoadState(m.FolderOfKind(k)).ToString(), children = m.ChildFolders(m.FolderOfKind(k)).Count + m.ChildItems(m.FolderOfKind(k)).Count }).ToArray(),
                    visible_rows = v.Rows().Count, status = v.Status().Key,
                };
        }
    }

    /// <summary>The outfit part of the development command. Reading is always allowed and answers counts and ids, never a name.
    /// Changing what is worn is refused unless the run says a scratch test account is in use (<c>NV_OUTFIT_SCRATCH_OK=1</c>).
    /// Items are picked by exact name (case-blind) among the account's own items, the same way a person picks one.</summary>
    public object? OutfitCall(string action, string? text, int point, Guid pickId = default)
    {
        if (_inventoryPanel is not { Model: { } m } p || _inventoryFor is null || p.Outfit is not { } o)
            return null;
        var session = _inventoryFor;
        if (action == "outfit")
        {
            var look = session.World.Appearance(session.AgentId);
            var current = Viewer.Core.Inventory.OutfitPlanner.Current(m);
            var last = session.LastOutfitResult;
            var snap = session.World.Snapshot();
            string BakeHash(Viewer.Core.World.AvatarAppearance? a) => a is null ? "" : string.Join(",", a.Bakes.OrderBy(k => k.Key).Select(k => k.Value.ToString("N")[..8]));
            var cofId = m.FolderOfKind(Viewer.Core.Inventory.FolderKind.CurrentOutfit);
            bool Spare(Viewer.Core.Inventory.InventoryItemData i, Viewer.Core.Inventory.ItemKind kind) =>
                i.Kind == kind && !m.IsInLibrary(i.ParentId) && !m.IsDescendantOf(i.Id, m.FolderOfKind(Viewer.Core.Inventory.FolderKind.Trash)) && !Viewer.Core.Inventory.OutfitPlanner.IsWorn(m, i.Id);
            return new
            {
                links = cofId == Guid.Empty ? Array.Empty<object>() : m.ChildItems(cofId).Select(l => (object)new { target = l.LinkedId?.ToString(), kind = l.Kind.ToString(), desc = l.Description }).OrderBy(x => x.ToString()).ToArray(),
                spare_clothing = m.AllItems().Where(i => Spare(i, Viewer.Core.Inventory.ItemKind.Clothing)).Take(40).Select(i => new { id = i.Id.ToString(), type = (int)Viewer.Core.Inventory.OutfitPlanner.WearableTypeOf(i) }).ToArray(),
                library_clothing = m.AllItems().Where(i => i.Kind == Viewer.Core.Inventory.ItemKind.Clothing && m.IsInLibrary(i.ParentId)).Take(8).Select(i => new { id = i.Id.ToString(), type = (int)Viewer.Core.Inventory.OutfitPlanner.WearableTypeOf(i) }).ToArray(),
                spare_objects = m.AllItems().Where(i => Spare(i, Viewer.Core.Inventory.ItemKind.Object)).Take(10).Select(i => i.Id.ToString()).ToArray(),
                attachments = snap.Objects.Values.Count(o => o.WearerAgentId == session.AgentId && o.AttachmentPoint is not null),
                others = snap.Avatars.Values.Where(a => a.AgentId != session.AgentId).Select(a =>
                {
                    var ap = session.World.Appearance(a.AgentId);
                    return new { agent = a.AgentId.ToString(), version = ap?.AppearanceVersion ?? 0, cof_version = ap?.CofVersion ?? 0, bakes = BakeHash(ap),
                        attachments = snap.Objects.Values.Count(o => o.WearerAgentId == a.AgentId && o.AttachmentPoint is not null) };
                }).ToArray(),
                self_bakes = BakeHash(look),
                cof_loaded = m.FolderOfKind(Viewer.Core.Inventory.FolderKind.CurrentOutfit) is var c && c != Guid.Empty && m.LoadState(c) == Viewer.Core.Inventory.FolderLoad.Loaded,
                worn = current.Count,
                sections = Enumerable.Range(0, 4).Select(n => current.Count(e => e.Section == n)).ToArray(),
                targets = current.Select(e => e.TargetId.ToString()).ToArray(),
                base_outfit = p.View is not null && new Viewer.Shell.Outfit.OutfitView(m).BaseOutfitName() is not null,
                saved = new Viewer.Shell.Outfit.OutfitView(m).Saved().Count,
                saved_outfits = new Viewer.Shell.Outfit.OutfitView(m).Saved().Select(f => new { id = f.Id.ToString(), load = m.LoadState(f.Id).ToString(), version = f.Version, count = f.DescendentCount, items = m.ChildItems(f.Id).Count }).ToArray(),
                done_count = session.OutfitResults,
                last = last is null ? null : new { ok = last.Ok, save = last.Save, reason = last.Reason, cof_version = last.CofVersion },
                look = look is null ? null : new { version = look.AppearanceVersion, cof_version = look.CofVersion, bakes = look.Bakes.Count },
            };
        }
        if (action == "refresh")
        {
            // asks the grid for the current outfit folder again (reading, so no scratch account is needed)
            var folder = m.FolderOfKind(Viewer.Core.Inventory.FolderKind.CurrentOutfit);
            if (folder != Guid.Empty)
                m.Invalidate(folder);
            return new { requested = folder != Guid.Empty };
        }
        if (Environment.GetEnvironmentVariable("NV_OUTFIT_SCRATCH_OK") != "1")
            return new { refused = "scratch_only" };
        string wanted = text ?? "";
        bool Same(string n) => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase);
        var cof = m.FolderOfKind(Viewer.Core.Inventory.FolderKind.CurrentOutfit);
        List<Guid> matches = action switch
        {
            "takeoff" when pickId != Guid.Empty => new List<Guid> { pickId },
            "takeoff" => Viewer.Core.Inventory.OutfitPlanner.Current(m).Where(e => Same(e.Name)).Select(e => e.TargetId).Distinct().ToList(),
            "wearoutfit" when pickId != Guid.Empty => new List<Guid> { pickId },
            "wearoutfit" => m.AllFolders().Where(f => f.Kind == Viewer.Core.Inventory.FolderKind.Outfit && Same(f.Name)).Select(f => f.Id).ToList(),
            "saveoutfit" or "dropbase" => new List<Guid> { Guid.Empty },
            _ when pickId != Guid.Empty => new List<Guid> { pickId },
            _ => m.AllItems().Where(i => Same(i.Name) && !m.IsInLibrary(i.ParentId) && !m.IsDescendantOf(i.Id, cof) && i.Kind != Viewer.Core.Inventory.ItemKind.Link).Select(i => i.Id).ToList(),
        };
        if (matches.Count != 1)
            return new { matched = matches.Count, submitted = false, code = matches.Count == 0 ? "none" : "ambiguous" };
        var id = matches[0];
        if (action == "dropbase")
        {
            // removes only the link to the outfit folder the look came from (the link wearing an outfit adds)
            var baseLinks = m.ChildItems(cof).Where(i => i.Kind == Viewer.Core.Inventory.ItemKind.LinkFolder).Select(i => i.Id).ToList();
            if (baseLinks.Count == 0)
                return new { matched = 1, submitted = false, code = "no_base_link" };
            var done = session.Actions.Submit(new Viewer.Core.Actions.OutfitChangeIntent(new Viewer.Core.Inventory.OutfitChange(cof, [], baseLinks, [], [])));
            return new { matched = 1, submitted = done.Sent, code = done.Code };
        }
        var result = action switch
        {
            "wear" => o.Wear(id),
            "add" => o.Add(id),
            "takeoff" => o.TakeOff(id),
            "attach" => o.Attach(id, point, false),
            "detach" => o.Detach(id),
            "wearoutfit" => o.WearOutfit(id),
            _ => o.SaveOutfit(wanted),
        };
        return new { matched = 1, submitted = result.Submitted, code = result.Code };
    }
#endif
}
