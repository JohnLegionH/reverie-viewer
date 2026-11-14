// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Collections.Concurrent;
using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Xunit;
using F = Viewer.Motion.AgentControlFlags;

namespace Viewer.Core.Tests;

/// <summary>
/// The AgentUpdate the protocol library used to send from its own timer thread. The library keeps the control bits as one
/// shared value that the property setters change a bit at a time with no lock, and its periodic update (a timer thread, turned off now)
/// reads that value; an update sent while the main thread is part-way through setting seventeen bits could carry a mix
/// of the old and new bits (for example the move bit cleared with its fast bit still set). The transport therefore sends
/// the periodic update itself, under the lock that every change of the state takes. Every test builds its own transport:
/// no process-wide state, so the class runs in parallel with the others.
/// </summary>
public class AgentUpdateThreadTests
{
    private static LmvTransport NewTransport() => new(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "agent-update"));

    [Fact]
    public void A_periodic_update_never_carries_a_half_written_control_state()
    {
        using var transport = NewTransport();
        var seen = new ConcurrentQueue<uint>();
        transport.SendProbe = seen.Enqueue;
        // two states that differ in every bit of the first: any value between them is a torn write
        const uint a = F.AtPos | F.FastAt | F.YawPos | F.Fly | F.UpPos | F.FastUp | F.LeftPos | F.FastLeft;
        const uint b = F.AtNeg | F.YawNeg | F.Stop | F.UpNeg | F.LeftNeg | F.NudgeAtPos | F.NudgeLeftNeg;
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            bool flip = false;
            while (!stop.IsCancellationRequested)
            {
                transport.SendAgentUpdate(new AgentUpdate(flip ? a : b, Quaternion.Identity, Vector3.Zero, Vector3.Zero));
                flip = !flip;
            }
        });
        var timer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) transport.SetDrawDistance(128);
        });
        Thread.Sleep(400);   // a window in which two threads race; what must not happen is a torn value
        stop.Cancel();
        Task.WaitAll(writer, timer);
        var torn = seen.Where(c => c != 0 && c != a && c != b).Distinct().ToList();
        Assert.True(torn.Count == 0, $"{torn.Count} torn control values were sent, for example 0x{torn.FirstOrDefault():X}");
    }

    [Fact]
    public void The_library_does_not_send_its_own_periodic_update()
    {
        using var transport = NewTransport();
        Assert.Equal(0, transport.LibraryUpdateInterval);
    }
}
