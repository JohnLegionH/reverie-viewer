// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using System.Text.Json;

namespace Viewer.Core.DecodeProbe;

/// <summary>
/// The fuzz driver. It never decodes: it runs this program's <c>mutate</c> command as a child for a
/// batch of seeds per source and watches it. A case that runs past the deadline is a hang, a child whose working set
/// passes the cap is a memory blow-up, and a child that dies or prints CRASH is a crash; each is recorded (source, seed,
/// kind, level), its case is written to the work folder, the child is killed with its whole tree, and the next child
/// starts after that seed. Sources are taken in turn until the time is up.
///   fuzz --sources FILE|DIR[...] --seconds N --work DIR [--deadline-ms 10000] [--mem-cap-mib 1536] [--levels 0,2] [--batch 200]
/// The work folder receives only the cases found and summary.json.
/// </summary>
public static class DecodeFuzz
{
    private sealed record Hit(string Source, int Seed, string Case, string What, string Detail);

    public static int Drive(Dictionary<string, string?> o)
    {
        var sources = o["sources"]!.Split(',').SelectMany(p => Directory.Exists(p)
            ? Directory.GetFiles(p).Where(x => x.EndsWith(".j2c") || x.EndsWith(".j2k")).Order().ToArray() : new[] { p }).ToList();
        var work = o["work"]!;
        Directory.CreateDirectory(work);
        var until = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(double.Parse(o.GetValueOrDefault("seconds") ?? "60"));
        var deadline = TimeSpan.FromMilliseconds(int.Parse(o.GetValueOrDefault("deadline-ms") ?? "10000"));
        long cap = long.Parse(o.GetValueOrDefault("mem-cap-mib") ?? "1536") * 1024 * 1024;
        string levels = o.GetValueOrDefault("levels") ?? "0,2";
        int batch = int.Parse(o.GetValueOrDefault("batch") ?? "200");
        var next = new int[sources.Count];
        var hits = new List<Hit>();
        long cases = 0, decodes = 0, ok = 0, slowest = 0, peak = 0;
        string slowestCase = "";
        var self = typeof(Program).Assembly.Location;
        var host = Environment.ProcessPath ?? "dotnet";
        for (int turn = 0; until.Elapsed < budget; turn++)
        {
            int si = turn % sources.Count;
            string src = sources[si];
            int from = next[si], to = from + batch - 1;
            var psi = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { self, "mutate", "--source", src, "--seeds", $"{from}-{to}", "--levels", levels }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            string? current = null;
            var caseClock = Stopwatch.StartNew();
            int lastSeed = from - 1;
            Hit? hit = null;
            var lineTask = p.StandardOutput.ReadLineAsync();
            while (true)
            {
                if (lineTask.Wait(25))
                {
                    var line = lineTask.Result;
                    if (line is null) break;   // the child ended
                    if (line.StartsWith("START "))
                    {
                        current = line[6..];
                        caseClock.Restart();
                        int s = int.Parse(current.Split(':')[0]);
                        if (s != lastSeed) { cases++; lastSeed = s; }
                    }
                    else if (line.StartsWith("RESULT "))
                    {
                        decodes++;
                        var f = line.Split(' ');
                        if (f[3] == "1") ok++;
                        long ms = long.Parse(f[4]);
                        if (ms > slowest) { slowest = ms; slowestCase = $"{Path.GetFileName(src)} {f[1]} {f[2]}"; }
                        current = null;
                    }
                    else if (line.StartsWith("CRASH "))
                    {
                        hits.Add(new(src, lastSeed, line[6..], "crash", "exception escaped the decoder"));
                        current = null;
                    }
                    lineTask = p.StandardOutput.ReadLineAsync();
                }
                if (until.Elapsed > budget + deadline) { Kill(p); break; }
                try
                {
                    p.Refresh();
                    peak = Math.Max(peak, p.WorkingSet64);
                    if (p.WorkingSet64 > cap) { hit = new(src, lastSeed, current ?? "", "memory", $"{p.WorkingSet64 / (1024 * 1024)} MiB"); Kill(p); break; }
                }
                catch (InvalidOperationException) { }
                if (current is not null && caseClock.Elapsed > deadline) { hit = new(src, lastSeed, current, "hang", $"over {deadline.TotalMilliseconds} ms"); Kill(p); break; }
            }
            p.WaitForExit();
            if (hit is null && p.ExitCode != 0 && until.Elapsed <= budget + deadline)
                hit = new(src, lastSeed, current ?? "", "crash", $"child exited {p.ExitCode}");
            if (hit is not null)
            {
                hits.Add(hit);
                var file = Path.Combine(work, $"{hit.What}-{Path.GetFileNameWithoutExtension(src)}-seed{hit.Seed}.j2c");
                File.WriteAllBytes(file, Mutator.Make(File.ReadAllBytes(src), hit.Seed, out _));
                Console.Out.WriteLine($"HIT {hit.What} {Path.GetFileName(src)} seed {hit.Seed} {hit.Case} {hit.Detail}");
                next[si] = hit.Seed + 1;
            }
            else next[si] = Math.Max(to + 1, lastSeed + 1);
        }
        var summary = new
        {
            seconds = Math.Round(until.Elapsed.TotalSeconds, 1), sources = sources.Select(Path.GetFileName), cases, decodes, decoded_ok = ok,
            refused_or_failed = decodes - ok, slowest_ms = slowest, slowest_case = slowestCase, child_peak_working_set_mib = peak / (1024 * 1024),
            deadline_ms = deadline.TotalMilliseconds, mem_cap_mib = cap / (1024 * 1024), levels,
            hangs = hits.Count(h => h.What == "hang"), crashes = hits.Count(h => h.What == "crash"), memory = hits.Count(h => h.What == "memory"),
            hits = hits.Select(h => new { source = Path.GetFileName(h.Source), h.Seed, h.Case, h.What, h.Detail }),
        };
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(work, "summary.json"), json);
        Console.Out.WriteLine(json);
        return 0;
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        p.WaitForExit();
    }
}
