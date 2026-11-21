// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell.Social;
using Viewer.Shell.Travel;

namespace Viewer.Shell.Notifications;

/// <summary>
/// Shows what the friend, teleport and notice models already decide as notifications. It reads what those models raise and hold
/// (their notices, offers, requests and alerts) and changes none of their receiving code: an answer given to a notification goes back
/// through the same model methods the other windows use, and an offer answered somewhere else leaves the notification. Main thread only.
/// </summary>
public sealed class NoticeFeeder
{
    private readonly NotificationModel _model;
    private readonly FriendsModel _friends;
    private readonly TeleportModel _teleport;
    private readonly NoticeModel _notices;
    private readonly Func<Guid, string> _nameOf;
    private readonly Action<Guid>? _openConversation;
    private readonly Dictionary<Guid, long> _friendOffers = new();
    private readonly Dictionary<Guid, long> _teleportOffers = new();
    private readonly Dictionary<Guid, long> _teleportRequests = new();
    private readonly HashSet<Notice> _seen = new(ReferenceEqualityComparer.Instance);

    public NoticeFeeder(NotificationModel model, FriendsModel friends, TeleportModel teleport, NoticeModel notices, Func<Guid, string> nameOf, Action<Guid>? openConversation = null)
    {
        _model = model;
        _friends = friends;
        _teleport = teleport;
        _notices = notices;
        _nameOf = nameOf;
        _openConversation = openConversation;
        friends.Notice += OnFriendNotice;
        friends.Changed += SyncFriendOffers;
        teleport.Changed += SyncTeleport;
        notices.Changed += SyncNotices;
    }

    // ---------------- friends ----------------

    private void OnFriendNotice(FriendNotice notice)
    {
        string key = notice.Kind switch
        {
            FriendNoticeKind.Online => "friend.notice.online",
            FriendNoticeKind.Offline => "friend.notice.offline",
            FriendNoticeKind.Accepted => "friend.notice.accepted",
            FriendNoticeKind.Declined => "friend.notice.declined",
            FriendNoticeKind.Ended => "friend.notice.ended",
            FriendNoticeKind.ModifyGranted => "friend.notice.modify_granted",
            _ => "friend.notice.modify_revoked",
        };
        _model.Post(new Notification
        {
            Kind = NotificationKind.Tip,
            Body = NoticeText.Of(key, _nameOf(notice.Friend)),
            UniqueKey = "friend:" + notice.Friend,   // a friend's newest notice replaces their last
            // pressing the online or offline notice opens the conversation with that friend (the official viewer's friend notice does the same)
            Clicked = notice.Kind is FriendNoticeKind.Online or FriendNoticeKind.Offline && _openConversation is { } open ? () => open(notice.Friend) : null,
        });
    }

    private void SyncFriendOffers()
    {
        var current = _friends.Offers;
        foreach (var offer in current.Where(o => !_friendOffers.ContainsKey(o.From)))
        {
            Guid from = offer.From;
            _friendOffers[from] = _model.Post(new Notification
            {
                Kind = NotificationKind.Offer,
                Body = NoticeText.Of("friend.offer.notice", offer.Name),
                Details = string.IsNullOrWhiteSpace(offer.Message) ? [] : [NoticeText.Raw(offer.Message)],
                Buttons = [new NoticeButton(NoticeText.Of("button.accept"), 0), new NoticeButton(NoticeText.Of("button.decline"), 1)],
                Source = from,
                Responder = a =>
                {
                    _friendOffers.Remove(from);
                    if (a.Button == 0)
                        _friends.Accept(from);
                    else if (a.Button == 1)
                        _friends.Decline(from);
                },
            });
        }
        Retire(_friendOffers, current.Select(o => o.From).ToHashSet());
    }

    // ---------------- teleport offers and requests ----------------

    private void SyncTeleport()
    {
        var offers = _teleport.Offers;
        foreach (var offer in offers.Where(o => !_teleportOffers.ContainsKey(o.LureId)))
        {
            Guid from = offer.From, lure = offer.LureId;
            _teleportOffers[lure] = _model.Post(new Notification
            {
                Kind = NotificationKind.Offer,
                Body = NoticeText.Of("teleport.offer.notice", offer.Name),
                Details = string.IsNullOrWhiteSpace(offer.Message) ? [] : [NoticeText.Raw(offer.Message)],
                Buttons = [new NoticeButton(NoticeText.Of("button.teleport"), 0), new NoticeButton(NoticeText.Of("button.cancel"), 1)],
                Source = from,
                Sound = Viewer.Shell.Sound.UiSoundSlot.NewIncomingIMSession,   // the official teleport offer names this sound
                Responder = a =>
                {
                    _teleportOffers.Remove(lure);
                    if (a.Button == 0)
                        _teleport.Accept(from);
                    else if (a.Button == 1)
                        _teleport.Decline(from);
                },
            });
        }
        Retire(_teleportOffers, offers.Select(o => o.LureId).ToHashSet());

        var requests = _teleport.Requests;
        foreach (var request in requests.Where(r => !_teleportRequests.ContainsKey(r.From)))
        {
            Guid from = request.From;
            _teleportRequests[from] = _model.Post(new Notification
            {
                Kind = NotificationKind.Offer,
                Body = NoticeText.Of("teleport.request.notice", request.Name),
                Details = string.IsNullOrWhiteSpace(request.Message) ? [] : [NoticeText.Raw(request.Message)],
                Buttons = [new NoticeButton(NoticeText.Of("button.yes"), 0), new NoticeButton(NoticeText.Of("button.no"), 1)],
                Source = from,
                Responder = a =>
                {
                    _teleportRequests.Remove(from);
                    if (a.Button == 0)
                        _teleport.OfferToRequester(from);
                    else if (a.Button == 1)
                        _teleport.DeclineRequest(from);
                },
            });
        }
        Retire(_teleportRequests, requests.Select(r => r.From).ToHashSet());
    }

    /// <summary>Drops the notifications of offers the model no longer holds (answered in another window, or withdrawn).</summary>
    private void Retire<TKey>(Dictionary<TKey, long> posted, HashSet<TKey> held) where TKey : notnull
    {
        foreach (var key in posted.Keys.Where(k => !held.Contains(k)).ToArray())
        {
            _model.Remove(posted[key]);
            posted.Remove(key);
        }
    }

    // ---------------- the grid's notices ----------------

    private void SyncNotices()
    {
        var current = _notices.Notices;
        foreach (var notice in current.Where(n => !_seen.Contains(n)))
        {
            _seen.Add(notice);
            _model.Post(From(notice));
        }
        _seen.IntersectWith(current);   // a notice the model let go of is forgotten here too
    }

    private static Notification From(Notice notice)
    {
        var kind = notice.Kind == Viewer.Core.Session.AlertKind.HomeSet ? NotificationKind.Notify : notice.Severity switch
        {
            NoticeSeverity.Error => NotificationKind.Alert,
            NoticeSeverity.Important => NotificationKind.Notify,
            _ => NotificationKind.Tip,
        };
        return new Notification
        {
            Kind = kind,
            // words the grid sent are shown exactly as it sent them; our own wording is a string-table key
            Body = notice.Key == "notice.raw" ? NoticeText.Raw(notice.Arg ?? "") : NoticeText.Of(notice.Key, notice.Arg),
            Buttons = kind == NotificationKind.Alert ? [new NoticeButton(NoticeText.Of("button.ok"), 0)] : [],
            DefaultButton = kind == NotificationKind.Alert ? 0 : null,   // Enter and Esc both answer an error alert
            CancelButton = kind == NotificationKind.Alert ? 0 : null,
        };
    }
}
