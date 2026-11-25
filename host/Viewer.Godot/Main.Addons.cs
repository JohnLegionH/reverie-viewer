// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Viewer.Addons;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Addons;
using Viewer.Shell.Inventory;
using Viewer.Shell.Notifications;
using Viewer.Shell.Overrider;
using ChatKind = Viewer.Addons.ChatKind;

namespace Viewer.Godot;

/// <summary>
/// The add-on host in the viewer: built-in add-ons from the add-ons folder beside the viewer's own libraries, the user's from
/// the Addons folder in the user's settings folder. Each frame it tells the add-ons about the session, the user's own avatar
/// and local chat, sends what they ask for, and draws their windows and menu items; it is a veto point of each session's
/// action pipeline, and a refusal is shown to the user as a notice. The Add-ons window (Me menu) switches add-ons on and off.
/// </summary>
public partial class Main
{
    private AddonHost? _addons;
    private AddonsPanel? _addonsPanel;
    private ViewerSession? _addonsFor;
    private bool _addonsInWorld;
    private (Guid Id, int Sequence)[] _addonPlaying = [];
    private readonly ConcurrentQueue<ChatLine> _addonChat = new();
    private readonly Dictionary<string, (AddonWindowView View, AddonWindow? Window)> _addonViews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string AddonId, Action Chosen)> _addonMenu = new(StringComparer.Ordinal);
    private readonly List<string> _addonSubmenus = [];
    private readonly Dictionary<string, (AddonWindowView View, AddonWindow Row)> _addonQuick = new(StringComparer.Ordinal);
    private Guid? _addonPreview;
    private readonly HashSet<Guid> _addonStarted = new();   // the animations add-ons (the animation overrider) have started and not stopped

    /// <summary>What the viewer does for add-ons.</summary>
    private sealed class AddonServices(Main main) : IAddonServices
    {
        public double Now => main._cameraClock.Elapsed.TotalSeconds;

        public void SendAnimations(IReadOnlyList<(Guid Animation, bool Start)> requests)
        {
            // requests of one frame go out as one message, in order: the region then answers once with the list as it is after all of them
            bool sent = main._addonsFor is { IsEnded: false } session && session.SendAnimations([.. requests]);
            foreach (var (animation, start) in requests)
            {
                if (start) main._addonStarted.Add(animation);
                else main._addonStarted.Remove(animation);
                Log.Info("addons.animation", CoreLog.F("animation", animation), CoreLog.F("start", start), CoreLog.F("sent", sent));
            }
        }

        public bool Say(string text) =>
            main._addonsFor is { IsEnded: false } session && session.Actions.Submit(new Viewer.Core.Actions.SayIntent(text)).Sent;

        public IAddonInventory? Inventory => main._addonsFor is { IsEnded: false } session && main._addonsInWorld ? new AddonInventory(session.Inventory, main.AddonNotecardsFor(session)) : null;

        public IAddonInventoryWriter? InventoryWriter => main._addonsFor is { IsEnded: false } session && main._addonsInWorld ? new AddonInventoryWriter(session) : null;

        public bool SayOnChannel(int channel, string text) => main.SayOnChannel(channel, text);

        public bool ForceSit(Guid objectId) => main.RlvForceSit(objectId);

        public bool ForceStand() => main.RlvForceStand();

        public bool ForceTeleport(double x, double y, double z) => main.RlvForceTeleport(x, y, z);

        public bool ObjectPresent(Guid objectId) => main.RlvObjectPresent(objectId);

        public string ViewerName => main._shell?.Shell.Strings.Get("brand.default") ?? "Viewer";

        public AddonWorldSnapshot? ReadWorld() => main.AddonWorldNow();

        public float? GroundHeight(float x, float y) =>
            main._addonsFor is { IsEnded: false } session && main._addonsInWorld ? AddonWorldReader.GroundHeight(session.World, x, y) : null;

        public System.Numerics.Vector3? WindAt(float x, float y) =>
            main._addonsFor is { IsEnded: false } session && main._addonsInWorld ? AddonWorldReader.WindAt(session.World, x, y) : null;

        public void PlayLocally(Guid? animation)
        {
            // the own avatar plays it in this viewer only: it joins the region's list for the own avatar and is never sent
            main._addonPreview = animation;
            if (main.OwnAvatars?.Scene is { } scene)
                scene.LocalPreview = animation is { } id ? [id] : [];
            Log.Info("addons.preview", CoreLog.F("playing", animation is not null));
        }
    }

    // the world state, and its region's epoch, that the own avatar's centre and the camera were last placed in
    private (Viewer.Core.World.WorldState? World, int Epoch) _addonPlacedIn;

    /// <summary>Whether the avatar and the camera are placed in the region the session is in now: not in the frames between a
    /// change of region and the avatar's drawing there.</summary>
    private bool AddonViewPlaced(ViewerSession session) =>
        _following && ReferenceEquals(_addonPlacedIn.World, session.World) && _addonPlacedIn.Epoch == session.World.Epoch;

    /// <summary>Once a frame, after the camera has followed the own avatar: the add-ons that read the world are told the region,
    /// and any teleport that ended, only when a read in their handler answers for that region.</summary>
    private void AddonsWorldFrame()
    {
        if (_addons is { } host && _addonsInWorld && _addonsFor is { IsEnded: false } session && AddonViewPlaced(session) && session.Region is { } here)
            host.InRegion(here.Name, Viewer.Core.Map.MapMath.HandleOf(here.GridX, here.GridY));
    }

    /// <summary>The world for add-ons that read it, taken when asked (nothing is gathered while none asks): the camera and the own
    /// avatar as they are drawn, in the anchor region's frame, and the sky as it is drawn.</summary>
    private AddonWorldSnapshot? AddonWorldNow()
    {
        if (_addonsFor is not { IsEnded: false } session || !_addonsInWorld || !AddonViewPlaced(session) || _camera is not { } eye
            || OwnAvatars?.Scene.Self is not { } self)
            return null;
        // the engine's axes are x east, y up, z south; the viewer's world is x east, y north, z up
        static System.Numerics.Vector3 World(Vector3 v) => new(v.X, -v.Z, v.Y);
        var camera = eye.GlobalTransform;
        var view = new AddonViewpoint(World(camera.Origin), World(-camera.Basis.Z), World(camera.Basis.Y), _agentCentre,
            _presented?.World.Avatar(self.Id)?.Velocity ?? default);
        var sky = _presenter?.Environment?.Frame;
        return AddonWorldReader.Read(session.World, view, sky,
            sky is null ? null : AddonWorldReader.Summary(_presenter!.EnvironmentSource, _skyChoice, sky));
    }

    /// <summary>Changes to the inventory for add-ons (the host keeps each inside its own folder): folders and links are made
    /// and waited for as the grid answers, renames and moves to the Trash go through the action pipeline, and the folders
    /// touched are read again.</summary>
    private sealed class AddonInventoryWriter(ViewerSession session) : IAddonInventoryWriter
    {
        private InventoryActions Actions => new(session.Inventory, intent => session.Actions.Submit(intent));

        public async System.Threading.Tasks.Task<Guid> CreateFolderAsync(Guid parent, string name)
        {
            if (session.IsEnded || session.InventoryBuilder is not { } builder)
                return Guid.Empty;
            Guid made = await builder.CreatePlainFolderAsync(parent, name);
            session.Inventory.Invalidate(parent);
            return made;
        }

        public async System.Threading.Tasks.Task<bool> LinkAsync(Guid folder, Guid target, string description)
        {
            if (session.IsEnded || session.InventoryBuilder is not { } builder)
                return false;
            int made = await builder.CreateItemLinksAsync(folder, [(target, description)]);
            session.Inventory.Invalidate(folder);
            return made > 0;
        }

        public System.Threading.Tasks.Task<bool> RenameFolderAsync(Guid folder, string name) =>
            System.Threading.Tasks.Task.FromResult(!session.IsEnded && Actions.Rename(folder, name).Submitted);

        public System.Threading.Tasks.Task<bool> MoveToTrashAsync(Guid id, bool isFolder) =>
            System.Threading.Tasks.Task.FromResult(!session.IsEnded && Actions.Trash(id, confirmed: true).Submitted);
    }

    private (ViewerSession Session, AddonNotecards Gate)? _addonNotecards;

    /// <summary>Which notecards add-ons may read in this session: those the user dropped on an add-on (kept per session).</summary>
    private AddonNotecards AddonNotecardsFor(ViewerSession session)
    {
        if (_addonNotecards is { } held && held.Session == session)
            return held.Gate;
        var reader = new NotecardReader(session.Inventory, session.Notecards);
        var gate = new AddonNotecards(session.Inventory, reader.OpenAsync);
        _addonNotecards = (session, gate);
        return gate;
    }

    /// <summary>The user dropped these on an add-on's drop zone: notecards among them, and in dropped folders, are handed over.</summary>
    private void AddonHandedOver(IReadOnlyList<AddonDropped> dropped)
    {
        if (_addonsFor is { IsEnded: false } session)
            AddonNotecardsFor(session).Dropped(dropped.Select(d => d.Id));
    }

    /// <summary>What an inventory row dragged onto an add-on's drop zone is: an animation (a link counts as what it points to) or a folder.</summary>
    private AddonDropped? ResolveDropped(Guid id)
    {
        if (_addonsFor is not { IsEnded: false } session)
            return null;
        var inventory = session.Inventory;
        if (inventory.Folder(id) is { } folder)
            return new AddonDropped(id, true, folder.Name);
        if (inventory.Item(id) is { } item && item.Kind == ItemKind.Animation)
            return new AddonDropped(id, false, item.Name);
        if (inventory.Item(id) is { LinkedId: { } target } link && inventory.Item(target) is { Kind: ItemKind.Animation })
            return new AddonDropped(target, false, link.Name);
        return null;
    }

    /// <summary>The inventory model as add-ons read it: names, links and animations' asset ids, nothing more.</summary>
    private sealed class AddonInventory(InventoryModel model, AddonNotecards notecards) : IAddonInventory
    {
        private readonly OverriderInventory _read = new(model);
        public Guid RootId => _read.RootId;
        public bool IsComplete(Guid folder) => _read.IsComplete(folder);
        public void Fetch(Guid folder) => _read.Fetch(folder);
        public IReadOnlyList<AddonFolder> Folders(Guid folder) => _read.Folders(folder).Select(f => new AddonFolder(f.Id, f.Name)).ToArray();
        public IReadOnlyList<AddonItem> Items(Guid folder) =>
            _read.Items(folder).Select(i => new AddonItem(i.Id, i.Name, i.Description, i.IsLink, i.IsAnimation, i.LinkedId, i.AssetId,
                model.Item(i.Id) is { Kind: ItemKind.Notecard })).ToArray();

        public System.Threading.Tasks.Task<string?> ReadNotecardTextAsync(Guid itemId) => notecards.ReadTextAsync(itemId);
        public Guid AssetOf(Guid linkItemId) => _read.AssetOf(linkItemId);

        public void Refresh(Guid folder)
        {
            if (model.Invalidate(folder) && folder == model.RootId)
                model.EnsureLoaded(folder);
        }
    }

    /// <summary>The viewer's own add-ons folder: beside the viewer's libraries (the development build and the export alike).</summary>
    internal static string? BuiltInAddonsFolder()
    {
        string?[] candidates =
        [
            Path.GetDirectoryName(typeof(Main).Assembly.Location),
            AppContext.BaseDirectory,
            Path.GetDirectoryName(OS.GetExecutablePath()),
        ];
        return candidates.Where(c => !string.IsNullOrEmpty(c)).Select(c => Path.Combine(c!, "addons")).FirstOrDefault(Directory.Exists);
    }

    /// <summary>Makes the host once the shell is up: finds the add-ons and starts the ones that are on.</summary>
    private void EnsureAddons()
    {
        if (_addons is not null || _shell is null)
            return;
        string? userRoot = null;
        try { userRoot = Viewer.Core.Platform.DataDirectories.ForCurrentUser().Settings; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Log.Warn("addons.user-folder-unavailable", CoreLog.F("reason", ex.GetType().Name)); }
        var host = new AddonHost(new AddonServices(this), Log, userRoot is null ? null : Path.Combine(userRoot, AddonHost.StateFileName));
        _addons = host;
        string? builtIn = BuiltInAddonsFolder();
        int found = builtIn is null ? 0 : host.Discover(builtIn, builtIn: true);
        int outside = 0;
        if (userRoot is not null)
        {
            string user = Path.Combine(userRoot, "Addons");
            try { Directory.CreateDirectory(user); } catch (IOException) { }   // so the user finds where add-ons go
            outside = host.Discover(user, builtIn: false);
            host.SettingsRoot = Path.Combine(userRoot, "AddonSettings");
            CarryOverriderSettings(host);
        }
        Log.Info("addons.found", CoreLog.F("built_in", found), CoreLog.F("outside", outside), CoreLog.F("built_in_folder", builtIn is not null));
        host.ScreenChanged += SyncAddonScreen;
        host.Refused += (name, reason) =>
        {
            Log.Info("addons.refused-shown");
            _notes?.Model.Post(new Notification { Kind = NotificationKind.Tip, Title = NoticeText.Raw(name), Body = NoticeText.Raw(reason) });
        };
        host.Changed += () => Log.Info("addons.changed", CoreLog.F("running", host.Addons.Count(a => a.Running)));

        var s = _shell.Shell;
        _addonsPanel = new AddonsPanel(s.Strings, host);
        _shell.AddFloater(new FloaterSpec(AddonsPanel.FloaterId, "addons.title", new UiSize(420, 460), new UiSize(320, 260)), _addonsPanel);
        s.Commands.Register("floater.addons", () => s.Floaters.Toggle(AddonsPanel.FloaterId));
        s.Menus.AddEntry("me", new MenuEntry("me.addons", "menu.me.addons", "floater.addons", Order: 8));

        EnsureRlvGate(host);
        host.StartEnabled();
        SyncAddonScreen();
    }

    /// <summary>The overrider's switch and set were kept in the viewer's settings before it became an add-on: they are carried into the
    /// add-on's own settings once, so a user who had it on still has it on.</summary>
    private void CarryOverriderSettings(AddonHost host)
    {
        if (host.Addons.FirstOrDefault(a => a.Id == "ao" && a.BuiltIn) is null)
            return;
        var settings = UserSettings(null);
        var carried = new Dictionary<string, string> { ["use"] = settings.Get(Settings.AnimationOverrider) ? "true" : "false" };
        if (settings.GetText(Settings.AnimationOverriderSetKey) is { Length: > 0 } set)
            carried["set"] = set;
        if (host.SeedSettings("ao", carried))
            Log.Info("addons.overrider-settings-carried");
    }

    /// <summary>Once a frame: the session's start and end, the own avatar's motion and animations, local chat, then the add-ons' frame.</summary>
    private void AddonsFrame()
    {
        EnsureAddons();
        if (_addons is not { } host)
            return;
        var session = Model?.Session;
        if (!ReferenceEquals(session, _addonsFor))
        {
            if (_addonsFor is not null)
            {
                _addonsFor.Communication -= AddonHeard;
                _addonsFor.Communication -= RlvHeard;
            }
            _addonsFor = null;
            if (session is { IsEnded: false })
            {
                _addonsFor = session;
                session.Communication += AddonHeard;
                session.Communication += RlvHeard;
                session.Actions.AddVeto(host);
                AttachRlvGate(session);
            }
        }
        bool live = _addonsFor is { IsEnded: false } && _driver is not null;
        if (!live && _addonsInWorld)
        {
            _addonsInWorld = false;
            _addonPlaying = [];
            _addonPreview = null;
            host.LoggedOut();
        }
        var self = live ? OwnAvatars?.Scene.Self : null;
        if (live && !_addonsInWorld && self is not null)
        {
            _addonsInWorld = true;
            host.LoggedIn(_addonsFor!.Grid);
        }
        while (_addonChat.TryDequeue(out var line))
            if (_addonsInWorld)
                host.Chat(line);
        if (_addonsInWorld && self is not null)
        {
            // a preview plays in this viewer only: it is not part of what the region says the avatar plays
            var playing = _addonPreview is { } preview ? self.Playing.Where(p => p.AnimationId != preview).ToArray() : self.Playing;
            // an avatar always plays something: an empty list is one that has not arrived yet (a region just entered)
            if (playing.Count > 0 && !SameAddonPlaying(playing))
            {
                _addonPlaying = playing.Select(p => (p.AnimationId, p.Sequence)).ToArray();
                host.Playing(_addonPlaying.Select(p => new PlayingAnimation(p.Id, p.Sequence)).ToArray());
            }
            host.Motion(AddonMotion.From(_addonPlaying.Select(p => p.Id).ToArray(), self.Seat != 0));
        }
        host.Frame();
        RlvFrame();
    }

    private bool SameAddonPlaying(IReadOnlyList<Viewer.Core.World.PlayingAnimation> playing)
    {
        if (playing.Count != _addonPlaying.Length)
            return false;
        for (int i = 0; i < playing.Count; i++)
            if (playing[i].AnimationId != _addonPlaying[i].Id || playing[i].Sequence != _addonPlaying[i].Sequence)
                return false;
        return true;
    }

    /// <summary>Local chat and the user's own objects' owner-say, kept for the main thread (the session raises this on its own thread).</summary>
    private void AddonHeard(TransportEvent e)
    {
        if (e is not ChatReceived chat)
            return;
        ChatKind? kind = chat.Type switch
        {
            ChatLineType.Say or ChatLineType.Shout or ChatLineType.Whisper => ChatKind.Local,
            ChatLineType.OwnerSay => ChatKind.OwnerSay,
            _ => null,
        };
        if (kind is { } k)
            _addonChat.Enqueue(new ChatLine(k, chat.SourceId, chat.FromName, chat.Text.Reveal()));
    }

    /// <summary>Draws the add-ons' windows and menu items as they are now: a window is installed once and shows the add-on's current
    /// window (empty while the add-on is off); menu items follow the add-ons exactly.</summary>
    private void SyncAddonScreen()
    {
        if (_addons is not { } host || _shell is null)
            return;
        var s = _shell.Shell;
        var windows = host.Windows.ToDictionary(w => $"addon.{w.AddonId}.{w.Window.Id}", w => w, StringComparer.Ordinal);
        foreach (var (key, (addonId, window)) in windows)
        {
            if (!_addonViews.TryGetValue(key, out var shown))
            {
                s.Strings.Define(key + ".title", window.Title);
                var view = new AddonWindowView(s.Strings, host, addonId, ResolveDropped) { HandedOver = AddonHandedOver };
                // a long window scrolls inside its floater
                var scroll = new ScrollContainer { Name = "addon_scroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
                scroll.AddChild(view);
                _shell.AddFloater(new FloaterSpec(key, key + ".title", new UiSize(380, 420), new UiSize(260, 200)), scroll);
                shown = (view, null);
            }
            if (!ReferenceEquals(shown.Window, window))
            {
                string floater = key;
                window.OpenRequested += open => { if (open) s.Floaters.Open(floater); else s.Floaters.Close(floater); };
                shown.View.Bind(window);
                _addonViews[key] = (shown.View, window);
            }
        }
        foreach (var key in _addonViews.Keys.Where(k => !windows.ContainsKey(k)).ToArray())
        {
            _addonViews[key].View.Bind(null);
            _addonViews[key] = (_addonViews[key].View, null);
            s.Floaters.Close(key);
        }

        SyncAddonQuickBar(host);
        SyncAddonDock(host);
        foreach (var id in _addonMenu.Keys.Concat(_addonSubmenus).ToArray())
            s.Menus.RemoveEntry("me", id);
        _addonMenu.Clear();
        _addonSubmenus.Clear();
        var items = host.MenuItems;
        var grouped = new Dictionary<string, List<MenuEntry>>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            var (addonId, label, chosen) = items[i];
            string id = $"addon.{addonId}.menu.{items.Take(i).Count(m => m.AddonId == addonId)}";
            int split = label.IndexOf(" > ", StringComparison.Ordinal);
            string? group = split > 0 ? label[..split] : null;
            s.Strings.Define(id, split > 0 ? label[(split + 3)..] : label);
            _addonMenu[id] = (addonId, chosen);
            string command = "addon.command." + id;
            if (!s.Commands.Has(command))
                s.Commands.Register(command, () => { if (_addonMenu.TryGetValue(id, out var run)) host.Run(run.AddonId, run.Chosen); });
            var entry = new MenuEntry(id, id, command, Order: 9);
            if (group is null)
                s.Menus.AddEntry("me", entry);
            else
                (grouped.TryGetValue(group, out var list) ? list : grouped[group] = []).Add(entry);
        }
        foreach (var (group, children) in grouped)
        {
            // "RLV > Restrictions…" is the item Restrictions… in a submenu RLV
            string id = "addon.group." + group.ToLowerInvariant().Replace(' ', '_');
            s.Strings.Define(id, group);
            _addonSubmenus.Add(id);
            s.Menus.AddEntry("me", new MenuEntry(id, id, "", Order: 9, Children: children));
        }
    }

    private readonly Dictionary<DockItem, AddonDockItem> _addonDock = new();
    private bool _addonDockWired;

    /// <summary>The add-ons' dock items (interface 0.5): each becomes a button in the dock's Make group, in the order the add-ons were
    /// turned on, with its state kept up to date; an add-on turned off takes its buttons with it.</summary>
    private void SyncAddonDock(AddonHost host)
    {
        if (_shell is null)
            return;
        var dock = _shell.DockModel;
        if (!_addonDockWired)
        {
            _addonDockWired = true;
            _shell.AddonDockUsed += (item, index) =>
            {
                if (!_addonDock.TryGetValue(item, out var source))
                    return;
                if (index < 0)
                    source.Click();
                else
                    source.ChooseMenu(index);
            };
        }
        var wanted = host.DockItems;
        foreach (var (shown, source) in _addonDock.ToArray())
            if (!wanted.Any(w => ReferenceEquals(w.Item, source)))
            {
                dock.RemoveAddonItem(shown.Owner!, shown.Id);
                _addonDock.Remove(shown);
            }
        foreach (var (addonId, source) in wanted)
        {
            var shown = _addonDock.FirstOrDefault(p => ReferenceEquals(p.Value, source)).Key;
            if (shown is null)
            {
                var kind = source.Kind switch { AddonDockKind.Toggle => DockItemKind.Toggle, AddonDockKind.ToggleWithMenu => DockItemKind.ToggleWithMenu, _ => DockItemKind.Action };
                shown = dock.AddAddonItem(addonId, source.Id, source.Label, kind, source.Icon, source.Tooltip, Menu(source));
                if (shown is null)
                    continue;
                _addonDock[shown] = source;
            }
            dock.SetOn(shown, source.On);
            dock.SetAttention(shown, source.Attention, source.Count);
            if (!shown.Menu.SequenceEqual(Menu(source)))
                dock.SetMenu(shown, Menu(source));
        }
    }

    private static DockMenuEntry[] Menu(AddonDockItem item) =>
        item.Menu.Select(m => new DockMenuEntry(m.Label, m.Icon, m.Checked, m.Separator)).ToArray();

    /// <summary>The add-ons' quick controls, at the end of the toolbar's row: one small row per add-on, drawn by the same view as windows.</summary>
    private void SyncAddonQuickBar(AddonHost host)
    {
        if (_shell is null)
            return;
        var byAddon = host.QuickControls.GroupBy(q => q.AddonId).ToDictionary(g => g.Key, g => g.Select(q => q.Control).ToArray(), StringComparer.Ordinal);
        foreach (var (addonId, controls) in byAddon)
        {
            if (!_addonQuick.TryGetValue(addonId, out var shown))
            {
                var view = new AddonWindowView(_shell.Shell.Strings, host, addonId, ResolveDropped, row: true) { Name = "addon_quick_" + addonId, HandedOver = AddonHandedOver };
                _shell.ToolbarRow.AddChild(view);
                shown = (view, AddonWindow.Create("quick", ""));
                _addonQuick[addonId] = shown;
                view.Bind(shown.Row);
            }
            if (!shown.Row.Controls.SequenceEqual(controls))
            {
                shown.Row.SetControls(controls);
                _shell.RefitToolbar();
            }
        }
        foreach (var (addonId, shown) in _addonQuick)
        {
            if (!byAddon.ContainsKey(addonId) && shown.Row.Controls.Count > 0)
            {
                shown.Row.SetControls([]);
                _shell.RefitToolbar();
            }
        }
    }

    // ---------------- development builds ----------------

#if NV_AUTOMATION
    /// <summary>The add-ons as the automation sees and switches them (development builds only).</summary>
    public object? AddonsCall(string action, string? id)
    {
        EnsureAddons();
        if (_addons is not { } host)
            return null;
        switch (action)
        {
            case "on": if (id is null || !host.SetEnabled(id, true, grantPermissions: true)) return new { error = "on" }; break;
            case "off": if (id is null || !host.SetEnabled(id, false)) return new { error = "off" }; break;
            case "open": _shell?.Shell.Floaters.Open(AddonsPanel.FloaterId); break;
            case "rlv": return RlvState();
            case "diagnose":
            {
                // "<add-on id>" or "<add-on id>/<action>": what a running add-on's own diagnostics answer
                var parts = (id ?? "").Split('/', 2);
                return host.Diagnose(parts[0], parts.Length > 1 ? parts[1] : "state", null) ?? new { error = "diagnose" };
            }
        }
        return new
        {
            addons = host.Addons.Select(a => new
            {
                id = a.Id,
                built_in = a.BuiltIn,
                job = a.Manifest.Job,
                enabled = a.Enabled,
                running = a.Running,
                permissions = a.Manifest.Permissions,
                granted = a.Granted,
                note = a.Note is not null,
            }).ToArray(),
            windows = host.Windows.Select(w => $"{w.AddonId}.{w.Window.Id}").ToArray(),
            menu_items = host.MenuItems.Count,
            logged_in = _addonsInWorld,
        };
    }

    /// <summary>The overrider as the automation sees and drives it (development builds only): the add-on that fills the ao job answers.</summary>
    public object? OverriderCall(string action, string? text)
    {
        if (_addons?.ActiveFor("ao") is not { Running: true } ao || !_addonsInWorld || _addonsFor is not { IsEnded: false } session)
            return null;
        switch (action)
        {
            case "animations":
            {
                // the first animations of the user's own inventory (not the library), by id only: what a check may drop on the editor
                var inventory = session.Inventory;
                var found = inventory.AllItems().Where(i => i.Kind == ItemKind.Animation && !inventory.IsInLibrary(i.Id))
                    .OrderBy(i => i.Id).Take(int.TryParse(text, out int n) ? n : 8).ToArray();
                return new
                {
                    animations = found.Select(i => i.Id).ToArray(),
                    folders = found.Select(i => i.ParentId).Distinct().ToArray(),
                    folders_unloaded = inventory.UnloadedFolderCount,
                    folders_asked = found.Length == 0 ? inventory.EnsureAllLoaded() : 0,
                };
            }
            case "drop":
            {
                // drops ids on the set editor as the user drops inventory rows on it: "<ids>" on the editor's drop zone, or
                // "<zone id>|<ids>" on another zone (a state's row, "state_Standing")
                string zoneId = text is { } t0 && t0.Contains('|') ? t0[..t0.IndexOf('|')] : "drop";
                string idList = text is { } t1 && t1.Contains('|') ? t1[(t1.IndexOf('|') + 1)..] : text ?? "";
                var dropped = idList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => Guid.TryParse(t, out var g) ? ResolveDropped(g) : null).OfType<AddonDropped>().ToArray();
                if (dropped.Length == 0 || _addons.Windows.FirstOrDefault(w => w.AddonId == ao.Id && w.Window.Id == "editor").Window?.Controls.OfType<AddonDropZone>().FirstOrDefault(c => c.Id == zoneId) is not { } zone)
                    return new { error = "drop" };
                AddonHandedOver(dropped);
                _addons.Run(ao.Id, () => zone.Drop(dropped));
                break;
            }
        }
        return _addons.Diagnose(ao.Id, action is "drop" ? "review" : action, text);
    }

    /// <summary>
    /// Lays out a small set in the agent's own inventory for a live check (development builds only): the folders the
    /// Firestorm viewer keeps sets in, one set with two cycling stands, a walk and a fly, linked to the first four
    /// animations the inventory (the library included) holds. Answers the animations' asset ids; nothing when a set of
    /// that name is already there.
    /// </summary>
    public async System.Threading.Tasks.Task<object?> OverriderMakeSet(string name, double cycleSeconds)
    {
        if (_addonsFor is not { IsEnded: false } session || session.InventoryBuilder is not { } builder)
            return null;
        var inventory = session.Inventory;
        var animations = inventory.AllItems().Where(i => i.Kind == ItemKind.Animation && i.AssetId is not null)
            .OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Id).Take(4).ToArray();
        if (animations.Length < 4)
        {
            int queued = inventory.EnsureAllLoaded();
            return new { made = false, reason = "animations", found = animations.Length, folders_asked = queued, folders_unloaded = inventory.UnloadedFolderCount };
        }

        async System.Threading.Tasks.Task<Guid> Under(Guid parent, string folder)
        {
            if (inventory.ChildFolders(parent).FirstOrDefault(f => f.Name == folder) is { } have)
                return have.Id;
            return await builder.CreatePlainFolderAsync(parent, folder);
        }

        Guid top = await Under(inventory.RootId, "#Firestorm");
        Guid ao = top == Guid.Empty ? Guid.Empty : await Under(top, "#AO");
        if (ao == Guid.Empty)
            return new { made = false, reason = "folders", found = animations.Length, folders_asked = 0, folders_unloaded = inventory.UnloadedFolderCount };
        if (inventory.ChildFolders(ao).Any(f => f.Name.Split(':')[0] == name))
            return new { made = false, reason = "exists", found = animations.Length, folders_asked = 0, folders_unloaded = inventory.UnloadedFolderCount };

        Guid set = await builder.CreatePlainFolderAsync(ao, name);
        string time = cycleSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        Guid standing = await builder.CreatePlainFolderAsync(set, $"Standing:CT{time}:CY");
        Guid walking = await builder.CreatePlainFolderAsync(set, "Walking");
        Guid flying = await builder.CreatePlainFolderAsync(set, "Flying");
        int linked = await builder.CreateItemLinksAsync(standing, [(animations[0].Id, "0"), (animations[1].Id, "1")]);
        linked += await builder.CreateItemLinksAsync(walking, [(animations[2].Id, "0")]);
        linked += await builder.CreateItemLinksAsync(flying, [(animations[3].Id, "0")]);
        return new
        {
            made = set != Guid.Empty && standing != Guid.Empty && walking != Guid.Empty && flying != Guid.Empty && linked == 4,
            linked,
            stands = new[] { animations[0].AssetId, animations[1].AssetId },
            walk = animations[2].AssetId,
            fly = animations[3].AssetId,
        };
    }
#endif
}
