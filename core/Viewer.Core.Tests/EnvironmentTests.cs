// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The region environment (Docs/ENVIRONMENT-SPEC.md): LLSD, day cycle, water height, captures.</summary>
public class EnvironmentTests
{
    private static string Synthetic => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "fixtures", "environment", "synthetic-environment.llsd.xml"));

    [Fact]
    public void The_synthetic_environment_parses_to_its_sky_frames_and_water()
    {
        var e = RegionEnvironment.FromLlsd(Synthetic, out var why);
        Assert.True(e is not null, why);
        Assert.Equal("region", e!.Source);
        Assert.Equal([0.125f, 0.25f, 0.5f], e.Sky.Select(s => s.Key));
        Assert.Equal(new Vector3(0.05f, 0.25f, 0.30f), e.Water.FogColor);
        Assert.Equal(10f, e.Water.FogDensity);
        Assert.Equal((14400, 57600), (e.DayLength, e.DayOffset));
    }

    [Theory]
    // spec 2: the sun is +X (east) turned by sun_rotation; at noon (key 0.5, a 90-degree turn about -Y) it is overhead
    [InlineData(0.5, 0f, 0f, 1f)]
    // morning (key 0.25): 45 degrees up in the east
    [InlineData(0.25, 0.7071f, 0f, 0.7071f)]
    public void The_sun_direction_follows_the_keyframe_rotation(double f, float x, float y, float z)
    {
        var s = RegionEnvironment.FromLlsd(Synthetic, out _)!.SkyAt(f);
        Assert.Equal(x, s.SunDirection.X, 3); Assert.Equal(y, s.SunDirection.Y, 3); Assert.Equal(z, s.SunDirection.Z, 3);
    }

    [Fact]
    public void Between_keyframes_the_sky_is_interpolated_and_the_cycle_wraps()
    {
        var e = RegionEnvironment.FromLlsd(Synthetic, out _)!;
        var mid = e.SkyAt(0.375);   // half way from morning (0.25) to noon (0.5)
        var m = e.Sky[1]; var n = e.Sky[2];
        Assert.Equal((m.SunColor.X + n.SunColor.X) / 2, mid.SunColor.X, 4);
        Assert.Equal((m.HazeDensity + n.HazeDensity) / 2, mid.HazeDensity, 4);
        Assert.Equal(1f, mid.SunDirection.Length(), 3);
        // after the last key (0.5) the cycle runs on to the first (0.125) through 1.0
        var late = e.SkyAt(0.8);
        Assert.InRange(late.SunColor.X, MathF.Min(n.SunColor.X, e.Sky[0].SunColor.X) - 1e-4f, MathF.Max(n.SunColor.X, e.Sky[0].SunColor.X) + 1e-4f);
    }

    [Theory]
    [InlineData(0, 0.0)]            // (0 + 57600) mod 14400 = 0
    [InlineData(3600, 0.25)]
    [InlineData(7200, 0.5)]
    [InlineData(14400 * 10 + 10800, 0.75)]
    public void The_day_fraction_is_the_clock_plus_offset_modulo_the_day_length(double unix, double f) =>
        Assert.Equal(f, RegionEnvironment.Default.DayFraction(unix), 6);

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<llsd><map><key>success</key><boolean>0</boolean></map></llsd>")]
    public void Anything_but_an_EEP_day_cycle_is_refused_with_a_reason(string llsd)
    {
        Assert.Null(RegionEnvironment.FromLlsd(llsd, out var why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    // ----------------: every limit of the LLSD reader, each proved by its own test ----------------

    private static string Nested(int levels) =>
        "<llsd>" + string.Concat(Enumerable.Repeat("<array>", levels)) + string.Concat(Enumerable.Repeat("</array>", levels)) + "</llsd>";

    [Fact]
    public void Llsd_limit_nesting_depth_a_document_deeper_than_64_levels_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => Llsd.Parse(Nested(100)));
        Assert.Throws<InvalidDataException>(() => Llsd.Parse(Nested(Llsd.MaxDepth + 2)));
        Llsd.Parse(Nested(Llsd.MaxDepth));   // at the limit: accepted
    }

    [Fact]
    public void Llsd_limit_dtd_processing_is_prohibited()
    {
        const string withDtd = "<?xml version=\"1.0\"?><!DOCTYPE llsd [<!ENTITY x \"y\">]><llsd><string>&x;</string></llsd>";
        Assert.Throws<System.Xml.XmlException>(() => Llsd.Parse(withDtd));
        Assert.Null(RegionEnvironment.FromLlsd(withDtd, out var why));
        Assert.StartsWith("not LLSD", why);
    }

    [Fact]
    public void Llsd_limit_no_xml_resolver_an_external_entity_is_never_read()
    {
        // Even with DTD parsing switched ON (test only), the reader's settings carry no resolver, so an external entity
        // naming a local file is never read. A committed file with no markup serves as the "secret".
        string target = new Uri(Path.Combine(TestPaths.RepoRoot, "fixtures", "textures", "manifest.json")).AbsoluteUri;
        string doc = $"<?xml version=\"1.0\"?><!DOCTYPE llsd [<!ENTITY ext SYSTEM \"{target}\">]><llsd><string>&ext;</string></llsd>";
        const string marker = "L4 texture-zoo";
        string? Read(System.Xml.XmlReaderSettings s)
        {
            try { return Llsd.Parse(doc, s) as string; }
            catch (Exception e) when (e is System.Xml.XmlException or InvalidDataException) { return null; }
        }
        var ours = Llsd.ReaderSettings(System.Xml.DtdProcessing.Parse);
        Assert.DoesNotContain(marker, Read(ours) ?? "");
        // negative control: the same settings WITH a resolver read the file, so the test can see a resolver
        var withResolver = Llsd.ReaderSettings(System.Xml.DtdProcessing.Parse);
        withResolver.XmlResolver = new System.Xml.XmlUrlResolver();
        withResolver.MaxCharactersFromEntities = 0;
        Assert.Contains(marker, Read(withResolver) ?? "");
    }

    [Fact]
    public void Llsd_limit_total_size_a_document_over_512_KiB_in_utf8_bytes_is_refused_before_parsing()
    {
        // 600 000 bytes of otherwise valid LLSD
        string big = "<llsd><string>" + new string('a', 600_000) + "</string></llsd>";
        var e = Assert.Throws<InvalidDataException>(() => Llsd.Parse(big));
        Assert.Contains("bytes", e.Message);
        // bytes, not characters: 200 000 three-byte characters are 600 000 bytes (a character count would pass them)
        string wide = "<llsd><string>" + new string('€', 200_000) + "</string></llsd>";
        Assert.True(wide.Length < Llsd.MaxBytes);
        Assert.Throws<InvalidDataException>(() => Llsd.Parse(wide));
        // just under the cap: accepted
        Llsd.Parse("<llsd><string>" + new string('a', Llsd.MaxBytes - 64) + "</string></llsd>");
    }

    [Fact]
    public void Llsd_limit_element_count_a_document_with_more_than_50000_values_is_refused()
    {
        string Values(int n) => "<llsd><array>" + string.Concat(Enumerable.Repeat("<undef/>", n)) + "</array></llsd>";
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(Values(60_000)) < Llsd.MaxBytes);   // the byte cap is not what refuses it
        Assert.Throws<InvalidDataException>(() => Llsd.Parse(Values(60_000)));
        Assert.Equal(40_000, ((List<object?>)Llsd.Parse(Values(40_000))!).Count);
    }

    [Fact]
    public void Llsd_limit_numbers_are_parsed_in_the_invariant_culture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            // negative control: in this culture a culture-sensitive parse reads "1.5" as fifteen
            Assert.Equal(15.0, double.Parse("1.5", System.Globalization.CultureInfo.CurrentCulture));
            Assert.Equal(1.5, Llsd.Parse("<llsd><real>1.5</real></llsd>"));
            Assert.Equal(-12345L, Llsd.Parse("<llsd><integer>-12345</integer></llsd>"));
            var e = RegionEnvironment.FromLlsd(Synthetic, out _)!;
            Assert.Equal(10f, e.Water.FogDensity);
            Assert.Equal(0.05f, e.Water.FogColor.X);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")]   // overflows double: parses to Infinity
    public void Llsd_limit_non_finite_reals_are_rejected(string real)
    {
        Assert.Throws<InvalidDataException>(() => Llsd.Parse($"<llsd><real>{real}</real></llsd>"));
        Assert.Null(RegionEnvironment.FromLlsd(Synthetic.Replace("<real>0.7</real>", $"<real>{real}</real>"), out var why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void Llsd_limit_a_finite_real_too_large_for_a_float_never_reaches_the_scene_as_infinity()
    {
        // 1e300 is a finite double (the reader keeps it) but Infinity as a float: every value handed on must be finite
        string huge = Synthetic.Replace("<real>0.7</real>", "<real>1e300</real>").Replace("<real>2.0</real>", "<real>-1e300</real>").Replace("<real>10</real>", "<real>1e300</real>");
        Assert.NotEqual(Synthetic, huge);
        var e = RegionEnvironment.FromLlsd(huge, out var why);
        Assert.True(e is not null, why);
        static bool Fin(float f) => float.IsFinite(f);
        static bool V3(Vector3 v) => Fin(v.X) && Fin(v.Y) && Fin(v.Z);
        foreach (var s in e!.Sky)
            Assert.True(V3(s.SunDirection) && V3(s.SunColor) && V3(s.MoonDirection) && Fin(s.MoonBrightness) && V3(s.Ambient) && V3(s.BlueHorizon)
                && V3(s.BlueDensity) && Fin(s.HazeHorizon) && Fin(s.HazeDensity) && Fin(s.DensityMultiplier) && Fin(s.DistanceMultiplier), $"non-finite value in sky frame {s.Key}");
        var w = e.Water;
        Assert.True(V3(w.FogColor) && Fin(w.FogDensity) && Fin(w.Wave1.X) && Fin(w.Wave1.Y) && Fin(w.Wave2.X) && Fin(w.Wave2.Y) && Fin(w.FresnelScale) && Fin(w.FresnelOffset));
        Assert.Equal(16f, w.FogDensity);   // the field's default
        foreach (double f in new[] { 0.0, 0.2, 0.4, 0.6, 0.8 })
            Assert.True(V3(e.SkyAt(f).SunColor) && Fin(e.SkyAt(f).HazeDensity));
    }

    [Fact]
    public void Llsd_an_integer_that_overflows_is_refused_not_thrown_past_the_reader()
    {
        Assert.Throws<InvalidDataException>(() => Llsd.Parse("<llsd><integer>99999999999999999999999</integer></llsd>"));
        Assert.Null(RegionEnvironment.FromLlsd("<llsd><integer>99999999999999999999999</integer></llsd>", out var why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void Water_height_and_environment_round_trip_through_a_capture()
    {
        string path = TestTemp.NewFilePath("env", ".jsonl.gz");
        var sim = new SimRef(((ulong)(1000 * 256) << 32) | (1000 * 256), "a");
        try
        {
            var w = new CaptureFile.Writer(path, new CaptureHeader(CaptureFile.FormatVersion, "t", Guid.Empty, 0, 0, "test"));
            w.Write(new CaptureRecord(0, null, 1, new SimConnected(sim, 256, 256, false, null, 21.5f)));
            w.Write(new CaptureRecord(1, null, 1, new EnvironmentReceived(sim, Synthetic, null)));
            w.Write(new CaptureRecord(2, null, 1, new EnvironmentReceived(sim, null, "no ExtEnvironment capability")));
            w.Write(new CaptureRecord(3, null, 1, new SimConnected(sim, 256, 256)));
            w.Dispose();
            var ev = CaptureFile.Read(path).Records.Select(r => r.Event).ToList();
            Assert.Equal(21.5f, ((SimConnected)ev[0]).WaterHeight);
            Assert.Equal(Synthetic, ((EnvironmentReceived)ev[1]).Llsd);
            Assert.Equal("no ExtEnvironment capability", ((EnvironmentReceived)ev[2]).Why);
            Assert.Null(((SimConnected)ev[3]).WaterHeight);   // absent: an older capture reads the same way
        }
        finally { TestTemp.Delete(path); }
    }

    [CaptureFact("capture-region-a-v5.jsonl.gz")]
    public void RegionA_v5_carries_its_water_height_and_the_regions_EEP_environment()
    {
        using var s = ReplayRunner.Load(CaptureFile.Read(LiveCaptures.PathOf("capture-region-a-v5.jsonl.gz")));
        var r = s.World.Region!;
        Assert.Equal(20f, r.WaterHeight);
        Assert.NotNull(r.EnvironmentLlsd);
        var e = RegionEnvironment.FromLlsd(r.EnvironmentLlsd!, out var why);
        Assert.True(e is not null, why);
        Assert.Equal(8, e!.Sky.Length);   // the grid's default day cycle: 8 ground-level sky keyframes
        Assert.Equal((14400, 57600), (e.DayLength, e.DayOffset));
    }

    [CaptureFact("capture-region-a-v4.jsonl.gz")]
    public void An_older_capture_has_no_water_height_and_no_environment()
    {
        using var s = ReplayRunner.Load(CaptureFile.Read(LiveCaptures.PathOf("capture-region-a-v4.jsonl.gz")));
        Assert.Null(s.World.Region!.WaterHeight);
        Assert.Null(s.World.Region!.EnvironmentLlsd);
    }
}
