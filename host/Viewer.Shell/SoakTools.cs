// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// A long run in one viewer process (--tr-soak=&lt;minutes&gt;, test only): the same cycle of scenes over and over, with the
/// process's memory, handles, threads and frame times sampled at the same point of every cycle, so a leak or a slow decay shows
/// as a trend. These are the plain parts of it: reading the switches, the steps of one cycle, the time-of-day sweep and the
/// CSV lines. The scenes themselves run in the render harness.
/// </summary>
public sealed record SoakOptions
{
    /// <summary>How long the soak runs, in minutes; a cycle that is under way when it ends is finished.</summary>
    public double Minutes { get; init; }
    /// <summary>The most cycles to run (0: as many as fit in <see cref="Minutes"/>).</summary>
    public int MaxCycles { get; init; }
    /// <summary>Seconds each time-of-day choice stays during a replay.</summary>
    public double SkySeconds { get; init; }
    public string RegionV9 { get; init; } = "";
    public string RegionV5 { get; init; } = "";
    public string Zoo { get; init; } = "";
    /// <summary>Seconds of each region replay's camera path.</summary>
    public double PathSeconds { get; init; }
    public double BusySeconds { get; init; }
    public double CrowdSeconds { get; init; }
    /// <summary>Arguments every step gets.</summary>
    public IReadOnlyList<string> CommonArgs { get; init; } = [];
    /// <summary>Arguments the replay steps get besides the common ones.</summary>
    public IReadOnlyList<string> RegionArgs { get; init; } = [];
    /// <summary>Arguments the arrival and crowd steps get besides the common ones.</summary>
    public IReadOnlyList<string> AvatarArgs { get; init; } = [];
    /// <summary>Arguments the busy-view step gets besides the common ones.</summary>
    public IReadOnlyList<string> BusyArgs { get; init; } = [];

    /// <summary>Reads the soak switches from a run's arguments; null when --tr-soak= is absent.</summary>
    public static SoakOptions? Parse(IEnumerable<string> args)
    {
        var list = args.ToList();
        string? Value(string name) => list.Where(a => a.StartsWith(name, StringComparison.Ordinal)).Select(a => a[name.Length..]).FirstOrDefault();
        List<string> All(string name) => list.Where(a => a.StartsWith(name, StringComparison.Ordinal)).Select(a => a[name.Length..]).ToList();
        double Number(string name, double fallback) =>
            Value(name) is { } t && double.TryParse(t.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        if (Value("--tr-soak=") is not { } minutes) return null;
        return new SoakOptions
        {
            Minutes = double.TryParse(minutes.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0,
            MaxCycles = (int)Number("--tr-soak-cycles=", 0),
            SkySeconds = Number("--tr-soak-sky-seconds=", 2.5),
            PathSeconds = Number("--tr-soak-path-seconds=", 12),
            BusySeconds = Number("--tr-soak-busy-seconds=", 10),
            CrowdSeconds = Number("--tr-soak-crowd-seconds=", 6),
            RegionV9 = Value("--tr-soak-v9=")?.Trim('"') ?? "",
            RegionV5 = Value("--tr-soak-v5=")?.Trim('"') ?? "",
            Zoo = Value("--tr-soak-zoo=")?.Trim('"') ?? "",
            CommonArgs = All("--tr-soak-arg="),
            RegionArgs = All("--tr-soak-region-arg="),
            AvatarArgs = All("--tr-soak-avatar-arg="),
            BusyArgs = All("--tr-soak-busy-arg="),
        };
    }
}

/// <summary>One scene of a cycle: its name, the kind of run it is (region, busy or avatars) and the arguments of the harness run that plays it.</summary>
public sealed record SoakStep(string Name, string Kind, IReadOnlyList<string> Args);

public static class SoakPlan
{
    /// <summary>
    /// The steps of cycle number <paramref name="cycle"/> (counted from 1), in the order they run: the v9 replay and then the v5
    /// replay (a region change), the busy view at 6,000 objects with churn, 25 avatars arriving and leaving, and 25 standing.
    /// An argument given for a kind of step (or for every step) replaces the plan's own argument of the same name.
    /// </summary>
    public static IReadOnlyList<SoakStep> Cycle(SoakOptions o, int cycle)
    {
        string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        string tagOf(string name) => "--tr-tag=soak-c" + cycle.ToString("000", CultureInfo.InvariantCulture) + "-" + name;
        SoakStep Make(string name, string kind, IEnumerable<string> own, IReadOnlyList<string> kindArgs)
        {
            var extra = o.CommonArgs.Concat(kindArgs).ToList();
            var named = extra.Select(NameOf).ToHashSet(StringComparer.Ordinal);
            var args = own.Append(tagOf(name)).Where(a => !named.Contains(NameOf(a))).Concat(extra).ToList();
            return new SoakStep(name, kind, args);
        }
        IEnumerable<string> Replay(string fixture) =>
            ["--tr-fixture=" + fixture, "--tr-path-seconds=" + N(o.PathSeconds), "--tr-return", "--tr-sky-sweep=" + N(o.SkySeconds)];
        return
        [
            Make("replay-v9", "region", Replay(o.RegionV9), o.RegionArgs),
            Make("replay-v5", "region", Replay(o.RegionV5), o.RegionArgs),
            Make("busy-churn", "busy", ["--tr-fixture=" + o.Zoo, "--tr-busy=objects:6000,glow:10,lights:40,projectors:8", "--tr-busy-churn", "--tr-busy-seconds=" + N(o.BusySeconds)], o.BusyArgs),
            Make("arrive-25", "avatars", ["--tr-avatars=arrive:25", "--tr-leave", "--tr-crowd-seconds=1"], o.AvatarArgs),
            Make("crowd-25-standing", "avatars", ["--tr-avatars=crowd:25,real", "--tr-crowd-seconds=" + N(o.CrowdSeconds)], o.AvatarArgs),
        ];
    }

    /// <summary>An argument's name: what stands before its first '='.</summary>
    private static string NameOf(string arg) { int i = arg.IndexOf('='); return i < 0 ? arg : arg[..i]; }
}

public static class SkySweep
{
    private static readonly SkyChoice[] Order = [SkyChoice.Sunrise, SkyChoice.Midday, SkyChoice.LegacyMidday, SkyChoice.Sunset, SkyChoice.Midnight, SkyChoice.Region];

    /// <summary>The time-of-day choice <paramref name="seconds"/> into a sweep that stays <paramref name="periodSeconds"/> on each choice: every choice in turn, then again.</summary>
    public static SkyChoice At(double seconds, double periodSeconds)
    {
        if (!(periodSeconds > 0) || !(seconds > 0)) return Order[0];
        long step = (long)Math.Floor(seconds / periodSeconds);
        return Order[(int)(step % Order.Length)];
    }
}

public static class SoakCsv
{
    /// <summary>A number as the CSV holds it: the invariant culture, no thousands separator.</summary>
    public static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The header line for a row.</summary>
    public static string Header(IReadOnlyList<KeyValuePair<string, string>> row) => string.Join(",", row.Select(c => Field(c.Key)));

    /// <summary>The line for a row: values in order, text with a comma, quote or line break quoted.</summary>
    public static string Line(IReadOnlyList<KeyValuePair<string, string>> row) => string.Join(",", row.Select(c => Field(c.Value)));

    /// <summary>One value, quoted when it holds a comma, quote or line break.</summary>
    public static string Field(string text)
    {
        if (text.IndexOfAny([',', '"', '\n', '\r']) < 0) return text;
        var b = new StringBuilder("\"");
        b.Append(text.Replace("\"", "\"\""));
        return b.Append('"').ToString();
    }
}
