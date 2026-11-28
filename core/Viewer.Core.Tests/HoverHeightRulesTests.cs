// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The rules for a person's own hover height and the request that states it to the grid.</summary>
public class HoverHeightRulesTests
{
    [Theory]
    [InlineData(0.5f, 0.5f)]
    [InlineData(-2f, -2f)]
    [InlineData(2f, 2f)]
    [InlineData(2.4f, 2f)]
    [InlineData(-7f, -2f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 0f)]
    public void A_height_is_kept_in_the_official_range(float given, float kept) =>
        Assert.Equal(kept, HoverHeightRules.Clamp(given));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void The_control_works_only_with_the_capability_and_the_feature(bool capability, bool feature, bool works) =>
        Assert.Equal(works, HoverHeightRules.Supported(capability, feature));

    [Theory]
    [InlineData("0.5", true)]
    [InlineData(" -1.25 ", true)]
    [InlineData("0,5", true)]
    [InlineData("100", true)]
    [InlineData("1+0.5", true)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData("1.2.3", false)]
    [InlineData("NaN", false)]
    [InlineData("0.5 metres", false)]
    public void A_typed_entry_is_told_apart_as_a_number_or_not(string typed, bool number) =>
        Assert.Equal(number, HoverHeightRules.IsTypedNumber(typed));

    [Fact]
    public void The_request_states_the_height_as_hover_height_and_nothing_else()
    {
        var body = LmvTransport.HoverHeightBody(0.25f);
        Assert.Single(body.Keys);
        Assert.True(body.ContainsKey("hover_height"));
        Assert.Equal(0.25, body["hover_height"].AsReal(), 4);
    }

    [Fact]
    public void The_request_never_states_a_height_outside_the_range() =>
        Assert.Equal(2.0, LmvTransport.HoverHeightBody(9f)["hover_height"].AsReal(), 4);
}
