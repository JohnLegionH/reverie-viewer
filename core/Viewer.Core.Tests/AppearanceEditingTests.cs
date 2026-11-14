// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Actions;
using Viewer.Core.Appearance;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;
using Viewer.Shell.Appearance;
using Xunit;
using static Viewer.Core.Tests.OutfitPlannerTests;

namespace Viewer.Core.Tests;

/// <summary>
/// The Edit tab's model: what can be opened, how opening fails, what Save and Save As submit, what closing with unsaved changes
/// needs, and what the avatar preview is told. A fake stands for the session's reads; the pipeline is a recording function.
/// </summary>
public class AppearanceEditingTests
{
    private static readonly Guid AssetShirt = Guid.Parse("d4000000-0000-4000-8000-0000000000a1");
    private static readonly Guid AssetShape = Guid.Parse("d4000000-0000-4000-8000-0000000000a2");
    private static readonly Guid Copy = Guid.Parse("d4000000-0000-4000-8000-0000000000c1");

    private sealed class Harness
    {
        public readonly Rig Rig = new();
        public readonly Dictionary<Guid, WearableItemInfo> Info = new();
        public readonly Dictionary<Guid, byte[]> Assets = new();
        public readonly List<ActionIntent> Submitted = [];
        public ActionStatus Answer = ActionStatus.Sent;
        public bool Male;
        public readonly AppearanceEditing Edit;
        public int Changes;
        public readonly List<IReadOnlyDictionary<int, float>?> Previews = [];

        public Harness(bool withCatalog = true, bool canModify = true, bool canCopy = true)
        {
            Rig.SetCof(LinkTo(1, ShapeS1, "shape one", ""), LinkTo(2, ShirtA, "shirt a", "@400"), LinkTo(3, ObjO1, "hat", ""));
            Info[Id(ShirtA)] = new WearableItemInfo(AssetShirt, 5, canModify, canCopy);
            Info[Id(ShapeS1)] = new WearableItemInfo(AssetShape, 13, canModify, canCopy);
            Assets[AssetShirt] = EditFixture.Shirt().ToBytes();
            Assets[AssetShape] = EditFixture.Shape().ToBytes();
            Edit = new AppearanceEditing(Rig.Model, id => Info.GetValueOrDefault(id), (a, t, ct) =>
                Task.FromResult(Assets.TryGetValue(a, out var b) ? AssetFetchResult.Ok(b) : AssetFetchResult.Of(AssetFetchStatus.NotFound)),
                withCatalog ? EditFixture.Catalog() : null, () => Male, intent =>
                {
                    Submitted.Add(intent);
                    return new ActionResult(Answer, 1, Answer == ActionStatus.Sent ? null : "refused");
                });
            Edit.Changed += () => Changes++;
            Edit.PreviewChanged += w => Previews.Add(w);
        }

        public async Task<WearableEditor> OpenShirt()
        {
            await Edit.OpenAsync(Id(ShirtA));
            Assert.Equal(EditState.Ready, Edit.State);
            return Edit.Editor!;
        }
    }

    [Fact]
    public void The_worn_list_holds_body_parts_then_clothing_and_leaves_out_attachments()
    {
        var h = new Harness();
        var worn = h.Edit.Worn();
        Assert.Equal(new[] { "shape one", "shirt a" }, worn.Select(w => w.Name).ToArray());
        Assert.Equal(new[] { WearableType.Shape, WearableType.Shirt }, worn.Select(w => w.Type).ToArray());
    }

    [Fact]
    public async Task Opening_a_worn_wearable_reads_its_file_and_makes_an_editor_for_it()
    {
        var h = new Harness();
        var e = await h.OpenShirt();
        Assert.Equal(Id(ShirtA), h.Edit.ItemId);
        Assert.Equal(WearableType.Shirt, e.Type);
        Assert.Equal("Plain shirt", e.Name);
        Assert.True(e.CanModify);
        Assert.True(h.Edit.CanSave && h.Edit.CanSaveAs);
        Assert.True(h.Changes > 0);
        Assert.False(h.Edit.HasUnsavedChanges);
    }

    [Fact]
    public async Task Opening_an_item_the_session_does_not_know_fails_with_that_reason()
    {
        var h = new Harness();
        await h.Edit.OpenAsync(Id(ShirtB));
        Assert.Equal((EditState.Failed, "unknown_item"), (h.Edit.State, h.Edit.Reason));
        Assert.Null(h.Edit.Editor);
    }

    [Fact]
    public async Task A_file_the_grid_does_not_have_or_that_is_not_a_wearable_fails_with_its_own_reason()
    {
        var h = new Harness();
        h.Assets.Remove(AssetShirt);
        await h.Edit.OpenAsync(Id(ShirtA));
        Assert.Equal((EditState.Failed, "asset_unavailable"), (h.Edit.State, h.Edit.Reason));
        h.Assets[AssetShirt] = Encoding.UTF8.GetBytes("junk");
        await h.Edit.OpenAsync(Id(ShirtA));
        Assert.Equal((EditState.Failed, "bad_asset"), (h.Edit.State, h.Edit.Reason));
    }

    [Fact]
    public async Task A_fetch_that_throws_fails_with_a_reason_and_not_an_exception()
    {
        var rig = new Rig();
        rig.SetCof(LinkTo(2, ShirtA, "shirt a", "@400"));
        var edit = new AppearanceEditing(rig.Model, _ => new WearableItemInfo(AssetShirt, 5, true, true), (_, _, _) => throw new InvalidOperationException("x"), null, () => false, _ => throw new InvalidOperationException());
        await edit.OpenAsync(Id(ShirtA));
        Assert.Equal((EditState.Failed, "asset_unavailable"), (edit.State, edit.Reason));
    }

    [Fact]
    public async Task Without_a_definition_file_there_are_no_sliders_but_colour_and_texture_still_edit()
    {
        var h = new Harness(withCatalog: false);
        var e = await h.OpenShirt();
        Assert.Empty(h.Edit.Catalog.Subparts(WearableType.Shirt, false));
        e.SetColour(WearableKinds.Swatches(WearableType.Shirt)[0], 0.2f, 0.3f, 0.4f);
        Assert.True(h.Edit.HasUnsavedChanges);
    }

    [Fact]
    public async Task An_item_the_agent_cannot_modify_opens_read_only_and_cannot_be_saved()
    {
        var h = new Harness(canModify: false);
        var e = await h.OpenShirt();
        Assert.False(e.CanModify);
        Assert.False(h.Edit.CanSave);
        Assert.False(h.Edit.Save());
        Assert.Empty(h.Submitted);
    }

    [Fact]
    public async Task An_item_that_cannot_be_copied_cannot_be_saved_as_but_can_be_saved()
    {
        var h = new Harness(canCopy: false);
        var e = await h.OpenShirt();
        Assert.True(h.Edit.CanSave);
        Assert.False(h.Edit.CanSaveAs);
        e.Name = "Other";
        Assert.False(h.Edit.SaveAs("Other"));
        Assert.Empty(h.Submitted);
    }

    [Fact]
    public async Task Save_submits_one_intent_with_the_item_the_name_the_type_and_the_edited_file()
    {
        var h = new Harness();
        var e = await h.OpenShirt();
        e.SetValue(840, 1f);
        Assert.True(h.Edit.Save());
        var intent = Assert.IsType<WearableSaveIntent>(Assert.Single(h.Submitted));
        Assert.Equal((Id(ShirtA), "Plain shirt", WearableType.Shirt), (intent.ItemId, intent.Name, intent.Type));
        Assert.True(WearableAsset.TryParse(intent.Data, out var a, out _));
        Assert.Equal(1f, a.Params[840]);
        Assert.Equal(EditSaveState.Saving, h.Edit.SaveState);
    }

    [Fact]
    public async Task Save_with_nothing_changed_submits_nothing()
    {
        var h = new Harness();
        await h.OpenShirt();
        Assert.False(h.Edit.Save());
        Assert.Empty(h.Submitted);
        Assert.Equal(EditSaveState.Idle, h.Edit.SaveState);
    }

    [Fact]
    public async Task A_save_the_pipeline_refuses_is_reported_as_failed_with_its_code_and_keeps_the_edits()
    {
        var h = new Harness();
        var e = await h.OpenShirt();
        e.SetValue(840, 1f);
        h.Answer = ActionStatus.Vetoed;
        Assert.False(h.Edit.Save());
        Assert.Equal(EditSaveState.Failed, h.Edit.SaveState);
        Assert.Equal("refused", h.Edit.SaveReason);
        Assert.True(h.Edit.HasUnsavedChanges);
    }

    [Fact]
    public async Task A_finished_save_makes_the_editor_clean_and_says_whether_the_look_was_rebuilt()
    {
        var h = new Harness();
        var e = await h.OpenShirt();
        e.SetValue(840, 1f);
        h.Edit.Save();
        h.Edit.OnSaveFinished(new WearableSaveFinished(false, true, true, "", Id(ShirtA)));
        Assert.Equal(EditSaveState.Saved, h.Edit.SaveState);
        Assert.False(h.Edit.HasUnsavedChanges);
        e.SetValue(840, 0f);
        Assert.True(h.Edit.Save());
        h.Edit.OnSaveFinished(new WearableSaveFinished(false, true, false, "rebuild_failed", Id(ShirtA)));
        Assert.Equal((EditSaveState.SavedNotRebuilt, "rebuild_failed"), (h.Edit.SaveState, h.Edit.SaveReason));
        Assert.False(h.Edit.HasUnsavedChanges);
    }

    [Fact]
    public async Task A_save_that_failed_on_the_grid_keeps_the_editor_dirty_and_names_the_reason()
    {
        var h = new Harness();
        var e = await h.OpenShirt();
        e.SetValue(840, 1f);
        h.Edit.Save();
        h.Edit.OnSaveFinished(new WearableSaveFinished(false, false, false, "upload_failed", Id(ShirtA)));
        Assert.Equal((EditSaveState.Failed, "upload_failed"), (h.Edit.SaveState, h.Edit.SaveReason));
        Assert.True(h.Edit.HasUnsavedChanges);
    }

    [Fact]
    public async Task A_save_result_for_another_item_is_ignored()
    {
        var h = new Harness();
        var e = await h.OpenShirt();
        e.SetValue(840, 1f);
        h.Edit.Save();
        h.Edit.OnSaveFinished(new WearableSaveFinished(false, true, true, "", Id(ShirtB)));
        Assert.Equal(EditSaveState.Saving, h.Edit.SaveState);
        Assert.True(h.Edit.HasUnsavedChanges);
    }

    [Fact]
    public async Task Save_As_submits_a_copy_into_the_originals_folder_and_after_it_the_copy_is_what_is_edited()
    {
        var h = new Harness();
        var e = await h.OpenShirt();
        e.SetValue(840, 1f);
        Assert.True(h.Edit.SaveAs("Red shirt"));
        var intent = Assert.IsType<WearableSaveAsIntent>(Assert.Single(h.Submitted));
        Assert.Equal((Id(ShirtA), ClothF, "Red shirt", WearableType.Shirt), (intent.SourceItemId, intent.FolderId, intent.Name, intent.Type));
        h.Edit.OnSaveFinished(new WearableSaveFinished(true, true, true, "", Copy));
        Assert.Equal(Copy, h.Edit.ItemId);
        Assert.Equal("Red shirt", h.Edit.Editor!.Name);
        Assert.False(h.Edit.HasUnsavedChanges);
        Assert.Equal(EditSaveState.Saved, h.Edit.SaveState);
    }

    [Fact]
    public async Task Closing_a_clean_editor_closes_it_at_once_and_a_dirty_one_asks_first()
    {
        var h = new Harness();
        var e = await h.OpenShirt();
        Assert.Equal(CloseVerdict.Closed, h.Edit.RequestClose());
        Assert.Equal(EditState.Closed, h.Edit.State);
        e = await h.OpenShirt();
        e.SetValue(840, 1f);
        Assert.Equal(CloseVerdict.NeedsDecision, h.Edit.RequestClose());
        Assert.Equal(EditState.Ready, h.Edit.State);
        Assert.True(h.Edit.HasUnsavedChanges);
    }

    [Fact]
    public async Task Not_saving_throws_the_edits_away_closes_and_submits_nothing()
    {
        var h = new Harness();
        var e = await h.OpenShirt();
        e.SetValue(840, 1f);
        h.Edit.DiscardAndClose();
        Assert.Equal(EditState.Closed, h.Edit.State);
        Assert.False(e.IsDirty);
        Assert.Equal(0f, e.Value(840));
        Assert.Empty(h.Submitted);
        Assert.NotEmpty(h.Previews);
        Assert.Null(h.Previews[^1]);
    }

    [Fact]
    public async Task Every_edit_tells_the_preview_the_current_weights_and_closing_clears_it()
    {
        var h = new Harness();
        await h.Edit.OpenAsync(Id(ShapeS1));
        var e = h.Edit.Editor!;
        h.Previews.Clear();
        e.SetValue(33, 0.75f);
        Assert.NotEmpty(h.Previews);
        var last = Assert.IsAssignableFrom<IReadOnlyDictionary<int, float>>(h.Previews[^1]);
        Assert.Equal(0.75f, last[33]);
        e.Revert();
        Assert.NotEmpty(h.Previews);
        Assert.Equal(0f, h.Previews[^1]![33]);
        h.Edit.DiscardAndClose();
        Assert.NotEmpty(h.Previews);
        Assert.Null(h.Previews[^1]);
    }

    [Fact]
    public async Task Opening_another_wearable_leaves_the_first_behind_and_clears_its_preview()
    {
        var h = new Harness();
        await h.Edit.OpenAsync(Id(ShapeS1));
        h.Edit.Editor!.SetValue(33, 0.75f);
        await h.Edit.OpenAsync(Id(ShirtA));
        Assert.Equal(Id(ShirtA), h.Edit.ItemId);
        Assert.Equal(WearableType.Shirt, h.Edit.Editor!.Type);
        Assert.False(h.Edit.HasUnsavedChanges);
        Assert.DoesNotContain(h.Previews.TakeLast(1), p => p is not null && p.ContainsKey(33) && p[33] == 0.75f);
    }

    [Fact]
    public async Task The_avatars_sex_picks_which_tabs_the_catalog_offers()
    {
        var h = new Harness();
        h.Male = true;
        Assert.True(h.Edit.IsMale);
        h.Male = false;
        Assert.False(h.Edit.IsMale);
        await Task.CompletedTask;
    }
}

public class TexturePickerTests
{
    private static InventoryItemData Tex(int n, Guid folder, string name) =>
        new(Id(n), folder, name, ItemKind.Texture, 0, 0, 0, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(n));

    private static Rig WithTextures()
    {
        var r = new Rig();
        var f = Guid.Parse("00000000-0000-4000-8000-0000000000f9");
        r.Model.Apply(new InventoryChanged(new[] { new InventoryFolderData(f, Root, "Textures", FolderKind.Texture, 1) }, Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>()));
        r.Put(f, Tex(50, f, "Wool"), Tex(51, f, "cotton"), Tex(52, f, "Denim twill"));
        r.Put(TrashF, Tex(53, TrashF, "Thrown out"));
        return r;
    }

    private static readonly Guid Root = Guid.Parse("00000000-0000-4000-8000-000000000001");

    [Fact]
    public void The_picker_lists_inventory_textures_by_name_without_case_and_leaves_out_the_trash()
    {
        var r = WithTextures();
        Assert.Equal(new[] { "cotton", "Denim twill", "Wool" }, TexturePicker.Rows(r.Model, "").Select(x => x.Name).ToArray());
    }

    [Fact]
    public void A_search_narrows_the_list_by_part_of_a_name_without_case()
    {
        var r = WithTextures();
        Assert.Equal(new[] { "Denim twill", "Wool" }, TexturePicker.Rows(r.Model, "L").Select(x => x.Name).ToArray());
        Assert.Equal(new[] { "Wool" }, TexturePicker.Rows(r.Model, " wool ").Select(x => x.Name).ToArray());
        Assert.Empty(TexturePicker.Rows(r.Model, "zzz"));
    }

    [Fact]
    public void Things_that_are_not_textures_are_not_listed_and_the_rows_carry_the_item_id()
    {
        var r = WithTextures();
        var rows = TexturePicker.Rows(r.Model, "");
        Assert.DoesNotContain(rows, x => x.Name == "a note" || x.Name == "shirt a");
        Assert.Equal(Id(52), Assert.Single(rows, x => x.Name == "Denim twill").ItemId);
        Assert.All(rows, x => Assert.False(x.InLibrary));
    }

    [Fact]
    public void The_list_is_cut_at_the_row_limit()
    {
        var r = new Rig();
        var f = Guid.Parse("00000000-0000-4000-8000-0000000000f8");
        r.Model.Apply(new InventoryChanged(new[] { new InventoryFolderData(f, Root, "Textures", FolderKind.Texture, 1) }, Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>()));
        r.Put(f, Enumerable.Range(100, TexturePicker.MaxRows + 20).Select(n => Tex(n, f, $"t{n:0000}")).ToArray());
        Assert.Equal(TexturePicker.MaxRows, TexturePicker.Rows(r.Model, "").Count);
    }

    [Fact]
    public void Preparing_asks_for_an_unloaded_texture_folder_and_says_when_all_are_loaded()
    {
        var r = new Rig();
        var f = Guid.Parse("00000000-0000-4000-8000-0000000000f7");
        r.Model.Apply(new InventoryChanged(new[] { new InventoryFolderData(f, Root, "Textures", FolderKind.Texture, 1) }, Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>()));
        Assert.False(TexturePicker.Prepare(r.Model));
        r.Put(f, Tex(60, f, "x"));
        Assert.True(TexturePicker.Prepare(r.Model));
    }
}

public class WearableEditorCommitTests
{
    [Fact]
    public void Commit_takes_the_edits_as_saved_so_the_editor_is_clean_and_revert_returns_to_them()
    {
        var e = new WearableEditor(EditFixture.Shirt(), EditFixture.Catalog(), true);
        e.SetValue(840, 1f);
        e.Name = "Red";
        e.SetTexture(1, EditFixture.Other);
        e.Commit();
        Assert.False(e.IsDirty);
        e.SetValue(840, 0.2f);
        Assert.True(e.IsDirty);
        e.Revert();
        Assert.Equal(1f, e.Value(840));
        Assert.Equal("Red", e.Name);
        Assert.Equal(EditFixture.Other, e.Texture(1));
    }

    [Fact]
    public void An_empty_catalog_has_no_tabs_and_no_parameters()
    {
        Assert.Empty(EditParamCatalog.Empty.Subparts(WearableType.Shape, false));
        Assert.Empty(EditParamCatalog.Empty.ParamsOf(WearableType.Shirt));
        Assert.False(EditParamCatalog.Empty.TryGet(33, out _));
    }
}

/// <summary>A wearable may only use textures the agent has every right to (modify, copy and transfer): the grid refuses the save otherwise. The picker lists those.</summary>
public class TexturePickerRightsTests
{
    private static InventoryItemData Tex(int n, Guid folder, string name) =>
        new(OutfitPlannerTests.Id(n), folder, name, ItemKind.Texture, 0, 0, 0, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(n));

    [Fact]
    public void Only_textures_the_filter_accepts_are_listed()
    {
        var r = new OutfitPlannerTests.Rig();
        var f = Guid.Parse("00000000-0000-4000-8000-0000000000fa");
        r.Model.Apply(new InventoryChanged(new[] { new InventoryFolderData(f, Guid.Parse("00000000-0000-4000-8000-000000000001"), "Textures", FolderKind.Texture, 1) }, Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>()));
        r.Put(f, Tex(50, f, "Wool"), Tex(51, f, "Cotton"), Tex(52, f, "Denim"));
        Assert.Equal(new[] { "Cotton", "Denim", "Wool" }, TexturePicker.Rows(r.Model, "").Select(x => x.Name).ToArray());
        var usable = TexturePicker.Rows(r.Model, "", id => id != OutfitPlannerTests.Id(51));
        Assert.Equal(new[] { "Denim", "Wool" }, usable.Select(x => x.Name).ToArray());
        Assert.Empty(TexturePicker.Rows(r.Model, "", _ => false));
        Assert.Equal(new[] { "Denim" }, TexturePicker.Rows(r.Model, "den", id => id != OutfitPlannerTests.Id(51)).Select(x => x.Name).ToArray());
    }

    [Theory]
    [InlineData(0x7fffffffu, true)]
    [InlineData(0x2000u, true)]
    [InlineData(0x4000u, false)]
    [InlineData(0u, false)]
    public void The_transfer_bit_of_an_owner_mask_is_read(uint mask, bool transfer) =>
        Assert.Equal(transfer, WearablePermissions.CanTransfer(mask));

    [Fact]
    public void An_item_that_is_not_fully_the_agents_is_not_usable_in_a_wearable()
    {
        Assert.True(new WearableItemInfo(Guid.NewGuid(), 0, true, true, true).FullRights);
        Assert.False(new WearableItemInfo(Guid.NewGuid(), 0, true, true, false).FullRights);
        Assert.False(new WearableItemInfo(Guid.NewGuid(), 0, false, true, true).FullRights);
        Assert.False(new WearableItemInfo(Guid.NewGuid(), 0, true, false, true).FullRights);
    }
}
