// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The official shortcuts for the menu entries this viewer has (menu_viewer.xml): an entry added without a shortcut takes its
/// official one, every shortcut is used once, none takes a key the viewer already uses, and the ones not built are written down.
/// And when a menu opens and closes: on a click, switching on hover only while one is open, and the click that closed a menu
/// never opens it again. Pure, runs in parallel.
/// </summary>
public class ShortcutAndMenuToggleTests
{
    private static MenuModel Menus()
    {
        var menus = new MenuModel();
        foreach (var (id, label) in new[] { ("me", "menu.me"), ("communicate", "menu.communicate"), ("world", "menu.world"), ("content", "menu.content") })
            menus.AddMenu(id, label);
        menus.AddEntry("content", new MenuEntry("content.inventory", "menu.content.inventory", "floater.inventory"));
        menus.AddEntry("me", new MenuEntry("me.preferences", "menu.me.preferences", "floater.preferences"));
        menus.AddEntry("world", new MenuEntry("world.map", "menu.world.map", "floater.map"));
        menus.AddEntry("world", new MenuEntry("world.minimap", "menu.world.minimap", "floater.minimap"));
        menus.AddEntry("communicate", new MenuEntry("communicate.conversations", "menu.communicate.conversations", "floater.conversations"));
        menus.AddEntry("communicate", new MenuEntry("communicate.chat", "menu.communicate.chat", "floater.chat"));
        return menus;
    }

    [Theory]
    [InlineData("control|I", "content.inventory")]
    [InlineData("control|P", "me.preferences")]
    [InlineData("control|M", "world.map")]
    [InlineData("control|shift|M", "world.minimap")]
    [InlineData("control|T", "communicate.conversations")]
    [InlineData("control|H", "communicate.chat")]
    public void An_entry_added_without_a_shortcut_takes_its_official_one(string shortcut, string entry) =>
        Assert.Equal(entry, Menus().ForShortcut(shortcut)?.Id);

    [Fact]
    public void An_entry_keeps_a_shortcut_of_its_own()
    {
        var menus = new MenuModel();
        menus.AddMenu("content", "menu.content");
        menus.AddEntry("content", new MenuEntry("content.inventory", "menu.content.inventory", "floater.inventory", Shortcut: "control|alt|I"));
        Assert.Equal("content.inventory", menus.ForShortcut("control|alt|I")?.Id);
        Assert.Null(menus.ForShortcut("control|I"));
    }

    [Fact]
    public void Every_shortcut_is_used_once_and_none_takes_a_key_the_viewer_already_uses()
    {
        var all = ViewerShortcuts.ByEntry.Values.Select(s => s.ToLowerInvariant()).ToList();
        Assert.True(all.Count >= 10, $"{all.Count} shortcuts");
        Assert.Equal(all.Count, all.Distinct().Count());
        string[] taken = ["control|u", "control|4", "control|shift|o", "control|shift|y", "control|shift|t", "control|shift|n", "control|shift|z"];
        Assert.Empty(all.Intersect(taken));
        Assert.All(all, s => Assert.Matches("^(control\\|)?(alt\\|)?(shift\\|)?[a-z0-9]$", s));
    }

    [Fact]
    public void The_shortcuts_whose_feature_is_not_built_are_written_down_and_not_wired()
    {
        var notBuilt = ViewerShortcuts.NotBuilt.Select(n => n.Shortcut.ToLowerInvariant()).ToList();
        Assert.Contains("control|f", notBuilt);           // Search
        Assert.Contains("control|k", notBuilt);           // Camera controls
        Assert.Contains("control|grave", notBuilt);       // Snapshot to disk (the Snapshot window, control+shift+S, is built)
        Assert.Contains("control|5", notBuilt);           // Land tool (Build, control+B, is built)
        Assert.Empty(notBuilt.Intersect(ViewerShortcuts.ByEntry.Values.Select(s => s.ToLowerInvariant())));
        Assert.All(ViewerShortcuts.NotBuilt, n => Assert.False(string.IsNullOrWhiteSpace(n.Feature)));
    }

    private static string Canonical(string shortcut) =>
        shortcut.ToLowerInvariant().Replace("period", ".", StringComparison.Ordinal).Replace("comma", ",", StringComparison.Ordinal);

    [Fact]
    public void No_key_is_both_wired_and_listed_as_not_built()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Viewer.sln")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("repo root not found");
        // wired: a menu entry written with its own shortcut anywhere in the host, the official or borrowed shortcuts of entries, the command list's key,
        // and Ctrl+R (the Control-held binding of R that toggles always run)
        var wired = new HashSet<string>(ViewerShortcuts.ByEntry.Values.Select(Canonical));
        wired.UnionWith(ViewerShortcuts.FromOtherViewers.Values.Select(v => Canonical(v.Shortcut)));
        wired.Add(Canonical(ViewerShortcuts.CommandList));
        wired.Add(Canonical(ViewerShortcuts.CloseWindow));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "host"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "Shortcut: \"([^\"]+)\""))
                wired.Add(Canonical(m.Groups[1].Value));
        Assert.True(KeyBindings.Default.TryCommand("R", shift: false, out var run, control: true) && run == Viewer.Motion.MoveCommand.ToggleRun);
        wired.Add("control|r");
        var both = ViewerShortcuts.NotBuilt.Where(n => wired.Contains(Canonical(n.Shortcut))).Select(n => n.Shortcut + " (" + n.Feature + ")").ToArray();
        Assert.True(both.Length == 0, "wired and listed as not built: " + string.Join(", ", both));
    }

    [Fact]
    public void A_click_opens_a_menu_and_hovering_opens_nothing_while_none_is_open()
    {
        var t = new MenuToggle();
        Assert.False(t.HoverOpens("me"));
        Assert.True(t.PressOpens("me", 1000));
        t.Opened("me");
        Assert.Equal("me", t.Open);
    }

    [Fact]
    public void While_one_is_open_hovering_another_title_switches_to_it()
    {
        var t = new MenuToggle();
        t.Opened("me");
        Assert.True(t.HoverOpens("help"));
        Assert.False(t.HoverOpens("me"));
    }

    [Fact]
    public void A_click_on_the_open_menus_title_closes_it_and_it_stays_closed()
    {
        var t = new MenuToggle();
        t.Opened("me");
        Assert.False(t.PressOpens("me", 2000));           // the press on the open title closes it
        // the closing popup sees the same click first and hides; the title's press arrives just after
        t.Closed("me", 2000);
        Assert.Null(t.Open);
        Assert.False(t.PressOpens("me", 2000 + MenuToggle.SameClickMs / 2));
        Assert.False(t.HoverOpens("me"));
        Assert.False(t.HoverOpens("help"));
        Assert.True(t.PressOpens("me", 2000 + MenuToggle.SameClickMs + 50));   // a new click opens it again
        Assert.True(t.PressOpens("help", 2010));          // a click on another title is a new click
    }

    [Fact]
    public void Esc_or_a_click_outside_closes_it_and_hovering_opens_nothing_after()
    {
        var t = new MenuToggle();
        t.Opened("me");
        t.Closed("me", 5000);
        Assert.Null(t.Open);
        Assert.False(t.HoverOpens("me"));
        Assert.False(t.HoverOpens("help"));
    }

    [Fact]
    public void Ctrl_O_for_Outfits_is_labelled_as_Firestorm_s_not_the_official_viewer_s()
    {
        // the official viewer's Ctrl+O is Avatar > Now wearing... (menu_viewer.xml NowWearing); Firestorm puts Avatar > Outfits on it
        Assert.False(ViewerShortcuts.ByEntry.ContainsKey("content.outfits"));
        Assert.DoesNotContain("control|O", ViewerShortcuts.ByEntry.Values);
        Assert.Equal("Firestorm", ViewerShortcuts.SourceOf("content.outfits"));
        Assert.Equal("official", ViewerShortcuts.SourceOf("content.inventory"));
        // the binding itself is kept
        Assert.Equal("control|O", ViewerShortcuts.WithOfficial(new MenuEntry("content.outfits", "menu.outfits", "content.outfits")).Shortcut);
    }
}
