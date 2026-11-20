// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The second half of the new look, third part: the gesture editor, the Gestures window (sorting, Stop, Delete, the gear menu, the
/// muted labels of an inactive row), Add-ons, the add-on windows and personal lighting. Source reads, pure rules and the list's
/// state; runs in parallel (no process-wide state).
/// </summary>
public class ReverieBatch3Tests
{
    private static string Host(params string[] path) => File.ReadAllText(Path.Combine([TestPaths.RepoRoot, "host", "Viewer.Godot", .. path]));

    private static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");

    private static GestureListRow Row(int n, string name, string trigger = "", string key = GestureListRows.NoShortcut, bool active = false) =>
        new(G(n), name, trigger, key, active);

    // ---- the add-on windows' prefix ----

    [Theory]
    [InlineData("addon.example.window", true)]
    [InlineData("addon.example.panel.2", true)]
    [InlineData("addon.", false)]
    [InlineData("addon", false)]
    [InlineData("addons", false)]
    [InlineData("notecard.4", true)]
    public void An_add_on_window_is_named_by_its_prefix(string id, bool perItem) =>
        Assert.Equal(perItem, ReverieRestyleRules.IsPerItemWindow(id));

    [Fact]
    public void The_batch_three_windows_are_on_the_restyle_list_by_their_ids()
    {
        string restyle = Host("Reverie", "ReverieRestyle.cs");
        foreach (var id in new[] { "GesturePanel.FloaterId", "AddonsPanel.FloaterId", "PersonalLightingFeature.FloaterId" })
            Assert.Contains(id, restyle);
    }

    [Fact]
    public void The_Gestures_window_is_not_restyled_because_it_is_built_from_the_Reverie_components_and_its_rows_would_shrink()
    {
        // its rows are label-less buttons laid over by their columns: the restyle sizes a button in a column to its label, which
        // would make every row zero wide
        Assert.DoesNotContain("GesturesFeature.FloaterId", Host("Reverie", "ReverieRestyle.cs"));
    }

    [Fact]
    public void An_inactive_gesture_row_draws_its_name_in_the_muted_look()
    {
        string window = Host("GesturesWindow.cs");
        Assert.Contains("row.Active ? ReverieStyle.CardHeading : ReverieStyle.Muted", window);
        Assert.DoesNotContain("ThemeTypeVariation = row.Active ? ReverieStyle.CardHeading : \"\"", window);
    }

    // ---- sorting ----

    [Fact]
    public void The_list_is_sorted_by_name_to_begin_with_and_a_click_on_the_same_column_reverses_it()
    {
        var list = new GestureList();
        list.SetRows([Row(1, "wave"), Row(2, "Bow"), Row(3, "Clap")]);
        Assert.Equal(GestureColumn.Name, list.SortColumn);
        Assert.True(list.SortAscending);
        Assert.Equal(["Bow", "Clap", "wave"], list.Rows.Select(r => r.Name).ToArray());
        list.SortBy(GestureColumn.Name);
        Assert.False(list.SortAscending);
        Assert.Equal(["wave", "Clap", "Bow"], list.Rows.Select(r => r.Name).ToArray());
        list.SortBy(GestureColumn.Name);
        Assert.True(list.SortAscending);
    }

    [Fact]
    public void A_click_on_another_column_sorts_by_it_ascending_with_the_name_breaking_ties()
    {
        var list = new GestureList();
        list.SetRows([Row(1, "wave", "/wave", "F2"), Row(2, "Bow", "/bow", "F5"), Row(3, "Clap", "/bow", "F3"), Row(4, "Zed")]);
        list.SortBy(GestureColumn.Trigger);
        Assert.Equal(GestureColumn.Trigger, list.SortColumn);
        Assert.True(list.SortAscending);
        Assert.Equal(["Zed", "Bow", "Clap", "wave"], list.Rows.Select(r => r.Name).ToArray());
        list.SortBy(GestureColumn.Key);
        Assert.True(list.SortAscending);
        Assert.Equal(["Zed", "wave", "Clap", "Bow"], list.Rows.Select(r => r.Name).ToArray());
        Assert.Equal([GestureListRows.NoShortcut, "F2", "F3", "F5"], list.Rows.Select(r => r.Shortcut).ToArray());
    }

    [Fact]
    public void The_sort_is_kept_when_the_rows_are_read_again_and_the_choice_stays()
    {
        var list = new GestureList();
        list.SetRows([Row(1, "wave"), Row(2, "Bow")]);
        list.SortBy(GestureColumn.Name);
        list.Click(G(1), add: false);
        list.SetRows([Row(2, "Bow"), Row(1, "wave"), Row(3, "Clap")]);
        Assert.Equal(["wave", "Clap", "Bow"], list.Rows.Select(r => r.Name).ToArray());
        Assert.Equal([G(1)], list.Selected.ToArray());
    }

    // ---- Delete and Stop ----

    [Fact]
    public void Delete_needs_a_chosen_row_and_names_every_chosen_gesture()
    {
        var list = new GestureList();
        Assert.False(list.Buttons.Delete);
        list.SetRows([Row(1, "wave"), Row(2, "Bow")]);
        Assert.True(list.Buttons.Delete);   // the first row is chosen when the list fills
        list.Click(G(1), add: true);
        Assert.Equal([G(2), G(1)], list.ToDelete().ToArray());
        list.Click(G(1), add: true);
        list.Click(G(2), add: true);
        Assert.False(list.Buttons.Delete);
        Assert.Empty(list.ToDelete());
    }

    [Fact]
    public void Stop_works_only_while_a_chosen_gesture_is_playing()
    {
        var list = new GestureList();
        list.SetRows([Row(1, "Bow"), Row(2, "wave")]);
        Assert.False(list.Buttons.Stop);
        list.SetPlaying([G(2)]);
        Assert.False(list.Buttons.Stop);   // Bow is chosen, wave plays
        list.Click(G(2), add: true);
        Assert.True(list.Buttons.Stop);
        Assert.Equal([G(2)], list.ToStop().ToArray());
        list.SetPlaying([]);
        Assert.False(list.Buttons.Stop);
        Assert.Empty(list.ToStop());
    }

    [Fact]
    public void The_window_has_Stop_Delete_a_confirmation_a_gear_menu_and_sortable_columns()
    {
        string window = Host("GesturesWindow.cs");
        foreach (var part in new[] { "\"stop_btn\"", "\"delete_btn\"", "\"confirm_bar\"", "\"gear_btn\"", "List.SortBy(", "StopRequested", "DeleteRequested" })
            Assert.Contains(part, window);
        Assert.Contains("LucideIcons.More", window);
    }

    [Fact]
    public void The_Delete_question_and_the_new_strings_exist_with_counted_forms()
    {
        string en = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Shell", "Strings", "en.json"));
        foreach (var key in new[] { "gestures.stop", "gestures.delete", "gestures.delete.many", "gestures.menu", "gestures.sort.tip", "count.gestures.one", "count.gestures.other" })
            Assert.Contains($"\"{key}\"", en);
    }

    [Fact]
    public void The_more_icon_is_carried()
    {
        Assert.Contains(LucideIcons.More, LucideIcons.All);
        Assert.NotNull(LucideIcons.Svg(LucideIcons.More, new UiColour(1f, 1f, 1f)));
    }
}
