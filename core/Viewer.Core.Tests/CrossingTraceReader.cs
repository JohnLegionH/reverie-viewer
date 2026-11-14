// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Viewer.Core.Tests;

/// <summary>
/// Reads one crossing-frames trace (the development trace of the own avatar around a region crossing, one line per frame) into the replay
/// form <see cref="CrossingReplayTests"/> reads: R lines for the two regions (metres, the region left at 0 0), S lines for the statements
/// and F lines for the frames, times in seconds from the crossing message.
/// <list type="bullet">
/// <item>A statement is taken in the frame in which the avatar's update count rises, with the place and velocity that frame shows. Its event
/// is usually logged in the same frame, but one seen at the end of a frame is taken by the scene in the next, and the frame of the event
/// then still shows the statement before it.</item>
/// <item>A frame is read at the time its body was placed: the driving entry's motion time (now=), one time per frame. The trace line's own
/// time (t=) is read later in the frame, by up to a few tens of milliseconds, which at flying speed is tenths of a metre.</item>
/// <item>A statement belongs to the region whose avatar event was seen last, up to and including its own frame.</item>
/// </list>
/// Names, ids, grid places and the clock's origin are not carried into the output.
/// </summary>
internal static partial class CrossingTraceReader
{
    [GeneratedRegex(@"\[(\S+) grid=(-?\d*),(-?\d*) off=(\S+) td=([0-9.]+)([^\]]*)\]")]
    private static partial Regex EntryPattern();

    [GeneratedRegex(@"(\w+)=(\S+)")]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"([0-9.]+) (\S+) (?:AvatarMoved|AvatarAdded|AvatarChanged) local=(\d+)")]
    private static partial Regex EventPattern();

    [GeneratedRegex(@"([0-9.]+) region Crossed")]
    private static partial Regex CrossedPattern();

    private sealed record Region(Vector3 Offset, float TimeDilation);

    /// <summary>One motion's update count and last statement, from the region it was read in.</summary>
    private sealed record Count(string Region, bool Shared, long Updates, Vector3 Stated, Vector3 Velocity);

    private sealed record Record(double Time, double? PlacedAt, string Current, Dictionary<string, Region> Regions, string Events, Vector3? Body, Vector3? Anchor,
        Dictionary<string, Count> Counts);

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static Vector3 V(string s)
    {
        var p = s.Split(',');
        return new Vector3((float)D(p[0]), (float)D(p[1]), p.Length > 2 ? (float)D(p[2]) : 0);
    }

    private static Record Parse(string line)
    {
        int bar = line.IndexOf(" | events: ", StringComparison.Ordinal);
        string head = bar < 0 ? line : line[..bar];
        string events = bar < 0 ? "" : line[(bar + " | events: ".Length)..];
        int bracket = head.IndexOf(" [", StringComparison.Ordinal);
        var top = FieldPattern().Matches(bracket < 0 ? head : head[..bracket]).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var regions = new Dictionary<string, Region>();
        Vector3? body = null;
        double? placedAt = null;
        var counts = new Dictionary<string, Count>();
        foreach (Match m in EntryPattern().Matches(head))
        {
            regions[m.Groups[1].Value] = new Region(V(m.Groups[4].Value), (float)D(m.Groups[5].Value));
            foreach (var chunk in m.Groups[6].Value.Split(" entry=").Skip(1))
            {
                var e = FieldPattern().Matches("entry=" + chunk).ToDictionary(f => f.Groups[1].Value, f => f.Groups[2].Value);
                if (e.GetValueOrDefault("drives") == "1")
                {
                    if (body is null && e.TryGetValue("drawn", out var drawn)) body = V(drawn);
                    if (placedAt is null && e.TryGetValue("now", out var now)) placedAt = D(now);
                }
                if (e.TryGetValue("upd", out var u) && e.TryGetValue("stated", out var st))
                {
                    // a motion shared by the regions' scenes is one count, whichever region it is read in; otherwise each region's entry counts its own
                    bool shared = e.GetValueOrDefault("shared") == "1" && e.ContainsKey("motion");
                    string key = shared ? "motion " + e["motion"] : m.Groups[1].Value + " " + e["entry"];
                    var c = new Count(m.Groups[1].Value, shared, long.Parse(u, CultureInfo.InvariantCulture), V(st), e.TryGetValue("vel", out var vel) ? V(vel) : Vector3.Zero);
                    if (!counts.TryGetValue(key, out var seen) || c.Updates > seen.Updates) counts[key] = c;
                }
            }
        }
        return new Record(D(top["t"]), placedAt, top.GetValueOrDefault("cur", ""), regions, events, body,
            top.TryGetValue("anchor", out var a) ? V(a) : null, counts);
    }

    /// <summary>The crossing's R, S and F lines, or null when the trace has no crossing message or no statement from one side.
    /// <paramref name="drawn"/> adds the frame's drawn body and camera anchor to each F line.</summary>
    public static List<string>? Read(IEnumerable<string> lines, bool drawn = false)
    {
        var recs = lines.Where(l => l.StartsWith("frame=", StringComparison.Ordinal)).Select(Parse).ToList();
        // the motion's time, when the motion runs on the trace's clock (one time per frame). An older build timed each region's motion on a clock
        // started with the session, and a motion's time there is its own last reading, not the frame's: such a trace is read on its lines' times.
        var offsets = recs.Where(r => r.PlacedAt is not null).Select(r => r.Time - r.PlacedAt!.Value).Order().ToList();
        if (offsets.Count > 0 && Math.Abs(offsets[offsets.Count / 2]) < 1.0)
            recs = recs.Select(r => r with { Time = r.PlacedAt ?? r.Time }).ToList();
        int ci = recs.FindIndex(r => r.Events.Contains("Crossed", StringComparison.Ordinal));
        if (ci < 0) return null;
        double crossedAt = D(CrossedPattern().Match(recs[ci].Events).Groups[1].Value);
        static string Bracket(string name, Record r) => r.Regions.ContainsKey(name) && name != "anchor" ? name : "anchor";
        string left = Bracket(recs[Math.Max(ci - 1, 0)].Current, recs[Math.Max(ci - 1, 0)]);
        string entered = Bracket(recs[^1].Current, recs[^1]);
        if (left == entered) return null;
        var places = new Dictionary<string, Vector3>();
        foreach (var r in recs)
            foreach (var (name, reg) in r.Regions)
                places.TryAdd(name, reg.Offset);
        if (!places.TryGetValue(left, out var origin) || !places.TryGetValue(entered, out var other)) return null;
        var index = new Dictionary<string, int> { [left] = 0, [entered] = 1 };
        float ox = other.X - origin.X, oy = other.Y - origin.Y, width = Math.Max(Math.Abs(ox), Math.Abs(oy));
        var I = CultureInfo.InvariantCulture;
        var output = new List<string> { string.Create(I, $"R 0 0 0 {width:0}"), string.Create(I, $"R 1 {ox:0} {oy:0} {width:0}") };
        Dictionary<string, long>? counted = null;
        string eventRegion = left;
        var sides = new HashSet<int>();
        foreach (var r in recs)
        {
            foreach (Match m in EventPattern().Matches(r.Events))
            {
                string name = m.Groups[2].Value == "here" ? "anchor" : m.Groups[2].Value;
                if (index.ContainsKey(name)) eventRegion = name;
            }
            if (counted is not null)
            {
                // every motion whose count rose (or that first appears) took a statement this frame. A motion shared by the regions' scenes
                // belongs to the region whose avatar event was seen last; a region's own motion to that region. The event region's is taken last.
                // Two regions' own motions that take the same statement in one frame took one statement: the event region's, or the first.
                var risen = r.Counts.Where(kv => !counted.TryGetValue(kv.Key, out var before) || kv.Value.Updates > before).Select(kv => kv.Value)
                    .GroupBy(c => (c.Stated, c.Velocity)).Select(g => g.FirstOrDefault(c => c.Region == eventRegion) ?? g.First())
                    .OrderBy(c => c.Shared || c.Region == eventRegion ? 1 : 0).ToList();
                foreach (var c in risen)
                {
                    int reg = !c.Shared && index.TryGetValue(c.Region, out var own) ? own : index.GetValueOrDefault(eventRegion, 0);
                    sides.Add(reg);
                    var (p, v) = (c.Stated, c.Velocity);
                    output.Add(string.Create(I, $"S {r.Time - crossedAt:0.0000} {reg} {p.X - origin.X:0.000} {p.Y - origin.Y:0.000} {p.Z:0.000} {v.X:0.000} {v.Y:0.000} {v.Z:0.000}"));
                }
            }
            counted ??= new Dictionary<string, long>();
            foreach (var (key, c) in r.Counts) counted[key] = Math.Max(counted.GetValueOrDefault(key, long.MinValue), c.Updates);
            float td = r.Regions.TryGetValue(eventRegion, out var er) ? er.TimeDilation : 1f;
            string extra = drawn && r.Body is { } b && r.Anchor is { } a
                ? string.Create(I, $" {b.X - origin.X:0.000} {b.Y - origin.Y:0.000} {b.Z:0.000} {a.X - origin.X:0.000} {a.Y - origin.Y:0.000} {a.Z:0.000}")
                : "";
            output.Add(string.Create(I, $"F {r.Time - crossedAt:0.0000} {td:0.000}{extra}"));
        }
        return sides.SetEquals([0, 1]) ? output : null;
    }

    /// <summary>Every crossing trace (*-crossing.txt) of each labelled folder, read into one replay file's text; the skipped ones listed.</summary>
    public static (string Text, List<string> Skipped, int Count) ReadFolders(IEnumerable<(string Label, string Folder)> folders, bool drawn = false)
    {
        var text = new System.Text.StringBuilder();
        var skipped = new List<string>();
        int count = 0;
        foreach (var (label, folder) in folders)
            foreach (var file in Directory.GetFiles(folder, "*-crossing.txt").OrderBy(f => f, StringComparer.Ordinal))
            {
                string seq = Path.GetFileName(file).Split('-')[0];
                var lines = Read(File.ReadLines(file), drawn);
                if (lines is null) { skipped.Add($"{label} {seq}"); continue; }
                text.Append(CultureInfo.InvariantCulture, $"X {label} {seq}\n");
                foreach (var l in lines) text.Append(l).Append('\n');
                count++;
            }
        return (text.ToString(), skipped, count);
    }
}
