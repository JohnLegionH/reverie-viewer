// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Which texture the water's ripples are read from: the normal map the region's water settings name, and the official default when none
/// is named (the official viewer's water draw pool asks for the default when the settings' id is null).
/// </summary>
public class WaterNormalMapIdTests
{
    private static readonly Guid OfficialDefault = new("822ded49-9a6c-f61c-cb89-6df54f42cdf4");

    private static string Env(string waterExtra) => $"""
        <llsd><map><key>environment</key><map><key>day_cycle</key><map><key>frames</key><map>
          <key>w</key><map><key>type</key><string>water</string>
            <key>water_fog_color</key><array><real>0.1</real><real>0.2</real><real>0.3</real></array>{waterExtra}
          </map>
          <key>s</key><map><key>type</key><string>sky</string>
            <key>sun_rotation</key><array><real>0</real><real>-0.7071</real><real>0</real><real>0.7071</real></array>
            <key>moon_rotation</key><array><real>0</real><real>0.7071</real><real>0</real><real>0.7071</real></array>
            <key>sunlight_color</key><array><real>1</real><real>1</real><real>1</real><real>1</real></array>
            <key>legacy_haze</key><map><key>ambient</key><array><real>1</real><real>1</real><real>1</real></array></map>
          </map></map>
          <key>tracks</key><array><array><map><key>key_keyframe</key><real>0</real><key>key_name</key><string>w</string></map></array><array><map><key>key_keyframe</key><real>0</real><key>key_name</key><string>s</string></map></array></array>
        </map></map></map></llsd>
        """;

    [Fact]
    public void A_water_with_no_settings_uses_the_official_default_normal_map()
        => Assert.Equal(OfficialDefault, RegionEnvironment.Default.Water.NormalMap);

    [Fact]
    public void A_region_that_names_a_normal_map_gets_it()
    {
        var id = Guid.NewGuid();
        var env = RegionEnvironment.FromLlsd(Env($"<key>normal_map</key><uuid>{id}</uuid>"), out var why);
        Assert.True(env is not null, why);
        Assert.Equal(id, env!.Water.NormalMap);
    }

    [Fact]
    public void A_region_that_names_no_normal_map_or_the_null_id_gets_the_default()
    {
        var none = RegionEnvironment.FromLlsd(Env(""), out _);
        Assert.Equal(OfficialDefault, none!.Water.NormalMap);
        var nul = RegionEnvironment.FromLlsd(Env("<key>normal_map</key><uuid>00000000-0000-0000-0000-000000000000</uuid>"), out _);
        Assert.Equal(OfficialDefault, nul!.Water.NormalMap);
    }
}
