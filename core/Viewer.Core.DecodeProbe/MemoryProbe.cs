// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;

namespace Viewer.Core.DecodeProbe;

/// <summary>
/// The memory a texture's level decode costs on the viewer's GPU path (a discard level with its
/// mipmap chain, <see cref="LmvTexture.DecodeLevel"/> with mipmaps), in this process, one decode at a time:
///   memory --dir DIR [--repeats R] [--bakes N] [--release]
/// DIR: codestreams (*.j2k, *.j2c), each decoded at every level it has. --bakes: N of our own bake-shaped codestreams
/// (1024x1024, 5 components, CoreJ2K's default quality layers, more than 8), decoded at levels 1-5. --release: after each decode the level's buffers are handed back as the
/// viewer's texture source does once the engine image exists.
/// For each decode: the time, and the managed bytes allocated on this thread, split into the decoder's own (CoreJ2K's
/// decode of the same level, measured alone) and the rest (ours). Lines:
///   LEVEL kind WxH layers L ms alloc_kib decoder_kib ours_kib level_rgba_kib
///   SUMMARY kind decodes N ms p50 p95 ours_kib_per_level p50 p95 max ours_over_rgba p50 max peak_ws_mib peak_private_mib
/// </summary>
public static class MemoryProbe
{
    public static int Run(Dictionary<string, string?> o)
    {
        int repeats = int.Parse(o.GetValueOrDefault("repeats") ?? "3");
        bool release = o.ContainsKey("release");
        var sets = new List<(string Kind, List<byte[]> Codestreams, int FirstLevel)>();
        if (o.GetValueOrDefault("dir") is { } dir)
            sets.Add(("texture", Directory.GetFiles(dir).Where(f => f.EndsWith(".j2k") || f.EndsWith(".j2c")).Order().Select(File.ReadAllBytes).ToList(), 0));
        if (int.TryParse(o.GetValueOrDefault("bakes"), out int bakes) && bakes > 0)
            sets.Add(("bake", Enumerable.Range(0, bakes).Select(Bake).ToList(), 1));
        foreach (var (kind, list, firstLevel) in sets)
        {
            var ms = new List<double>();
            var ours = new List<double>();
            var ratio = new List<double>();
            foreach (var b in list)
            {
                if (J2kGuard.Validate(b, null, out var h) is not null) continue;
                int count = h!.MaxBitDepth > 8 ? 1 : TextureLevels.Count(h.Width, h.Height, h.DecompositionLevels);
                for (int level = firstLevel; level < count; level++)
                {
                    int lv = level;
                    long decoder = Allocated(() => DecoderAlone(b, h, lv));
                    for (int r = 0; r < repeats; r++)
                    {
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        var sw = Stopwatch.StartNew();
                        var d = LmvTexture.DecodeLevel(b, lv, mipmaps: true);
                        sw.Stop();
                        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                        if (!d.Ok) { Console.Out.WriteLine($"REFUSED {kind} L{lv} {d.Refusal}"); break; }
                        var t = d.Texture!;
                        long rgba = (long)t.Width * t.Height * 4;
                        if (release) Release(t);
                        if (r == 0) continue;   // the first decode of each level warms the decoder and the pool
                        ms.Add(sw.Elapsed.TotalMilliseconds);
                        ours.Add((alloc - decoder) / 1024.0);
                        ratio.Add((alloc - decoder) / (double)rgba);
                        Console.Out.WriteLine($"LEVEL {kind} {h.Width}x{h.Height} layers {h.Layers} L{lv} {sw.Elapsed.TotalMilliseconds:F1} {alloc / 1024} {decoder / 1024} {(alloc - decoder) / 1024} {rgba / 1024}");
                    }
                }
            }
            using var self = Process.GetCurrentProcess();
            Console.Out.WriteLine($"SUMMARY {kind} decodes {ms.Count} ms p50 {P(ms, 0.5):F1} p95 {P(ms, 0.95):F1} ours_kib_per_level p50 {P(ours, 0.5):F0} p95 {P(ours, 0.95):F0} max {ours.DefaultIfEmpty().Max():F0} ours_over_rgba p50 {P(ratio, 0.5):F2} max {ratio.DefaultIfEmpty().Max():F2} peak_ws_mib {self.PeakWorkingSet64 / 1048576.0:F0} peak_private_mib {self.PeakPagedMemorySize64 / 1048576.0:F0}");
        }
        return 0;
    }

    /// <summary>CoreJ2K's own decode of the level (what the decoder allocates whatever we do with its output): the reduced
    /// decode for a level above 0, the full decode at level 0, with its component planes read out as ours are.</summary>
    private static void DecoderAlone(byte[] b, J2kHeader h, int level)
    {
        if (h.Components <= 4 && level == 0)
        {
            // level 0 with 4 components or fewer goes through LibreMetaverse's decode (its planes are part of it)
            var t = new LibreMetaverse.Assets.AssetTexture(LibreMetaverse.UUID.Zero, b);
            t.Decode();
            return;
        }
        var cfg = new CoreJ2K.Configuration.J2KDecoderConfiguration();
        bool reduced = level > 0;
        if (reduced) cfg = cfg.WithResolutionLevel(h.DecompositionLevels - level);
        if (!reduced && h.Components <= 4)
        {
            var t = new LibreMetaverse.Assets.AssetTexture(LibreMetaverse.UUID.Zero, b);
            t.Decode();
            return;
        }
        using var img = CoreJ2K.J2kImage.FromBytes(b, cfg);
        for (int c = 0; c < Math.Min(img.NumberOfComponents, 4); c++) img.GetComponentBytes(c);
    }

    /// <summary>What the viewer's texture source does with a level once its engine image exists.</summary>
    private static void Release(DecodedTexture t) => t.ReleasePixels();

    private static long Allocated(Action a)
    {
        a();   // warm
        long before = GC.GetAllocatedBytesForCurrentThread();
        a();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static byte[] Bake(int seed)
    {
        var d = Own.Detailed(1024, seed + 11);
        return LmvTexture.EncodePlanes(1024, 1024, Enumerable.Range(0, 5).Select(c => Enumerable.Range(0, 1024 * 1024).Select(i => c == 4 ? (byte)255 : d[i * 4 + Math.Min(c, 3)]).ToArray()).ToList());
    }

    private static double P(List<double> v, double q)
    {
        if (v.Count == 0) return 0;
        var s = v.Order().ToList();
        return s[Math.Min(s.Count - 1, (int)Math.Floor(q * (s.Count - 1) + 0.5))];
    }
}
