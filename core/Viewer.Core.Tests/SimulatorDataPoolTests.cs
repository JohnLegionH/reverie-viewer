// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections;
using System.Net;
using System.Reflection;
using LibreMetaverse;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// LibreMetaverse 3.1.6 puts a Simulator into its process-global SimulatorDataPool only when
/// Settings.Parcel.PoolParcelData or Settings.World.CachePrimitives is on, and then looks the pool up BEFORE assigning
/// Simulator.Handle, so every simulator of every client shares one entry (measured offline: key 0, ActiveClients 3 for two
/// clients and two regions). The live two-session run in Alpha found the pool empty and no shared Primitive objects under
/// the adapter's settings. This pins that: two adapters in one process, each creating a Simulator (offline, no network),
/// and neither joins the global pool.
/// </summary>
public class SimulatorDataPoolTests
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static Simulator NewSimulator(GridClient c, ulong handle)
    {
        var ctor = typeof(Simulator).GetConstructors(Any).OrderBy(k => k.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p =>
            p.ParameterType == typeof(GridClient) ? c :
            p.ParameterType == typeof(IPEndPoint) ? new IPEndPoint(IPAddress.Loopback, 9) :
            p.ParameterType == typeof(ulong) ? handle :
            p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        return (Simulator)ctor.Invoke(args);
    }

    [Fact]
    public void Two_adapters_in_one_region_never_join_the_process_global_data_pool()
    {
        var pools = (IDictionary)typeof(GridClient).Assembly.GetType("LibreMetaverse.SimulatorDataPool")!
            .GetField("SimulatorDataPools", Any)!.GetValue(null)!;
        int before;
        lock (pools.SyncRoot) before = pools.Count;

        using var a = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        using var b = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        const ulong alpha = 1099511628032000UL;
        var sa = NewSimulator(a.Client, alpha);
        var sb = NewSimulator(b.Client, alpha);

        var dataPool = typeof(Simulator).GetField("DataPool", Any)!;
        Assert.Null(dataPool.GetValue(sa));
        Assert.Null(dataPool.GetValue(sb));
        lock (pools.SyncRoot) Assert.Equal(before, pools.Count);
    }
}
