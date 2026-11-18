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
