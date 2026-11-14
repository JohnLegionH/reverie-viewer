// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Imaging;
using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The discard level a texture is asked for by its size on screen, against the official rule written out again here independently of the code under
/// test (LLViewerLODTexture::processTextureStats in llviewertexture.cpp: the level is floor(log4(texels in the image / pixels the face claims)); at
/// least 1 for an image wider or taller than the largest size allowed; the coarsest level for a claim of 10 square pixels or less; a claim is
/// first limited to the largest size allowed squared). Textures are decided a slice at a time within the frame's time, and the level that results
/// must not depend on how the work was cut up.
/// </summary>
public class TextureLevelRuleTests
{
    private static Guid G(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
    private static ResidencyCamera Cam(float x = 0) => new(new(x, 0, 30), Vector3.UnitX, Vector3.UnitZ, MathF.PI / 3, 16f / 9, 0.1f, 4096f, 1080);

    private static int Official(int w, int h, int levels, double claim, int maxRes = 2048)
    {
        int coarsest = levels - 1;
        claim = Math.Min(claim, (double)maxRes * maxRes);
        if (claim <= 10) return coarsest;
        double d = Math.Floor(Math.Log((double)w * h / claim) / Math.Log(4));
        int min = (w > maxRes || h > maxRes) ? 1 : 0;
        return (int)Math.Clamp(d, Math.Min(min, coarsest), coarsest);
    }

    [Theory]
    [InlineData(1024, 1024, 6, 1_048_576.0)]   // claims every texel: full resolution
    [InlineData(1024, 1024, 6, 262_144.0)]     // a quarter of the texels: one level down
    [InlineData(1024, 1024, 6, 65_536.0)]
    [InlineData(1024, 1024, 6, 4_000.0)]
    [InlineData(1024, 1024, 6, 11.0)]
    [InlineData(1024, 1024, 6, 10.0)]          // 10 or fewer: the coarsest
    [InlineData(512, 512, 5, 100_000.0)]
    [InlineData(256, 256, 4, 65_536.0)]
    [InlineData(2048, 1024, 6, 500_000.0)]
    [InlineData(4096, 4096, 8, 16_777_216.0)]  // over 2,048 across: never the full image
    [InlineData(4096, 2048, 8, 100.0)]
    [InlineData(16, 16, 5, 10.0)]              // a small image: 10 square pixels or fewer still gives the coarsest
    [InlineData(16, 16, 5, 11.0)]              // one more pixel: the level its size gives, not the coarsest
    public void The_level_asked_for_follows_the_official_rule(int w, int h, int levels, double claim)
        => Assert.Equal(Official(w, h, levels, claim), TextureResidency.LevelForClaim(w, h, levels, claim));

    [Theory]
    [InlineData(1024, 1024, 6, 1_048_576.0, 512)]   // limited to 512: one level down
    [InlineData(1024, 512, 6, 500_000.0, 512)]      // wider than 512: not the full image
    [InlineData(2048, 2048, 7, 4_000_000.0, 1024)]
    [InlineData(256, 256, 4, 65_536.0, 512)]        // already within the limit: unchanged
    public void A_lower_largest_size_limits_the_level_as_the_official_rule_does(int w, int h, int levels, double claim, int maxRes)
        => Assert.Equal(Official(w, h, levels, claim, maxRes), TextureResidency.LevelForClaim(w, h, levels, claim, 1.0, maxRes));

    [Fact]
    public void The_level_wanted_does_not_depend_on_how_the_scoring_was_cut_into_frames()
    {
        // 3,000 textures of 1024 x 1024, each used by one face at a different distance: scored whole in one frame, or 40 at a time over many frames,
        // every texture ends with the level the official rule gives for its claim
        var whole = Build(int.MaxValue, TimeSpan.Zero);
        var sliced = Build(40, TimeSpan.Zero);
        whole.Update(Cam(), TimeSpan.Zero);
        for (int f = 0; f < 200; f++)
            sliced.Update(Cam(), TimeSpan.FromMilliseconds(f));
        for (int i = 0; i < 3_000; i++)
        {
            int want = whole.DesiredLevelOf(G(i));
            Assert.Equal(want, sliced.DesiredLevelOf(G(i)));
            Assert.InRange(want, 0, 5);
        }
        // and the rule itself, per texture: the claim the camera makes for the face gives the official level
        var cam = Cam();
        for (int i = 0; i < 3_000; i += 97)
        {
            double claim = cam.VirtualSize(Bounds(i), 1f);
            Assert.Equal(Official(1024, 1024, 6, claim), whole.DesiredLevelOf(G(i)));
        }
    }

    private static Bounds Bounds(int i) => new(new(8 + i * 0.1f, (i % 17) - 8, 30), 1.5f);

    private static TextureResidency Build(int usersPerFrame, TimeSpan now)
    {
        var r = new TextureResidency(new() { BudgetBytes = 1L << 40, UsersPerFrame = usersPerFrame, MaxLoadsPerFrame = 100_000 });
        for (int i = 0; i < 3_000; i++)
        {
            r.SetUser(new TextureUser(TextureUserKind.Face, (uint)i, 0, 1), G(i), Bounds(i));
            r.Uploaded(G(i), 5, TextureLevels.Count(1024, 1024, 6), 1024, 1024, 4096, PlaceholderColour.Of(90, 100, 110, 255), now);
        }
        return r;
    }
}
