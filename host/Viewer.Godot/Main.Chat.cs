// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Chat;

namespace Viewer.Godot;

/// <summary>
/// Chat and conversations in the running viewer: the shell gains the Communicate menu, the toolbar buttons and both floaters
/// when the first session logs in (features add their entries after start-up); each session gets its own hub, attached as
/// soon as the login reply names the agent so no early message is missed, and dropped when the session goes.
/// </summary>
public partial class Main
{
    private SettingsService? _settings;
    private (NearbyChatPanel Nearby, ConversationsPanel Conversations)? _chatPanels;
    private PeoplePanel? _people;
    private CommsHub? _comms;
    private ViewerSession? _commsFor;
    private Viewer.Shell.Social.BlockedModel? _blocked;

    /// <summary>The user's settings (settings.json in the per-user settings folder; in memory only when there is no folder).</summary>
    private SettingsService UserSettings(string? settingsDir)
    {
        if (_settings is not null)
            return _settings;
        _settings = new SettingsService(settingsDir is null ? null : new SettingsFile(settingsDir));
        ColourPickerWindow.Settings = _settings;   // the colour picker keeps My colours, the recent colours and Apply as I choose here
        if (_settings.MigrateGraphicsDefaults() is { } was)
            Log.Info("settings.draw-distance-reset", CoreLog.F("was", was), CoreLog.F("now", Settings.DrawDistance.Default));
        return _settings;
    }

    /// <summary>Every frame: attaches a hub to a new session, drops it from an ended one, and pumps the hub.</summary>
    private PreferencesPanel? _preferences;

    private void UpdateComms()
    {
        var session = Model?.Session;
        bool attach = session is not null && session != _commsFor && !session.IsEnded && session.AgentId != Guid.Empty;
        bool drop = _commsFor is not null && (_commsFor != session || _commsFor.IsEnded);
        if (attach || drop)
        {
            if (_commsFor is not null && _comms is not null)
                _commsFor.Communication -= _comms.Enqueue;
            _comms = null;
            _commsFor = null;
            _blocked = null;
            _sharing = null;
            if (attach && _shell is not null)
            {
                _chatPanels ??= ChatFeature.Install(_shell, UserSettings(null));
                _people ??= PeopleFeature.Install(_shell, UserSettings(null), _chatPanels.Value.Conversations);
                var s = session!;
                _preferences?.Bind(s.AgentId.ToString("N"), null);   // this account's saved look
                var strings = _shell.Shell.Strings;
                _comms = new CommsHub(s.AgentId, s.Names, s.Actions.Submit, UserSettings(null), () => DateTimeOffset.Now, s.RequestQueuedNames,
                    notice => PeopleFeature.NoticeText(notice, strings, s.Names), s.RequestGroupTitles);
                _comms.Incoming = RlvIncomingFilter;
                // the grid's own grid-info document (the grid list keeps it for the grid logged in to): its search page and currency helper
                if (_gridList?.Selected?.Info is { } info)
                    s.GridInfoValues = new Viewer.Core.Session.GridLoginValues(SearchUrl: info.Search?.ToString(), CurrencyHelper: info.Economy?.ToString());
                // chat from beyond the grid's say range is dimmed; chat places are in the speaker's region, as the agent's own is
                _comms.Nearby.IsBeyondSayRange = at => s.World.LastSelfMotion is { } me
                    && s.GridFeatures.BeyondSayRange(System.Numerics.Vector3.Distance(at, me.Motion.Position - s.World.OriginOffset));
                s.Communication += _comms.Enqueue;
                _commsFor = s;
                AttachLinks(s);
                AttachSharing(_comms, s.AgentId.ToString("N"));
                _blocked = new Viewer.Shell.Social.BlockedModel(s.Mutes, (k, id, n) => s.Block(k, id, n), (id, n) => s.Unblock(id, n));
            }
            _chatPanels?.Nearby.Bind(_comms);
            _shell?.Chat.Bind(_comms);
            _nearbyLines?.Bind(_comms);
            _chatPanels?.Conversations.Bind(_comms);
            _people?.Bind(_comms);
            _people?.BindBlocked(_blocked);
        }
        _comms?.Pump();
        UpdateBalance();
    }

    private (int? Balance, string? Region, long Slot) _balanceShown;

    /// <summary>The balance in the top bar, only on a grid with a currency, in the grid's own symbol; worked out again when the balance,
    /// the region or the 5-second slot changes (the currency is the region's setting).</summary>
    private void UpdateBalance()
    {
        var s = _commsFor;
        var key = (_comms?.Balance, s?.Region?.Name, Environment.TickCount64 / 5000);   // the region's features come after it: look again every 5 s
        if (key == _balanceShown)
            return;
        _balanceShown = key;
        _shell?.TopBar.SetBalance(s is not null && key.Item1 is { } b ? s.GridFeatures.BalanceText(b) : null);
    }

#if NV_AUTOMATION
    /// <summary>The chat command (development builds only): say a line, send a message, or count the lines holding a token.
    /// The caller gives the text; the answer never carries anyone's text, only counts, kinds and ids.</summary>
    public object? ChatCall(string action, string? text, string? kind, Guid agent, string? token)
    {
        if (_comms is not { } hub || _commsFor is not { } s)
            return null;
        hub.Pump();
        switch (action)
        {
            case "say":
            {
                ChatKind? k = kind switch { "whisper" => ChatKind.Whisper, "shout" => ChatKind.Shout, "say" => ChatKind.Say, _ => null };
                bool cleared = hub.Nearby.Send(text ?? "", k);
                return new { cleared, status = hub.Nearby.Status?.Key };
            }
            case "im":
            {
                var c = hub.Ims.OpenWith(agent);
                if (c is null) return new { cleared = false, status = "no_conversation" };
                bool cleared = hub.Ims.Send(c.SessionId, text ?? "");
                return new { cleared, status = c.Status?.Key };
            }
            default:
            {
                string t = token ?? "";
                bool Has(ChatLine l) => t.Length > 0 && l.Text.Contains(t, StringComparison.Ordinal);
                return new
                {
                    self = s.AgentId,
                    nearby = hub.Nearby.Lines.Where(Has).Select(l => new { speaker = l.SpeakerId, style = l.Style.ToString(), mark = l.Mark.ToString(), @object = l.ObjectId, has_place = l.Place is { Length: > 0 } }).ToArray(),
                    typing = hub.Nearby.Typing.Count,
                    conversations = hub.Ims.Open.Select(c => new
                    {
                        other = c.Other, unread = c.Unread, other_typing = c.OtherTyping,
                        matching = c.Lines.Count(Has), saved = c.Lines.Count(l => l.Saved && Has(l)), own = c.Lines.Count(l => l.Style == ChatLineStyle.Own && Has(l)),
                    }).ToArray(),
                };
            }
        }
    }

    /// <summary>The friends command (development builds only): the list as ids and flags, an offer, an answer, an IM opened from the
    /// list, and the recent friend notices as kinds and ids. Never a name or a line of text.</summary>
    public object? FriendsCall(string action, Guid agent)
    {
        if (_comms is not { } hub || _commsFor is not { } s)
            return null;
        hub.Pump();
        switch (action)
        {
            case "offer":
            {
                bool sent = hub.Friends.Offer(agent, "Would you like to be my friend?");
                return new { sent, status = hub.Friends.Status?.Key };
            }
            case "accept":
            case "decline":
            {
                bool sent = action == "accept" ? hub.Friends.Accept(agent) : hub.Friends.Decline(agent);
                return new { sent, status = hub.Friends.Status?.Key };
            }
            case "grant_map":
            {
                // gives the friend the right to see this agent on the map (the online right with it), keeping what was already given
                var row = hub.Friends.Friends.FirstOrDefault(f => f.Id == agent);
                if (row is null)
                    return new { sent = false, status = "not_a_friend" };
                bool sent = hub.Friends.SetRights(agent, row.Granted | FriendRightsFlags.SeeOnline | FriendRightsFlags.SeeOnMap, false);
                return new { sent, status = hub.Friends.Status?.Key };
            }
            case "im":
            {
                // as the friends list's Message button does: the conversation with that friend opens and the panel is shown
                bool listed = hub.Friends.Friends.Any(f => f.Id == agent);
                var c = listed ? hub.Ims.OpenWith(agent) : null;
                if (c is not null)
                    _shell?.Shell.Floaters.Open(ChatFeature.ConversationsId);
                return new { listed, opened = c is not null, active = hub.Ims.Active == c?.SessionId };
            }
            case "notices":
                return new { notices = hub.RecentFriendNotices.Select(n => new { kind = n.Kind.ToString(), friend = n.Friend }).ToArray() };
            default:
                return new
                {
                    self = s.AgentId,
                    offers = hub.Friends.Offers.Select(o => new { from = o.From, online = o.Online }).ToArray(),
                    friends = hub.Friends.Friends.Select(f => new { id = f.Id, online = f.Online, granted = (int)f.Granted, received = (int)f.Received }).ToArray(),
                };
        }
    }

    /// <summary>The groups command (development builds only): the group list and the active group as ids, a group chat joined and
    /// said into, and the lines and notices holding a token as counts.</summary>
    public object? GroupsCall(string action, Guid group, string? text, string? token)
    {
        if (_comms is not { } hub || _commsFor is null)
            return null;
        hub.Pump();
        switch (action)
        {
            case "join":
            {
                string name = hub.Groups.Groups.FirstOrDefault(g => g.Id == group)?.Name ?? "";
                var c = hub.Ims.OpenGroup(group, name);
                return new { opened = c is not null, state = c?.Group.ToString(), status = c?.Status?.Key };
            }
            case "say":
            {
                if (hub.Ims.Find(group) is not { IsGroup: true } c)
                    return new { cleared = false, status = "no_conversation" };
                bool cleared = hub.Ims.Send(group, text ?? "");
                return new { cleared, status = c.Status?.Key };
            }
            case "find":
            {
                string t = token ?? "";
                bool Has(ChatLine l) => t.Length > 0 && l.Text.Contains(t, StringComparison.Ordinal);
                return new
                {
                    chats = hub.Ims.Open.Where(c => c.IsGroup).Select(c => new { group = c.SessionId, state = c.Group.ToString(), unread = c.Unread, matching = c.Lines.Count(Has) }).ToArray(),
                    notices = hub.Groups.Notices.Count(n => t.Length > 0 && (n.Text.Contains(t, StringComparison.Ordinal) || n.Subject.Contains(t, StringComparison.Ordinal))),
                };
            }
            default:
                return new
                {
                    active = hub.Groups.Active.Id,
                    groups = hub.Groups.Groups.Select(g => new { id = g.Id }).ToArray(),
                    unread_notices = hub.Groups.UnreadNotices,
                };
        }
    }
#endif
}
