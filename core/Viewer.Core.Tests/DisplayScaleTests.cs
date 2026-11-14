// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The interface scale follows the operating system's display scale for the screen the viewer is on, until the person sets a
/// scale of their own; from then on their own setting wins, also when it is 100 percent on a larger display scale.
/// </summary>
public class DisplayScaleTests
{
    private sealed class MemoryStore : ISettingsStore
    {
        public string? Text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    [Theory]
    [InlineData(96f, 100)]
    [InlineData(120f, 125)]
    [InlineData(144f, 150)]
    [InlineData(168f, 175)]
    [InlineData(192f, 200)]
    [InlineData(288f, 200)]   // the interface scale tops out at 200 percent
    [InlineData(72f, 100)]    // and never goes below 100
    [InlineData(0f, 100)]     // no reading
    [InlineData(-5f, 100)]
    public void The_display_scale_for_a_dpi_is_its_percent_of_96_within_the_interface_range(float dpi, int expected) =>
        Assert.Equal(expected, DisplayScale.PercentForDpi(dpi));

    private static InterfacePreferences Page(int display, bool own = false) => new(ThemeChoice.Default, null, own, display);

    [Fact]
    public void Until_the_person_sets_a_scale_the_page_uses_the_display_scale()
    {
        var p = Page(150);
        Assert.False(p.ScaleOwn);
        Assert.Equal(150, p.EffectiveScale);
        Assert.False(p.IsChanged);
    }

    [Fact]
    public void A_scale_the_person_sets_wins_over_the_display_scale_also_at_100_percent()
    {
        var p = Page(150);
        p.SetScale(125);
        Assert.True(p.ScaleOwn);
        Assert.Equal(125, p.EffectiveScale);
        p.SetScale(100);
        Assert.Equal(100, p.EffectiveScale);
        Assert.True(p.ScaleOwn);
    }

    [Fact]
    public void Cancel_gives_back_the_following_display_scale_and_save_keeps_the_persons_own()
    {
        var p = Page(150);
        p.SetScale(100);
        p.Cancel();
        Assert.False(p.ScaleOwn);
        Assert.Equal(150, p.EffectiveScale);

        var store = new MemoryStore();
        var settings = new SettingsService(store);
        p.SetScale(100);
        Assert.Equal(SaveOutcome.Saved, p.Save(settings, "alice", confirmed: true));
        var saved = ThemeStore.Load(new SettingsService(store), "alice");
        Assert.True(ThemeStore.ScaleOwn(new SettingsService(store), "alice", saved));
        p.Cancel();
        Assert.True(p.ScaleOwn);
        Assert.Equal(100, p.EffectiveScale);
    }

    [Fact]
    public void A_look_saved_without_touching_the_scale_still_follows_the_display()
    {
        var store = new MemoryStore();
        var p = Page(150);
        p.SelectPreset("high-contrast");
        Assert.Equal(SaveOutcome.Saved, p.Save(new SettingsService(store), "alice", confirmed: true));
        var settings = new SettingsService(store);
        Assert.False(ThemeStore.ScaleOwn(settings, "alice", ThemeStore.Load(settings, "alice")));
    }

    [Fact]
    public void A_look_saved_by_an_earlier_version_with_a_scale_other_than_100_counts_as_the_persons_own()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        Assert.True(ThemeStore.Save(settings, "alice", ThemeChoice.Default with { ScalePercent = 150 }));
        Assert.True(ThemeStore.ScaleOwn(settings, "alice", ThemeStore.Load(settings, "alice")));
        Assert.False(ThemeStore.ScaleOwn(new SettingsService(new MemoryStore()), "bob", ThemeChoice.Default));
    }
}
