// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell.Chat;

namespace Viewer.Shell.Travel;

// Behaviour follows the official viewer's teleport handling (one at a time, progress texts, cancel, the failure reasons, arrival,
// teleport offers), written from a behaviour spec in our own words (no code, text or layout copied).

public enum TeleportStage { Idle, Requested, Active, Arriving, Failed, Local }

/// <summary>A teleport offer waiting for the user.</summary>
public sealed record TeleportOfferRow(Guid From, string Name, string Message, Guid LureId);

/// <summary>Someone asking this agent to offer them a teleport.</summary>
public sealed record TeleportRequestRow(Guid From, string Name, string Message);

/// <summary>
/// The teleport in progress and the offers waiting, as data. Every send goes through the action pipeline. A teleport is refused
/// here while another is going on; the grid's messages move it from requested to active to arriving and out, or to a failure
/// that stays on show until it is dismissed or another teleport starts. An offer is only ever answered by the user. Main thread only.
/// </summary>
public sealed class TeleportModel
{
    /// <summary>The grid's short progress keys this viewer words itself; each has a string "teleport.progress.&lt;key&gt;".</summary>
    public static readonly IReadOnlyList<string> KnownProgress =
    [
        "sending_dest", "redirecting", "relaying", "sending_home", "sending_landmark", "completing",
        "resolving", "contacting", "arriving", "requesting", "pending",
    ];

    /// <summary>The grid's short failure keys this viewer words itself; each has a string "teleport.error.&lt;key&gt;".</summary>
    public static readonly IReadOnlyList<string> KnownErrors =
    [
        "invalid_tport", "invalid_region_handoff", "blocked_tport", "nolandmark_tport", "timeout_tport", "NoHelpIslandTP", "noaccess_tport",
        "missing_attach_tport", "too_many_uploads_tport", "expired_tport", "expired_region_handoff", "preexisting_tport", "no_host",
        "no_inventory_host", "MustGetAgeRegion", "RegionTPSpecialUsageBlocked",
    ];

    private sealed record Offer(Guid From, string FromName, string Message, Guid LureId);

    private readonly Guid _self;
    private readonly NameCache _names;
    private readonly Func<ActionIntent, ActionResult> _submit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<Offer> _offers = new();
    private readonly List<Offer> _requests = new();
    private DateTimeOffset _startedAt;                    // when the teleport began: the give-up time runs from here, and no message restarts it
    private bool _connected;                              // a new region has connected during this teleport
    private bool _movedEarly;                             // the agent's movement-complete message came before the region connected
    private DateTimeOffset? _arrivedAt;                   // when the agent was in place in the new region: the progress stays up for the arrival delay from here
    private DateTimeOffset? _holdUntil;                   // after a teleport inside the region: a late "teleport started" message is ignored until then
    private bool _moving;                                 // the grid has finished the teleport (the official TELEPORT_MOVING): the next movement message is the arrival
    private bool _localDone;                              // the teleport inside the region has been answered; only its hold is left
    private bool _finishOwed;                             // the agent was in place before the grid's finish for that teleport came: that finish is not another arrival
    private DateTimeOffset? _movementOwedUntil;           // given up after the grid's finish: a movement message that still comes by then is the arrival

    /// <summary>After a teleport inside the region the progress view stays shut and a late "teleport started" message is ignored for this long.</summary>
    public static readonly TimeSpan LocalDelay = TimeSpan.FromSeconds(1);

    /// <summary>After the agent is in place in a new region the progress view stays up this long, while the content settles.</summary>
    public static readonly TimeSpan ArrivalDelay = TimeSpan.FromSeconds(2);

    /// <summary>A teleport still on screen this long after it began (plus <see cref="ExpiryPerAttachment"/> for each attachment worn) is given up
    /// silently: the progress closes and no failure is shown. The official viewer's TELEPORT_EXPIRY.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(15);

    /// <summary>The give-up time added for each attachment the agent wears (the official TELEPORT_EXPIRY_PER_ATTACHMENT).</summary>
    public static readonly TimeSpan ExpiryPerAttachment = TimeSpan.FromSeconds(3);

    private readonly Func<(ulong Handle, string Name)?>? _here;
    private readonly Func<int>? _attachments;

    /// <param name="here">The region the agent is in (handle and name), when known: a teleport to it is a teleport inside the region.</param>
    /// <param name="attachments">How many objects the agent wears; each one lengthens the time a teleport is given before the viewer gives up.</param>
    public TeleportModel(Guid self, NameCache names, Func<ActionIntent, ActionResult> submit, Func<DateTimeOffset> clock, Func<(ulong Handle, string Name)?>? here = null, Func<int>? attachments = null)
    {
        _here = here;
        _attachments = attachments;
        _self = self;
        _names = names;
        _submit = submit;
        _clock = clock;
    }

    public TeleportStage Stage { get; private set; }

    /// <summary>A teleport is going on (asked for, started or arriving): another is refused.</summary>
    public bool Busy => Stage is TeleportStage.Requested or TeleportStage.Active or TeleportStage.Arriving or TeleportStage.Local;

    /// <summary>A teleport is going on and has not yet put the agent in place: the grid's messages still move it.</summary>
    private bool Pending => Busy && _arrivedAt is null && !_localDone;

    /// <summary>The progress view is up: a teleport to another region is going on or arriving. Not for a teleport inside the region, and not
    /// for a failure (that shows its own notice).</summary>
    public bool ProgressVisible => Stage is TeleportStage.Requested or TeleportStage.Active or TeleportStage.Arriving;

    /// <summary>The grid allows cancelling now.</summary>
    public bool CanCancel { get; private set; }

    /// <summary>A string-table key (and its argument) for what the progress panel says; null when nothing is going on.</summary>
    public (string Key, string? Arg)? Progress { get; private set; }

    /// <summary>The failure on show; null when there is none.</summary>
    public (string Key, string? Arg)? Failure { get; private set; }

    /// <summary>A string-table key (and its argument) saying why the last action was refused; null after one went through.</summary>
    public (string Key, string? Arg)? Status { get; private set; }

    /// <summary>The offers waiting for an answer, oldest first. One per person.</summary>
    public IReadOnlyList<TeleportOfferRow> Offers =>
        _offers.Select(o => new TeleportOfferRow(o.From, !string.IsNullOrWhiteSpace(o.FromName) ? o.FromName : _names.Get(o.From) ?? NameCache.Placeholder(o.From), o.Message, o.LureId)).ToArray();

    /// <summary>How many teleports have ended in an arrival.</summary>
    public int Arrivals { get; private set; }

    /// <summary>Raised when anything shown changed.</summary>
    public event Action? Changed;

    /// <summary>Raised when the teleport home the user asked for was refused, with the failure shown (the grid's words when it gave any).
    /// While visiting another grid the interface then offers to log out and log back in at home.</summary>
    public event Action<(string Key, string? Arg)>? HomeFailed;

    private bool _homeAsked;   // the teleport going on is the user's teleport home

    /// <summary>Raised when a teleport begins: the user's request was sent, or the grid started one while none was going on (the
    /// moments the official viewer plays its teleport sound). Not again for the same teleport's later messages.</summary>
    public event Action? Departing;

    /// <summary>Raised once when a teleport has ended and the agent is in place (the official viewer's handleTeleportFinished), for a teleport
    /// inside the region too, and for one the progress view does not show: one the grid makes while the one before is still arriving or
    /// within the local delay, one that ends after the viewer gave up on it, or a move the grid reports with no start message.</summary>
    public event Action? Arrived;

    // ---------------- starting ----------------

    public bool ToRegion(string region, Vector3 position) => Start(new TeleportRegionIntent(region, position));

    /// <summary>A teleport to a place by its travel address: a region on the grid the agent is on (<paramref name="currentGrid"/>,
    /// "host:port") by its name, a region on another grid by the grid's map lookup of "host:port:Region" (OpenSim links it as a Hypergrid
    /// region), then like any teleport. The grid's own refusal text is shown when the lookup or the teleport fails.</summary>
    public bool ToAddress(Viewer.Core.Links.TravelAddress address, string? currentGrid, Vector3? fallback = null)
    {
        var place = address.OnGrid(currentGrid);
        var at = place.Coordinates > 0 ? place.Position : fallback ?? new Vector3(128, 128, 30);
        return Start(new TeleportRegionIntent(place.LookupName, at, place.Grid));
    }

    public bool ToHandle(ulong handle, Vector3 position) => Start(new TeleportHandleIntent(handle, position));

    public bool ToLandmark(Guid landmark, string? grid = null) => Start(new TeleportLandmarkIntent(landmark, grid));

    public bool Home() => Start(new TeleportHomeIntent());

    private bool Start(ActionIntent intent)
    {
        if (Refuse())
            return false;
        bool inside = InsideRegion(intent);
        if (!Finish(_submit(intent)))
            return false;
        Begin(inside);
        _homeAsked = intent is TeleportHomeIntent;
        return true;
    }

    /// <summary>Whether the teleport asked for goes to the region the agent is in. The official viewer treats only a teleport to a position
    /// (here: a region name or a region handle) this way; a landmark or home is always shown as a teleport.</summary>
    private bool InsideRegion(ActionIntent intent)
    {
        if (_here?.Invoke() is not { } here)
            return false;
        return intent switch
        {
            TeleportRegionIntent r => string.Equals(r.RegionName.Trim(), here.Name, StringComparison.OrdinalIgnoreCase),
            TeleportHandleIntent h => h.RegionHandle == here.Handle,
            _ => false,
        };
    }

    /// <summary>One teleport at a time: true (and the reason set) when another is going on.</summary>
    private bool Refuse()
    {
        if (!Busy)
            return false;
        Status = ("status.teleport.busy", null);
        Changed?.Invoke();
        return true;
    }

    private bool Finish(ActionResult r)
    {
        Status = ChatText.StatusOf(r);
        if (!r.Sent)
            Changed?.Invoke();
        return r.Sent;
    }

    private void Begin(bool inside = false)
    {
        // inside the region the progress stays shut (the official TELEPORT_LOCAL); otherwise it comes up asking
        Stage = inside ? TeleportStage.Local : TeleportStage.Requested;
        Progress = inside ? null : ("teleport.progress.requesting", null);
        Failure = null;
        CanCancel = !inside;
        _holdUntil = null;
        _finishOwed = false;
        _movementOwedUntil = null;
        ForgetArrival();
        _startedAt = _clock();
        Changed?.Invoke();
        Departing?.Invoke();
    }

    private void ForgetArrival()
    {
        _homeAsked = false;
        _connected = false;
        _movedEarly = false;
        _moving = false;
        _arrivedAt = null;
        _localDone = false;
    }

    // ---------------- cancel, dismiss ----------------

    /// <summary>Asks the grid to stop the teleport. Nothing is sent when none is going on or the grid forbids cancelling.</summary>
    public bool Cancel()
    {
        if (!Busy || !CanCancel)
            return false;
        if (!Finish(_submit(new TeleportCancelIntent())))
            return false;
        Reset();
        return true;
    }

    /// <summary>Closes the failure on show.</summary>
    public void Dismiss()
    {
        if (Stage == TeleportStage.Failed)
            Reset();
    }

    private void Reset()
    {
        Stage = TeleportStage.Idle;
        Progress = null;
        Failure = null;
        CanCancel = false;
        ForgetArrival();
        Changed?.Invoke();
    }

    /// <summary>The agent is in place in the new region (the official TELEPORT_START_ARRIVAL): the progress stays up, without Cancel, for the arrival
    /// delay while the content settles, and then closes.</summary>
    private void Arrive()
    {
        _finishOwed = !_moving;
        CountArrival();
        _arrivedAt = _clock();
        Stage = TeleportStage.Arriving;
        Progress = ("teleport.progress.arriving", null);
        CanCancel = false;
        Changed?.Invoke();
    }

    /// <summary>The grid placed the agent inside the region (the official process_teleport_local). A teleport asked for inside the region stays
    /// in its quiet state for the local delay, ignoring a late "teleport started"; any other closes the progress at once and holds off a late
    /// start for the same time.</summary>
    private void ArriveInside()
    {
        CountArrival();
        var now = _clock();
        if (Stage == TeleportStage.Local)
        {
            _localDone = true;
            _holdUntil = now + LocalDelay;
            Changed?.Invoke();
            return;
        }
        Reset();
        _holdUntil = now + LocalDelay;
    }

    private void CountArrival()
    {
        Arrivals++;
        Arrived?.Invoke();
    }

    private void Fail((string Key, string? Arg) failure)
    {
        Stage = TeleportStage.Failed;
        Progress = null;
        Failure = failure;
        CanCancel = false;
        ForgetArrival();
        Changed?.Invoke();
    }

    // ---------------- offers ----------------

    /// <summary>Offers a teleport to one agent. Not a teleport of our own: it neither needs nor starts one.</summary>
    public bool OfferTo(Guid target, string message)
    {
        if (target == _self)
        {
            Status = ("status.teleport.no_target", null);
            Changed?.Invoke();
            return false;
        }
        return Finish(_submit(new TeleportOfferIntent(target, message)));
    }

    /// <summary>Accepts an offer: a teleport via the offer. Refused while another teleport goes on, and the offer stays.</summary>
    public bool Accept(Guid from)
    {
        var offer = _offers.Find(o => o.From == from);
        if (offer is null || Refuse())
            return false;
        if (!Finish(_submit(new TeleportLureAnswerIntent(from, offer.LureId, true))))
            return false;
        _offers.Remove(offer);
        Begin();
        return true;
    }

    public bool Decline(Guid from)
    {
        var offer = _offers.Find(o => o.From == from);
        if (offer is null)
            return false;
        if (!Finish(_submit(new TeleportLureAnswerIntent(from, offer.LureId, false))))
            return false;
        _offers.Remove(offer);
        Changed?.Invoke();
        return true;
    }

    // ---------------- set home, teleport requests ----------------

    /// <summary>Someone asking this agent to offer them a teleport, oldest first. One per person. Nothing is sent for one until the user chooses.</summary>
    public IReadOnlyList<TeleportRequestRow> Requests =>
        _requests.Select(o => new TeleportRequestRow(o.From, !string.IsNullOrWhiteSpace(o.FromName) ? o.FromName : _names.Get(o.From) ?? NameCache.Placeholder(o.From), o.Message)).ToArray();

    /// <summary>Sets the home location to where the agent stands. The region decides and answers with an alert (shown as a notice). It is not a
    /// teleport, so it neither needs the teleport to be idle nor starts one.</summary>
    public bool SetHome() => Finish(_submit(new SetHomeIntent()));

    /// <summary>Asks one person to offer this agent a teleport (the official viewer's "request teleport"). The answer, if any, arrives as an
    /// offer, which is only ever accepted by the user.</summary>
    public bool AskToBeTeleportedBy(Guid target, string message)
    {
        if (target == _self)
        {
            Status = ("status.teleport.no_target", null);
            Changed?.Invoke();
            return false;
        }
        return Finish(_submit(new TeleportRequestIntent(target, message)));
    }

    /// <summary>Answers a request with an offer to that person (the official viewer's "Yes"); the request is removed once the offer went out.</summary>
    public bool OfferToRequester(Guid from)
    {
        var request = _requests.Find(o => o.From == from);
        if (request is null)
            return false;
        if (!Finish(_submit(new TeleportOfferIntent(from, ""))))
            return false;
        _requests.Remove(request);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Turns a request down (the official viewer's "No"): nothing is sent, the request is removed.</summary>
    public bool DeclineRequest(Guid from)
    {
        if (_requests.RemoveAll(o => o.From == from) == 0)
            return false;
        Changed?.Invoke();
        return true;
    }

    // ---------------- the grid's messages ----------------

    public void Receive(TransportEvent e)
    {
        switch (e)
        {
            case TeleportProgressed p:
                Progressed(p);
                break;
            case TeleportOffered o when o.From != Guid.Empty:
                _offers.RemoveAll(x => x.From == o.From);
                _offers.Add(new Offer(o.From, o.FromName ?? "", o.Message.Reveal(), o.LureId));
                Changed?.Invoke();
                break;
            case TeleportRequested q when q.From != Guid.Empty && q.From != _self:
                _requests.RemoveAll(x => x.From == q.From);
                _requests.Add(new Offer(q.From, q.FromName ?? "", q.Message.Reveal(), Guid.Empty));
                Changed?.Invoke();
                break;
            case SimConnected when Pending:
                // the first region to connect after a teleport began is its destination; the agent's movement message for it may
                // have come first (it does at login too), so either order ends the arrival
                _connected = true;
                Stage = TeleportStage.Arriving;
                Progress = ("teleport.progress.arriving", null);
                if (_movedEarly)
                    Arrive();
                else
                    Changed?.Invoke();
                break;
            case MovementComplete when Pending:
                // the official process_agent_movement_complete looks at the teleport state only, never at which region the message
                // names: once the grid has finished the teleport (or the new region is up) the agent is in place
                if (_moving || _connected)
                    Arrive();
                else
                    _movedEarly = true;
                break;
            case MovementComplete when _movementOwedUntil is { } by && _clock() <= by:
                _movementOwedUntil = null;
                CountArrival();
                Changed?.Invoke();
                break;
            case SimConnected:
                _movementOwedUntil = null;   // another region since: its movement message is not the one waited for
                break;
            case Disconnected:
                _finishOwed = false;
                _movementOwedUntil = null;
                if (Stage != TeleportStage.Idle)
                    Reset();
                break;
        }
    }

    private void Progressed(TeleportProgressed p)
    {
        switch (p.Phase)
        {
            case TeleportPhase.Started:
                _finishOwed = false;   // another teleport: its finish is its own
                _movementOwedUntil = null;
                // the grid may start a teleport nobody here asked for (being sent home)
                // like the official viewer, a start message only brings the progress up when nothing else is going on, and not within the
                // local delay of a teleport that ended inside the region (a late start would leave the progress on screen)
                if (Stage is TeleportStage.Arriving or TeleportStage.Local || _holdUntil is { } until && _clock() < until)
                    return;
                bool unasked = !Busy;
                _startedAt = _clock();
                Stage = TeleportStage.Active;
                Progress = ("teleport.progress.starting", null);
                Failure = null;
                CanCancel = p.CanCancel;
                Changed?.Invoke();
                if (unasked)
                    Departing?.Invoke();
                break;
            case TeleportPhase.Progress when Pending && Stage != TeleportStage.Local:
                if (Stage != TeleportStage.Arriving)
                    Stage = TeleportStage.Active;
                Progress = ProgressText(p.Message);
                CanCancel = p.CanCancel;
                Changed?.Invoke();
                break;
            case TeleportPhase.Finished when Pending:
                _moving = true;
                if (!_connected && !_movedEarly)
                    ArriveInside();   // no new region connected: it was inside the region
                else if (_movedEarly)
                    Arrive();
                else
                {
                    Stage = TeleportStage.Arriving;
                    Progress = ("teleport.progress.arriving", null);
                    Changed?.Invoke();
                }
                break;
            case TeleportPhase.Local when Pending:
                ArriveInside();
                break;
            case TeleportPhase.Finished when _finishOwed:
                _finishOwed = false;
                break;
            case TeleportPhase.Finished or TeleportPhase.Local:
                // the grid's finish with no teleport waiting for it: the progress view stays as it is, the arrival still counts
                CountArrival();
                Changed?.Invoke();
                break;
            case TeleportPhase.Failed:
            {
                _finishOwed = false;
                _movementOwedUntil = null;
                bool home = _homeAsked && Busy;
                Fail(ErrorText(p.Message, p.RegionName));
                if (home)
                    HomeFailed?.Invoke(Failure!.Value);
                break;
            }
            case TeleportPhase.Cancelled when Busy && _arrivedAt is null:
                _movementOwedUntil = null;
                Reset();
                break;
        }
    }

    private static (string Key, string? Arg) ProgressText(string message) =>
        KnownProgress.Contains(message) ? ("teleport.progress." + message, null) : ("teleport.progress.raw", message);

    private static (string Key, string? Arg) ErrorText(string reason, string region)
    {
        if (KnownErrors.Contains(reason))
            return ("teleport.error." + reason, region.Length > 0 ? region : null);
        return reason.Length > 0 ? ("teleport.error.raw", reason) : ("teleport.error.unknown", null);
    }

    /// <summary>Main thread, every frame: ends the arrival delay, and gives up a teleport that has gone on too long (the official update_tp_display:
    /// the state goes to none and the message is cleared, with no failure shown).</summary>
    public void Tick()
    {
        var now = _clock();
        if (Busy && now - _startedAt > Expiry + ExpiryPerAttachment * Math.Max(0, _attachments?.Invoke() ?? 0))
        {
            // the grid had finished it: if the movement message still comes soon, that is the arrival
            _movementOwedUntil = _moving && Pending ? now + Expiry : null;
            Reset();
            return;
        }
        if (_arrivedAt is { } arrived)
        {
            if (now - arrived >= ArrivalDelay)
                Reset();   // the official TELEPORT_NONE: the progress closes
            return;
        }
        // inside the region, once answered, the quiet state ends after the local delay
        if (Stage == TeleportStage.Local && _localDone && now >= _holdUntil)
            Reset();
    }
}
