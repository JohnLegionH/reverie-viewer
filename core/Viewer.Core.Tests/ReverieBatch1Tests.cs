// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Viewer.Shell.Notifications;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The shared pieces the second half of the new look builds on (windows made once per item, parts outside a window, a variation set
/// again by code, the text area) and the small fixes made with them (Try again, the New folder wording, the notecard status line, the
/// close buttons, the title-bar inset). Pure rules and source reads; runs in parallel.
/// </summary>
public class ReverieBatch1Tests
{
    private static string Host(params string[] path) => File.ReadAllText(Path.Combine([TestPaths.RepoRoot, "host", "Viewer.Godot", .. path]));
    private static string Shell(params string[] path) => File.ReadAllText(Path.Combine([TestPaths.RepoRoot, "host", "Viewer.Shell", .. path]));

    // ---- windows named per item ----

    [Theory]
    [InlineData("notecard.1", true)]
    [InlineData("notecard.12", true)]
    [InlineData("script.1", true)]
    [InlineData("script.30", true)]
    [InlineData("notecard", false)]
    [InlineData("notecard.", false)]
    [InlineData("script_queue", false)]
    [InlineData("scripts.1", false)]
    [InlineData("gesture", false)]
    [InlineData("addon.example.window", true)]
    [InlineData("", false)]
    public void A_window_made_once_per_item_is_named_by_its_prefix(string id, bool perItem) =>
        Assert.Equal(perItem, ReverieRestyleRules.IsPerItemWindow(id));

    [Fact]
    public void The_restyle_lists_windows_by_exact_id_or_by_the_per_item_prefix_and_the_compile_queue_is_listed()
    {
        string restyle = Host("Reverie", "ReverieRestyle.cs");
        Assert.Contains("public static bool IsListed(string floaterId)", restyle);
        Assert.Contains("ReverieRestyleRules.IsPerItemWindow(floaterId)", restyle);
        Assert.Contains("ScriptQueueWindow.FloaterId", restyle);
        Assert.Contains("if (IsListed(floaterId))", restyle);
        Assert.DoesNotContain("FirstHalf", restyle);
    }

    // ---- a part outside a window ----

    [Fact]
    public void A_part_that_is_not_a_floater_restyles_itself_through_Apply_which_is_not_gated_by_the_window_list()
    {
        string restyle = Host("Reverie", "ReverieRestyle.cs");
        int from = restyle.IndexOf("public static void Apply(Node root", StringComparison.Ordinal);
        Assert.True(from > 0);
        string body = restyle[from..restyle.IndexOf("private static void Visit", from, StringComparison.Ordinal)];
        Assert.DoesNotContain("IsListed", body);
        Assert.Contains("Visit(root, place)", body);
        Assert.Contains("a part that is not a floater", restyle);
    }

    // ---- a variation set again by code ----

    [Theory]
    [InlineData("Label", "MutedLabel", true, ReverieVariations.Muted)]
    [InlineData("Label", "CaptionLabel", true, ReverieVariations.Caption)]
    [InlineData("Label", "ErrorLabel", true, "ErrorLabel")]
    [InlineData("Label", "", true, "")]
    [InlineData("Button", "PrimaryButton", true, ReverieVariations.Primary)]
    [InlineData("Button", "DangerButton", true, "DangerButton")]
    [InlineData("Label", "MutedLabel", false, "MutedLabel")]
    [InlineData("Label", "ErrorLabel", false, "ErrorLabel")]
    public void A_variation_set_again_after_the_restyle_keeps_the_Reverie_component(string kind, string classic, bool restyled, string expected) =>
        Assert.Equal(expected, ReverieRestyleRules.AfterSet(kind, classic, false, RestylePlace.Window, false, restyled));

    [Fact]
    public void The_count_label_and_the_preferences_status_line_set_their_look_through_SetVariation()
    {
        string restyle = Host("Reverie", "ReverieRestyle.cs");
        Assert.Contains("public static void SetVariation(Control control, string classic)", restyle);
        Assert.Contains("ReverieRestyleRules.AfterSet(", restyle);
        string notecard = Host("NotecardPanels.cs");
        Assert.Contains("ReverieRestyle.SetVariation(CountLabel, s.OverLimit ? UiTheme.ErrorLabel : UiTheme.MutedLabel)", notecard);
        Assert.DoesNotContain("CountLabel.ThemeTypeVariation =", notecard);
        string prefs = Host("PreferencesPanels.cs");
        Assert.Contains("ReverieRestyle.SetVariation(label, error ? UiTheme.ErrorLabel : UiTheme.MutedLabel)", prefs);
        Assert.DoesNotContain("label.ThemeTypeVariation =", prefs);
    }

    // ---- the text area ----

    [Theory]
    [InlineData("TextEdit", "", ReverieVariations.TextArea)]
    [InlineData("CodeEdit", "", ReverieVariations.TextArea)]
    [InlineData("TextEdit", "SomethingElse", null)]
    public void A_text_area_takes_the_text_area_variation(string kind, string current, string? expected) =>
        Assert.Equal(expected, ReverieRestyleRules.VariationFor(kind, current, false));

    [Fact]
    public void The_text_area_is_the_field_face_for_the_frame_only_and_leaves_a_code_editors_own_colours_alone()
    {
        string style = Host("Reverie", "ReverieStyle.cs");
        Assert.Contains("\"RvTextArea\"", style);
        Assert.Contains("SetTypeVariation(TextArea, \"TextEdit\")", style);
        int from = style.IndexOf("SetTypeVariation(TextArea", StringComparison.Ordinal);
        string part = style[from..Math.Min(style.Length, from + 1400)];
        foreach (var state in new[] { "\"normal\"", "\"focus\"", "\"read_only\"" })
            Assert.Contains("SetStylebox(" + state + ", TextArea", part);
        foreach (var colour in new[] { "background_color", "line_number_color", "current_line_color", "completion", "caret_background_color" })
            Assert.DoesNotContain("\"" + colour + "\", TextArea", part);
        Assert.Equal("RvTextArea", ReverieVariations.TextArea);
    }

    // ---- Try again ----

    [Theory]
    [InlineData(true, true, FolderLoad.Loading, false)]
    [InlineData(true, true, FolderLoad.Loaded, false)]
    [InlineData(true, true, FolderLoad.Failed, true)]
    [InlineData(true, false, FolderLoad.Failed, false)]
    [InlineData(false, true, FolderLoad.Failed, false)]
    public void Try_again_shows_only_for_a_selected_folder_whose_load_failed(bool bound, bool selected, FolderLoad state, bool shown) =>
        Assert.Equal(shown, InventoryButtons.ShowsRetry(bound, selected, state));

    [Fact]
    public void Try_again_is_hidden_after_a_good_retry_and_the_button_and_the_menu_follow_the_rule()
    {
        Assert.True(InventoryButtons.ShowsRetry(true, true, FolderLoad.Failed));
        Assert.False(InventoryButtons.ShowsRetry(true, true, FolderLoad.Loading));   // the retry is out
        Assert.False(InventoryButtons.ShowsRetry(true, true, FolderLoad.Loaded));    // and it came back
        string src = Host("InventoryPanels.cs");
        Assert.Contains("RetryButton.Visible = InventoryButtons.ShowsRetry(", src);
        Assert.DoesNotContain("RetryButton.Disabled", src);
        Assert.Contains("RetryInMenu(RetryButton.Visible)", src);
    }

    // ---- wording and small fixes ----

    private static Dictionary<string, string> En() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(Shell("Strings", "en.json"))!;

    [Fact]
    public void The_new_folder_action_has_one_sentence_case_wording_in_the_button_and_the_plus_menu()
    {
        var en = En();
        Assert.Equal("New folder", en["inventory.button.new_folder"]);
        Assert.Equal(en["inventory.button.new_folder"], en["inventory.new.folder"]);
    }

    [Fact]
    public void The_notecard_status_line_shows_the_place_and_the_byte_count_apart()
    {
        var en = En();
        Assert.True(en.TryGetValue("notecard.status.separator", out var separator), "notecard.status.separator is missing");
        Assert.Equal("·", separator);
        string src = Host("NotecardPanels.cs");
        int pos = src.IndexOf("foot.AddChild(PositionLabel);", StringComparison.Ordinal);
        int sep = src.IndexOf("notecard.status.separator", StringComparison.Ordinal);
        int count = src.IndexOf("foot.AddChild(CountLabel);", StringComparison.Ordinal);
        Assert.True(pos > 0 && sep > pos && count > sep, "the separator sits between the place and the count");
    }

    [Theory]
    [InlineData(false, false, "", "notifications.toast.close")]
    [InlineData(true, false, "", "notifications.toast.close.script_dialog")]
    [InlineData(false, false, "script.permission.request", "notifications.toast.close.permission")]
    [InlineData(false, true, "script.permission.caution", "notifications.toast.close.permission")]
    public void The_close_button_of_a_notice_names_what_it_closes(bool scriptDialog, bool caution, string bodyKey, string expected)
    {
        var n = new Notification { ScriptDialog = scriptDialog, Caution = caution, Body = bodyKey.Length == 0 ? NoticeText.Raw("x") : NoticeText.Of(bodyKey) };
        Assert.Equal(expected, NoticeClose.TooltipKey(n));
        var en = En();
        Assert.True(en.ContainsKey(expected));
    }

    [Fact]
    public void The_close_buttons_that_were_a_bare_x_draw_the_close_icon_and_keep_a_tooltip_and_the_accessible_name()
    {
        string chat = Host("ChatPanels.cs");
        Assert.Contains("new RvIconButton(LucideIcons.Close, strings.Get(\"im.close\")) { Name = \"close_conversation\" }", chat);
        string toast = Host("NotificationPanels.cs");
        Assert.Contains("new RvIconButton(LucideIcons.Close, strings.Get(NoticeClose.TooltipKey(n))) { Name = \"hide\" }", toast);
        Assert.DoesNotContain("floater.close.glyph", chat + toast);
        Assert.False(En().ContainsKey("floater.close.glyph"));
    }

    [Fact]
    public void The_title_of_every_window_is_inset_from_the_left_edge_of_the_title_bar()
    {
        string frame = Host("FloaterFrame.cs");
        Assert.Contains("public const float TitleInset", frame);
        int spacer = frame.IndexOf("TitleInset", frame.IndexOf("TitleInset", StringComparison.Ordinal) + 1, StringComparison.Ordinal);
        int title = frame.IndexOf("bar.AddChild(Title);", StringComparison.Ordinal);
        Assert.True(spacer > 0 && title > spacer, "the inset is added before the title");
    }
}
