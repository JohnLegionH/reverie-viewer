// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Actions;

/// <summary>What became of one submitted intent.</summary>
public enum ActionStatus
{
    /// <summary>The zero value: nothing was submitted.</summary>
    NotSubmitted = 0,
    /// <summary>The command was handed to the protocol layer exactly once. This says nothing about what the grid made of it.</summary>
    Sent,
    /// <summary>The intent failed its own checks; nothing was sent. <see cref="ActionResult.Issues"/> says which.</summary>
    Invalid,
    /// <summary>A veto point refused it; nothing was sent. <see cref="ActionResult.Code"/> is the refusal's code.</summary>
    Vetoed,
    /// <summary>It could not be tried now: no session in world, or no command transport. Nothing was sent.</summary>
    Unavailable,
    /// <summary>The protocol layer reported that it did not send the command. Nothing left the viewer.</summary>
    Failed,
    /// <summary>The protocol layer threw while sending. The command may or may not have gone out: it is never sent again
    /// on its own, and the caller reconciles with an authoritative read, then calls <see cref="ActionPipeline.Reconciled"/>.</summary>
    OutcomeUnknown,
}

/// <summary>One thing wrong with an intent: a stable code (<c>say.empty</c>) the interface maps to a string-table text,
/// and a short detail for the log. Never carries the user's text.</summary>
public readonly record struct ActionIssue(string Code, string Detail = "");

/// <summary>The outcome of a submission. <see cref="Id"/> numbers the submission (0: none was made).</summary>
public readonly record struct ActionResult(ActionStatus Status, long Id = 0, string? Code = null, IReadOnlyList<ActionIssue>? Issues = null)
{
    public bool Sent => Status == ActionStatus.Sent;
}

/// <summary>What a veto point is asked about: a first submission, the start of a held intent, or one of its continuation ticks.</summary>
public enum ActionPhase { Submit, Begin, Continue }

public readonly record struct VetoDecision(bool Allowed, string? Code = null)
{
    public static VetoDecision Allow => new(true);
    public static VetoDecision Refuse(string code) => new(false, code);
}

/// <summary>
/// A point where policy can refuse an intent (restrictions today; scripts and add-ons later). Runs on the submitting
/// thread, in registration order; the first refusal wins. A veto that throws refuses (the pipeline fails closed).
/// </summary>
public interface IActionVeto
{
    VetoDecision Check(ActionIntent intent, ActionPhase phase);
}

/// <summary>
/// A point where policy may change an intent before it is checked and sent (restraints: chat turned into a whisper, filtered
/// or redirected). Answers the intent itself when nothing changes, another intent to send in its place, or null to send
/// nothing (the submission is then refused with <paramref name="code"/>). Runs before validation and the veto points, in
/// registration order. A rewriter that throws leaves the intent as it was (it fails open: it can only ever add limits).
/// </summary>
public interface IActionRewriter
{
    ActionIntent? Rewrite(ActionIntent intent, out string? code);
}

/// <summary>A veto whose answers can change while a held intent runs: it tells the pipeline when, so a revocation ends the
/// hold at once and does not wait for the next tick.</summary>
public interface IChangingVeto : IActionVeto
{
    event Action? Changed;
}

/// <summary>Why a held intent ended.</summary>
public enum HoldEnd { Released, Revoked, SessionEnded, Failed }

/// <summary>Where a held intent's effects go: the owner of the repeated traffic (for movement, the adapter's AgentUpdate
/// sender). The pipeline calls it only while the hold is allowed, and calls <see cref="End"/> exactly once.</summary>
public interface IHeldSink
{
    void Begin(HeldIntent intent);
    void Continue(HeldIntent intent);
    void End(HeldIntent intent, HoldEnd reason);
}
