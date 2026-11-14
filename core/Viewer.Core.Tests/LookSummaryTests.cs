// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

public class LookSummaryTests
{
    private static AvatarAppearance Look(byte height, int cof = 7, params (string Slot, string Id)[] bakes) => new(
        Guid.NewGuid(), false,
        bakes.ToDictionary(b => b.Slot, b => Guid.Parse(b.Id)),
        ImmutableArray<Guid>.Empty,
        new VisualParamSet(new byte[] { 10, height, 30 }, [5, LookSummary.HeightParamId, 9]),
        1, cof);

    [Fact]
    public void A_summary_carries_the_versions_the_height_byte_and_the_bakes_by_slot_in_slot_order()
    {
        var look = Look(136, 132, ("upper", "5b0d2c1e-7a34-4c58-9e11-0a6f3d2b8c47"), ("eyes", "c93e8a10-45d2-4f7b-a1e6-72b0d5f94e38"));
        var s = LookSummary.Of(look);
        Assert.Equal(1, s.Version);
        Assert.Equal(132, s.CofVersion);
        Assert.Equal((byte)136, s.HeightByte);
        Assert.Equal("eyes=c93e8a10,upper=5b0d2c1e", s.Bakes);
    }

    [Fact]
    public void The_parameter_hash_moves_with_a_parameter_and_not_with_the_agent()
    {
        var a = LookSummary.Of(Look(100));
        var b = LookSummary.Of(Look(100));
        var c = LookSummary.Of(Look(101));
        Assert.Equal(12, a.ParamsHash.Length);
        Assert.Equal(a.ParamsHash, b.ParamsHash);   // two agents with the same parameters hash the same
        Assert.NotEqual(a.ParamsHash, c.ParamsHash);
    }

    [Fact]
    public void A_look_without_a_height_parameter_has_no_height_byte()
    {
        var look = Look(1) with { VisualParams = new VisualParamSet(new byte[] { 1 }, [5]) };
        Assert.Null(LookSummary.Of(look).HeightByte);
    }
}
