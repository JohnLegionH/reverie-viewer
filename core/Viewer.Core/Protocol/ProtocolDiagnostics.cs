// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using LibreMetaverse;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>One transport's view of LibreMetaverse's per-region data pool, as plain values.</summary>
/// <param name="PoolIdentity">RuntimeHelpers.GetHashCode of the pool object: equal values across transports mean one shared object.</param>
public sealed record DataPoolView(ulong? CurrentSimHandle, int? PoolIdentity, int? ActiveClients, int? PoolPrimCache, int SimulatorObjectsPrimitives);

/// <summary>The evidence for two transports.</summary>
/// <param name="StaticPoolCount">Entries in the process-global SimulatorDataPool.SimulatorDataPools.</param>
/// <param name="SamePoolObject">Both transports' current simulators hold the same pool object.</param>
/// <param name="SharedPrimitiveObjects">Local ids present in both clients whose Primitive is the SAME object (of <paramref name="CommonPrimitives"/> checked).</param>
public sealed record DataPoolReport(int StaticPoolCount, DataPoolView A, DataPoolView B, bool SamePoolObject, int CommonPrimitives, int SharedPrimitiveObjects);

/// <summary>
/// Read-only diagnostics over LibreMetaverse internals for multi-session hazards (ADR-006). Reflection only:
/// nothing here changes library state. Lives in Viewer.Core.Protocol because it touches LibreMetaverse types;
/// callers see plain records.
/// </summary>
public static class ProtocolDiagnostics
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static DataPoolReport DataPools(ISessionTransport a, ISessionTransport b)
    {
        var ca = ((LmvTransport)a).Client;
        var cb = ((LmvTransport)b).Client;
        var poolType = typeof(GridClient).Assembly.GetType("LibreMetaverse.SimulatorDataPool", throwOnError: true)!;
        var pools = (IDictionary?)poolType.GetField("SimulatorDataPools", Any)!.GetValue(null);
        int count;
        lock (pools!.SyncRoot) count = pools.Count;

        var simA = ca.Network.CurrentSim;
        var simB = cb.Network.CurrentSim;
        object? poolA = simA is null ? null : typeof(Simulator).GetField("DataPool", Any)!.GetValue(simA);
        object? poolB = simB is null ? null : typeof(Simulator).GetField("DataPool", Any)!.GetValue(simB);

        int common = 0, shared = 0;
        if (simA is not null && simB is not null)
        {
            var primsA = simA.ObjectsPrimitives.ToArray();
            foreach (var kv in primsA)
            {
                if (simB.ObjectsPrimitives.TryGetValue(kv.Key, out var pb))
                {
                    common++;
                    if (ReferenceEquals(kv.Value, pb))
                        shared++;
                }
            }
        }
        return new DataPoolReport(count, View(simA, poolA, poolType), View(simB, poolB, poolType), poolA is not null && ReferenceEquals(poolA, poolB), common, shared);
    }

    private static DataPoolView View(Simulator? sim, object? pool, Type poolType)
    {
        if (sim is null)
            return new(null, null, null, null, 0);
        int? active = null, cache = null;
        if (pool is not null)
        {
            active = (int?)poolType.GetField("ActiveClients", Any)?.GetValue(pool);
            if (poolType.GetField("PrimCache", Any)?.GetValue(pool) is ICollection c)
                lock (c.SyncRoot) cache = c.Count;
        }
        return new(sim.Handle, pool is null ? null : RuntimeHelpers.GetHashCode(pool), active, cache, sim.ObjectsPrimitives.Count);
    }
}
