// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The long-run switch of the render harness (--tr-soak): its options, the steps of a cycle, the time-of-day sweep and the CSV lines.</summary>
public class SoakToolsTests
{
    private static SoakOptions Options(params string[] extra) => SoakOptions.Parse(
        new[] { "--tr-soak=90", "--tr-soak-v9=a-v9.jsonl.gz", "--tr-soak-v5=a-v5.jsonl.gz", "--tr-soak-zoo=zoo.json",
                "--tr-soak-arg=--tr-seed=8", "--tr-soak-arg=--tr-size=1920x1080",
                "--tr-soak-region-arg=--tr-texture-server=partial", "--tr-soak-avatar-arg=--tr-character=chars",
                "--tr-soak-busy-arg=--tr-busy-seconds=15" }.Concat(extra))!;

    [Fact]
    public void Without_the_switch_there_is_no_soak()
    {
        Assert.Null(SoakOptions.Parse(["--test-render", "--tr-out=x"]));
    }

    [Fact]
    public void The_switch_gives_minutes_and_the_defaults_of_the_rest()
    {
        var o = SoakOptions.Parse(["--tr-soak=90"])!;
        Assert.Equal(90, o.Minutes);
        Assert.Equal(0, o.MaxCycles);
        Assert.Equal(12, o.PathSeconds);
        Assert.Equal(10, o.BusySeconds);
        Assert.Equal(6, o.CrowdSeconds);
        Assert.Equal(2.5, o.SkySeconds);
    }

    [Fact]
    public void Numbers_are_read_with_the_invariant_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var o = SoakOptions.Parse(["--tr-soak=1.5", "--tr-soak-sky-seconds=0.5", "--tr-soak-cycles=3"])!;
            Assert.Equal(1.5, o.Minutes);
            Assert.Equal(0.5, o.SkySeconds);
            Assert.Equal(3, o.MaxCycles);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Repeated_argument_switches_are_collected_in_order_by_kind()
    {
        var o = Options();
        Assert.Equal(["--tr-seed=8", "--tr-size=1920x1080"], o.CommonArgs);
        Assert.Equal(["--tr-texture-server=partial"], o.RegionArgs);
        Assert.Equal(["--tr-character=chars"], o.AvatarArgs);
        Assert.Equal(["--tr-busy-seconds=15"], o.BusyArgs);
        Assert.Equal("a-v9.jsonl.gz", o.RegionV9);
        Assert.Equal("a-v5.jsonl.gz", o.RegionV5);
        Assert.Equal("zoo.json", o.Zoo);
    }

    [Fact]
    public void A_cycle_is_the_two_replays_the_busy_view_the_arrival_and_the_standing_crowd_in_that_order()
    {
        var steps = SoakPlan.Cycle(Options(), 1);
        Assert.Equal(["replay-v9", "replay-v5", "busy-churn", "arrive-25", "crowd-25-standing"], steps.Select(s => s.Name));
        Assert.Equal(["region", "region", "busy", "avatars", "avatars"], steps.Select(s => s.Kind));
    }

    [Fact]
    public void A_replay_step_has_its_capture_a_timed_return_path_the_sky_sweep_and_the_common_and_region_arguments_only()
    {
        var o = Options();
        var steps = SoakPlan.Cycle(o, 1);
        Assert.Equal(5, steps.Count);
        var v9 = steps[0].Args;
        var v5 = steps[1].Args;
        Assert.Contains("--tr-fixture=a-v9.jsonl.gz", v9);
        Assert.Contains("--tr-fixture=a-v5.jsonl.gz", v5);
        foreach (var a in new[] { v9, v5 })
        {
            Assert.Contains("--tr-path-seconds=12", a);
            Assert.Contains("--tr-return", a);
            Assert.Contains("--tr-sky-sweep=2.5", a);
            Assert.Contains("--tr-seed=8", a);
            Assert.Contains("--tr-texture-server=partial", a);
            Assert.DoesNotContain("--tr-character=chars", a);
            Assert.DoesNotContain("--tr-busy-seconds=15", a);
        }
    }

    [Fact]
    public void The_busy_step_is_6000_objects_with_churn_on_the_zoo_scene()
    {
        var cycle = SoakPlan.Cycle(Options(), 1);
        Assert.Equal(5, cycle.Count);
        var a = cycle[2].Args;
        Assert.Contains("--tr-fixture=zoo.json", a);
        Assert.Contains("--tr-busy=objects:6000,glow:10,lights:40,projectors:8", a);
        Assert.Contains("--tr-busy-churn", a);
        Assert.Contains("--tr-busy-seconds=15", a);
        Assert.Contains("--tr-seed=8", a);
        Assert.DoesNotContain("--tr-texture-server=partial", a);
        Assert.DoesNotContain("--tr-character=chars", a);
    }

    [Fact]
    public void The_avatar_steps_are_25_arriving_then_leaving_and_25_standing_with_the_avatar_arguments_only()
    {
        var steps = SoakPlan.Cycle(Options(), 1);
        Assert.Equal(5, steps.Count);
        var arrive = steps[3].Args;
        var crowd = steps[4].Args;
        Assert.Contains("--tr-avatars=arrive:25", arrive);
        Assert.Contains("--tr-leave", arrive);
        Assert.Contains("--tr-crowd-seconds=1", arrive);
        Assert.Contains("--tr-avatars=crowd:25,real", crowd);
        Assert.DoesNotContain("--tr-leave", crowd);
        Assert.Contains("--tr-crowd-seconds=6", crowd);
        foreach (var a in new[] { arrive, crowd })
        {
            Assert.Contains("--tr-character=chars", a);
            Assert.Contains("--tr-seed=8", a);
            Assert.DoesNotContain("--tr-texture-server=partial", a);
            Assert.DoesNotContain("--tr-busy-seconds=15", a);
            Assert.DoesNotContain(a, x => x.StartsWith("--tr-fixture=", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_step_of_every_cycle_has_its_own_tag_and_no_step_carries_the_soak_switches()
    {
        var o = Options();
        var tags = new List<string>();
        foreach (int cycle in new[] { 1, 2, 12 })
            foreach (var s in SoakPlan.Cycle(o, cycle))
            {
                Assert.DoesNotContain(s.Args, x => x.StartsWith("--tr-soak", StringComparison.Ordinal));
                var tag = Assert.Single(s.Args, x => x.StartsWith("--tr-tag=", StringComparison.Ordinal));
                tags.Add(tag);
            }
        Assert.Equal(15, tags.Distinct().Count());
        Assert.Contains("--tr-tag=soak-c012-replay-v9", tags);
    }

    [Fact]
    public void The_sweep_visits_every_time_of_day_choice_in_turn_and_starts_over()
    {
        var seen = Enumerable.Range(0, 12).Select(i => SkySweep.At(i * 2.5 + 0.1, 2.5)).ToList();
        Assert.Equal([SkyChoice.Sunrise, SkyChoice.Midday, SkyChoice.LegacyMidday, SkyChoice.Sunset, SkyChoice.Midnight, SkyChoice.Region,
                      SkyChoice.Sunrise, SkyChoice.Midday, SkyChoice.LegacyMidday, SkyChoice.Sunset, SkyChoice.Midnight, SkyChoice.Region], seen);
        Assert.Equal(6, Enum.GetValues<SkyChoice>().Length);
        Assert.Equal(SkyChoice.Sunrise, SkySweep.At(0, 2.5));
    }

    [Fact]
    public void A_number_is_written_in_the_invariant_culture_without_a_thousands_separator()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1234567.5", SoakCsv.Number(1234567.5));
            Assert.Equal("0.125", SoakCsv.Number(0.125));
            Assert.Equal("-3", SoakCsv.Number(-3));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void A_line_follows_the_header_column_for_column_and_quotes_text_that_needs_it()
    {
        var row = new List<KeyValuePair<string, string>> { new("cycle", "3"), new("note", "a, \"b\""), new("private_mib", "812.5") };
        Assert.Equal("cycle,note,private_mib", SoakCsv.Header(row));
        Assert.Equal("3,\"a, \"\"b\"\"\",812.5", SoakCsv.Line(row));
    }
}
