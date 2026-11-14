// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The draw distance default is 128 m (the official viewer's level for most graphics cards). A saved 1024 from the earlier default is
/// reset once, as the official viewer resets graphics settings when its feature table version changes; a value set afterwards stays.</summary>
public class GraphicsDefaultsMigrationTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string t) { Text = t; return true; }
    }

    [Fact]
    public void The_default_draw_distance_is_128()
    {
        Assert.Equal(128, new SettingsService(new MemoryStore()).Get(Settings.DrawDistance));
        Assert.Equal(128, Settings.DrawDistance.Default);
    }

    [Fact]
    public void A_saved_1024_is_reset_once_to_the_default()
    {
        var store = new MemoryStore("""{ "draw_distance": 1024, "fps_limit": 60 }""");
        var s = new SettingsService(store);
        Assert.Equal(1024, s.MigrateGraphicsDefaults());
        Assert.Equal(128, s.Get(Settings.DrawDistance));
        Assert.False(s.Contains(Settings.DrawDistance.Key));
        Assert.Equal(60, s.Get(Settings.FpsLimit));   // nothing else is touched
        Assert.Contains("graphics_defaults_version", store.Text);
    }

    [Fact]
    public void A_value_set_after_the_reset_stays_and_the_reset_does_not_run_again()
    {
        var store = new MemoryStore("""{ "draw_distance": 1024 }""");
        var s = new SettingsService(store);
        s.MigrateGraphicsDefaults();
        s.Set(Settings.DrawDistance, 1024);   // the person chose it
        Assert.Null(new SettingsService(store).MigrateGraphicsDefaults());   // next run: nothing to reset
        Assert.Equal(1024, new SettingsService(store).Get(Settings.DrawDistance));
    }

    [Fact]
    public void Another_saved_distance_is_kept()
    {
        var s = new SettingsService(new MemoryStore("""{ "draw_distance": 300 }"""));
        Assert.Null(s.MigrateGraphicsDefaults());
        Assert.Equal(300, s.Get(Settings.DrawDistance));
    }

    [Fact]
    public void A_new_install_is_marked_current_and_nothing_is_reset()
    {
        var store = new MemoryStore();
        var s = new SettingsService(store);
        Assert.Null(s.MigrateGraphicsDefaults());
        Assert.Equal(Settings.CurrentGraphicsDefaultsVersion, s.Get(Settings.GraphicsDefaultsVersion));
    }
}
