// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Actions;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;
using Viewer.Shell.Addons;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Notecards: the Linden notecard file (text and embedded items, versions 1 and 2), the official viewer's rules for reading a
/// card and copying an item out of it, finding links in the text, opening a card from the inventory through a fake transport,
/// the window's model (links, landmarks, copy to inventory, text copying), several windows at once, and which cards add-ons may
/// read. Fixtures in the official format under fixtures/notecards. Pure, runs in parallel.
/// </summary>
public class NotecardTests
{
    private static readonly Guid Agent = Guid.Parse("0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0");
    private static readonly Guid Landmark = Guid.Parse("a1b2c3d4-0000-4000-8000-000000000001");
    private static readonly Guid LandmarkAsset = Guid.Parse("a1b2c3d4-0000-4000-8000-0000000000a1");
    private static readonly Guid Box = Guid.Parse("a1b2c3d4-0000-4000-8000-000000000002");

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(TestPaths.RepoRoot, "fixtures", "notecards", name));

    private static string E(int index) => char.ConvertFromUtf32(NotecardFormat.FirstEmbeddedChar + index);

    private static NotecardDocument Doc(string name)
    {
        var parsed = NotecardFormat.Parse(Fixture(name));
        Assert.Null(parsed.Error);
        return Assert.IsType<NotecardDocument>(parsed.Document);
    }

    private static NotecardAccess Mine(uint ownerMask = 0x7fffffff, Guid? asset = null) =>
        new(Guid.NewGuid(), asset ?? Guid.NewGuid(), Agent, new NotecardPermissions(Agent, Agent, 0x7fffffff, ownerMask, 0, 0, 0x7fffffff), InLibrary: false);

    private static NotecardAccess Library(uint everyoneMask) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Agent, new NotecardPermissions(Guid.NewGuid(), Guid.NewGuid(), 0x7fffffff, 0x7fffffff, 0, everyoneMask, 0x7fffffff), InLibrary: true);

    // ---------------- the file ----------------

    [Fact]
    public void A_plain_card_reads_its_text_whole()
    {
        var doc = Doc("plain.notecard");
        Assert.Equal(2, doc.Version);
        Assert.Empty(doc.Items);
        Assert.StartsWith("Welcome to the example shop.\nSee https://example.org/help", doc.Text);
        Assert.EndsWith("Thanks!\n", doc.Text);
    }

    [Fact]
    public void Embedded_items_of_each_kind_are_read_with_their_place_in_the_text_and_their_permissions()
    {
        var doc = Doc("embedded.notecard");
        Assert.Equal(2, doc.Items.Count);
        var lm = doc.Items[0];
        Assert.Equal((0, Landmark, LandmarkAsset, "Beach", "Example Region (128, 128, 25)", ItemKind.Landmark), (lm.Index, lm.ItemId, lm.AssetId, lm.Name, lm.Description, lm.Kind));
        Assert.Equal(3, lm.AssetType);
        Assert.Equal(0x7fffffffu, lm.Permissions.OwnerMask);
        Assert.Equal(Agent, lm.Permissions.Owner);
        var box = doc.Items[1];
        Assert.Equal((1, Box, "Gift box", ItemKind.Object, 6), (box.Index, box.ItemId, box.Name, box.Kind, box.AssetType));
        Assert.Equal(0x8000u, box.Permissions.OwnerMask);
        Assert.Equal("Landmark: " + E(0) + "\nThe box: " + E(1) + "\nGrüße, café — 日本語 ✓\n", doc.Text);
        Assert.Same(lm, doc.ItemFor(NotecardFormat.FirstEmbeddedChar));
        Assert.Same(box, doc.ItemFor(NotecardFormat.FirstEmbeddedChar + 1));
        Assert.Null(doc.ItemFor('L'));
        Assert.Equal("Landmark: Beach\nThe box: Gift box\nGrüße, café — 日本語 ✓\n", doc.PlainText);
    }

    [Fact]
    public void A_version_1_card_maps_its_high_bytes_to_embedded_items()
    {
        var doc = Doc("version1.notecard");
        Assert.Equal(1, doc.Version);
        Assert.Equal(ItemKind.Notecard, Assert.Single(doc.Items).Kind);
        Assert.Equal("Old card with " + E(0) + " inside.\n", doc.Text);
        Assert.Equal("Old card with Read me inside.\n", doc.PlainText);
    }

    [Fact]
    public void An_empty_card_has_no_text_and_no_items()
    {
        var doc = Doc("empty.notecard");
        Assert.Equal("", doc.Text);
        Assert.Empty(doc.Items);
        Assert.Empty(doc.Segments());
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("hello", "hello")]
    [InlineData("hello there\n\0", "hello there\n")]
    public void A_file_without_the_header_is_a_version_0_card_of_plain_text(string file, string text)
    {
        var doc = NotecardFormat.Parse(Encoding.UTF8.GetBytes(file)).Document;
        Assert.NotNull(doc);
        Assert.Equal((0, text), (doc.Version, doc.Text));
        Assert.Empty(doc.Items);
    }

    [Fact]
    public void A_blank_line_before_the_text_length_is_passed_over()
    {
        var doc = NotecardFormat.Parse(Encoding.UTF8.GetBytes("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\n\nText length 2\nhi}\n")).Document;
        Assert.Equal("hi", doc?.Text);
    }

    [Theory]
    [InlineData("Linden text version 3\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 0\n}\n")]
    [InlineData("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 1\n}\nText length 0\n}\n")]
    [InlineData("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 50\nshort}\n")]
    [InlineData("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount -1\n}\nText length 0\n}\n")]
    [InlineData("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText size 3\nabc}\n")]
    public void A_malformed_file_is_refused_with_a_reason_and_no_document(string file)
    {
        var parsed = NotecardFormat.Parse(Encoding.UTF8.GetBytes(file));
        Assert.Null(parsed.Document);
        Assert.False(string.IsNullOrEmpty(parsed.Error));
    }

    [Theory]
    [InlineData("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 2\nhi}\n\0")]
    [InlineData("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 2\nhi")]
    public void The_counted_text_is_read_whatever_follows_it(string file) =>
        Assert.Equal("hi", NotecardFormat.Parse(Encoding.UTF8.GetBytes(file)).Document?.Text);   // a NUL after the brace (as OpenSim writes an empty card), or no brace

    [Fact]
    public void A_card_written_here_reads_back_the_same()
    {
        var doc = Doc("embedded.notecard");
        var again = NotecardFormat.Parse(NotecardFormat.Write(doc.Text, doc.Items));
        Assert.Null(again.Error);
        Assert.NotNull(again.Document);
        Assert.Equal(doc.Text, again.Document!.Text);
        Assert.Equal(doc.Items, again.Document.Items);
    }

    [Fact]
    public void A_shadow_id_in_place_of_an_asset_id_is_turned_back_into_the_asset()
    {
        // the legacy form keeps the asset id of an item the owner may not see hidden as "shadow_id", the id XORed with a fixed key
        var doc = Doc("embedded.notecard");
        string file = Encoding.UTF8.GetString(NotecardFormat.Write(doc.Text, doc.Items));
        string shadow = "\t\tshadow_id\t" + Shadow(LandmarkAsset);
        file = file.Replace("\t\tasset_id\t" + LandmarkAsset.ToString("D"), shadow);
        Assert.Contains("shadow_id", file);
        var read = NotecardFormat.Parse(Encoding.UTF8.GetBytes(file)).Document;
        Assert.NotNull(read);
        Assert.Equal(LandmarkAsset, read.Items[0].AssetId);
    }

    private static string Shadow(Guid asset)
    {
        byte[] key = Guid.Parse("3c115e51-04f4-523c-9fa6-98aff1034730").ToByteArray(bigEndian: true);
        byte[] id = asset.ToByteArray(bigEndian: true);
        for (int i = 0; i < 16; i++)
            id[i] ^= key[i];
        return new Guid(id, bigEndian: true).ToString("D");
    }

    // ---------------- links ----------------

    [Fact]
    public void Web_addresses_and_location_links_are_found_in_the_text()
    {
        var segments = Doc("plain.notecard").Segments();
        Assert.Equal(["https://example.org/help", "www.example.org/faq"], segments.Where(s => s.Kind == NotecardSegmentKind.WebLink).Select(s => s.Text));
        Assert.Equal(["secondlife://Example%20Region/128/128/25", "hop://grid.example.org:8002/Example%20Region/10/20/30"],
            segments.Where(s => s.Kind == NotecardSegmentKind.PlaceLink).Select(s => s.Text));
        Assert.Equal(Doc("plain.notecard").Text, string.Concat(segments.Select(s => s.Text)));   // nothing lost or added
    }

    [Theory]
    [InlineData("see http://example.org.", "http://example.org")]
    [InlineData("(https://example.org/a_b?c=1)", "https://example.org/a_b?c=1")]
    [InlineData("go to www.example.org, then", "www.example.org")]
    public void A_link_stops_before_closing_punctuation(string text, string link) =>
        Assert.Equal(link, Assert.Single(NotecardLinks.Split(text), s => s.Kind == NotecardSegmentKind.WebLink).Text);

    [Theory]
    [InlineData("ftp://example.org/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:someone@example.org")]
    [InlineData("http://")]
    [InlineData("no links here at all")]
    public void Other_schemes_and_bare_text_are_not_links(string text)
    {
        var segments = NotecardLinks.Split(text);
        Assert.Equal(text, string.Concat(segments.Select(s => s.Text)));
        Assert.All(segments, s => Assert.Equal(NotecardSegmentKind.Text, s.Kind));
    }

    [Fact]
    public void Embedded_items_are_their_own_segments_named_by_the_item()
    {
        var items = Doc("embedded.notecard").Segments().Where(s => s.Kind == NotecardSegmentKind.Item).ToArray();
        Assert.Equal([(0, "Beach"), (1, "Gift box")], items.Select(s => (s.ItemIndex, s.Text)));
    }

    // ---------------- the rules ----------------

    [Fact]
    public void The_owner_reads_a_card_only_with_copy_permission()
    {
        Assert.True(NotecardRules.CanRead(Mine(NotecardPermissions.Copy | NotecardPermissions.Transfer)));
        Assert.False(NotecardRules.CanRead(Mine(NotecardPermissions.Modify | NotecardPermissions.Transfer)));
    }

    [Fact]
    public void Anyone_else_reads_a_card_by_its_everyone_mask_the_library_included()
    {
        Assert.True(NotecardRules.CanRead(Library(NotecardPermissions.Copy)));
        Assert.False(NotecardRules.CanRead(Library(0)));
    }

    [Fact]
    public void An_item_is_copied_out_only_from_a_readable_card_of_the_users_own_whose_item_the_owner_may_transfer()
    {
        var doc = Doc("embedded.notecard");
        Assert.True(NotecardRules.CanCopyOut(Mine(), doc.Items[0]));                                  // full rights
        Assert.False(NotecardRules.CanCopyOut(Mine(), doc.Items[1]));                                 // copy only: the grid refuses no-transfer
        Assert.False(NotecardRules.CanCopyOut(Mine(NotecardPermissions.Modify), doc.Items[0]));       // the card cannot be read
        Assert.False(NotecardRules.CanCopyOut(Library(NotecardPermissions.Copy), doc.Items[0]));      // the grid reads cards from the own inventory
    }

    // ---------------- opening from the inventory ----------------

    private sealed class Transport(NotecardAccess? access, AssetFetchResult answer) : INotecardTransport
    {
        public int Fetches;
        public NotecardAccess? NotecardAccessOf(Guid itemId) => access is null ? null : access with { ItemId = itemId };
        public Task<AssetFetchResult> FetchNotecardAsync(NotecardAccess a, CancellationToken ct) { Fetches++; return Task.FromResult(answer); }
    }

    private static (InventoryModel Model, Guid Item) InventoryWith(ItemKind kind, string name = "Shop card", string description = "From the shop")
    {
        var model = new InventoryModel();
        Guid root = Guid.NewGuid(), item = Guid.NewGuid();
        model.Apply(new InventorySkeletonReceived(Agent, root, Guid.NewGuid(), Guid.NewGuid(), [new InventoryFolderData(root, Guid.Empty, "My Inventory", FolderKind.Root)], []));
        model.Apply(new InventoryFolderFetched(root, true, 2, 1, [], [new InventoryItemData(item, root, name, kind, 7, 7, 0, null, Description: description)]));
        return (model, item);
    }

    [Fact]
    public async Task Opening_a_readable_card_fetches_and_reads_it()
    {
        var (model, item) = InventoryWith(ItemKind.Notecard);
        var transport = new Transport(Mine(), AssetFetchResult.Ok(Fixture("embedded.notecard")));
        var opened = await new NotecardReader(model, transport).OpenAsync(item);
        Assert.Equal(NotecardOpenStatus.Ok, opened.Status);
        Assert.Equal(("Shop card", "From the shop"), (opened.Name, opened.Description));
        Assert.Equal(2, opened.Document!.Items.Count);
        Assert.Equal(1, transport.Fetches);
        Assert.Null(opened.MessageKey);
    }

    [Fact]
    public async Task A_card_the_agent_may_not_copy_is_not_fetched_and_says_so()
    {
        var (model, item) = InventoryWith(ItemKind.Notecard);
        var transport = new Transport(Mine(NotecardPermissions.Modify), AssetFetchResult.Ok(Fixture("plain.notecard")));
        var opened = await new NotecardReader(model, transport).OpenAsync(item);
        Assert.Equal(NotecardOpenStatus.NotAllowed, opened.Status);
        Assert.Equal(0, transport.Fetches);
        Assert.Equal("notecard.not_allowed", opened.MessageKey);
    }

    [Fact]
    public async Task A_card_with_no_asset_opens_empty_without_a_fetch()
    {
        var (model, item) = InventoryWith(ItemKind.Notecard);
        var transport = new Transport(Mine(asset: Guid.Empty), AssetFetchResult.Of(AssetFetchStatus.Failed));
        var opened = await new NotecardReader(model, transport).OpenAsync(item);
        Assert.Equal(NotecardOpenStatus.Ok, opened.Status);
        Assert.NotNull(opened.Document);
        Assert.Equal("", opened.Document.Text);
        Assert.Equal(0, transport.Fetches);
    }

    [Theory]
    [InlineData(AssetFetchStatus.NotFound, NotecardOpenStatus.Missing, "notecard.missing")]
    [InlineData(AssetFetchStatus.Denied, NotecardOpenStatus.NotAllowed, "notecard.not_allowed")]
    [InlineData(AssetFetchStatus.Failed, NotecardOpenStatus.Failed, "notecard.failed")]
    public async Task A_failed_fetch_says_why(AssetFetchStatus fetched, NotecardOpenStatus status, string key)
    {
        var (model, item) = InventoryWith(ItemKind.Notecard);
        var opened = await new NotecardReader(model, new Transport(Mine(), AssetFetchResult.Of(fetched))).OpenAsync(item);
        Assert.Equal((status, key), (opened.Status, opened.MessageKey));
    }

    [Fact]
    public async Task A_file_that_does_not_read_as_a_notecard_is_unreadable()
    {
        var (model, item) = InventoryWith(ItemKind.Notecard);
        var opened = await new NotecardReader(model, new Transport(Mine(), AssetFetchResult.Ok(Encoding.UTF8.GetBytes("Linden text version 2")))).OpenAsync(item);
        Assert.Equal((NotecardOpenStatus.Unreadable, "notecard.unreadable"), (opened.Status, opened.MessageKey));
        Assert.False(string.IsNullOrEmpty(opened.Error));   // the reason, for the log
    }

    [Fact]
    public async Task Something_other_than_a_notecard_is_not_opened()
    {
        var (model, item) = InventoryWith(ItemKind.Texture);
        var transport = new Transport(Mine(), AssetFetchResult.Ok(Fixture("plain.notecard")));
        var opened = await new NotecardReader(model, transport).OpenAsync(item);
        Assert.Equal(NotecardOpenStatus.NotANotecard, opened.Status);
        Assert.Equal(0, transport.Fetches);
    }

    // ---------------- the window ----------------

    private static NotecardWindowModel Window(string fixture, NotecardAccess? access = null) =>
        new(NotecardReader.FromBytes(Guid.NewGuid(), "Shop card", "From the shop", access ?? Mine(), Fixture(fixture)));

    [Fact]
    public void The_window_shows_the_cards_name_and_text_in_runs()
    {
        var w = Window("plain.notecard");
        Assert.Equal("Shop card", w.Title);
        Assert.Equal(Doc("plain.notecard").Text, string.Concat(w.Segments.Select(s => s.Text)));
    }

    [Fact]
    public void A_web_link_click_asks_the_pipeline_to_open_it_and_a_location_link_asks_nothing_yet()
    {
        var w = Window("plain.notecard");
        var web = w.Segments.Where(s => s.Kind == NotecardSegmentKind.WebLink).ToArray();
        Assert.Equal(2, web.Length);
        Assert.Equal(new OpenUrlIntent("https://example.org/help"), w.LinkClicked(web[0]));
        Assert.Equal(new OpenUrlIntent("http://www.example.org/faq"), w.LinkClicked(web[1]));
        Assert.Null(w.LinkClicked(w.Segments.First(s => s.Kind == NotecardSegmentKind.PlaceLink)));
    }

    [Fact]
    public void A_landmark_offers_its_place_and_a_teleport_and_copy_follows_the_rules()
    {
        var w = Window("embedded.notecard");
        var lm = Assert.IsType<NotecardItemOffer>(w.Offer(0));
        Assert.True(lm.Teleport);
        Assert.True(lm.Copy);
        Assert.Equal("Example Region (128, 128, 25)", lm.Place);
        Assert.Equal(new TeleportLandmarkIntent(LandmarkAsset), w.TeleportTo(0));
        var box = Assert.IsType<NotecardItemOffer>(w.Offer(1));
        Assert.False(box.Teleport);
        Assert.False(box.Copy);
        Assert.Null(w.TeleportTo(1));
        Assert.Null(w.CopyToInventory(1));
        Assert.Null(w.Offer(5));
    }

    [Fact]
    public void Copy_to_inventory_names_the_card_and_the_item_and_leaves_the_folder_to_the_grid()
    {
        var w = Window("embedded.notecard");
        var intent = Assert.IsType<NotecardCopyItemIntent>(w.CopyToInventory(0));
        Assert.Equal((w.Opened.ItemId, Landmark, Guid.Empty), (intent.NotecardItemId, intent.EmbeddedItemId, intent.FolderId));
        Assert.Empty(intent.Validate());
        Assert.NotEmpty(new NotecardCopyItemIntent(Guid.Empty, Landmark).Validate());
        Assert.NotEmpty(new NotecardCopyItemIntent(w.Opened.ItemId, Guid.Empty).Validate());
    }

    [Fact]
    public void A_library_card_offers_no_copy_out()
    {
        var w = Window("embedded.notecard", Library(NotecardPermissions.Copy));
        var lm = Assert.IsType<NotecardItemOffer>(w.Offer(0));
        Assert.True(lm.Teleport);
        Assert.False(lm.Copy);
        Assert.Null(w.CopyToInventory(0));
    }

    [Fact]
    public void Copying_a_selection_gives_plain_text_with_items_as_names()
    {
        var w = Window("embedded.notecard");
        string plain = Doc("embedded.notecard").PlainText;
        Assert.Equal("Landmark: Beach", w.CopyText(0, 15));
        Assert.Equal(plain, w.CopyText(0, plain.Length));
        Assert.Equal("", w.CopyText(500, 3));
    }

    [Fact]
    public void Several_cards_stay_open_and_one_already_open_comes_to_the_front()
    {
        var set = new NotecardWindowSet();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Assert.True(set.Show(a));
        Assert.True(set.Show(b));
        Assert.Equal([a, b], set.Open);
        Assert.Equal(b, set.Front);
        Assert.False(set.Show(a));            // not opened twice
        Assert.Equal([b, a], set.Open);
        Assert.Equal(a, set.Front);
        set.Close(a);
        Assert.Equal([b], set.Open);
        Assert.Equal(b, set.Front);
    }

    [Fact]
    public void Copy_to_inventory_goes_to_the_grid_with_the_fields_the_official_viewer_posts()
    {
        Guid card = Guid.NewGuid();
        var command = Assert.IsType<Viewer.Core.Session.CopyFromNotecardCommand>(new NotecardCopyItemIntent(card, Landmark).ToCommand());
        Assert.Equal((card, Landmark, Guid.Empty), (command.NotecardItemId, command.EmbeddedItemId, command.FolderId));
        var body = Viewer.Core.Protocol.LmvTransport.CopyFromNotecardBody(command);
        Assert.Equal(["callback-id", "folder-id", "item-id", "notecard-id", "object-id"], body.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(card, body["notecard-id"].AsUUID().Guid);
        Assert.Equal(Landmark, body["item-id"].AsUUID().Guid);
        Assert.Equal(Guid.Empty, body["object-id"].AsUUID().Guid);
        Assert.Equal(Guid.Empty, body["folder-id"].AsUUID().Guid);
        Assert.True(Viewer.Core.Protocol.LmvTransport.IsKnownCommand(command));
    }

    [Fact]
    public void A_notecard_is_fetched_from_the_regions_asset_capability_by_its_asset_id()
    {
        var url = Viewer.Core.Protocol.LmvTransport.NotecardAssetUrl(new Uri("https://region.example.org:9000/cap/1234/"), LandmarkAsset);
        Assert.Equal("https://region.example.org:9000/cap/1234/?notecard_id=" + LandmarkAsset.ToString("D"), url.ToString());
        var withQuery = Viewer.Core.Protocol.LmvTransport.NotecardAssetUrl(new Uri("https://region.example.org:9000/cap?a=1"), LandmarkAsset);
        Assert.Equal("https://region.example.org:9000/cap?a=1&notecard_id=" + LandmarkAsset.ToString("D"), withQuery.ToString());
    }

    // ---------------- add-ons ----------------

    [Fact]
    public async Task An_add_on_reads_only_a_card_the_user_dropped_on_it_or_inside_a_dropped_folder()
    {
        var (model, card) = InventoryWith(ItemKind.Notecard);
        Guid root = model.RootId, folder = Guid.NewGuid(), inner = Guid.NewGuid();
        model.Apply(new InventoryChanged([new InventoryFolderData(folder, root, "Bought AO", FolderKind.None)],
            [new InventoryItemData(inner, folder, "Read me", ItemKind.Notecard, 7, 7, 0, null)], [], []));
        var opened = new List<Guid>();
        var gate = new AddonNotecards(model, (id, _) =>
        {
            opened.Add(id);
            return Task.FromResult(NotecardReader.FromBytes(id, "card", "", Mine(), Fixture("embedded.notecard")));
        });

        Assert.Null(await gate.ReadTextAsync(inner));                       // nothing handed over yet
        Assert.Empty(opened);
        gate.Dropped([folder]);
        Assert.True(gate.IsHandedOver(inner));
        Assert.False(gate.IsHandedOver(card));
        Assert.Equal(Doc("embedded.notecard").PlainText, await gate.ReadTextAsync(inner));
        Assert.Null(await gate.ReadTextAsync(card));
        Assert.Equal([inner], opened);
        gate.Dropped([card]);
        Assert.NotNull(await gate.ReadTextAsync(card));
    }

    [Fact]
    public async Task An_add_on_gets_no_text_from_a_card_the_user_may_not_read()
    {
        var (model, card) = InventoryWith(ItemKind.Notecard);
        var gate = new AddonNotecards(model, (id, _) => Task.FromResult(new NotecardOpenResult(NotecardOpenStatus.NotAllowed, id, "card", "", Mine(NotecardPermissions.Modify), null)));
        gate.Dropped([card]);
        Assert.True(gate.IsHandedOver(card));
        Assert.Null(await gate.ReadTextAsync(card));
    }
}
