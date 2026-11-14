// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using OpenSimNGC.Appearance.Baking;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Xunit;
using BakeKind = OpenSimNGC.Appearance.Baking.WearableKind;
using TextureSlot = OpenSimNGC.Appearance.Baking.TextureSlot;

namespace Viewer.Core.Tests;

/// <summary>
/// The development aid that bakes the worn outfit locally and keeps the finished bakes, without uploading them or sending an appearance: it is how a bake made
/// here is compared with the one a region's server made for the same outfit.
/// </summary>
public class BakeDryRunTests
{
    private static string Wearable(int type, string name) =>
        $"LLWearable version 22\n{name}\n\n\tpermissions 0\n\t{{\n\t}}\ntype {type}\nparameters 1\n80 0.5\ntextures 0\n\n";

    private sealed class Env : IBakeEnvironment
    {
        public readonly List<WornItem> Worn = [];
        public readonly Dictionary<Guid, string> Assets = [];
        public int Listed, Uploads;
        public Task<IReadOnlyList<WornItem>?> ListWornAsync(CancellationToken ct) { Listed++; return Task.FromResult<IReadOnlyList<WornItem>?>(Worn); }
        public Task<byte[]?> FetchWearableAsync(Guid assetId, bool bodyPart, CancellationToken ct) =>
            Task.FromResult(Assets.TryGetValue(assetId, out var t) ? (byte[]?)System.Text.Encoding.UTF8.GetBytes(t) : null);
        public Task<byte[]?> FetchTextureAsync(Guid id, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task<Guid> UploadBakeAsync(byte[] j2k, CancellationToken ct) { Uploads++; return Task.FromResult(Guid.NewGuid()); }
    }

    private sealed class Compositor : IBakeCompositor
    {
        public IReadOnlyList<string> Refusals(IReadOnlyList<ParsedWearable> worn, IReadOnlyList<BakeChannel> channels) => [];
        public IReadOnlyCollection<TextureSlot> SlotsOf(IReadOnlyList<BakeChannel> channels) => [];
        public bool IsMale(IReadOnlyList<ParsedWearable> worn) => false;
        public Task<IReadOnlyList<BakeResult>> BakeAsync(BakeRequest request, CancellationToken ct)
        {
            var fid = new FidelityReport([], [], [], []);
            IReadOnlyList<BakeResult> l = new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes }
                .Select(c => new BakeResult(c, [(byte)c, 9, 9], "hash" + c, fid)).ToList();
            return Task.FromResult(l);
        }
        public byte[] EncodeVisualParams(IEnumerable<(BakeKind Kind, IReadOnlyDictionary<int, float> Params)> worn) => [1];
    }

    private static Env Outfit()
    {
        var e = new Env();
        foreach (var (t, n) in new[] { (0, "shape"), (1, "skin"), (2, "hair"), (3, "eyes") })
        {
            var a = Guid.NewGuid();
            e.Worn.Add(new WornItem(Guid.NewGuid(), a, t));
            e.Assets[a] = Wearable(t, n);
        }
        return e;
    }

    private static readonly CoreLog Log = new(new ListSink(), "test");

    [Fact]
    public async Task Every_bake_is_kept_under_its_channel_and_none_is_uploaded()
    {
        var env = Outfit();
        var bakes = await BakeDryRun.RunAsync(env, new Compositor(), Log, CancellationToken.None);
        Assert.NotNull(bakes);
        Assert.Equal(4, bakes!.Count);
        foreach (var channel in new[] { BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes })
            Assert.Equal([(byte)channel, 9, 9], bakes[channel]);
        Assert.Equal(0, env.Uploads);
    }

    [Fact]
    public async Task An_outfit_that_cannot_be_baked_gives_nothing_and_still_uploads_nothing()
    {
        var env = Outfit();
        env.Worn.RemoveAll(w => w.WearableType == 3);   // no eyes
        var bakes = await BakeDryRun.RunAsync(env, new Compositor(), Log, CancellationToken.None);
        Assert.Equal(1, env.Listed);   // it did look at the outfit
        Assert.Null(bakes);
        Assert.Equal(0, env.Uploads);
    }

    [Fact]
    public void A_bake_is_written_as_a_picture_whose_pixels_are_the_bakes()
    {
        var planes = new RgbaPlanes(2, 1, true);
        planes.R[0] = 10; planes.G[0] = 20; planes.B[0] = 30; planes.A[0] = 255;
        planes.R[1] = 200; planes.G[1] = 150; planes.B[1] = 100; planes.A[1] = 255;
        var png = BakeDryRun.Png(planes);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        Assert.Equal([10, 20, 30, 255, 200, 150, 100, 255], Pixels(png, out int w, out int h));
        Assert.Equal((2, 1), (w, h));
    }

    // a minimal reader for the pictures BakeDryRun.Png writes: 8-bit RGBA, no interlace, filter 0 on every row
    private static byte[] Pixels(byte[] png, out int w, out int h)
    {
        w = h = 0;
        var idat = new MemoryStream();
        int at = 8;
        while (at < png.Length)
        {
            int len = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            if (type == "IHDR") { w = (png[at + 8] << 24) | (png[at + 9] << 16) | (png[at + 10] << 8) | png[at + 11]; h = (png[at + 12] << 24) | (png[at + 13] << 16) | (png[at + 14] << 8) | png[at + 15]; }
            if (type == "IDAT") idat.Write(png, at + 8, len);
            at += 12 + len;
        }
        idat.Position = 0;
        using var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        var rows = raw.ToArray();
        var pixels = new List<byte>();
        for (int y = 0; y < h; y++)
        {
            Assert.Equal(0, rows[y * (w * 4 + 1)]);
            pixels.AddRange(rows.Skip(y * (w * 4 + 1) + 1).Take(w * 4));
        }
        return [.. pixels];
    }
}
