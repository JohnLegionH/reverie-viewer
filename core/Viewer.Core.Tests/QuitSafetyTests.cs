// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The engine corrupts its heap when it quits while resources the viewer created are still live. Two guards:
/// a ledger of the engine resources the viewer makes (so a run can list what is still live at quit), and a
/// teardown that runs every step even when an earlier one throws.
/// </summary>
[Collection("TimingTests")]
public class QuitSafetyTests
{
    [Fact]
    public void A_resource_that_was_made_and_freed_is_not_live()
    {
        var ledger = new LiveRidLedger();
        ledger.Created(7, "water mesh");
        ledger.Freed(7);
        Assert.Empty(ledger.Live());
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void A_resource_that_was_made_and_not_freed_is_listed_with_its_owner()
    {
        var ledger = new LiveRidLedger();
        ledger.Created(7, "water mesh");
        ledger.Created(8, "water instance");
        ledger.Created(9, "terrain chunk");
        ledger.Freed(8);
        var live = ledger.Live();
        Assert.Equal(2, live.Count);
        Assert.Contains(live, l => l.Id == 7 && l.Owner == "water mesh");
        Assert.Contains(live, l => l.Id == 9 && l.Owner == "terrain chunk");
        Assert.Equal("2 live: terrain chunk x1, water mesh x1", ledger.Summary());
    }

    [Fact]
    public void Freeing_a_resource_the_ledger_never_saw_is_counted_and_harmless()
    {
        var ledger = new LiveRidLedger();
        ledger.Created(1, "a");
        ledger.Freed(99);   // made by the engine itself (a resource's own id), not by us
        Assert.Equal(1, ledger.Count);
        Assert.Equal(1, ledger.UnknownFrees);
    }

    [Fact]
    public void Freeing_twice_is_counted()
    {
        var ledger = new LiveRidLedger();
        ledger.Created(1, "a");
        ledger.Freed(1);
        ledger.Freed(1);
        Assert.Equal(0, ledger.Count);
        Assert.Equal(1, ledger.UnknownFrees);
    }

    [Fact]
    public void The_summary_of_an_empty_ledger_says_none()
    {
        Assert.Equal("none live", new LiveRidLedger().Summary());
    }

    [Fact]
    public void Concurrent_use_loses_nothing()
    {
        var ledger = new LiveRidLedger();
        Parallel.For(0, 2000, i => ledger.Created((ulong)(i + 1), "x"));
        Parallel.For(0, 1000, i => ledger.Freed((ulong)(i + 1)));
        Assert.Equal(1000, ledger.Count);
    }

    [Fact]
    public void Every_step_runs_when_an_earlier_one_throws_and_the_exception_is_still_raised()
    {
        var ran = new List<string>();
        var ex = Assert.Throws<AggregateException>(() => TeardownSteps.RunAll(
            ("a", () => ran.Add("a")),
            ("b", () => { ran.Add("b"); throw new InvalidOperationException("b failed"); }),
            ("c", () => ran.Add("c")),
            ("d", () => { ran.Add("d"); throw new ArgumentException("d failed"); })));
        Assert.Equal(["a", "b", "c", "d"], ran);
        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains("b", ex.Message);
        Assert.Contains("d", ex.Message);
    }

    [Fact]
    public void Steps_that_all_succeed_raise_nothing()
    {
        int n = 0;
        TeardownSteps.RunAll(("a", () => n++), ("b", () => n++));
        Assert.Equal(2, n);
    }

    [Fact]
    public void The_failures_are_reported_to_the_sink_as_they_happen()
    {
        var seen = new List<string>();
        Assert.Throws<AggregateException>(() => TeardownSteps.RunAll(
            seen.Add,
            ("a", () => throw new InvalidOperationException("boom")),
            ("b", () => { })));
        Assert.Single(seen);
        Assert.Contains("a", seen[0]);
        Assert.Contains("boom", seen[0]);
    }

    [Fact]
    public void A_step_that_takes_over_the_threshold_is_reported_with_its_name_and_time_and_a_quick_one_is_not()
    {
        var slow = new List<(string Name, double Ms)>();
        TeardownSteps.RunAll(_ => { }, (name, ms) => slow.Add((name, ms)),
            ("quick", () => { }),
            ("slow", () => System.Threading.Thread.Sleep(TeardownSteps.SlowStepMilliseconds + 60)),
            ("quick again", () => { }));
        var only = Assert.Single(slow);
        Assert.Equal("slow", only.Name);
        Assert.True(only.Ms >= TeardownSteps.SlowStepMilliseconds, only.Ms.ToString());
    }

    [Fact]
    public void A_slow_step_that_throws_is_still_reported_slow_and_its_failure_raised()
    {
        var slow = new List<string>();
        Assert.Throws<AggregateException>(() => TeardownSteps.RunAll(_ => { }, (name, _) => slow.Add(name),
            ("slow and failing", () => { System.Threading.Thread.Sleep(TeardownSteps.SlowStepMilliseconds + 60); throw new InvalidOperationException("late"); })));
        Assert.Equal(["slow and failing"], slow);
    }

    [Theory]
    [InlineData("host/Viewer.Godot/Presentation/WorldPresenter.cs", "public void Dispose()")]
    [InlineData("host/Viewer.Godot/Main.cs", "private void Unpresent()")]
    [InlineData("host/Viewer.Godot/Main.Neighbours.cs", "TeardownSteps.RunAll(")]
    public void The_viewers_teardown_paths_report_their_slow_steps(string file, string signature)
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Viewer.sln"))) dir = Path.GetDirectoryName(dir)!;
        string text = File.ReadAllText(Path.Combine(dir ?? throw new InvalidOperationException("repository root not found"), file.Replace('/', Path.DirectorySeparatorChar)));
        int at = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature + " not found in " + file);
        Assert.Contains("teardown.step-slow", text[at..Math.Min(text.Length, at + 1400)]);
    }

    // The two quit paths the shipping viewer takes, checked in the source so a step cannot go back to a plain list.
    [Theory]
    [InlineData("host/Viewer.Godot/Presentation/WorldPresenter.cs", "public void Dispose()")]
    [InlineData("host/Viewer.Godot/Main.cs", "private void Unpresent()")]
    public void The_presentation_teardown_runs_every_step_through_the_helper(string file, string signature)
    {
        string text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, file.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");
        int at = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature + " not found in " + file);
        int end = text.IndexOf("\n    }\n", at, StringComparison.Ordinal);
        string body = text.Substring(at, end - at);
        Assert.Contains("TeardownSteps.RunAll", body);
    }

    [Fact]
    public void Every_engine_resource_the_viewer_creates_goes_through_the_ledger()
    {
        // A direct create or free in the host would leave the ledger blind to it. (Texture slots are not tracked: a texture made with Texture2DCreate is handed to TextureReplace, which takes it over.)
        var offenders = new List<string>();
        string host = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
        foreach (var f in Directory.EnumerateFiles(host, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains(Path.DirectorySeparatorChar + "build" + Path.DirectorySeparatorChar) || f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            if (Path.GetFileName(f) == "Rids.cs") continue;
            int line = 0;
            foreach (var l in File.ReadLines(f))
            {
                line++;
                if (System.Text.RegularExpressions.Regex.IsMatch(l, @"RenderingServer\.(MeshCreate|InstanceCreate2?|SkeletonCreate|SpotLightCreate|OmniLightCreate|FreeRid)\("))
                    offenders.Add($"{Path.GetRelativePath(TestPaths.RepoRoot, f)}:{line}");
            }
        }
        Assert.True(offenders.Count == 0, "direct engine resource calls: " + string.Join(", ", offenders));
    }
}
