// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Xunit;
using static Viewer.Core.Tests.InventoryModelTests;
using static Viewer.Core.Tests.OutfitPlannerTests;

namespace Viewer.Core.Tests;

/// <summary>
/// After a layer of clothing is taken off, the layer notes of the links that stay are written again so each type's layers run 0, 1,
/// 2 and so on, as the official viewer does (LLAppearanceMgr::updateClothingOrderingInfo, which sorts a type's links by layer, with
/// links whose note is not valid after the valid ones, and renumbers any whose note differs). Offline; every member builds its own
/// model, so the class runs in parallel.
/// </summary>
public class OutfitLayerOrderTests
{
    private const int ShirtC = 40, ShirtE = 41;

    private static Rig WithShirts(params (int Link, int Shirt, string Note)[] worn)
    {
        var r = new Rig();
        r.Put(ClothF, Thing(ShirtA, ClothF, "shirt a", ItemKind.Clothing, WearableType.Shirt), Thing(ShirtB, ClothF, "shirt b", ItemKind.Clothing, WearableType.Shirt),
            Thing(ShirtC, ClothF, "shirt c", ItemKind.Clothing, WearableType.Shirt), Thing(ShirtE, ClothF, "shirt e", ItemKind.Clothing, WearableType.Shirt),
            Thing(PantsC, ClothF, "pants c", ItemKind.Clothing, WearableType.Pants));
        r.SetCof(worn.Select(w => LinkTo(w.Link, w.Shirt, "link " + w.Link, w.Note)).ToArray());
        return r;
    }

    [Fact]
    public void Taking_off_a_middle_layer_renumbers_the_layers_above_it()
    {
        var r = WithShirts((1, ShirtA, "@400"), (2, ShirtB, "@401"), (3, ShirtC, "@402"));
        var p = OutfitPlanner.TakeOff(r.Model, Id(1002));
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(new[] { Id(1002) }, p.Change!.RemoveLinks.ToArray());
        Assert.Equal(new[] { new LinkDescription(Id(1003), "@401") }, p.Change.Renumbered.ToArray());
    }

    [Fact]
    public void Taking_off_the_lowest_layer_moves_every_layer_down_one()
    {
        var r = WithShirts((1, ShirtA, "@400"), (2, ShirtB, "@401"), (3, ShirtC, "@402"));
        var p = OutfitPlanner.TakeOff(r.Model, Id(1001));
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(new[] { new LinkDescription(Id(1002), "@400"), new LinkDescription(Id(1003), "@401") }, p.Change!.Renumbered.OrderBy(d => d.Description).ToArray());
    }

    [Fact]
    public void Taking_off_the_top_layer_renumbers_nothing()
    {
        var r = WithShirts((1, ShirtA, "@400"), (2, ShirtB, "@401"), (3, ShirtC, "@402"));
        var p = OutfitPlanner.TakeOff(r.Model, Id(1003));
        Assert.True(p.Ok, p.Refusal);
        Assert.Empty(p.Change!.Renumbered);
    }

    [Fact]
    public void Each_clothing_type_is_numbered_on_its_own()
    {
        var r = WithShirts((1, ShirtA, "@400"), (2, ShirtB, "@401"), (4, PantsC, "@500"));
        var p = OutfitPlanner.TakeOff(r.Model, Id(1001));
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(new[] { new LinkDescription(Id(1002), "@400") }, p.Change!.Renumbered.ToArray());   // the pants keep @500
    }

    [Fact]
    public void A_link_whose_note_is_not_valid_goes_after_the_valid_ones_and_is_numbered_too()
    {
        // shirts a (@400), e (no note) and c (@402) are worn; taking off the pants leaves those three, which are put in order
        var r = WithShirts((1, ShirtA, "@400"), (2, ShirtE, ""), (3, ShirtC, "@402"), (4, PantsC, "@500"));
        var p = OutfitPlanner.TakeOff(r.Model, Id(1004));
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(new[] { new LinkDescription(Id(1002), "@402"), new LinkDescription(Id(1003), "@401") }, p.Change!.Renumbered.OrderBy(d => d.LinkId).ToArray());
    }

    [Fact]
    public void A_change_that_only_renumbers_is_not_empty_and_the_intent_checks_the_notes()
    {
        var only = new OutfitChange(CofF, [], [], [], [], null, new[] { new LinkDescription(Id(1003), "@401") });
        Assert.False(only.IsEmpty);
        Assert.Empty(new OutfitChangeIntent(only).Validate());
        var noId = only with { NewDescriptions = new[] { new LinkDescription(Guid.Empty, "@401") } };
        Assert.Contains(new OutfitChangeIntent(noId).Validate(), i => i.Code == "outfit.no_item");
        var tooLong = only with { NewDescriptions = new[] { new LinkDescription(Id(1003), new string('x', 300)) } };
        Assert.Contains(new OutfitChangeIntent(tooLong).Validate(), i => i.Code == "outfit.bad_name");
    }
}
