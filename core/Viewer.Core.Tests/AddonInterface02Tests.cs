// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Addons;
using Viewer.Core.Diagnostics;
using Viewer.Shell.Addons;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What interface 0.2 adds: changing the inventory only inside the folder the manifest names, a local preview of an
/// animation that the host ends when the add-on stops, and a control in the quick bar for add-ons that come with the
/// viewer only. A fake inventory and recording services; pure, runs in parallel.
/// </summary>
public class AddonInterface02Tests
{
    private sealed class Inv : IAddonInventory, IAddonInventoryWriter
    {
        public readonly Dictionary<Guid, (Guid Parent, string Name)> FolderById = [];
        public readonly Dictionary<Guid, (Guid Parent, string Name)> ItemById = [];
        public readonly List<string> Done = [];
        public Guid RootId { get; } = Guid.NewGuid();
        public bool IsComplete(Guid folder) => true;
        public void Fetch(Guid folder) { }
        public IReadOnlyList<AddonFolder> Folders(Guid folder) => FolderById.Where(f => f.Value.Parent == folder).Select(f => new AddonFolder(f.Key, f.Value.Name)).ToArray();
        public IReadOnlyList<AddonItem> Items(Guid folder) => ItemById.Where(i => i.Value.Parent == folder).Select(i => new AddonItem(i.Key, i.Value.Name, "", false, true, Guid.Empty, Guid.Empty)).ToArray();
        public Guid AssetOf(Guid linkItemId) => Guid.Empty;
        public void Refresh(Guid folder) { }

        public Guid Folder(Guid parent, string name) { var id = Guid.NewGuid(); FolderById[id] = (parent, name); return id; }
        public Guid Item(Guid parent, string name) { var id = Guid.NewGuid(); ItemById[id] = (parent, name); return id; }

        public Task<Guid> CreateFolderAsync(Guid parent, string name) { Done.Add("folder " + name); return Task.FromResult(Folder(parent, name)); }
        public Task<bool> LinkAsync(Guid folder, Guid target, string description) { Done.Add("link"); Item(folder, "link"); return Task.FromResult(true); }
        public Task<bool> RenameFolderAsync(Guid folder, string name) { Done.Add("rename " + name); FolderById[folder] = (FolderById[folder].Parent, name); return Task.FromResult(true); }
        public Task<bool> MoveToTrashAsync(Guid id, bool isFolder) { Done.Add("trash"); return Task.FromResult(true); }
    }

    private sealed class Services : IAddonServices
    {
        public readonly Inv Inv = new();
        public readonly List<Guid?> Local = [];
        public double Now => 0;
        public void SendAnimations(IReadOnlyList<(Guid Animation, bool Start)> requests) { }
        public bool Say(string text) => false;
        public IAddonInventory? Inventory => Inv;
        public IAddonInventoryWriter? InventoryWriter => Inv;
        public void PlayLocally(Guid? animation) => Local.Add(animation);
    }

    private sealed class Probe : IAddon
    {
        public IAddonContext? Context;
        public Action<IAddonContext>? OnStart;
        public void Start(IAddonContext context) { Context = context; OnStart?.Invoke(context); }
        public void Stop() { }
    }

    private static AddonManifest Manifest(string id, string? folder, params string[] permissions) =>
        new(id, "Name " + id, "1.0", "Someone", "MIT", "0.2", null, permissions, id + ".dll", null, folder);

    private static (AddonHost Host, Services Services, Probe Probe) Start(bool builtIn, string? folder, params string[] permissions)
    {
        var services = new Services();
        var host = new AddonHost(services, new CoreLog(new ListSink(), "test"), null);
        var probe = new Probe();
        host.Register(Manifest("probe", folder, permissions), () => probe, builtIn);
        host.StartEnabled();
        if (!builtIn)
            host.SetEnabled("probe", true, grantPermissions: true);
        return (host, services, probe);
    }

    [Fact]
    public async Task An_add_on_changes_the_inventory_only_inside_the_folder_its_manifest_names()
    {
        var (_, services, probe) = Start(builtIn: true, "#Firestorm/#AO", AddonPermissions.InventoryRead, AddonPermissions.InventoryWrite);
        var inv = services.Inv;
        var elsewhere = inv.Folder(inv.RootId, "Clothes");
        var shirt = inv.Item(elsewhere, "shirt");
        var writer = probe.Context!.InventoryWriter;
        Assert.NotNull(writer);

        // the folders of its path may be made, in order, and nothing else beside them
        var top = await writer.CreateFolderAsync(inv.RootId, "#Firestorm");
        Assert.NotEqual(Guid.Empty, top);
        Assert.Equal(Guid.Empty, await writer.CreateFolderAsync(inv.RootId, "Other"));
        var ao = await writer.CreateFolderAsync(top, "#AO");
        Assert.NotEqual(Guid.Empty, ao);
        Assert.Equal(Guid.Empty, await writer.CreateFolderAsync(elsewhere, "Inside clothes"));

        var set = await writer.CreateFolderAsync(ao, "Beach");
        var state = await writer.CreateFolderAsync(set, "Standing");
        Assert.NotEqual(Guid.Empty, state);
        Assert.True(await writer.LinkAsync(state, shirt, "0"));             // a link may point anywhere
        Assert.False(await writer.LinkAsync(elsewhere, shirt, "0"));        // but is made only inside
        Assert.True(await writer.RenameFolderAsync(set, "Beach:SO"));
        Assert.False(await writer.RenameFolderAsync(ao, "Gone"));           // never the folder itself
        Assert.False(await writer.RenameFolderAsync(elsewhere, "Renamed"));
        Assert.False(await writer.MoveToTrashAsync(ao, isFolder: true));
        Assert.False(await writer.MoveToTrashAsync(shirt, isFolder: false));
        Assert.True(await writer.MoveToTrashAsync(set, isFolder: true));

        Assert.Equal(["folder #Firestorm", "folder #AO", "folder Beach", "folder Standing", "link", "rename Beach:SO", "trash"], inv.Done);
    }

    [Fact]
    public void Asking_to_change_the_inventory_needs_the_folder_named_and_the_grant()
    {
        Assert.Null(AddonManifest.Parse("""{ "id": "x", "name": "x", "version": "1", "author": "a", "licence": "l", "interface": "0.2", "permissions": ["inventory.write"], "library": "x.dll" }""", out var problems));
        Assert.Contains(problems, p => p.Contains("inventory_folder"));
        var ok = AddonManifest.Parse("""{ "id": "x", "name": "x", "version": "1", "author": "a", "licence": "l", "interface": "0.2", "permissions": ["inventory.write"], "library": "x.dll", "inventory_folder": "#Firestorm/#AO" }""", out _);
        Assert.NotNull(ok);
        Assert.Equal("#Firestorm/#AO", ok.InventoryFolder);

        var (_, _, probe) = Start(builtIn: true, null, AddonPermissions.InventoryRead);
        Assert.Throws<AddonPermissionException>(() => probe.Context!.InventoryWriter);
    }

    [Fact]
    public void A_local_preview_reaches_the_viewer_and_ends_when_the_add_on_stops()
    {
        var (host, services, probe) = Start(builtIn: true, null, AddonPermissions.AnimationsPlay);
        var dance = Guid.NewGuid();
        probe.Context!.PlayLocally(dance);
        probe.Context.StopLocally();
        probe.Context.PlayLocally(dance);
        Assert.Equal([dance, null, dance], services.Local);

        host.SetEnabled("probe", false);
        Assert.Equal([dance, null, dance, null], services.Local);
    }

    // ---------------- the dock item call (interface 0.5) ----------------

    [Fact]
    public void A_dock_item_needs_the_window_grant_and_any_add_on_may_ask_for_one()
    {
        Assert.Equal("0.6", AddonInterface.Version);
        var (_, _, noWindow) = Start(builtIn: true, null, AddonPermissions.ChatSend);
        Assert.Throws<AddonPermissionException>(() => noWindow.Context!.AddDockItem("a", "A", AddonDockKind.Action));
        var (host, _, outside) = Start(builtIn: false, null, AddonPermissions.Window);
        var item = outside.Context!.AddDockItem("a", "Tool", AddonDockKind.Toggle, "plug", "Turns the tool on");
        Assert.NotNull(item);
        Assert.Equal(("probe", item!), Assert.Single(host.DockItems));
        Assert.Equal(("Tool", AddonDockKind.Toggle, "plug", "Turns the tool on"), (item.Label, item.Kind, item.Icon, item.Tooltip));
    }

    [Fact]
    public void An_add_on_gets_two_dock_items_at_most_and_they_go_when_it_is_turned_off()
    {
        var (host, _, probe) = Start(builtIn: true, null, AddonPermissions.Window);
        Assert.NotNull(probe.Context!.AddDockItem("a", "A", AddonDockKind.Action));
        Assert.Null(probe.Context.AddDockItem("a", "Again", AddonDockKind.Action));
        Assert.NotNull(probe.Context.AddDockItem("b", "B", AddonDockKind.Action));
        Assert.Null(probe.Context.AddDockItem("c", "C", AddonDockKind.Action));
        Assert.Equal(["a", "b"], host.DockItems.Select(d => d.Item.Id));
        host.SetEnabled("probe", false);
        Assert.Empty(host.DockItems);
    }

    [Fact]
    public void The_add_on_sets_a_dock_items_state_and_hears_its_clicks_and_menu_choices()
    {
        var (host, _, probe) = Start(builtIn: true, null, AddonPermissions.Window);
        var item = probe.Context!.AddDockItem("ao", "AO", AddonDockKind.ToggleWithMenu, menu: [new AddonDockMenuEntry("Main", Checked: true), new AddonDockMenuEntry("Edit sets...")]);
        Assert.NotNull(item);
        int changes = 0, clicks = 0;
        var chosen = new List<int>();
        item!.Changed += () => changes++;
        item.Clicked += () => clicks++;
        item.MenuChosen += chosen.Add;
        item.SetOn(true);
        item.SetAttention(true, 3);
        item.SetMenu([new AddonDockMenuEntry("Main"), new AddonDockMenuEntry("Beach", Checked: true), new AddonDockMenuEntry("Edit sets...")]);
        Assert.Equal((true, true, 3, 3), (item.On, item.Attention, item.Count, item.Menu.Count));
        item.Click();
        item.ChooseMenu(1);
        item.ChooseMenu(7);   // past the end: nothing
        Assert.Equal((3, 1), (changes, clicks));
        Assert.Equal([1], chosen);
        item.Remove();
        Assert.True(item.Removed);
        Assert.Empty(host.DockItems);
    }

    [Fact]
    public void Only_an_add_on_that_comes_with_the_viewer_may_put_a_control_in_the_quick_bar()
    {
        var toggle = new AddonToggle("ao_on") { Text = "AO" };
        var (host, _, probe) = Start(builtIn: true, null, AddonPermissions.Window);
        probe.Context!.AddQuickControl(toggle);
        var quick = Assert.Single(host.QuickControls);
        Assert.Equal(("probe", (AddonControl)toggle), quick);
        host.SetEnabled("probe", false);
        Assert.Empty(host.QuickControls);

        var (outsideHost, _, outside) = Start(builtIn: false, null, AddonPermissions.Window);
        Assert.Throws<AddonPermissionException>(() => outside.Context!.AddQuickControl(new AddonToggle("x")));
        Assert.Empty(outsideHost.QuickControls);
    }

    [Fact]
    public void A_drop_zone_hands_the_dropped_things_to_the_add_on_and_a_window_can_take_a_control_out()
    {
        var window = AddonWindow.Create("w", "W");
        var zone = window.Add(new AddonDropZone("drop") { Text = "Drop animations here" });
        var label = window.Insert(0, new AddonLabel("top"));
        IReadOnlyList<AddonDropped>? got = null;
        zone.Dropped += d => got = d;
        var dropped = new[] { new AddonDropped(Guid.NewGuid(), true, "Bought AO") };
        zone.Drop(dropped);
        Assert.Same(dropped, got);
        Assert.Equal(["top", "drop"], window.Controls.Select(c => c.Id));
        Assert.True(window.Remove(label));
        Assert.Equal(["drop"], window.Controls.Select(c => c.Id));
    }
}
