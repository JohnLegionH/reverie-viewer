// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Reflection;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Guards for the tests whose result could depend on how busy the machine is. A test that claims a speed (a frame, a decode or a scan
/// finishing within some milliseconds) belongs to the TimingTests collection, which runs with no other test class beside it. A test that
/// waits for something to happen waits for the event with a generous bound, so a slow machine makes it slower and never fails it. The
/// tests read the test assembly and the source of one launcher test; they start nothing.
/// </summary>
public sealed class TimeLimitGuardTests
{
    private static readonly string[] SpeedClaimClasses =
    [
        nameof(ResidencyBudgetTests), nameof(LinksetSpeedTests), nameof(DecodeSchedulerTests), nameof(QuitSafetyTests),
        nameof(InventoryRecentFetchTests), nameof(MeshFuzzTests), nameof(TruncatedCodestreamTests), nameof(DecoderHangTests),
    ];

    [Fact]
    public void The_classes_that_claim_a_speed_run_in_the_serial_timing_collection()
    {
        var wrong = new List<string>();
        foreach (var name in SpeedClaimClasses)
        {
            var type = typeof(TimeLimitGuardTests).Assembly.GetTypes().Single(t => t.Name == name);
            var named = type.CustomAttributes.Where(a => a.AttributeType == typeof(CollectionAttribute)).Select(a => a.ConstructorArguments[0].Value as string);
            if (!named.Contains("TimingTests")) wrong.Add(name);
        }
        Assert.True(wrong.Count == 0, "not in the TimingTests collection: " + string.Join(", ", wrong));
    }

    [Fact]
    public void The_timing_collection_runs_alone()
    {
        var def = typeof(TimeLimitGuardTests).Assembly.GetTypes()
            .SelectMany(t => t.CustomAttributes).FirstOrDefault(a => a.AttributeType == typeof(CollectionDefinitionAttribute) && a.ConstructorArguments[0].Value as string == "TimingTests");
        Assert.NotNull(def);
        Assert.True(def!.NamedArguments.Any(n => n.MemberName == "DisableParallelization" && n.TypedValue.Value is true));
    }

    [Fact]
    public void The_default_wait_for_an_event_is_generous()
    {
        Assert.True(Wait.DefaultTimeoutMs >= 30_000, $"Wait.Until gives an event {Wait.DefaultTimeoutMs} ms");
    }

    [Fact]
    public void The_launcher_stream_test_does_not_cut_a_long_run_off_at_a_short_limit()
    {
        string src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "core", "Viewer.Core.Tests", "LauncherLogStreamingTests.cs"));
        Assert.DoesNotContain("\"-TimeoutSeconds\", \"120\"", src);
        Assert.DoesNotContain("FromSeconds(180)", src);
    }
}
