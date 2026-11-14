// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using Viewer.Addons;
using Viewer.Core.Actions;
using Viewer.Core.Session;
using ChatKind = Viewer.Core.Session.ChatKind;

namespace Viewer.Shell.Addons;

/// <summary>One of the user's own attachments, as the restraint gate needs it.</summary>
public readonly record struct RlvAttachment(Guid ObjectId, uint LocalId, Guid ItemId, int Point);

/// <summary>What the restraint gate reads of the world: whether the user sits, where the user and objects are, what is worn.</summary>
public interface IRlvWorld
{
    bool Seated { get; }
    /// <summary>The user's position in the region, metres; null when not known.</summary>
    Vector3? AgentPosition { get; }
    /// <summary>An object's position in the region, metres; null when not known.</summary>
    Vector3? ObjectPosition(Guid objectId);
    IReadOnlyList<RlvAttachment> Attachments { get; }
    /// <summary>Whether an IM conversation with this avatar or group is open.</summary>
    bool HasImSession(Guid id);
}

/// <summary>What became of an incoming instant message under the restraints.</summary>
/// <param name="Text">The text to show (the original, or the blocked notice); null to show nothing.</param>
/// <param name="AutoReply">A message to send back to the sender, or null.</param>
public readonly record struct RlvIncomingIm(string? Text, string? AutoReply);

/// <summary>
/// The viewer's enforcement of RLV restrictions: the restraint add-on publishes what objects hold, and this gate refuses or
/// changes the user's actions to match, as a veto point and a rewriter of the action pipeline. Incoming chat and IMs are
/// filtered through it too. The rules follow RLVa (Firestorm's rlvactions.cpp, rlvhandler.cpp filterChat and
/// redirectChatOrEmote, fsnearbychathub.cpp send_chat_from_viewer, llviewermessage.cpp and llimview.cpp for incoming chat and
/// IMs, llagent.cpp for teleports), written from that behaviour. Anything unexpected lets the action go ahead.
/// </summary>
public sealed class RlvGate : IActionVeto, IActionRewriter
{
    /// <summary>The text an outgoing IM is replaced with under @sendim (RLVa's blocked_sendim string).</summary>
    public const string BlockedSendIm = "*** IM blocked by sender's viewer";
    /// <summary>The text an incoming IM is shown as under @recvim (RLVa's blocked_recvim string).</summary>
    public const string BlockedRecvIm = "*** IM blocked by your viewer";
    /// <summary>The answer the sender of a blocked IM gets (RLVa's blocked_recvim_remote string).</summary>
    public const string BlockedRecvImRemote = "The Resident you messaged is currently prevented from reading your instant messages at the moment, please try again later.";
    /// <summary>What filtered chat becomes (RLVa shows an ellipsis by default).</summary>
    public const string Ellipsis = "...";
    /// <summary>@sittp's default distance in metres (RLVa's RLV_MODIFIER_SITTP_DEFAULT).</summary>
    public const float SitTpDistance = 1.5f;
    /// <summary>The chat channel that shows as public chat on viewers.</summary>
    private const int DebugChannel = int.MaxValue;

    /// <summary>The attachment point names RLV uses in @detach:&lt;point&gt;, by point number (the avatar definition's names, lower case).</summary>
    private static readonly string[] PointNames =
    [
        "", "chest", "skull", "left shoulder", "right shoulder", "left hand", "right hand", "left foot", "right foot", "spine",
        "pelvis", "mouth", "chin", "left ear", "right ear", "left eyeball", "right eyeball", "nose", "r upper arm", "r forearm",
        "l upper arm", "l forearm", "right hip", "r upper leg", "r lower leg", "left hip", "l upper leg", "l lower leg", "stomach",
        "left pec", "right pec", "center 2", "top right", "top", "top left", "center", "bottom left", "bottom", "bottom right",
        "neck", "avatar center", "left ring finger", "right ring finger", "tail base", "tail tip", "left wing", "right wing",
        "jaw", "alt left ear", "alt right ear", "alt left eye", "alt right eye", "tongue", "groin", "left hind foot",
        "right hind foot",
    ];

    private readonly IRlvWorld _world;
    private readonly Func<int, string, bool> _sayOnChannel;
    private AddonRestriction[] _holds = [];
    private readonly Stack<(Guid Issuer, bool Reply)> _forcing = new();
    private Vector3? _satFrom;

    public RlvGate(IRlvWorld world, Func<int, string, bool> sayOnChannel)
    {
        _world = world;
        _sayOnChannel = sayOnChannel;
    }

    /// <summary>The restrictions in force.</summary>
    public IReadOnlyList<AddonRestriction> Holds => _holds;

    /// <summary>Raised when the restrictions change.</summary>
    public event Action? Changed;

    /// <summary>Raised when the gate refuses one of the user's actions, with the refusal's code.</summary>
    public event Action<string>? Refused;

    /// <summary>The notice shown for a refusal's code: RLVa's own wording (rlva_strings.xml blocked_teleport, blocked_startim,
    /// blocked_generic).</summary>
    public static string NoticeFor(string code) => code switch
    {
        "rlv.teleport" => "Unable to initiate teleport due to RLV restrictions",
        "rlv.startim" => "Unable to start IM session due to RLV restrictions",
        _ => "Unable to perform action due to RLV restrictions",
    };

    /// <summary>Replaces the restrictions in force.</summary>
    public void Set(IReadOnlyList<AddonRestriction> restrictions)
    {
        var next = restrictions.ToArray();
        if (next.SequenceEqual(_holds))
            return;
        _holds = next;
        Changed?.Invoke();
    }

    /// <summary>Drops every restriction (the add-on stopped).</summary>
    public void Clear()
    {
        if (_holds.Length == 0)
            return;
        _holds = [];
        Changed?.Invoke();
    }

    /// <summary>Whether a behaviour is held (enforced) by any object.</summary>
    public bool Has(string behaviour) => Holding(behaviour, Excepted);

    /// <summary>For the time of the returned scope, the issuing object's own restrictions are passed over (a forced action), or
    /// every chat restriction (a reply).</summary>
    public IDisposable Forcing(Guid issuer, bool reply = false)
    {
        _forcing.Push((issuer, reply));
        return new Scope(this);
    }

    private Guid? Excepted => _forcing.Count > 0 ? _forcing.Peek().Issuer : null;
    private bool Replying => _forcing.Count > 0 && _forcing.Peek().Reply;

    // ---------------- what is held ----------------

    private IEnumerable<AddonRestriction> Enforced => _holds.Where(h => h.Enforced);

    /// <summary>A restriction (no option, or the strict "_sec" form) held by any object but the excepted one.</summary>
    private bool Holding(string behaviour, Guid? except = null) =>
        Enforced.Any(h => (h.Behaviour == behaviour || h.Behaviour == behaviour + "_sec") && h.Option.Length == 0 && h.ObjectId != except);

    /// <summary>An exception (the behaviour with this option). When an object holds the strict ("_sec") form, only exceptions
    /// set by such an object count (RLVa's strict exception check).</summary>
    private bool IsException(string behaviour, Guid id)
    {
        var strict = Enforced.Where(h => h.Behaviour == behaviour + "_sec" && h.Option.Length == 0).Select(h => h.ObjectId).ToHashSet();
        return Enforced.Any(h => h.Behaviour == behaviour && Guid.TryParse(h.Option, out var g) && g == id && (strict.Count == 0 || strict.Contains(h.ObjectId)));
    }

    private bool IsChannelException(string behaviour, int channel) =>
        Enforced.Any(h => h.Behaviour == behaviour && int.TryParse(h.Option, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int c) && c == channel);

    private bool SeatedAndUnsit => _world.Seated && Holding("unsit", Excepted);

    // ---------------- the veto ----------------

    public VetoDecision Check(ActionIntent intent, ActionPhase phase)
    {
        if (_holds.Length == 0 || phase == ActionPhase.Continue)
            return VetoDecision.Allow;
        try
        {
            if (Refusal(intent) is not { } code)
                return VetoDecision.Allow;
            Refused?.Invoke(code);
            return VetoDecision.Refuse(code);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return VetoDecision.Allow;   // fails open: a fault here never traps anyone
        }
    }

    private string? Refusal(ActionIntent intent)
    {
        var except = Excepted;
        switch (intent)
        {
            case SitIntent sit:
                if (Holding("sit", except) || _world.Seated && (Holding("unsit", except) || Holding("standtp", except)))
                    return "rlv.sit";
                if (Holding("sittp", except) && _world.AgentPosition is { } me && _world.ObjectPosition(sit.ObjectId) is { } seat
                    && Vector3.Distance(me, seat) > SitTpDistance)
                    return "rlv.sit";
                return null;
            case SitGroundIntent:
                return Holding("sit", except) || SeatedAndUnsit ? "rlv.sit" : null;
            case StandIntent:
                return SeatedAndUnsit ? "rlv.stand" : null;
            case TeleportLandmarkIntent:
                return Holding("tplm", except) || SeatedAndUnsit ? "rlv.teleport" : null;
            case TeleportHomeIntent:
                return Holding("tplm", except) && Holding("tploc", except) || SeatedAndUnsit ? "rlv.teleport" : null;
            case TeleportRegionIntent or TeleportHandleIntent:
                return Holding("tploc", except) || SeatedAndUnsit ? "rlv.teleport" : null;
            case TeleportLureAnswerIntent { Accept: true } lure:
                return Holding("tplure", except) && !IsException("tplure", lure.From) || SeatedAndUnsit ? "rlv.teleport" : null;
            case OutfitChangeIntent change:
                if (change.Change.Detach.Any(item => !CanDetach(item)))
                    return "rlv.detach";
                if (change.Change.Attach.Any(a => !a.Add && _world.Attachments.Any(w => w.Point == a.Point && !CanDetach(w.ItemId))))
                    return "rlv.detach";
                return null;
            case DropAttachmentIntent drop:
                return _world.Attachments.Where(a => a.LocalId == drop.LocalId).Any(a => !CanDetach(a.ItemId)) ? "rlv.detach" : null;
            case InstantMessageIntent im:
                return Holding("startim", except) && !IsException("startim", im.Target) && !_world.HasImSession(im.Target) ? "rlv.startim" : null;
            default:
                return null;
        }
    }

    /// <summary>Whether a worn object may be taken off (Detach and Take Off are greyed when not): it is locked when it holds
    /// @detach itself, or when any object holds @detach:&lt;the point it is worn on&gt;.</summary>
    public bool CanDetach(Guid itemId)
    {
        if (_holds.Length == 0)
            return true;
        var worn = _world.Attachments.Where(a => a.ItemId == itemId).ToArray();
        foreach (var a in worn)
        {
            string point = a.Point > 0 && a.Point < PointNames.Length ? PointNames[a.Point] : "";
            if (Enforced.Any(h => h.Behaviour == "detach" && (h.Option.Length == 0 && h.ObjectId == a.ObjectId || h.Option.Length > 0 && h.Option == point)))
                return false;
        }
        return true;
    }

    // ---------------- the rewriter ----------------

    public ActionIntent? Rewrite(ActionIntent intent, out string? code)
    {
        code = null;
        if (_holds.Length == 0 || Replying)
            return intent;
        try
        {
            return intent switch
            {
                SayIntent say => RewriteSay(say, out code),
                InstantMessageIntent im when Holding("sendim", Excepted) && !IsException("sendim", im.Target) => im with { Text = BlockedSendIm },
                GroupMessageIntent gm when Holding("sendim", Excepted) && !IsException("sendim", gm.Group) => gm with { Text = BlockedSendIm },
                _ => intent,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            code = null;
            return intent;   // fails open
        }
    }

    private SayIntent? RewriteSay(SayIntent say, out string? code)
    {
        code = null;
        var except = Excepted;
        bool emote = IsEmote(say.Text);
        if (say.Channel != 0)
        {
            if (!CanSendChannel(say.Channel, except))
            {
                code = "rlv.sendchannel";
                return null;
            }
            if (say.Channel == DebugChannel && (Holding("sendchat", except) || !emote && Holding("redirchat", except)))
            {
                code = "rlv.sendchat";
                return null;
            }
            return say;
        }
        var kind = say.Chat;
        if (kind is ChatKind.Say or ChatKind.Shout && Holding("chatnormal", except))
            kind = ChatKind.Whisper;
        else if (kind == ChatKind.Shout && Holding("chatshout", except))
            kind = ChatKind.Say;
        else if (kind == ChatKind.Whisper && Holding("chatwhisper", except))
            kind = ChatKind.Say;

        if (!emote && say.Text.Length > 0 && Enforced.Any(h => h.Behaviour == "redirchat" && h.ObjectId != except) && Filter(say.Text, false, except) is not null)
        {
            foreach (var channel in Enforced.Where(h => h.Behaviour == "redirchat" && h.ObjectId != except)
                         .Select(h => int.TryParse(h.Option, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int c) ? c : 0)
                         .Where(c => c != 0).Distinct().ToArray())
                if (CanSendChannel(channel, except))
                    _sayOnChannel(channel, say.Text);
            code = "rlv.redirected";
            return null;
        }
        string text = say.Text;
        if (Holding("sendchat", except) && Filter(text, true, except) is { } filtered)
        {
            if (filtered.Length == 0)
            {
                code = "rlv.sendchat";
                return null;
            }
            text = filtered;
        }
        return text == say.Text && kind == say.Chat ? say : say with { Text = text, Chat = kind };
    }

    private bool CanSendChannel(int channel, Guid? except) =>
        !Holding("sendchannel", except) || IsChannelException("sendchannel", channel);

    private static bool IsEmote(string text) =>
        text.StartsWith("/me ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/me'", StringComparison.OrdinalIgnoreCase);

    /// <summary>RLVa's chat filter: what the text becomes (an ellipsis, or a shortened emote), or null when it passes as it is.
    /// Out-of-character text in double brackets passes; a short '/' gesture passes.</summary>
    private string? Filter(string text, bool filterEmote, Guid? except)
    {
        if (text.Length == 0)
            return null;
        bool filter = false;
        if (IsEmote(text))
        {
            if (!filterEmote)
                return null;
            if (text.IndexOfAny(['"', '(', ')', '*', '=', '^', '_', '?', '~']) >= 0 || text.Contains(" -", StringComparison.Ordinal)
                || text.Contains("- ", StringComparison.Ordinal) || text.Contains("''", StringComparison.Ordinal))
                filter = true;
            else if (!Holding("emote", except))
            {
                int dot = text.IndexOf('.');
                int keep = dot > 0 && dot < 20 ? dot + 1 : 20;
                return text.Length > keep ? text[..keep] : null;
            }
        }
        else if (text[0] == '/')
            filter = text.Length > 7;
        else if (text.Length < 4 || !text.StartsWith("((", StringComparison.Ordinal) || !text.EndsWith("))", StringComparison.Ordinal))
            filter = true;
        return filter ? Ellipsis : null;
    }

    // ---------------- incoming ----------------

    /// <summary>A local chat line heard from someone else: the text to show (filtered), or null to show nothing.</summary>
    public string? Incoming(Guid fromId, bool fromAgent, string text)
    {
        if (_holds.Length == 0 || IsEmote(text))
            return text;
        try
        {
            if (Holding("recvchat") && !IsException("recvchat", fromId) && Filter(text, false, null) is { } filtered)
                return filtered;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return text;
    }

    /// <summary>An incoming instant message (not a group message).</summary>
    public RlvIncomingIm IncomingIm(Guid fromId, string text) =>
        _holds.Length > 0 && Holding("recvim") && !IsException("recvim", fromId) ? new(BlockedRecvIm, BlockedRecvImRemote) : new(text, null);

    /// <summary>Whether a group's messages may be received.</summary>
    public bool CanReceiveGroup(Guid groupId) => !Holding("recvim") || IsException("recvim", groupId);

    /// <summary>Whether a teleport offer from this avatar is accepted without asking (@accepttp).</summary>
    public bool AutoAcceptLure(Guid fromId) =>
        Enforced.Any(h => h.Behaviour == "accepttp" && (h.Option.Length == 0 || Guid.TryParse(h.Option, out var g) && g == fromId));

    /// <summary>The user stood up: where to teleport back to, under @standtp; null otherwise.</summary>
    public Vector3? StandTeleportTarget() => Holding("standtp") ? _satFrom : null;

    /// <summary>The user sat down: the place they sat from is kept for @standtp.</summary>
    public void SatDown(Vector3? from) => _satFrom = from;

    private sealed class Scope(RlvGate gate) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            if (gate._forcing.Count > 0)
                gate._forcing.Pop();
        }
    }
}
