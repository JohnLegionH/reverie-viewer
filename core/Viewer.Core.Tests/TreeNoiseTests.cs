// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The trunk noise of the official viewer's trees: its table is filled by the C runtime's rand() after srand(42), so the noise is the same
/// on every run. The generator is the Windows runtime's (state * 214013 + 2531011, the top bits of the state), which is what the Windows
/// viewer draws with.
/// </summary>
public class TreeNoiseTests
{
    [Fact]
    public void The_generator_after_seed_42_gives_the_runtimes_first_five_numbers()
    {
        var r = new MsvcRand();
        r.Seed(42);
        Assert.Equal([175, 400, 17869, 30056, 16083], [r.Next(), r.Next(), r.Next(), r.Next(), r.Next()]);
    }

    [Fact]
    public void The_first_gradient_is_the_first_number_taken_through_the_tables_formula()
        => Assert.Equal(-0.31640625f, TreeNoise.G1(0));   // (175 % 512 - 256) / 256

    [Fact]
    public void The_permutation_is_a_shuffle_of_0_to_255_repeated_after_256()
    {
        var seen = new bool[256];
        for (int i = 0; i < 256; i++) { Assert.InRange(TreeNoise.P(i), 0, 255); seen[TreeNoise.P(i)] = true; }
        Assert.All(seen, s => Assert.True(s));
        for (int i = 0; i < 258; i++) Assert.Equal(TreeNoise.P(i), TreeNoise.P(256 + i));
        Assert.NotEqual(Enumerable.Range(0, 256), Enumerable.Range(0, 256).Select(TreeNoise.P));
    }

    [Fact]
    public void The_two_and_three_dimensional_gradients_are_unit_vectors_and_repeat()
    {
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(1.0, Math.Sqrt(Math.Pow(TreeNoise.G2(i, 0), 2) + Math.Pow(TreeNoise.G2(i, 1), 2)), 4);
            Assert.Equal(1.0, Math.Sqrt(Math.Pow(TreeNoise.G3(i, 0), 2) + Math.Pow(TreeNoise.G3(i, 1), 2) + Math.Pow(TreeNoise.G3(i, 2), 2)), 4);
            Assert.Equal(TreeNoise.G3(i, 1), TreeNoise.G3(256 + i, 1));
            Assert.Equal(TreeNoise.G1(i), TreeNoise.G1(256 + i));
        }
    }

    [Fact]
    public void The_noise_is_zero_at_whole_number_points_and_stays_small_between_them()
    {
        Assert.Equal(0f, TreeNoise.Noise3(3f, -2f, 7f));
        Assert.Equal(0f, TreeNoise.Noise3(0f, 0f, 0f));
        float max = 0;
        for (int i = 0; i < 2000; i++) max = Math.Max(max, Math.Abs(TreeNoise.Noise3(i * 0.137f, i * 0.291f - 40f, i * 0.053f + 3f)));
        Assert.InRange(max, 0.2f, 1.2f);   // varies, and a gradient noise never leaves about +-1
    }

    [Fact]
    public void Turbulence_sums_the_noise_at_each_octave_over_its_frequency()
    {
        var v = new Vector3(0.31f, 0.77f, 1.4f);
        float want = 0;
        for (float f = 5f; f >= 1f; f *= 0.5f) want += TreeNoise.Noise3(f * v.X, f * v.Y, f * v.Z) / f;
        Assert.NotEqual(0f, want);
        Assert.Equal(want, TreeNoise.Turbulence3(v, 5f));
    }
}
