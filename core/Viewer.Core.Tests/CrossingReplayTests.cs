// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// Recorded crossings of the user's own avatar, flying at about 16 m/s between two regions, replayed frame by frame: the statements both regions
/// sent, as the viewer took them, with the frames' times (fixtures/crossings/own-avatar-crossings.txt; names, ids, grid places and clock origins
/// removed). Each is drawn by this viewer's own motion as the viewer runs it and by the official viewer's (<see cref="OfficialOwnAvatarReplay"/>),
/// and the two must agree in every frame. The steps the official replay also shows are the official viewer's behaviour on these statements.
/// Engine-free and stateless: runs in parallel with the other classes. NV_REPLAY_REPORT names a file for the per-crossing table.
/// </summary>
public class CrossingReplayTests(ITestOutputHelper output)
{
    internal sealed record Statement(double Time, int Region, Vector3 Position, Vector3 Velocity);
    internal sealed record Frame(double Time, float TimeDilation, List<Statement> Taken);
    internal sealed record Crossing(string Name, (Vector3 Origin, float Width)[] Regions, List<Frame> Frames)
    {
        /// <summary>Whether one of the crossing's regions lies at a place (metres in the common frame).</summary>
        public bool Known(Vector3 at) => Regions.Any(r => at.X >= r.Origin.X && at.X < r.Origin.X + r.Width && at.Y >= r.Origin.Y && at.Y < r.Origin.Y + r.Width);

        /// <summary>The region <paramref name="index"/> as this viewer's motion is told it.</summary>
        public MotionRegion Region(int index) => new(Regions[index].Origin, Regions[index].Width, Known);
    }

    private static List<Crossing> Load() => Load("own-avatar-crossings.txt");

    internal static List<Crossing> Load(string file)
    {
        var path = Path.Combine(TestPaths.RepoRoot, "fixtures", "crossings", file);
        var list = new List<Crossing>();
        Crossing? current = null;
        var pending = new List<Statement>();
        var regions = new List<(Vector3, float)>();
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split(' ');
            switch (p[0])
            {
                case "X":
                    regions = new List<(Vector3, float)>();
                    current = new Crossing($"{p[1]} {p[2]}", [], new List<Frame>());
                    list.Add(current);
                    break;
                case "R":
                    regions.Add((new Vector3(F(p[2]), F(p[3]), 0), F(p[4])));
                    list[^1] = current = current! with { Regions = regions.ToArray() };
                    break;
                case "S":
                    pending.Add(new(double.Parse(p[1], CultureInfo.InvariantCulture), int.Parse(p[2]), new(F(p[3]), F(p[4]), F(p[5])), new(F(p[6]), F(p[7]), F(p[8]))));
                    break;
                case "F":
                    current!.Frames.Add(new(double.Parse(p[1], CultureInfo.InvariantCulture), F(p[2]), pending));
                    pending = new List<Statement>();
                    break;
            }
        }
        return list;
    }

    /// <summary>Per frame of <paramref name="c"/>: what this viewer draws and what the official replay draws, and the velocity last stated.</summary>
    internal static List<(double Time, Vector3 Ours, Vector3 Official, Vector3 Velocity, double Dt)> Replay(Crossing c, double crossingSeconds = Viewer.Motion.InterpolatedObject.MaxRegionCrossingSeconds, bool restartOnUpdate = false, bool oursWithPingTerm = false)
    {
        var ours = new ObjectMotion();
        var official = new OfficialOwnAvatarReplay(crossingSeconds, restartOnUpdate);
        bool Known(Vector3 at) => c.Known(at);
        var rows = new List<(double, Vector3, Vector3, Vector3, double)>();
        double last = double.NaN;
        Vector3 velocity = Vector3.Zero;
        bool started = false;
        foreach (var f in c.Frames)
        {
            double dt = double.IsNaN(last) ? 1.0 / 60 : f.Time - last;
            last = f.Time;
            foreach (var s in f.Taken)
            {
                // the viewer's scene takes a statement in its frame, before the body is placed (the region the agent is in is framed first)
                if (oursWithPingTerm) ours.OneWayDelay = 0.5 * f.TimeDilation * Math.Min(dt, 0.2);
                ours.Region = c.Region(s.Region);   // as the region's scene tells it
                ours.Update(s.Position, s.Velocity, Quaternion.Identity, f.Time);
                var (origin, width) = c.Regions[s.Region];
                official.Statement(s.Position, s.Velocity, origin, width, f.Time, dt, f.TimeDilation);
                velocity = s.Velocity;
                started = true;
            }
            if (!started) continue;
            ours.TimeDilation = f.TimeDilation;
            var drawnOurs = ours.At(f.Time).Position;
            var drawnOfficial = official.Frame(f.Time, dt, f.TimeDilation, Known);
            rows.Add((f.Time, drawnOurs, drawnOfficial, velocity, dt));
        }
        return rows;
    }

    /// <summary>The largest frame-to-frame step beyond what the last stated velocity gives, once the drawn place has settled (from the third frame on),
    /// and its time from the crossing message.</summary>
    private static (double Step, double At) LargestStep(List<(double Time, Vector3 Ours, Vector3 Official, Vector3 Velocity, double Dt)> rows, Func<(double Time, Vector3 Ours, Vector3 Official, Vector3 Velocity, double Dt), Vector3> drawn)
    {
        double worst = 0, at = double.NaN;
        for (int i = 3; i < rows.Count; i++)
        {
            double step = (drawn(rows[i]) - drawn(rows[i - 1]) - rows[i].Velocity * (float)rows[i].Dt).Length();
            if (step > worst) { worst = step; at = rows[i].Time; }
        }
        return (worst, at);
    }

    [Fact]
    public void The_recorded_crossings_are_all_read()
    {
        var all = Load();
        Assert.True(all.Count >= 50, $"{all.Count} crossings");
        Assert.All(all, c =>
        {
            Assert.Equal(2, c.Regions.Length);
            Assert.True(c.Frames.Count > 30, $"{c.Name}: {c.Frames.Count} frames");
            Assert.Contains(c.Frames, f => f.Taken.Any(s => s.Region == 0));
            Assert.Contains(c.Frames, f => f.Taken.Any(s => s.Region == 1));
        });
    }

    /// <summary>
    /// In every frame of every recorded crossing, this viewer draws the own avatar within 0.1 m of where the official viewer would (release 26.4 with its
    /// default settings: PingInterpolate off, so a statement is taken where it says; an update taken where it says however late it comes). The table the
    /// test writes (NV_REPLAY_REPORT) gives, per crossing, the largest gap now and as recorded when the replay was added
    /// (fixtures/crossings/own-avatar-crossings-gaps.txt, when this viewer still moved late updates on and the official model added a frame's ping term),
    /// the largest steps of both, and of two other rules for carrying an avatar across a border: no time limit, and a limit restarted by each update.
    /// </summary>
    [Fact]
    public void This_viewer_draws_every_recorded_crossing_within_a_tenth_of_a_metre_of_the_official_viewer_in_every_frame()
    {
        var recorded = File.ReadLines(Path.Combine(TestPaths.RepoRoot, "fixtures", "crossings", "own-avatar-crossings-gaps.txt"))
            .Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t')).ToDictionary(p => p[0], p => double.Parse(p[1], CultureInfo.InvariantCulture));
        var report = new StringBuilder("crossing\tlargest ours-official m\tat s\tas recorded when the replay was added m\tofficial largest step m\tat s\tours largest step m\tat s\tno crossing limit: largest step m\tlimit restarted by each statement: largest step m\n");
        var failures = new List<string>();
        foreach (var c in Load())
        {
            var rows = Replay(c);
            var worst = rows.Select(r => (Gap: (r.Ours - r.Official).Length(), r.Time)).MaxBy(r => r.Gap);
            var (officialStep, officialAt) = LargestStep(rows, r => r.Official);
            var (oursStep, oursAt) = LargestStep(rows, r => r.Ours);
            var (unlimited, _) = LargestStep(Replay(c, double.PositiveInfinity), r => r.Official);
            var (restarted, _) = LargestStep(Replay(c, restartOnUpdate: true), r => r.Official);
            string before = recorded.TryGetValue(c.Name, out var b) ? b.ToString("0.000", CultureInfo.InvariantCulture) : "-";
            report.Append(CultureInfo.InvariantCulture, $"{c.Name}\t{worst.Gap:0.000}\t{worst.Time:0.00}\t{before}\t{officialStep:0.000}\t{officialAt:0.00}\t{oursStep:0.000}\t{oursAt:0.00}\t{unlimited:0.000}\t{restarted:0.000}\n");
            if (worst.Gap > 0.1) failures.Add($"{c.Name}: {worst.Gap:0.000} m at {worst.Time:0.00} s");
        }
        output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("NV_REPLAY_REPORT") is { Length: > 0 } file) File.WriteAllText(file, report.ToString());
        Assert.True(failures.Count == 0, $"{failures.Count} crossings drawn more than 0.1 m from the official viewer: {string.Join("; ", failures)}");
    }
}
