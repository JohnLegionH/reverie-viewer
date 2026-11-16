// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The menus' shortcuts are read from the menus when the key is pressed: every shortcut a menu shows reaches its entry with no menu ever
/// opened, a disabled entry does nothing, and while a text field holds the keyboard only chords with Ctrl or Alt reach the menus (the
/// official viewer's llviewerwindow.cpp handleKey, read as behaviour). The cases are generated from the menu model, so an entry added
/// later is covered without a new test. The engine's key events are checked in S-1 (S1Smoke KeysCheck).
/// </summary>
public class MenuShortcutRouteTests
{
    private sealed class Rig
    {
        public readonly ShellController Shell = new(StringTable.English(), () => { });
        public bool InWorld = true, Selection = true, Build;

        public Rig()
        {
            BuildMenu.Install(Shell.Menus, () => InWorld);
            BuildMenu.InstallEditing(Shell.Menus, () => InWorld, () => null, () => Selection);
            // one entry for every official shortcut the viewer wires, so the generated cases cover every one of them
            Shell.Menus.AddMenu("keys", "menu.help");
            foreach (var id in ViewerShortcuts.ByEntry.Keys)
                if (!Ids(Shell.Menus.Menus.SelectMany(m => m.Entries)).Contains(id))
                    Shell.Menus.AddEntry("keys", new MenuEntry(id, "menu.help.about", "cmd." + id, () => InWorld));
            foreach (var id in Ids(Shell.Menus.Menus.SelectMany(m => m.Entries)))
            {
                var entry = Flat(Shell.Menus.Menus.SelectMany(m => m.Entries)).First(e => e.Id == id);
                if (entry.CommandId.Length > 0 && !Shell.Commands.Has(entry.CommandId))
                    Shell.Commands.Register(entry.CommandId, () => { });
            }
        }

        public IReadOnlyList<TopMenuView> View => TopBarMenuMap.Build(Shell.Menus, Shell.Commands);
    }

    private static IEnumerable<MenuEntry> Flat(IEnumerable<MenuEntry> entries) =>
        entries.SelectMany(e => e.Children is { } kids ? Flat(kids).Prepend(e) : [e]);

    private static HashSet<string> Ids(IEnumerable<MenuEntry> entries) => Flat(entries).Select(e => e.Id).ToHashSet();

    private static IEnumerable<TopMenuEntryView> FlatView(IEnumerable<TopMenuEntryView> entries) =>
        entries.SelectMany(e => e.Children is { } kids ? FlatView(kids).Prepend(e) : [e]);

    [Theory]
    [InlineData("control|I", "I", true, false, false)]
    [InlineData("control|shift|L", "L", true, true, false)]
    [InlineData("alt|shift|N", "N", false, true, true)]
    [InlineData("control|alt|U", "U", true, false, true)]
    [InlineData("control|3", "3", true, false, false)]
    [InlineData("shift|X", "X", false, true, false)]
    [InlineData("G", "G", false, false, false)]
    [InlineData("control|b", "B", true, false, false)]
    [InlineData("control|.", ".", true, false, false)]   // Select Next Part, as the official menu writes it
    [InlineData("control|,", ",", true, false, false)]   // Select Previous Part
    public void A_shortcut_reads_as_its_key_and_modifiers(string shortcut, string key, bool ctrl, bool shift, bool alt) =>
        Assert.Equal(new KeyChord(key, ctrl, shift, alt), KeyChord.Parse(shortcut));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("F1")]
    [InlineData("control|grave")]
    [InlineData("control|period")]
    public void A_shortcut_that_names_no_letter_or_digit_reads_as_none(string? shortcut) => Assert.Null(KeyChord.Parse(shortcut));

    [Fact]
    public void Every_shortcut_a_menu_shows_is_listed_submenus_included()
    {
        var rig = new Rig();
        var shown = FlatView(rig.View.SelectMany(m => m.Entries)).Where(e => KeyChord.Parse(e.Shortcut) is not null).Select(e => e.Id).ToArray();
        Assert.Contains("content.inventory", shown);
        Assert.Contains(BuildMenu.GridSnapId, shown);   // inside the Options submenu
        Assert.Equal(shown, MenuShortcuts.All(rig.View).Select(s => s.Entry.Id).ToArray());
        Assert.All(MenuShortcuts.All(rig.View), s => Assert.Equal(KeyChord.Parse(s.Entry.Shortcut), s.Chord));
    }

    [Fact]
    public void Every_enabled_entry_is_reached_by_its_shortcut_with_no_menu_opened()
    {
        var rig = new Rig();
        // the build mode the grid entries need: a stand-in controller is not needed, the entries' enabled state is what the route reads
        var all = MenuShortcuts.All(rig.View);
        Assert.True(all.Count >= ViewerShortcuts.ByEntry.Count + 8, $"only {all.Count} shortcuts listed");
        foreach (var (entry, chord) in all.Where(s => s.Entry.Enabled))
            Assert.Equal(entry.Id, MenuShortcuts.Route(rig.View, chord, textEntry: false)?.Id);
    }

    [Fact]
    public void A_disabled_entry_does_nothing_on_its_shortcut()
    {
        var rig = new Rig { Selection = false };
        Assert.Equal(BuildMenu.ToggleId, MenuShortcuts.Route(rig.View, new KeyChord("B", Control: true), false)?.Id);
        Assert.Null(MenuShortcuts.Route(rig.View, new KeyChord("L", Control: true), false));   // Link needs a selection
        Assert.Null(MenuShortcuts.Route(rig.View, new KeyChord("G"), false));                  // the grid's keys need build mode
        rig.InWorld = false;
        Assert.Null(MenuShortcuts.Route(rig.View, new KeyChord("I", Control: true), false));
    }

    [Fact]
    public void Ctrl_L_and_Ctrl_Shift_L_reach_different_entries()
    {
        var rig = new Rig();
        Assert.Equal(BuildMenu.LinkId, MenuShortcuts.Route(rig.View, new KeyChord("L", Control: true), false)?.Id);
        Assert.Equal(BuildMenu.UnlinkId, MenuShortcuts.Route(rig.View, new KeyChord("L", Control: true, Shift: true), false)?.Id);
        Assert.Null(MenuShortcuts.Route(rig.View, new KeyChord("L"), false));
    }

    [Fact]
    public void While_typing_only_chords_with_ctrl_or_alt_reach_the_menus()
    {
        var rig = new Rig();
        foreach (var (entry, chord) in MenuShortcuts.All(rig.View).Where(s => s.Entry.Enabled))
        {
            var routed = MenuShortcuts.Route(rig.View, chord, textEntry: true);
            if (chord.Modified && !MenuShortcuts.TextFieldFirst(chord))   // Ctrl+Z and Ctrl+Y stay with the field: its own undo and redo
                Assert.Equal(entry.Id, routed?.Id);
            else
                Assert.Null(routed);
        }
        Assert.Contains(MenuShortcuts.All(rig.View), s => !s.Chord.Modified);   // the rule was exercised both ways
    }

    [Fact]
    public void A_check_entry_is_reached_and_its_command_toggles_it()
    {
        var shell = new ShellController(StringTable.English(), () => { });
        bool on = false;
        shell.Menus.AddMenu("build", "menu.build");
        shell.Menus.AddEntry("build", new MenuEntry("t.check", "menu.build", "t.check", Checked: () => on, Shortcut: "control|J"));
        shell.Commands.Register("t.check", () => on = !on);
        var view = TopBarMenuMap.Build(shell.Menus, shell.Commands);
        var entry = MenuShortcuts.Route(view, new KeyChord("J", Control: true), false);
        Assert.NotNull(entry);
        Assert.False(entry.Checked);
        shell.Commands.Execute(entry.CommandId!);
        Assert.True(MenuShortcuts.Route(TopBarMenuMap.Build(shell.Menus, shell.Commands), new KeyChord("J", Control: true), false)?.Checked);
    }
}
