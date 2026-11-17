// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The command list (Ctrl+Shift+K): the feature map it carries, the places it opens, the search over this viewer's names and the official
/// viewer's and Firestorm's, and its keys.
/// </summary>
public class CommandListTests
{
    /// <summary>The Preferences pages, as the Preferences window lists them.</summary>
    private static readonly string[] Pages = ["general", "graphics", "sound", "chat", "moveview", "interface", "privacy", "cache", "building"];

    private static readonly string[] Windows =
    [
        "content.inventory", "content.outfits", "communicate.chat", "communicate.conversations", "communicate.people", "communicate.notifications",
        "me.appearance", "me.addons", "me.preferences", "app.preferences", "app.about", "help.about", "world.map", "world.minimap", "world.teleport",
        "world.about_land",
    ];

    /// <summary>Every menu entry the targets name, in its menu, labelled by its own string ("menu." + its id, as every menu entry here is).</summary>
    private static IReadOnlyList<TopMenuView> Menus()
    {
        var t = CommandTargets.Bundled;
        var ids = t.ByRow.Values.SelectMany(x => x).Concat(t.SlOf.Keys).Where(x => x.StartsWith("menu:", StringComparison.Ordinal))
            .Select(x => x[5..]).Distinct().ToArray();
        string MenuOf(string id) => id.StartsWith("env.", StringComparison.Ordinal) ? "world" : id[..id.IndexOf('.')];
        return ids.GroupBy(MenuOf).Select(g => new TopMenuView(g.Key, g.Key == "app" ? "topbar.app" : "menu." + g.Key, g.Key == "app",
            g.Select(id => new TopMenuEntryView(id, "menu." + id, Windows.Contains(id) ? "floater." + id : "cmd." + id, null,
                id == "content.inventory" ? "control|I" : null, true, null, null)).ToArray())).ToArray();
    }

    private static CommandIndex Index(IReadOnlyList<TopMenuView>? menus = null, Func<string, bool>? registered = null) =>
        CommandIndex.Build(menus ?? Menus(), registered ?? (_ => true), StringTable.English(), PreferenceCatalog.All, Pages, FeatureMap.Bundled, CommandTargets.Bundled);

    private sealed class MemoryStore : ISettingsStore
    {
        public string? Text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Viewer.sln")))
            root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    // ---- the data ----

    [Fact]
    public void The_bundled_map_has_every_feature_with_its_official_and_firestorm_places()
    {
        var map = FeatureMap.Bundled;
        Assert.Equal(273, map.Count);
        Assert.Equal(Enumerable.Range(1, 273), map.Select(r => r.Row));
        var stats = map.Single(r => r.Row == 252);
        Assert.Equal("Advanced > Performance Tools > Statistics Bar [hidden by default]", stats.SlPlaces[0]);
        Assert.Contains("Statistics Bar", stats.SlNames);
        Assert.Equal("Ctrl+Shift+1", stats.SlKey);
        var land = map.Single(r => r.Row == 164);
        Assert.Equal("About land", land.SlNames[0]);
        Assert.Contains(map, r => r.Aliases.Count > 0);
    }

    [Fact]
    public void Place_names_drop_the_hidden_mark_the_dots_the_toolbar_quotes_and_a_closing_note()
    {
        Assert.Equal("Inventory", CommandText.Leaf("Me > Inventory..."));
        Assert.Equal("Statistics Bar", CommandText.Leaf("Advanced > Performance Tools > Statistics Bar [hidden by default]"));
        Assert.Equal("Map", CommandText.Leaf("Toolbar button \"Map\""));
        Assert.Equal("Start location", CommandText.Leaf("Login screen > Start location (My last location / My home / typed region)"));
        Assert.Equal(["A > B", "C"], CommandText.Places("A > B ; none ; automatic (no menu) ; C"));
        Assert.Equal("Ctrl+Shift+K", CommandText.ShortcutText("control|shift|K"));
    }

    [Fact]
    public void Every_target_names_a_map_row_and_a_place_this_viewer_has()
    {
        var t = CommandTargets.Bundled;
        Assert.NotEmpty(t.ByRow);
        var rows = FeatureMap.Bundled.Select(r => r.Row).ToHashSet();
        var keys = PreferenceCatalog.All.Select(d => d.Key).ToHashSet();
        var strings = StringTable.English();
        string host = Path.Combine(RepoRoot(), "host");
        string source = string.Join("\n", Directory.EnumerateFiles(host, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            // the top bar's icon table names entries without making them
            .Select(f => f.EndsWith("DockModels.cs", StringComparison.Ordinal) ? File.ReadAllText(f).Split("Icons = new()")[0] : File.ReadAllText(f)));
        foreach (var (row, targets) in t.ByRow)
        {
            Assert.Contains(row, rows);
            foreach (var target in targets)
            {
                if (target.StartsWith("pref:", StringComparison.Ordinal))
                    Assert.Contains(target[5..], keys);
                else if (target.StartsWith("prefs:", StringComparison.Ordinal))
                    Assert.Contains(target[6..], Pages);
                else
                {
                    Assert.StartsWith("menu:", target);
                    string id = target[5..];
                    // the entry is made by the viewer's own code, and its label is in the string table
                    // (the sky choices are made in a loop as "env." + item: their item name is the literal)
                    bool made = source.Contains("\"" + id + "\"", StringComparison.Ordinal)
                        || id.StartsWith("env.", StringComparison.Ordinal) && source.Contains("(\"" + id[4..] + "\"", StringComparison.Ordinal);
                    Assert.True(made, $"no menu entry {id} in the viewer's source");
                    Assert.NotEqual("menu." + id, strings.Get("menu." + id));
                }
            }
        }
    }

    // ---- no dead entries ----

    [Fact]
    public void Every_result_opens_something_the_viewer_has_now()
    {
        var full = Index();
        Assert.NotEmpty(full.Items);
        Assert.All(full.Items, i => Assert.True(i.CommandId is not null || i.PrefKey is not null || i.PrefPageId is not null, i.Target));
        Assert.All(full.Items.Where(i => i.PrefKey is not null), i => Assert.NotNull(PreferenceCatalog.Find(i.PrefKey!)));
        // a command that is not registered (the feature is not installed: out of the world, About Land) is not offered
        var outside = Index(registered: c => c != "floater.world.about_land");
        Assert.Null(outside.Find("menu:world.about_land"));
        Assert.DoesNotContain(outside.Search("about land").Ordered, i => i.Target == "menu:world.about_land");
        // a target the menus do not hold is not offered either
        var fewer = Index(Menus().Where(m => m.Id != "world").ToArray());
        Assert.Null(fewer.Find("menu:world.map"));
    }

    // ---- the search ----

    [Fact]
    public void Every_official_name_and_alias_in_the_map_finds_its_feature_within_the_top_three()
    {
        var index = Index();
        var failures = new List<string>();
        int checkedNames = 0;
        foreach (var (row, _) in CommandTargets.Bundled.ByRow)
        {
            var feature = FeatureMap.Bundled.Single(r => r.Row == row);
            foreach (var name in feature.SlNames.Concat(feature.Aliases).Distinct())
            {
                checkedNames++;
                var top = index.Rank(name).Take(3).Select(x => x.Item).ToArray();
                if (!top.Any(i => i.Rows.Any(r => r.Row == row)))
                    failures.Add($"row {row} '{name}': {string.Join(", ", top.Select(i => i.Target))}");
            }
        }
        Assert.True(checkedNames > 50, $"only {checkedNames} names checked");
        Assert.Empty(failures);
    }

    [Fact]
    public void About_land_is_the_best_match_a_window_with_its_official_place()
    {
        var g = Index().Search("about land");
        Assert.NotNull(g.Best);
        Assert.Equal("menu:world.about_land", g.Best!.Target);
        Assert.Equal(CommandKind.Window, g.Best.Kind);
        Assert.Equal("World > About Land", g.Best.Place);
        Assert.Equal("SL", g.Best.ElsewhereViewer);
        Assert.Equal("World > About land", g.Best.ElsewherePlace);
    }

    [Fact]
    public void A_setting_result_names_its_preferences_page_and_row()
    {
        var g = Index().Search("draw distance");
        Assert.NotNull(g.Best);
        Assert.Equal("pref:draw_distance", g.Best!.Target);
        Assert.Equal(CommandKind.Setting, g.Best.Kind);
        Assert.Equal("draw_distance", g.Best.PrefKey);
        Assert.StartsWith("Preferences > Graphics > ", g.Best.Place);
    }

    [Fact]
    public void Results_are_grouped_best_match_then_windows_commands_settings()
    {
        var g = Index().Search("build");
        Assert.NotNull(g.Best);
        Assert.NotEmpty(g.Commands);
        Assert.All(g.Windows, i => Assert.Equal(CommandKind.Window, i.Kind));
        Assert.All(g.Commands, i => Assert.Equal(CommandKind.Command, i.Kind));
        Assert.All(g.Settings, i => Assert.Equal(CommandKind.Setting, i.Kind));
        Assert.DoesNotContain(g.Best, g.Windows.Concat(g.Commands).Concat(g.Settings));
        Assert.Equal(g.Best, g.Ordered[0]);
    }

    [Fact]
    public void A_firestorm_name_finds_the_feature_and_a_result_shows_firestorm_when_the_official_viewer_has_no_place()
    {
        var index = Index();
        // Firestorm calls the Outfits window "Outfits" under Avatar (Ctrl+O there)
        Assert.Contains(index.Rank("Outfits").Take(3), x => x.Item.Target == "menu:content.outfits");
        // an entry with an official place shows it, with the official shortcut
        var inv = index.Find("menu:content.inventory")!;
        Assert.Equal("SL", inv.ElsewhereViewer);
        Assert.Equal("Me > Inventory...", inv.ElsewherePlace);
        Assert.Equal("Ctrl+I", inv.ElsewhereShortcut);
        Assert.Equal("Ctrl+I", inv.Shortcut);
        // the add-on host has no official place: Firestorm's or none
        var addons = index.Find("menu:me.addons")!;
        Assert.NotEqual("SL", addons.ElsewhereViewer);
    }

    [Fact]
    public void Statistics_is_not_built_and_the_list_says_where_the_official_viewer_keeps_it()
    {
        var index = Index();
        Assert.Null(index.Search("statistics").Best);
        var row = index.NotBuilt("statistics");
        Assert.NotNull(row);
        Assert.Equal(252, row!.Row);
        Assert.Equal("Advanced > Performance Tools > Statistics Bar", CommandText.Shown(row.SlPlaces[0]));
        // a name the viewer has gives no such line
        Assert.Null(index.NotBuilt("inventory"));
    }

    // ---- keys and recent picks ----

    [Fact]
    public void Up_and_down_move_and_wrap_enter_opens_ctrl_enter_shows_its_place_esc_closes()
    {
        var state = new CommandListState(Index(), new CommandRecents(null));
        state.SetQuery("build");
        int n = state.Shown.Count;
        Assert.True(n >= 3);
        Assert.Equal(0, state.Selected);
        state.Key("Down");
        state.Key("Down");
        Assert.Equal(2, state.Selected);
        state.Key("Up");
        Assert.Equal(1, state.Selected);
        state.Key("Up");
        state.Key("Up");
        Assert.Equal(n - 1, state.Selected);
        state.Key("Down");
        Assert.Equal(0, state.Selected);
        var open = state.Key("Enter");
        Assert.Equal(CommandListAction.Open, open.Action);
        Assert.Equal(state.Shown[0], open.Item);
        var place = state.Key("Enter", control: true);
        Assert.Equal(CommandListAction.ShowPlace, place.Action);
        Assert.Equal(CommandListAction.Close, state.Key("Escape").Action);
        state.SetQuery("zzzz no such thing");
        Assert.Empty(state.Shown);
        Assert.Equal(CommandListAction.None, state.Key("Enter").Action);
    }

    [Fact]
    public void A_new_query_selects_the_first_result_and_a_disabled_result_does_not_open()
    {
        var menus = Menus().Select(m => m with { Entries = m.Entries.Select(e => e.Id == "build.undo" ? e with { Enabled = false } : e).ToArray() }).ToArray();
        var state = new CommandListState(Index(menus), new CommandRecents(null));
        state.SetQuery("undo");
        Assert.NotNull(state.Current);
        Assert.Equal("menu:build.undo", state.Current!.Target);
        Assert.False(state.Current.Enabled);
        Assert.Equal(CommandListAction.None, state.Key("Enter").Action);
        Assert.Equal(CommandListAction.ShowPlace, state.Key("Enter", control: true).Action);
        state.Key("Down");
        state.SetQuery("map");
        Assert.Equal(0, state.Selected);
    }

    [Fact]
    public void Recent_picks_show_first_when_the_field_is_empty_newest_first_and_survive_a_restart()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        var state = new CommandListState(Index(), new CommandRecents(settings));
        Assert.Empty(state.Shown);
        foreach (var q in new[] { "inventory", "world map", "draw distance", "inventory" })
        {
            state.SetQuery(q);
            Assert.Equal(CommandListAction.Open, state.Key("Enter").Action);
        }
        state.SetQuery("");
        Assert.Equal(["menu:content.inventory", "pref:draw_distance", "menu:world.map"], state.Shown.Select(i => i.Target));
        var again = new CommandListState(Index(), new CommandRecents(new SettingsService(store)));
        Assert.Equal(["menu:content.inventory", "pref:draw_distance", "menu:world.map"], again.Shown.Select(i => i.Target));
        var recents = new CommandRecents(null);
        for (int i = 0; i < 10; i++)
            recents.Add("t" + i);
        Assert.Equal(CommandRecents.Max, recents.Targets.Count);
        Assert.Equal("t9", recents.Targets[0]);
    }

    // ---- the shortcut ----

    [Fact]
    public void The_list_opens_on_ctrl_shift_k_and_ctrl_k_stays_camera_controls()
    {
        Assert.Equal("control|shift|K", ViewerShortcuts.CommandList);
        Assert.Contains(ViewerShortcuts.NotBuilt, s => s.Shortcut == "control|K" && s.Feature == "Camera controls");
        Assert.DoesNotContain(ViewerShortcuts.ByEntry.Values, s => s == "control|shift|K");
        Assert.DoesNotContain(ViewerShortcuts.FromOtherViewers.Values, s => s.Shortcut == "control|shift|K");
        Assert.DoesNotContain(ViewerShortcuts.NotBuilt, s => s.Shortcut == "control|shift|K");
        Assert.Equal(new KeyChord("K", Control: true, Shift: true), KeyChord.Parse(ViewerShortcuts.CommandList));
    }
}
