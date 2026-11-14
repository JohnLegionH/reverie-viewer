// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using System.Text.Json;
using Viewer.Core.Protocol;

namespace Viewer.Core.DecodeProbe;

/// <summary>
/// Decodes texture codestreams that may be hostile, in this process only, and reports each case on
/// its own line, so the parent can tell which case overran and kill this process.
///   decode FILE|DIR... [--levels 0,max]          every file (a folder: its *.j2c and *.j2k) at every level
///   mutate --source FILE --seeds A-B [--levels]  seeded mutations of one codestream (<see cref="Mutator"/>)
///   bitflips --seeds A-B                         the seeded bit flips of its two 64x64 textures (TextureFuzzTests)
///   write-case --source FILE --seed S --out FILE a mutation written to a file, not decoded
///   own --out DIR                                our own source codestreams for fuzzing (generated here)
///   own-many --out DIR                           our own many-layer codestreams (27 and 32 layers)
///   memory --dir DIR [--bakes N] [--release]: the memory of level decodes (<see cref="MemoryProbe"/>)
///   repeat..., leak...                         the many-layer checks (<see cref="LayerChecks"/>)
///   fuzz...                                     the driver (<see cref="DecodeFuzz"/>): runs this program as children
///   material FILE...                            each file through LmvMaterials.GltfOfMaterialAsset; "MATERIAL name 1|0 length", or "CRASH name Type"
/// Lines: "START name Llevel", then "RESULT name Llevel ok ms alloc_mib why", or "CRASH name Llevel ExceptionType" when an
/// exception escapes the decoder. Level "max" is the coarsest the codestream has.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: decode|mutate|bitflips|write-case|own|fuzz ..."); return 2; }
        var opts = Options(args.Skip(1));
        var levels = (opts.GetValueOrDefault("levels") ?? "0,max").Split(',').Select(l => l == "max" ? int.MaxValue : int.Parse(l)).ToArray();
        switch (args[0])
        {
            case "decode":
                foreach (var path in args.Skip(1).TakeWhile(a => !a.StartsWith("--")))
                    foreach (var f in Directory.Exists(path) ? Directory.GetFiles(path).Where(x => x.EndsWith(".j2c") || x.EndsWith(".j2k")).Order().ToArray() : new[] { path })
                        foreach (int l in levels) Run(Path.GetFileName(f), File.ReadAllBytes(f), l);
                return 0;
            case "mutate":
            {
                var src = File.ReadAllBytes(opts["source"]!);
                var (a, b) = Range(opts["seeds"]!);
                for (int s = a; s <= b; s++)
                {
                    var bytes = Mutator.Make(src, s, out var kind);
                    foreach (int l in levels) Run($"{s}:{kind}", bytes, l);
                }
                return 0;
            }
            case "bitflips":
            {
                var sources = new[] { Own.Opaque64(), Own.Blended64() };
                var (a, b) = Range(opts["seeds"]!);
                for (int seed = a; seed <= b; seed++)
                {
                    var rng = new Random(seed);   // the same flips as the in-process test
                    var bytes = (byte[])sources[seed % 2].Clone();
                    for (int k = 0, n = 1 + rng.Next(6); k < n; k++) bytes[rng.Next(bytes.Length)] ^= (byte)(1 << rng.Next(8));
                    Run($"flip{seed}", bytes, 0);
                }
                return 0;
            }
            case "write-case":
                File.WriteAllBytes(opts["out"]!, Mutator.Make(File.ReadAllBytes(opts["source"]!), int.Parse(opts["seed"]!), out _));
                return 0;
            case "own":
                Directory.CreateDirectory(opts["out"]!);
                foreach (var (name, bytes) in Own.Sources()) File.WriteAllBytes(Path.Combine(opts["out"]!, name + ".j2c"), bytes);
                return 0;
            case "material":
                foreach (var path in args.Skip(1).TakeWhile(a => !a.StartsWith("--")))
                {
                    string name = Path.GetFileName(path);
                    Console.Out.WriteLine("START " + name);
                    Console.Out.Flush();
                    try
                    {
                        var gltf = LmvMaterials.GltfOfMaterialAsset(File.ReadAllBytes(path));
                        Console.Out.WriteLine($"MATERIAL {name} {(gltf is null ? 0 : 1)} {gltf?.Length ?? 0}");
                    }
                    catch (Exception e) { Console.Out.WriteLine($"CRASH {name} {e.GetType().Name}"); }
                    Console.Out.Flush();
                }
                return 0;
            case "dump":
                // the decoded RGBA of each file at full resolution, as <out>/<name>.rgba (width and height in the name's sidecar line on stdout)
                Directory.CreateDirectory(opts["out"]!);
                foreach (var path in args.Skip(1).TakeWhile(a => !a.StartsWith("--")))
                {
                    var r = LmvTexture.Decode(File.ReadAllBytes(path));
                    if (!r.Ok) { Console.Out.WriteLine($"DUMP {Path.GetFileName(path)} refused"); continue; }
                    File.WriteAllBytes(Path.Combine(opts["out"]!, Path.GetFileName(path) + ".rgba"), r.Texture!.Rgba.AsSpan(0, r.Texture.Width * r.Texture.Height * 4).ToArray());
                    Console.Out.WriteLine($"DUMP {Path.GetFileName(path)} {r.Texture.Width}x{r.Texture.Height}");
                }
                return 0;
            case "rawdecode":
                // CoreJ2K alone, with our guard bypassed: does the decoder itself return, refuse or hang on this codestream?
                foreach (var path in args.Skip(1).TakeWhile(a => !a.StartsWith("--")))
                {
                    string name = Path.GetFileName(path);
                    Console.Out.WriteLine("START " + name);
                    Console.Out.Flush();
                    try
                    {
                        using var img = CoreJ2K.J2kImage.FromBytes(File.ReadAllBytes(path));
                        Console.Out.WriteLine($"RAW {name} returned {img.Width}x{img.Height}");
                    }
                    catch (Exception e) { Console.Out.WriteLine($"RAW {name} threw {e.GetType().Name}"); }
                    Console.Out.Flush();
                }
                return 0;
            case "fuzz":
                return DecodeFuzz.Drive(opts);
            case "repeat":
                return LayerChecks.Repeat(opts);
            case "levelcost":
                return LayerChecks.LevelCost(opts);
            case "memory":
                return MemoryProbe.Run(opts);
            case "leak":
                return LayerChecks.Leak(opts);
            case "own-large":
                // our own 2048x2048 codestreams (the deadline measurement): 5 layers, and LibreMetaverse's encoder
                Directory.CreateDirectory(opts["out"]!);
                File.WriteAllBytes(Path.Combine(opts["out"]!, "own-2048-5layers.j2c"), LmvTexture.EncodeLayered(2048, 2048, Own.Detailed(2048, 4), "0.0625 0.25 0.5 1.0 2.0", 2.0));
                File.WriteAllBytes(Path.Combine(opts["out"]!, "own-2048-lmv.j2c"), LmvTexture.Encode(2048, 2048, Own.Detailed(2048, 4), withAlpha: false));
                return 0;
            case "own-many":
                // many-layer codestreams of our own content: LibreMetaverse's encoder (27 layers) and CoreJ2K's lossless (32)
                Directory.CreateDirectory(opts["out"]!);
                File.WriteAllBytes(Path.Combine(opts["out"]!, "own-1024-lmv.j2c"), LmvTexture.Encode(1024, 1024, Own.Detailed(1024, 3), withAlpha: false));
                File.WriteAllBytes(Path.Combine(opts["out"]!, "own-1024-lossless.j2c"), LmvTexture.EncodeLossless(1024, 1024, Own.Detailed(1024, 3)));
                return 0;
            default:
                Console.Error.WriteLine("unknown command " + args[0]);
                return 2;
        }
    }

    private static void Run(string name, byte[] bytes, int level)
    {
        string tag = $"{name} L{(level == int.MaxValue ? "max" : level)}";
        Console.Out.WriteLine("START " + tag);
        Console.Out.Flush();
        long before = GC.GetAllocatedBytesForCurrentThread();
        // Processor time as well as wall time: a decoder that loops burns processor time, while a wait for the machine
        // (another process holding the cores) adds only wall time.
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        TextureDecodeResult r;
        try
        {
            r = level == 0 ? LmvTexture.Decode(bytes) : LmvTexture.DecodeLevel(bytes, level);
        }
        catch (Exception e)
        {
            Console.Out.WriteLine($"CRASH {tag} {e.GetType().Name}");
            Console.Out.Flush();
            return;
        }
        sw.Stop();
        long cpuMs = (long)(Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        long mib = (GC.GetAllocatedBytesForCurrentThread() - before) / (1024 * 1024);
        Console.Out.WriteLine($"RESULT {tag} {(r.Ok ? 1 : 0)} {sw.ElapsedMilliseconds} {mib} {cpuMs} {JsonSerializer.Serialize(r.Refusal ?? "")}");
        Console.Out.Flush();
    }

    internal static Dictionary<string, string?> Options(IEnumerable<string> args)
    {
        var o = new Dictionary<string, string?>();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
            if (list[i].StartsWith("--")) o[list[i][2..]] = i + 1 < list.Count && !list[i + 1].StartsWith("--") ? list[++i] : "";
        return o;
    }

    internal static (int, int) Range(string s)
    {
        var p = s.Split('-');
        return (int.Parse(p[0]), int.Parse(p.Length > 1 ? p[1] : p[0]));
    }
}
