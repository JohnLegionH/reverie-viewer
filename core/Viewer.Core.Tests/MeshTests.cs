// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using Viewer.Core.Assets;
using Viewer.Core.Meshing;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>L1: the committed mesh zoo decodes through the adapter's guarded decoder.</summary>
public class MeshDecodeTests
{
    public static string MeshDir => Path.Combine(TestPaths.RepoRoot, "fixtures", "meshes");

    [Fact]
    public void The_committed_mesh_zoo_decodes_to_its_generator()
    {
        var zoo = MeshZooBuilder.Make();
        if (Environment.GetEnvironmentVariable("NV_WRITE_MESH_ZOO") == "1")
            foreach (var a in zoo)
                File.WriteAllBytes(Path.Combine(MeshDir, a.Name + ".llmesh"), a.Bytes);   // proposal only
        foreach (var a in zoo)
        {
            var r = LmvMesh.Decode(a.Id, File.ReadAllBytes(Path.Combine(MeshDir, a.Name + ".llmesh")));
            Assert.True(r.Ok, a.Name + ": " + r.Refusal);
            for (int d = 0; d < MeshGeometry.LodCount; d++)
            {
                var lod = r.Geometry!.Lods[d];
                if (a.FacesPerLod[d] == 0) { Assert.Null(lod); continue; }
                Assert.NotNull(lod);
                Assert.Equal(a.FacesPerLod[d], lod!.Faces.Count);
                Assert.True(a.TrianglesPerLod[d] == lod.Triangles, $"{a.Name} lod {d}: expected {a.TrianglesPerLod[d]} triangles, decoded {lod.Triangles} (faces {string.Join(",", lod.Faces.Select(f => f.Triangles))})");
                Assert.All(lod.Faces, f => Assert.All(f.Positions, p => Assert.InRange(p, -0.5001f, 0.5001f)));
            }
            Assert.Null(r.Geometry!.Skin);
        }
        Assert.Equal(1, LmvMesh.Decode(zoo[2].Id, zoo[2].Bytes).Geometry!.Resolve(0));   // no lowest LOD: the next higher
    }

    [Fact]
    public void Decoded_vertices_match_the_encoded_geometry_within_the_wire_quantisation()
    {
        // Positions travel as 16-bit values over the position domain (1 m here): error <= 1/65535 m, allow 2e-5.
        var cube = LmvMesh.Decode(Guid.NewGuid(), MeshZooBuilder.Make()[0].Bytes).Geometry!;
        var top = cube.Lods[3]!.Faces[0];
        for (int i = 0; i < top.Vertices; i++)
            Assert.Equal(0.5f, top.Positions[i * 3 + 2], 0.00002f);
    }
}

/// <summary>Hostile mesh assets are refused with a bounded cost; the corpus is committed (fixtures/meshes/fuzz).</summary>
[Collection("TimingTests")]
public class MeshFuzzTests
{
    private static string FuzzDir => Path.Combine(MeshDecodeTests.MeshDir, "fuzz");

    private static byte[] Cube => MeshZooBuilder.Make()[0].Bytes;

    private static byte[] Header(string body) => [(byte)'{', .. Be(body.Length > 0 ? 1 : 0), .. System.Text.Encoding.ASCII.GetBytes(body)];

    private static byte[] Be(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }

    private static byte[] Key(string k) => [(byte)'k', .. Be(k.Length), .. System.Text.Encoding.ASCII.GetBytes(k)];

    private static byte[] PartHeader(string part, int offset, int size) =>
        [(byte)'{', .. Be(1), .. Key(part), (byte)'{', .. Be(2), .. Key("offset"), (byte)'i', .. Be(offset), .. Key("size"), (byte)'i', .. Be(size), (byte)'}', (byte)'}'];

    private static byte[] Zlib(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>The corpus: name -> bytes. Hand-made classes of hostile input plus truncations of a real asset.</summary>
    internal static IEnumerable<(string Name, byte[] Bytes)> Corpus()
    {
        var cube = Cube;
        foreach (int cut in new[] { 1, 5, 17, 40, cube.Length / 3, cube.Length / 2, cube.Length - 1 })
            yield return ($"truncated-{cut}", cube[..cut]);
        yield return ("size-int-max", PartHeader("high_lod", 0, int.MaxValue));
        yield return ("size-negative", PartHeader("high_lod", 0, -5));
        yield return ("offset-beyond", [.. PartHeader("high_lod", 1 << 20, 16), .. new byte[16]]);
        yield return ("map-count-absurd", [(byte)'{', .. Be(0x7FFFFFFF)]);
        yield return ("array-count-absurd", [(byte)'{', .. Be(1), .. Key("x"), (byte)'[', .. Be(0x7FFFFFFF), (byte)']', (byte)'}']);
        yield return ("string-length-absurd", [(byte)'{', .. Be(1), .. Key("x"), (byte)'s', .. Be(0x7FFFFFF0), (byte)'}']);
        yield return ("deep-nesting", [(byte)'{', .. Be(1), .. Key("x"), .. Enumerable.Range(0, 5000).SelectMany(_ => new byte[] { (byte)'[', 0, 0, 0, 1 }), .. Enumerable.Repeat((byte)']', 5000), (byte)'}']);
        yield return ("unknown-type", [(byte)'{', .. Be(1), .. Key("x"), (byte)'Q', (byte)'}']);
        yield return ("offset-not-integer", [(byte)'{', .. Be(1), .. Key("high_lod"), (byte)'{', .. Be(1), .. Key("offset"), (byte)'s', .. Be(1), (byte)'9', (byte)'}', (byte)'}']);
        var junk = new byte[64];
        new Random(6).NextBytes(junk);
        yield return ("bad-zlib", [.. PartHeader("high_lod", 0, junk.Length), .. junk]);
        var bomb = Zlib(new byte[64 * 1024 * 1024]);   // 64 MiB of zeros in a few tens of KiB
        yield return ("zlib-bomb", [.. PartHeader("high_lod", 0, bomb.Length), .. bomb]);
        yield return ("oversized-asset", new byte[MeshLimits.Default.MaxAssetBytes + 1]);
        yield return ("not-a-map", "hello, mesh"u8.ToArray());
    }

    [Fact]
    public void The_committed_corpus_matches_its_generator()
    {
        var corpus = Corpus().Where(c => c.Name != "oversized-asset").ToList();   // 16 MiB of zeros is generated, not committed
        if (Environment.GetEnvironmentVariable("NV_WRITE_MESH_FUZZ") == "1")
        {
            Directory.CreateDirectory(FuzzDir);
            foreach (var (name, bytes) in corpus)
                File.WriteAllBytes(Path.Combine(FuzzDir, name + ".bin"), bytes);
        }
        foreach (var (name, bytes) in corpus.Where(c => !c.Name.StartsWith("truncated-")))   // truncations follow the (dated) encoder output
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(FuzzDir, name + ".bin")));
    }

    [Fact]
    public void Every_corpus_entry_is_refused_quickly_without_a_large_allocation()
    {
        var files = Directory.EnumerateFiles(FuzzDir, "*.bin").Select(f => (Path.GetFileNameWithoutExtension(f), File.ReadAllBytes(f)))
            .Append(("oversized-asset", new byte[MeshLimits.Default.MaxAssetBytes + 1])).ToList();
        Assert.True(files.Count >= 19, $"corpus has {files.Count} entries");
        foreach (var (name, bytes) in files)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var r = LmvMesh.Decode(Guid.NewGuid(), bytes);
            sw.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.False(r.Ok, name + " decoded");
            Assert.NotNull(r.Refusal);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"{name}: {sw.ElapsedMilliseconds} ms");
            Assert.True(allocated < 128L * 1024 * 1024, $"{name}: allocated {allocated / (1024 * 1024)} MiB");
        }
    }

    [Theory]
    [InlineData("size-int-max", "beyond the asset")]
    [InlineData("zlib-bomb", "inflates beyond")]
    [InlineData("bad-zlib", "bad zlib")]
    [InlineData("array-count-absurd", "count beyond the data")]
    [InlineData("deep-nesting", "nesting too deep")]
    [InlineData("offset-not-integer", "not an integer")]
    public void Each_limit_refuses_with_its_reason(string name, string reason)
    {
        var r = MeshAssetGuard.Validate(Corpus().Single(c => c.Name == name).Bytes);
        Assert.NotNull(r);
        Assert.Contains(reason, r!.Reason);
    }

    [Fact]
    public void Negative_control_the_zoo_passes_the_guard()
    {
        foreach (var a in MeshZooBuilder.Make())
            Assert.Null(MeshAssetGuard.Validate(a.Bytes));
    }

    [Fact]
    public void Seeded_mutations_never_crash_or_exceed_the_limits()
    {
        var sources = MeshZooBuilder.Make().Select(a => a.Bytes).ToList();
        int refused = 0, decoded = 0;
        for (int seed = 0; seed < 1500; seed++)
        {
            var rng = new Random(seed);
            var b = (byte[])sources[seed % sources.Count].Clone();
            int flips = 1 + rng.Next(8);
            for (int i = 0; i < flips; i++) b[rng.Next(b.Length)] ^= (byte)(1 << rng.Next(8));
            var r = LmvMesh.Decode(Guid.NewGuid(), b);   // must return, never throw
            if (r.Ok)
            {
                decoded++;
                foreach (var lod in r.Geometry!.Lods.Where(l => l is not null))
                    Assert.All(lod!.Faces, f => Assert.All(f.Indices, i => Assert.InRange(i, 0, f.Vertices - 1)));
            }
            else refused++;
        }
        Assert.True(refused > 0 && refused + decoded == 1500);
    }
}

/// <summary>SL's LOD rule at its switch boundaries (worked from the official viewer's formula at the factor 2.0 (its Ultra level),
/// with its distance factor 1 - 0.1 x 2 = 0.8; MeshLodFactorTests compares the whole range).</summary>
public class MeshLodRuleTests
{
    // A 1 m cube: radius = |0.5 * (1,1,1)| = 0.866 -> 0.87; lodFactor * radius = 1.74.
    private static readonly float R = MeshLodRule.Radius(Vector3.One);

    [Theory]
    [InlineData(8.48f, 3)]   // d' = 8.48 * 0.8 * pi / 3 = 7.10 -> tan 0.2451 -> 0.25 > 0.24
    [InlineData(8.49f, 2)]   // d' = 7.11 -> tan 0.2447 -> 0.24
    [InlineData(31.94f, 2)]  // d' = 26.76 -> tan 0.06502 -> 0.07
    [InlineData(31.95f, 1)]  // d' = 26.77 -> tan 0.06500 -> 0.06
    [InlineData(59.34f, 1)]  // d' = 49.71 -> tan 0.035003 -> 0.04
    [InlineData(59.35f, 0)]  // d' = 49.72 -> tan 0.034996 -> 0.03
    [InlineData(1.0f, 3)]    // inside the near ramp
    [InlineData(500f, 0)]
    public void The_detail_switches_where_the_official_viewer_switches(float distance, int detail) =>
        Assert.Equal(detail, MeshLodRule.Detail(distance, R, lodFactor: 2.0f));

    [Fact]
    public void Radius_uses_the_mesh_bias_and_a_higher_lod_factor_keeps_detail_further_out()
    {
        Assert.Equal(0.866f, R, 0.001f);
        Assert.Equal(1, MeshLodRule.Detail(20f, R, lodFactor: 1.25f));
        Assert.Equal(2, MeshLodRule.Detail(20f, R, lodFactor: 2.0f));
        Assert.Equal(3, MeshLodRule.Detail(20f, R, lodFactor: 4f));
    }

    [Fact]
    public void A_missing_lod_falls_back_to_the_nearest_higher_then_lower()
    {
        var face = new MeshFace([0, 0, 0, 1, 0, 0, 0, 1, 0], new float[9], new float[6], [0, 1, 2]);
        var g = new MeshGeometry([null, new MeshLod([face]), null, null], null);
        Assert.Equal(1, g.Resolve(0));
        Assert.Equal(1, g.Resolve(3));
        Assert.Equal(-1, new MeshGeometry([null, null, null, null], null).Resolve(2));
    }
}

/// <summary>Mesh fetch through the per-session asset access, against a fake transport.</summary>
public class SessionMeshesTests
{
    private static readonly Guid Id = new("4e560006-0000-4000-8000-0000000000aa");

    private static SessionMeshes Make(Func<Guid, bool> authorize, Func<Guid, CancellationToken, Task<AssetFetchResult>> fetch, SharedAssetStore? store = null) =>
        new(new SessionAssets(store ?? new SharedAssetStore(), authorize, fetch), (id, b) => { var r = LmvMesh.Decode(id, b); return (r.Geometry, r.Refusal); });

    [Fact]
    public async Task A_fetched_mesh_decodes()
    {
        var bytes = MeshZooBuilder.Make()[1].Bytes;
        using var m = Make(_ => true, (_, _) => Task.FromResult(AssetFetchResult.Ok(bytes)));
        var r = await m.GetAsync(Id, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Ok, r.Status);
        Assert.Equal(4, r.Geometry!.Lods.Count(l => l is not null));
    }

    [Fact]
    public async Task Not_found_is_reported()
    {
        using var m = Make(_ => true, (_, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.NotFound)));
        Assert.Equal(AssetFetchStatus.NotFound, (await m.GetAsync(Id, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Denied_never_fetches_even_when_the_shared_store_has_it()
    {
        int fetches = 0;
        var store = new SharedAssetStore();
        using (var other = Make(_ => true, (_, _) => { fetches++; return Task.FromResult(AssetFetchResult.Ok(MeshZooBuilder.Make()[0].Bytes)); }, store))
            Assert.Equal(AssetFetchStatus.Ok, (await other.GetAsync(Id, CancellationToken.None)).Status);
        using var m = Make(_ => false, (_, _) => { fetches++; return Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Failed)); }, store);
        var r = await m.GetAsync(Id, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Denied, r.Status);
        Assert.Null(r.Geometry);
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task A_hostile_asset_is_refused_not_thrown()
    {
        using var m = Make(_ => true, (_, _) => Task.FromResult(AssetFetchResult.Ok(new byte[] { (byte)'{', 0x7F, 0xFF, 0xFF, 0xFF })));
        var r = await m.GetAsync(Id, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Failed, r.Status);
        Assert.True(r.Refused);
    }

    [Fact]
    public async Task Disposing_the_session_cancels_an_in_flight_fetch()
    {
        var started = new TaskCompletionSource();
        var m = Make(_ => true, async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return AssetFetchResult.Of(AssetFetchStatus.Failed); });
        var pending = m.GetAsync(Id, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        m.Dispose();
        var r = await pending.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(AssetFetchStatus.Cancelled, r.Status);
    }
}
