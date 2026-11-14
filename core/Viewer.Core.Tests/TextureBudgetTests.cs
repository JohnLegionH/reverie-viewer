// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Residency;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The texture budget.</summary>
public class TextureBudgetTests
{
    private const long MiB = TextureBudget.MiB;
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(10);
    private const long Half8 = 4096 * MiB;   // an 8 GB card's share (VRAM / 2)

    // what each card class gets (MiB): VRAM / 2, minus the larger of 512 MiB or a fifth of that half, minus the
    // non-texture memory, at least 768 MiB. Static: the 768 MiB assumed before the engine is read (or with live accounting
    // off). Live: a non-texture reading of 300 MiB (measured 28-269 MiB at 1920x1080).
    public static readonly TheoryData<long, long, long> CardTable = new()
    {
        { 16384L, 5785L, 6253L },   // 16 GB: 8,192 - 1,638.4 - 768 / 300
        { 8192L, 2508L, 2976L },    // 8 GB: 4,096 - 819.2 - 768 / 300
        { 4096L, 768L, 1236L },     // 4 GB: 2,048 - 512 - 768 = 768 (the minimum) / 300
        { 2048L, 768L, 768L },      // 2 GB (hardware-floor class): 1,024 - 512 leaves 512: the minimum
        { 1024L, 768L, 768L },      // 1 GB: the minimum
    };

    [Theory]
    [MemberData(nameof(CardTable))]
    public void The_budget_is_half_the_video_memory_less_the_reserve_with_the_static_non_texture_figure(long vramMiB, long staticMiB, long liveMiB)
    {
        _ = liveMiB;
        Assert.Equal(staticMiB, TextureBudget.For(vramMiB * MiB) / MiB);
        Assert.Equal(staticMiB, new LiveTextureBudget(vramMiB * MiB).Budget / MiB);   // what the app starts with
    }

    [Theory]
    [MemberData(nameof(CardTable))]
    public void With_a_live_reading_the_budget_follows_what_the_engine_holds(long vramMiB, long staticMiB, long liveMiB)
    {
        _ = staticMiB;
        Assert.Equal(liveMiB, TextureBudget.For(vramMiB * MiB, 300 * MiB) / MiB);
        var b = new LiveTextureBudget(vramMiB * MiB);
        Assert.Equal(liveMiB, b.Update(300 * MiB, T0) / MiB);
    }

    [Fact]
    public void The_reserve_is_512_MiB_or_a_fifth_of_the_share_whichever_is_larger()
    {
        // 4 GB: half is 2,048, a fifth 409.6 < 512: 512 is kept back. 16 GB: half 8,192, a fifth 1,638.4 > 512.
        Assert.Equal((2048 - 512) * MiB, TextureBudget.For(4096 * MiB, 0));
        Assert.Equal(8192 * MiB - 8192 * MiB / 5, TextureBudget.For(16384 * MiB, 0));
    }

    // Negative control: the whole-card formula (VRAM - 512 MiB - non-texture, at least 768) fails the table, so the
    // table would catch a return to it.
    [Theory]
    [InlineData(16384L)]
    [InlineData(8192L)]
    [InlineData(4096L)]
    public void Negative_control_the_whole_card_formula_fails_the_table(long vramMiB)
    {
        static long WholeCard(long vram, long nonTexture) => Math.Max(768 * MiB, vram - 512 * MiB - nonTexture);
        var row = CardTable.Select(r => (object[])r).Single(r => (long)r[0] == vramMiB);
        Assert.NotEqual((long)row[1], WholeCard(vramMiB * MiB, 768 * MiB) / MiB);
        Assert.NotEqual((long)row[2], WholeCard(vramMiB * MiB, 300 * MiB) / MiB);
    }

    [Fact]
    public void Unreadable_video_memory_gets_768_MiB() =>
        Assert.Equal(768 * MiB, TextureBudget.For(null));

    [Theory]
    [InlineData(null)]
    [InlineData(2048L)]
    [InlineData(16384L)]
    public void A_configured_cap_always_wins(long? vramMiB)
    {
        Assert.Equal(100 * MiB, TextureBudget.For(vramMiB * MiB, 300 * MiB, 100 * MiB));
        var live = new LiveTextureBudget(vramMiB * MiB, 100 * MiB);
        Assert.False(live.Live);
        Assert.Equal(100 * MiB, live.Update(5000 * MiB, T0));
        Assert.Equal("configured", live.Source);
    }

    [Theory]
    [InlineData(512L, 0L)]
    [InlineData(1024L, 0L)]
    [InlineData(2048L, 4096L)]
    [InlineData(4096L, 100000L)]
    [InlineData(16384L, 100000L)]
    public void The_minimum_holds_however_much_the_engine_holds(long vramMiB, long nonTextureMiB) =>
        Assert.True(TextureBudget.For(vramMiB * MiB, nonTextureMiB * MiB) >= 768 * MiB);

    [Fact]
    public void A_jump_in_non_texture_memory_lowers_the_budget()
    {
        var b = new LiveTextureBudget(8192 * MiB);
        Assert.True(b.Live);
        long first = b.Update(300 * MiB, T0);
        Assert.Equal(Half8 - Half8 / 5 - 300 * MiB, first);
        long after = b.Update(900 * MiB, T0 + TimeSpan.FromSeconds(2));   // e.g. a 4K window's render targets
        Assert.Equal(Half8 - Half8 / 5 - 900 * MiB, after);
        Assert.True(after < first);
        Assert.Equal(2, b.Changes);   // the static starting point to 300, then to 900
    }

    [Fact]
    public void The_smoothing_ignores_a_change_under_32_MiB()
    {
        var b = new LiveTextureBudget(8192 * MiB);
        long first = b.Update(300 * MiB, T0);
        Assert.Equal(first, b.Update(300 * MiB + 31 * MiB, T0 + TimeSpan.FromSeconds(5)));
        Assert.Equal(first, b.Update(300 * MiB - 31 * MiB, T0 + TimeSpan.FromSeconds(10)));
        Assert.NotEqual(first, b.Update(300 * MiB + 32 * MiB, T0 + TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void The_budget_moves_at_most_once_a_second()
    {
        var b = new LiveTextureBudget(8192 * MiB);
        long first = b.Update(300 * MiB, T0);
        Assert.Equal(first, b.Update(900 * MiB, T0 + TimeSpan.FromMilliseconds(999)));   // big change, too soon
        Assert.Equal(Half8 - Half8 / 5 - 900 * MiB, b.Update(900 * MiB, T0 + TimeSpan.FromMilliseconds(1000)));
    }

    [Fact]
    public void Negative_control_with_live_accounting_off_the_budget_ignores_the_jump()
    {
        var b = new LiveTextureBudget(8192 * MiB, live: false);
        Assert.False(b.Live);
        long fixedBudget = TextureBudget.For(8192 * MiB);
        Assert.Equal(fixedBudget, b.Update(300 * MiB, T0));
        Assert.Equal(fixedBudget, b.Update(900 * MiB, T0 + TimeSpan.FromSeconds(2)));
        Assert.Equal(0, b.Changes);
        Assert.Equal("video memory, static", b.Source);
    }

    [Fact]
    public void Unreadable_video_memory_does_not_follow_the_engine()
    {
        var b = new LiveTextureBudget(null);
        Assert.False(b.Live);
        Assert.Equal(768 * MiB, b.Update(300 * MiB, T0));
        Assert.Equal(768 * MiB, b.Update(5000 * MiB, T0 + TimeSpan.FromSeconds(5)));
        Assert.Equal("unreadable", b.Source);
    }

    [Fact]
    public void Test_renders_keep_their_fixed_budget_of_384_MiB() => Assert.Equal(384 * MiB, TextureBudget.TestRenderFixed);
}
