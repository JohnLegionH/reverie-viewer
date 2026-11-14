// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Viewer.Shell.Notifications;
using Viewer.Shell.Social;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The notices the friend, teleport and notice models already decide, shown as notifications. The models' own receiving code is the
/// real code, fed synthetic events; the feeder only reads what they raise and hold. Every member builds its own objects, so the
/// class runs in parallel.
/// </summary>
public class NoticeFeederTests
{
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Ann = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Bob = Guid.Parse("5e5e5e5e-0000-4000-8000-000000000002");
    private static readonly Guid OfferId = Guid.Parse("0ffe0ffe-0000-4000-8000-000000000001");

    private sealed class Rig
    {
        public double Now = 50;
        public DateTimeOffset Clock = new(2026, 3, 4, 13, 5, 0, TimeSpan.Zero);
        public readonly FakeTransport Transport = new();
        public readonly ActionPipeline Pipeline;
        public readonly NameCache Names = new();
        public readonly NotificationModel Model;
        public readonly FriendsModel Friends;
        public readonly TeleportModel Teleport;
        public readonly NoticeModel Notices;
        public readonly NoticeFeeder Feeder;
        public bool NoticesOn = true;

        public Rig()
        {
            Pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
            Model = new NotificationModel(() => Now);
            Friends = new FriendsModel(Self, Names, Pipeline.Submit, () => NoticesOn);
            Teleport = new TeleportModel(Self, Names, Pipeline.Submit, () => Clock);
            Notices = new NoticeModel(() => Now);
            Feeder = new NoticeFeeder(Model, Friends, Teleport, Notices, id => id == Ann ? "Ann Example" : id == Bob ? "Bob Example" : "Someone");
            Friends.Receive(new FriendsListed([new FriendEntry(Ann, "Ann Example", false, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline)]));
        }

        public IEnumerable<T> Sent<T>() where T : TransportCommand => Transport.Commands.OfType<T>();
    }

    // ---------------- friends ----------------

    [Fact]
    public void A_friend_coming_online_is_a_tip_naming_them_and_their_offline_notice_replaces_it()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendPresenceChanged(Ann, true));
        var tip = Assert.Single(r.Model.Toasts);
        Assert.Equal(NotificationKind.Tip, tip.Kind);
        Assert.Equal("friend.notice.online", tip.Body.Key);
        Assert.Equal("Ann Example", tip.Body.Arg);
        Assert.Empty(r.Model.List);   // a tip is a toast only
        r.Friends.Receive(new FriendPresenceChanged(Ann, false));
        var now = Assert.Single(r.Model.Toasts);
        Assert.Equal("friend.notice.offline", now.Body.Key);
    }

    [Fact]
    public void Friend_notices_switched_off_in_the_settings_show_nothing()
    {
        var r = new Rig { NoticesOn = false };
        r.Friends.Receive(new FriendPresenceChanged(Ann, true));
        Assert.Empty(r.Model.Toasts);
        r.NoticesOn = true;   // switched on again: the next change shows
        r.Friends.Receive(new FriendPresenceChanged(Ann, false));
        Assert.Equal("friend.notice.offline", Assert.Single(r.Model.Toasts).Body.Key);
    }

    [Fact]
    public void An_accepted_or_ended_friendship_is_a_tip()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendshipAnswered(Bob, "Bob Example", true));
        Assert.Equal("friend.notice.accepted", Assert.Single(r.Model.Toasts).Body.Key);
        r.Friends.Receive(new FriendshipEnded(Bob));
        Assert.Contains(r.Model.Toasts, n => n.Body.Key == "friend.notice.ended");
    }

    [Fact]
    public void A_friendship_offer_is_an_offer_and_accept_goes_through_the_friends_model()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendshipOffered(Bob, "Bob Example", OfferId, true, new MessageBody("be my friend", MessageChannel.InstantMessage)));
        var offer = Assert.Single(r.Model.List);
        Assert.Equal(NotificationKind.Offer, offer.Kind);
        Assert.Equal("friend.offer.notice", offer.Body.Key);
        Assert.Equal("Bob Example", offer.Body.Arg);
        Assert.Equal(2, offer.Buttons.Count);
        r.Model.Answer(offer.Id, 0);
        var answer = Assert.Single(r.Sent<FriendshipAnswerCommand>());
        Assert.True(answer.Accept);
        Assert.Equal(OfferId, answer.OfferId);
        Assert.Empty(r.Friends.Offers);
        Assert.Empty(r.Model.List);
    }

    [Fact]
    public void Declining_sends_the_decline_and_an_offer_answered_in_the_people_list_leaves_the_notification()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendshipOffered(Bob, "Bob Example", OfferId, true, new MessageBody("hi", MessageChannel.InstantMessage)));
        r.Model.Answer(Assert.Single(r.Model.List).Id, 1);
        Assert.False(Assert.Single(r.Sent<FriendshipAnswerCommand>()).Accept);

        var r2 = new Rig();
        r2.Friends.Receive(new FriendshipOffered(Bob, "Bob Example", OfferId, true, new MessageBody("hi", MessageChannel.InstantMessage)));
        Assert.Single(r2.Model.List);
        r2.Friends.Accept(Bob);   // answered from the people list instead
        Assert.Empty(r2.Model.List);
    }

    [Fact]
    public void A_friend_notice_goes_to_nearby_chat_unless_the_interface_shows_it_and_always_to_that_friends_conversation()
    {
        foreach (bool inChat in new[] { true, false })
        {
            var hub = new CommsHub(Self, new NameCache(), _ => new ActionResult(ActionStatus.Sent, 1), new SettingsService(null), () => new DateTimeOffset(2026, 3, 4, 13, 5, 0, TimeSpan.Zero),
                noticeText: n => "friend notice text") { FriendNoticesInNearbyChat = inChat };
            hub.Ims.OpenWith(Ann);
            hub.Friends.Receive(new FriendsListed([new FriendEntry(Ann, "Ann Example", false, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline)]));
            hub.Friends.Receive(new FriendPresenceChanged(Ann, true));
            Assert.Equal(inChat ? 1 : 0, hub.Nearby.Lines.Count(l => l.Text == "friend notice text"));
            Assert.Contains(hub.Ims.Open.Single().Lines, l => l.Text == "friend notice text");
        }
    }

    // ---------------- teleport offers and requests ----------------

    [Fact]
    public void A_teleport_offer_waits_as_an_offer_and_teleport_accepts_through_the_teleport_model()
    {
        var r = new Rig();
        var lure = Guid.NewGuid();
        r.Teleport.Receive(new TeleportOffered(Ann, "Ann Example", lure, new MessageBody("join me", MessageChannel.InstantMessage)));
        var offer = Assert.Single(r.Model.List);
        Assert.Equal(NotificationKind.Offer, offer.Kind);
        Assert.Equal("teleport.offer.notice", offer.Body.Key);
        r.Model.Answer(offer.Id, 0);
        var cmd = Assert.Single(r.Sent<TeleportLureAnswerCommand>());
        Assert.True(cmd.Accept);
        Assert.Equal(lure, cmd.LureId);
        Assert.Empty(r.Model.List);
    }

    [Fact]
    public void A_teleport_request_waits_as_an_offer_and_yes_offers_a_teleport_and_no_declines()
    {
        var r = new Rig();
        r.Teleport.Receive(new TeleportRequested(Ann, "Ann Example", new MessageBody("may I come?", MessageChannel.InstantMessage)));
        var req = Assert.Single(r.Model.List);
        Assert.Equal("teleport.request.notice", req.Body.Key);
        r.Model.Answer(req.Id, 0);
        Assert.Equal(Ann, Assert.Single(r.Sent<TeleportOfferCommand>()).Target);

        var r2 = new Rig();
        r2.Teleport.Receive(new TeleportRequested(Ann, "Ann Example", new MessageBody("may I come?", MessageChannel.InstantMessage)));
        r2.Model.Answer(Assert.Single(r2.Model.List).Id, 1);
        Assert.Empty(r2.Sent<TeleportOfferCommand>());
        Assert.Empty(r2.Teleport.Requests);
    }

    // ---------------- parcel and home notices ----------------

    [Fact]
    public void A_parcel_refusal_is_a_tip_in_words_of_our_own()
    {
        var r = new Rig();
        r.Notices.Receive(new ServerAlert("/You are banned from parcel", "", false));
        var tip = Assert.Single(r.Model.Toasts);
        Assert.Equal(NotificationKind.Tip, tip.Kind);
        Assert.Equal("notice.parcel.banned", tip.Body.Key);
    }

    [Fact]
    public void The_answer_to_set_home_is_a_notify_and_stays_until_read()
    {
        var r = new Rig();
        r.Notices.Receive(new ServerAlert("Home position set.", "HomePositionSet", false));
        var n = Assert.Single(r.Model.List);
        Assert.Equal(NotificationKind.Notify, n.Kind);
        Assert.Equal("notice.home.set", n.Body.Key);
    }

    [Fact]
    public void A_plain_grid_alert_is_shown_as_the_grid_sent_it_and_a_modal_one_is_a_modal_alert()
    {
        var r = new Rig();
        r.Notices.Receive(new ServerAlert("The region will restart soon.", "", false));
        Assert.Equal("The region will restart soon.", Assert.Single(r.Model.Toasts).Body.Literal);
        r.Notices.Receive(new ServerAlert("Something went wrong.", "", true));
        Assert.NotNull(r.Model.Modal);
    }

    [Fact]
    public void Each_notice_is_posted_once_even_when_the_model_reports_changes_again()
    {
        var r = new Rig();
        r.Notices.Receive(new ServerAlert("You do not have access to the parcel", "", false));
        r.Notices.Receive(new ParcelCollision(ParcelDenial.Banned));   // raises Changed again, adds no notice
        Assert.Single(r.Model.Toasts);
    }

    [Fact]
    public void The_models_decisions_are_untouched_the_pending_lists_still_hold_what_they_held()
    {
        var r = new Rig();
        r.Friends.Receive(new FriendshipOffered(Bob, "Bob Example", OfferId, true, new MessageBody("hi", MessageChannel.InstantMessage)));
        r.Teleport.Receive(new TeleportRequested(Ann, "Ann Example", new MessageBody("x", MessageChannel.InstantMessage)));
        Assert.Single(r.Friends.Offers);
        Assert.Single(r.Teleport.Requests);
        Assert.Empty(r.Transport.Commands);   // nothing is answered by itself
        Assert.Equal(2, r.Model.List.Count);   // and both wait for the user as notifications
    }
}
