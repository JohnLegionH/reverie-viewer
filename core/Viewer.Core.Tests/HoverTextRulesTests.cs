// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Interaction;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Floating text: colour, opacity by distance, the draw limit and the anchor. Pure; independent members, run in parallel.</summary>
public class HoverTextRulesTests
{
    private static PrimInteraction Text(string? t, uint colour) => new(0, 0, t, colour);
    private static readonly uint Red = HoverTextRules.Pack(255, 0, 0, 255);

    [Fact]
    public void Pack_puts_red_green_blue_alpha_in_that_order()
    {
        Assert.Equal(0x11223344u, HoverTextRules.Pack(0x11, 0x22, 0x33, 0x44));
    }

    [Fact]
    public void Text_close_by_shows_in_its_own_colour_and_full_opacity()
    {
        var s = HoverTextRules.Style(Text("hello", HoverTextRules.Pack(255, 128, 0, 255)), 3f);
        Assert.True(s.Visible);
        Assert.Equal(1f, s.R, 3);
        Assert.Equal(128f / 255f, s.G, 3);
        Assert.Equal(0f, s.B, 3);
        Assert.Equal(1f, s.Alpha, 3);
    }

    [Fact]
    public void The_scripts_alpha_scales_the_opacity()
    {
        var s = HoverTextRules.Style(Text("hello", HoverTextRules.Pack(255, 255, 255, 51)), 2f);
        Assert.Equal(0.2f, s.Alpha, 3);
    }

    [Fact]
    public void Opacity_stays_full_up_to_eight_metres_then_fades_to_nothing_at_twelve()
    {
        var t = Text("hello", Red);
        Assert.Equal(1f, HoverTextRules.Style(t, 8f).Alpha, 3);
        Assert.Equal(0.5f, HoverTextRules.Style(t, 10f).Alpha, 3);
        Assert.False(HoverTextRules.Style(t, 12.01f).Visible);
    }

    [Fact]
    public void Text_without_words_is_never_visible()
    {
        Assert.False(HoverTextRules.Style(Text(null, Red), 1f).Visible);
        Assert.False(HoverTextRules.Style(Text("", Red), 1f).Visible);
        Assert.False(HoverTextRules.Style(null, 1f).Visible);
    }

    [Fact]
    public void A_smaller_draw_limit_hides_text_before_the_fade_ends()
    {
        Assert.False(HoverTextRules.Style(Text("hello", Red), 6f, maxDistance: 5f).Visible);
        Assert.True(HoverTextRules.Style(Text("hello", Red), 4f, maxDistance: 5f).Visible);
    }

    [Fact]
    public void The_anchor_is_six_tenths_of_the_prims_height_above_it()
    {
        var a = HoverTextRules.Anchor(new Vector3(10f, 20f, 30f), 2f);
        Assert.Equal(10f, a.X, 3);
        Assert.Equal(20f, a.Y, 3);
        Assert.Equal(31.2f, a.Z, 3);
    }
}
