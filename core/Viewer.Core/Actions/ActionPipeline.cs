// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Core.Actions;

/// <summary>A held intent that has begun. Ended once, by whichever comes first: the caller, a revocation, or the session.</summary>
public sealed class HeldAction
{
    private readonly object _state = new();
    private HoldEnd? _endedBy;

    public long Id { get; internal set; }

    /// <summary>The intent as last begun or continued (a continuation may change it).</summary>
    public HeldIntent Intent { get; internal set; } = null!;

    public bool IsActive { get { lock (_state) return _endedBy is null; } }

    public HoldEnd? EndedBy { get { lock (_state) return _endedBy; } }

    internal IHeldSink Sink { get; set; } = null!;

    /// <summary>Serialises the sink's calls for this hold, so a revocation from another thread never overlaps a tick.</summary>
    internal object Gate { get; } = new();

    /// <summary>Marks the hold ended; true only for the one call that did it.</summary>
    internal bool TryEnd(HoldEnd reason)
    {
        lock (_state)
        {
            if (_endedBy is not null)
                return false;
            _endedBy = reason;
            return true;
        }
    }
}

/// <summary>
/// The single path from the interface to the grid (ADR-015). An intent is checked (<see cref="ActionIntent.Validate"/>), then
/// tried against the session (in world, with a command side), then offered to the veto points in order, and only then handed
/// to <see cref="ICommandTransport"/>, once. A held intent (movement) begins the same way, is re-checked on every
/// <see cref="Continue"/> tick and the moment a changing veto says its answers moved, and ends exactly once.
/// <para>What it does not do: transport maintenance (acknowledgements, retransmission, pings, circuit upkeep, the repeated
/// AgentUpdate traffic of a hold) belongs to the protocol adapter and never waits here. It does not retry: "submit once" is
/// a local invariant only, and a send that threw is recorded as unknown and left for the caller to reconcile with an
/// authoritative read. It never logs a message body, a name or an id of another person.</para>
/// </summary>
public sealed class ActionPipeline : IDisposable
{
    private readonly Func<ICommandTransport?> _transport;
    private readonly Func<bool> _inWorld;
    private readonly CoreLog _log;
    private readonly object _lock = new();
    private IActionVeto[] _vetoes = [];
    private IActionRewriter[] _rewriters = [];
    private readonly List<HeldAction> _holds = new();
    private readonly SortedSet<long> _unreconciled = new();
    private long _nextId;
    private bool _disposed;

    /// <param name="transport">The session's command side now, or null when its transport has none.</param>
    /// <param name="inWorld">True while the session is in world and not ending.</param>
    public ActionPipeline(Func<ICommandTransport?> transport, Func<bool> inWorld, CoreLog log)
    {
        _transport = transport;
        _inWorld = inWorld;
        _log = log.For("actions");
    }

    /// <summary>Runs the commands that are the viewer's own (<see cref="ILocalCommand"/>) instead of handing them to the grid transport. They are
    /// checked and vetoed like any other intent. Null: such a command is unavailable.</summary>
    public Func<TransportCommand, CommandDispatch>? LocalExecutor { get; set; }

    /// <summary>Adds a veto point, asked after the ones already added. One that can change its mind while a hold runs
    /// (<see cref="IChangingVeto"/>) also gets every active hold re-checked the moment it says so.</summary>
    public void AddVeto(IActionVeto veto)
    {
        ArgumentNullException.ThrowIfNull(veto);
        lock (_lock) _vetoes = [.. _vetoes, veto];
        if (veto is IChangingVeto changing)
            changing.Changed += RecheckHolds;
    }

    /// <summary>Adds a rewriter, asked after the ones already added, before an intent is checked and offered to the veto points.</summary>
    public void AddRewriter(IActionRewriter rewriter)
    {
        ArgumentNullException.ThrowIfNull(rewriter);
        lock (_lock) _rewriters = [.. _rewriters, rewriter];
    }

    /// <summary>The intent after every rewriter: itself, another in its place, or null (refused with the rewriter's code). A
    /// rewriter that throws leaves the intent as it was.</summary>
    private ActionIntent? Rewritten(ActionIntent intent, out string? code)
    {
        code = null;
        IActionRewriter[] rewriters;
        lock (_lock) rewriters = _rewriters;
        foreach (var rewriter in rewriters)
        {
            ActionIntent? next;
            string? why;
            try { next = rewriter.Rewrite(intent, out why); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("action.rewrite_error", CoreLog.F("kind", intent.Kind), CoreLog.F("error", ex.GetType().Name));
                continue;
            }
            if (next is null)
            {
                code = why ?? "rewritten";
                return null;
            }
            intent = next;
        }
        return intent;
    }

    // ---------------- one-off intents ----------------

    /// <summary>Checks, tries and sends one intent. Never throws for a bad intent or a failing transport: the result says what happened.</summary>
    public ActionResult Submit(ActionIntent intent)
    {
        if (intent is null)
            return Record(null, new ActionResult(ActionStatus.Invalid, 0, null, [new("action.null")]));
        if (intent.IsHeld)
            return Record(intent, new ActionResult(ActionStatus.Invalid, 0, null, [new("action.held_intent")]));
        if (Rewritten(intent, out string? dropped) is not { } rewritten)
            return Record(intent, new ActionResult(ActionStatus.Vetoed, 0, dropped));
        intent = rewritten;
        var refused = Refuse(intent);
        if (refused is { } r0)
            return Record(intent, r0);

        var transport = _transport();
        var local = LocalExecutor;
        var command = intent.ToCommand();
        bool isLocal = command is ILocalCommand;
        if (!isLocal && transport is null)
            return Record(intent, new ActionResult(ActionStatus.Unavailable, 0, "no_command_transport"));
        var veto = Vet(intent, ActionPhase.Submit);
        if (!veto.Allowed)
            return Record(intent, new ActionResult(ActionStatus.Vetoed, 0, veto.Code));
        if (command is null)
            return Record(intent, new ActionResult(ActionStatus.Failed, 0, "no_command"));
        if (isLocal && local is null)
            return Record(intent, new ActionResult(ActionStatus.Unavailable, 0, "no_local_executor"));
        long id = Interlocked.Increment(ref _nextId);
        try
        {
            var dispatch = isLocal ? local!(command) : transport!.Execute(command);
            return Record(intent, dispatch.Sent
                ? new ActionResult(ActionStatus.Sent, id)
                : new ActionResult(ActionStatus.Failed, id, dispatch.Reason ?? "not_sent"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_lock) _unreconciled.Add(id);
            _log.Warn("action.outcome_unknown", CoreLog.F("id", id), CoreLog.F("kind", intent.Kind), CoreLog.F("error", ex.GetType().Name));
            return Record(intent, new ActionResult(ActionStatus.OutcomeUnknown, id, "send_threw"));
        }
    }

    /// <summary>The submissions whose outcome is unknown (the send threw), oldest first. Each stays until the caller has read the
    /// grid's state and calls <see cref="Reconciled"/>.</summary>
    public IReadOnlyList<long> Unreconciled
    {
        get { lock (_lock) return _unreconciled.ToArray(); }
    }

    /// <summary>Marks an unknown outcome as settled by an authoritative read. False when there was no such outstanding submission.</summary>
    public bool Reconciled(long submissionId)
    {
        lock (_lock) return _unreconciled.Remove(submissionId);
    }

    // ---------------- held intents ----------------

    /// <summary>Checks and begins a held intent. On <see cref="ActionStatus.Sent"/> <paramref name="hold"/> is the running hold;
    /// otherwise null and nothing was started.</summary>
    public ActionResult BeginHold(HeldIntent intent, IHeldSink sink, out HeldAction? hold)
    {
        hold = null;
        if (intent is null || sink is null)
            return Record(intent, new ActionResult(ActionStatus.Invalid, 0, null, [new("action.null")]));
        if (!intent.IsHeld)
            return Record(intent, new ActionResult(ActionStatus.Invalid, 0, null, [new("action.not_held")]));
        var refused = Refuse(intent);
        if (refused is { } r0)
            return Record(intent, r0);
        var veto = Vet(intent, ActionPhase.Begin);
        if (!veto.Allowed)
            return Record(intent, new ActionResult(ActionStatus.Vetoed, 0, veto.Code));

        var started = new HeldAction { Id = Interlocked.Increment(ref _nextId), Intent = intent, Sink = sink };
        lock (_lock)
        {
            if (_disposed)
                return Record(intent, new ActionResult(ActionStatus.Unavailable, 0, "disposed"));
            _holds.Add(started);
        }
        try
        {
            lock (started.Gate) sink.Begin(intent);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            EndHold(started, HoldEnd.Failed, callSink: false);
            _log.Warn("action.hold_failed", CoreLog.F("id", started.Id), CoreLog.F("kind", intent.Kind), CoreLog.F("error", ex.GetType().Name));
            return Record(intent, new ActionResult(ActionStatus.Failed, started.Id, "sink_error"));
        }
        hold = started;
        return Record(intent, new ActionResult(ActionStatus.Sent, started.Id));
    }

    /// <summary>One tick of a hold: re-checks it against the session and every veto point, then lets the sink continue. False (and
    /// the hold ended) when anything now refuses it; the sink is not called again. <paramref name="changed"/> replaces the held
    /// intent (new control bits), and is checked as a new one would be.</summary>
    public bool Continue(HeldAction hold, HeldIntent? changed = null)
    {
        if (hold is null || !hold.IsActive)
            return false;
        bool disposed;
        lock (_lock) disposed = _disposed;
        if (disposed || !_inWorld())
        {
            EndHold(hold, HoldEnd.SessionEnded);
            return false;
        }
        var next = changed ?? hold.Intent;
        if (changed is not null && (changed.Kind != hold.Intent.Kind || changed.Validate().Count > 0))
        {
            EndHold(hold, HoldEnd.Failed);
            return false;
        }
        if (!Vet(next, ActionPhase.Continue).Allowed)
        {
            EndHold(hold, HoldEnd.Revoked);
            return false;
        }
        try
        {
            lock (hold.Gate)
            {
                if (!hold.IsActive)
                    return false;
                hold.Intent = next;
                hold.Sink.Continue(next);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("action.hold_failed", CoreLog.F("id", hold.Id), CoreLog.F("kind", next.Kind), CoreLog.F("error", ex.GetType().Name));
            EndHold(hold, HoldEnd.Failed);
            return false;
        }
        return hold.IsActive;
    }

    /// <summary>The caller lets go. Ending twice ends once.</summary>
    public void End(HeldAction hold)
    {
        if (hold is not null)
            EndHold(hold, HoldEnd.Released);
    }

    /// <summary>Ends every active hold with <paramref name="reason"/> (the session ended, or the pipeline is shutting down).</summary>
    public void EndAllHolds(HoldEnd reason)
    {
        HeldAction[] all;
        lock (_lock) all = _holds.ToArray();
        foreach (var h in all)
            EndHold(h, reason);
    }

    public int ActiveHolds
    {
        get { lock (_lock) return _holds.Count; }
    }

    /// <summary>Ends the holds and refuses new work.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        EndAllHolds(HoldEnd.SessionEnded);
    }

    // ---------------- internals ----------------

    /// <summary>Re-checks every active hold; the ones a veto now refuses end at once.</summary>
    private void RecheckHolds()
    {
        HeldAction[] all;
        lock (_lock) all = _holds.ToArray();
        foreach (var h in all)
            if (h.IsActive && !Vet(h.Intent, ActionPhase.Continue).Allowed)
                EndHold(h, HoldEnd.Revoked);
    }

    private void EndHold(HeldAction hold, HoldEnd reason, bool callSink = true)
    {
        if (!hold.TryEnd(reason))
            return;
        lock (_lock) _holds.Remove(hold);
        if (callSink)
        {
            try { lock (hold.Gate) hold.Sink.End(hold.Intent, reason); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("action.hold_end_failed", CoreLog.F("id", hold.Id), CoreLog.F("error", ex.GetType().Name));
            }
        }
        _log.Info("action.hold_end", CoreLog.F("id", hold.Id), CoreLog.F("kind", hold.Intent.Kind), CoreLog.F("reason", reason));
    }

    /// <summary>The checks every intent passes before a veto is asked: valid, the pipeline alive, the session in world.</summary>
    private ActionResult? Refuse(ActionIntent intent)
    {
        IReadOnlyList<ActionIssue> issues;
        try { issues = intent.Validate(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ActionResult(ActionStatus.Invalid, 0, null, [new("action.validate_failed", ex.GetType().Name)]);
        }
        if (issues.Count > 0)
            return new ActionResult(ActionStatus.Invalid, 0, null, issues);
        bool disposed;
        lock (_lock) disposed = _disposed;
        if (disposed)
            return new ActionResult(ActionStatus.Unavailable, 0, "disposed");
        if (!_inWorld())
            return new ActionResult(ActionStatus.Unavailable, 0, "not_in_world");
        return null;
    }

    private VetoDecision Vet(ActionIntent intent, ActionPhase phase)
    {
        IActionVeto[] vetoes;
        lock (_lock) vetoes = _vetoes;
        foreach (var v in vetoes)
        {
            VetoDecision d;
            try { d = v.Check(intent, phase); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("action.veto_error", CoreLog.F("kind", intent.Kind), CoreLog.F("error", ex.GetType().Name));
                return VetoDecision.Refuse("veto.error");
            }
            if (!d.Allowed)
                return d with { Code = d.Code ?? "vetoed" };
        }
        return VetoDecision.Allow;
    }

    /// <summary>Logs what was done (the kind and the outcome, never the content) and returns the result.</summary>
    private ActionResult Record(ActionIntent? intent, ActionResult result)
    {
        _log.Info("action.submit", CoreLog.F("kind", intent?.Kind.ToString() ?? "none"), CoreLog.F("status", result.Status),
            CoreLog.F("id", result.Id), CoreLog.F("code", result.Code ?? result.Issues?.FirstOrDefault().Code ?? ""));
        return result;
    }
}
