// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Imaging;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The largest texture level the viewer keeps (the Preferences setting): no level bigger than the cap is wanted for a texture that is
/// being looked at closely, the default is the rule the viewer has always had (over 2,048 texels across starts one level down), a
/// cap never makes a far-away texture finer, and a cap changed while textures are resident re-decides every one of them. Every member
/// builds its own objects, so the class runs in parallel.
/// </summary>
public class TextureResolutionCapTests
{
    private const double Huge = 1e9;   // a claim far larger than any texture: the finest level allowed is wanted

    private static Guid G(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
    private static ResidencyCamera Cam() => new(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 512f, 360);

    [Theory]
    [InlineData(1024, 6, 2048, 0)]
    [InlineData(2048, 7, 2048, 0)]
    [InlineData(4096, 8, 2048, 1)]    // the rule before the setting: over 2,048 across starts one level down
    [InlineData(1024, 6, 1024, 0)]
    [InlineData(2048, 7, 1024, 1)]
    [InlineData(1024, 6, 512, 1)]
    [InlineData(2048, 7, 512, 2)]
    [InlineData(4096, 8, 512, 3)]
    [InlineData(1024, 6, 256, 2)]
    [InlineData(512, 5, 512, 0)]
    [InlineData(256, 4, 256, 0)]
    public void A_close_texture_is_wanted_at_the_finest_level_the_cap_allows(int size, int levels, int cap, int expected)
    {
        Assert.Equal(expected, TextureResidency.LevelForClaim(size, size, levels, Huge, 1.0, cap));
    }

    [Fact]
    public void The_cap_looks_at_the_longer_side_of_a_picture_that_is_not_square()
    {
        Assert.Equal(2, TextureResidency.LevelForClaim(2048, 512, 7, Huge, 1.0, 512));
    }

    [Fact]
    public void A_cap_needing_more_levels_than_the_texture_has_gives_its_coarsest()
    {
        Assert.Equal(1, TextureResidency.LevelForClaim(1024, 1024, 2, Huge, 1.0, 256));
    }

    [Fact]
    public void A_cap_never_makes_a_small_claim_wanted_finer_than_the_claim_alone_would()
    {
        Assert.Equal(5, TextureResidency.LevelForClaim(1024, 1024, 6, 4, 1.0, 512));    // 10 square pixels or less: the coarsest
        Assert.Equal(TextureResidency.LevelForClaim(1024, 1024, 6, 20000, 1.0), TextureResidency.LevelForClaim(1024, 1024, 6, 20000, 1.0, 2048));
        Assert.Equal(2, TextureResidency.LevelForClaim(1024, 1024, 6, 20000, 1.0, 512));   // the claim wants level 2 anyway (floor of log4 of 1,048,576 over 20,000); the cap's level 1 does not matter
    }

    [Fact]
    public void The_manager_starts_with_the_rule_it_always_had_and_takes_a_new_cap_at_once()
    {
        var r = new TextureResidency(new() { BudgetBytes = 1L << 30 });
        Assert.Equal(2048, r.MaxTextureResolution);
        r.MaxTextureResolution = 512;
        Assert.Equal(512, r.MaxTextureResolution);
        r.MaxTextureResolution = 0;
        Assert.Equal(2048, r.MaxTextureResolution);   // a value that cannot be a size is ignored
    }

    [Fact]
    public void A_cap_set_while_a_texture_is_resident_changes_the_level_wanted_at_once_and_the_next_frames_load_it()
    {
        var r = new TextureResidency(new() { BudgetBytes = 1L << 30 });
        var avg = PlaceholderColour.Of(90, 100, 110, 255);
        r.SetUser(new TextureUser(TextureUserKind.Face, 1, 0), G(1), new Bounds(new(3, 0, 0), 6f));   // near and large: wants full resolution
        void Frames(int from, int count)
        {
            for (int f = from; f < from + count; f++)
            {
                foreach (var a in r.Update(Cam(), TestClock.Frames(f)).Where(a => a.Kind != ResidencyActionKind.Evict).ToList())
                {
                    int levels = TextureLevels.Count(1024, 1024, 5), level = Math.Min(a.Level, levels - 1);
                    if (a.Kind == ResidencyActionKind.Level && r.PendingLevelOf(a.Texture) != a.Level) continue;
                    long bytes = TextureFootprint.Bytes(TextureLevels.Size(1024, level), TextureLevels.Size(1024, level), true);
                    r.Uploaded(a.Texture, level, levels, 1024, 1024, bytes, avg, TestClock.Frames(f));
                }
            }
        }
        Frames(0, 4);
        Assert.Equal(0, r.DesiredLevelOf(G(1)));
        Assert.Equal(0, r.LevelOf(G(1)));

        r.MaxTextureResolution = 512;

        Assert.Equal(1, r.DesiredLevelOf(G(1)));   // 1,024 across with a 512 cap: one level down, wanted at once
        Frames(100, 6);
        Assert.Equal(1, r.LevelOf(G(1)));
        Assert.True(r.AtDesired(G(1)));

        r.MaxTextureResolution = 2048;
        Assert.Equal(0, r.DesiredLevelOf(G(1)));
        Frames(400, 6);
        Assert.Equal(0, r.LevelOf(G(1)));
    }
}
