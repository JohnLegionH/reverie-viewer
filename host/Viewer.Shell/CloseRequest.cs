// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Shell;

/// <summary>
/// What closing the viewer (the window's close request, or the automation API's quit) does with the session: a session
/// still logged in is logged out first, so the region removes the avatar at once instead of keeping it until the
/// connection times out (when a quick login of the same account can be refused for the presence left behind).
///   - in world: the logout is sent and the viewer waits for the grid's reply, at most <see cref="LogoutWait"/>, then quits
///     whether or not the reply came;
///   - a second close while waiting quits at once;
///   - a login in progress is cancelled, and with no session (or one already ended) the viewer quits at once.
/// The host polls <see cref="ReadyToQuit"/> on its main thread and quits there. A crash cannot log out: the avatar stays
/// in the region until the grid's own timeout drops it.
/// </summary>
public sealed class CloseRequest(Func<ViewerSession?> session, Action cancelAttempt, CoreLog log)
{
    /// <summary>The longest wait for the grid's logout reply. The official viewer waits the same 6 s on quit (the reply
    /// usually ends it far sooner); long enough for a grid across the internet, short enough that a window that does not
    /// close at once still feels like it is closing.</summary>
    public static TimeSpan LogoutWait { get; } = TimeSpan.FromSeconds(6);

    public TimeSpan Wait { get; init; } = LogoutWait;

    /// <summary>The bounded wait (tests pass a controllable one).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    private readonly object _lock = new();
    private bool _ready;
    private bool _loggingOut;

    public bool ReadyToQuit { get { lock (_lock) return _ready; } }

    /// <summary>True from the first close until the logout has finished or the wait ran out.</summary>
    public bool LoggingOut { get { lock (_lock) return _loggingOut && !_ready; } }

    public void Request()
    {
        ViewerSession? s;
        lock (_lock)
        {
            if (_ready)
                return;
            if (_loggingOut)
            {
                _ready = true;
                log.Info("close.forced");
                return;
            }
            s = session();
            if (s is null || s.IsEnded)
            {
                _ready = true;
            }
            else
            {
                _loggingOut = true;
            }
        }
        cancelAttempt();
        log.Info("close.request", CoreLog.F("logout", s is { IsEnded: false }), CoreLog.F("wait-seconds", Wait.TotalSeconds));
        if (s is { IsEnded: false })
            _ = LogoutAsync(s);
    }

    private async Task LogoutAsync(ViewerSession s)
    {
        using var stop = new CancellationTokenSource();
        var logout = s.LogoutAsync(stop.Token);
        var bound = Delay(Wait, stop.Token);
        try
        {
            var first = await Task.WhenAny(logout, bound).ConfigureAwait(false);
            if (first != logout)
                log.Info("close.logout_wait_over", CoreLog.F("wait-seconds", Wait.TotalSeconds));
        }
        finally
        {
            stop.Cancel();   // the logout call (if still waiting) and the bound both stop here
            lock (_lock) _ready = true;
        }
    }
}
