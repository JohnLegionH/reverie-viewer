// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The restyle of the classic windows to the Reverie look: which variation each kind of control takes (pure), which windows take
/// it, and source rules for the engine side (it changes variations only: no colour, size or font of its own, no node made or
/// removed). Pure and file reads; runs in parallel.
/// </summary>
public class ReverieRestyleTests
{
    private static string Host(string file) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", file));

    [Theory]
    [InlineData("Button", "", false, ReverieVariations.Secondary)]
    [InlineData("Button", "PrimaryButton", false, ReverieVariations.Primary)]
    [InlineData("Button", "", true, ReverieVariations.IconButton)]
    [InlineData("Button", "FlatButton", false, ReverieVariations.IconButton)]
    [InlineData("Button", "ToolbarButton", false, ReverieVariations.IconButton)]
    [InlineData("MenuButton", "", false, ReverieVariations.Secondary)]
    [InlineData("OptionButton", "", false, ReverieVariations.Select)]
    [InlineData("Label", "MutedLabel", false, ReverieVariations.Muted)]
    [InlineData("Label", "CaptionLabel", false, ReverieVariations.Caption)]
    [InlineData("Label", "TitleLabel", false, ReverieVariations.CardHeading)]
    [InlineData("LineEdit", "", false, ReverieVariations.Field)]
    [InlineData("ItemList", "", false, ReverieVariations.List)]
    [InlineData("Tree", "", false, ReverieVariations.Tree)]
    [InlineData("TabContainer", "", false, ReverieVariations.Tabs)]
    [InlineData("PopupMenu", "", false, ReverieVariations.Popup)]
    public void Each_classic_control_takes_the_Reverie_component_of_its_role(string kind, string current, bool flat, string expected) =>
        Assert.Equal(expected, ReverieRestyleRules.VariationFor(kind, current, flat));

    [Theory]
    [InlineData("Button", "DangerButton")]
    [InlineData("Button", "ToolbarButtonActive")]
    [InlineData("Button", "RvPrimary")]
    [InlineData("Label", "")]
    [InlineData("Label", "ErrorLabel")]
    [InlineData("CheckBox", "")]
    [InlineData("CheckButton", "")]
    [InlineData("TextEdit", "")]
    [InlineData("RichTextLabel", "")]
    [InlineData("PanelContainer", "")]
    [InlineData("HSlider", "")]
    public void A_control_with_no_Reverie_counterpart_or_already_restyled_keeps_its_look(string kind, string current) =>
        Assert.Null(ReverieRestyleRules.VariationFor(kind, current, flat: false));

    [Fact]
    public void In_a_menu_drawn_as_a_panel_buttons_are_menu_rows_and_the_panel_is_the_dropdown_menu()
    {
        Assert.Equal(ReverieVariations.ListRow, ReverieRestyleRules.VariationFor("Button", "", false, RestylePlace.Menu));
        Assert.Equal(ReverieVariations.ListRow, ReverieRestyleRules.VariationFor("Button", "", true, RestylePlace.Menu));
        Assert.Equal(ReverieVariations.MenuPanel, ReverieRestyleRules.VariationFor("PanelContainer", "", false, RestylePlace.Menu));
        Assert.Null(ReverieRestyleRules.VariationFor("PanelContainer", "", false));
    }

    [Fact]
    public void Every_variation_named_is_defined_by_the_engine_theme_under_the_same_name()
    {
        string style = Host(Path.Combine("Reverie", "ReverieStyle.cs"));
        foreach (var name in new[] { ReverieVariations.Select, ReverieVariations.Field, ReverieVariations.List, ReverieVariations.Tree,
                     ReverieVariations.Tabs, ReverieVariations.MenuPanel })
            Assert.Contains("\"" + name + "\"", style);
        foreach (var (field, name) in new[] { ("Primary", ReverieVariations.Primary), ("Secondary", ReverieVariations.Secondary),
                     ("IconButton", ReverieVariations.IconButton), ("ListRow", ReverieVariations.ListRow), ("Muted", ReverieVariations.Muted),
                     ("Caption", ReverieVariations.Caption), ("CardHeading", ReverieVariations.CardHeading), ("Popup", ReverieVariations.Popup) })
            Assert.Contains(field + " = \"" + name + "\"", style);
    }

    [Fact]
    public void The_first_half_windows_take_the_restyle_when_added_and_the_right_click_menu_and_toasts_too()
    {
        string restyle = Host(Path.Combine("Reverie", "ReverieRestyle.cs"));
        foreach (var id in new[] { "InventoryFeature.FloaterId", "ChatFeature.NearbyId", "ChatFeature.ConversationsId", "PeopleFeature.PeopleId",
                     "TravelFeature.MiniMapId", "TravelFeature.MapId", "TravelFeature.TeleportId", "AppearanceFeature.FloaterId", "OutfitFeature.FloaterId",
                     "NotificationFeature.ListId", "PreferencesFeature.FloaterId", "InteractFeature.InspectId", "InteractFeature.PayId", "InteractFeature.LandId" })
            Assert.Contains(id, restyle);
        Assert.Contains("ReverieRestyle.ApplyIfListed(spec.Id", Host("ShellRoot.cs"));
        Assert.Contains("ReverieRestyle.Apply(menu, RestylePlace.Menu)", Host("InteractPanels.cs"));
        Assert.Contains("ReverieRestyle.Apply(toasts", Host("NotificationPanels.cs"));
    }

    [Fact]
    public void The_restyle_changes_variations_only_and_sets_no_colour_size_or_font_of_its_own()
    {
        string restyle = Host(Path.Combine("Reverie", "ReverieRestyle.cs"));
        Assert.Contains("ThemeTypeVariation =", restyle);
        Assert.Contains("ReverieRestyleRules.VariationFor(", restyle);
        foreach (var banned in new[] { "AddTheme", "new Color(", "CustomMinimumSize", "QueueFree", "RemoveChild", ".Text =", "Disabled =", "Visible =" })
            Assert.DoesNotContain(banned, restyle);
    }

    [Fact]
    public void The_Reverie_variant_follows_the_panel_the_controls_sit_on_not_the_bar()
    {
        var classic = ThemePresets.Get(ThemePresets.Classic);
        var lightBar = classic with { Bar = new UiColour(0.95f, 0.95f, 0.95f) };
        Assert.Equal(ReverieVariant.Dark, ReverieTheme.VariantFor(lightBar));
        var lightPanel = classic with { Panel = new UiColour(0.95f, 0.95f, 0.95f, classic.Panel.A), Bar = new UiColour(0.1f, 0.1f, 0.1f) };
        Assert.Equal(ReverieVariant.Light, ReverieTheme.VariantFor(lightPanel));
        Assert.Contains("ReverieTheme.VariantFor(t)", Host(Path.Combine("Reverie", "ReverieStyle.cs")));
    }

    [Fact]
    public void Restyled_labels_set_again_later_keep_the_Reverie_look_and_number_fields_inside_other_controls_are_left_alone()
    {
        Assert.Contains("ReverieRestyleRules.VariationFor(\"Label\"", Host("PreferencesPanels.cs"));
        string restyle = Host(Path.Combine("Reverie", "ReverieRestyle.cs"));
        Assert.Contains("SpinBox", restyle);
        string style = Host(Path.Combine("Reverie", "ReverieStyle.cs"));
        Assert.Contains("\"hovered_selected\"", style);
    }

    [Fact]
    public void The_notecard_link_overlay_reads_the_text_once_per_draw_and_leaves_the_caret_uncovered()
    {
        string src = Host("NotecardPanels.cs");
        int from = src.IndexOf("private void DrawItemLinks()", StringComparison.Ordinal);
        int to = src.IndexOf("public int IndexOf(int line, int column)", StringComparison.Ordinal);
        Assert.True(from > 0 && to > from);
        string body = src[from..to];
        Assert.DoesNotContain("PlaceOf(", body);
        Assert.Contains("GetCaretLine()", body);
    }

    [Fact]
    public void Toast_notes_take_their_colour_from_a_variation_not_an_override()
    {
        Assert.DoesNotContain("AddThemeColorOverride", Host("NotificationPanels.cs"));
    }
}
