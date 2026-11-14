// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Interfaces;
using LibreMetaverse.Messages.Linden;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>
/// Crossing a region border with child circuits to the neighbours: the viewer runs every crossing itself (with neighbours on), as the
/// official viewer does. The library handles the crossing message by connecting to the destination as a new simulator, although the
/// agent may already have a child circuit to it; then the destination never sends a region handshake again, the library gives up after a
/// minute and drops the previous region's world (seen live; in library 3.1.6 its seed-capability retry then recursed until the process
/// died, fixed in the pinned build). The official viewer reuses the destination's region object and tells it the agent has arrived (a
/// complete-agent-movement message); a destination it holds no region object for gets one made first. So here: a destination this
/// adapter holds a circuit to is promoted; one it does not hold is connected as a child by the adapter itself, off the event thread
/// and with a limit, and then promoted the same way; only a connect that fails is left to the library's own crossing (the backstop).
/// Behaviour reference: the official viewer's handling of the crossed-region message in <c>llviewermessage.cpp</c> and its region
/// creation in <c>llworld.cpp</c> (nothing copied).
/// </summary>
internal sealed partial class LmvTransport : IChildCircuitNudger
{
    internal enum CrossingPlan { Library, Promote, Connect }

    private Caps.EventQueueCallback? _libraryCrossing;
    private Simulator? _promoting;

    /// <summary>How a crossing is handled: the library's own, or by making an already connected neighbour the agent's region.</summary>
    internal static CrossingPlan PlanCrossing(bool neighbours, bool destinationHeld) =>
        !neighbours ? CrossingPlan.Library : destinationHeld ? CrossingPlan.Promote : CrossingPlan.Connect;

    /// <summary>The longest the viewer waits for a destination's circuit before leaving the crossing to the library.</summary>
    internal static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(45);

    private CrossingRunner<Simulator>? _runner;
    private readonly CancellationTokenSource _crossings = new();

    private CrossingRunner<Simulator> Runner => _runner ??= new CrossingRunner<Simulator>(ConnectChild, PromoteConnected, LeaveToLibrary, ConnectLimit);

    /// <summary>The library's handler of the crossing message (non-public in the pinned library), or null when it is not there.</summary>
    internal static System.Reflection.MethodInfo? LibraryCrossingHandler() =>
        typeof(AgentManager).GetMethod("CrossedRegionEventHandler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [typeof(string), typeof(IMessage), typeof(Simulator)], null);

    /// <summary>With neighbours on, takes the crossing message from the library's handler (which is still called for every crossing this
    /// adapter does not take over). A library without that handler leaves the crossing entirely to it, and says so.</summary>
    private void TakeOverCrossing(bool neighbours)
    {
        if (!neighbours || _libraryCrossing is not null)
            return;
        // after the library's own handler of the same message: a neighbour it connected from the other announcement first gets the handle here
        _client.Network.RegisterEventCallback("EnableSimulator", OnEnableSimulator);
        TakeOverEstablish();
        var handler = LibraryCrossingHandler();
        if (handler is null)
        {
            _log.Warn("crossing.takeover-unavailable", CoreLog.F("why", "the library's crossing handler was not found"));
            return;
        }
        _libraryCrossing = (Caps.EventQueueCallback)Delegate.CreateDelegate(typeof(Caps.EventQueueCallback), _client.Self, handler);
        _client.Network.UnregisterEventCallback("CrossedRegion", _libraryCrossing);
        _client.Network.RegisterEventCallback("CrossedRegion", OnCrossedRegion);
    }

    /// <summary>The enable-simulator message names a neighbour's handle and size. The library skips it for an address it already holds (a neighbour it
    /// connected first from the establish-agent-communication message, with no handle and the default size), so the handle and size are given
    /// here, and the neighbour is placed (<see cref="LateHandles"/>).</summary>
    private void OnEnableSimulator(string capsKey, IMessage message, Simulator simulator)
    {
        if (_disposed || message is not EnableSimulatorMessage enabled)
            return;
        var blocks = enabled.Simulators.Select(b => new EnabledSim(new System.Net.IPEndPoint(b.IP, b.Port), b.RegionHandle, b.RegionSizeX, b.RegionSizeY)).ToArray();
        var changed = LateHandles.Apply(blocks, a => _client.Network.FindSimulator(a), s => s.Handle, (s, b) =>
        {
            s.Handle = b.Handle;
            if (b.SizeX != 0) s.SizeX = b.SizeX;
            if (b.SizeY != 0) s.SizeY = b.SizeY;
        });
        foreach (var sim in changed)
        {
            _log.Info("circuit.handle-learned", CoreLog.F("size", $"{sim.SizeX}x{sim.SizeY}"));
            _ = Map(sim);   // the reference is made again with the handle: the neighbour is announced (OnSimRefReplaced)
        }
    }

    private Caps.EventQueueCallback? _libraryEstablish;

    /// <summary>The longest the establish message waits for the simulator the enable message is making before the library connects it itself.</summary>
    internal static readonly TimeSpan EstablishWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>Takes the establish-agent-communication message from the library's handler, which connects a neighbour it does not hold yet with no
    /// handle; ours first waits a short while for the enable-simulator message's simulator (<see cref="SimAppearance"/>), then calls the library's.</summary>
    private void TakeOverEstablish()
    {
        if (_libraryEstablish is not null)
            return;
        var handler = typeof(AgentManager).GetMethod("EstablishAgentCommunicationEventHandler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [typeof(string), typeof(IMessage), typeof(Simulator)], null);
        if (handler is null)
        {
            _log.Warn("circuit.establish-takeover-unavailable");
            return;
        }
        _libraryEstablish = (Caps.EventQueueCallback)Delegate.CreateDelegate(typeof(Caps.EventQueueCallback), _client.Self, handler);
        _client.Network.UnregisterEventCallback("EstablishAgentCommunication", _libraryEstablish);
        _client.Network.RegisterEventCallback("EstablishAgentCommunication", OnEstablishAgentCommunication);
    }

    private void OnEstablishAgentCommunication(string capsKey, IMessage message, Simulator simulator)
    {
        if (_disposed)
            return;
        if (message is EstablishAgentCommunicationMessage establish && establish.Address is not null)
        {
            var address = new System.Net.IPEndPoint(establish.Address, establish.Port);
            if (_client.Network.FindSimulator(address) is null)
            {
                var made = SimAppearance.WaitFor(() => _client.Network.FindSimulator(address), EstablishWait, TimeSpan.FromMilliseconds(50), Thread.Sleep);
                _log.Info("circuit.establish-waited", CoreLog.F("found", made is not null));
            }
        }
        _libraryEstablish?.Invoke(capsKey, message, simulator);
    }

    private void OnCrossedRegion(string capsKey, IMessage message, Simulator simulator)
    {
        if (_disposed)
            return;
        Simulator? held = null;
        CrossingTarget? target = null;
        if (message is CrossedRegionMessage crossed)
        {
            var sims = _client.Network.Simulators;
            lock (sims)
                held = sims.FirstOrDefault(s => s.Handle == crossed.RegionHandle && s.Connected && !ReferenceEquals(s, _client.Network.CurrentSim));
            if (held is not null && _heldBack.TakeMask(held.IPEndPoint))
            {
                _log.Info("crossing.not-held-pretended");   // a development aid: the viewer's own connect runs although the circuit is up
                held = null;
            }
            target = CrossingTarget.From(crossed) is { } t ? t with { Context = (capsKey, message, simulator) } : null;
            if (held is not null) Trace.Seeds(crossed.RegionHandle, held.Caps?.SeedCapsURI, target?.Seed);
            Trace.Message(crossed.RegionHandle, Num(crossed.Position), Num(crossed.LookAt));
        }
        switch (PlanCrossing(true, held is not null || target is null))
        {
            case CrossingPlan.Promote when held is not null:
                Promote(held);
                return;
            case CrossingPlan.Connect:
                _log.Info("crossing.connect", CoreLog.F("size", $"{target!.SizeX}x{target.SizeY}"));
                _ = Runner.RunAsync(target, _crossings.Token);   // off this (event) thread: the connect blocks
                return;
        }
        _log.Info("crossing.library", CoreLog.F("why", target is null ? "the message names no address" : "the destination is not a held neighbour"));
        _libraryCrossing?.Invoke(capsKey, message, simulator);
    }

    /// <summary>Opens the destination's circuit as a child (the agent is not moved into it yet) and gives it the seed capability from the
    /// message. Null when the circuit did not come up.</summary>
    private Task<Simulator?> ConnectChild(CrossingTarget target, CancellationToken ct) => Task.Run(() =>
    {
        _heldBack.Allow(target.EndPoint);   // a crossing needs this region: a development hold on it is lifted
        var sim = _client.Network.Connect(target.EndPoint, target.Handle, false, null, target.SizeX == 0 ? Simulator.DefaultRegionSizeX : target.SizeX,
            target.SizeY == 0 ? Simulator.DefaultRegionSizeY : target.SizeY);
        if (sim is null || ct.IsCancellationRequested || !sim.Connected)
            return null;
        lock (_client.Network.Simulators)
            if (!_client.Network.Simulators.Contains(sim))
                return null;   // the library removed it (no region handshake came)
        if (target.Seed is not null)
            sim.SetSeedCaps(target.Seed, true);
        return sim;
    }, ct);

    private void PromoteConnected(Simulator sim, CrossingTarget target) => Promote(sim);

    /// <summary>The viewer could not make the destination's circuit: the library's own crossing runs (its seed retry is bounded now, and a
    /// failure there is a simulator disconnect the session handles).</summary>
    private void LeaveToLibrary(CrossingTarget target, string why)
    {
        _log.Warn("crossing.library", CoreLog.F("why", "the viewer's own connect failed: " + why));
        if (target.Context is (string key, IMessage message, Simulator from) && !_disposed)
            _libraryCrossing?.Invoke(key, message, from);
    }

    /// <summary>Asks a child circuit for data again: an agent update sent to that circuit alone.</summary>
    public void Nudge(SimRef sim)
    {
        if (_disposed)
            return;
        Simulator? target;
        var sims = _client.Network.Simulators;
        lock (sims) target = sims.FirstOrDefault(s => s.Handle == sim.Handle && s.Connected);
        if (target is null)
            return;
        lock (_movementLock)
            _client.Self.Movement.SendUpdate(true, target);
    }

    /// <summary>The agent has crossed into a neighbour this client holds a circuit to: it becomes the agent's region and is told the agent
    /// has arrived; the session is told the same way it is told of any arrival.</summary>
    private void Promote(Simulator neighbour)
    {
        _log.Info("crossing.promote");
        _promoting = neighbour;
        try
        {
            _client.Network.CurrentSim = neighbour;
            _client.Self.CompleteAgentMovement(neighbour);
            Announce(neighbour, reArrival: true);
            Trace.Promoted(neighbour.Handle, neighbour.Name ?? "", Num(_client.Self.SimPosition), _client.Network.Simulators.Count);
        }
        finally { _promoting = null; }
    }
}
