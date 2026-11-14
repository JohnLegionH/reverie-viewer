// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The reader of the crossing traces takes each statement in the frame the viewer took it (the frame in which the avatar's update count
/// rises, with that frame's stated place), and each frame at the time its body was placed (the motion's own time, which the trace writes as
/// now=, not the time the trace line itself was written). Engine-free and stateless: runs in parallel with the other classes.
/// </summary>
public class CrossingTraceReaderTests
{
    // Five recorded frames, trimmed: region names, entry ids, grid places and the clock's origin replaced. Frame 1 takes a statement of the region
    // left; frame 2 holds the crossing message
    // and a statement taken in its own frame; frame 3 sees the next statement's event at its very end, and the scene takes it in frame 4.
    private static readonly string[] Trace =
    [
        "frame=0 t=10.0010 cur=anchor anchor=556.110,1031.800,26.823 own_users=1 [anchor grid=0,0 off=0,0 td=1.000 entry=7 held=0 drives=1 shared=1 motion=1 upd=9 stated=556.000,1032.300,26.823 vel=0.033,-2.700,0.000 now=9.9900 place=556.110,1031.800,25.895 drawn=556.110,1031.800,25.895]",
        "frame=1 t=10.0500 cur=anchor anchor=556.100,1031.500,26.823 own_users=1 [anchor grid=0,0 off=0,0 td=1.000 entry=7 held=0 drives=1 shared=1 motion=1 upd=10 stated=556.000,1032.000,26.823 vel=0.033,-2.700,0.000 now=10.0400 place=556.100,1031.500,25.895 drawn=556.100,1031.500,25.895] | events: 10.0300 anchor AvatarMoved local=7 epoch=0 handoff=0",
        "frame=2 t=10.1000 cur=B anchor=556.050,1030.800,26.823 own_users=2 [anchor grid=0,0 off=0,0 td=1.000 entry=7 held=1 drives=0 shared=1 motion=1 upd=11 stated=556.011,1029.400,26.823 vel=0.033,-2.701,0.000 now=10.0850 place=556.050,1030.800,25.895 drawn=556.050,1030.800,25.895] [B grid=0,4 off=0,1024 td=1.000 entry=8 held=0 drives=1 shared=1 motion=1 upd=11 stated=556.011,1029.400,26.823 vel=0.033,-2.701,0.000 now=10.0850 place=556.050,1030.800,25.895 drawn=556.050,1030.800,25.895] | events: 10.0820 region Crossed B; 10.0840 B AvatarMoved local=8 epoch=0 handoff=0",
        "frame=3 t=10.2095 cur=B anchor=556.021,1028.846,26.823 own_users=2 [anchor grid=0,0 off=0,0 td=1.000 entry=7 held=1 drives=0 shared=1 motion=1 upd=11 stated=556.011,1029.400,26.823 vel=0.033,-2.701,0.000 now=10.1900 place=556.021,1028.845,25.895 drawn=556.021,1028.867,25.895] [B grid=0,4 off=0,1024 td=1.000 entry=8 held=0 drives=1 shared=1 motion=1 upd=11 stated=556.011,1029.400,26.823 vel=0.033,-2.701,0.000 now=10.1900 place=556.021,1028.845,25.895 drawn=556.021,1028.867,25.895] | events: 10.2093 B AvatarMoved local=8 epoch=0 handoff=0",
        "frame=4 t=10.2549 cur=B anchor=556.018,1028.621,26.823 own_users=2 [anchor grid=0,0 off=0,0 td=0.989 entry=7 held=1 drives=0 shared=1 motion=1 upd=12 stated=556.013,1028.185,26.823 vel=0.014,1.111,0.000 now=10.2400 place=556.018,1028.620,25.895 drawn=556.018,1028.628,25.895] [B grid=0,4 off=0,1024 td=1.000 entry=8 held=0 drives=1 shared=1 motion=1 upd=12 stated=556.013,1028.185,26.823 vel=0.014,1.111,0.000 now=10.2400 place=556.018,1028.620,25.895 drawn=556.018,1028.628,25.895]",
    ];

    private static List<string[]> Lines(string kind) => (CrossingTraceReader.Read(Trace) ?? []).Where(l => l.StartsWith(kind + " ", StringComparison.Ordinal)).Select(l => l.Split(' ')).ToList();

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    [Fact]
    public void A_statement_whose_update_count_rises_the_frame_after_its_event_is_taken_in_that_frame_with_its_own_place()
    {
        var statements = Lines("S");
        Assert.Equal(3, statements.Count);
        // the third statement: taken in frame 4 (0.240 s on the motion's clock less the crossing message's 0.082 s), at the place frame 4 states
        Assert.Equal(10.2400 - 10.0820, D(statements[2][1]), 4);
        Assert.Equal("1", statements[2][2]);
        Assert.Equal(1028.185, D(statements[2][4]), 3);
        Assert.Equal(1.111, D(statements[2][7]), 3);
        // never a repeat of the statement before it
        Assert.DoesNotContain(statements, s => D(s[4]) == 1029.400 && D(s[1]) > 0.01);
    }

    [Fact]
    public void A_statement_taken_in_the_frame_of_its_event_is_taken_there_and_belongs_to_the_region_that_stated_it()
    {
        var statements = Lines("S");
        Assert.Equal(3, statements.Count);
        Assert.Equal(10.0400 - 10.0820, D(statements[0][1]), 4);
        Assert.Equal("0", statements[0][2]);
        Assert.Equal(10.0850 - 10.0820, D(statements[1][1]), 4);
        Assert.Equal("1", statements[1][2]);
        Assert.Equal(1029.400, D(statements[1][4]), 3);
    }

    [Fact]
    public void Each_frame_is_read_at_the_time_its_body_was_placed_not_when_its_trace_line_was_written()
    {
        var frames = Lines("F");
        Assert.Equal(5, frames.Count);
        Assert.Equal(new[] { 9.9900, 10.0400, 10.0850, 10.1900, 10.2400 }.Select(t => Math.Round(t - 10.0820, 4)).ToArray(), frames.Select(f => Math.Round(D(f[1]), 4)).ToArray());
    }

    [Fact]
    public void A_trace_whose_motion_ran_on_another_clock_is_read_on_its_lines_times()
    {
        // an older build timed each region's motion on a clock started with the session, and a motion's time there was its own last reading,
        // not the frame's (it stepped back when the driving region changed): such a trace is read on the times its lines were written
        var shifted = Trace.Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"now=([0-9.]+)",
            m => "now=" + (D(m.Groups[1].Value) - 9.0).ToString("0.0000", CultureInfo.InvariantCulture))).ToArray();
        var frames = (CrossingTraceReader.Read(shifted) ?? []).Where(l => l.StartsWith("F ", StringComparison.Ordinal)).Select(l => Math.Round(D(l.Split(' ')[1]), 4)).ToList();
        Assert.Equal(new[] { 10.0010, 10.0500, 10.1000, 10.2095, 10.2549 }.Select(t => Math.Round(t - 10.0820, 4)).ToArray(), frames.ToArray());
    }

    [Fact]
    public void A_trace_with_a_motion_per_region_takes_each_region_s_statements_by_its_own_count()
    {
        // an older build kept one motion per region (shared=0), each counting its own updates: the region entered's count starts low and the
        // region left's holds its last statement
        string Unshared(string l)
        {
            int b = l.IndexOf("[B ", StringComparison.Ordinal);
            string a = (b < 0 ? l : l[..b]).Replace("shared=1", "shared=0");
            a = System.Text.RegularExpressions.Regex.Replace(a, @"upd=1[12] stated=\S+ vel=\S+", "upd=10 stated=556.000,1032.000,26.823 vel=0.033,-2.700,0.000");
            if (b < 0) return a;
            string rest = l[b..].Replace("shared=1", "shared=0").Replace("upd=11 ", "upd=1 ").Replace("upd=12 ", "upd=2 ");
            return a + rest;
        }
        var statements = (CrossingTraceReader.Read(Trace.Select(Unshared)) ?? []).Where(l => l.StartsWith("S ", StringComparison.Ordinal)).Select(l => l.Split(' ')).ToList();
        Assert.Equal(3, statements.Count);
        Assert.Equal(["0", "1", "1"], statements.Select(s => s[2]).ToArray());
        Assert.Equal([1032.000, 1029.400, 1028.185], statements.Select(s => D(s[4])).ToArray());
    }

    [Fact]
    public void The_regions_are_placed_relative_to_the_region_left()
    {
        var regions = Lines("R");
        Assert.Equal(["R 0 0 0 1024", "R 1 0 1024 1024"], regions.Select(r => string.Join(' ', r)).ToArray());
    }

    [Fact]
    public void A_drawn_reading_carries_the_body_and_the_camera_anchor_of_each_frame()
    {
        var f = (CrossingTraceReader.Read(Trace, drawn: true) ?? []).Where(l => l.StartsWith("F ", StringComparison.Ordinal)).Select(l => l.Split(' ')).ToList();
        Assert.Equal(5, f.Count);
        Assert.Equal(9, f[3].Length);
        Assert.Equal(1028.867, D(f[3][4]), 3);   // the driving entry's drawn body
        Assert.Equal(1028.846, D(f[3][7]), 3);   // the camera anchor
    }

    /// <summary>
    /// A development aid, not a check: reads the crossing traces of NV_CROSSING_TRACES (label=folder, separated by ';') into the replay file
    /// NV_CROSSING_OUT, in the committed fixture's form, or with each frame's drawn body and camera anchor when NV_CROSSING_DRAWN is 1.
    /// A real skip when the traces are not named (they hold grid content and are never committed).
    /// </summary>
    [CrossingTracesFact]
    public void The_named_crossing_traces_are_read_into_a_replay_file()
    {
        var folders = Environment.GetEnvironmentVariable("NV_CROSSING_TRACES")!.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Select(p => (p[0], p[1])).ToList();
        bool drawn = Environment.GetEnvironmentVariable("NV_CROSSING_DRAWN") == "1";
        var (text, skipped, count) = CrossingTraceReader.ReadFolders(folders, drawn);
        const string header = "# own-avatar crossings replayed: X flight seq; R region origin (metres, region left at 0 0); S time region position velocity "
            + "(a statement, taken in the frame its update count rises); F frame time (the time the frame's body was placed) and time dilation"
            + "; with drawn readings, then the drawn body and the camera anchor. Times are seconds from the crossing message.\n";
        File.WriteAllText(Environment.GetEnvironmentVariable("NV_CROSSING_OUT")!, header + text);
        Console.WriteLine($"{count} crossings written; skipped (no statement from one side, or no crossing message): {string.Join(", ", skipped)}");
        Assert.True(count > 0);
    }
}

/// <summary>A [Fact] that is a real SKIP, with its reason, unless NV_CROSSING_TRACES and NV_CROSSING_OUT name the traces and the output file.</summary>
public sealed class CrossingTracesFactAttribute : FactAttribute
{
    public CrossingTracesFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NV_CROSSING_TRACES")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NV_CROSSING_OUT")))
            Skip = "development aid: NV_CROSSING_TRACES and NV_CROSSING_OUT are not set (crossing traces hold grid content and are never committed)";
    }
}
