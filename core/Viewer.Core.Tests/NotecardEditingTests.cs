// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The notecard editor: the official viewer's rules for editing a card (who may modify it, the 65,536-byte text limit, which items
/// may be dropped into the text and what a click on one does), the editor's text with embedded items standing as named marks,
/// and the edit session (changes, Save through a fake transport, Close with unsaved changes, read-only cards). Pure, runs in parallel.
/// </summary>
public class NotecardEditingTests
{
    private static readonly Guid Agent = Guid.Parse("0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0");
    private static readonly Guid Other = Guid.Parse("7a6b5c4d-3e2f-4011-9223-344556677889");
    private static readonly Guid Card = Guid.Parse("c0c0c0c0-0000-4000-8000-000000000001");
    private static readonly Guid Box = Guid.Parse("a1b2c3d4-0000-4000-8000-000000000002");
    private static readonly Guid Home = Guid.Parse("a1b2c3d4-0000-4000-8000-000000000001");
    private const uint All = 0x7fffffff;
    private const char M = NotecardEditBuffer.Mark;

    private static string E(int index) => char.ConvertFromUtf32(NotecardFormat.FirstEmbeddedChar + index);

    private static NotecardAccess Access(uint ownerMask, bool library = false, Guid objectId = default, Guid? owner = null, uint everyone = 0) =>
        new(Card, Guid.NewGuid(), Agent, new NotecardPermissions(Agent, owner ?? Agent, All, ownerMask, 0, everyone, All), library, objectId);

    private static NotecardEmbeddedItem Item(int index, Guid id, string name, ItemKind kind, int assetType, uint nextOwner = All) =>
        new(index, id, Guid.NewGuid(), name, "", assetType, assetType, kind, new NotecardPermissions(Agent, Agent, All, All, 0, 0, nextOwner));

    [Fact]
    public void A_card_may_be_modified_by_its_owner_with_the_modify_bit_and_not_from_the_library_or_a_locked_object()
    {
        Assert.True(NotecardEditRules.CanModify(Access(All), objectModify: true));
        Assert.False(NotecardEditRules.CanModify(Access(All & ~NotecardPermissions.Modify), objectModify: true));
        Assert.False(NotecardEditRules.CanModify(Access(All, library: true), objectModify: true));
        Assert.False(NotecardEditRules.CanModify(Access(All, objectId: Box), objectModify: false));
        Assert.True(NotecardEditRules.CanModify(Access(All, objectId: Box), objectModify: true));
        // someone else's card: the everyone mask decides
        Assert.False(NotecardEditRules.CanModify(Access(All, owner: Other), objectModify: true));
        Assert.True(NotecardEditRules.CanModify(Access(0, owner: Other, everyone: All), objectModify: true));
    }

    [Fact]
    public void The_text_limit_is_65536_bytes_counted_in_UTF8_and_the_description_127()
    {
        Assert.Equal(65536, NotecardEditRules.MaxTextBytes);
        Assert.Equal(127, NotecardEditRules.MaxDescriptionBytes);
        Assert.Equal(3, NotecardEditRules.TextBytes("aé"));
        Assert.Equal(5, NotecardEditRules.TextBytes("a" + E(0)));   // an embedded item is one character, four bytes
    }

    [Fact]
    public void Items_drop_into_a_card_only_when_the_next_owner_may_copy_modify_and_transfer_them()
    {
        foreach (var kind in new[] { ItemKind.Landmark, ItemKind.Texture, ItemKind.Notecard, ItemKind.Object, ItemKind.Script, ItemKind.Clothing,
                     ItemKind.Bodypart, ItemKind.Animation, ItemKind.Gesture, ItemKind.Sound, ItemKind.CallingCard, ItemKind.Settings, ItemKind.Material, ItemKind.Mesh })
            Assert.True(NotecardEditRules.CanEmbed(kind, All), kind.ToString());
        Assert.False(NotecardEditRules.CanEmbed(ItemKind.Landmark, All & ~NotecardPermissions.Copy));
        Assert.False(NotecardEditRules.CanEmbed(ItemKind.Landmark, All & ~NotecardPermissions.Transfer));
        Assert.False(NotecardEditRules.CanEmbed(ItemKind.Landmark, All & ~NotecardPermissions.Modify));
        Assert.False(NotecardEditRules.CanEmbed(ItemKind.Link, All));
        Assert.False(NotecardEditRules.CanEmbed(ItemKind.Other, All));
    }

    [Fact]
    public void A_click_on_an_embedded_item_opens_a_place_picture_or_sound_and_offers_a_copy_of_anything_else()
    {
        foreach (var kind in new[] { ItemKind.Landmark, ItemKind.Texture, ItemKind.Sound, ItemKind.CallingCard, ItemKind.Settings, ItemKind.Material })
            Assert.Equal(EmbeddedClick.Open, NotecardEditRules.ClickOf(kind));
        foreach (var kind in new[] { ItemKind.Notecard, ItemKind.Script, ItemKind.Clothing, ItemKind.Object, ItemKind.Bodypart, ItemKind.Animation, ItemKind.Gesture })
            Assert.Equal(EmbeddedClick.OfferCopy, NotecardEditRules.ClickOf(kind));
        Assert.Equal(EmbeddedClick.None, NotecardEditRules.ClickOf(ItemKind.Other));
    }

    [Fact]
    public void A_card_shows_each_embedded_item_as_its_name_between_marks_and_reads_back_unchanged()
    {
        var doc = new NotecardDocument(2, "Go " + E(0) + " now", [Item(0, Home, "Home", ItemKind.Landmark, 3)]);
        var buffer = new NotecardEditBuffer(doc);
        string shown = buffer.ToDisplay(doc.Text);
        Assert.Equal("Go " + M + "Home" + M + " now", shown);
        var (text, items) = buffer.FromDisplay(shown);
        Assert.Equal(doc.Text, text);
        Assert.Equal(Home, Assert.Single(items).ItemId);
    }

    [Fact]
    public void Several_items_dropped_together_are_kept_apart_by_a_space_and_the_caret_ends_after_the_last()
    {
        var buffer = new NotecardEditBuffer(null);
        var a = Item(0, Box, "Box", ItemKind.Object, 6);
        var b = Item(1, Other, "Hat", ItemKind.Object, 6);
        var (shown, caret) = buffer.InsertAll("xy", 1, [a, b]);
        Assert.Equal("x" + M + "Box" + M + " " + M + "Hat" + M + "y", shown);
        Assert.Equal(shown.IndexOf("y", StringComparison.Ordinal), caret);
        var (single, _) = buffer.InsertAll("xy", 1, [a]);
        Assert.Equal("x" + M + "Box" + M + "y", single);   // one item alone gets no extra space
        var (spaced, _) = buffer.InsertAll("x y", 2, [a, b]);
        Assert.Equal("x " + M + "Box" + M + " " + M + "Hat" + M + "y", spaced);
    }

    [Fact]
    public void An_item_inserted_into_the_text_is_written_into_the_file_as_an_embedded_item()
    {
        var buffer = new NotecardEditBuffer(null);
        string shown = buffer.Insert("ab", 1, Item(0, Box, "Box", ItemKind.Object, 6));
        Assert.Equal("a" + M + "Box" + M + "b", shown);
        var read = NotecardFormat.Parse(buffer.ToFile(shown));
        Assert.True(read.Ok, read.Error);
        Assert.Equal("a" + E(0) + "b", read.Document!.Text);
        Assert.Equal(Box, Assert.Single(read.Document.Items).ItemId);
    }

    [Fact]
    public void Items_are_numbered_again_in_text_order_and_a_deleted_one_leaves_the_file()
    {
        var doc = new NotecardDocument(2, E(0) + "x" + E(1), [Item(0, Home, "Home", ItemKind.Landmark, 3), Item(1, Box, "Box", ItemKind.Object, 6)]);
        var buffer = new NotecardEditBuffer(doc);
        string shown = buffer.ToDisplay(doc.Text);
        Assert.StartsWith("" + M + "Home" + M, shown);
        string withoutHome = shown.Remove(0, ("" + M + "Home" + M).Length);
        var (text, items) = buffer.FromDisplay(withoutHome);
        Assert.Equal("x" + E(0), text);
        var only = Assert.Single(items);
        Assert.Equal(Box, only.ItemId);
        Assert.Equal(0, only.Index);
    }

    [Fact]
    public void Two_items_with_one_name_keep_their_own_ids_in_order()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var doc = new NotecardDocument(2, E(0) + E(1), [Item(0, a, "Same", ItemKind.Object, 6), Item(1, b, "Same", ItemKind.Object, 6)]);
        var buffer = new NotecardEditBuffer(doc);
        var (_, items) = buffer.FromDisplay(buffer.ToDisplay(doc.Text));
        Assert.Equal(new[] { a, b }, items.Select(i => i.ItemId).ToArray());
    }

    [Fact]
    public void A_second_item_of_the_same_name_dropped_in_front_keeps_each_id_in_its_place()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var buffer = new NotecardEditBuffer(new NotecardDocument(2, "x" + E(0), [Item(0, first, "Box", ItemKind.Object, 6)]));
        string shown = buffer.ToDisplay("x" + E(0));
        shown = buffer.Insert(shown, 0, Item(0, second, "Box", ItemKind.Object, 6));   // the new one in front of the old one
        var (_, items) = buffer.FromDisplay(shown);
        Assert.Equal(new[] { second, first }, items.Select(i => i.ItemId).ToArray());
        Assert.Equal(first, buffer.ItemAt(shown, shown.Length - 2)?.ItemId);
    }

    [Fact]
    public void The_same_item_twice_is_one_entry_in_the_file()
    {
        var buffer = new NotecardEditBuffer(null);
        var box = Item(0, Box, "Box", ItemKind.Object, 6);
        string shown = buffer.Insert(buffer.Insert("ab", 1, box), 0, box);
        var (text, items) = buffer.FromDisplay(shown);
        Assert.Single(items);
        Assert.Equal(E(0) + "a" + E(0) + "b", text);
    }

    [Fact]
    public void Places_in_a_line_count_characters_as_the_editor_does_so_an_emoji_counts_once()
    {
        string text = "ab\n\U0001F600" + M + "Home" + M + "c";
        // line 1, column 1 is just after the emoji: index 3 + 2 (the emoji is two UTF-16 units)
        Assert.Equal(5, NotecardEditBuffer.IndexOf(text, 1, 1));
        Assert.Equal((1, 1), NotecardEditBuffer.PlaceOf(text, 5));
        Assert.Equal((1, 7), NotecardEditBuffer.PlaceOf(text, 11));
        Assert.Equal(11, NotecardEditBuffer.IndexOf(text, 1, 7));
    }

    [Fact]
    public void A_mark_left_without_its_partner_is_dropped_and_the_item_with_it()
    {
        var buffer = new NotecardEditBuffer(new NotecardDocument(2, E(0), [Item(0, Home, "Home", ItemKind.Landmark, 3)]));
        var (text, items) = buffer.FromDisplay("a" + M + "Hom");
        Assert.Equal("aHom", text);
        Assert.Empty(items);
    }

    [Fact]
    public void A_key_that_deletes_into_an_item_takes_the_whole_item()
    {
        string shown = "a" + M + "Home" + M + "b";
        // Backspace with the caret just after the item: the whole item, marks included
        Assert.Equal((1, 6), NotecardEditBuffer.TokenSpanAt(shown, 6));
        // Delete with the caret inside the item's name
        Assert.Equal((1, 6), NotecardEditBuffer.TokenSpanAt(shown, 3));
        Assert.Null(NotecardEditBuffer.TokenSpanAt(shown, 0));
        Assert.Null(NotecardEditBuffer.TokenSpanAt(shown, 8));
    }

    [Fact]
    public void The_item_under_a_click_is_found_by_its_place_in_the_shown_text()
    {
        var doc = new NotecardDocument(2, "x" + E(0), [Item(0, Home, "Home", ItemKind.Landmark, 3)]);
        var buffer = new NotecardEditBuffer(doc);
        string shown = buffer.ToDisplay(doc.Text);
        Assert.Equal(Home, buffer.ItemAt(shown, 3)?.ItemId);
        Assert.Null(buffer.ItemAt(shown, 0));
    }

    [Fact]
    public async Task An_edited_card_saves_its_file_and_is_unchanged_after()
    {
        var transport = new FakeSave(NotecardSaveResult.Saved(Guid.NewGuid()));
        var session = new NotecardEditSession(Access(All), objectModify: true, new NotecardDocument(2, "old", []), transport);
        Assert.True(session.CanEdit);
        Assert.False(session.IsDirty);
        session.SetText("new " + M + "Box" + M, [Item(0, Box, "Box", ItemKind.Object, 6)]);
        Assert.True(session.IsDirty);
        Assert.True(session.CanSave);
        var result = await session.SaveAsync(CancellationToken.None);
        Assert.Equal(NotecardSaveStatus.Saved, result.Status);
        Assert.False(session.IsDirty);
        var saved = NotecardFormat.Parse(transport.Sent!);
        Assert.Equal("new " + E(0), saved.Document!.Text);
        Assert.Equal(Card, transport.SentFor!.ItemId);
    }

    [Fact]
    public async Task A_failed_save_keeps_the_changes()
    {
        var session = new NotecardEditSession(Access(All), true, new NotecardDocument(2, "old", []), new FakeSave(NotecardSaveResult.Of(NotecardSaveStatus.Failed)));
        session.SetText("new", []);
        var result = await session.SaveAsync(CancellationToken.None);
        Assert.Equal(NotecardSaveStatus.Failed, result.Status);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void Text_over_the_limit_cannot_be_saved()
    {
        var session = new NotecardEditSession(Access(All), true, new NotecardDocument(2, "", []), new FakeSave(NotecardSaveResult.Of(NotecardSaveStatus.Failed)));
        session.SetText(new string('x', NotecardEditRules.MaxTextBytes + 1), []);
        Assert.True(session.OverLimit);
        Assert.False(session.CanSave);
    }

    [Fact]
    public void Close_with_unsaved_changes_asks_and_Discard_or_Cancel_answer_it()
    {
        var session = new NotecardEditSession(Access(All), true, new NotecardDocument(2, "old", []), new FakeSave(NotecardSaveResult.Of(NotecardSaveStatus.Failed)));
        Assert.Equal(NotecardCloseVerdict.Close, session.RequestClose());
        session.SetText("changed", []);
        Assert.Equal(NotecardCloseVerdict.Ask, session.RequestClose());
        session.SetText("old", []);
        Assert.Equal(NotecardCloseVerdict.Close, session.RequestClose());   // back to what was saved: nothing to ask
    }

    [Fact]
    public void A_card_the_user_may_not_modify_is_read_only_and_never_dirty()
    {
        var session = new NotecardEditSession(Access(All & ~NotecardPermissions.Modify), true, new NotecardDocument(2, "text", []), new FakeSave(NotecardSaveResult.Of(NotecardSaveStatus.Failed)));
        Assert.False(session.CanEdit);
        Assert.False(session.CanSave);
        Assert.True(session.CanCopyText);
        session.SetText("changed", []);
        Assert.False(session.IsDirty);
        Assert.Equal(NotecardCloseVerdict.Close, session.RequestClose());
    }

    [Fact]
    public void Each_embedded_item_is_found_as_a_link_over_its_name_and_a_stray_mark_pair_is_not()
    {
        var doc = new NotecardDocument(2, "Go " + E(0) + " and " + E(1), [Item(0, Home, "Home", ItemKind.Landmark, 3), Item(1, Box, "Box", ItemKind.Object, 6)]);
        var buffer = new NotecardEditBuffer(doc);
        string shown = buffer.ToDisplay(doc.Text) + " " + M + "typed" + M;
        var spans = buffer.ItemSpans(shown);
        Assert.Equal(2, spans.Count);
        Assert.Equal("Home", shown.Substring(spans[0].Start, spans[0].Length));
        Assert.Equal(Home, spans[0].Item.ItemId);
        Assert.Equal("Box", shown.Substring(spans[1].Start, spans[1].Length));
        Assert.Equal(Box, spans[1].Item.ItemId);
    }

    [Fact]
    public void A_read_card_draws_each_embedded_item_as_a_link_that_names_the_item()
    {
        var doc = new NotecardDocument(2, "Go " + E(0) + " now", [Item(0, Home, "Home", ItemKind.Landmark, 3)]);
        var model = new NotecardWindowModel(new NotecardOpenResult(NotecardOpenStatus.Ok, Card, "Card", "", Access(All), doc));
        string bb = model.RenderText(s => s, null);
        Assert.Contains("[url=" + NotecardWindowModel.ItemMetaPrefix + "0]Home[/url]", bb);
        Assert.DoesNotContain("[b]", bb);
        Assert.Equal(0, NotecardWindowModel.ItemOfMeta(NotecardWindowModel.ItemMetaPrefix + "0"));
        Assert.Null(NotecardWindowModel.ItemOfMeta("link:https%3A%2F%2Fexample.org"));
    }

    [Fact]
    public void The_editor_draws_embedded_items_in_the_link_colour_underlined_with_the_name_and_description_as_tooltip()
    {
        string src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "NotecardPanels.cs"));
        Assert.Contains(".ItemSpans(", src);
        Assert.Contains("DrawLine(", src);
        Assert.Contains("SetTooltipRequestFunc(", src);
        Assert.Contains("NotecardWindowModel.ItemOfMeta(", src);
        Assert.DoesNotContain("new Color(", src);
    }

    [Fact]
    public async Task An_item_dropped_in_is_unsaved_until_the_card_is_saved_and_a_click_on_it_asks_to_save_first()
    {
        var doc = new NotecardDocument(2, "Go " + E(0), [Item(0, Home, "Home", ItemKind.Landmark, 3)]);
        var session = new NotecardEditSession(Access(All), objectModify: true, doc, new FakeSave(NotecardSaveResult.Saved(Guid.NewGuid())));
        var box = Item(1, Box, "Box", ItemKind.Object, 6);
        session.SetText("Go " + M + "Home" + M + " " + M + "Box" + M, [box]);
        Assert.True(session.IsSaved(doc.Items[0]));
        Assert.False(session.IsSaved(box));
        await session.SaveAsync(CancellationToken.None);
        Assert.True(session.IsSaved(box));
        string src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "NotecardPanels.cs"));
        Assert.Contains(".IsSaved(item)", src);
        Assert.Contains("\"notecard.save_first\"", src);
    }

    private sealed class FakeSave(NotecardSaveResult answer) : INotecardSaveTransport
    {
        public byte[]? Sent { get; private set; }
        public NotecardAccess? SentFor { get; private set; }

        public Task<NotecardSaveResult> SaveNotecardAsync(NotecardAccess access, byte[] file, CancellationToken ct)
        {
            Sent = file;
            SentFor = access;
            return Task.FromResult(answer);
        }
    }
}
