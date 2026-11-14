// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Actions;
using Viewer.Core.Appearance;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Core.Session;
using Xunit;
using static Viewer.Core.Tests.ServerAppearanceTests;

namespace Viewer.Core.Tests;

/// <summary>
/// Saving a wearable: the order of the steps (the official viewer's: link refresh, asset upload, item update, then the rebuild
/// request last; Save As copies first and swaps what is worn), what a failing step stops, and the intents and commands that carry
/// a save through the action pipeline. A recording fake stands for the grid; no network.
/// </summary>
public class WearableSaveTests
{
    private static readonly Guid Item = Guid.Parse("a1000000-0000-4000-8000-000000000001");
    private static readonly Guid Copy = Guid.Parse("a1000000-0000-4000-8000-000000000002");
    private static readonly Guid Folder = Guid.Parse("a1000000-0000-4000-8000-0000000000f1");
    private static readonly Guid Tx = Guid.Parse("a1000000-0000-4000-8000-0000000000e1");
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("file");

    private sealed class FakeOps : IWearableSaveOps
    {
        public readonly List<string> Calls = [];
        public string? FailAt;     // the call name that fails ("link", "upload", "update", "copy", "swap", "rebake")
        public string? ThrowAt;

        private bool Step(string name, string detail)
        {
            Calls.Add(detail);
            if (ThrowAt == name) throw new InvalidOperationException("boom");
            return FailAt != name;
        }

        public Task<bool> RefreshLinkAsync(Guid itemId) => Task.FromResult(Step("link", $"link:{itemId}"));
        public Task<Guid> UploadAsync(int assetType, byte[] data) => Task.FromResult(Step("upload", $"upload:{assetType}:{data.Length}") ? Tx : Guid.Empty);
        public Task<bool> UpdateItemAsync(Guid itemId, Guid transactionId, string name) => Task.FromResult(Step("update", $"update:{itemId}:{transactionId}:{name}"));
        public Task<Guid> CopyItemAsync(Guid sourceItemId, Guid folderId, string name) => Task.FromResult(Step("copy", $"copy:{sourceItemId}:{folderId}:{name}") ? Copy : Guid.Empty);
        public Task<bool> SwapWornAsync(Guid originalItemId, Guid copyItemId) => Task.FromResult(Step("swap", $"swap:{originalItemId}:{copyItemId}"));
        public Task<bool> RebakeAsync() => Task.FromResult(Step("rebake", "rebake"));
    }

    private static WearableSaveRequest Save(byte[]? data = null, Guid? item = null) => new(item ?? Item, "Plain shirt", 5, data ?? Data, false, Guid.Empty);
    private static WearableSaveRequest SaveAs() => new(Item, "Red shirt", 5, Data, true, Folder);

    [Fact]
    public async Task Save_refreshes_the_link_uploads_the_asset_updates_the_item_and_asks_for_the_rebuild_last()
    {
        var ops = new FakeOps();
        var outcome = await WearableSaveSequence.RunAsync(ops, Save());
        Assert.Equal([$"link:{Item}", "upload:5:4", $"update:{Item}:{Tx}:Plain shirt", "rebake"], ops.Calls);
        Assert.Equal(new WearableSaveOutcome(true, true, "", Item), outcome);
    }

    [Fact]
    public async Task A_link_that_cannot_be_refreshed_stops_the_save_before_anything_is_uploaded()
    {
        var ops = new FakeOps { FailAt = "link" };
        var outcome = await WearableSaveSequence.RunAsync(ops, Save());
        Assert.Equal([$"link:{Item}"], ops.Calls);
        Assert.Equal(new WearableSaveOutcome(false, false, "link_failed", Item), outcome);
    }

    [Fact]
    public async Task A_failed_upload_leaves_the_item_alone_and_asks_for_no_rebuild()
    {
        var ops = new FakeOps { FailAt = "upload" };
        var outcome = await WearableSaveSequence.RunAsync(ops, Save());
        Assert.Equal([$"link:{Item}", "upload:5:4"], ops.Calls);
        Assert.Equal(new WearableSaveOutcome(false, false, "upload_failed", Item), outcome);
    }

    [Fact]
    public async Task A_failed_item_update_reports_not_saved_and_asks_for_no_rebuild()
    {
        var ops = new FakeOps { FailAt = "update" };
        var outcome = await WearableSaveSequence.RunAsync(ops, Save());
        Assert.DoesNotContain("rebake", ops.Calls);
        Assert.Equal(new WearableSaveOutcome(false, false, "update_failed", Item), outcome);
    }

    [Fact]
    public async Task A_refused_rebuild_still_counts_as_saved_and_says_the_look_was_not_rebuilt()
    {
        var ops = new FakeOps { FailAt = "rebake" };
        var outcome = await WearableSaveSequence.RunAsync(ops, Save());
        Assert.Equal(new WearableSaveOutcome(true, false, "rebuild_failed", Item), outcome);
        Assert.Equal("rebake", ops.Calls[^1]);
    }

    [Fact]
    public async Task Save_As_copies_first_then_uploads_updates_the_copy_swaps_what_is_worn_and_asks_for_the_rebuild_last()
    {
        var ops = new FakeOps();
        var outcome = await WearableSaveSequence.RunAsync(ops, SaveAs());
        Assert.Equal([$"copy:{Item}:{Folder}:Red shirt", "upload:5:4", $"update:{Copy}:{Tx}:Red shirt", $"swap:{Item}:{Copy}", "rebake"], ops.Calls);
        Assert.Equal(new WearableSaveOutcome(true, true, "", Copy), outcome);
    }

    [Fact]
    public async Task Save_As_with_a_copy_that_fails_stops_at_once_and_names_no_new_item()
    {
        var ops = new FakeOps { FailAt = "copy" };
        var outcome = await WearableSaveSequence.RunAsync(ops, SaveAs());
        Assert.Equal([$"copy:{Item}:{Folder}:Red shirt"], ops.Calls);
        Assert.Equal(new WearableSaveOutcome(false, false, "copy_failed", Guid.Empty), outcome);
    }

    [Fact]
    public async Task Save_As_that_cannot_swap_keeps_the_saved_copy_and_asks_for_no_rebuild()
    {
        var ops = new FakeOps { FailAt = "swap" };
        var outcome = await WearableSaveSequence.RunAsync(ops, SaveAs());
        Assert.DoesNotContain("rebake", ops.Calls);
        Assert.Equal(new WearableSaveOutcome(true, false, "wear_failed", Copy), outcome);
    }

    [Fact]
    public async Task Save_As_with_a_failed_upload_names_the_copy_that_now_exists()
    {
        var ops = new FakeOps { FailAt = "upload" };
        var outcome = await WearableSaveSequence.RunAsync(ops, SaveAs());
        Assert.Equal(new WearableSaveOutcome(false, false, "upload_failed", Copy), outcome);
        Assert.DoesNotContain(ops.Calls, c => c.StartsWith("swap") || c == "rebake");
    }

    [Fact]
    public async Task A_step_that_throws_ends_the_save_with_a_reason_and_not_an_exception()
    {
        var ops = new FakeOps { ThrowAt = "upload" };
        var outcome = await WearableSaveSequence.RunAsync(ops, Save());
        Assert.False(outcome.Saved);
        Assert.Equal("failed", outcome.Reason);
        Assert.DoesNotContain("rebake", ops.Calls);
    }

    [Fact]
    public async Task A_request_with_no_item_or_no_data_is_refused_before_any_step()
    {
        var ops = new FakeOps();
        var a = await WearableSaveSequence.RunAsync(ops, Save(item: Guid.Empty));
        var b = await WearableSaveSequence.RunAsync(ops, Save(data: []));
        Assert.Equal("no_item", a.Reason);
        Assert.Equal("no_data", b.Reason);
        Assert.Empty(ops.Calls);
    }

    // ---------------- intents and the pipeline ----------------

    private static (ActionPipeline Pipeline, FakeTransport Transport) Pipe()
    {
        var t = new FakeTransport();
        return (new ActionPipeline(() => t, () => true, new CoreLog(new ListSink(), "test")), t);
    }

    private static byte[] Shirt(string name = "x")
    {
        var a = new WearableAsset { Name = name, Type = WearableType.Shirt, PermissionsLines = ["\tpermissions 0", "\t{", "\t}"], SaleInfoLines = ["\tsale_info\t0", "\t{", "\t}"] };
        a.Params[803] = 1f;
        return a.ToBytes();
    }

    [Fact]
    public void A_good_save_becomes_one_command_with_the_item_the_cleaned_name_the_asset_type_and_the_file()
    {
        var (p, t) = Pipe();
        var data = Shirt();
        Assert.Equal(ActionStatus.Sent, p.Submit(new WearableSaveIntent(Item, "  Plain shirt ", WearableType.Shirt, data)).Status);
        var cmd = Assert.IsType<SaveWearableCommand>(Assert.Single(t.Commands));
        Assert.Equal((Item, "Plain shirt", 5), (cmd.ItemId, cmd.Name, cmd.AssetType));
        Assert.Same(data, cmd.Data);
    }

    [Fact]
    public void A_good_save_as_becomes_one_command_with_the_source_the_folder_and_a_body_part_asset_type_for_a_body_part()
    {
        var (p, t) = Pipe();
        var shape = new WearableAsset { Name = "s", Type = WearableType.Shape, PermissionsLines = ["\tpermissions 0", "\t{", "\t}"], SaleInfoLines = ["\tsale_info\t0", "\t{", "\t}"] }.ToBytes();
        Assert.Equal(ActionStatus.Sent, p.Submit(new WearableSaveAsIntent(Item, Folder, "My shape 2", WearableType.Shape, shape)).Status);
        var cmd = Assert.IsType<SaveWearableAsCommand>(Assert.Single(t.Commands));
        Assert.Equal((Item, Folder, "My shape 2", 13), (cmd.SourceItemId, cmd.FolderId, cmd.Name, cmd.AssetType));
    }

    public static TheoryData<ActionIntent, string> Bad => new()
    {
        { new WearableSaveIntent(Guid.Empty, "x", WearableType.Shirt, Shirt()), "wearable.no_item" },
        { new WearableSaveIntent(Item, "", WearableType.Shirt, Shirt()), "wearable.bad_name" },
        { new WearableSaveIntent(Item, "bad\u0007name", WearableType.Shirt, Shirt()), "wearable.bad_name" },
        { new WearableSaveIntent(Item, "x", WearableType.Shirt, []), "wearable.no_data" },
        { new WearableSaveIntent(Item, "x", WearableType.Shirt, new byte[WearableAsset.MaxBytes + 1]), "wearable.too_big" },
        { new WearableSaveIntent(Item, "x", WearableType.Shirt, Encoding.UTF8.GetBytes("not a wearable")), "wearable.bad_data" },
        { new WearableSaveIntent(Item, "x", WearableType.Pants, Shirt()), "wearable.bad_data" },     // the file says shirt
        { new WearableSaveIntent(Item, "x", WearableType.None, Shirt()), "wearable.bad_type" },
        { new WearableSaveAsIntent(Guid.Empty, Folder, "x", WearableType.Shirt, Shirt()), "wearable.no_item" },
        { new WearableSaveAsIntent(Item, Guid.Empty, "x", WearableType.Shirt, Shirt()), "wearable.no_folder" },
        { new WearableSaveAsIntent(Item, Folder, " ", WearableType.Shirt, Shirt()), "wearable.bad_name" },
        { new WearableSaveAsIntent(Item, Folder, "x", WearableType.Shirt, []), "wearable.no_data" },
    };

    [Theory]
    [MemberData(nameof(Bad))]
    public void A_save_that_breaks_a_rule_is_refused_with_that_rules_code_and_sends_nothing(ActionIntent intent, string code)
    {
        Assert.Contains(code, intent.Validate().Select(i => i.Code));
        var (p, t) = Pipe();
        Assert.NotEqual(ActionStatus.Sent, p.Submit(intent).Status);
        Assert.Empty(t.Commands);
    }

    [Fact]
    public void The_two_kinds_are_known_to_the_pipeline_and_to_nothing_else_by_accident()
    {
        Assert.Equal(ActionKind.WearableSave, new WearableSaveIntent(Item, "x", WearableType.Shirt, Shirt()).Kind);
        Assert.Equal(ActionKind.WearableSaveAs, new WearableSaveAsIntent(Item, Folder, "x", WearableType.Shirt, Shirt()).Kind);
    }
}

/// <summary>The protocol adapter's side of saving, as far as it can be tested without a grid: the commands are known to it and refused
/// without a circuit, and the current-outfit link of an item is found by the item it points to.</summary>
public class WearableAdapterTests
{
    private static readonly Guid Item = Guid.Parse("b2000000-0000-4000-8000-000000000001");

    [Fact]
    public void Without_a_circuit_the_save_commands_are_refused_for_that_reason()
    {
        using var t = new Viewer.Core.Protocol.LmvTransport(new Viewer.Core.Net.OutboundPolicy(new Viewer.Core.Net.OutboundOptions()), new CoreLog(new ListSink(), "test"));
        Assert.Equal(CommandDispatch.NotSent("not_connected"), t.Execute(new SaveWearableCommand(Item, "x", 5, [1])));
        Assert.Equal(CommandDispatch.NotSent("not_connected"), t.Execute(new SaveWearableAsCommand(Item, Guid.NewGuid(), "x", 5, [1])));
    }

    [Fact]
    public void The_link_to_an_item_is_found_by_the_item_it_points_to()
    {
        var a = new CofLink(Guid.NewGuid(), Guid.NewGuid(), "@400");
        var b = new CofLink(Guid.NewGuid(), Item, "@401");
        var found = WearableLinks.LinkTo([a, b], Item);
        Assert.Equal(b, found);
    }

    [Fact]
    public void An_item_that_is_not_worn_has_no_link_and_the_first_of_several_is_taken()
    {
        var a = new CofLink(Guid.NewGuid(), Guid.NewGuid(), "@400");
        Assert.Null(WearableLinks.LinkTo([a], Item));
        Assert.Null(WearableLinks.LinkTo([], Item));
        var first = new CofLink(Guid.NewGuid(), Item, "@401");
        var second = new CofLink(Guid.NewGuid(), Item, "@402");
        Assert.Equal(first, WearableLinks.LinkTo([first, second], Item));
    }
}

/// <summary>What the session does with a finished save, and its two reads of a wearable item.</summary>
public class WearableSessionTests
{
    private static readonly Guid Cof = Guid.Parse("00000000-0000-4000-8000-0000000000e1");
    private static readonly Guid RootId = Guid.Parse("00000000-0000-4000-8000-000000000001");
    private static readonly Guid Clothing = Guid.Parse("00000000-0000-4000-8000-0000000000c1");
    private static readonly Guid Item = Guid.Parse("c3000000-0000-4000-8000-000000000001");
    private static readonly Guid Asset = Guid.Parse("c3000000-0000-4000-8000-0000000000a1");

    private static (ViewerSession S, FakeTransport T) WithInventory()
    {
        var (s, t, _) = LoggedIn();
        t.Raise(new InventorySkeletonReceived(Me, RootId, Guid.NewGuid(), Guid.NewGuid(),
            new[] { new InventoryFolderData(RootId, Guid.Empty, "", FolderKind.Root, 1), new InventoryFolderData(Cof, RootId, "c", FolderKind.CurrentOutfit, 1),
                new InventoryFolderData(Clothing, RootId, "cl", FolderKind.Clothing, 1) }, Array.Empty<InventoryFolderData>()));
        t.Raise(new InventoryFolderFetched(Clothing, true, 1, 1, Array.Empty<InventoryFolderData>(), new[] { new InventoryItemData(Item, Clothing, "Shirt", ItemKind.Clothing, 5, 18, 4, null) }));
        return (s, t);
    }

    [Fact]
    public void A_finished_save_is_kept_announced_and_settles_the_current_outfit_folder_and_the_items_folder_with_the_grid()
    {
        var (s, t) = WithInventory();
        t.FolderRequests.Clear();
        var heard = new List<WearableSaveFinished>();
        s.EditedItemSaveDone += heard.Add;
        var done = new WearableSaveFinished(false, true, true, "", Item);
        t.Raise(done);
        Assert.Equal(done, s.LastEditedItemSave);
        Assert.Single(heard);
        Assert.Contains(t.FolderRequests, f => f.Folder == Cof);
        Assert.Contains(t.FolderRequests, f => f.Folder == Clothing);
    }

    [Fact]
    public async Task A_wearable_asset_is_read_through_the_transport_and_an_unknown_one_is_not_found()
    {
        var (s, t) = WithInventory();
        t.WearableAssets[Asset] = [1, 2, 3];
        var ok = await s.FetchEditAssetAsync(Asset, 5, CancellationToken.None);
        Assert.Equal(Viewer.Core.Assets.AssetFetchStatus.Ok, ok.Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, ok.Bytes.ToArray());
        var none = await s.FetchEditAssetAsync(Guid.NewGuid(), 5, CancellationToken.None);
        Assert.Equal(Viewer.Core.Assets.AssetFetchStatus.NotFound, none.Status);
    }

    [Fact]
    public void An_item_reports_its_asset_and_permissions_and_an_unknown_item_reports_nothing()
    {
        var (s, t) = WithInventory();
        var info = new WearableItemInfo(Asset, 5, true, false);
        t.WearableItems[Item] = info;
        Assert.Equal(info, s.EditItemInfo(Item));
        Assert.Null(s.EditItemInfo(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(0x7fffffffu, true, true)]
    [InlineData(0x4000u, true, false)]
    [InlineData(0x8000u, false, true)]
    [InlineData(0x2000u, false, false)]
    [InlineData(0u, false, false)]
    public void The_modify_and_copy_bits_of_an_owner_mask_decide_what_the_editor_allows(uint mask, bool modify, bool copy)
    {
        Assert.Equal(modify, WearablePermissions.CanModify(mask));
        Assert.Equal(copy, WearablePermissions.CanCopy(mask));
    }
}

/// <summary>The grid's inventory API takes a wearable save as a change to the item that names the upload's transaction (the official viewer's
/// form: the transaction id stands where the asset id would). These cover the request and how the answer is read.</summary>
public class WearableItemUpdateTests
{
    private static readonly Guid Item = Guid.Parse("e5000000-0000-4000-8000-000000000001");
    private static readonly Guid Tx = Guid.Parse("e5000000-0000-4000-8000-0000000000e1");
    private static readonly Guid Old = Guid.Parse("e5000000-0000-4000-8000-0000000000a1");
    private static readonly Guid New = Guid.Parse("e5000000-0000-4000-8000-0000000000a2");

    [Fact]
    public void The_request_names_the_item_the_transaction_and_the_name_and_no_asset()
    {
        var body = Viewer.Core.Protocol.LmvTransport.ItemUpdateBody(Tx, "Plain shirt");
        Assert.Equal(new[] { "hash_id", "name" }, body.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(Tx, body["hash_id"].AsUUID().Guid);
        Assert.Equal("Plain shirt", body["name"].AsString());
    }

    [Theory]
    [InlineData("https://sim.example.org:9000/cap/abc", "https://sim.example.org:9000/cap/abc/item/e5000000-0000-4000-8000-000000000001")]
    [InlineData("https://sim.example.org:9000/cap/abc/", "https://sim.example.org:9000/cap/abc/item/e5000000-0000-4000-8000-000000000001")]
    public void The_address_is_the_inventory_capability_with_the_item_added(string cap, string expected) =>
        Assert.Equal(expected, Viewer.Core.Protocol.LmvTransport.ItemUpdateUrl(new Uri(cap), Item).ToString());

    [Fact]
    public void The_asset_the_item_now_points_at_is_read_from_the_answer()
    {
        string reply = $"<?xml version=\"1.0\"?><llsd><map><key>item_id</key><uuid>{Item}</uuid><key>asset_id</key><uuid>{New}</uuid><key>name</key><string>x</string></map></llsd>";
        Assert.Equal(New, Viewer.Core.Protocol.LmvTransport.ReadUpdatedAsset(System.Text.Encoding.UTF8.GetBytes(reply)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not llsd")]
    [InlineData("<llsd><map><key>name</key><string>x</string></map></llsd>")]
    [InlineData("<llsd><array/></llsd>")]
    public void An_answer_with_no_asset_reads_as_none(string reply) =>
        Assert.Equal(Guid.Empty, Viewer.Core.Protocol.LmvTransport.ReadUpdatedAsset(System.Text.Encoding.UTF8.GetBytes(reply)));

    [Fact]
    public void A_new_asset_id_is_a_change_the_same_id_is_not_and_none_cannot_be_told()
    {
        Assert.Equal(Viewer.Core.Protocol.ItemUpdateOutcome.Changed, Viewer.Core.Protocol.LmvTransport.JudgeUpdate(Old, New));
        Assert.Equal(Viewer.Core.Protocol.ItemUpdateOutcome.Same, Viewer.Core.Protocol.LmvTransport.JudgeUpdate(Old, Old));
        Assert.Equal(Viewer.Core.Protocol.ItemUpdateOutcome.Unknown, Viewer.Core.Protocol.LmvTransport.JudgeUpdate(Old, Guid.Empty));
    }
}

/// <summary>The session's read of the texture behind an inventory texture item goes to the transport.</summary>
public class TextureAssetSessionTests
{
    [Fact]
    public void The_texture_asset_of_an_item_comes_from_the_transport_and_an_unknown_item_has_none()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        var item = Guid.Parse("e6000000-0000-4000-8000-000000000001");
        var asset = Guid.Parse("e6000000-0000-4000-8000-0000000000a1");
        t.TextureAssets[item] = asset;
        Assert.Equal(asset, s.TextureAssetOf(item));
        Assert.Equal(Guid.Empty, s.TextureAssetOf(Guid.NewGuid()));
    }
}
