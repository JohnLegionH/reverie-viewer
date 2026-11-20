// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Gestures window (Avatar &gt; Gestures, Ctrl+G), as the official one behaves (llfloatergesture.cpp, floater_gesture.xml): the
/// gestures in the Gestures folder and every active one, sorted by name, the active ones marked; a gesture's chat trigger and key show
/// once its file is read; the activate button activates a selection unless all of it is active; Edit needs one gesture, Play a current
/// one. Ctrl+G and the Avatar menu entry open and close it, and the window takes its look from the shared components only. Pure
/// (the window's source is read as text), runs in parallel.
/// </summary>
public class GestureListTests
{
    private static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");

    private static readonly Guid Owner = G(1), Root = G(2), LibOwner = G(3), LibRoot = G(4), Gestures = G(10), Sub = G(11), Trash = G(12), Objects = G(13);
    private static readonly Guid Wave = G(20), Bow = G(21), Clap = G(22), Nested = G(23), Trashed = G(24), Elsewhere = G(25), Link = G(26), Missing = G(27);

    private static InventoryItemData Gesture(Guid id, Guid parent, string name, Guid? linked = null) =>
        new(id, parent, name, ItemKind.Gesture, 21, 20, 0, null, linked);

    /// <summary>My Inventory with a Gestures folder (holding a subfolder), the Trash and an Objects folder, each with gestures.</summary>
    private static InventoryModel Inventory()
    {
        var model = new InventoryModel((_, _) => { });
        InventoryFolderData F(Guid id, Guid parent, string name, FolderKind kind = FolderKind.None) => new(id, parent, name, kind, 1, 1);
        var tops = new[] { F(Gestures, Root, "Gestures", FolderKind.Gesture), F(Trash, Root, "Trash", FolderKind.Trash), F(Objects, Root, "Objects", FolderKind.Object) };
        model.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot,
            new[] { new InventoryFolderData(Root, Guid.Empty, "My Inventory", FolderKind.Root, 1, -1) }.Concat(tops).Append(F(Sub, Gestures, "Mine")).ToArray(),
            new[] { new InventoryFolderData(LibRoot, Guid.Empty, "Library", FolderKind.Root, 1, 0) }));
        model.Apply(new InventoryFolderFetched(Root, true, 2, tops.Length, tops, []));
        model.Apply(new InventoryFolderFetched(Gestures, true, 2, 4, [F(Sub, Gestures, "Mine")],
            [Gesture(Wave, Gestures, "wave"), Gesture(Bow, Gestures, "Bow"), Gesture(Clap, Gestures, "Clap"), Gesture(Link, Gestures, "Wave link", linked: Wave)]));
        model.Apply(new InventoryFolderFetched(Sub, true, 2, 1, [], [Gesture(Nested, Sub, "Nested")]));
        model.Apply(new InventoryFolderFetched(Trash, true, 2, 1, [], [Gesture(Trashed, Trash, "Thrown away")]));
        model.Apply(new InventoryFolderFetched(Objects, true, 2, 1, [], [Gesture(Elsewhere, Objects, "Elsewhere")]));
        return model;
    }

    private static GestureDocument Doc(string trigger, byte key, uint mask) => new() { Trigger = trigger, Key = key, Mask = mask };

    private static IReadOnlyList<GestureListRow> Rows(IReadOnlyCollection<Guid> active, Func<Guid, GestureDocument?>? loaded = null) =>
        GestureListRows.Build(Inventory(), active, loaded ?? (_ => null), "Loading...");

    // ---------------- the rows ----------------

    [Fact]
    public void The_list_holds_the_Gestures_folder_and_the_active_ones_sorted_by_name_without_links_or_trash()
    {
        var rows = Rows([Elsewhere]);
        // the Gestures folder and its subfolders (collectDescendentsIf, EXCLUDE_TRASH), and an active gesture kept elsewhere
        Assert.Equal(["Bow", "Clap", "Elsewhere", "Nested", "wave"], rows.Select(r => r.Name).ToArray());
        Assert.DoesNotContain(rows, r => r.ItemId == Trashed || r.ItemId == Link);
    }

    [Fact]
    public void Active_gestures_are_marked_and_show_their_trigger_and_key_once_read_inactive_ones_show_none()
    {
        var docs = new Dictionary<Guid, GestureDocument> { [Wave] = Doc("/wave", GestureKeys.F2, GestureKeys.MaskShift) };
        var rows = Rows([Wave, Clap], id => docs.GetValueOrDefault(id));
        var wave = Assert.Single(rows, r => r.ItemId == Wave);
        Assert.True(wave.Active);
        Assert.Equal("/wave", wave.Trigger);
        Assert.Equal("Shift+F2", wave.Shortcut);
        var clap = Assert.Single(rows, r => r.ItemId == Clap);   // active, its file not read yet
        Assert.True(clap.Active);
        Assert.Equal("", clap.Trigger);
        Assert.Equal(GestureListRows.NoShortcut, clap.Shortcut);
        var bow = Assert.Single(rows, r => r.ItemId == Bow);
        Assert.False(bow.Active);
        Assert.Equal("", bow.Trigger);
        Assert.Equal("---", bow.Shortcut);
    }

    [Fact]
    public void An_active_gesture_the_inventory_does_not_hold_yet_shows_as_loading()
    {
        var rows = Rows([Missing]);
        var row = Assert.Single(rows, r => r.ItemId == Missing);
        Assert.Equal("Loading...", row.Name);
        Assert.True(row.Active);
    }

    [Theory]
    [InlineData(GestureKeys.None, GestureKeys.MaskNone, "---")]
    [InlineData(GestureKeys.F3, GestureKeys.MaskNone, "F3")]
    [InlineData(GestureKeys.F9, GestureKeys.MaskControl, "Ctrl+F9")]
    [InlineData(GestureKeys.F12, GestureKeys.MaskShift, "Shift+F12")]
    [InlineData(GestureKeys.F2, GestureKeys.MaskControl | GestureKeys.MaskShift, "Ctrl+Shift+F2")]
    public void The_key_reads_as_the_official_accelerator_text(byte key, uint mask, string text) =>
        Assert.Equal(text, GestureListRows.Shortcut(key, mask));

    // ---------------- the selection and the buttons ----------------

    [Fact]
    public void The_first_row_is_chosen_when_the_list_fills_and_a_chosen_row_stays_chosen_when_it_is_rebuilt()
    {
        var list = new GestureList();
        list.SetRows(Rows([]));
        Assert.Equal(Bow, list.Current);                  // postBuild selectFirstItem
        Assert.Equal([Bow], list.Selected.ToArray());
        list.Click(Clap, add: false);
        list.SetRows(Rows([Clap]));                         // refreshAll keeps the chosen row
        Assert.Equal(Clap, list.Current);
        Assert.Equal([Clap], list.Selected.ToArray());
    }

    [Fact]
    public void With_nothing_to_choose_no_button_works()
    {
        var list = new GestureList();
        list.SetRows([]);
        Assert.Equal(new GestureListButtons(GestureToggle.None, false, false), list.Buttons);
        Assert.Empty(list.Toggle());
    }

    [Fact]
    public void One_inactive_gesture_chosen_offers_Activate_Edit_and_Play()
    {
        var list = new GestureList();
        list.SetRows(Rows([]));
        list.Click(Clap, add: false);
        Assert.Equal(new GestureListButtons(GestureToggle.Activate, true, true, false, true), list.Buttons);
    }

    [Fact]
    public void One_active_gesture_chosen_offers_Deactivate_Edit_and_Play()
    {
        var list = new GestureList();
        list.SetRows(Rows([Clap]));
        list.Click(Clap, add: false);
        Assert.Equal(new GestureListButtons(GestureToggle.Deactivate, true, true, false, true), list.Buttons);
    }

    [Fact]
    public void Several_chosen_offer_no_Edit_and_activate_all_when_mixed()
    {
        var list = new GestureList();
        list.SetRows(Rows([Clap]));
        list.Click(Clap, add: false);
        list.Click(Bow, add: true);
        Assert.Equal(2, list.Selected.Count);
        Assert.Equal(Bow, list.Current);
        // edit_gesture needs exactly one (isActionEnabled); a mixed selection is activated (onActivateBtnClick is_mixed)
        Assert.Equal(new GestureListButtons(GestureToggle.Activate, false, true, false, true), list.Buttons);
        Assert.Equal([(Bow, true)], list.Toggle().ToArray());
        // all active: every one is deactivated
        list.SetRows(Rows([Clap, Bow]));
        Assert.Equal(GestureToggle.Deactivate, list.Buttons.Toggle);
        Assert.Equal(new[] { (Bow, false), (Clap, false) }, list.Toggle().OrderBy(c => c.ItemId).ToArray());
        // a second click with Ctrl takes a row out of the choice
        list.Click(Bow, add: true);
        Assert.Equal([Clap], list.Selected.ToArray());
        Assert.Equal(Clap, list.Current);
    }

    [Fact]
    public void Activating_and_deactivating_moves_the_mark()
    {
        var active = new HashSet<Guid>();
        var list = new GestureList();
        list.SetRows(Rows(active));
        list.Click(Clap, add: false);
        foreach (var (id, on) in list.Toggle())
            if (on) active.Add(id); else active.Remove(id);
        list.SetRows(Rows(active));
        Assert.True(Assert.Single(list.Rows, r => r.ItemId == Clap).Active);
        Assert.Equal(GestureToggle.Deactivate, list.Buttons.Toggle);
        foreach (var (id, on) in list.Toggle())
            if (on) active.Add(id); else active.Remove(id);
        list.SetRows(Rows(active));
        Assert.False(Assert.Single(list.Rows, r => r.ItemId == Clap).Active);
        Assert.DoesNotContain(list.Rows, r => r.Active);
    }

    // ---------------- Ctrl+G and the menu ----------------

    [Fact]
    public void Ctrl_G_is_the_Gestures_entry_s_official_shortcut()
    {
        Assert.Equal("control|G", ViewerShortcuts.ByEntry.GetValueOrDefault("me.gestures"));
        Assert.DoesNotContain(ViewerShortcuts.NotBuilt, n => n.Shortcut == "control|G");
        var menus = new MenuModel();
        menus.AddMenu("me", "menu.me");
        menus.AddEntry("me", new MenuEntry("me.gestures", "menu.me.gestures", "floater.gestures"));
        Assert.Equal("me.gestures", menus.ForShortcut("control|G")?.Id);
        Assert.NotEqual("menu.me.gestures", StringTable.English().Get("menu.me.gestures"));
    }

    private static string Window() => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "GesturesWindow.cs"));

    [Fact]
    public void The_menu_entry_toggles_the_window_as_Floater_Toggle_does()
    {
        string src = Window();
        Assert.Contains("new MenuEntry(\"me.gestures\", \"menu.me.gestures\", \"floater.gestures\"", src);
        Assert.Contains("s.Commands.Register(\"floater.gestures\"", src);
        Assert.Contains("s.Floaters.Toggle(FloaterId)", src);
        Assert.Contains("public const string FloaterId = \"gestures\"", src);
    }

    // ---------------- the window's look ----------------

    private static readonly int[] Spacing = [2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 24];

    [Fact]
    public void The_window_takes_its_look_from_the_shared_components_and_sets_no_colour_or_size_of_its_own()
    {
        string src = Window();
        string code = string.Join('\n', src.Split('\n').Select(l => l.IndexOf("//", StringComparison.Ordinal) is var c and >= 0 ? l[..c] : l));
        Assert.Empty(UiLiteralScanTests.Hits(src));
        Assert.DoesNotMatch(@"\bAddTheme\w*Override\b", code);
        Assert.DoesNotMatch(@"\bnew\s+Color\s*\(|\bColors\.", code);
        // a literal size is one of the spacing steps; any other size comes from the theme's constants
        foreach (Match m in Regex.Matches(code, @"new\s+Vector2\s*\(([^)]*)\)"))
            foreach (Match n in Regex.Matches(m.Groups[1].Value, @"(?<![\w.])(\d+(?:\.\d+)?)f?\b"))
                Assert.True(n.Groups[1].Value == "0" || Spacing.Contains((int)double.Parse(n.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)),
                    $"size {n.Value} in {m.Value}");
        foreach (var used in new[] { "ReverieStyle.ListRow", "ReverieStyle.ListRowSelected", "new RvButton(", "new RvIconButton(", "LucideIcons.Check", "LucideIcons.Add" })
            Assert.Contains(used, code);
    }
}
