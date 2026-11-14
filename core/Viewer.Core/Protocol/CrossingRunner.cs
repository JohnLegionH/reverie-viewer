// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using LibreMetaverse.Messages.Linden;

namespace Viewer.Core.Protocol;

/// <summary>Where a crossed-region message sends the agent: the destination's address, handle, size and seed capability.</summary>
internal sealed record CrossingTarget(IPEndPoint EndPoint, ulong Handle, Uri? Seed, uint SizeX, uint SizeY)
{
    /// <summary>What the crossing was made from, for the backstop (the message and the simulator it came through).</summary>
    internal object? Context { get; init; }

    /// <summary>The target a crossed-region message names; null when it names no usable address.</summary>
    internal static CrossingTarget? From(CrossedRegionMessage message)
    {
        if (message.IP is null || message.Port <= 0 || message.Port > ushort.MaxValue)
            return null;
        return new CrossingTarget(new IPEndPoint(message.IP, message.Port), message.RegionHandle, message.SeedCapability, message.RegionSizeX, message.RegionSizeY);
    }
}

/// <summary>How a crossing the viewer runs ended.</summary>
internal enum CrossingOutcome
{
    None = 0,
    /// <summary>The destination's circuit is up and the agent has been moved into it.</summary>
    Promoted,
    /// <summary>The viewer could not make the circuit; the library's own crossing is left to try (and report).</summary>
    Backstop,
    /// <summary>A crossing to the same destination was already running; this message started nothing.</summary>
    Duplicate,
}

/// <summary>
/// Runs a crossing into a region the viewer holds no circuit to: opens the circuit (a child one), then makes it the agent's region.
/// One run per destination at a time; a failed, slow or throwing connect hands the crossing to the backstop instead of leaving the
/// agent in a region nobody is connected to. Engine- and library-free: the three steps are given.
/// </summary>
internal sealed class CrossingRunner<TSim> where TSim : class
{
    private readonly Func<CrossingTarget, CancellationToken, Task<TSim?>> _connect;
    private readonly Action<TSim, CrossingTarget> _promote;
    private readonly Action<CrossingTarget, string> _backstop;
    private readonly TimeSpan _limit;
    private readonly HashSet<ulong> _running = new();

    public CrossingRunner(Func<CrossingTarget, CancellationToken, Task<TSim?>> connect, Action<TSim, CrossingTarget> promote,
        Action<CrossingTarget, string> backstop, TimeSpan limit)
    {
        _connect = connect;
        _promote = promote;
        _backstop = backstop;
        _limit = limit;
    }

    public async Task<CrossingOutcome> RunAsync(CrossingTarget target, CancellationToken ct)
    {
        lock (_running)
        {
            if (!_running.Add(target.Handle))
                return CrossingOutcome.Duplicate;
        }
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task<TSim?> connecting;
            try { connecting = Task.Run(() => _connect(target, attempt.Token), CancellationToken.None); }
            catch (Exception ex) { return Fail(target, ex.GetType().Name); }
            _ = connecting.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);   // never unobserved, whoever waits

            Task finished;
            try { finished = await Task.WhenAny(connecting, Task.Delay(_limit, ct)).ConfigureAwait(false); }
            catch (OperationCanceledException) { attempt.Cancel(); return CrossingOutcome.None; }   // the transport is going away
            if (!ReferenceEquals(finished, connecting))
            {
                attempt.Cancel();
                return Fail(target, "the circuit was not up in time");
            }
            TSim? sim;
            try { sim = await connecting.ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return CrossingOutcome.None; }
            catch (Exception ex) { return Fail(target, ex.GetType().Name); }
            if (sim is null)
                return Fail(target, "no circuit");
            try { _promote(sim, target); }
            catch (Exception ex) { return Fail(target, "promote: " + ex.GetType().Name); }
            return CrossingOutcome.Promoted;
        }
        finally
        {
            lock (_running) _running.Remove(target.Handle);
        }
    }

    private CrossingOutcome Fail(CrossingTarget target, string why)
    {
        _backstop(target, why);
        return CrossingOutcome.Backstop;
    }
}
