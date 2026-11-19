// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The restyled windows' look under every theme preset (the high-contrast one keeps its own colours, outlines, selected fill and
/// text size), the selected state of rows that stay pressed, check boxes, the one tab style, buttons sized to their label, the
/// collapsible section header, and where a dropdown menu opens. Pure and file reads; runs in parallel.
/// </summary>
public class ReverieLookFixTests
{
    private static string Host(string file) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", file));

    [Fact]
    public void High_contrast_keeps_its_own_colours_outlines_selected_fill_and_text_size()
    {
        var hc = ThemePresets.Get(ThemePresets.HighContrast);
        var look = ReverieTheme.LookFor(hc);
        Assert.True(look.OutlinedRows);
        Assert.Equal(hc.BorderWidth, look.BorderWidth);
        Assert.Equal(hc.FontBody / (float)ReverieTheme.BaseFontBody, look.TypeScale, 3);
        var r = look.Tokens;
        Assert.Equal(hc.Panel.Opaque(), r["surface.base"]);
        Assert.Equal(hc.Input.Opaque(), r["surface.raised"]);
        Assert.Equal(hc.Border, r["border.control"]);
        Assert.Equal(hc.Text, r["text.primary"]);
        Assert.Equal(hc.Text, r["text.strong"]);
        Assert.Equal(hc.Accent.Opaque(), r.Accent);
        Assert.Equal(hc.AccentFillText, r.OnAccent);
        // the selected fill is the theme's selection (the accent, faint) over its panel
        var sel = hc.Selection;
        var p = hc.Panel.Opaque();
        Assert.Equal(sel.R * sel.A + p.R * (1 - sel.A), r["surface.selected"].R, 3);
        Assert.Equal(sel.G * sel.A + p.G * (1 - sel.A), r["surface.selected"].G, 3);
    }

    [Theory]
    [InlineData(ThemePresets.Classic)]
    [InlineData(ThemePresets.ClassicLight)]
    [InlineData(ThemePresets.PowerUser)]
    [InlineData(ReverieTheme.DarkId)]
    [InlineData(ReverieTheme.LightId)]
    public void The_other_presets_draw_the_Reverie_tokens_with_their_accent_and_one_pixel_lines(string id)
    {
        var t = ThemePresets.Get(id);
        var look = ReverieTheme.LookFor(t);
        Assert.False(look.OutlinedRows);
        Assert.Equal(1, look.BorderWidth);
        Assert.Equal(1f, look.TypeScale);
        var expected = ReverieTheme.Tokens(ReverieTheme.VariantFor(t), t.Accent);
        Assert.Equal(expected["surface.base"], look.Tokens["surface.base"]);
        Assert.Equal(expected.Accent, look.Tokens.Accent);
    }

    [Theory]
    [InlineData(ThemePresets.Classic)]
    [InlineData(ThemePresets.ClassicLight)]
    [InlineData(ThemePresets.PowerUser)]
    [InlineData(ThemePresets.HighContrast)]
    [InlineData(ReverieTheme.DarkId)]
    [InlineData(ReverieTheme.LightId)]
    public void Every_preset_reads_on_every_surface_the_restyled_controls_draw(string id)
    {
        var r = ReverieTheme.LookFor(ThemePresets.Get(id)).Tokens;
        foreach (var (text, surface) in ReverieTheme.TextPairs)
            Assert.True(UiColour.Contrast(r[text], r[surface]) >= Readability.MinimumRatio - 0.05,
                $"{id}: {text} on {surface} reads {UiColour.Contrast(r[text], r[surface]):0.00}:1");
    }

    [Fact]
    public void The_engine_theme_draws_the_Reverie_components_from_the_look_border_width_text_size_and_row_outlines()
    {
        string style = Host(Path.Combine("Reverie", "ReverieStyle.cs"));
        Assert.Contains("ReverieTheme.LookFor(", style);
        Assert.Contains("look.BorderWidth", style);
        Assert.Contains("look.TypeScale", style);
        Assert.Contains("look.OutlinedRows", style);
        foreach (var name in new[] { ReverieVariations.Check, ReverieVariations.TabBar, ReverieVariations.SectionHeader })
            Assert.Contains("\"" + name + "\"", style);
    }

    [Fact]
    public void A_button_that_stays_pressed_is_a_list_row_and_check_boxes_and_tab_bars_take_their_Reverie_look()
    {
        Assert.Equal(ReverieVariations.ListRow, ReverieRestyleRules.VariationFor("Button", "", false, RestylePlace.Window, toggle: true));
        Assert.Equal(ReverieVariations.Secondary, ReverieRestyleRules.VariationFor("Button", "", false, RestylePlace.Window, toggle: false));
        Assert.Null(ReverieRestyleRules.VariationFor("Button", ReverieVariations.SectionHeader, false, RestylePlace.Window, toggle: true));
        Assert.Equal(ReverieVariations.Check, ReverieRestyleRules.VariationFor("CheckBox", "", false));
        Assert.Equal(ReverieVariations.TabBar, ReverieRestyleRules.VariationFor("TabBar", "", false));
    }

    [Theory]
    [InlineData("Button", false, true, false, true)]
    [InlineData("MenuButton", false, true, false, true)]
    [InlineData("Button", true, true, false, false)]
    [InlineData("Button", false, false, false, false)]
    [InlineData("Button", false, true, true, false)]
    [InlineData("CheckBox", false, true, false, false)]
    public void Push_buttons_in_a_column_are_sized_to_their_label(string kind, bool toggle, bool inColumn, bool expands, bool expected) =>
        Assert.Equal(expected, ReverieRestyleRules.SizedToLabel(kind, toggle, inColumn, expands));

    [Fact]
    public void The_restyle_sizes_push_buttons_to_their_label_and_passes_whether_a_button_stays_pressed()
    {
        string restyle = Host(Path.Combine("Reverie", "ReverieRestyle.cs"));
        Assert.Contains("ReverieRestyleRules.SizedToLabel(", restyle);
        Assert.Contains("ToggleMode", restyle);
    }

    [Fact]
    public void A_menu_that_fits_below_its_button_opens_below_it()
    {
        var area = new PlacementRect(0, 0, 960, 600);
        var button = new PlacementRect(46, 436, 40, 26);
        Assert.Equal((46, 462), DropdownPlacement.Place(button, 160, 100, area));
    }

    [Fact]
    public void A_menu_that_does_not_fit_below_opens_above_and_never_covers_its_button()
    {
        var area = new PlacementRect(0, 0, 960, 600);
        var button = new PlacementRect(46, 436, 40, 26);
        var (x, y) = DropdownPlacement.Place(button, 160, 166, area);
        Assert.Equal(46, x);
        Assert.Equal(436 - 166, y);
        Assert.True(y + 166 <= button.Y, "the menu's bottom is at or above the button's top");
    }

    [Fact]
    public void A_menu_that_fits_on_neither_side_opens_on_the_side_with_more_room_and_stays_inside_the_area_sideways()
    {
        var area = new PlacementRect(0, 0, 960, 600);
        Assert.Equal(100 + 26, DropdownPlacement.Place(new PlacementRect(46, 100, 40, 26), 160, 700, area).Y);
        Assert.Equal(500 - 700, DropdownPlacement.Place(new PlacementRect(46, 500, 40, 26), 160, 700, area).Y);
        Assert.Equal(960 - 200, DropdownPlacement.Place(new PlacementRect(900, 100, 40, 26), 200, 50, area).X);
    }

    [Fact]
    public void Every_menu_button_and_select_opens_its_menu_through_the_placement()
    {
        string file = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "Reverie", "DropdownPlacer.cs");
        Assert.True(File.Exists(file), "the engine side of the placement is in Reverie/DropdownPlacer.cs");
        string placer = File.ReadAllText(file);
        Assert.Contains("DropdownPlacement.Place(", placer);
        Assert.Contains("MenuButton", placer);
        Assert.Contains("OptionButton", placer);
        Assert.Contains("AboutToPopup", placer);
        Assert.Contains("DropdownPlacer.Watch(", Host("ShellRoot.cs"));
    }

    [Fact]
    public void Inventory_switches_its_views_with_the_segmented_control()
    {
        string inv = Host("InventoryPanels.cs");
        Assert.Contains("ReverieStyle.Track", inv);
        Assert.Contains("ThemeTypeVariation = ReverieStyle.TabBar", inv);
    }

    [Fact]
    public void Appearance_section_headers_are_flat_with_a_chevron_icon_a_bold_label_and_a_divider()
    {
        string src = Host("AppearancePanels.cs");
        Assert.DoesNotContain("\"▾ \"", src);
        Assert.DoesNotContain("\"▸ \"", src);
        Assert.Contains("ReverieStyle.SectionHeader", src);
        Assert.Contains("LucideIcons.Down", src);
        Assert.Contains("LucideIcons.Submenu", src);
        Assert.Contains("HSeparator", src);
    }
}
