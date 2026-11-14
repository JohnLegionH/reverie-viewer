// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A 1024 m region with 1024 m neighbours on all eight sides: every neighbour is placed at the right corner (a whole number of
/// 1024 m steps from the anchor), whichever side it is on, and a simulator reported before its handle was final is placed from the
/// final one.
/// </summary>
public class NeighbourRingTests
{
    private const int Step = 1024;
    private const int CellsPerStep = Step / 256;
    private const int HomeX = 1000, HomeY = 1000;

    // one reference per region: the session compares simulators by reference, as the adapter hands them out
    private static readonly Dictionary<(int, int), SimRef> Made = new();

    private static SimRef Region(int dx, int dy, string name)
    {
        lock (Made)
        {
            if (!Made.TryGetValue((dx, dy), out var r))
                Made[(dx, dy)] = r = new SimRef((ulong)((HomeX + dx * CellsPerStep) * 256L) << 32 | (ulong)((HomeY + dy * CellsPerStep) * 256L), name);
            return r;
        }
    }

    private static readonly (int Dx, int Dy)[] Ring = [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];

    private static async Task<(ViewerSession S, FakeTransport T)> InWorld()
    {
        var t = new FakeTransport();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/", Neighbours = true }, t, new CoreLog(new ListSink(), "test"));
        await s.LoginAsync(new LoginCredentials("Test", "Resident", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None);
        t.DriveToInWorld(Region(0, 0, "Home"));
        t.Raise(new SimConnected(Region(0, 0, "Home"), Step, Step));
        return (s, t);
    }

    [Fact]
    public async Task Every_one_of_eight_neighbours_is_placed_a_whole_step_from_the_anchor_on_its_side()
    {
        var (s, t) = await InWorld();
        using var _ = s;
        foreach (var (dx, dy) in Ring)
        {
            t.ConnectedSimulatorCount++;
            t.Raise(new SimConnected(Region(dx, dy, $"R{dx + 1}{dy + 1}"), Step, Step));
        }
        Assert.Equal(9, s.Worlds.Count);
        foreach (var (dx, dy) in Ring)
        {
            var w = s.Worlds.Single(x => x.Sim == Region(dx, dy, $"R{dx + 1}{dy + 1}"));
            Assert.Equal(new Vector3(dx * Step, dy * Step, 0), w.OriginOffset);
            Assert.Equal(Step, w.Region.SizeX);
            Assert.Equal(Step, w.Region.SizeY);
        }
    }

    [Fact]
    public void A_simulator_reported_with_a_wrong_handle_then_its_final_one_is_placed_from_the_final_handle()
    {
        var map = new SimRefMap<object>();
        foreach (var (dx, dy) in Ring)
        {
            var sim = new object();
            var early = map.Get(sim, 0, "");
            ulong finalHandle = (ulong)((HomeX + dx * CellsPerStep) * 256L) << 32 | (ulong)((HomeY + dy * CellsPerStep) * 256L);
            var later = map.Get(sim, finalHandle, "n");
            Assert.NotSame(early, later);
            Assert.Equal(finalHandle, later.Handle);
            Assert.Equal(new Vector3(dx * Step, dy * Step, 0), RegionPlacement.Offset(Region(0, 0, "Home"), later));
        }
    }

    [Fact]
    public async Task A_neighbour_in_reach_of_the_draw_distance_is_found_on_every_side()
    {
        var (s, t) = await InWorld();
        using var _ = s;
        foreach (var (dx, dy) in Ring)
        {
            t.ConnectedSimulatorCount++;
            t.Raise(new SimConnected(Region(dx, dy, $"R{dx + 1}{dy + 1}"), Step, Step));
        }
        // standing 10 m inside the anchor's south-west corner: the three neighbours on the south and west sides are within 50 m, the far ones are not
        var corner = new Vector3(10, 10, 20);
        var near = s.Worlds.Where(w => !w.IsCurrent && RegionPlacement.InReach(corner, w.OriginOffset, w.Region.SizeX, w.Region.SizeY, 50f))
            .Select(w => w.Sim.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["R00", "R01", "R10"], near);
    }
}
