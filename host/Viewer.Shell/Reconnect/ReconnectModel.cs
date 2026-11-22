// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Shell.Reconnect;

// The notice follows the official viewer; the reconnect goes beyond it and is the user's click. A login the grid refuses because it still
// holds the old session (the official viewer ends that attempt and shows the grid's text: lllogininstance.cpp, "presence") is waited out
// here with a countdown and retried at the same spot, a few times at most, and the user can cancel.

/// <summary>The words and buttons of the notice shown when the grid ended the session unasked.</summary>
/// <param name="Reason">The one-line reason in words (the library's timeout, a kick's own text, a shutdown).</param>
public sealed record DropNotice(string Title, string Body, string Reason, bool CanReconnect);

/// <summary>Makes the notice for a session that ended; nothing for one the user ended.</summary>
public static class DropNotices
{
    public static DropNotice? For(ViewerSession session, StringTable strings, bool canReconnect)
    {
        if (session.EndReason != SessionEndReason.Disconnected || session.Drop is not { } drop)
            return null;
        string reason = drop.Kind switch
        {
            DisconnectKind.NetworkTimeout => strings.Get("drop.reason.timeout"),
            DisconnectKind.SimShutdown => strings.Get("drop.reason.shutdown"),
            DisconnectKind.ServerInitiated => KickReason(drop.Message.Trim(), strings),
            _ => strings.Get("drop.reason.unknown"),
        };
        return new DropNotice(strings.Get("drop.title"), strings.Get("drop.body"), reason, canReconnect);
    }

    /// <summary>The grid's own words; a reason that is one of the teleport failure keys is shown as its words (the official viewer looks the text up in the same table).</summary>
    private static string KickReason(string message, StringTable strings)
    {
        if (message.Length == 0)
            return strings.Get("drop.reason.unknown");
        return strings.Has("teleport.error." + message) ? strings.Get("teleport.error." + message, "") : message;
    }
}

/// <summary>Where a click on Reconnect has got to.</summary>
public enum ReconnectState { Idle, Trying, Failed, Succeeded }

/// <summary>
/// Reconnect after a dropped session. Holds the last successful login's credentials IN MEMORY ONLY (never written, never logged) until
/// <see cref="Forget"/>, and on a click logs in again: at most <see cref="ReconnectPlan.MaxAttempts"/> attempts, the same spot first,
/// then the grid's last location; a credential refusal ends it at once. A refusal for a held session is retried at the same spot after a
/// countdown, at most <see cref="ReconnectPlan.PresenceRetries"/> times and within <see cref="ReconnectPlan.PresenceBudget"/>, and
/// <see cref="Cancel"/> ends the wait. Never starts by itself. Main thread only.
/// </summary>
public sealed class ReconnectModel
{
    private readonly LoginModel _login;
    private readonly StringTable _strings;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private LoginRecord? _kept;
    private ReconnectState _state = ReconnectState.Idle;
    private CancellationTokenSource? _cancel;
    private bool _cancelled;

    public ReconnectModel(LoginModel login, StringTable strings, Func<TimeSpan, CancellationToken, Task> delay, Func<DateTimeOffset>? clock = null)
    {
        _login = login;
        _strings = strings;
        _delay = delay;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        login.Succeeded += record => { lock (_gate) _kept = record; };
    }

    /// <summary>Whether the credentials of a successful login are held.</summary>
    public bool HasLogin { get { lock (_gate) return _kept is not null; } }

    public ReconnectState State { get { lock (_gate) return _state; } }

    /// <summary>What the last click did, in words from the string table.</summary>
    public string Status { get; private set; } = "";

    /// <summary>The login attempts the last click made.</summary>
    public int Attempts { get; private set; }

    public event Action? Changed;

    /// <summary>The seconds left before the next try while the grid still holds the old session (0 when not waiting).</summary>
    public int Countdown { get; private set; }

    /// <summary>Ends the wait and any further tries at once. A login already sent is not taken back: if it succeeds, the session is used.
    /// Does nothing when no click is going on.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_state != ReconnectState.Trying)
                return;
            _cancelled = true;
            _cancel?.Cancel();
        }
    }

    /// <summary>Drops the held credentials (the user logged out, quit or is about to log in as someone else).</summary>
    public void Forget()
    {
        lock (_gate) _kept = null;
    }

    private void Set(ReconnectState state, string status)
    {
        lock (_gate) _state = state;
        Status = status;
        Changed?.Invoke();
    }

    /// <summary>Logs in again at the home location with the held login (the offer made when the home grid refused the teleport home
    /// during a Hypergrid visit; the session has been logged out first). False, with nothing sent, when no login is held: the login screen
    /// then asks, with its start set to home by the caller.</summary>
    public Task<bool> HomeAsync(CancellationToken ct) => RunAsync(null, ct, [HomeStart]);

    /// <summary>The login start that means the home location (the login service's "home").</summary>
    public const string HomeStart = "home";

    /// <summary>One click on Reconnect. True when a session is back. A second click while one is going on does nothing.</summary>
    /// <param name="starts">The start locations to try in order; the reconnect plan's for <paramref name="drop"/> when null.</param>
    public async Task<bool> RunAsync(DropInfo? drop, CancellationToken ct, IReadOnlyList<string>? starts = null)
    {
        LoginRecord? kept;
        lock (_gate)
        {
            if (_state == ReconnectState.Trying)
                return false;
            kept = _kept;
            if (kept is not null)
                _state = ReconnectState.Trying;
        }
        Attempts = 0;
        Countdown = 0;
        if (kept is null)
        {
            Set(ReconnectState.Failed, _strings.Get("drop.no_credentials"));
            return false;
        }
        starts ??= ReconnectPlan.StartLocations(drop);
        var previous = _login.Session;
        string startBefore = _login.StartLocation;
        var began = _clock();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _cancelled = false;
            _cancel = cancel;
        }
        try
        {
            for (int i = 0; i < starts.Count && i < ReconnectPlan.MaxAttempts; i++)
            {
                int retries = 0;
                while (true)
                {
                    Attempts++;
                    Set(ReconnectState.Trying, _strings.Get("drop.reconnecting", i + 1, starts.Count));
                    _login.FirstName = kept.FirstName;
                    _login.LastName = kept.LastName;
                    _login.LoginUri = kept.LoginUri;
                    _login.StartLocation = starts[i];
                    _login.SetPassword(kept.Password);
                    if (await _login.LoginAsync().ConfigureAwait(false))
                    {
                        if (previous is { IsEnded: true } && !ReferenceEquals(previous, _login.Session))
                            previous.Dispose();   // the ended session's resources go now that its replacement is in
                        Set(ReconnectState.Succeeded, "");
                        return true;
                    }
                    if (ReconnectPlan.StopsReconnect(_login.LastFailureCode) || cancel.IsCancellationRequested)
                        return Ended();
                    if (_login.LastFailureCode != ReconnectPlan.PresenceCode)
                    {
                        if (i + 1 < starts.Count)
                            await PauseAsync(ReconnectPlan.Pause, cancel.Token).ConfigureAwait(false);
                        if (cancel.IsCancellationRequested)
                            return Ended();
                        break;   // the next start location
                    }
                    // the grid still holds the old session: wait it out and try the same spot again, within the retry count and the time budget
                    if (retries >= ReconnectPlan.PresenceRetries || _clock() - began + ReconnectPlan.PresenceWait > ReconnectPlan.PresenceBudget)
                        return Ended();
                    retries++;
                    if (!await CountDownAsync(retries, cancel.Token).ConfigureAwait(false))
                        return Ended();
                }
            }
            return Ended();
        }
        finally
        {
            lock (_gate) _cancel = null;
            _login.StartLocation = startBefore;
        }
    }

    /// <summary>The click is over without a session: cancelled (by <see cref="Cancel"/>) or failed with the last refusal's words.</summary>
    private bool Ended()
    {
        bool cancelled;
        lock (_gate) cancelled = _cancelled;
        Countdown = 0;
        Set(ReconnectState.Failed, cancelled ? _strings.Get("drop.cancelled") : _strings.Get("drop.failed", _login.Status));
        return false;
    }

    private async Task PauseAsync(TimeSpan time, CancellationToken token)
    {
        try { await _delay(time, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>Counts <see cref="ReconnectPlan.PresenceWait"/> down in one-second steps, showing the seconds left. False when cancelled.</summary>
    private async Task<bool> CountDownAsync(int retry, CancellationToken token)
    {
        int seconds = (int)ReconnectPlan.PresenceWait.TotalSeconds;
        for (int left = seconds; left >= 1; left--)
        {
            Countdown = left;
            Set(ReconnectState.Trying, _strings.Get("drop.presence_wait", left, retry, ReconnectPlan.PresenceRetries));
            await PauseAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
                return false;
        }
        Countdown = 0;
        return true;
    }
}
