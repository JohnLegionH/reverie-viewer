// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>
/// The CurrentSim first-burst race (W-21; ADR-007 patch layer), copied from the web gateway (web-viewer bb91ba5,
/// AgentSession.FromOurSim). During login the simulator's first burst, our own avatar object among it, can arrive
/// before LibreMetaverse sets CurrentSim; comparing against a null CurrentSim dropped it all, and nothing re-sends a
/// full avatar update. While no CurrentSim is set and exactly one simulator is connected, that simulator IS the one we
/// are logging into, so its burst is ours. Once CurrentSim is set this is exactly as strict as reference comparison.
/// </summary>
public static class FirstBurst
{
    /// <remarks><c>object</c> so a test can drive the decision without a connection; the comparison is reference identity.</remarks>
    public static bool FromOurSim(object? eventSim, object? currentSim, int connectedSimulators)
    {
        if (eventSim is null)
            return false;
        if (currentSim is not null)
            return ReferenceEquals(eventSim, currentSim);
        return connectedSimulators == 1;
    }
}
