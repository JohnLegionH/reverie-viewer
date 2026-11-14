// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;

namespace Viewer.Core.Protocol;

/// <summary>
/// Two announcements of one neighbour (the region announcement and the agent-communication message) can each start a connect to the
/// same address at the same moment. Both simulators are in the list while either connects; both seeing the other must not both give up,
/// so the one earlier in the list is kept and the later is refused.
/// </summary>
internal static class CircuitDedupe
{
    /// <summary>Whether the simulator at <paramref name="index"/> in <paramref name="listed"/> (the list in its order) must be refused
    /// because an earlier entry has the same address.</summary>
    internal static bool Refuses(IReadOnlyList<IPEndPoint> listed, int index)
    {
        for (int i = 0; i < index && i < listed.Count; i++)
            if (listed[i].Equals(listed[index]))
                return true;
        return false;
    }
}
