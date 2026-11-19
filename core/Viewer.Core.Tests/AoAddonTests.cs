// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Addon.Ao;
using Viewer.Addons;
using Viewer.Core.Diagnostics;
using Viewer.Shell.Addons;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The animation overrider as the built-in add-on for the "ao" job: its window, its switch kept in its own settings, its
/// set played on the region's stand, and switching the add-on off and on through the host. Pure, runs in parallel.
/// </summary>
public class AoAddonTests
{
    private static readonly Guid Stand = Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f");
    private static readonly Guid SetStand = Guid.Parse("5c6d7e8f-0000-4000-8000-0000000000a1");

    private sealed class Inventory : IAddonInventory
    {
        private readonly Dictionary<Guid, List<AddonFolder>> _folders = [];
        private readonly Dictionary<Guid, List<AddonItem>> _items = [];
        public Guid RootId { get; } = Guid.NewGuid();
        public bool IsComplete(Guid folder) => true;
        public void Fetch(Guid folder) { }
        public IReadOnlyList<AddonFolder> Folders(Guid folder) => _folders.TryGetValue(folder, out var f) ? f : [];
        public IReadOnlyList<AddonItem> Items(Guid folder) => _items.TryGetValue(folder, out var i) ? i : [];
        public Guid AssetOf(Guid linkItemId) => _items.Values.SelectMany(i => i).FirstOrDefault(i => i.Id == linkItemId).AssetId;
        public void Refresh(Guid folder) { }

        public Guid Folder(Guid parent, string name)
        {
            var id = Guid.NewGuid();
            if (!_folders.TryGetValue(parent, out var list))
                _folders[parent] = list = [];
            list.Add(new AddonFolder(id, name));
            return id;
        }

        public void Link(Guid folder, string name, Guid asset)
        {
            if (!_items.TryGetValue(folder, out var list))
                _items[folder] = list = [];
            list.Add(new AddonItem(Guid.NewGuid(), name, "0", true, true, Guid.NewGuid(), asset));
        }
    }

    private sealed class Services : IAddonServices
    {
        public double Clock;
        public readonly List<(Guid Animation, bool Start)> Sent = [];
        public Inventory? Held;
        public double Now => Clock;
        public void SendAnimations(IReadOnlyList<(Guid Animation, bool Start)> requests) => Sent.AddRange(requests);
        public bool Say(string text) => false;
        public IAddonInventory? Inventory => Held;
    }

    private static (AddonHost Host, Services Services) Rig()
    {
        var services = new Services { Held = new Inventory() };
        var inv = services.Held;
        var set = inv.Folder(inv.Folder(inv.Folder(inv.RootId, "#Firestorm"), "#AO"), "Main:**");
        inv.Link(inv.Folder(set, "Standing"), "stand", SetStand);
        var host = new AddonHost(services, new CoreLog(new ListSink(), "test"));
        string manifest = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "addons-ao", AddonManifest.FileName));
        host.Register(AddonManifest.Parse(manifest, out _)!, () => new AoAddon(), builtIn: true);
        host.StartEnabled();
        return (host, services);
    }

    /// <summary>Logs in, lets the engine read the sets (two inventory looks, five seconds apart) and has the region list the default stand.</summary>
    private static void InWorld(AddonHost host, Services services)
    {
        host.LoggedIn();
        host.Motion(MotionState.Standing);
        host.Playing([new PlayingAnimation(Stand, 1)]);
        host.Frame();
        services.Clock += 5;
        host.Frame();
    }

    private static AddonWindow Window(AddonHost host) => Assert.Single(host.Windows, w => w.AddonId == "ao" && w.Window.Id == "overrider").Window;

    private static AddonToggle Use(AddonHost host) => (AddonToggle)Window(host).Controls.Single(c => c.Id == "use");

    [Fact]
    public void The_overrider_add_on_fills_the_ao_job_and_offers_its_window_from_the_menu()
    {
        var (host, _) = Rig();
        Assert.Equal("ao", host.ActiveFor("ao")?.Id);
        Assert.Equal("Animation Overrider", Window(host).Title);
        Assert.Equal("Animation Overrider", Assert.Single(host.MenuItems).Label);
        Assert.Equal(["use", "sets", "stand_row", "reload", "edit", "status"], Window(host).Controls.Select(c => c.Id));
        Assert.Equal("Use my animation set", Use(host).Text);
        Assert.True(Use(host).On);                                     // on from the first run; the AO button turns it off
    }

    [Fact]
    public void On_a_fresh_install_it_plays_the_stand_as_soon_as_it_has_a_set_and_the_button_turns_it_off()
    {
        var (host, services) = Rig();
        InWorld(host, services);
        host.Frame();
        Assert.Contains((SetStand, true), services.Sent);          // no click needed
        services.Sent.Clear();

        host.Run("ao", () => Use(host).Turn(false));
        host.Frame();
        Assert.False(Use(host).On);
        Assert.Contains((SetStand, false), services.Sent);
    }

    [Fact]
    public void Turned_on_it_plays_the_sets_stand_on_the_regions_stand_and_lists_the_set()
    {
        var (host, services) = Rig();
        InWorld(host, services);
        host.Run("ao", () => Use(host).Turn(true));
        host.Frame();

        Assert.Contains((SetStand, true), services.Sent);
        var sets = (AddonChoice)Window(host).Controls.Single(c => c.Id == "sets");
        Assert.Equal(["Main"], sets.Items);
        Assert.Equal(0, sets.Selected);
    }

    [Fact]
    public void Switching_the_add_on_off_stops_its_animations_and_switching_it_on_resumes_them()
    {
        var (host, services) = Rig();
        InWorld(host, services);
        host.Run("ao", () => Use(host).Turn(true));
        host.Frame();
        Assert.Contains((SetStand, true), services.Sent);
        services.Sent.Clear();

        Assert.True(host.SetEnabled("ao", false));
        // the set's stand is stopped (the engine also asks the region for its default again, as turning its own switch off does)
        Assert.Equal((SetStand, false), services.Sent[0]);
        Assert.DoesNotContain((SetStand, true), services.Sent);
        Assert.Empty(host.Windows);
        services.Sent.Clear();

        Assert.True(host.SetEnabled("ao", true));
        host.Frame();
        services.Clock += 5;
        host.Frame();
        Assert.True(Use(host).On);                                      // its switch was kept in its own settings
        Assert.Contains((SetStand, true), services.Sent);
    }
}
