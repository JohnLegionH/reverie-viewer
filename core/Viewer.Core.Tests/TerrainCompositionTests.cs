// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Terrain composition (Docs/TERRAIN-COMPOSITION-SPEC.md), the handshake settings in captures, and the fixture.</summary>
public class TerrainCompositionTests
{
    private static RegionTerrain Flat(float start, float range) =>
        new([Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()], [start, start, start, start], [range, range, range, range]);

    [Theory]
    // start 10, range 40 (spec 2, no noise): c = (h - 10) * 4 / 40
    [InlineData(0f, 0f)]      // below the start: layer 0
    [InlineData(10f, 0f)]     // at the start: pure layer 0
    [InlineData(15f, 0.5f)]   // half way to layer 1
    [InlineData(20f, 1f)]     // a quarter of the range up: pure layer 1
    [InlineData(30f, 2f)]
    [InlineData(35f, 2.5f)]
    [InlineData(40f, 3f)]     // three quarters: pure layer 3
    [InlineData(90f, 3f)]     // above: clamped to layer 3
    public void The_composition_value_follows_height_bands(float h, float c) =>
        Assert.Equal(c, TerrainComposition.Value(Flat(10, 40), 100, 100, h, 256, 256, noise: false), 4);

    [Theory]
    [InlineData(0f, 1f, 0f, 0f, 0f)]
    [InlineData(0.5f, 0.5f, 0.5f, 0f, 0f)]
    [InlineData(1f, 0f, 1f, 0f, 0f)]
    [InlineData(1.25f, 0f, 0.75f, 0.25f, 0f)]
    [InlineData(2f, 0f, 0f, 1f, 0f)]
    [InlineData(2.9f, 0f, 0f, 0.1f, 0.9f)]
    [InlineData(3f, 0f, 0f, 0f, 1f)]
    public void The_blend_weights_are_linear_between_neighbouring_layers(float c, float w0, float w1, float w2, float w3)
    {
        var w = TerrainComposition.Weights(c);
        Assert.Equal(w0, w.W0, 4); Assert.Equal(w1, w.W1, 4); Assert.Equal(w2, w.W2, 4); Assert.Equal(w3, w.W3, 4);
        Assert.Equal(1f, w.W0 + w.W1 + w.W2 + w.W3, 4);
    }

    [Fact]
    public void Corner_values_sit_at_their_xy_named_corners()
    {
        // handshake order 00, 01, 10, 11 = south-west, north-west (x 0, y 1), south-east (x 1, y 0), north-east
        ImmutableArray<float> v = [1f, 2f, 3f, 4f];
        Assert.Equal(1f, TerrainComposition.Corners(v, 0, 0));
        Assert.Equal(2f, TerrainComposition.Corners(v, 0, 1));
        Assert.Equal(3f, TerrainComposition.Corners(v, 1, 0));
        Assert.Equal(4f, TerrainComposition.Corners(v, 1, 1));
        Assert.Equal(2.5f, TerrainComposition.Corners(v, 0.5f, 0.5f), 4);
        // so a start height rising to the east moves the bands east-to-west
        var t = new RegionTerrain([Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty], [0f, 0f, 40f, 40f], [40f, 40f, 40f, 40f]);
        Assert.True(TerrainComposition.Value(t, 10, 128, 20, 256, 256, noise: false) > TerrainComposition.Value(t, 246, 128, 20, 256, 256, noise: false));
    }

    [Fact]
    public void The_noise_is_deterministic_bounded_and_continuous_across_regions()
    {
        Assert.Equal(TerrainComposition.Noise(1000.5, 2000.25, 22), TerrainComposition.Noise(1000.5, 2000.25, 22));
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < 2000; i++)
        {
            float n = TerrainComposition.Noise(i * 3.7, i * 1.3, i % 50);
            min = Math.Min(min, n); max = Math.Max(max, n);
        }
        // spec 3: low part up to 6.5 plus turbulence up to 2.25 * 1.5, doubled
        Assert.InRange(min, -13f - 0.01f, 20f);
        Assert.InRange(max, -13f, 13f + 6.75f + 0.01f);
        Assert.True(max - min > 5, "the noise actually varies");
        // at a region border the same global point gives the same noise from either side
        var t = Flat(10, 40);
        float east = TerrainComposition.Value(t, 256, 50, 25, 256, 256, originX: 256 * 1000, originY: 256 * 1000);
        float westOfNext = TerrainComposition.Value(t, 0, 50, 25, 256, 256, originX: 256 * 1001, originY: 256 * 1000);
        Assert.Equal(east, westOfNext, 4);
    }

    // The official viewer's composition noise is 2D gradient (Perlin) noise with signed turbulence and no height term: a low-frequency
    // part times 6.5 plus two signed octaves times 2.25, doubled. Its figures, from an independent double-precision simulation of that
    // algorithm over 200,000 points with random unit gradients: mean 0.00 m, standard deviation 2.92 m (the three runs with different
    // gradient tables agree to 0.05 m), and for ground at 26 m with start 10 and range 60 the weight on the two upper layers is 0.12.
    private static (double Mean, double Std, double UpperWeight) NoiseFigures(float height)
    {
        var t = Flat(10, 60);
        double sum = 0, sum2 = 0, upper = 0; int n = 0;
        for (int j = 0; j < 300; j++)
            for (int i = 0; i < 300; i++)
            {
                double gx = 256000 + i * 3.37, gy = 256000 + j * 3.37;
                double tw = TerrainComposition.Noise(gx, gy, height);
                sum += tw; sum2 += tw * tw; n++;
                var (_, _, w2, w3) = TerrainComposition.Weights(TerrainComposition.Value(t, (float)(i * 3.37 % 1024), (float)(j * 3.37 % 1024), height, 1024, 1024, 256000 - 0, 256000 - 0));
                upper += w2 + w3;
            }
        double mean = sum / n;
        return (mean, Math.Sqrt(sum2 / n - mean * mean), upper / n);
    }

    [Fact]
    public void The_noise_has_the_official_statistics_zero_mean_and_a_standard_deviation_near_three_metres()
    {
        var (mean, std, _) = NoiseFigures(26f);
        Assert.InRange(mean, -0.35, 0.35);   // signed turbulence: no lean towards the higher layers
        Assert.InRange(std, 2.4, 3.6);
    }

    [Fact]
    public void The_noise_does_not_depend_on_the_height()
    {
        for (int i = 0; i < 50; i++)
            Assert.Equal(TerrainComposition.Noise(i * 7.3, i * 2.9, 5f), TerrainComposition.Noise(i * 7.3, i * 2.9, 61f));
    }

    [Fact]
    public void Low_ground_stays_mostly_in_the_lower_layers()
    {
        var (_, _, upper) = NoiseFigures(26f);
        Assert.InRange(upper, 0.05, 0.20);   // the official figure is 0.12; with the value noise it was 0.22 and more on the same ground
    }

    [Fact]
    public void Terrain_settings_round_trip_through_a_capture_and_older_captures_stay_readable()
    {
        var t = new RegionTerrain([Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), Guid.NewGuid()], [1.5f, 2f, 3f, 4f], [10f, 20f, 30f, 40f]);
        string path = TestTemp.NewFilePath("terrain", ".jsonl.gz");
        try
        {
            var w = new CaptureFile.Writer(path, new CaptureHeader(CaptureFile.FormatVersion, "t", Guid.Empty, 0, 0, "test"));
            var sim = new SimRef(((ulong)(1000 * 256) << 32) | (1000 * 256), "a");
            w.Write(new CaptureRecord(0, null, 1, new SimConnected(sim, 256, 256, false, t)));
            w.Write(new CaptureRecord(1, null, 1, new SimConnected(sim, 256, 256)));
            w.Dispose();
            var events = CaptureFile.Read(path).Records.Select(e => e.Event).OfType<SimConnected>().ToList();
            Assert.Equal(t, events[0].Terrain);
            Assert.Null(events[1].Terrain);   // no terrain written: an older capture's simConnected reads the same way
        }
        finally { TestTemp.Delete(path); }
    }

    [Fact]
    public void The_committed_synthetic_detail_textures_match_their_generator_and_decode_exactly()
    {
        string dir = Path.Combine(TestPaths.RepoRoot, "fixtures", "terrain");
        if (Environment.GetEnvironmentVariable("NV_WRITE_TERRAIN_DETAILS") == "1")
            for (int i = 0; i < 4; i++)
                File.WriteAllBytes(Path.Combine(dir, $"detail-{i}.j2c"), LmvTexture.EncodeLossless(TerrainDetails.Size, TerrainDetails.Size, TerrainDetails.Pixels(i)));
        var files = TerrainDetails.SyntheticFiles;
        Assert.Equal(4, files.Count);
        int layer = 0;
        foreach (var id in TerrainDetails.SyntheticTerrain.Details)
        {
            var d = LmvTexture.Decode(File.ReadAllBytes(Path.Combine(dir, files[id])));
            Assert.True(d.Ok, d.Refusal);
            Assert.Equal(TerrainDetails.Pixels(layer++), d.Texture!.Rgba);
        }
    }
}
