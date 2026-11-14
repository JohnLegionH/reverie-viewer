// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A crossing into a region the viewer holds no circuit to is run by the viewer: it opens the circuit as a child, then makes it the
/// agent's region. A failed, slow or throwing connect hands the crossing to the library's own crossing (the backstop) instead of
/// leaving the agent in a region nobody is connected to; two messages for one destination start one connect.
/// </summary>
public class CrossingRunnerTests
{
    private static readonly CrossingTarget Target = new(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9000), 1000UL * 256 << 32 | 1000UL * 256,
        new Uri("http://sim.example.org/seed"), 1024, 1024);

    private sealed class Sim;

    private sealed class Probe
    {
        public int Connects, Promotes;
        public readonly List<string> Backstops = new();
    }

    private static CrossingRunner<Sim> Runner(Probe p, Func<CrossingTarget, CancellationToken, Task<Sim?>> connect, TimeSpan? limit = null) =>
        new(connect, (_, _) => Interlocked.Increment(ref p.Promotes), (_, why) => { lock (p.Backstops) p.Backstops.Add(why); }, limit ?? TimeSpan.FromSeconds(5));

    // ---------------- the plan ----------------

    [Fact]
    public void An_unheld_destination_is_connected_by_the_viewer_with_neighbours_on() =>
        Assert.Equal(LmvTransport.CrossingPlan.Connect, LmvTransport.PlanCrossing(true, destinationHeld: false));

    [Theory]
    [InlineData(true, true, "Promote")]
    [InlineData(true, false, "Connect")]
    [InlineData(false, true, "Library")]
    [InlineData(false, false, "Library")]
    public void The_plan_follows_the_setting_and_whether_the_destination_is_held(bool neighbours, bool held, string expected) =>
        Assert.Equal(expected, LmvTransport.PlanCrossing(neighbours, held).ToString());

    // ---------------- the target ----------------

    [Fact]
    public void The_target_is_read_from_the_crossed_region_message()
    {
        var msg = new CrossedRegionMessage
        {
            IP = IPAddress.Parse("192.0.2.20"), Port = 9010, RegionHandle = 5UL << 32 | 7, SeedCapability = new Uri("http://sim.example.org/s"),
            RegionSizeX = 1024, RegionSizeY = 512,
        };
        var t = CrossingTarget.From(msg);
        Assert.NotNull(t);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.20"), 9010), t!.EndPoint);
        Assert.Equal(5UL << 32 | 7, t.Handle);
        Assert.Equal(new Uri("http://sim.example.org/s"), t.Seed);
        Assert.Equal(1024u, t.SizeX);
        Assert.Equal(512u, t.SizeY);
    }

    [Fact]
    public void A_message_without_an_address_names_no_target() =>
        Assert.Null(CrossingTarget.From(new CrossedRegionMessage { IP = null!, Port = 0, RegionHandle = 1 }));

    // ---------------- the run ----------------

    [Fact]
    public async Task A_connect_that_succeeds_promotes_the_new_region_once()
    {
        var p = new Probe();
        var sim = new Sim();
        var r = Runner(p, (_, _) => { p.Connects++; return Task.FromResult<Sim?>(sim); });
        Assert.Equal(CrossingOutcome.Promoted, await r.RunAsync(Target, CancellationToken.None));
        Assert.Equal(1, p.Connects);
        Assert.Equal(1, p.Promotes);
        Assert.Empty(p.Backstops);
    }

    [Fact]
    public async Task A_connect_that_returns_nothing_goes_to_the_backstop_and_promotes_nothing()
    {
        var p = new Probe();
        var r = Runner(p, (_, _) => Task.FromResult<Sim?>(null));
        Assert.Equal(CrossingOutcome.Backstop, await r.RunAsync(Target, CancellationToken.None));
        Assert.Equal(0, p.Promotes);
        Assert.Single(p.Backstops);
    }

    [Fact]
    public async Task A_connect_that_throws_goes_to_the_backstop()
    {
        var p = new Probe();
        var r = Runner(p, (_, _) => throw new InvalidOperationException("socket"));
        Assert.Equal(CrossingOutcome.Backstop, await r.RunAsync(Target, CancellationToken.None));
        Assert.Equal(0, p.Promotes);
        Assert.Single(p.Backstops);
    }

    [Fact]
    public async Task A_connect_that_takes_longer_than_the_limit_goes_to_the_backstop()
    {
        var p = new Probe();
        var r = Runner(p, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new Sim(); }, TimeSpan.FromMilliseconds(100));
        Assert.Equal(CrossingOutcome.Backstop, await r.RunAsync(Target, CancellationToken.None));
        Assert.Equal(0, p.Promotes);
        Assert.Single(p.Backstops);
    }

    [Fact]
    public async Task A_second_message_for_the_same_destination_while_one_runs_starts_nothing()
    {
        var p = new Probe();
        var release = new TaskCompletionSource<Sim?>();
        var started = new TaskCompletionSource();
        var r = Runner(p, (_, _) => { Interlocked.Increment(ref p.Connects); started.TrySetResult(); return release.Task; });
        var first = r.RunAsync(Target, CancellationToken.None);
        Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(60))));   // (a stub that never connects must fail here, not hang)
        Assert.Equal(CrossingOutcome.Duplicate, await r.RunAsync(Target, CancellationToken.None));
        release.SetResult(new Sim());
        Assert.Equal(CrossingOutcome.Promoted, await first);
        Assert.Equal(1, p.Connects);
        Assert.Equal(1, p.Promotes);
    }

    [Fact]
    public async Task The_same_destination_can_be_crossed_again_after_the_first_run_has_ended()
    {
        var p = new Probe();
        var r = Runner(p, (_, _) => Task.FromResult<Sim?>(new Sim()));
        Assert.Equal(CrossingOutcome.Promoted, await r.RunAsync(Target, CancellationToken.None));
        Assert.Equal(CrossingOutcome.Promoted, await r.RunAsync(Target, CancellationToken.None));
        Assert.Equal(2, p.Promotes);
    }

    [Fact]
    public async Task A_promote_that_throws_goes_to_the_backstop_and_frees_the_destination()
    {
        var p = new Probe();
        var r = new CrossingRunner<Sim>((_, _) => Task.FromResult<Sim?>(new Sim()), (_, _) => throw new InvalidOperationException("x"),
            (_, why) => { lock (p.Backstops) p.Backstops.Add(why); }, TimeSpan.FromSeconds(5));
        Assert.Equal(CrossingOutcome.Backstop, await r.RunAsync(Target, CancellationToken.None));
        Assert.Single(p.Backstops);
        Assert.Equal(CrossingOutcome.Backstop, await r.RunAsync(Target, CancellationToken.None));   // not stuck as "running"
    }
}
