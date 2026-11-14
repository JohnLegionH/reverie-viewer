// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The graphics levels are the official viewer's feature table levels (Low, LowMid, Mid, MidHigh, High, HighUltra, Ultra) with
/// each level's own draw distance, largest texture and volume level-of-detail factor. Without a GPU-class rule the default level is HighUltra.</summary>
public class GraphicsLevelsTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string t) { Text = t; return true; }
    }

    private static (PreferencesModel M, SettingsService S) Model(string? json = null)
    {
        var s = new SettingsService(new MemoryStore(json));
        return (new PreferencesModel(s, null), s);
    }

    [Theory]
    [InlineData("low", 64, 512, 1.125f)]
    [InlineData("lowmid", 96, 1024, 1.125f)]
    [InlineData("mid", 128, 2048, 1.25f)]
    [InlineData("midhigh", 128, 2048, 1.375f)]
    [InlineData("high", 128, 2048, 1.5f)]
    [InlineData("highultra", 128, 2048, 1.75f)]
    [InlineData("ultra", 256, 2048, 2.0f)]
    public void Each_level_has_the_official_values(string id, int draw, int texture, float lod)
    {
        var v = GraphicsPresets.Values(id);
        Assert.NotNull(v);
        Assert.Equal(draw, v!.Value.DrawDistance);
        Assert.Equal(texture, v.Value.MaxTextureResolution);
        Assert.Equal(lod, v.Value.VolumeLodFactor);
    }

    [Fact]
    public void The_levels_are_listed_in_the_official_order_then_custom()
    {
        Assert.Equal(["low", "lowmid", "mid", "midhigh", "high", "highultra", "ultra", "custom"], PreferenceCatalog.Presets);
    }

    [Fact]
    public void A_fresh_install_shows_HighUltra_with_its_values()
    {
        var (m, s) = Model();
        Assert.Equal("highultra", m.Preset);
        Assert.Equal("highultra", PreferenceCatalog.OfPage(PrefPage.Graphics).First(d => d.Key == Settings.GraphicsPresetKey).Default);
        Assert.Equal(128, s.Get(Settings.DrawDistance));
        Assert.Equal(2048, s.Get(Settings.MaxTextureResolution));
    }

    [Theory]
    [InlineData("low"), InlineData("lowmid"), InlineData("mid"), InlineData("midhigh"), InlineData("high"), InlineData("highultra"), InlineData("ultra")]
    public void Choosing_a_level_shows_that_level_even_when_another_has_the_same_draw_distance_and_texture(string id)
    {
        var (m, s) = Model();
        Assert.True(m.SelectPreset(id));
        Assert.Equal(id, m.Preset);
        var v = GraphicsPresets.Values(id)!.Value;
        Assert.Equal(v.DrawDistance, s.Get(Settings.DrawDistance));
        Assert.Equal(v.MaxTextureResolution, s.Get(Settings.MaxTextureResolution));
    }

    [Fact]
    public void Editing_a_value_away_makes_the_level_custom_and_editing_it_back_returns_to_the_chosen_level()
    {
        var (m, s) = Model();
        m.SelectPreset("midhigh");
        m.Edit(PreferenceCatalog.OfPage(PrefPage.Graphics).First(d => d.Key == Settings.DrawDistance.Key), 300);
        Assert.Equal("custom", m.Preset);
        m.Edit(PreferenceCatalog.OfPage(PrefPage.Graphics).First(d => d.Key == Settings.DrawDistance.Key), 128);
        Assert.Equal("midhigh", m.Preset);
    }

    [Fact]
    public void Choosing_a_level_leaves_the_clouds_as_the_person_set_them()
    {
        // the official feature table has no clouds setting
        var (m, s) = Model("""{ "clouds_enabled": false }""");
        m.SelectPreset("ultra");
        Assert.False(s.Get(Settings.CloudsEnabled));
        m.SelectPreset("low");
        Assert.False(s.Get(Settings.CloudsEnabled));
    }

[Fact]    public void The_mesh_level_of_detail_default_is_the_default_levels_factor_and_the_default_level_is_the_bandwidth_rule_s_top_class_pick()    {        Assert.Equal(GraphicsPresets.HighUltra, GraphicsPresets.Default);        Assert.Equal(GraphicsPresets.Values(GraphicsPresets.Default)!.Value.VolumeLodFactor, Viewer.Core.Meshing.MeshLodRule.DefaultLodFactor);    }
    [Fact]
    public void The_camera_far_plane_is_the_draw_distance_even_at_the_full_distance()
    {
        Assert.Equal(1024f, DrawDistanceView.FarPlane(1024, 4000f));
        Assert.Equal(512f, DrawDistanceView.FarPlane(512, 4000f));
        Assert.Equal(64f, DrawDistanceView.FarPlane(10, 4000f));
    }
}
