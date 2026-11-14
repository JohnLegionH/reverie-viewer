// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;

namespace Viewer.Core.Protocol;

/// <summary>
/// Addresses of neighbours the viewer is told not to connect in advance (a development switch for proving the viewer's own connect at a
/// crossing). A held address is refused whenever the library starts a circuit to it, until a crossing needs the region and lets it through.
/// </summary>
internal sealed class HeldBackCircuits
{
    private readonly HashSet<IPEndPoint> _held = new();
    private readonly HashSet<IPEndPoint> _masked = new();

    public int Count
    {
        get { lock (_held) return _held.Count; }
    }

    public void Hold(IPEndPoint address)
    {
        lock (_held) _held.Add(address);
    }

    /// <summary>Whether a circuit to <paramref name="address"/> is refused (never before the agent is in a region).</summary>
    public bool Refuses(IPEndPoint address, bool inRegion)
    {
        if (!inRegion)
            return false;   // the first connect is the agent's own region at login: it always goes through
        lock (_held) return _held.Contains(address);
    }

    /// <summary>A crossing needs this region: its circuit is no longer refused.</summary>
    public void Allow(IPEndPoint address)
    {
        lock (_held) _held.Remove(address);
    }

    /// <summary>Marks an address so that the next crossing into it is treated as a crossing into a region the viewer holds no circuit to
    /// (the circuit, if there is one, is left alone).</summary>
    public void Mask(IPEndPoint address)
    {
        lock (_held) _masked.Add(address);
    }

    /// <summary>True once for a masked address, which is then no longer masked.</summary>
    public bool TakeMask(IPEndPoint address)
    {
        lock (_held) return _masked.Remove(address);
    }

    public void Clear()
    {
        lock (_held)
        {
            _held.Clear();
            _masked.Clear();
        }
    }
}
