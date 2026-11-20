// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The second half of the new look, second part: the Build and in-world windows (the colour picker, the texture picker, the Build
/// window's Contents tab, Take to folder, Grid options, the object contents, Buy and object info). Source reads and pure rules;
/// runs in parallel.
/// </summary>
public class ReverieBatch2Tests
{
    private static string Host(string file) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", file));

    [Fact]
    public void The_batch_two_windows_are_on_the_restyle_list_by_their_ids()
    {
        string restyle = Host(Path.Combine("Reverie", "ReverieRestyle.cs"));
        foreach (var id in new[] { "ColourPickerWindow.FloaterId", "FacePickerWindows.ItemPickerId", "FolderPickPanel.FloaterId", "GridPanel.FloaterId",
                     "InteractFeature.ContentsId", "InteractFeature.BuyId", "NotificationFeature.ObjectId" })
            Assert.Contains(id, restyle);
    }

    [Fact]
    public void A_colour_swatch_keeps_no_variation_because_it_draws_its_own_colour()
    {
        string restyle = Host(Path.Combine("Reverie", "ReverieRestyle.cs"));
        int chip = restyle.IndexOf("case ColourChip:", StringComparison.Ordinal);
        int general = restyle.IndexOf("case Control control:", StringComparison.Ordinal);
        Assert.True(chip > 0 && chip < general, "the colour chip case must come before the general control case");
        Assert.Contains("public partial class ColourChip : Button", Host("ColourPickerWindow.cs"));
    }

    [Fact]
    public void The_pickers_popups_and_the_Contents_tab_take_the_look_themselves_because_they_are_not_floaters()
    {
        Assert.Contains("ReverieRestyle.Apply(page);", Host("EditContentTab.cs"));
        string pick = Host("EditTextureTab.cs");
        Assert.Contains("ReverieRestyle.Apply(this);", pick[pick.IndexOf("class InventoryPickPopup", StringComparison.Ordinal)..]);
        string appearance = Host("AppearancePanels.cs");
        Assert.Contains("ReverieRestyle.Apply(this);", appearance[appearance.IndexOf("class TexturePickerPopup", StringComparison.Ordinal)..]);
    }

    [Theory]
    [InlineData("Button", "", true, ReverieVariations.ListRow)]   // the colour picker's Pick button: a toggle takes the list row with its selected face
    public void A_toggle_button_takes_the_list_row_so_its_pressed_state_shows(string kind, string current, bool toggle, string expected) =>
        Assert.Equal(expected, ReverieRestyleRules.VariationFor(kind, current, flat: false, RestylePlace.Window, toggle));

    [Fact]
    public void The_Build_window_Contents_list_and_menu_and_fields_take_the_Reverie_variations()
    {
        Assert.Equal(ReverieVariations.List, ReverieRestyleRules.VariationFor("ItemList", "", false));
        Assert.Equal(ReverieVariations.Popup, ReverieRestyleRules.VariationFor("PopupMenu", "", false));
        Assert.Equal(ReverieVariations.Field, ReverieRestyleRules.VariationFor("LineEdit", "", false));
        Assert.Equal(ReverieVariations.Select, ReverieRestyleRules.VariationFor("OptionButton", "", false));
        // sliders, number boxes and check boxes in Grid options keep their look (a check box takes the Reverie tick)
        Assert.Null(ReverieRestyleRules.VariationFor("HSlider", "", false));
        Assert.Null(ReverieRestyleRules.VariationFor("SpinBox", "", false));
    }

    [Fact]
    public void The_folder_picker_asks_for_folders_not_textures_and_the_colour_pick_toggle_keeps_its_outline()
    {
        Assert.Equal("Search folders", StringTable.English().Get("edit.take_folder.search"));
        Assert.DoesNotContain("appearance.texture.search", Host("FolderPickPanel.cs"));
        Assert.Contains("ThemeTypeVariation = ReverieVariations.Secondary", Host("ColourPickerWindow.cs"));
    }

    [Fact]
    public void The_example_scenes_for_the_new_windows_are_listed_in_the_render_harness()
    {
        string scenes = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tools", "render.ps1"));
        foreach (var scene in new[] { "texturepicker", "build-contents", "takefolder", "gridoptions", "objectcontents", "buy", "objectinfo", "colourpicker" })
            Assert.Contains("\"" + scene + "\"", scenes);
    }
}
