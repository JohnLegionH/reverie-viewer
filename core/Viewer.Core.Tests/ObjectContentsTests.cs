// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Viewer.Core.Actions;
using Viewer.Core.Assets;
using Viewer.Core.Interaction;
using Viewer.Core.Inventory;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Build;
using Viewer.Shell.Inventory;
using Xunit;
using Vector3 = System.Numerics.Vector3;

namespace Viewer.Core.Tests;

/// <summary>
/// Working with what is inside an object, as the official viewer does (llpanelobjectinventory.cpp, llcompilequeue.cpp, llviewermenu.cpp):
/// the rules for opening, renaming and dragging out an item, the message a rename becomes, a card inside an object opened read-only, the
/// selection card's names, Build › Scripts and the script queues. In-memory worlds and fakes; each test builds its own, so the class runs in parallel.
/// </summary>
public class ObjectContentsTests
{
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private static readonly Guid Me = BuildFeaturesTabTests.Me, Other = G(77);
    private const uint All = 0x7FFFFFFF;
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;
    private static readonly UUID Agent = new("1a2b3c4d-0000-4000-8000-000000000001"), Session = new("1a2b3c4d-0000-4000-8000-000000000002");

    private static TaskItem Item(uint n, string name, int type = 7, Guid owner = default, uint ownerMask = All, uint everyone = 0) =>
        new(G(n), name, type, type, owner == default ? Me : owner, ownerMask, everyone, AssetId: G(n + 500), CreatorId: G(66),
            BaseMask: All, GroupMask: 0, NextOwnerMask: 0x82000, Description: "a card", GroupId: G(55), Flags: 0, SaleType: 1, SalePrice: 10, CreationDate: 1700000000);

    private static BuildFeaturesTabTests.Rig Selected(uint flags = Own, params TaskItem[] items)
    {
        var r = new BuildFeaturesTabTests.Rig();
        r.Prim(1, new Vector3(10, 10, 30), flags);
        r.Select(10, 10);
        r.C.Receive(new TaskInventoryReceived(G(1), items));
        return r;
    }

    // ---------------- the item rules ----------------

    [Fact]
    public void The_agents_rights_on_an_item_are_the_owner_mask_for_the_owner_and_the_everyone_mask_for_anyone_else()
    {
        var mine = Item(1, "card", ownerMask: TaskItemRules.Copy, everyone: 0);
        Assert.Equal(TaskItemRules.Copy, TaskItemRules.MaskFor(mine, Me));
        Assert.True(TaskItemRules.Allows(mine, Me, TaskItemRules.Copy));
        Assert.False(TaskItemRules.Allows(mine, Me, TaskItemRules.Modify));
        Assert.False(TaskItemRules.Allows(mine, Other, TaskItemRules.Copy));
        var open = Item(2, "card", ownerMask: All, everyone: TaskItemRules.Copy);
        Assert.True(TaskItemRules.Allows(open, Other, TaskItemRules.Copy));
    }

    [Fact]
    public void Open_takes_scripts_notecards_and_textures_by_their_asset_type()
    {
        Assert.Equal(TaskOpenKind.Script, TaskItemRules.OpenKind(Item(1, "s", type: 10)));
        Assert.Equal(TaskOpenKind.Notecard, TaskItemRules.OpenKind(Item(2, "n", type: 7)));
        Assert.Equal(TaskOpenKind.Texture, TaskItemRules.OpenKind(Item(3, "t", type: 0)));
        Assert.Equal(TaskOpenKind.None, TaskItemRules.OpenKind(Item(4, "o", type: 6)));
    }

    [Fact]
    public void A_notecard_inside_an_object_is_read_when_the_card_may_be_copied_or_the_object_may_be_modified()
    {
        var noCopy = Item(1, "n", ownerMask: TaskItemRules.Modify | TaskItemRules.Transfer);
        Assert.False(TaskItemRules.CanOpenNotecard(noCopy, Me, objectModify: false));
        Assert.True(TaskItemRules.CanOpenNotecard(noCopy, Me, objectModify: true));
        Assert.True(TaskItemRules.CanOpenNotecard(Item(2, "n", ownerMask: TaskItemRules.Copy), Me, objectModify: false));
    }

    [Fact]
    public void An_item_is_renamed_only_when_it_may_be_modified()
    {
        Assert.True(TaskItemRules.CanRename(Item(1, "n"), Me));
        Assert.False(TaskItemRules.CanRename(Item(2, "n", ownerMask: TaskItemRules.Copy | TaskItemRules.Transfer), Me));
    }

    [Fact]
    public void An_item_leaves_by_drag_when_it_may_be_copied_and_given_to_the_agent_or_the_agent_owns_the_object_but_never_a_no_copy_item_of_an_attachment()
    {
        var copyable = Item(1, "n", owner: Other, ownerMask: TaskItemRules.Copy | TaskItemRules.Transfer, everyone: TaskItemRules.Copy);
        Assert.True(TaskItemRules.CanDragOut(copyable, Me, youOwnObject: false, attachment: false));
        var noTransfer = Item(2, "n", owner: Other, ownerMask: TaskItemRules.Copy, everyone: TaskItemRules.Copy);
        Assert.False(TaskItemRules.CanDragOut(noTransfer, Me, youOwnObject: false, attachment: false));
        var noCopy = Item(3, "n", ownerMask: TaskItemRules.Modify | TaskItemRules.Transfer);
        Assert.True(TaskItemRules.CanDragOut(noCopy, Me, youOwnObject: true, attachment: false));
        Assert.False(TaskItemRules.CanDragOut(noCopy, Me, youOwnObject: true, attachment: true));
    }

    [Fact]
    public void The_list_names_an_item_with_the_official_suffixes_for_what_the_agent_may_not_do()
    {
        var en = StringTable.English();
        Assert.Equal("card", TaskItemRules.DisplayName(Item(1, "card"), Me, k => en.Get(k)));
        Assert.Equal("card (no copy) (no modify) (no transfer)", TaskItemRules.DisplayName(Item(2, "card", ownerMask: 0), Me, k => en.Get(k)));
        Assert.Equal("card (no modify)", TaskItemRules.DisplayName(Item(3, "card", ownerMask: TaskItemRules.Copy | TaskItemRules.Transfer), Me, k => en.Get(k)));
    }

    [Fact]
    public void A_drag_out_of_the_contents_carries_the_object_and_the_item_and_an_inventory_drag_is_not_read_as_one()
    {
        var drag = new TaskDrag(G(1), 5, G(2));
        Assert.StartsWith(TaskDrag.Prefix, drag.Encode());
        Assert.Equal(drag, TaskDrag.Decode(drag.Encode()));
        Assert.Null(TaskDrag.Decode(InventoryDragSet.Encode([G(3)])));
        Assert.Null(TaskDrag.Decode("task:nonsense"));
    }

    // ---------------- rename, properties, drag out ----------------

    [Fact]
    public void Rename_sends_the_whole_item_back_with_the_new_name_and_every_other_field_as_listed()
    {
        var card = Item(11, "old");
        var r = Selected(Own, card);
        Assert.True(r.C.UpdateItemText(card.ItemId, "  new name  ", null, Me));
        var sent = r.Sent.OfType<UpdateObjectItemIntent>().Single();
        Assert.Equal(1u, sent.LocalId);
        Assert.Equal(G(1), sent.ObjectId);
        Assert.Equal(card with { Name = "new name" }, sent.Item);
        Assert.IsType<RequestContentsIntent>(r.Sent[^1]);
    }

    [Fact]
    public void A_new_description_keeps_the_name_and_both_are_cut_to_the_inventorys_lengths()
    {
        var card = Item(11, "old");
        var r = Selected(Own, card);
        Assert.True(r.C.UpdateItemText(card.ItemId, new string('n', 80), new string('d', 200), Me));
        var sent = r.Sent.OfType<UpdateObjectItemIntent>().Single().Item;
        Assert.Equal(TaskItemRules.MaxName, sent.Name.Length);
        Assert.Equal(TaskItemRules.MaxDescription, sent.Description.Length);
        Assert.True(r.C.UpdateItemText(card.ItemId, null, "plain", Me));
        Assert.Equal(card with { Description = "plain" }, r.Sent.OfType<UpdateObjectItemIntent>().Last().Item);
    }

    [Fact]
    public void An_item_that_may_not_be_modified_or_in_a_prim_that_may_not_be_is_not_renamed_nor_to_a_blank_name()
    {
        var locked = Item(11, "old", ownerMask: TaskItemRules.Copy | TaskItemRules.Transfer);
        var r = Selected(Own, locked);
        Assert.False(r.C.UpdateItemText(locked.ItemId, "new", null, Me));
        var card = Item(12, "old");
        var ro = Selected(ObjectFlagBits.Move, card);
        Assert.False(ro.C.UpdateItemText(card.ItemId, "new", null, Me));
        var ok = Selected(Own, card);
        Assert.False(ok.C.UpdateItemText(card.ItemId, "   ", null, Me));
        Assert.Empty(r.Sent.OfType<UpdateObjectItemIntent>().Concat(ro.Sent.OfType<UpdateObjectItemIntent>()).Concat(ok.Sent.OfType<UpdateObjectItemIntent>()));
    }

    [Fact]
    public void Rename_becomes_UpdateTaskInventory_for_one_item_with_the_prim_as_its_folder()
    {
        var card = Item(11, "renamed");
        var p = LmvFeatures.UpdateItem(Agent, Session, 7, new UUID(G(1)), card);
        Assert.Equal(7u, p.UpdateData.LocalID);
        Assert.Equal(0, p.UpdateData.Key);
        var d = p.InventoryData;
        Assert.Equal(new UUID(card.ItemId), d.ItemID);
        Assert.Equal(new UUID(G(1)), d.FolderID);
        Assert.Equal("renamed", Utils.BytesToString(d.Name));
        Assert.Equal("a card", Utils.BytesToString(d.Description));
        Assert.Equal((card.OwnerMask, card.EveryoneMask, card.NextOwnerMask, card.BaseMask), (d.OwnerMask, d.EveryoneMask, d.NextOwnerMask, d.BaseMask));
        Assert.Equal((new UUID(G(66)), new UUID(G(55)), (sbyte)7, (byte)1, 10, 1700000000), (d.CreatorID, d.GroupID, d.Type, d.SaleType, d.SalePrice, d.CreationDate));
        Assert.IsType<UpdateTaskInventoryPacket>(LmvFeatures.Packet(Agent, Session, UUID.Zero, new UpdateObjectItemIntent(7, G(1), card)));
    }

    [Fact]
    public void The_listed_item_keeps_its_description_group_sale_and_date_from_the_region()
    {
        var lmv = new InventoryItem(new UUID(G(11))) { Name = "card", Description = "said", AssetType = AssetType.Notecard, InventoryType = InventoryType.Notecard,
            OwnerID = new UUID(Me), CreatorID = new UUID(G(66)), GroupID = new UUID(G(55)), GroupOwned = false, Flags = 3, SaleType = SaleType.Original, SalePrice = 25,
            CreationDate = Utils.UnixTimeToDateTime(1700000000) };
        var t = LmvTransport.ToTaskItem(lmv);
        Assert.Equal(("said", G(55), 3u, 1, 25, 1700000000), (t.Description, t.GroupId, t.Flags, t.SaleType, t.SalePrice, t.CreationDate));
    }

    [Fact]
    public void An_item_dropped_on_an_inventory_folder_is_copied_into_that_folder_when_it_may_leave()
    {
        var card = Item(11, "card");
        var r = Selected(Own, card);
        Assert.True(r.C.CopyItemToFolder(card.ItemId, G(40), Me));
        Assert.Equal(new CopyObjectItemIntent(1, card.ItemId, 7, G(40)), r.Sent.OfType<CopyObjectItemIntent>().Single());
        var theirs = Item(12, "card", owner: Other, ownerMask: TaskItemRules.Copy, everyone: TaskItemRules.Copy);
        var other = Selected(ObjectFlagBits.Move | ObjectFlagBits.Copy, theirs);
        Assert.False(other.C.CopyItemToFolder(theirs.ItemId, G(40), Me));
        Assert.DoesNotContain(other.Sent, i => i is CopyObjectItemIntent);
    }

    // ---------------- a notecard inside an object ----------------

    private sealed class Cards(AssetFetchResult answer) : INotecardTransport
    {
        public NotecardAccess? Asked;
        public NotecardAccess? NotecardAccessOf(Guid itemId) => null;
        public Task<AssetFetchResult> FetchNotecardAsync(NotecardAccess access, CancellationToken ct) { Asked = access; return Task.FromResult(answer); }
    }

    private static readonly byte[] CardFile = System.Text.Encoding.UTF8.GetBytes(
        "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 5\nhello}\n");

    [Fact]
    public void A_notecard_inside_an_object_is_fetched_with_the_objects_id_and_shown_with_no_copying_out()
    {
        var card = Item(11, "card", ownerMask: TaskItemRules.Copy | TaskItemRules.Transfer);
        var t = new Cards(AssetFetchResult.Ok(CardFile));
        var opened = NotecardReader.OpenTaskAsync(t, card, G(1), Me, objectModify: false).GetAwaiter().GetResult();
        Assert.Equal(NotecardOpenStatus.Ok, opened.Status);
        Assert.Equal("hello", opened.Document!.Text);
        Assert.Equal(G(1), t.Asked!.ObjectId);
        Assert.Equal(card.AssetId, t.Asked.AssetId);
        var embedded = new NotecardEmbeddedItem(0, G(3), G(4), "x", "", 7, 7, ItemKind.Notecard, new NotecardPermissions(Me, Me, All, All, 0, 0, All));
        Assert.False(NotecardRules.CanCopyOut(opened.Access!, embedded));
    }

    [Fact]
    public void A_notecard_inside_an_object_the_agent_may_neither_copy_nor_modify_is_not_fetched()
    {
        var card = Item(11, "card", ownerMask: TaskItemRules.Transfer);
        var t = new Cards(AssetFetchResult.Ok(CardFile));
        var opened = NotecardReader.OpenTaskAsync(t, card, G(1), Me, objectModify: false).GetAwaiter().GetResult();
        Assert.Equal(NotecardOpenStatus.NotAllowed, opened.Status);
        Assert.Null(t.Asked);
    }

    // ---------------- names on the selection card ----------------

    private sealed class Asked : IAgentNameRequester
    {
        public List<Guid> Ids = new();
        public void RequestAgentNames(IReadOnlyList<Guid> agents) => Ids.AddRange(agents);
    }

    [Fact]
    public void A_person_is_named_through_the_name_cache_and_shows_loading_until_it_answers_never_the_id()
    {
        var en = StringTable.English();
        var cache = new Viewer.Core.Social.NameCache();
        Assert.Equal(en.Get("edit.name.loading"), BuildNames.Person(cache.Get, G(5), en));
        var asked = new Asked();
        Assert.Equal(1, cache.Flush(asked));
        Assert.Contains(G(5), asked.Ids);
        cache.Learn(G(5), "Test User");
        Assert.Equal("Test User", BuildNames.Person(cache.Get, G(5), en));
        Assert.Equal("", BuildNames.Person(cache.Get, Guid.Empty, en));
        Assert.DoesNotContain(G(6).ToString(), BuildNames.Person(null, G(6), en));
    }

    // ---------------- Build › Scripts ----------------

    [Fact]
    public void The_Scripts_menu_stands_before_Options_with_the_official_five_entries_on_while_something_editable_is_chosen()
    {
        var menus = new MenuModel();
        bool editable = false;
        BuildMenu.InstallScripts(menus, () => true, () => editable);
        var build = Assert.Single(menus.Menus, m => m.Id == BuildMenu.MenuId);
        var scripts = Assert.Single(build.Entries, e => e.Id == BuildMenu.ScriptsId);
        Assert.True(scripts.Order < BuildMenu.OptionsOrder && scripts.Order > BuildMenu.LinkOrder);
        Assert.Equal([BuildMenu.RecompileMonoId, BuildMenu.RecompileLslId, BuildMenu.ResetScriptsId, BuildMenu.SetRunningId, BuildMenu.SetNotRunningId],
            scripts.Children!.Select(c => c.Id));
        Assert.All(scripts.Children!, c => Assert.False(c.IsEnabled));
        editable = true;
        Assert.All(scripts.Children!, c => Assert.True(c.IsEnabled));
        var en = StringTable.English();
        Assert.Equal(["Recompile Scripts (Mono)", "Recompile Scripts (LSL)", "Reset Scripts", "Set Scripts to Running", "Set Scripts to Not Running"],
            scripts.Children!.Select(c => en.Get(c.LabelKey)));
    }

    [Fact]
    public void The_queue_takes_every_chosen_prim_that_has_a_script_and_may_be_modified()
    {
        var r = new BuildFeaturesTabTests.Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own | ObjectFlagBits.Scripted);
        r.Prim(2, new Vector3(20, 20, 30), Own);
        r.Prim(3, new Vector3(30, 30, 30), ObjectFlagBits.Move | ObjectFlagBits.Scripted);
        r.Select(10, 10);
        r.Select(20, 20, shift: true);
        r.Select(30, 30, shift: true);
        var (objects, refusal) = r.C.ScriptQueueObjects();
        Assert.Null(refusal);
        Assert.Equal([G(1)], objects.Select(o => o.ObjectId));
        Assert.Equal(1u, objects[0].LocalId);
    }

    [Fact]
    public void The_queue_refuses_as_the_official_one_when_no_chosen_prim_has_a_script_or_none_may_be_modified()
    {
        var plain = new BuildFeaturesTabTests.Rig();
        plain.Prim(1, new Vector3(10, 10, 30), Own);
        plain.Select(10, 10);
        Assert.Equal(ScriptQueueRefusal.NoScripts, plain.C.ScriptQueueObjects().Refusal);
        var locked = new BuildFeaturesTabTests.Rig();
        locked.Prim(1, new Vector3(10, 10, 30), ObjectFlagBits.Move | ObjectFlagBits.Scripted);
        locked.Select(10, 10);
        Assert.Equal(ScriptQueueRefusal.NoPermission, locked.C.ScriptQueueObjects().Refusal);
        var en = StringTable.English();
        Assert.Contains("Select an object with a script.", en.Get(ScriptQueue.RefusalKey(ScriptQueueAction.CompileMono, ScriptQueueRefusal.NoScripts)));
        Assert.Contains("permission to modify", en.Get(ScriptQueue.RefusalKey(ScriptQueueAction.Reset, ScriptQueueRefusal.NoPermission)));
        Assert.Equal("Set Not Running Progress", en.Get(ScriptQueue.TitleKey(ScriptQueueAction.SetNotRunning)));
    }

    private sealed class Scripts : IScriptTransport
    {
        public Dictionary<Guid, AssetFetchResult> Texts = new();
        public Dictionary<Guid, ScriptSaveResult> Answers = new();
        public List<(Guid Item, ScriptTarget Target, bool Running, string Text)> Saved = new();
        public List<Guid> Reset = new();
        public List<(Guid Item, bool Running)> Set = new();
        public ScriptAccess? ScriptAccessOf(Guid itemId) => null;
        public Task<AssetFetchResult> FetchScriptAsync(ScriptAccess access, CancellationToken ct) =>
            Task.FromResult(Texts.TryGetValue(access.ItemId, out var t) ? t : AssetFetchResult.Ok(System.Text.Encoding.UTF8.GetBytes("default{}")));
        public Task<ScriptSaveResult> SaveScriptAsync(ScriptAccess access, byte[] text, ScriptTarget target, bool running, CancellationToken ct)
        {
            Saved.Add((access.ItemId, target, running, System.Text.Encoding.UTF8.GetString(text)));
            return Task.FromResult(Answers.TryGetValue(access.ItemId, out var a) ? a : new ScriptSaveResult(ScriptSaveStatus.Saved, true, G(99), []));
        }
        public Task<ScriptRuntime?> GetScriptRunningAsync(ScriptAccess access, CancellationToken ct) => Task.FromResult<ScriptRuntime?>(null);
        public ScriptRegion Region => ScriptRegion.None;
        public Task<bool> SetScriptEngineAsync(ScriptAccess access, ScriptTarget? target, CancellationToken ct) => Task.FromResult(false);
        public bool SetScriptRunning(ScriptAccess access, bool running) { Set.Add((access.ItemId, running)); return true; }
        public bool ResetScript(ScriptAccess access) { Reset.Add(access.ItemId); return true; }
    }

    /// <summary>Runs a queue to its end, answering each contents request from <paramref name="contents"/> (an object missing there never answers).</summary>
    private static (ScriptQueue Q, List<ActionIntent> Sent) Run(ScriptQueueAction action, Scripts scripts, Dictionary<Guid, TaskItem[]> contents, params ScriptQueueObject[] objects)
    {
        var sent = new List<ActionIntent>();
        ScriptQueue? q = null;
        q = new ScriptQueue(action, objects, Me, i =>
        {
            sent.Add(i);
            if (i is RequestContentsIntent c && contents.TryGetValue(c.ObjectId, out var items))
                q!.Receive(new TaskInventoryReceived(c.ObjectId, items));
            return new ActionResult(ActionStatus.Sent, sent.Count);
        }, scripts, (_, _) => Task.CompletedTask);
        Assert.True(q.RunAsync().Wait(TimeSpan.FromSeconds(30)));
        return (q, sent);
    }

    private static List<string> Text(ScriptQueue q)
    {
        var en = StringTable.English();
        return q.Lines.Select(l => ScriptQueue.Format(l, en)).ToList();
    }

    private static readonly ScriptQueueObject Box = new(1, G(1), "Box"), Ball = new(2, G(2), "Ball");

    [Fact]
    public void Recompile_fetches_each_script_and_saves_it_back_running_with_the_chosen_target_and_says_what_compiled()
    {
        var s = new Scripts();
        s.Answers[G(12)] = new ScriptSaveResult(ScriptSaveStatus.Saved, false, G(98), [ScriptMessage.Parse("(0,4) Error: syntax"), ScriptMessage.Parse("(1,0) Error: more")]);
        var (q, _) = Run(ScriptQueueAction.CompileLsl, s, new() { [G(1)] = [Item(11, "good", type: 10), Item(12, "bad", type: 10), Item(13, "card")] }, Box);
        Assert.Equal([(G(11), ScriptTarget.Lsl2, true, "default{}"), (G(12), ScriptTarget.Lsl2, true, "default{}")], s.Saved);
        Assert.Equal(["Starting recompile of 1 items.", "Loading inventory for: Box", "Compilation of \"good\" succeeded", "Compilation of \"bad\" failed:",
            "(0,4) Error: syntax", "(1,0) Error: more", "Done."], Text(q).Take(7));
        Assert.True(q.Done);
        Assert.Equal((1, 1), (q.Handled, q.Failed));
        var mono = new Scripts();
        Run(ScriptQueueAction.CompileMono, mono, new() { [G(1)] = [Item(11, "good", type: 10)] }, Box);
        Assert.Equal(ScriptTarget.Mono, mono.Saved.Single().Target);
    }

    [Fact]
    public void Recompile_skips_a_script_the_agent_may_not_both_modify_and_copy_and_says_which_one_could_not_be_downloaded()
    {
        var s = new Scripts();
        s.Texts[G(12)] = AssetFetchResult.Of(AssetFetchStatus.NotFound);
        var (q, _) = Run(ScriptQueueAction.CompileMono, s, new() { [G(1)] = [Item(11, "locked", type: 10, ownerMask: TaskItemRules.Copy), Item(12, "gone", type: 10)] }, Box);
        Assert.Empty(s.Saved);
        Assert.Contains("Skipping: locked(Permissions)", Text(q));
        Assert.Contains("Problem downloading: gone", Text(q));
        Assert.Equal((0, 2), (q.Handled, q.Failed));
    }

    [Fact]
    public void Reset_and_running_go_to_every_script_of_every_object_one_object_at_a_time()
    {
        var contents = new Dictionary<Guid, TaskItem[]> { [G(1)] = [Item(11, "a", type: 10), Item(13, "card")], [G(2)] = [Item(21, "b", type: 10)] };
        var reset = new Scripts();
        var (q, sent) = Run(ScriptQueueAction.Reset, reset, contents, Box, Ball);
        Assert.Equal([G(11), G(21)], reset.Reset);
        Assert.Equal([G(1), G(2)], sent.OfType<RequestContentsIntent>().Select(c => c.ObjectId));
        Assert.Equal(["Starting reset of 2 items.", "Loading inventory for: Box", "Resetting: a", "Loading inventory for: Ball", "Resetting: b", "Done."], Text(q).Take(6));
        var run = new Scripts();
        var (qr, _) = Run(ScriptQueueAction.SetRunning, run, contents, Box, Ball);
        Assert.Equal([(G(11), true), (G(21), true)], run.Set);
        Assert.Contains("Running: a", Text(qr));
        var stop = new Scripts();
        var (qs, _) = Run(ScriptQueueAction.SetNotRunning, stop, contents, Box);
        Assert.Equal([(G(11), false)], stop.Set);
        Assert.Contains("Not running: a", Text(qs));
        Assert.Equal((1, 0), (qs.Handled, qs.Failed));
    }

    [Fact]
    public void An_object_whose_contents_never_come_is_named_as_timed_out_and_the_queue_goes_on()
    {
        var s = new Scripts();
        var (q, _) = Run(ScriptQueueAction.Reset, s, new() { [G(2)] = [Item(21, "b", type: 10)] }, Box, Ball);
        Assert.Contains("Timeout: Box", Text(q));
        Assert.Equal([G(21)], s.Reset);
        Assert.Equal("Done.", Text(q)[^2]);
        Assert.Equal("1 scripts done, 0 with errors.", Text(q)[^1]);
    }

    // ---------------- review fixes ----------------

    [Fact]
    public void A_contents_read_that_failed_or_a_request_that_was_not_sent_is_named_as_timed_out_not_read_as_empty()
    {
        var s = new Scripts();
        ScriptQueue? q = null;
        q = new ScriptQueue(ScriptQueueAction.Reset, [Box, Ball], Me, i =>
        {
            if (i is RequestContentsIntent c && c.ObjectId == G(1))
                q!.Receive(new TaskInventoryReceived(G(1), [], Ok: false));
            return new ActionResult(i is RequestContentsIntent { ObjectId: var o } && o == G(2) ? ActionStatus.Unavailable : ActionStatus.Sent, 1);
        }, s, (_, ct) => Task.Delay(TimeSpan.FromSeconds(30), ct));
        Assert.True(q.RunAsync().Wait(TimeSpan.FromSeconds(10)));
        var text = Text(q);
        Assert.Contains("Timeout: Box", text);
        Assert.Contains("Timeout: Ball", text);
        Assert.True(q.Done);
    }

    private sealed class Throwing : IScriptTransport
    {
        public ScriptAccess? ScriptAccessOf(Guid itemId) => null;
        public Task<AssetFetchResult> FetchScriptAsync(ScriptAccess access, CancellationToken ct) => throw new InvalidOperationException("gone");
        public Task<ScriptSaveResult> SaveScriptAsync(ScriptAccess access, byte[] text, ScriptTarget target, bool running, CancellationToken ct) => throw new InvalidOperationException();
        public Task<ScriptRuntime?> GetScriptRunningAsync(ScriptAccess access, CancellationToken ct) => throw new InvalidOperationException();
        public ScriptRegion Region => ScriptRegion.None;
        public Task<bool> SetScriptEngineAsync(ScriptAccess access, ScriptTarget? target, CancellationToken ct) => throw new InvalidOperationException();
        public bool SetScriptRunning(ScriptAccess access, bool running) => throw new InvalidOperationException();
        public bool ResetScript(ScriptAccess access) => throw new InvalidOperationException();
    }

    [Fact]
    public void A_queue_that_fails_partway_or_is_stopped_still_ends_so_its_window_can_close()
    {
        ScriptQueue? q = null;
        q = new ScriptQueue(ScriptQueueAction.CompileMono, [Box], Me, i =>
        {
            if (i is RequestContentsIntent c)
                q!.Receive(new TaskInventoryReceived(c.ObjectId, [Item(11, "a", type: 10)]));
            return new ActionResult(ActionStatus.Sent, 1);
        }, new Throwing(), (_, _) => Task.CompletedTask);
        try { q.RunAsync().Wait(TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { }   // the queue must not throw; if it does, Done below still tells
        Assert.True(q.Done);
        Assert.Contains(StringTable.English().Get("queue.stopped"), Text(q));
        var stopped = new ScriptQueue(ScriptQueueAction.Reset, [Box], Me, _ => new ActionResult(ActionStatus.Sent, 1), new Scripts(),
            (_, ct) => Task.Delay(TimeSpan.FromSeconds(30), ct));
        using var cts = new CancellationTokenSource();
        var run = stopped.RunAsync(cts.Token);
        cts.Cancel();
        run.Wait(TimeSpan.FromSeconds(10));
        Assert.True(stopped.Done);
    }

    [Fact]
    public void A_group_owned_object_or_item_is_owned_by_its_group_not_a_name_that_never_comes()
    {
        var en = StringTable.English();
        var cache = new Viewer.Core.Social.NameCache();
        Assert.Equal(en.Get("edit.name.group"), BuildNames.Owner(cache.Get, G(5), G(5), false, en));
        Assert.Equal(en.Get("edit.name.group"), BuildNames.Owner(cache.Get, Guid.Empty, G(5), true, en));
        Assert.Equal(0, cache.Flush(new Asked()));
        cache.Learn(G(6), "Test User");
        Assert.Equal("Test User", BuildNames.Owner(cache.Get, G(6), G(5), false, en));
    }

    [Fact]
    public void Whether_an_item_may_be_dragged_out_is_one_rule_that_counts_a_child_prim_of_an_attachment()
    {
        var noCopy = Item(11, "n", ownerMask: TaskItemRules.Modify | TaskItemRules.Transfer);
        var r = Selected(Own, noCopy);
        Assert.True(r.C.CanDragOut(noCopy.ItemId, Me));
        Assert.False(r.C.CanDragOut(G(99), Me));
    }
}
