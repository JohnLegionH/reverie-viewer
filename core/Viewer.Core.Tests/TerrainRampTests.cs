// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The ground blend as the official terrain shader does it: the composition value and a per-vertex jitter are read from a two-dimensional
/// ramp picture, so the edge between two layers is not a straight linear fade. The expected numbers below were worked out outside the code
/// under test, by reading the official ramp picture (bottom row first, bilinear, clamped) and the shader's mix chain.
/// </summary>
public class TerrainRampTests
{
    [Theory]
    // (u, v, value) read from the official ramp picture
    [InlineData(0.00f, 0.5f, 0.9961f)]
    [InlineData(0.25f, 0.5f, 0.9353f)]
    [InlineData(0.50f, 0.5f, 0.3088f)]
    [InlineData(0.75f, 0.5f, 0.1078f)]
    [InlineData(1.00f, 0.5f, 0.0078f)]
    [InlineData(0.50f, 0.0f, 0.5471f)]
    [InlineData(0.50f, 1.0f, 0.7431f)]
    [InlineData(0.50f, 0.9f, 0.6596f)]
    public void The_ramp_reads_as_the_official_picture(float u, float v, float expected) =>
        Assert.Equal(expected, TerrainRamp.Sample(u, v), 3);

    [Fact]
    public void The_ramp_is_clamped_outside_zero_to_one()
    {
        Assert.Equal(0.9961f, TerrainRamp.Sample(0f, 0.5f), 3);   // the picture is read at all
        Assert.Equal(TerrainRamp.Sample(0f, 0.5f), TerrainRamp.Sample(-3f, 0.5f), 5);
        Assert.Equal(TerrainRamp.Sample(1f, 0.5f), TerrainRamp.Sample(4f, 0.5f), 5);
        Assert.Equal(TerrainRamp.Sample(0.5f, 0f), TerrainRamp.Sample(0.5f, -2f), 5);
    }

    [Theory]
    // (composition, jitter, w0, w1, w2, w3): the shader's mix( mix(c3, c2, a2), mix(c1, c0, a1), aFinal ) with a1 = ramp(c), aFinal = ramp(c - 1), a2 = ramp(c - 2)
    [InlineData(0.0f, 0.5f, 0.9922f, 0.0039f, 0.0039f, 0.0000f)]
    [InlineData(0.5f, 0.1f, 0.5984f, 0.3977f, 0.0039f, 0.0000f)]
    [InlineData(0.5f, 0.5f, 0.3076f, 0.6885f, 0.0039f, 0.0000f)]
    [InlineData(0.5f, 0.9f, 0.6570f, 0.3391f, 0.0039f, 0.0000f)]
    [InlineData(1.0f, 0.5f, 0.0078f, 0.9883f, 0.0039f, 0.0000f)]
    [InlineData(1.13f, 0.5f, 0.0076f, 0.9649f, 0.0273f, 0.0001f)]
    [InlineData(1.5f, 0.1f, 0.0047f, 0.5961f, 0.3977f, 0.0016f)]
    [InlineData(1.5f, 0.5f, 0.0024f, 0.3064f, 0.6885f, 0.0027f)]
    [InlineData(2.0f, 0.5f, 0.0001f, 0.0078f, 0.9883f, 0.0039f)]
    [InlineData(2.5f, 0.5f, 0.0001f, 0.0078f, 0.3064f, 0.6858f)]
    [InlineData(3.0f, 0.5f, 0.0001f, 0.0078f, 0.0078f, 0.9844f)]
    public void The_layer_weights_follow_the_official_mix_chain(float c, float jitter, float w0, float w1, float w2, float w3)
    {
        var w = TerrainComposition.LayerWeights(c, jitter);
        Assert.Equal(w0, w.W0, 3); Assert.Equal(w1, w.W1, 3); Assert.Equal(w2, w.W2, 3); Assert.Equal(w3, w.W3, 3);
        Assert.Equal(1f, w.W0 + w.W1 + w.W2 + w.W3, 4);
    }

    [Fact]
    public void Halfway_between_layers_the_blend_is_not_the_linear_one()
    {
        // the old linear rule gave one half each at 1.5; the official ramp gives the upper layer 0.69 at the middle jitter
        var w = TerrainComposition.LayerWeights(1.5f, 0.5f);
        Assert.True(w.W2 > 0.6f, $"upper layer weight {w.W2}");
        Assert.True(w.W1 < 0.4f, $"lower layer weight {w.W1}");
    }

    [Fact]
    public void The_jitter_is_in_zero_to_one_centred_on_a_half_and_varies()
    {
        double sum = 0, sumSq = 0; int n = 0; float min = 1, max = 0;
        for (int gy = 0; gy < 1024; gy += 8)
            for (int gx = 0; gx < 1024; gx += 8)
            {
                float j = TerrainComposition.Jitter(gx + 256000.0, gy + 512000.0);
                Assert.InRange(j, 0f, 1f);
                sum += j; sumSq += (double)j * j; n++; min = Math.Min(min, j); max = Math.Max(max, j);
            }
        double mean = sum / n, sd = Math.Sqrt(sumSq / n - mean * mean);
        Assert.InRange(mean, 0.4, 0.6);
        Assert.True(sd > 0.1, $"spread {sd}");
        Assert.True(max - min > 0.4f, $"range {min}..{max}");
    }

    [Fact]
    public void The_jitter_changes_slowly_and_depends_only_on_the_global_position()
    {
        // it does vary over a few hundred metres, with a lattice about 155 m across: a metre step moves it by a few hundredths at most
        float lo = 1, hi = 0;
        for (double x = 0; x < 600; x += 7) { float j = TerrainComposition.Jitter(x, 300); lo = Math.Min(lo, j); hi = Math.Max(hi, j); }
        Assert.True(hi - lo > 0.1f, $"moved {lo}..{hi} over 600 m");
        for (double x = 0; x < 600; x += 7)
            Assert.True(Math.Abs(TerrainComposition.Jitter(x + 1, 300) - TerrainComposition.Jitter(x, 300)) < 0.03);
        Assert.Equal(TerrainComposition.Jitter(256000.0 + 40, 512000.0 + 90), TerrainComposition.Jitter(256040.0, 512090.0));
    }

    private static TerrainGrid Grid(int size, bool composed)
    {
        var t = SyntheticTerrain.Make("jit", size, size);
        var patches = t.Patches().ToDictionary(p => (p.X, p.Y), p => p.Heights);
        PatchSource src = (int x, int y, out float[] h) => patches.TryGetValue((x, y), out h!);
        return new TerrainGrid(size, size, 16, src) { Composition = composed ? TerrainDetails.SyntheticTerrain : null, OriginX = 256000, OriginY = 512000 };
    }

    [Fact]
    public void A_composed_chunk_carries_the_jitter_of_each_vertex_and_a_plain_one_none()
    {
        var chunk = Grid(256, true).Build(1, 2)!;
        Assert.NotNull(chunk.Jitter);
        Assert.Equal(chunk.VertexCount, chunk.Jitter!.Length);
        for (int v = 0; v < chunk.VertexCount; v += 37)
        {
            double gx = chunk.Positions[v * 3], gy = -chunk.Positions[v * 3 + 2];
            Assert.Equal(TerrainComposition.Jitter(256000 + gx, 512000 + gy), chunk.Jitter[v], 5);
        }
        Assert.Null(Grid(256, false).Build(1, 2)!.Jitter);
    }

    [Fact]
    public void The_prepared_surface_carries_the_composition_and_the_jitter_as_its_two_texture_coordinates()
    {
        var chunk = Grid(256, true).Build(0, 0)!;
        Assert.NotNull(chunk.Jitter);
        var s = TerrainSurface.Prepare(chunk);
        const int stride = 12;   // a vertex's attribute data: its colour (4 bytes), then the two texture coordinates
        Assert.Equal(chunk.VertexCount * stride, s.AttributeData.Length);
        for (int v = 0; v < chunk.VertexCount; v += 29)
        {
            float u0 = BinaryPrimitives.ReadSingleLittleEndian(s.AttributeData.AsSpan(v * stride + 4));
            float u1 = BinaryPrimitives.ReadSingleLittleEndian(s.AttributeData.AsSpan(v * stride + 8));
            Assert.Equal(chunk.Composition![v], u0, 5);
            Assert.Equal(chunk.Jitter![v], u1, 5);
        }
    }
}
