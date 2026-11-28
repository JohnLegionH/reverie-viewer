// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Linq;
using System.Numerics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The list of avatars in range, nearest first.</summary>
public class NearbyAvatarsTests
{
    private static readonly Guid Self = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Vector3 Own = new(100, 100, 25);

    private static (Guid, Vector3)[] Exact(params (Guid, Vector3)[] a) => a;
    private static CoarseAvatar[] Coarse(params (Guid Id, Vector3 At)[] a) => a.Select(x => new CoarseAvatar(x.Id, x.At)).ToArray();

    [Fact]
    public void Avatars_are_listed_nearest_first_with_their_distance()
    {
        var list = NearbyAvatars.List(Self, Own, Exact((A, new(100, 140, 25)), (B, new(103, 104, 25))), Coarse());
        Assert.Equal(new[] { B, A }, list.Select(r => r.Id));
        Assert.Equal(5f, list[0].Distance, 3);
        Assert.Equal(40f, list[1].Distance, 3);
        Assert.All(list, r => Assert.False(r.Approximate));
    }

    [Fact]
    public void A_seated_avatars_place_is_its_seat_turned_and_moved_through_the_linkset()
    {
        // a root turned a quarter about up at (10, 20, 30), a seat prim 2 m along its x, the avatar 1 m above the seat prim
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        (Vector3, Quaternion, uint)? Find(uint id) => id switch
        {
            1 => (new Vector3(10, 20, 30), quarter, 0u),
            2 => (new Vector3(2, 0, 0), Quaternion.Identity, 1u),
            _ => null,
        };
        var at = NearbyAvatars.WorldPosition(new Vector3(0, 0, 1), 2, Find);
        Assert.NotNull(at);
        Assert.True((at!.Value - new Vector3(10, 22, 31)).Length() < 0.001f, at.ToString());
    }

    [Fact]
    public void A_standing_avatars_place_is_its_own() =>
        Assert.Equal(new Vector3(1, 2, 3), NearbyAvatars.WorldPosition(new Vector3(1, 2, 3), 0, _ => null));

    [Fact]
    public void A_seat_the_viewer_does_not_have_or_a_loop_of_seats_gives_no_place()
    {
        Assert.Null(NearbyAvatars.WorldPosition(Vector3.Zero, 7, _ => null));
        Assert.Null(NearbyAvatars.WorldPosition(Vector3.Zero, 7, id => (Vector3.Zero, Quaternion.Identity, id)));
    }

    [Fact]
    public void The_first_exact_position_of_an_avatar_is_the_one_used()
    {
        var row = Assert.Single(NearbyAvatars.List(Self, Own, Exact((A, new(105, 100, 25)), (A, new(150, 100, 25))), Coarse()));
        Assert.Equal(5f, row.Distance, 3);
    }

    [Fact]
    public void The_own_avatar_is_not_listed() =>
        Assert.Empty(NearbyAvatars.List(Self, Own, Exact((Self, Own)), Coarse((Self, Own))));

    [Fact]
    public void An_exact_position_is_used_before_the_coarse_one_and_the_avatar_is_listed_once()
    {
        var list = NearbyAvatars.List(Self, Own, Exact((A, new(110, 100, 25.4f))), Coarse((A, new(112, 100, 28))));
        var row = Assert.Single(list);
        Assert.Equal(10.008f, row.Distance, 2);
        Assert.False(row.Approximate);
    }

    [Fact]
    public void An_avatar_only_in_the_coarse_list_is_listed_as_approximate()
    {
        var row = Assert.Single(NearbyAvatars.List(Self, Own, Exact(), Coarse((A, new(100, 160, 25)))));
        Assert.Equal(60f, row.Distance, 3);
        Assert.True(row.Approximate);
    }

    [Fact]
    public void An_unknown_coarse_height_leaves_the_height_out_of_the_distance()
    {
        var row = Assert.Single(NearbyAvatars.List(Self, Own, Exact(), Coarse((A, new(100, 130, 1020)))));
        Assert.Equal(30f, row.Distance, 3);
    }

    [Fact]
    public void An_avatar_out_of_range_is_not_listed()
    {
        var list = NearbyAvatars.List(Self, Own, Exact((A, new(100, 231, 25))), Coarse((B, new(100, 229, 25))));
        Assert.Equal(B, Assert.Single(list).Id);
    }

    [Fact]
    public void An_avatar_gone_from_both_sources_is_no_longer_listed()
    {
        Assert.Single(NearbyAvatars.List(Self, Own, Exact((A, new(101, 100, 25))), Coarse((A, new(101, 100, 24)))));
        Assert.Empty(NearbyAvatars.List(Self, Own, Exact(), Coarse()));
    }
}
