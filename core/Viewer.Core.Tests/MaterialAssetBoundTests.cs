// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using LibreMetaverse.StructuredData;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A PBR material asset is user content stored on the grid, and the library that reads its LLSD wrapper puts no limit on
/// nesting or on declared lengths. These tests hold the bound in front of that read: hostile assets end as "no material"
/// without a stack overflow, an out-of-memory error or an exception. The ones that could kill the process run in a child
/// with a deadline (the decode probe's <c>material</c> command), so a crash shows as a failed test, not a dead runner.
/// </summary>
public class MaterialAssetBoundTests
{
    private const string Gltf = "{\"asset\":{\"version\":\"2.0\"},\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,0.5,0.25,1]}}]}";
    private static readonly TimeSpan ChildDeadline = TimeSpan.FromSeconds(60);

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Be32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static readonly byte[] BinaryHeader = Ascii("<? LLSD/Binary ?>\n");

    private static byte[] Key(string k) => Join(Ascii("k"), Be32(k.Length), Ascii(k));

    private static byte[] Str(string s) => Join(Ascii("s"), Be32(Encoding.UTF8.GetByteCount(s)), Encoding.UTF8.GetBytes(s));

    /// <summary>The map entries of a valid material, without the surrounding map.</summary>
    private static byte[] MaterialEntries(string gltf = Gltf) =>
        Join(Key("version"), Str("1.1"), Key("type"), Str("GLTF 2.0"), Key("data"), Str(gltf));

    private static byte[] ValidBinary() => Join(BinaryHeader, Ascii("{"), Be32(3), MaterialEntries(), Ascii("}"));

    private static byte[] ValidXml() => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" ?><llsd><map><key>version</key><string>1.1</string><key>type</key><string>GLTF 2.0</string>" +
        "<key>data</key><string>" + System.Security.SecurityElement.Escape(Gltf) + "</string></map></llsd>");

    /// <summary>A valid material map that also holds one extra entry nested <paramref name="depth"/> arrays deep.</summary>
    private static byte[] BinaryWithNesting(int depth)
    {
        var open = Enumerable.Repeat(Join(Ascii("["), Be32(1)), depth).SelectMany(x => x).ToArray();
        return Join(BinaryHeader, Ascii("{"), Be32(4), MaterialEntries(), Key("extra"), open, Ascii("!"), Ascii(new string(']', depth)), Ascii("}"));
    }

    private static byte[] XmlWithNesting(int depth) => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" ?><llsd><map><key>version</key><string>1.1</string><key>type</key><string>GLTF 2.0</string>" +
        "<key>data</key><string>" + System.Security.SecurityElement.Escape(Gltf) + "</string><key>extra</key>" +
        string.Concat(Enumerable.Repeat("<array>", depth)) + "<undef/>" + string.Concat(Enumerable.Repeat("</array>", depth)) + "</map></llsd>");

    // ---- the hostile assets (each within the 256 KiB the cap lets through)

    private static IEnumerable<(string Name, byte[] Bytes)> Hostile()
    {
        const int Deep = 20_000;
        yield return ("binary-20000-nested-arrays", Join(BinaryHeader,
            Enumerable.Repeat(Join(Ascii("["), Be32(1)), Deep).SelectMany(x => x).ToArray(), Ascii("!"), Ascii(new string(']', Deep))));
        yield return ("binary-20000-nested-maps", Join(BinaryHeader,
            Enumerable.Repeat(Join(Ascii("{"), Be32(1), Key("a")), Deep).SelectMany(x => x).ToArray(), Ascii("!"), Ascii(new string('}', Deep))));
        yield return ("xml-20000-nested-arrays", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" ?><llsd>" + string.Concat(Enumerable.Repeat("<array>", Deep)) + "<undef/>" + string.Concat(Enumerable.Repeat("</array>", Deep)) + "</llsd>"));
        yield return ("xml-20000-nested-maps", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" ?><llsd>" + string.Concat(Enumerable.Repeat("<map><key>a</key>", Deep)) + "<undef/>" + string.Concat(Enumerable.Repeat("</map>", Deep)) + "</llsd>"));
        yield return ("binary-string-declares-2GiB", Join(BinaryHeader, Ascii("{"), Be32(1), Key("data"), Ascii("s"), Be32(int.MaxValue), Ascii("tiny")));
        yield return ("binary-blob-declares-2GiB", Join(BinaryHeader, Ascii("{"), Be32(1), Key("data"), Ascii("b"), Be32(int.MaxValue), Ascii("tiny")));
        yield return ("binary-array-declares-2G-elements", Join(BinaryHeader, Ascii("["), Be32(int.MaxValue), Ascii("!")));
        yield return ("binary-map-declares-2G-entries", Join(BinaryHeader, Ascii("{"), Be32(int.MaxValue), Key("a"), Ascii("!")));
        yield return ("binary-key-declares-2GiB", Join(BinaryHeader, Ascii("{"), Be32(1), Ascii("k"), Be32(int.MaxValue), Ascii("tiny")));
        // a small entity expansion ("billion laughs" shape), which an XML reader that allows a DTD would expand in memory
        yield return ("xml-entity-expansion", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" ?><!DOCTYPE llsd [<!ENTITY a \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\">" +
            "<!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">" +
            "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">" +
            "<!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">]><llsd><string>&d;&d;&d;&d;&d;&d;&d;&d;</string></llsd>"));
    }

    public static IEnumerable<object[]> HostileNames() => Hostile().Select(h => new object[] { h.Name });

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void A_hostile_material_asset_ends_as_no_material_in_a_child_process(string name)
    {
        var dir = TestTemp.NewFolder("material-bound");
        try
        {
            string path = Path.Combine(dir, name + ".bin");
            File.WriteAllBytes(path, Hostile().First(h => h.Name == name).Bytes);
            Assert.True(File.Exists(TestPaths.DecodeProbeDll), $"probe not built: {TestPaths.DecodeProbeDll}");
            var (code, output) = ChildProcess.Run(ChildProcess.DotnetHost, [TestPaths.DecodeProbeDll, "material", path], ChildDeadline);
            Assert.True(code == 0, $"probe exited {code} (a stack overflow shows as a crash code): {Truncate(output)}");
            Assert.DoesNotContain("CRASH ", output);
            Assert.Contains($"MATERIAL {name}.bin 0 0", output);
        }
        finally { TestTemp.Delete(dir); }
    }

    private static string Truncate(string s) => s.Length <= 600 ? s : s[..600] + "...";

    // ---- the limits, in process (depths that are safe to run on the old code too)

    [Fact]
    public void A_valid_material_still_decodes_to_the_same_gltf_in_every_form()
    {
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(ValidXml()));
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(ValidBinary()));
        var library = OSDParser.SerializeLLSDBinary(new OSDMap { ["version"] = "1.1", ["type"] = "GLTF 2.0", ["data"] = Gltf });
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(library));
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(Ascii("{'version':'1.0','type':'GLTF 2.0','data':'" + Gltf + "'}")));
    }

    [Fact]
    public void Nesting_up_to_128_is_read_and_deeper_is_refused()
    {
        // the map is depth 1, so an extra entry holding 126 arrays reaches depth 128
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(BinaryWithNesting(126)));
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(XmlWithNesting(126)));
        Assert.Null(LmvMaterials.GltfOfMaterialAsset(BinaryWithNesting(200)));
        Assert.Null(LmvMaterials.GltfOfMaterialAsset(XmlWithNesting(200)));
    }

    [Fact]
    public void A_declared_length_beyond_the_data_is_refused_before_anything_is_allocated()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var hostile = Join(BinaryHeader, Ascii("{"), Be32(4), MaterialEntries(), Key("more"), Ascii("s"), Be32(1_000_000_000), Ascii("x"), Ascii("}"));
        Assert.Null(LmvMaterials.GltfOfMaterialAsset(hostile));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 * 1024 * 1024, "a declared length must not be allocated");
    }

    [Fact]
    public void A_refused_asset_is_reported_once_and_an_accepted_or_foreign_one_is_not()
    {
        var reasons = new List<string>();
        Assert.Null(LmvMaterials.GltfOfMaterialAsset(BinaryWithNesting(200), reasons.Add));
        Assert.Single(reasons);
        Assert.False(string.IsNullOrWhiteSpace(reasons[0]));

        reasons.Clear();
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(ValidBinary(), reasons.Add));
        Assert.Null(LmvMaterials.GltfOfMaterialAsset(Ascii("{'version':'9','type':'x','data':'y'}"), reasons.Add));   // well-formed, not a material
        Assert.Empty(reasons);
    }

    [Fact]
    public void Every_value_type_the_library_writes_is_inside_the_bound()
    {
        var extra = new OSDMap
        {
            ["undef"] = new OSD(), ["yes"] = true, ["no"] = false, ["int"] = 7, ["real"] = 1.5, ["uuid"] = OSD.FromUUID(LibreMetaverse.UUID.Random()),
            ["text"] = "text", ["date"] = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), ["uri"] = new Uri("http://example.org/a"),
            ["blob"] = new byte[] { 1, 2, 3 }, ["list"] = new OSDArray { 1, "two", new OSDMap { ["k"] = new OSDArray() } },
            ["version"] = "1.1", ["type"] = "GLTF 2.0", ["data"] = Gltf,
        };
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(OSDParser.SerializeLLSDBinary(extra)));
        Assert.Equal(Gltf, LmvMaterials.GltfOfMaterialAsset(Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(extra))));
    }

    [Fact]
    public void The_material_source_logs_a_refused_asset_once_and_treats_it_as_unavailable()
    {
        var sink = new ListSink();
        using var src = new Viewer.Shell.MaterialSource((_, _) => Task.FromResult(Viewer.Core.Assets.AssetFetchResult.Ok(BinaryWithNesting(200))),
            new Viewer.Core.Diagnostics.CoreLog(sink, "test"));
        var id = Guid.NewGuid();
        src.TryGet(id, out _);
        SpinWait.SpinUntil(() => src.TryTakeArrival(out _), TimeSpan.FromSeconds(30));
        Assert.Equal(Viewer.Shell.MeshAvailability.Unavailable, src.TryGet(id, out _));
        Assert.Equal(Viewer.Shell.MeshAvailability.Unavailable, src.TryGet(id, out _));
        Assert.Single(sink.Entries, e => e.Event == "material.refused");
    }

    // ---- fuzz: truncations and corruptions of real material assets

    [Fact]
    public void Truncations_and_corruptions_of_a_real_material_never_throw_or_allocate_what_they_declare()
    {
        foreach (var source in new[] { ValidBinary(), ValidXml() })
        {
            for (int len = 0; len < source.Length; len++)   // every proper prefix
                AssertSafe(source[..len]);

            var rng = new Random(0x5EED);
            for (int i = 0; i < 3000; i++)
            {
                var bytes = (byte[])source.Clone();
                for (int k = 0, n = 1 + rng.Next(6); k < n; k++)
                {
                    int at = rng.Next(bytes.Length);
                    switch (rng.Next(3))
                    {
                        case 0: bytes[at] ^= (byte)(1 << rng.Next(8)); break;
                        case 1: bytes[at] = (byte)rng.Next(256); break;
                        default: if (at + 4 <= bytes.Length) Be32(rng.Next(2) == 0 ? int.MaxValue : rng.Next()).CopyTo(bytes, at); break;   // a wild length
                    }
                }
                AssertSafe(bytes);
            }
        }

        static void AssertSafe(byte[] bytes)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var ex = Record.Exception(() => LmvMaterials.GltfOfMaterialAsset(bytes));
            Assert.Null(ex);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024 * 1024, "a corrupted asset must not allocate what it declares");
        }
    }
}
