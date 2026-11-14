// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Shell.Inventory;
using Viewer.Shell.Outfit;
using Xunit;
using static Viewer.Core.Tests.OutfitPlannerTests;

namespace Viewer.Core.Tests;

/// <summary>
/// The wear, take off and outfit actions as the windows use them: planned, refused with a reason, or submitted once and then
/// the current outfit folder read again; and the inventory's rule that a worn item is not trashed. Offline; every member builds
/// its own model, so the class runs in parallel.
/// </summary>
public class OutfitActionsTests
{
    private sealed class Rig2
    {
        public readonly Rig Inventory = new();
        public readonly List<ActionIntent> Submitted = new();
        public readonly List<(Guid Folder, Guid Owner)> Asked = new();
        public ActionResult Answer = new(ActionStatus.Sent, 1);
        public readonly OutfitActions Actions;
        public readonly InventoryModel Model;

        public Rig2(Action<Rig>? setup = null)
        {
            setup?.Invoke(Inventory);
            // a model that records what it is asked for: rebuild it from the rig's content
            Model = Inventory.Model;
            Actions = new OutfitActions(Model, i => { Submitted.Add(i); return Answer; });
        }
    }

    [Fact]
    public void Wearing_submits_one_change_and_reads_the_current_outfit_folder_again()
    {
        var r = new Rig2();
        var res = r.Actions.Wear(Id(ShirtA));
        Assert.True(res.Submitted);
        var intent = Assert.IsType<OutfitChangeIntent>(Assert.Single(r.Submitted));
        Assert.Equal(Id(ShirtA), Assert.Single(intent.Change.Create).TargetId);
        Assert.Equal(FolderLoad.Loading, r.Model.LoadState(CofF));   // read again
    }

    [Fact]
    public void A_refused_action_submits_nothing_and_says_why()
    {
        var r = new Rig2(rig => rig.SetCof(LinkTo(1, ShirtA, "shirt a", "@400")));
        var worn = r.Actions.Wear(Id(ShirtA));
        Assert.False(worn.Submitted);
        Assert.Equal("outfit.already_worn", worn.Code);
        Assert.Equal("outfit.not_worn", r.Actions.TakeOff(Id(ShapeS1)).Code);
        Assert.Empty(r.Submitted);
    }

    [Fact]
    public void Taking_off_a_body_part_is_never_submitted()
    {
        var r = new Rig2(rig => rig.SetCof(LinkTo(1, ShapeS1, "shape one")));
        var res = r.Actions.TakeOff(Id(ShapeS1));
        Assert.False(res.Submitted);
        Assert.Equal("outfit.bodypart", res.Code);
        Assert.Empty(r.Submitted);
    }

    [Fact]
    public void When_the_pipeline_cannot_send_it_is_reported_and_nothing_is_read_again()
    {
        var r = new Rig2();
        r.Answer = new ActionResult(ActionStatus.Unavailable, 0, "no_command_transport");
        var res = r.Actions.Wear(Id(ShirtA));
        Assert.False(res.Submitted);
        Assert.Equal("no_command_transport", res.Code);
        Assert.Equal(FolderLoad.Loaded, r.Model.LoadState(CofF));
    }

    [Fact]
    public void Wearing_an_outfit_that_has_not_arrived_asks_for_it_and_submits_nothing()
    {
        var r = new Rig2();
        r.Model.Apply(new InventoryFolderFetched(OutfitF, false, -1, -1, Array.Empty<InventoryFolderData>(), Array.Empty<InventoryItemData>()));
        var res = r.Actions.WearOutfit(OutfitF);
        Assert.False(res.Submitted);
        Assert.Equal("outfit.not_loaded", res.Code);
        Assert.Equal(FolderLoad.Loading, r.Model.LoadState(OutfitF));
        Assert.Empty(r.Submitted);
    }

    [Fact]
    public void Saving_submits_the_save_and_reads_my_outfits_again()
    {
        var r = new Rig2(rig => { rig.SetCof(LinkTo(1, ShirtA, "shirt a", "@400")); rig.Put(MyOutfitsF); });
        var res = r.Actions.SaveOutfit("  Evening ");
        Assert.True(res.Submitted);
        Assert.Equal(new OutfitSaveIntent(CofF, MyOutfitsF, "Evening"), Assert.IsType<OutfitSaveIntent>(Assert.Single(r.Submitted)));
        Assert.Equal(FolderLoad.Loading, r.Model.LoadState(MyOutfitsF));
        Assert.Equal("outfit.empty", new Rig2().Actions.SaveOutfit("x").Code);
    }

    [Fact]
    public void The_menus_offer_what_the_rules_allow()
    {
        var r = new Rig2(rig => rig.SetCof(LinkTo(1, ShirtA, "shirt a", "@400"), LinkTo(2, ShapeS1, "shape one"), LinkTo(3, ObjO1, "hat")));
        var a = r.Actions;
        Assert.True(a.IsWorn(Id(ShirtA)));
        Assert.False(a.CanWear(Id(ShirtA)) || a.CanAdd(Id(ShirtA)));      // already worn
        Assert.True(a.CanTakeOff(Id(ShirtA)));
        Assert.False(a.CanTakeOff(Id(ShapeS1)));                          // body parts only change
        Assert.True(a.CanWear(Id(ShapeS2)));
        Assert.False(a.CanAdd(Id(ShapeS2)));                              // a body part has no layers to add
        Assert.True(a.CanWear(Id(ShirtB)) && a.CanAdd(Id(ShirtB)));
        Assert.True(a.CanTakeOff(Id(ObjO1)));
        Assert.False(a.CanAttach(Id(ObjO1)));                             // worn
        Assert.True(a.CanAttach(Id(ObjO2)));
        Assert.False(a.CanWear(Id(NoteN)));
        Assert.False(a.CanWearOutfit(OutfitF));                           // empty
    }

    [Fact]
    public void A_worn_item_and_an_item_in_the_current_outfit_folder_cannot_be_moved_to_the_trash()
    {
        var r = new Rig2(rig => rig.SetCof(LinkTo(1, ShirtA, "shirt a", "@400")));
        var inv = new InventoryActions(r.Model, i => new ActionResult(ActionStatus.Sent, 1));
        Assert.Equal("inventory.worn", inv.WhyNotTrash(Id(ShirtA)));
        Assert.Equal("inventory.worn", inv.WhyNotTrash(Id(1001)));        // the link itself
        Assert.Null(inv.WhyNotTrash(Id(ShirtB)));
    }
}

/// <summary>The outfit window's lists without the engine. Written after the code and proved by mutation.</summary>
public class OutfitViewTests
{
    [Fact]
    public void Saved_outfits_are_the_outfit_folders_under_my_outfits_by_name_and_the_base_outfit_is_named()
    {
        var rig = new Rig();
        rig.SetCof(LinkTo(1, ShirtA, "shirt a", "@400"), new InventoryItemData(Id(1900), CofF, "x", ItemKind.LinkFolder, 25, 8, 0, null, OutfitF));
        rig.Model.Apply(new InventoryChanged(new[] { new InventoryFolderData(Guid.NewGuid(), MyOutfitsF, "Misc", FolderKind.None, 1) }, Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>()));
        var view = new OutfitView(rig.Model);
        Assert.Equal(new[] { "Party", "Work" }, view.Saved().Select(f => f.Name).ToArray());
        Assert.Equal("Party", view.BaseOutfitName());
        Assert.Equal("shirt a", Assert.Single(view.Current()).Name);
    }

    [Fact]
    public void Preparing_asks_for_the_folders_it_needs_and_says_when_they_are_here()
    {
        var rig = new Rig(cofLoaded: false);
        var view = new OutfitView(rig.Model);
        Assert.False(view.Prepare());
        Assert.Equal(FolderLoad.Loading, rig.Model.LoadState(CofF));
        Assert.Equal(FolderLoad.Loading, rig.Model.LoadState(MyOutfitsF));
        rig.SetCof();
        rig.Put(MyOutfitsF);
        Assert.True(view.Prepare());
    }

    [Fact]
    public void Without_a_base_outfit_link_there_is_no_base_name()
    {
        var rig = new Rig();
        Assert.Null(new OutfitView(rig.Model).BaseOutfitName());
    }
}
