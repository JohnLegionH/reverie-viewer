// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;

namespace Viewer.Core.Protocol;

/// <summary>One simulator an enable-simulator message names: its address, region handle and size.</summary>
internal readonly record struct EnabledSim(IPEndPoint Address, ulong Handle, uint SizeX, uint SizeY);

/// <summary>
/// The grid tells the viewer about a neighbour twice, on the event queue: an enable-simulator message (address, handle, size) and an
/// establish-agent-communication message (address and seed capability only). They can arrive in either order. When the second comes first, the
/// library connects the neighbour from it with no handle and the default size, and then skips the enable-simulator message because it already
/// holds a simulator at that address, so the neighbour keeps handle 0 and 256 m and is never placed. This gives such a simulator the handle and
/// size the enable-simulator message states.
/// </summary>
internal static class LateHandles
{
    /// <summary>Sets the handle and size of every simulator held at an address in <paramref name="blocks"/> whose handle is still 0. The simulators changed.</summary>
    public static IReadOnlyList<T> Apply<T>(IEnumerable<EnabledSim> blocks, Func<IPEndPoint, T?> find, Func<T, ulong> handleOf, Action<T, EnabledSim> set) where T : class
    {
        var changed = new List<T>();
        foreach (var block in blocks)
        {
            if (block.Handle == 0 || find(block.Address) is not { } sim || handleOf(sim) != 0)
                continue;
            set(sim, block);
            changed.Add(sim);
        }
        return changed;
    }
}
