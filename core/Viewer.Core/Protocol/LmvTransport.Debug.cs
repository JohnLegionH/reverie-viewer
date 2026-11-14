// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Packets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using N = System.Numerics;

namespace Viewer.Core.Protocol;

/// <summary>The development aids of <see cref="INeighbourControl"/>: the crossing record and the neighbours held back from an advance connect.</summary>
internal sealed partial class LmvTransport : INeighbourControl
{
    private readonly HeldBackCircuits _heldBack = new();
    private CrossingTrace? _trace;

    private CrossingTrace Trace => _trace ??= new CrossingTrace(_log, () => DateTime.UtcNow);

    private static N.Vector3 Num(Vector3 v) => new(v.X, v.Y, v.Z);

    private static float YawOf(Quaternion q) => MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z));

    public void SetCrossingTrace(bool on) => Trace.Enabled = on;

    private volatile bool _agentTrace;

    public void SetAgentUpdateTrace(bool on) => _agentTrace = on;

    /// <summary>The trace line for an agent update about to be written to the socket (when the agent trace is on).</summary>
    private void TraceAgentUpdate(AgentUpdatePacket packet, bool connected)
    {
        if (!_agentTrace)
            return;
        var d = packet.AgentData;
        float dot = MathF.Abs(d.BodyRotation.X * d.HeadRotation.X + d.BodyRotation.Y * d.HeadRotation.Y + d.BodyRotation.Z * d.HeadRotation.Z + d.BodyRotation.W * d.HeadRotation.W);
        _log.Info("agent.update.sent", CoreLog.F("t_ms", Environment.TickCount64), CoreLog.F("controls", d.ControlFlags), CoreLog.F("state", d.State),
            CoreLog.F("flags", d.Flags), CoreLog.F("head_body_dot", MathF.Round(dot, 5)), CoreLog.F("far", d.Far), CoreLog.F("reliable", packet.Header.Reliable), CoreLog.F("connected", connected));
    }

    public bool PretendNotHeld(string address)
    {
        if (!System.Net.IPEndPoint.TryParse(address, out var parsed))
            return false;
        _heldBack.Mask(parsed);
        return true;
    }

    public bool HoldAtLogin(string address)
    {
        if (!System.Net.IPEndPoint.TryParse(address, out var parsed))
            return false;
        _heldBack.Hold(parsed);
        return true;
    }

    /// <summary>Tests: whether a circuit to the address is refused once the agent is in a region.</summary>
    internal bool IsHeldBack(System.Net.IPEndPoint address) => _heldBack.Refuses(address, true);

    public void ReleaseHolds() => _heldBack.Clear();

    /// <summary>The crossing record for an agent update about to go to the agent's current region.</summary>
    private void TraceUpdate(AgentManager.AgentMovement mv)
    {
        if (_trace is not { Enabled: true } trace || _client.Network.CurrentSim is not { } sim)
            return;
        trace.Update(sim.Handle, sim.AgentMovementComplete, mv.AgentControls, YawOf(mv.BodyRotation), Num(mv.Camera.Position), Num(_client.Self.SimPosition));
    }
}
