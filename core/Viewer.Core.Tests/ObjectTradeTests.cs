// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Opening an object and copying its contents, buying, and paying, on an in-memory world: what the rules decide and what the controller
/// sends. No engine and no grid; every test builds its own world, so the class runs in parallel.
/// </summary>
public class ObjectTradeTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly RegionInfo Region = new("Here", 1000, 1000, 256, 256, false);
    private static readonly Guid Me = Guid.Parse("0f3f2d4e-7a41-4c1d-9a55-5b0f2f6a1e11");
    private static readonly Guid Seller = Guid.Parse("0f3f2d4e-7a41-4c1d-9a55-5b0f2f6a1e33");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);

    private const uint All = PermissionBits.Copy | PermissionBits.Modify | PermissionBits.Transfer;

    private static TaskItem Item(string name, Guid owner, uint ownerMask, uint everyone = 0) =>
        new(Guid.NewGuid(), name, 6, 6, owner, ownerMask, everyone);

    private static ObjectPropertiesReceived Props(uint local, string name, Guid owner, uint ownerMask, byte saleType, int price) =>
        new(G(local), name, "", owner, owner, Guid.Empty, null, ownerMask, 0, 0, price, saleType != 0, saleType);

    private sealed class Rig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public InteractionController C;
        public List<ObjectId> Opened = new();
        public List<BuyOffer> Offered = new();
        public int Refused;
        public List<PayOffer> PayOffered = new();
        public Dictionary<Guid, int> FolderCount = new();
        public List<Guid> Worn = new();

        public Rig()
        {
            World.BeginRegion(Sim, Region);
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new InteractionController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, Me,
                new SocialHooks(_ => { }, _ => { }, _ => { }, _ => { }, () => { }), _ => false);
            C.OpenRequested += Opened.Add;
            C.BuyOffered += Offered.Add;
            C.BuyRefused += () => Refused++;
            C.PayOffered += PayOffered.Add;
            C.ItemsInFolder = f => FolderCount.GetValueOrDefault(f);
            C.WearFolder = Worn.Add;
        }

        public void Prim(uint local, Vector3 pos, PrimInteraction i) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), 0, 0, pos, Quaternion.Identity, new Vector3(2, 2, 2), Interaction: i));

        public PickResult Down(float x, float y) =>
            ObjectPicker.Pick(World, new PickRay(new Vector3(x, y, 60), new Vector3(0, 0, -1)), (_, _) => 20f, ignoreAgent: Me);

        public void ClickOpenable(uint flags = ObjectFlagBits.YouOwner)
        {
            Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Open, flags));
            C.LeftDown(Down(10, 10));
        }

        public T Last<T>() where T : ActionIntent { var found = Sent.OfType<T>().LastOrDefault(); Assert.NotNull(found); return found!; }
    }

    // ---------------- the rules ----------------

    [Fact]
    public void An_item_the_agent_owns_can_be_copied_when_its_owner_mask_allows_copying()
    {
        Assert.True(ObjectContents.CanCopyItem(Item("a", Me, All), Me));
        Assert.False(ObjectContents.CanCopyItem(Item("a", Me, PermissionBits.Transfer | PermissionBits.Modify), Me));
    }

    [Fact]
    public void An_item_of_another_owner_needs_the_everyone_copy_bit_and_the_owner_transfer_bit()
    {
        Assert.True(ObjectContents.CanCopyItem(Item("a", Seller, PermissionBits.Transfer, PermissionBits.Copy), Me));
        Assert.False(ObjectContents.CanCopyItem(Item("a", Seller, PermissionBits.Transfer | PermissionBits.Copy), Me));   // everyone cannot copy
        Assert.False(ObjectContents.CanCopyItem(Item("a", Seller, PermissionBits.Copy, PermissionBits.Copy), Me));   // not transferable
    }

    [Fact]
    public void Copying_is_accepted_when_every_item_can_be_copied_or_the_agent_owns_the_object()
    {
        var copyable = new[] { Item("a", Me, All), Item("b", Me, All) };
        var locked = new[] { Item("b", Me, PermissionBits.Modify), Item("a", Me, All) };
        Assert.Equal(new ContentsPlan(true, false), ObjectContents.Plan(copyable, Me, youOwnObject: true));
        Assert.Equal(new ContentsPlan(true, true), ObjectContents.Plan(locked, Me, youOwnObject: true));   // moved out of the object instead
        Assert.Equal(new ContentsPlan(false, false), ObjectContents.Plan(locked, Me, youOwnObject: false));
    }

    [Fact]
    public void Once_an_item_has_been_accepted_a_later_one_that_cannot_be_copied_does_not_cancel_the_copy()
    {
        // the official viewer's loop keeps its "accept" flag once it is set; the region refuses the items that may not be taken
        var mixed = new[] { Item("a", Me, All), Item("b", Seller, PermissionBits.Modify) };
        Assert.Equal(new ContentsPlan(true, false), ObjectContents.Plan(mixed, Me, youOwnObject: false));
    }

    [Fact]
    public void An_empty_object_has_nothing_to_copy()
    {
        Assert.False(ObjectContents.Plan([], Me, true).Accepted);
    }

    [Fact]
    public void A_sale_type_comes_from_the_properties_and_an_older_region_that_only_says_for_sale_means_original()
    {
        Assert.Equal(SaleTypes.Copy, ObjectSale.TypeOf(Props(1, "x", Seller, All, SaleTypes.Copy, 0)));
        Assert.Equal(SaleTypes.Original, ObjectSale.TypeOf(new ObjectPropertiesReceived(G(1), "x", "", Seller, Seller, Guid.Empty, null, All, 0, 0, 5, true)));
        Assert.Equal(SaleTypes.Not, ObjectSale.TypeOf(Props(1, "x", Seller, All, 0, 0)));
        Assert.True(ObjectSale.IsForSale(Props(1, "x", Seller, All, SaleTypes.Copy, 0)));
        Assert.False(ObjectSale.IsForSale(Props(1, "x", Seller, All, 0, 0)));
    }

    [Fact]
    public void Buying_needs_the_owner_to_allow_transfer_and_a_copy_sale_to_allow_copying()
    {
        Assert.True(ObjectSale.CanBuy(Props(1, "x", Seller, All, SaleTypes.Copy, 0)));
        Assert.True(ObjectSale.CanBuy(Props(1, "x", Seller, PermissionBits.Transfer, SaleTypes.Original, 5)));
        Assert.False(ObjectSale.CanBuy(Props(1, "x", Seller, PermissionBits.Transfer, SaleTypes.Copy, 5)));   // a copy sale of a no-copy object
        Assert.False(ObjectSale.CanBuy(Props(1, "x", Seller, PermissionBits.Copy, SaleTypes.Original, 5)));   // not transferable
        Assert.False(ObjectSale.CanBuy(Props(1, "x", Seller, All, 0, 0)));
    }

    // ---------------- the intents ----------------

    [Fact]
    public void The_trade_intents_name_the_commands_they_send()
    {
        Assert.Equal(new RequestTaskInventoryCommand(4, G(4)), new RequestContentsIntent(4, G(4)).ToCommand());
        var ids = new[] { Guid.NewGuid() };
        Assert.Equal(new CopyTaskContentsCommand(4, G(4), "Shop", true, ids), new CopyContentsIntent(4, G(4), "Shop", true, ids).ToCommand());
        Assert.Equal(new BuyObjectCommand(4, 2, 0), new BuyObjectIntent(4, 2, 0).ToCommand());
        Assert.Equal(new PayObjectCommand(G(4), 10, "Tip jar"), new PayObjectIntent(G(4), 10, "Tip jar").ToCommand());
    }

    [Fact]
    public void A_price_of_zero_is_a_valid_buy_but_a_bad_sale_type_or_negative_price_is_not()
    {
        Assert.Empty(new BuyObjectIntent(4, 1, 0).Validate());
        Assert.NotEmpty(new BuyObjectIntent(4, 0, 0).Validate());
        Assert.NotEmpty(new BuyObjectIntent(4, 4, 0).Validate());
        Assert.NotEmpty(new BuyObjectIntent(4, 1, -1).Validate());
        Assert.NotEmpty(new BuyObjectIntent(0, 1, 0).Validate());
    }

    [Fact]
    public void Copying_contents_needs_items_and_a_usable_folder_name()
    {
        var ids = new[] { Guid.NewGuid() };
        Assert.Empty(new CopyContentsIntent(4, G(4), "Shop", false, ids).Validate());
        Assert.NotEmpty(new CopyContentsIntent(4, G(4), "Shop", false, []).Validate());
        Assert.NotEmpty(new CopyContentsIntent(4, G(4), "  ", false, ids).Validate());
    }

    // ---------------- open ----------------

    [Fact]
    public void Opening_an_object_asks_for_its_contents_and_its_properties()
    {
        var r = new Rig();
        r.ClickOpenable();
        Assert.Single(r.Opened);
        var ask = r.Last<RequestContentsIntent>();
        Assert.Equal(1u, ask.LocalId);
        Assert.Equal(G(1), ask.ObjectId);
        Assert.Contains(r.Sent, i => i is ObjectPropertiesIntent { LocalId: 1 });
        Assert.False(r.C.Contents!.Loaded);
    }

    [Fact]
    public void The_contents_the_region_sends_are_listed()
    {
        var r = new Rig();
        r.ClickOpenable();
        r.C.Receive(new TaskInventoryReceived(G(1), [Item("a", Me, All), Item("b", Me, All)]));
        Assert.True(r.C.Contents!.Loaded);
        Assert.Equal(2, r.C.Contents.Items!.Count);
    }

    [Fact]
    public void An_answer_for_another_object_is_ignored()
    {
        var r = new Rig();
        r.ClickOpenable();
        r.C.Receive(new TaskInventoryReceived(G(77), [Item("a", Me, All)]));
        Assert.False(r.C.Contents!.Loaded);
    }

    [Fact]
    public void A_failed_read_is_shown_as_failed_not_as_empty()
    {
        var r = new Rig();
        r.ClickOpenable();
        r.C.Receive(new TaskInventoryReceived(G(1), [], Ok: false));
        Assert.True(r.C.Contents!.Failed);
        Assert.False(r.C.CanCopyContents);
    }

    [Fact]
    public void Copy_To_Inventory_sends_every_item_with_a_folder_named_after_the_object()
    {
        var r = new Rig();
        r.ClickOpenable();
        var a = Item("a", Me, All); var b = Item("b", Me, All);
        r.C.Receive(new TaskInventoryReceived(G(1), [a, b]));
        Assert.False(r.C.CanCopyContents);   // the object's name has not arrived yet
        r.C.Receive(Props(1, "Script Shop", Me, All, 0, 0));
        Assert.True(r.C.CanCopyContents);
        Assert.True(r.C.CopyContents(wear: false));
        var copy = r.Last<CopyContentsIntent>();
        Assert.Equal("Script Shop", copy.FolderName);
        Assert.False(copy.Wear);
        Assert.Equal(new[] { a.ItemId, b.ItemId }, copy.ItemIds);
        Assert.Equal(1u, copy.LocalId);
    }

    [Fact]
    public void Copy_and_Wear_asks_for_wearing()
    {
        var r = new Rig();
        r.ClickOpenable();
        r.C.Receive(new TaskInventoryReceived(G(1), [Item("a", Me, All)]));
        r.C.Receive(Props(1, "Outfit", Me, All, 0, 0));
        Assert.True(r.C.CopyContents(wear: true));
        Assert.True(r.Last<CopyContentsIntent>().Wear);
    }

    [Fact]
    public void Contents_that_may_not_be_copied_are_refused_and_nothing_is_sent()
    {
        var r = new Rig();
        r.ClickOpenable(ObjectFlagBits.Modify);   // not the owner, but may modify (so Open is allowed)
        r.C.Receive(new TaskInventoryReceived(G(1), [Item("a", Seller, PermissionBits.Modify)]));
        r.C.Receive(Props(1, "Shop", Seller, All, 0, 0));
        Assert.False(r.C.CopyContents(wear: false));
        Assert.DoesNotContain(r.Sent, i => i is CopyContentsIntent);
    }

    [Fact]
    public void Copy_and_Wear_wears_the_new_folder_once_its_items_have_arrived()
    {
        var r = new Rig();
        var folder = Guid.NewGuid();
        r.C.Receive(new ContentsCopied(G(1), folder, 2, Wear: true));
        r.C.Tick();
        Assert.Empty(r.Worn);   // nothing in the folder yet
        r.FolderCount[folder] = 1;
        r.C.Tick();
        Assert.Empty(r.Worn);   // one of two
        r.FolderCount[folder] = 2;
        r.C.Tick();
        Assert.Equal([folder], r.Worn);
        r.C.Tick();
        Assert.Single(r.Worn);   // once only
    }

    [Fact]
    public void A_copy_without_wearing_does_not_wear_anything()
    {
        var r = new Rig();
        var folder = Guid.NewGuid();
        r.FolderCount[folder] = 3;
        r.C.Receive(new ContentsCopied(G(1), folder, 3, Wear: false));
        r.C.Tick();
        Assert.Empty(r.Worn);
    }

    // ---------------- buy ----------------

    [Fact]
    public void A_click_on_a_Buy_object_asks_for_its_properties_and_offers_the_sale_when_they_arrive()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Buy, 0));
        r.C.LeftDown(r.Down(10, 10));
        Assert.Contains(r.Sent, i => i is ObjectPropertiesIntent { LocalId: 1 });
        Assert.Empty(r.Offered);
        r.C.Receive(Props(1, "Hat", Seller, All, SaleTypes.Copy, 0));
        var offer = Assert.Single(r.Offered);
        Assert.Equal(("Hat", SaleTypes.Copy, 0), (offer.Name, offer.SaleType, offer.Price));
        Assert.Equal(1u, offer.LocalId);
    }

    [Fact]
    public void Confirming_a_free_buy_sends_the_sale_type_and_a_price_of_zero()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Buy, 0));
        r.C.LeftDown(r.Down(10, 10));
        r.C.Receive(Props(1, "Hat", Seller, All, SaleTypes.Copy, 0));
        Assert.True(r.C.ConfirmBuy());
        Assert.Equal(new BuyObjectIntent(1, SaleTypes.Copy, 0), r.Last<BuyObjectIntent>());
        Assert.Null(r.C.Offer);
        Assert.False(r.C.ConfirmBuy());   // only once
    }

    [Fact]
    public void Buying_contents_also_asks_for_what_is_inside()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Buy, 0));
        r.C.LeftDown(r.Down(10, 10));
        r.C.Receive(Props(1, "Box of hats", Seller, All, SaleTypes.Contents, 25));
        Assert.Contains(r.Sent, i => i is RequestContentsIntent { LocalId: 1 });
        r.C.Receive(new TaskInventoryReceived(G(1), [Item("hat", Seller, All)]));
        Assert.Single(r.C.Contents!.Items!);
        r.C.ConfirmBuy();
        Assert.Equal(new BuyObjectIntent(1, SaleTypes.Contents, 25), r.Last<BuyObjectIntent>());
    }

    [Fact]
    public void An_object_that_is_not_for_sale_is_refused_without_an_offer()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Buy, 0));
        r.C.LeftDown(r.Down(10, 10));
        r.C.Receive(Props(1, "Hat", Seller, All, 0, 0));
        Assert.Empty(r.Offered);
        Assert.Equal(1, r.Refused);
    }

    [Fact]
    public void Cancelling_drops_the_offer()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Buy, 0));
        r.C.LeftDown(r.Down(10, 10));
        r.C.Receive(Props(1, "Hat", Seller, All, SaleTypes.Original, 5));
        r.C.CancelBuy();
        Assert.Null(r.C.Offer);
        Assert.False(r.C.ConfirmBuy());
        Assert.DoesNotContain(r.Sent, i => i is BuyObjectIntent);
    }

    [Fact]
    public void The_menu_offers_Buy_for_an_object_that_is_for_sale_and_not_yours()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(0, 0));
        r.C.RightClick(r.Down(10, 10));
        Assert.DoesNotContain(r.C.Entries, e => e.Id == "buy");   // the sale is not known yet
        r.C.Receive(Props(1, "Hat", Seller, All, SaleTypes.Copy, 0));
        var buy = Assert.Single(r.C.Entries, e => e.Id == "buy");
        Assert.True(buy.Enabled);
        Assert.DoesNotContain(r.C.Entries, e => e.Id == "take");   // Buy takes Take's place, as in the official menu
        Assert.True(r.C.Choose("buy"));
        Assert.Single(r.Offered);
    }

    [Fact]
    public void The_menu_has_no_Buy_for_your_own_object_or_one_not_for_sale()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(0, ObjectFlagBits.YouOwner));
        r.C.RightClick(r.Down(10, 10));
        r.C.Receive(Props(1, "Hat", Me, All, SaleTypes.Copy, 0));
        Assert.DoesNotContain(r.C.Entries, e => e.Id == "buy");
        Assert.Contains(r.C.Entries, e => e.Id == "take");
        var r2 = new Rig();
        r2.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(0, 0));
        r2.C.RightClick(r2.Down(10, 10));
        r2.C.Receive(Props(1, "Hat", Seller, All, 0, 0));
        Assert.DoesNotContain(r2.C.Entries, e => e.Id == "buy");
    }

    // ---------------- pay ----------------

    [Fact]
    public void Pay_is_offered_only_for_an_object_that_takes_money()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(0, 0));
        r.C.RightClick(r.Down(10, 10));
        Assert.DoesNotContain(r.C.Entries, e => e.Id == "pay");
        var r2 = new Rig();
        r2.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(0, ObjectFlagBits.TakesMoney));
        r2.C.RightClick(r2.Down(10, 10));
        Assert.Contains(r2.C.Entries, e => e.Id == "pay");
    }

    [Fact]
    public void Choosing_Pay_asks_what_the_object_charges_and_the_window_opens_on_the_answer()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(0, ObjectFlagBits.TakesMoney));
        r.C.RightClick(r.Down(10, 10));
        r.C.Receive(Props(1, "Tip jar", Seller, All, 0, 0));
        Assert.True(r.C.Choose("pay"));
        Assert.Contains(r.Sent, i => i is RequestPayPriceIntent { LocalId: 1 });
        Assert.Empty(r.PayOffered);
        r.C.Receive(new PayPriceReceived(G(1), 10, [5, 10, 25, 100]));
        var offer = Assert.Single(r.PayOffered);
        Assert.Equal(("Tip jar", 10), (offer.Name, offer.DefaultPrice));
        Assert.True(r.C.ConfirmPay(25));
        Assert.Equal(new PayObjectIntent(G(1), 25, "Tip jar"), r.Last<PayObjectIntent>());
    }

    [Fact]
    public void A_pay_price_nobody_asked_for_opens_nothing()
    {
        var r = new Rig();
        r.C.Receive(new PayPriceReceived(G(9), 10, [10]));
        Assert.Empty(r.PayOffered);
        Assert.False(r.C.ConfirmPay(10));
    }

    [Fact]
    public void A_click_on_a_Pay_object_asks_the_price_only_when_it_takes_money()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Pay, 0));
        r.C.LeftDown(r.Down(10, 10));
        Assert.DoesNotContain(r.Sent, i => i is RequestPayPriceIntent);
        var r2 = new Rig();
        r2.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Pay, ObjectFlagBits.TakesMoney));
        r2.C.LeftDown(r2.Down(10, 10));
        Assert.Contains(r2.Sent, i => i is RequestPayPriceIntent { LocalId: 1 });
    }

    // ---------------- the wire ----------------

    [Fact]
    public void The_adapter_knows_the_trade_commands()
    {
        Assert.True(Viewer.Core.Protocol.LmvTransport.IsKnownCommand(new RequestTaskInventoryCommand(1, G(1))));
        Assert.True(Viewer.Core.Protocol.LmvTransport.IsKnownCommand(new CopyTaskContentsCommand(1, G(1), "x", false, [G(2)])));
        Assert.True(Viewer.Core.Protocol.LmvTransport.IsKnownCommand(new BuyObjectCommand(1, 1, 0)));
        Assert.True(Viewer.Core.Protocol.LmvTransport.IsKnownCommand(new RequestPayPriceCommand(1, G(1))));
        Assert.True(Viewer.Core.Protocol.LmvTransport.IsKnownCommand(new PayObjectCommand(G(1), 5, "x")));
    }
}
