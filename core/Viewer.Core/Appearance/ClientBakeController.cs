// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.World;

namespace Viewer.Core.Appearance;

/// <summary>What a region announcement did to the client-bake work.</summary>
internal enum BakeRegionAction { None, Aborted, Started, Resent }

/// <summary>
/// Decides, at every region the agent arrives in, whether this viewer bakes its own look. A region that announces the agent
/// appearance service (the handshake's protocol bit 0) bakes on the server and its bake is the one that counts, so nothing is
/// baked or sent for it; any other region needs the viewer to bake and send its own look (the OpenSim-aware Firestorm does
/// the same: it sends nothing while the region reports a central bake version, llagent.cpp sendAgentSetAppearance).
/// A bake that is still running when the agent crosses into a server-baked region is stopped before it can send.
/// </summary>
internal sealed class ClientBakeController
{
    private readonly object _lock = new();
    private readonly Func<string, CancellationToken, Task> _bake;
    private readonly Func<bool> _resendLast;
    private readonly CoreLog? _log;
    private AppearanceMode _mode = AppearanceMode.Unknown;
    private CancellationTokenSource? _current;
    private Task _tail = Task.CompletedTask;
    private int _active;

    /// <param name="bake">One bake run; it sends its finished look only through <see cref="TrySend"/>.</param>
    /// <param name="resendLast">Sends the look already made again (a new region does not know it); false when there is none.</param>
    public ClientBakeController(Func<string, CancellationToken, Task> bake, Func<bool> resendLast, CoreLog? log = null)
    {
        _bake = bake;
        _resendLast = resendLast;
        _log = log;
    }

    /// <summary>The mode of the region the agent is in.</summary>
    public AppearanceMode Mode { get { lock (_lock) return _mode; } }

    /// <summary>A bake run is in progress.</summary>
    public bool Running { get { lock (_lock) return _active > 0; } }

    /// <summary>The agent arrived in a region (every connect, login or teleport or crossing).</summary>
    public BakeRegionAction OnRegion(string region, bool serverBaked)
    {
        lock (_lock)
        {
            if (serverBaked)
            {
                var was = _active > 0;
                _mode = AppearanceMode.Server;
                CancelLocked();
                _log?.Info("bake.region", CoreLog.F("region", region), CoreLog.F("mode", "server"), CoreLog.F("aborted", was));
                return was ? BakeRegionAction.Aborted : BakeRegionAction.None;
            }
            _mode = AppearanceMode.Client;
            if (_active > 0)
            {
                CancelLocked();   // a run made for the region just left would be sent to this one half done
                StartLocked("region");
                _log?.Info("bake.region", CoreLog.F("region", region), CoreLog.F("mode", "client"), CoreLog.F("action", "restarted"));
                return BakeRegionAction.Started;
            }
            if (_resendLast())
            {
                _log?.Info("bake.region", CoreLog.F("region", region), CoreLog.F("mode", "client"), CoreLog.F("action", "resent"));
                return BakeRegionAction.Resent;
            }
            StartLocked("region");
            _log?.Info("bake.region", CoreLog.F("region", region), CoreLog.F("mode", "client"), CoreLog.F("action", "started"));
            return BakeRegionAction.Started;
        }
    }

    /// <summary>The worn set changed (or the region asked for a rebake): bake again. False, and nothing done, in a server-baked region.</summary>
    public bool RequestBake(string why)
    {
        lock (_lock)
        {
            if (_mode != AppearanceMode.Client)
                return false;
            CancelLocked();   // the run in progress started from the old worn set
            StartLocked(why);
            return true;
        }
    }

    /// <summary>The one place a look is sent from: runs <paramref name="send"/> only while the region is a client-bake region and
    /// the run that asks has not been stopped. Returns whether it ran.</summary>
    public bool TrySend(CancellationToken run, Func<bool> send)
    {
        lock (_lock)
        {
            if (_mode != AppearanceMode.Client || run.IsCancellationRequested)
                return false;
            return send();
        }
    }

    /// <summary>Completes when no run is in progress.</summary>
    public Task Idle()
    {
        lock (_lock) return _tail;
    }

    private void CancelLocked()
    {
        var c = _current;
        _current = null;
        try { c?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void StartLocked(string why)
    {
        var cts = new CancellationTokenSource();
        _current = cts;
        var token = cts.Token;
        var before = _tail;
        _active++;
        _tail = Task.Run(async () =>
        {
            try
            {
                try { await before.ConfigureAwait(false); }
                catch (Exception) { /* the earlier run's end is its own */ }
                if (token.IsCancellationRequested)
                    return;
                await _bake(why, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log?.Warn("bake.failed", CoreLog.F("error", ex.GetType().Name));
            }
            finally
            {
                lock (_lock) _active--;
            }
        });
    }
}
