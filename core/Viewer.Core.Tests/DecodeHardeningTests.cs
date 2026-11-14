// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>Runs the decode probe (Viewer.Core.DecodeProbe) as a child with a deadline. A child that
/// overruns is killed with its whole tree and the call throws (<see cref="ChildProcess"/>); nothing is left running.</summary>
internal static class DecodeProbeChild
{
    /// <summary>One decode: wall time (<c>Ms</c>) and the probe's processor time over the decode (<c>CpuMs</c>).</summary>
    public sealed record Result(string Tag, bool Ok, long Ms, long AllocatedMiB, long CpuMs, string Why);

    public static List<Result> Run(TimeSpan deadline, params string[] args)
    {
        Assert.True(File.Exists(TestPaths.DecodeProbeDll), $"probe not built: {TestPaths.DecodeProbeDll}");
        var (code, output) = ChildProcess.Run(ChildProcess.DotnetHost, [TestPaths.DecodeProbeDll, .. args], deadline);
        Assert.True(code == 0, $"probe exited {code}: {output}");
        Assert.DoesNotContain("CRASH ", output);
        var results = new List<Result>();
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("RESULT ")))
        {
            var f = line.Split(' ', 8);
            results.Add(new($"{f[1]} {f[2]}", f[3] == "1", long.Parse(f[4]), long.Parse(f[5]), long.Parse(f[6]), JsonSerializer.Deserialize<string>(f[7]) ?? ""));
        }
        return results;
    }
}

[Collection("TimingTests")]
public class DecoderHangTests(ITestOutputHelper output)
{
    private static string FuzzFile(string name) => Path.Combine(TestPaths.RepoRoot, "fixtures", "textures", "fuzz", name + ".j2c");

    [Fact]
    public void The_committed_hang_case_is_our_own_texture_with_its_packet_data_filled_with_0xFF()
    {
        var bytes = File.ReadAllBytes(FuzzFile("packet-data-ff"));
        Assert.Equal(TextureFuzzTests.FillPacketData(TextureFixtures.Opaque64, 0xFF), bytes);
        // the header is intact: the guard passed it (only the new packet-data rule refuses it)
        Assert.Contains("0xFF followed by 0xFF", J2kGuard.Validate(bytes, null, out _));
    }

    /// <summary>The hang, in a child with a deadline, at full resolution and at the coarsest level: before the guard
    /// rule the child overran and was killed (the test failed); now the guard refuses it at once with its reason.
    /// The child first decodes a valid texture of the zoo at the same levels (a warm-up: a cold child
    /// once took 279-495 ms to refuse), and the limit is 1 s: far under the 20 s deadline that proves no hang, far
    /// over a warm refusal (0-4 ms measured).</summary>
    [Fact]
    public void A_codestream_whose_packet_data_is_all_0xFF_is_refused_without_hanging()
    {
        var sw = Stopwatch.StartNew();
        var warm = Path.Combine(TestPaths.RepoRoot, "fixtures", "textures", "tex-gradient.j2c");
        var all = DecodeProbeChild.Run(TimeSpan.FromSeconds(20), "decode", warm, FuzzFile("packet-data-ff"), "--levels", "0,max");
        output.WriteLine($"{sw.ElapsedMilliseconds} ms: " + string.Join("; ", all));
        Assert.Equal(4, all.Count);
        Assert.All(all.Take(2), r => Assert.True(r.Ok, $"warm-up {r.Tag}: {r.Why}"));
        var results = all.Skip(2).ToList();
        Assert.All(results, r =>
        {
            Assert.StartsWith("packet-data-ff.j2c ", r.Tag);
            Assert.False(r.Ok);
            Assert.Contains("0xFF followed by 0xFF", r.Why);
            Assert.True(r.Ms < 1000, $"{r.Tag}: {r.Ms} ms");
        });
    }
}

/// <summary>The bit-stuffing rule in <see cref="J2kGuard"/>, checked on the guard alone (no decode).</summary>
public class PacketDataRuleTests(ITestOutputHelper output)
{
    private static int Sod(byte[] b)
    {
        int sot = b.AsSpan().IndexOf(new byte[] { 0xFF, 0x90 });
        return sot + b.AsSpan(sot).IndexOf(new byte[] { 0xFF, 0x93 });
    }

    private static byte[] WithPair(byte[] b, int at, byte second)
    {
        var c = (byte[])b.Clone();
        c[at] = 0xFF;
        c[at + 1] = second;
        return c;
    }

    [Fact]
    public void Only_a_byte_above_0x8F_after_0xFF_is_refused()
    {
        var ok = TextureFixtures.Opaque64;
        int at = Sod(ok) + 10;
        Assert.Null(J2kGuard.Validate(WithPair(ok, at, 0x8F), null, out _));   // allowed by the standard (MQ coder range)
        Assert.Null(J2kGuard.Validate(WithPair(ok, at, 0x00), null, out _));
        foreach (byte b in new byte[] { 0x90, 0x91, 0x92, 0x93, 0xD9, 0xFF })   // SOP / EPH not enabled in this file
            Assert.Contains($"0xFF followed by 0x{b:X2}", J2kGuard.Validate(WithPair(ok, at, b), null, out _));
    }

    [Fact]
    public void The_rule_applies_to_the_bytes_present_of_a_prefix_and_ignores_the_cut()
    {
        var cs = PartialFetchTests.Codestream.Value;
        int at = Sod(cs) + 100;
        var bad = WithPair(cs, at, 0xFF);
        Assert.NotNull(J2kGuard.Validate(bad[..(at + 50)], null, out _, allowTruncated: true));
        Assert.Null(J2kGuard.Validate(bad[..(at - 10)], null, out _, allowTruncated: true));   // the bad pair is not present
        var endsWithFF = cs[..(at + 1)];
        endsWithFF[^1] = 0xFF;                                                                 // a cut right after a 0xFF
        Assert.Null(J2kGuard.Validate(endsWithFF, null, out _, allowTruncated: true));
    }

    /// <summary>SOP and EPH markers are allowed exactly when the COD enables them (CoreJ2K's encoder writes them on request).</summary>
    [Fact]
    public void Sop_and_eph_are_allowed_only_where_the_COD_enables_them()
    {
        var rgba = TextureLevelDecodeTests.Smooth(128, 128, false);
        var withMarkers = EncodeWith(rgba, sop: true, eph: true);
        Assert.True(withMarkers.AsSpan().IndexOf(new byte[] { 0xFF, 0x91 }) > 0, "no SOP written");
        Assert.Null(J2kGuard.Validate(withMarkers, null, out _));
        var d = LmvTexture.Decode(withMarkers);   // our own clean content: decoded in-process
        Assert.True(d.Ok, d.Refusal);
        var plain = EncodeWith(rgba, sop: false, eph: false);
        var withSop = WithPair(plain, Sod(plain) + 10, 0x91);
        Assert.Contains("0xFF followed by 0x91", J2kGuard.Validate(withSop, null, out _));
        // an SOP whose length field is wrong is refused even where SOP is enabled
        int sop = withMarkers.AsSpan().IndexOf(new byte[] { 0xFF, 0x91 });
        var badSop = (byte[])withMarkers.Clone();
        badSop[sop + 3] = 9;
        Assert.Contains("SOP length", J2kGuard.Validate(badSop, null, out _));
    }

    private static byte[] EncodeWith(byte[] rgba, bool sop, bool eph)
    {
        var header = System.Text.Encoding.ASCII.GetBytes("P6\n128 128\n255\n");
        var ppm = new byte[header.Length + 128 * 128 * 3];
        header.CopyTo(ppm, 0);
        for (int i = 0; i < 128 * 128; i++) { ppm[header.Length + i * 3] = rgba[i * 4]; ppm[header.Length + i * 3 + 1] = rgba[i * 4 + 1]; ppm[header.Length + i * 3 + 2] = rgba[i * 4 + 2]; }
        using var input = new MemoryStream(ppm);
        var source = CoreJ2K.J2kImage.CreateEncodableSource(input)!;
        var p = new CoreJ2K.Configuration.J2KEncoderConfiguration().WithFileFormat(false).ToParameterList();
        p["rate"] = "2";
        p["Psop"] = sop ? "on" : "off";
        p["Peph"] = eph ? "on" : "off";
        return CoreJ2K.J2kImage.ToBytes(source, p);
    }

    /// <summary>Packet headers packed into a PPT segment follow the same rule.</summary>
    [Fact]
    public void Packed_packet_headers_in_a_PPT_follow_the_rule()
    {
        var ok = TextureFixtures.Opaque64;
        Assert.Contains("PPT", J2kGuard.Validate(InsertPpt(ok, [0x12, 0xFF, 0xFF, 0x00]), null, out _));
        Assert.Null(J2kGuard.Validate(InsertPpt(ok, [0x12, 0xFF, 0x7F, 0x00]), null, out _));
    }

    /// <summary>A PPT segment (Zppt 0, then <paramref name="headers"/>) inserted just before SOD, with Psot grown to match.</summary>
    private static byte[] InsertPpt(byte[] b, byte[] headers)
    {
        int sot = b.AsSpan().IndexOf(new byte[] { 0xFF, 0x90 }), sod = Sod(b);
        byte[] seg = [0xFF, 0x61, 0, (byte)(3 + headers.Length), 0, .. headers];
        var c = (byte[])[.. b[..sod], .. seg, .. b[sod..]];
        uint psot = BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(sot + 6));
        if (psot != 0) BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(sot + 6), psot + (uint)seg.Length);
        return c;
    }

    /// <summary>The rule's cost: linear in the bytes, measured on our own 1024x1024 and 2048x2048 codestreams.</summary>
    [Fact]
    public void The_rule_costs_little_on_large_textures()
    {
        foreach (int size in new[] { 1024, 2048 })
        {
            var cs = LmvTexture.EncodeLayered(size, size, Noise(size), "0.0625 0.25 0.5 1.0 2.0", 2.0);
            Assert.Null(J2kGuard.Validate(cs, null, out _));
            var times = new List<double>();
            for (int i = 0; i < 50; i++)
            {
                var sw = Stopwatch.StartNew();
                J2kGuard.Validate(cs, null, out _);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            output.WriteLine($"{size}x{size}, {cs.Length} bytes: guard p50 {times[25]:F3} ms, max {times[^1]:F3} ms");
            Assert.True(times[25] < 5, $"{size}: p50 {times[25]} ms");
        }
    }

    private static byte[] Noise(int size)
    {
        var rgba = TextureLevelDecodeTests.Smooth(size, size, false);
        var rng = new Random(size);
        for (int i = 0; i < rgba.Length; i++) if (i % 4 != 3) rgba[i] = (byte)Math.Clamp(rgba[i] + rng.Next(-40, 41), 0, 255);
        return rgba;
    }
}

/// <summary>
/// Many-layer codestreams are decoded at reduced levels like any other. The state the decoder carries from one decode to
/// the next must not reach ordinary textures: every level of the texture zoo and our own codestreams decodes byte for byte the
/// same alone, straight after a many-layer decode on the same thread, and while many-layer decodes run on other threads.
/// In the decode probe, a fresh process, so the baseline comes before any many-layer decode.
/// </summary>
public class ManyLayerLeakTests(ITestOutputHelper output)
{
    private static string Leak(string ordinary)
    {
        var (code, text) = ChildProcess.Run(ChildProcess.DotnetHost, [TestPaths.DecodeProbeDll, "leak", "--ordinary", ordinary, "--many", "own"], TimeSpan.FromMinutes(5));
        Assert.Equal(0, code);
        return text.Split('\n').Single(l => l.StartsWith("LEAK ")).Trim();
    }

    [Fact]
    public void Ordinary_textures_decode_the_same_after_and_during_many_layer_decodes()
    {
        var line = Leak(Path.Combine(TestPaths.RepoRoot, "fixtures", "textures") + ",own");
        output.WriteLine(line);
        Assert.Contains("refused 0 differ-after 0 differ-during 0", line);
    }

    [LocalFixturesFact("assets")]
    public void The_cached_real_textures_decode_the_same_after_and_during_many_layer_decodes()
    {
        var line = Leak(LiveCaptures.PathOf("assets"));
        output.WriteLine(line);
        // every cached texture is accounted for (the cache grows with each recapture): the ordinary ones (8 layers or
        // fewer, or unreadable) all went through the probe, and the rest are many-layer codestreams (avatar bakes), which
        // the probe uses only as the many-layer side
        var all = Directory.GetFiles(LiveCaptures.PathOf("assets")).Where(f => f.EndsWith(".j2k") || f.EndsWith(".j2c")).ToList();
        int ordinary = all.Count(f => Viewer.Core.Imaging.J2kGuard.Validate(File.ReadAllBytes(f), null, out var h) is not null || h!.Layers <= Viewer.Core.Imaging.TextureByteRange.MaxLayers);
        int manyLayer = all.Count - ordinary;
        output.WriteLine($"cached textures {all.Count}: ordinary {ordinary}, many-layer {manyLayer}");
        Assert.True(ordinary >= 62, ordinary + " ordinary cached textures");
        Assert.Contains($"files {ordinary} ", line);
        Assert.Contains("refused 0 differ-after 0 differ-during 0", line);
    }
}

/// <summary>
/// Our own many-layer codestream (27 layers, LibreMetaverse's encoder) decoded at every reduced level
/// several times in a row and 8 at once gives the same pixels every time, and each level stays within the decode tests'
/// tolerance (<see cref="TextureLevelDecodeTests"/>, 8) of level 0 scaled down. In the decode probe, a fresh process (as the other decode checks).
/// The 1024 x 1024 stream is cut into four tiles. With the decoder before its fix a reduced level of it was not repeatable
/// (L1 20.6, L4 107.0) and the viewer made its levels from a full decode; the reduced decode is used now.
/// </summary>
public class ManyLayerLevelTests(ITestOutputHelper output)
{
    private static List<string> Repeat(string own)
    {
        var (code, text) = ChildProcess.Run(ChildProcess.DotnetHost, [TestPaths.DecodeProbeDll, "repeat", "--own", own, "--times", "4"], TimeSpan.FromMinutes(5));
        Assert.Equal(0, code);
        return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    [Theory]
    [InlineData("lmv-1024")]
    [InlineData("lmv-512")]
    public void A_many_layer_codestream_decodes_every_level_the_same_every_time_and_close_to_level_0(string own)
    {
        var lines = Repeat(own);
        foreach (var l in lines) output.WriteLine(l);
        Assert.Contains(lines, l => l.StartsWith("file ") && l.Contains(" layers 27 "));
        var repeats = lines.Where(l => l.StartsWith("REPEAT ")).ToList();
        var close = lines.Where(l => l.StartsWith("CLOSE ")).ToList();
        Assert.True(repeats.Count >= 4, $"{repeats.Count} levels");
        Assert.Equal(repeats.Count, close.Count);
        Assert.All(repeats, l => Assert.Matches(@"sequential max 0\.00 \(differ 0 of 3\) parallel max 0\.00 \(differ 0 of 8\)", l));
        Assert.All(close, l =>
        {
            double d = double.Parse(l.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(d < TextureLevelDecodeTests.Tolerance, l);
        });
    }
}

/// <summary>
/// The guard refuses a codestream declaring more than 64 quality layers
/// (a resource limit like the decomposition-level limit: measured 389 MiB for one 1024x1024 decode at 20,658
/// layers). Real textures declare 1-7, the grid's bakes 14-19, LibreMetaverse's encoder 27, CoreJ2K's lossless 32.
/// </summary>
public class LayerCapTests
{
    private static int Cod(byte[] b)
    {
        for (int i = 0; i + 1 < b.Length; i++) if (b[i] == 0xFF && b[i + 1] == 0x52) return i;
        return -1;
    }

    private static byte[] WithLayers(byte[] src, int layers)
    {
        var b = (byte[])src.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(Cod(b) + 6), (ushort)layers);
        return b;
    }

    private static byte[] Detailed(int size, int seed)
    {
        var rng = new Random(seed);
        var b = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            int x = i % size, y = i / size;
            b[i * 4] = (byte)Math.Clamp(x * 255 / size + rng.Next(-40, 41), 0, 255);
            b[i * 4 + 1] = (byte)Math.Clamp(y * 255 / size + rng.Next(-40, 41), 0, 255);
            b[i * 4 + 2] = (byte)Math.Clamp(128 + rng.Next(-40, 41), 0, 255);
            b[i * 4 + 3] = 255;
        }
        return b;
    }

    [Theory]
    [InlineData(65)]
    [InlineData(20658)]   // the fuzzed header, rebuilt on our own 1024x1024 codestream
    [InlineData(65535)]
    public void More_than_64_quality_layers_are_refused(int layers)
    {
        var own = LmvTexture.EncodeLayered(1024, 1024, Detailed(1024, 3), "0.0625 0.25 0.5 1.0 2.0", 2.0);
        var bytes = WithLayers(own, layers);
        Assert.Equal($"{layers} quality layers (limit 64)", J2kGuard.Validate(bytes, null, out _));
        Assert.False(LmvTexture.Decode(bytes).Ok);
        Assert.False(LmvTexture.DecodeLevel(bytes, 2).Ok);
        Assert.Contains("quality layers (limit 64)", J2kGuard.Validate(bytes.AsSpan(0, bytes.Length / 2), null, out _, allowTruncated: true));
    }

    [Fact]
    public void Sixty_four_layers_and_every_real_encoder_still_pass()
    {
        var own = LmvTexture.EncodeLayered(256, 256, Detailed(256, 1), "0.0625 0.25 0.5 1.0 2.0", 2.0);
        Assert.Null(J2kGuard.Validate(WithLayers(own, 64), null, out var h64));
        Assert.Equal(64, h64!.Layers);
        var lmv = LmvTexture.Encode(1024, 1024, Detailed(1024, 3), withAlpha: false);
        var lossless = LmvTexture.EncodeLossless(1024, 1024, Detailed(1024, 3));
        var d = Detailed(1024, 5);
        var bake = LmvTexture.EncodePlanes(1024, 1024, Enumerable.Range(0, 5).Select(c => Enumerable.Range(0, 1024 * 1024).Select(i => c == 4 ? (byte)255 : d[i * 4 + Math.Min(c, 3)]).ToArray()).ToList());
        foreach (var (name, cs, expected) in new[] { ("lmv", lmv, 27), ("lossless", lossless, 32), ("bake", bake, -1) })
        {
            Assert.True(J2kGuard.Validate(cs, null, out var h) is null, name);
            if (expected > 0) Assert.Equal(expected, h!.Layers);
            else Assert.InRange(h!.Layers, 9, 64);
            Assert.True(LmvTexture.DecodeLevel(cs, 1).Ok, name);
        }
    }

    [Fact]
    public void A_measurement_still_reads_a_many_layer_header()
    {
        var own = LmvTexture.EncodeLayered(256, 256, Detailed(256, 1), "0.0625 0.25 0.5 1.0 2.0", 2.0);
        var (h, why) = TextureLayers.Read(WithLayers(own, 20658));
        Assert.Null(h);
        Assert.Contains("limit 64", why);
        var m = TextureLayers.Measure(WithLayers(own, 20658));
        Assert.Equal(20658, m.Header!.Layers);   // the live check's layer report counts what is there
    }
}

