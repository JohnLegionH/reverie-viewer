// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The frame-time log: off by default, and when on it names the frames that hitched and what they spent their time on, and sums the
/// frame times up every few seconds. Plain .NET; each test builds its own log, so the class runs in parallel.
/// </summary>
public class FrameTimeLogTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private static Func<string, string?> Env(params (string, string)[] v) => name => v.Where(x => x.Item1 == name).Select(x => x.Item2).FirstOrDefault();

    private sealed class Lines
    {
        public readonly List<(string Event, Dictionary<string, object?> Fields)> All = new();
        public void Write(string ev, LogField[] f) => All.Add((ev, f.ToDictionary(x => x.Name, x => x.Value)));
        public List<Dictionary<string, object?>> Of(string ev) => All.Where(x => x.Event == ev).Select(x => x.Fields).ToList();
    }

    private static FrameTimeLog Log(Lines into, FrameTimeLogMode mode = FrameTimeLogMode.Hitches) => new(into.Write, mode);

    [Fact]
    public void Frames_under_the_hitch_limit_write_no_line_of_their_own()
    {
        var lines = new Lines();
        var log = Log(lines);
        for (int i = 0; i < 100; i++) { log.Cost("presenter", 2); log.EndFrame(16.7, 0, 0); }
        Assert.Empty(lines.Of("frame.hitch"));
        Assert.Empty(lines.Of("frame.sample"));
    }

    [Fact]
    public void A_frame_over_33_ms_is_logged_with_its_largest_costs_first_and_the_collector_pause()
    {
        var lines = new Lines();
        var log = Log(lines);
        log.Cost("comms", 1.5); log.Cost("presenter", 41.25); log.Cost("names", 3.0); log.Cost("presenter", 4.0);
        log.EndFrame(58.4, 22.5, 2);
        var h = Assert.Single(lines.Of("frame.hitch"));
        Assert.Equal(58.4, Convert.ToDouble(h["ms"]));
        Assert.Equal("presenter=45.25 names=3 comms=1.5", h["top"]);
        Assert.Equal(22.5, Convert.ToDouble(h["gc_pause_ms"]));
        Assert.Equal(2, Convert.ToInt32(h["gcs"]));
    }

    [Fact]
    public void The_frame_time_not_spent_in_the_viewers_own_work_is_written_as_the_engine_share()
    {
        var lines = new Lines();
        var log = Log(lines);
        log.Cost("presenter", 10); log.Cost("comms", 5);
        log.EndFrame(50, 0, 0);
        Assert.Equal(35.0, Convert.ToDouble(Assert.Single(lines.Of("frame.hitch"))["engine_ms"]));
    }

    [Fact]
    public void The_costs_of_one_frame_do_not_carry_into_the_next()
    {
        var lines = new Lines();
        var log = Log(lines);
        log.Cost("presenter", 40); log.EndFrame(45, 0, 0);
        log.Cost("comms", 1); log.EndFrame(50, 0, 0);
        var hitches = lines.Of("frame.hitch");
        Assert.Equal(2, hitches.Count);
        Assert.Equal("comms=1", hitches[1]["top"]);
    }

    [Fact]
    public void A_summary_is_written_after_five_seconds_of_frames_with_the_percentiles_and_the_hitch_counts()
    {
        var lines = new Lines();
        var log = Log(lines);
        // 280 frames of 16 ms, 8 of 40 ms, one of 150 ms: 4.48 + 0.32 + 0.15 = 4.95 s, so one more frame closes the 5 s window
        for (int i = 0; i < 280; i++) log.EndFrame(16, 0, 0);
        for (int i = 0; i < 8; i++) log.EndFrame(40, 0, 0);
        log.EndFrame(150, 0, 0);
        Assert.Empty(lines.Of("frame.pacing"));
        log.EndFrame(60, 0, 0);
        var s = Assert.Single(lines.Of("frame.pacing"));
        Assert.Equal(290, Convert.ToInt32(s["frames"]));
        Assert.Equal(16.0, Convert.ToDouble(s["p50_ms"]));
        Assert.Equal(16.0, Convert.ToDouble(s["p95_ms"]));
        Assert.Equal(40.0, Convert.ToDouble(s["p99_ms"]));
        Assert.Equal(10, Convert.ToInt32(s["over_33_ms"]));
        Assert.Equal(1, Convert.ToInt32(s["over_100_ms"]));
        Assert.Equal(150.0, Convert.ToDouble(s["max_ms"]));
    }

    [Fact]
    public void The_summary_starts_again_after_it_is_written()
    {
        var lines = new Lines();
        var log = Log(lines);
        for (int i = 0; i < 400; i++) log.EndFrame(16, 0, 0);   // 6.4 s: one summary, at 5 s
        Assert.Single(lines.Of("frame.pacing"));
        for (int i = 0; i < 300; i++) log.EndFrame(16, 0, 0);   // 4.8 s more plus the 1.4 s left over: a second summary
        var all = lines.Of("frame.pacing");
        Assert.Equal(2, all.Count);
        Assert.Equal(313, Convert.ToInt32(all[1]["frames"]));
    }

    [Fact]
    public void Off_writes_nothing()
    {
        var lines = new Lines();
        var log = Log(lines, FrameTimeLogMode.Off);
        log.Cost("presenter", 80); log.EndFrame(120, 30, 3);
        for (int i = 0; i < 1000; i++) log.EndFrame(16, 0, 0);
        Assert.Empty(lines.All);
    }

    [Fact]
    public void Full_mode_writes_a_line_for_every_frame_with_the_extra_fields()
    {
        var lines = new Lines();
        var log = Log(lines, FrameTimeLogMode.Full);
        log.EndFrame(16.5, 0, 0, new LogField("own", "1,2,3"));
        var f = Assert.Single(lines.Of("frame.sample"));
        Assert.Equal(16.5, Convert.ToDouble(f["ms"]));
        Assert.Equal("1,2,3", f["own"]);
    }

    [Fact]
    public void The_extra_fields_are_written_on_a_hitch_line_too_so_the_engine_share_can_be_taken_apart()
    {
        var lines = new Lines();
        var log = Log(lines);
        log.EndFrame(80, 0, 0, new LogField("setup_cpu_ms", 61.5));
        var h = Assert.Single(lines.Of("frame.hitch"));
        Assert.True(h.ContainsKey("setup_cpu_ms"), "the hitch line has no setup_cpu_ms");
        Assert.Equal(61.5, Convert.ToDouble(h["setup_cpu_ms"]));
    }

    [Fact]
    public void The_setting_is_off_when_nothing_is_saved_and_no_variable_is_set()
    {
        Assert.Equal(FrameTimeLogMode.Off, FrameTimeLogSettings.Resolve(new SettingsService(new MemoryStore()), Env()));
    }

    [Fact]
    public void The_saved_setting_turns_the_log_on()
    {
        var s = new SettingsService(new MemoryStore());
        s.Set(Settings.FrameTimeLog, true);
        Assert.Equal(FrameTimeLogMode.Hitches, FrameTimeLogSettings.Resolve(s, Env()));
    }

    [Theory]
    [InlineData("1", FrameTimeLogMode.Hitches)]
    [InlineData("on", FrameTimeLogMode.Hitches)]
    [InlineData("full", FrameTimeLogMode.Full)]
    [InlineData("0", FrameTimeLogMode.Off)]
    [InlineData("nonsense", FrameTimeLogMode.Hitches)]   // an unreadable value is ignored: the saved setting (here on) stands
    public void The_variable_wins_over_the_saved_setting(string value, FrameTimeLogMode expected)
    {
        var s = new SettingsService(new MemoryStore());
        s.Set(Settings.FrameTimeLog, true);
        Assert.Equal(expected, FrameTimeLogSettings.Resolve(s, Env(("NV_FRAME_TIME_LOG", value))));
    }
}
