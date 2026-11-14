// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Security.Cryptography;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;

namespace Viewer.Core.DecodeProbe;

/// <summary>
/// The many-layer checks, each in a fresh process (so no earlier decode in the same process colours
/// the result).
///   repeat --file F|--own lmv-1024 [--times 5] [--clear-pool]
///                                               decodes F at every reduced level N times in a row, then 8 at once;
///                                               prints the mean absolute difference from the first decode per level
///                                               (REPEAT) and how far the first is from level 0 scaled down (CLOSE)
///   leak --ordinary F|DIR[...] --many F[...] [--clear-pool]
///                                               every level of every ordinary codestream decoded alone first (the
///                                               baseline), then straight after a many-layer decode on the same thread,
///                                               then while many-layer decodes run on 4 other threads; prints how many
///                                               differ from the baseline byte for byte
/// --clear-pool sets CoreJ2K's InvWTFull.ClearArrayPoolBuffersOnReturn (its reconstruction buffers come from the shared
/// ArrayPool and are returned uncleared by default): the cause experiment.
/// </summary>
public static class LayerChecks
{
    private static TextureDecodeResult At(byte[] b, int level) => level == 0 ? LmvTexture.Decode(b) : LmvTexture.DecodeLevel(b, level);

    private static double Mad(byte[] a, byte[] b) => a.Length != b.Length ? 255 : a.Zip(b, (x, y) => Math.Abs(x - y)).Average();

    private static int Levels(byte[] b) => J2kGuard.Validate(b, null, out var h) is null ? TextureLevels.Count(h!.Width, h.Height, h.DecompositionLevels) : 0;

    private static void ClearPool(Dictionary<string, string?> o)
    {
        if (o.ContainsKey("clear-pool")) CoreJ2K.j2k.wavelet.synthesis.InvWTFull.ClearArrayPoolBuffersOnReturn = true;
        Console.Out.WriteLine($"clear-pool {CoreJ2K.j2k.wavelet.synthesis.InvWTFull.ClearArrayPoolBuffersOnReturn}");
    }

    private static List<string> Files(string list) => list.Split(',').SelectMany(p => Directory.Exists(p)
        ? Directory.GetFiles(p).Where(x => x.EndsWith(".j2c") || x.EndsWith(".j2k")).Order().ToArray() : new[] { p }).ToList();

    /// <summary>Our own many-layer codestreams made in this process (no file): "lmv-SIZE" is
    /// LibreMetaverse's encoder (27 layers), "lossless-SIZE" CoreJ2K's lossless one (32 layers).</summary>
    public static (string Name, byte[] Bytes) OwnMany(string spec)
    {
        var p = spec.Split('-');
        int size = int.Parse(p[1]);
        return (spec, p[0] switch
        {
            "lmv" => LmvTexture.Encode(size, size, Own.Detailed(size, 3), withAlpha: false),
            "lossless" => LmvTexture.EncodeLossless(size, size, Own.Detailed(size, 3)),
            _ => throw new ArgumentException("unknown own codestream " + spec),
        });
    }

    /// <summary>How far a reduced level is from level 0 downscaled to its size: the same measure as the decode tests'
    /// (TextureLevelDecodeTests.Close): a JPEG 2000 low-pass sample x at level n sits at source position 2^n x, so the reference
    /// is a box of 2^n + 1 texels centred there. Mean absolute difference per channel (0-255).</summary>
    public static double Close(DecodedTexture level, DecodedTexture l0, int n)
    {
        int f = 1 << n, half = f / 2;
        double sum = 0;
        for (int y = 0; y < level.Height; y++)
            for (int x = 0; x < level.Width; x++)
                for (int c = 0; c < 4; c++)
                {
                    int acc = 0, k = 0;
                    for (int yy = Math.Max(0, y * f - half); yy <= Math.Min(l0.Height - 1, y * f + half); yy++)
                        for (int xx = Math.Max(0, x * f - half); xx <= Math.Min(l0.Width - 1, x * f + half); xx++) { acc += l0.Rgba[(yy * l0.Width + xx) * 4 + c]; k++; }
                    sum += Math.Abs(level.Rgba[(y * level.Width + x) * 4 + c] - (double)acc / k);
                }
        return sum / (level.Width * level.Height * 4);
    }

    public static int Repeat(Dictionary<string, string?> o)
    {
        ClearPool(o);
        var (name, b) = o.GetValueOrDefault("own") is { } own ? OwnMany(own) : (Path.GetFileName(o["file"]!), File.ReadAllBytes(o["file"]!));
        J2kGuard.Validate(b, null, out var h);
        int times = int.Parse(o.GetValueOrDefault("times") ?? "5");
        Console.Out.WriteLine($"file {name} {h!.Width}x{h.Height} layers {h.Layers} levels {Levels(b)}");
        var l0 = At(b, 0).Texture!;
        for (int level = 1; level < Levels(b); level++)
        {
            var firstT = At(b, level).Texture!;
            var first = firstT.Rgba;
            Console.Out.WriteLine($"CLOSE L{level} {Close(firstT, l0, level):F2}");
            var seq = Enumerable.Range(1, times - 1).Select(_ => Mad(first, At(b, level).Texture!.Rgba)).ToList();
            var par = new double[8];
            Parallel.For(0, 8, i => par[i] = Mad(first, At(b, level).Texture!.Rgba));
            Console.Out.WriteLine($"REPEAT L{level} sequential max {seq.Max():F2} (differ {seq.Count(x => x > 0)} of {seq.Count}) parallel max {par.Max():F2} (differ {par.Count(x => x > 0)} of 8)");
        }
        return 0;
    }

    public static int Leak(Dictionary<string, string?> o)
    {
        ClearPool(o);
        // "own" in either list: our own codestreams generated here (Own.Sources; the 27- and 32-layer ones for --many)
        var ordinary = o["ordinary"]!.Split(',').SelectMany(p => p == "own" ? Own.Sources()
            : Files(p).Select(f => (Path.GetFileName(f), File.ReadAllBytes(f)))).Select(x => (Name: x.Item1, Bytes: x.Item2))
            .Where(x => J2kGuard.Validate(x.Bytes, null, out var h) is not null || h!.Layers <= TextureByteRange.MaxLayers).ToList();   // ordinary: 8 layers or fewer
        var many = o["many"]!.Split(',').SelectMany(p => p == "own"
            ? [LmvTexture.Encode(1024, 1024, Own.Detailed(1024, 3), withAlpha: false), LmvTexture.EncodeLossless(1024, 1024, Own.Detailed(1024, 3))]
            : Files(p).Select(File.ReadAllBytes)).ToList();
        var cases = ordinary.SelectMany(f => Enumerable.Range(0, Math.Max(1, Levels(f.Bytes))).Select(l => (f.Name, f.Bytes, Level: l))).ToList();
        string Hash(byte[] b, int l) => At(b, l) is { Ok: true } r ? Convert.ToHexString(SHA256.HashData(r.Texture!.Rgba)) : "refused";
        var phase = System.Diagnostics.Stopwatch.StartNew();
        // 1. alone, before any many-layer decode in this process, one at a time
        var caseMs = new double[cases.Count];
        var baseline = cases.Select((c, i) => { var s = System.Diagnostics.Stopwatch.StartNew(); var h = Hash(c.Bytes, c.Level); caseMs[i] = s.Elapsed.TotalMilliseconds; return h; }).ToList();
        double p1 = phase.Elapsed.TotalSeconds; phase.Restart();
        // 2. straight after a many-layer reduced decode, on the same thread. Each case is one pair (the many-layer decode,
        // then the case, inside one loop body, so on one thread); the pairs run on every core at once. The many-layer
        // decodes are most of this check's time (a full 1024 x 1024 decode each), and a pair needs nothing from another.
        int after = 0;
        long manyTicks = 0;
        var cores = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.For(0, cases.Count, cores, k =>
        {
            var m = many[k % many.Count];
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            At(m, 1 + k % Math.Max(1, Levels(m) - 1));
            Interlocked.Add(ref manyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            if (Hash(cases[k].Bytes, cases[k].Level) != baseline[k]) { Interlocked.Increment(ref after); Console.Out.WriteLine($"DIFF after {cases[k].Name} L{cases[k].Level}"); }
        });
        double p2 = phase.Elapsed.TotalSeconds; phase.Restart();
        // 3. while many-layer reduced decodes run on four other threads (threads of their own, each running before the
        // first case is checked); the cases are checked on the remaining cores
        int during = 0;
        long noiseDecodes = 0;
        using var stop = new CancellationTokenSource();
        using var running = new CountdownEvent(4);
        var noise = Enumerable.Range(0, 4).Select(t => Task.Factory.StartNew(() =>
        {
            int i = t;
            running.Signal();
            while (!stop.IsCancellationRequested) { var m = many[i % many.Count]; At(m, 1 + i % Math.Max(1, Levels(m) - 1)); i++; Interlocked.Increment(ref noiseDecodes); }
        }, TaskCreationOptions.LongRunning)).ToArray();
        running.Wait();
        Parallel.For(0, cases.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - noise.Length) }, i =>
        {
            if (Hash(cases[i].Bytes, cases[i].Level) != baseline[i]) { Interlocked.Increment(ref during); Console.Out.WriteLine($"DIFF during {cases[i].Name} L{cases[i].Level}"); }
        });
        stop.Cancel();
        Task.WaitAll(noise);
        double p3 = phase.Elapsed.TotalSeconds;
        Console.Out.WriteLine($"TIME baseline {p1:F1} s after {p2:F1} s (many-layer decodes {manyTicks / (double)System.Diagnostics.Stopwatch.Frequency:F1} s over {cores.MaxDegreeOfParallelism} threads) during {p3:F1} s (many-layer decodes beside it {noiseDecodes})");
        foreach (var g in cases.Select((c, i) => (c.Level, Ms: caseMs[i])).GroupBy(x => x.Level).OrderBy(g => g.Key))
            Console.Out.WriteLine($"TIME L{g.Key} cases {g.Count()} baseline {g.Sum(x => x.Ms) / 1000:F2} s");
        foreach (var f in cases.Select((c, i) => (c.Name, c.Bytes, Ms: caseMs[i])).GroupBy(x => x.Name).OrderByDescending(g => g.Sum(x => x.Ms)).Take(5))
        {
            J2kGuard.Validate(f.First().Bytes, null, out var fh);
            Console.Out.WriteLine($"TIME file {f.Key} {fh?.Width}x{fh?.Height} layers {fh?.Layers} levels {f.Count()} baseline {f.Sum(x => x.Ms):F0} ms");
        }
        Console.Out.WriteLine($"LEAK files {ordinary.Count} level-decodes {cases.Count} refused {baseline.Count(x => x == "refused")} differ-after {after} differ-during {during}");
        return 0;
    }

    /// <summary>The cost: level 0 (the decode), each level through <see cref="LmvTexture.DecodeLevel"/>, and CoreJ2K's
    /// reduced decode called directly (what DecodeLevel wraps, for comparison). The downscale column is the cost of
    /// <see cref="LevelDownscale"/> alone, which a codestream above 8 layers used to need after a level 0 decode. Median of N after a warm-up.
    /// levelcost --file F | --own lmv-2048 [--times 3]</summary>
    public static int LevelCost(Dictionary<string, string?> o)
    {
        var (name, b) = o.GetValueOrDefault("own") is { } own ? OwnMany(own) : (Path.GetFileName(o["file"]!), File.ReadAllBytes(o["file"]!));
        int times = int.Parse(o.GetValueOrDefault("times") ?? "3");
        double Median(Func<double> f) { f(); var x = Enumerable.Range(0, times).Select(_ => f()).Order().ToList(); return x[x.Count / 2]; }
        double Ms(Action a) { var sw = System.Diagnostics.Stopwatch.StartNew(); a(); return sw.Elapsed.TotalMilliseconds; }
        double l0 = Median(() => Ms(() => LmvTexture.Decode(b)));
        J2kGuard.Validate(b, null, out var h);
        Console.Out.WriteLine($"COST {name} {h!.Width}x{h.Height} layers {h.Layers}: L0 {l0:F0} ms");
        for (int level = 1; level < Levels(b); level++)
        {
            int lv = level;
            double viewer = Median(() => Ms(() => LmvTexture.DecodeLevel(b, lv)));
            double reduced = Median(() => Ms(() =>
            {
                using var img = CoreJ2K.J2kImage.FromBytes(b, new CoreJ2K.Configuration.J2KDecoderConfiguration().WithResolutionLevel(h.DecompositionLevels - lv));
            }));
            double down = Median(() => Ms(() => LevelDownscale.ToLevel(h.Width, h.Height, new byte[h.Width * h.Height * 4], lv, out _, out _)));
            Console.Out.WriteLine($"COST L{level}: viewer {viewer:F0} ms (downscale alone {down:F1} ms); reduced decode {reduced:F0} ms ({viewer / reduced:F1}x)");
        }
        return 0;
    }
}
