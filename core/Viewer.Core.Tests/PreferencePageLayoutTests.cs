// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// How the ordinary Preferences pages group and word their settings: every setting in one section, a header and (for a closed "More") a
/// one-line summary for each section, and a label short enough for the fixed label column (UI-STANDARD U1, U5, U6, U8).
/// Plain .NET; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class PreferencePageLayoutTests
{
    /// <summary>The longest setting label the label column holds on one line, in characters.</summary>
    private const int LongestLabel = 32;

    private static readonly PrefPage[] LaidOutPages =
        Enum.GetValues<PrefPage>().Where(p => p != PrefPage.Sound).ToArray();

    [Fact]
    public void Every_setting_of_a_page_is_in_exactly_one_section_in_catalogue_order()
    {
        foreach (var page in LaidOutPages)
        {
            var inSections = PreferencePageLayout.For(page).SelectMany(s => s.Keys).ToList();
            Assert.Equal(PreferenceCatalog.OfPage(page).Select(d => d.Key).Order(), inSections.Order());
            Assert.Equal(inSections.Count, inSections.Distinct().Count());
            foreach (var section in PreferencePageLayout.For(page))
            {
                var keys = PreferenceCatalog.OfPage(page).Select(d => d.Key).ToList();
                Assert.Equal(section.Keys.OrderBy(keys.IndexOf), section.Keys);   // catalogue order inside a section
            }
        }
    }

    [Fact]
    public void Every_section_has_its_header_and_a_closed_more_has_a_one_line_summary()
    {
        var strings = StringTable.English();
        foreach (var page in LaidOutPages)
            foreach (var section in PreferencePageLayout.For(page))
            {
                Assert.True(strings.Has(section.TitleKey(page)), section.TitleKey(page));
                if (section.Collapsed)
                {
                    Assert.Equal("more", section.Id);
                    Assert.True(strings.Has(section.SummaryKey(page)), section.SummaryKey(page));
                    Assert.DoesNotContain('\n', strings.Get(section.SummaryKey(page), Enumerable.Repeat((object)"1", section.Keys.Count).ToArray()));
                }
            }
    }

    [Fact]
    public void A_page_has_at_most_one_more_and_it_comes_last()
    {
        foreach (var page in LaidOutPages)
        {
            var sections = PreferencePageLayout.For(page);
            Assert.True(sections.Count(s => s.Collapsed) <= 1);
            if (sections.Any(s => s.Collapsed))
                Assert.True(sections[^1].Collapsed);
        }
    }

    [Fact]
    public void Every_setting_label_fits_the_label_column_on_one_line()
    {
        var strings = StringTable.English();
        foreach (var def in PreferenceCatalog.All.Where(d => d.Kind != PrefKind.Switch))   // a tick box has no label column: its label sits beside the box
        {
            string label = strings.Get(def.LabelKey);
            Assert.True(label.Length <= LongestLabel, $"{def.LabelKey} is {label.Length} characters: '{label}'");
        }
    }

    [Fact]
    public void The_sound_labels_the_mute_tooltip_and_the_cache_placeholder_are_worded()
    {
        var strings = StringTable.English();
        Assert.Equal("Money sound above", strings.Get("prefs.s.ui_sound_money_threshold"));
        Assert.Equal("Pain sound above", strings.Get("prefs.s.ui_sound_health_threshold"));
        Assert.True(strings.Has("prefs.sound.mute.tip"));
        Assert.True(strings.Has("prefs.sound.unmute.tip"));
        Assert.Equal("Default folder", strings.Get("prefs.cache.placeholder"));
        Assert.Equal("Cache folder", strings.Get("prefs.s.cache_folder"));
    }

    [Fact]
    public void The_two_speaker_icons_exist_in_the_icon_set()
    {
        Assert.Contains(LucideIcons.Volume, LucideIcons.All);
        Assert.Contains(LucideIcons.Muted, LucideIcons.All);
        Assert.NotNull(LucideIcons.Svg(LucideIcons.Volume, new UiColour(1, 1, 1)));
        Assert.NotNull(LucideIcons.Svg(LucideIcons.Muted, new UiColour(1, 1, 1)));
    }
}
