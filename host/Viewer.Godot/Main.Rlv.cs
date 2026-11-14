// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell.Addons;
using Viewer.Shell.Notifications;
using Vector3 = System.Numerics.Vector3;

namespace Viewer.Godot;

/// <summary>
/// RLV in the viewer: the restraint gate the RLV add-on publishes to is a veto point and a rewriter of each session's action
/// pipeline, filters incoming chat and IMs, greys Detach and Take Off for locked objects, accepts teleport offers under
/// @accepttp and takes the user back under @standtp. A refusal is shown as a notice in RLVa's words.
/// </summary>
public partial class Main
{
    private RlvGate? _rlvGate;
    private readonly ConcurrentQueue<(Guid From, Guid LureId)> _rlvLures = new();
    private bool _rlvWasSeated;
    private Vector3? _rlvLastStanding;
    private int _rlvSwallowed, _rlvAutoReplies, _rlvAutoAccepted, _rlvStandTeleports;
    private readonly List<string> _rlvRefusals = [];

    /// <summary>What the gate reads of the world: the user's seat and position, objects' positions, what the user wears, open IMs.</summary>
    private sealed class RlvWorld(Main main) : IRlvWorld
    {
        private ViewerSession? Session => main._addonsFor is { IsEnded: false } s ? s : null;
        private Viewer.Core.World.AvatarState? Self => Session is { } s ? s.World.AvatarOf(s.AgentId) : null;

        public bool Seated => Self is { ParentLocalId: not 0 };

        public Vector3? AgentPosition => Self is { ParentLocalId: 0 } self ? self.Position : null;

        public Vector3? ObjectPosition(Guid objectId) =>
            Session?.World.Snapshot().Objects.Values.FirstOrDefault(o => o.FullId == objectId && o.ParentLocalId == 0) is { } o ? o.Position : null;

        public IReadOnlyList<RlvAttachment> Attachments => Session is { } s
            ? s.World.Snapshot().Objects.Values
                .Where(o => o.AttachmentPoint is not null && o.WearerAgentId == s.AgentId && o.AttachItemId is { } item && item != Guid.Empty)
                .Select(o => new RlvAttachment(o.FullId, o.Id.LocalId, o.AttachItemId!.Value, o.AttachmentPoint!.Value)).ToArray()
            : [];

        public bool HasImSession(Guid id) => main._comms?.Ims.Open.Any(c => c.Other == id || c.SessionId == id) ?? false;
    }

    /// <summary>Makes the gate (once, with the add-on host): the RLV add-on's restraints handle publishes to it.</summary>
    private void EnsureRlvGate(AddonHost host)
    {
        if (_rlvGate is not null)
            return;
        var gate = new RlvGate(new RlvWorld(this), (channel, text) => SayOnChannel(channel, text));
        gate.Refused += code =>
        {
            _rlvRefusals.Add(code);
            if (_rlvRefusals.Count > 32)
                _rlvRefusals.RemoveAt(0);
            Log.Info("rlv.refused", CoreLog.F("code", code));
            _notes?.Model.Post(new Notification { Kind = NotificationKind.Tip, Title = NoticeText.Raw("RLV"), Body = NoticeText.Raw(RlvGate.NoticeFor(code)) });
        };
        gate.Changed += () => Log.Info("rlv.restrictions", CoreLog.F("held", gate.Holds.Count), CoreLog.F("enforced", gate.Holds.Count(h => h.Enforced)));
        host.Restraints = gate;
        _rlvGate = gate;
    }

    /// <summary>A new session's pipeline gets the gate as a rewriter and a veto point.</summary>
    private void AttachRlvGate(ViewerSession session)
    {
        if (_rlvGate is not { } gate)
            return;
        session.Actions.AddRewriter(gate);
        session.Actions.AddVeto(gate);
    }

    private bool SayOnChannel(int channel, string text) =>
        _addonsFor is { IsEnded: false } session && session.Actions.Submit(new SayIntent(text, channel)).Sent;

    private bool RlvForceSit(Guid objectId) =>
        _addonsFor is { IsEnded: false } session && session.Actions.Submit(new SitIntent(objectId)).Sent;

    private bool RlvForceStand() =>
        _addonsFor is { IsEnded: false } session && session.Actions.Submit(new StandIntent()).Sent;

    /// <summary>@tpto: a global position in metres goes to the region cell that holds it, as a teleport by region handle.</summary>
    private bool RlvForceTeleport(double x, double y, double z)
    {
        if (_addonsFor is not { IsEnded: false } session || x < 0 || y < 0)
            return false;
        int cellX = (int)Math.Floor(x / Viewer.Core.Map.MapMath.RegionMetres), cellY = (int)Math.Floor(y / Viewer.Core.Map.MapMath.RegionMetres);
        var local = new Vector3((float)(x - cellX * Viewer.Core.Map.MapMath.RegionMetres), (float)(y - cellY * Viewer.Core.Map.MapMath.RegionMetres), (float)z);
        return session.Actions.Submit(new TeleportHandleIntent(Viewer.Core.Map.MapMath.HandleOf(cellX, cellY), local)).Sent;
    }

    private bool RlvObjectPresent(Guid objectId) =>
        _addonsFor is { IsEnded: false } session && session.World.Snapshot().Objects.Values.Any(o => o.FullId == objectId);

    /// <summary>The chat hub's filter: owner-say command lines kept out while RLV runs, others' chat and IMs filtered, and the
    /// sender of a blocked IM told.</summary>
    private TransportEvent? RlvIncomingFilter(TransportEvent e)
    {
        if (_addons is not { } host || _rlvGate is not { } gate || _commsFor is not { IsEnded: false } session)
            return e;
        var result = RlvIncoming.Apply(e, host, gate, session.AgentId);
        if (result.Shown is null && e is ChatReceived { Type: ChatLineType.OwnerSay })
            _rlvSwallowed++;
        if (result.AutoReplyTo is { } to && result.AutoReply is { } reply)
        {
            using (gate.Forcing(Guid.Empty, reply: true))
                session.Actions.Submit(new InstantMessageIntent(to, reply));
            _rlvAutoReplies++;
        }
        return result.Shown;
    }

    /// <summary>A teleport offer heard on the session's thread, kept for the frame.</summary>
    private void RlvHeard(TransportEvent e)
    {
        if (e is TeleportOffered offer && offer.From != Guid.Empty)
            _rlvLures.Enqueue((offer.From, offer.LureId));
    }

    /// <summary>Once a frame: offers accepted under @accepttp, and @standtp's way back when the user stands up.</summary>
    private void RlvFrame()
    {
        if (_rlvGate is not { } gate || _addonsFor is not { IsEnded: false } session)
        {
            _rlvLures.Clear();
            return;
        }
        while (_rlvLures.TryDequeue(out var lure))
            if (gate.AutoAcceptLure(lure.From) && session.Actions.Submit(new TeleportLureAnswerIntent(lure.From, lure.LureId, true)).Sent)
                _rlvAutoAccepted++;
        var self = session.World.AvatarOf(session.AgentId);
        if (self is null)
            return;
        bool seated = self.ParentLocalId != 0;
        if (!seated)
            _rlvLastStanding = self.Position;
        if (seated && !_rlvWasSeated)
            gate.SatDown(_rlvLastStanding);
        if (!seated && _rlvWasSeated && gate.StandTeleportTarget() is { } back && session.World.Region is { } region)
        {
            var holder = gate.Holds.FirstOrDefault(h => h.Behaviour is "standtp" or "standtp_sec" && h.Enforced)?.ObjectId ?? Guid.Empty;
            using (gate.Forcing(holder))
                if (session.Actions.Submit(new TeleportHandleIntent(Viewer.Core.Map.MapMath.HandleOf(region.GridX, region.GridY), back)).Sent)
                    _rlvStandTeleports++;
        }
        _rlvWasSeated = seated;
    }

    /// <summary>Whether a worn item may be taken off now (Detach and Take Off are greyed when not).</summary>
    private bool RlvCanDetach(Guid itemId) => _rlvGate?.CanDetach(itemId) ?? true;

#if NV_AUTOMATION
    /// <summary>RLV as the automation sees it (development builds only): what is held and enforced, which worn items are locked,
    /// how many owner-say lines were kept out, the refusals shown, and the Restrictions window's lines. No one's chat text.</summary>
    private object? RlvState()
    {
        var gate = _rlvGate;
        var rlv = _addons?.ActiveFor("rlv");
        var world = gate is null ? null : new RlvWorld(this);
        return new
        {
            enabled = rlv?.Enabled ?? false,
            running = rlv?.Running ?? false,
            waits_for_login = rlv?.WaitsForLogin ?? false,
            note = rlv?.Note,
            held = gate?.Holds.Select(h => new { @object = h.ObjectId, name = h.ObjectName, behaviour = h.Behaviour, option = h.Option, enforced = h.Enforced }).ToArray(),
            locked = world?.Attachments.Where(a => !gate!.CanDetach(a.ItemId)).Select(a => a.ItemId).ToArray(),
            worn = world?.Attachments.Select(a => new { @object = a.ObjectId, item = a.ItemId, point = a.Point }).ToArray(),
            swallowed = _rlvSwallowed,
            auto_replies = _rlvAutoReplies,
            auto_accepted = _rlvAutoAccepted,
            stand_teleports = _rlvStandTeleports,
            refusals = _rlvRefusals.ToArray(),
            seated = world?.Seated,
            lines = rlv is { Running: true } ? _addons!.Diagnose(rlv.Id, "lines", null) : null,
        };
    }
#endif
}
