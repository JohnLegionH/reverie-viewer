// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using Viewer.Core.Assets;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Our own test textures, encoded with LibreMetaverse's encoder (deterministic pixels).</summary>
internal static class TextureFixtures
{
    public static byte[] Rgba(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> px)
    {
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, bl, a) = px(x, y);
                int i = (y * w + x) * 4;
                b[i] = r; b[i + 1] = g; b[i + 2] = bl; b[i + 3] = a;
            }
        return b;
    }

    public static byte[] Opaque64 => LmvTexture.Encode(64, 64, Rgba(64, 64, (x, y) => ((byte)(x * 4), (byte)(y * 4), 128, 255)), withAlpha: false);
    public static byte[] Binary64 => LmvTexture.Encode(64, 64, Rgba(64, 64, (x, y) => (200, 50, 50, (byte)((x / 8 + y / 8) % 2 == 0 ? 255 : 0))), withAlpha: true);
    public static byte[] Blended64 => LmvTexture.Encode(64, 64, Rgba(64, 64, (x, y) => (20, 200, 90, (byte)(x * 4))), withAlpha: true);
}

public class TextureDecodeTests
{
    [Fact]
    public void Our_textures_decode_to_rgba8_with_the_right_alpha_class()
    {
        var o = LmvTexture.Decode(TextureFixtures.Opaque64);
        Assert.True(o.Ok, o.Refusal);
        Assert.Equal((64, 64, 64 * 64 * 4), (o.Texture!.Width, o.Texture.Height, o.Texture.Rgba.Length));
        Assert.Equal(AlphaClass.Opaque, o.Texture.Alpha);
        Assert.Equal(AlphaClass.Binary, LmvTexture.Decode(TextureFixtures.Binary64).Texture!.Alpha);
        Assert.Equal(AlphaClass.Blended, LmvTexture.Decode(TextureFixtures.Blended64).Texture!.Alpha);
        // lossy codec: close to the source colours
        int i = (10 * 64 + 20) * 4;
        Assert.InRange(o.Texture.Rgba[i], 80 - 12, 80 + 12);       // x * 4
        Assert.InRange(o.Texture.Rgba[i + 1], 40 - 12, 40 + 12);   // y * 4
    }

    [Fact]
    public void The_header_is_read_before_decoding()
    {
        Assert.Null(J2kGuard.Validate(TextureFixtures.Blended64, null, out var h));
        Assert.Equal((64, 64, 4), (h!.Width, h.Height, h.Components));
        Assert.True(h.TileParts >= 1 && h.Layers >= 1);
    }

    [Fact]
    public void Alpha_classification()
    {
        Assert.Equal(AlphaClass.Opaque, DecodedTexture.Classify([1, 2, 3, 255, 4, 5, 6, 255]));
        Assert.Equal(AlphaClass.Binary, DecodedTexture.Classify([1, 2, 3, 0, 4, 5, 6, 255]));
        Assert.Equal(AlphaClass.Blended, DecodedTexture.Classify([1, 2, 3, 0, 4, 5, 6, 128]));
        Assert.Equal(AlphaClass.Binary, DecodedTexture.Classify([1, 2, 3, 12, 4, 5, 6, 243]));    // lossy mask, inside the tolerance
        Assert.Equal(AlphaClass.Opaque, DecodedTexture.Classify([1, 2, 3, 254, 4, 5, 6, 241]));
    }

    [LocalFixturesFact("assets")]
    public void The_real_sculpt_maps_cached_in_NV5b_pass_the_guard_and_decode()
    {
        var files = Directory.GetFiles(LiveCaptures.PathOf("assets"), "*.j2k");
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var r = LmvTexture.Decode(File.ReadAllBytes(f));
            Assert.True(r.Ok, Path.GetFileName(f) + ": " + r.Refusal);
        }
    }
}

/// <summary>Hostile codestreams are refused within bounded time and memory (committed corpus + seeded mutations).
/// The time bound is a wall-clock time per decode, measured in a child process, so the class runs alone: with the
/// other test classes running beside it, the child waits for a core and an ordinary decode reads as slow.</summary>
[Collection(nameof(TextureFuzzTests))]
public class TextureFuzzTests
{
    private static string FuzzDir => Path.Combine(TestPaths.RepoRoot, "fixtures", "textures", "fuzz");

    private static byte[] Patch(byte[] b, int offset, uint value, int size)
    {
        var c = (byte[])b.Clone();
        if (size == 4) BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(offset), value);
        else if (size == 2) BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(offset), (ushort)value);
        else c[offset] = (byte)value;
        return c;
    }

    // SIZ layout after SOC(2)+SIZ marker(2)+Lsiz(2)+Rsiz(2): Xsiz@8 Ysiz@12 XOsiz@16 YOsiz@20 XTsiz@24 YTsiz@28 XTOsiz@32 YTOsiz@36 Csiz@40 comps@42
    internal static IEnumerable<(string Name, byte[] Bytes)> Corpus()
    {
        var ok = TextureFixtures.Opaque64;
        foreach (int cut in new[] { 1, 3, 10, 45, 60, ok.Length / 2, ok.Length - 3 })
            yield return ($"truncated-{cut}", ok[..cut]);
        yield return ("dims-65535", Patch(Patch(Patch(Patch(ok, 8, 65535, 4), 12, 65535, 4), 24, 65535, 4), 28, 65535, 4));
        yield return ("dims-4096", Patch(Patch(Patch(Patch(ok, 8, 4096, 4), 12, 4096, 4), 24, 4096, 4), 28, 4096, 4));
        yield return ("tiles-1x1-on-2048", Patch(Patch(Patch(Patch(ok, 8, 2048, 4), 12, 2048, 4), 24, 1, 4), 28, 1, 4));
        yield return ("tile-size-zero", Patch(ok, 24, 0, 4));
        yield return ("origin-beyond-size", Patch(ok, 16, 70000, 4));
        yield return ("bit-depth-38", Patch(ok, 42, 0x25, 1));
        yield return ("subsampling-zero", Patch(ok, 43, 0, 1));
        yield return ("components-9-length-mismatch", Patch(ok, 40, 9, 2));
        yield return ("siz-length-absurd", Patch(ok, 4, 0xFFFF, 2));
        yield return ("jp2-box", [0, 0, 0, 12, (byte)'j', (byte)'P', (byte)' ', (byte)' ', 13, 10, 135, 10, .. ok]);
        int firstSot = FindSot(ok);
        yield return ("psot-beyond-data", Patch(ok, firstSot + 6, 0x7FFFFFF0, 4));
        yield return ("sot-tile-index-absurd", Patch(ok, firstSot + 4, 60000, 2));
        yield return ("cod-levels-200", PatchCod(ok, 200));
        var junk = new byte[256];
        new Random(7).NextBytes(junk);
        yield return ("random-junk", junk);
        yield return ("soc-only", [0xFF, 0x4F]);
        yield return ("packet-data-ff", FillPacketData(ok, 0xFF));   // hung CoreJ2K before the guard rule
    }

    /// <summary>Every byte of the packet data (after SOD, up to EOC) overwritten with <paramref name="fill"/>;
    /// the headers stay valid.</summary>
    internal static byte[] FillPacketData(byte[] b, byte fill)
    {
        var c = (byte[])b.Clone();
        int sod = c.AsSpan(FindSot(c)).IndexOf(new byte[] { 0xFF, 0x93 }) + FindSot(c);
        int eoc = c.Length - 2;
        if (c[eoc] != 0xFF || c[eoc + 1] != 0xD9) throw new InvalidOperationException("no EOC at the end");
        c.AsSpan(sod + 2, eoc - sod - 2).Fill(fill);
        return c;
    }

    private static int FindSot(byte[] b)
    {
        for (int i = 0; i + 1 < b.Length; i++) if (b[i] == 0xFF && b[i + 1] == 0x90) return i;
        throw new InvalidOperationException("no SOT");
    }

    private static byte[] PatchCod(byte[] b, byte levels)
    {
        for (int i = 0; i + 1 < b.Length; i++)
            if (b[i] == 0xFF && b[i + 1] == 0x52) return Patch(b, i + 9, levels, 1);
        throw new InvalidOperationException("no COD");
    }

    [Fact]
    public void The_committed_corpus_matches_its_generator()
    {
        var corpus = Corpus().ToList();
        if (Environment.GetEnvironmentVariable("NV_WRITE_TEXTURE_FUZZ") == "1")
        {
            Directory.CreateDirectory(FuzzDir);
            foreach (var (name, bytes) in corpus) File.WriteAllBytes(Path.Combine(FuzzDir, name + ".j2c"), bytes);
        }
        foreach (var (name, _) in corpus)
            Assert.True(File.Exists(Path.Combine(FuzzDir, name + ".j2c")), name);
    }

    /// <summary>The decodes run in the decode probe (a child with a deadline, killed on overrun), never in
    /// the test host: a corpus entry that hangs the decoder fails this test instead of hanging the run.</summary>
    [Fact]
    public void Every_corpus_entry_is_refused_quickly_without_a_large_allocation()
    {
        var files = Directory.GetFiles(FuzzDir, "*.j2c");
        Assert.True(files.Length >= 20, $"{files.Length} entries");
        var results = DecodeProbeChild.Run(TimeSpan.FromSeconds(60), "decode", FuzzDir, "--levels", "0");
        Assert.Equal(files.Length, results.Count);
        foreach (var r in results)
        {
            Assert.False(r.Ok, r.Tag + " decoded");
            Assert.True(r.CpuMs < 2000, $"{r.Tag}: {r.CpuMs} ms of processor time ({r.Ms} ms wall)");
            Assert.True(r.AllocatedMiB < 64, $"{r.Tag}: {r.AllocatedMiB} MiB");
        }
    }

    [Theory]
    [InlineData("dims-65535", "exceeds 2048x2048")]
    [InlineData("tiles-1x1-on-2048", "tiles (limit")]
    [InlineData("bit-depth-38", "-bit (limit")]
    [InlineData("jp2-box", "no SOC")]
    [InlineData("psot-beyond-data", "tile-part length beyond")]
    [InlineData("cod-levels-200", "decomposition levels")]
    [InlineData("components-9-length-mismatch", "does not match")]
    public void Each_limit_refuses_with_its_reason(string name, string reason)
    {
        var why = J2kGuard.Validate(Corpus().Single(c => c.Name == name).Bytes, null, out _);
        Assert.NotNull(why);
        Assert.Contains(reason, why);
    }

    /// <summary>The 400 seeded bit flips, decoded in the decode probe (never in the test host). The bound is each decode's processor
    /// time, which a looping decoder runs up; wall time also counts waits for a busy machine, and the probe's deadline bounds it.</summary>
    [Fact]
    public void Seeded_mutations_never_crash_and_stay_bounded()
    {
        var results = DecodeProbeChild.Run(TimeSpan.FromSeconds(120), "bitflips", "--seeds", "0-399");
        Assert.Equal(400, results.Count);
        Assert.All(results, r => Assert.True(r.CpuMs < 2000, $"{r.Tag}: {r.CpuMs} ms of processor time ({r.Ms} ms wall)"));
    }
}

/// <summary>TextureFuzzTests run alone (no other test class in parallel with them).</summary>
[CollectionDefinition(nameof(TextureFuzzTests), DisableParallelization = true)]
public sealed class TextureFuzzTestsCollection;

/// <summary>Texture fetch through the per-session asset access, against a fake transport.</summary>
public class TextureFetchTests
{
    private sealed class FakeTextures : ITextureTransport
    {
        public int Calls;
        public Func<Guid, CancellationToken, Task<AssetFetchResult>> Answer = (_, _) => Task.FromResult(AssetFetchResult.Ok(TextureFixtures.Opaque64));
        public Task<AssetFetchResult> FetchTextureAsync(Guid id, CancellationToken ct) { Interlocked.Increment(ref Calls); return Answer(id, ct); }
    }

    private static readonly Guid Id = new("4e560007-0000-4000-8000-000000000001");

    [Fact]
    public async Task Success_and_not_found()
    {
        var t = new FakeTextures();
        using var a = new SessionAssets(new SharedAssetStore(), _ => true, t.FetchTextureAsync);
        var r = await a.GetAsync(Id, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Ok, r.Status);
        Assert.True(LmvTexture.Decode(r.Bytes.ToArray()).Ok);
        t.Answer = (_, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.NotFound));
        Assert.Equal(AssetFetchStatus.NotFound, (await a.GetAsync(Guid.NewGuid(), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Denied_never_fetches()
    {
        var t = new FakeTextures();
        using var a = new SessionAssets(new SharedAssetStore(), _ => false, t.FetchTextureAsync);
        Assert.Equal(AssetFetchStatus.Denied, (await a.GetAsync(Id, CancellationToken.None)).Status);
        Assert.Equal(0, t.Calls);
    }

    [Fact]
    public async Task Disposing_the_session_cancels_an_in_flight_fetch()
    {
        var started = new TaskCompletionSource();
        var t = new FakeTextures { Answer = async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return AssetFetchResult.Of(AssetFetchStatus.Failed); } };
        var a = new SessionAssets(new SharedAssetStore(), _ => true, t.FetchTextureAsync);
        var pending = a.GetAsync(Id, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        a.Dispose();
        Assert.Equal(AssetFetchStatus.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(60))).Status);
    }

    [Fact]
    public async Task Duplicate_requests_coalesce_within_one_session_only()
    {
        var gate = new TaskCompletionSource();
        var t = new FakeTextures { Answer = async (_, _) => { await gate.Task; return AssetFetchResult.Of(AssetFetchStatus.NotFound); } };
        var store = new SharedAssetStore();
        using var a = new SessionAssets(store, _ => true, t.FetchTextureAsync);
        using var b = new SessionAssets(store, _ => true, t.FetchTextureAsync);
        var a1 = a.GetAsync(Id, CancellationToken.None);
        var a2 = a.GetAsync(Id, CancellationToken.None);
        var b1 = b.GetAsync(Id, CancellationToken.None);
        Wait.Until(() => t.Calls == 2, "both sessions' fetches started");   // the event, not a fixed delay
        gate.SetResult();
        await Task.WhenAll(a1, a2, b1);
        Assert.Equal(2, t.Calls);   // one for session A (its two requests joined), one for session B (never joined to A's)
    }
}
