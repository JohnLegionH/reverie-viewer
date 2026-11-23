// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using Viewer.Shell;
using Viewer.Shell.Ui;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The layout every window shares: one inner padding of 12 interface units (12 px at 100 %, scaled by the interface size) on every
/// registered window and panel, held in the shared frame code and not set window by window; and a disabled control that shows
/// why it is disabled as its tooltip. Plain .NET, no engine; parallel (no process-wide state; the source files are only read).
/// </summary>
public class SharedLayoutTests
{
    public static TheoryData<int> Sizes() => new(100, 150, 200);

    public static TheoryData<string> SurfaceNames()
    {
        var data = new TheoryData<string>();
        foreach (var s in PanelPadding.Surfaces) data.Add(s.Name);
        return data;
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([TestPaths.RepoRoot, "host", .. parts]));

    [Theory]
    [MemberData(nameof(SurfaceNames))]
    public void Every_registered_surface_pads_each_content_side_by_the_shared_inner_padding(string name)
    {
        var s = PanelPadding.Surfaces.Single(x => x.Name == name);
        Assert.Equal(12f, PanelPadding.Inner);
        Assert.True(s.Left >= PanelPadding.Inner && s.Right >= PanelPadding.Inner && s.Top >= PanelPadding.Inner,
            $"{name}: left {s.Left}, right {s.Right}, top {s.Top}");
        if (!name.EndsWith("footer", StringComparison.Ordinal))
            Assert.True(s.Bottom >= PanelPadding.Inner, $"{name}: bottom {s.Bottom}");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void The_padding_on_screen_is_12_px_at_100_percent_and_scales_with_the_interface_size(int percent)
    {
        Assert.Equal(12f * percent / 100f, PanelPadding.OnScreen(percent), 3);
        foreach (var s in PanelPadding.Surfaces)
            Assert.True(s.Left * percent / 100f >= 12f * percent / 100f, $"{s.Name} at {percent} %");
    }

    [Fact]
    public void The_floater_frame_and_the_reverie_window_take_their_padding_from_the_shared_value()
    {
        foreach (var file in new[] { "FloaterFrame.cs", "Reverie/RvWindowChrome.cs" })
        {
            string src = Source("Viewer.Godot", file);
            Assert.Contains("PanelPadding.", src);
            Assert.DoesNotMatch(new Regex(@"AddThemeConstantOverride\(side, \d+\)"), src);
        }
    }

    [Theory]
    [InlineData("Reverie/ReverieStyle.cs")]
    [InlineData("ShellTheme.cs")]
    [InlineData("PreferencesPanels.cs")]
    [InlineData("PreferencePages.cs")]
    public void Lists_text_areas_and_the_preference_pages_pad_by_the_shared_value_in_the_theme_not_per_window(string file)
        => Assert.Contains("PanelPadding.Inner", Source("Viewer.Godot", file));

    [Fact]
    public void The_registry_names_the_lists_trees_text_areas_and_preference_pages_too()
    {
        var names = PanelPadding.Surfaces.Select(s => s.Name).ToHashSet();
        foreach (var n in new[] { "list", "tree", "text.area", "preferences.page" })
            Assert.Contains(n, names);
    }

    [Fact]
    public void No_dialog_or_card_sets_a_frame_margin_of_its_own_below_the_shared_padding()
    {
        foreach (var file in new[] { "LoginScreen.cs", "LoginGridPanels.cs", "LoginDialogView.cs", "ScriptEditorPanels.cs", "NotecardPanels.cs" })
            Assert.DoesNotMatch(new Regex(@"margin_(left|right|top|bottom)""\s*,\s*[0-9]\b"), Source("Viewer.Godot", file));
    }

    [Theory]
    [InlineData(true, "You need to pick something first.", "Wear it", "You need to pick something first.")]
    [InlineData(false, "You need to pick something first.", "Wear it", "Wear it")]
    [InlineData(true, null, "Wear it", "Wear it")]
    [InlineData(true, "  ", null, null)]
    [InlineData(false, "Why", null, null)]
    public void A_disabled_control_shows_its_reason_as_the_tooltip_and_an_enabled_one_its_own(bool disabled, string? reason, string? normal, string? expected)
        => Assert.Equal(expected, DisabledReasons.TooltipFor(disabled, reason, normal));

    [Fact]
    public void Every_reason_key_has_a_text_that_says_why_in_the_string_table()
    {
        Assert.NotEmpty(DisabledReasons.Keys);
        var table = JsonSerializer.Deserialize<Dictionary<string, string>>(Source("Viewer.Shell", "Strings", "en.json"))!;
        foreach (var key in DisabledReasons.Keys)
        {
            Assert.True(table.TryGetValue(key, out var text), key);
            Assert.True(text!.Length >= 12, key);
            Assert.DoesNotMatch(new Regex("not built|coming|soon|todo", RegexOptions.IgnoreCase), text);
        }
    }

    [Theory]
    [InlineData("InventoryPanels.cs", "WearButton")]
    [InlineData("InventoryPanels.cs", "TakeOffButton")]
    [InlineData("OutfitPanels.cs", "WearOutfitButton")]
    [InlineData("OutfitPanels.cs", "SaveButton")]
    [InlineData("NotificationPanels.cs", "MarkAll")]
    [InlineData("MapPanels.cs", "TeleportButton")]
    [InlineData("TravelPanels.cs", "HomeButton")]
    [InlineData("TravelPanels.cs", "OfferButton")]
    [InlineData("TravelPanels.cs", "AskButton")]
    [InlineData("EditPanel.cs", "Link")]
    [InlineData("LoginScreen.cs", "_rememberPassword")]
    [InlineData("ScriptEditorPanels.cs", "SaveButton")]
    [InlineData("InventoryPanels.cs", "DetachButton")]
    [InlineData("PreferencePages.cs", "check")]
    public void The_disabled_controls_the_review_names_set_their_state_through_the_shared_tooltip_mechanism(string file, string control)
    {
        string src = Source("Viewer.Godot", file);
        Assert.Matches(new Regex($@"DisabledTip\.Set\(\s*{control}\b"), src);
        Assert.DoesNotMatch(new Regex($@"\b{control}\.Disabled\s*="), src);
    }

    [Fact]
    public void The_shared_mechanism_sets_the_tooltip_the_engine_shows_on_hover_and_a_disabled_control_keeps_catching_the_pointer()
    {
        string src = Source("Viewer.Godot", "Reverie/DisabledTip.cs");
        Assert.Contains("DisabledReasons.TooltipFor", src);
        Assert.Contains("TooltipText", src);
        Assert.DoesNotContain("MouseFilterEnum.Ignore", src);   // a disabled control must stay hoverable
    }
}
