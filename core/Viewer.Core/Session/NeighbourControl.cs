// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>
/// Development aids for neighbour work, reached only through the development automation: a record of what a crossing does, and a way to
/// keep a neighbour from being connected in advance (<see cref="HoldAtLogin"/>, set before the login) so that the viewer's own connect at a
/// crossing can be seen. Nothing in the viewer uses it.
/// </summary>
public interface INeighbourControl
{
    /// <summary>Switches the crossing record on or off (crossed-region messages, promotions and a rate-limited line per agent update).</summary>
    void SetCrossingTrace(bool on);

    /// <summary>Development aid: each agent update sent is written to the log (when, control bits, render state, flag bytes, how far the head
    /// rotation is from the body's). Off by default.</summary>
    void SetAgentUpdateTrace(bool on) { }

    /// <summary>The next crossing into the region at <paramref name="address"/> ("ip:port") is handled as one into a region the viewer holds no circuit to,
    /// though the circuit is up (it is used, not made again). False when the address is not one.</summary>
    bool PretendNotHeld(string address);

    /// <summary>Before the login: the region at <paramref name="address"/> ("ip:port") is not connected in advance once the agent is in a region
    /// (the agent's own region, the first connect, always connects). False when the address is not one.</summary>
    bool HoldAtLogin(string address);

    /// <summary>Lifts every hold.</summary>
    void ReleaseHolds();
}
