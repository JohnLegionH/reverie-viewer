// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Shell.Notifications;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Inventory offers from residents and objects: the protocol messages in both directions (offer in, answer out), the intent that answers,
/// and the notification with Keep, Discard and Block. Offline; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class InventoryOfferTests
{
    private static readonly Guid From = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000f1");
    private static readonly Guid Transaction = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000f2");
    private static readonly Guid ItemId = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000f3");
    private static readonly Guid Notecards = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000f4");
    private static readonly Guid Trash = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000f5");
    private const int NotecardType = 7;

    private static InstantMessage Im(InstantMessageDialog dialog, byte[] bucket) => new()
    {
        FromAgentID = new UUID(From), FromAgentName = "Example Giver", ToAgentID = new UUID(Guid.NewGuid()), IMSessionID = new UUID(Transaction),
        Dialog = dialog, Offline = InstantMessageOnline.Online, Message = "'A note' (example.org)", BinaryBucket = bucket,
    };

    private static ActionIntent At(List<ActionIntent> list, int i)
    {
        Assert.True(list.Count > i, $"{list.Count} intents sent, wanted at least {i + 1}");
        return list[i];
    }

    private static byte[] AgentBucket(byte type, Guid item) => [type, .. item.ToByteArray(bigEndian: true)];

    // ---------------- the offer, in ----------------

    [Fact]
    public void An_offer_from_a_resident_names_the_type_and_the_copy_the_grid_put_in_inventory()
    {
        var offer = LmvTransport.ToInventoryOffer(Im(InstantMessageDialog.InventoryOffered, AgentBucket(NotecardType, ItemId)));
        Assert.NotNull(offer);
        Assert.Equal(From, offer!.FromId);
        Assert.Equal("Example Giver", offer.FromName);
        Assert.False(offer.FromObject);
        Assert.Equal(Transaction, offer.TransactionId);
        Assert.Equal(NotecardType, offer.AssetType);
        Assert.Equal(ItemId, offer.ItemId);
        Assert.Equal("'A note' (example.org)", offer.Description);
    }

    [Fact]
    public void A_bucket_with_bytes_after_the_item_id_still_names_the_type_and_the_item()
    {
        byte[] longer = [.. AgentBucket(NotecardType, ItemId), 0];
        var offer = LmvTransport.ToInventoryOffer(Im(InstantMessageDialog.InventoryOffered, longer));
        Assert.NotNull(offer);
        Assert.Equal(ItemId, offer!.ItemId);
        Assert.Equal(NotecardType, offer.AssetType);
    }

    [Fact]
    public void An_offer_from_an_object_carries_only_the_type_and_the_item_is_not_in_inventory_yet()
    {
        var offer = LmvTransport.ToInventoryOffer(Im(InstantMessageDialog.TaskInventoryOffered, [NotecardType]));
        Assert.NotNull(offer);
        Assert.True(offer!.FromObject);
        Assert.Equal(NotecardType, offer.AssetType);
        Assert.Equal(Guid.Empty, offer.ItemId);
    }

    [Fact]
    public void Other_dialogs_and_a_message_too_short_for_its_item_are_not_offers()
    {
        Assert.Null(LmvTransport.ToInventoryOffer(Im(InstantMessageDialog.MessageFromAgent, AgentBucket(NotecardType, ItemId))));
        Assert.Null(LmvTransport.ToInventoryOffer(Im(InstantMessageDialog.InventoryOffered, [NotecardType, 1, 2, 3])));
        Assert.Null(LmvTransport.ToInventoryOffer(Im(InstantMessageDialog.TaskInventoryOffered, [])));
    }

    // ---------------- the answer, out ----------------

    [Theory]
    [InlineData(false, true, InstantMessageDialog.InventoryAccepted)]
    [InlineData(false, false, InstantMessageDialog.InventoryDeclined)]
    [InlineData(true, true, InstantMessageDialog.TaskInventoryAccepted)]
    [InlineData(true, false, InstantMessageDialog.TaskInventoryDeclined)]
    public void The_answer_is_the_offers_dialog_plus_one_to_accept_and_plus_two_to_decline(bool fromObject, bool accept, InstantMessageDialog expected)
    {
        var reply = LmvTransport.ToInventoryOfferReply(new InventoryOfferAnswerCommand(From, Transaction, fromObject, accept, Notecards));
        Assert.Equal(expected, reply.Dialog);
        Assert.Equal(Transaction, reply.Session);
    }

    [Fact]
    public void Accepting_carries_the_destination_folder_and_declining_carries_nothing()
    {
        var yes = LmvTransport.ToInventoryOfferReply(new InventoryOfferAnswerCommand(From, Transaction, false, true, Notecards));
        Assert.Equal(Notecards.ToByteArray(bigEndian: true), yes.Bucket);
        var no = LmvTransport.ToInventoryOfferReply(new InventoryOfferAnswerCommand(From, Transaction, false, false, Notecards));
        Assert.Empty(no.Bucket);
        var noFolder = LmvTransport.ToInventoryOfferReply(new InventoryOfferAnswerCommand(From, Transaction, false, true, Guid.Empty));
        Assert.Empty(noFolder.Bucket);   // where the grid put it
    }

    [Fact]
    public void The_adapter_knows_the_new_commands()
    {
        Assert.True(LmvTransport.IsKnownCommand(new InventoryOfferAnswerCommand(From, Transaction, false, true, Notecards)));
        Assert.True(LmvTransport.IsKnownCommand(new AttachWorldObjectCommand(7, 6)));
    }

    [Theory]
    [InlineData(6, 0x86)]
    [InlineData(1, 0x81)]
    [InlineData(55, 0xB7)]
    public void The_attach_message_adds_to_what_is_worn_at_the_point(int point, int expected) =>
        Assert.Equal((byte)expected, LmvTransport.AttachPointByte(point));

    // ---------------- the intents ----------------

    [Fact]
    public void An_answer_needs_the_sender_and_the_transaction()
    {
        Assert.Contains(new InventoryOfferAnswerIntent(Guid.Empty, Transaction, false, true, Notecards).Validate(), i => i.Code == "offer.no_sender");
        Assert.Contains(new InventoryOfferAnswerIntent(From, Guid.Empty, false, true, Notecards).Validate(), i => i.Code == "offer.no_transaction");
        Assert.Empty(new InventoryOfferAnswerIntent(From, Transaction, false, true, Notecards).Validate());
        Assert.Empty(new InventoryOfferAnswerIntent(From, Transaction, false, false, Guid.Empty).Validate());
    }

    [Fact]
    public void The_answer_goes_through_the_pipeline_as_its_command_and_so_does_attaching()
    {
        var transport = new FakeTransport();
        var pipeline = new ActionPipeline(() => transport, () => true, new Viewer.Core.Diagnostics.CoreLog(new ListSink(), "test"));
        Assert.Equal(ActionStatus.Sent, pipeline.Submit(new InventoryOfferAnswerIntent(From, Transaction, true, true, Notecards)).Status);
        Assert.Equal(new InventoryOfferAnswerCommand(From, Transaction, true, true, Notecards), transport.Commands[^1]);
        Assert.Equal(ActionStatus.Sent, pipeline.Submit(new AttachWorldObjectIntent(77, 6)).Status);
        Assert.Equal(new AttachWorldObjectCommand(77, 6), transport.Commands[^1]);
    }

    [Theory]
    [InlineData(0u, 6, "attach.no_object")]
    [InlineData(5u, 0, "attach.bad_point")]
    [InlineData(5u, 56, "attach.bad_point")]
    public void Attaching_needs_an_object_and_a_real_attachment_point(uint local, int point, string code) =>
        Assert.Contains(new AttachWorldObjectIntent(local, point).Validate(), i => i.Code == code);

    // ---------------- through the session ----------------

    [Fact]
    public void An_offer_reaches_the_communication_channel_and_is_never_written_to_a_capture()
    {
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new Viewer.Core.Diagnostics.CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(new SimRef(1099511628032256UL, "Example Region"));
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        var offer = new InventoryOffered(From, "Example Giver", false, Transaction, NotecardType, ItemId, "A note");
        fake.Raise(offer);
        Assert.Contains(offer, seen);
        Assert.False(Viewer.Core.Replay.CaptureFile.IsRecorded(offer));
    }

    // ---------------- the notification ----------------

    private sealed class Rig
    {
        public readonly NotificationModel Model = new(() => 1000);
        public readonly InventoryOffers Offers;
        public readonly List<ActionIntent> Sent = new();
        public readonly List<(MuteKind Kind, Guid Id, string Name)> Blocks = new();
        public bool Blocked;

        public Rig()
        {
            Offers = new InventoryOffers(Model, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); })
            {
                FolderFor = type => type == NotecardType ? Notecards : Guid.Empty,
                TrashFolder = () => Trash,
                Block = (k, id, n) => Blocks.Add((k, id, n)),
                IsBlocked = (_, _) => Blocked,
            };
        }

        public InventoryOffered Agent() => new(From, "Example Giver", false, Transaction, NotecardType, ItemId, "A note");
        public InventoryOffered Object() => new(From, "Example Box", true, Transaction, NotecardType, Guid.Empty, "A note");
        public Notification Only() => Assert.Single(Model.List);
    }

    [Fact]
    public void An_offer_waits_for_the_user_with_keep_and_discard_and_a_block_button()
    {
        var r = new Rig();
        r.Offers.Receive(r.Agent());
        var n = r.Only();
        Assert.Equal(NotificationKind.Offer, n.Kind);
        Assert.Equal("Example Giver", n.Title!.Literal);
        Assert.Equal("A note", n.Body.Literal);
        Assert.Equal([InventoryOffers.KeepIndex, InventoryOffers.DiscardIndex], n.Buttons.Select(b => b.Index).ToArray());
        Assert.True(n.CanBlock);
        Assert.Empty(r.Sent);   // nothing is accepted without the user's answer
    }

    [Fact]
    public void Keep_accepts_into_the_folder_of_the_items_type()
    {
        var r = new Rig();
        r.Offers.Receive(r.Agent());
        Assert.True(r.Model.Answer(r.Only().Id, InventoryOffers.KeepIndex));
        var answer = Assert.IsType<InventoryOfferAnswerIntent>(Assert.Single(r.Sent));
        Assert.Equal(new InventoryOfferAnswerIntent(From, Transaction, false, true, Notecards), answer);
    }

    [Fact]
    public void Discard_declines_and_puts_the_item_the_grid_already_delivered_in_the_trash()
    {
        var r = new Rig();
        r.Offers.Receive(r.Agent());
        Assert.True(r.Model.Answer(r.Only().Id, InventoryOffers.DiscardIndex));
        Assert.Equal(new InventoryOfferAnswerIntent(From, Transaction, false, false, Guid.Empty), At(r.Sent, 0));
        Assert.Equal(new InventoryMoveIntent(ItemId, Trash), At(r.Sent, 1));
        Assert.Equal(2, r.Sent.Count);
    }

    [Fact]
    public void Discarding_an_offer_from_an_object_only_declines_because_there_is_no_item_yet()
    {
        var r = new Rig();
        r.Offers.Receive(r.Object());
        Assert.True(r.Model.Answer(r.Only().Id, InventoryOffers.DiscardIndex));
        Assert.Equal(new InventoryOfferAnswerIntent(From, Transaction, true, false, Guid.Empty), Assert.Single(r.Sent));
    }

    [Fact]
    public void Block_declines_discards_and_blocks_the_resident_or_the_object()
    {
        var r = new Rig();
        r.Offers.Receive(r.Agent());
        Assert.True(r.Model.Answer(r.Only().Id, NotificationAnswer.BlockIndex));
        Assert.Equal(new InventoryOfferAnswerIntent(From, Transaction, false, false, Guid.Empty), At(r.Sent, 0));
        Assert.Equal(new InventoryMoveIntent(ItemId, Trash), At(r.Sent, 1));
        Assert.Equal([(MuteKind.Resident, From, "Example Giver")], r.Blocks);

        var o = new Rig();
        o.Offers.Receive(o.Object());
        Assert.True(o.Model.Answer(o.Only().Id, NotificationAnswer.BlockIndex));
        Assert.Equal([(MuteKind.Object, From, "Example Box")], o.Blocks);
    }

    [Fact]
    public void Closing_the_offer_without_an_answer_sends_nothing()
    {
        var r = new Rig();
        r.Offers.Receive(r.Agent());
        Assert.True(r.Model.Answer(r.Only().Id, -1));
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void An_offer_from_a_blocked_sender_is_declined_at_once_and_never_shown()
    {
        var r = new Rig { Blocked = true };
        r.Offers.Receive(r.Agent());
        Assert.Empty(r.Model.List);
        Assert.Equal(new InventoryOfferAnswerIntent(From, Transaction, false, false, Guid.Empty), At(r.Sent, 0));
        Assert.Equal(new InventoryMoveIntent(ItemId, Trash), At(r.Sent, 1));
        Assert.Equal(1, r.Offers.Declined);

        var o = new Rig { Blocked = true };
        o.Offers.Receive(o.Object());
        Assert.Equal(new InventoryOfferAnswerIntent(From, Transaction, true, false, Guid.Empty), Assert.Single(o.Sent));
    }

    [Fact]
    public void The_hub_turns_a_session_event_into_the_offer_notification()
    {
        var hub = new NotificationHub(i => new ActionResult(ActionStatus.Sent, 1), () => 1000, () => "Test User");
        hub.Enqueue(new InventoryOffered(From, "Example Giver", false, Transaction, NotecardType, ItemId, "A note"));
        hub.Pump();
        Assert.Single(hub.Model.List);
    }
}
