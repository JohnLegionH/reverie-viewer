// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Outfit;
using Xunit;
using static Viewer.Core.Tests.OutfitPlannerTests;

namespace Viewer.Core.Tests;

/// <summary>
/// The official ways to take worn clothing off from the own avatar's menu: Take Off, then Clothes, then one entry per clothing
/// type (enabled while one of that type is worn; it takes off the top layer of that type) and All Clothes (every clothing
/// layer, no body part, no object). From the official viewer's menu_avatar_self.xml (the "Clothes" menu and its order) and
/// llviewermenu.cpp (LLEditEnableTakeOff, LLEditTakeOff, which removes the last-worn layer of the type, and
/// LLAppearanceMgr::removeAllClothesFromAvatar). Engine-free; every member builds its own data, so the class runs in parallel.
/// </summary>
public class TakeOffClothesTests
{
    private static readonly WornEntry Shirt = new(Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000f1"), "Example Shirt", WearableType.Shirt, 0);
    private static readonly WornEntry AlphaLow = new(Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000f2"), "Example Alpha One", WearableType.Alpha, 0);
    private static readonly WornEntry AlphaTop = new(Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000f3"), "Example Alpha Two", WearableType.Alpha, 1);
    private static readonly WornEntry Hat = new(Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000f4"), "Example Hat", WearableType.None, 0, IsObject: true);

    private static readonly string[] OfficialOrder =
        ["shirt", "pants", "skirt", "shoes", "socks", "jacket", "gloves", "undershirt", "underpants", "tattoo", "physics", "alpha"];

    private static ContextEntry Clothes(IReadOnlyList<ContextEntry> entries)
    {
        var takeOff = Assert.Single(entries, e => e.Id == "take_off");
        Assert.NotNull(takeOff.Children);
        return Assert.Single(takeOff.Children!, e => e.Id == "clothes");
    }

    [Fact]
    public void Take_off_holds_a_clothes_menu_with_each_clothing_type_in_the_official_order_then_all_clothes()
    {
        var clothes = Clothes(ContextMenus.ForSelf(new SelfMenuContext(false, false, [Shirt, AlphaLow, AlphaTop, Hat])));
        Assert.Equal(OfficialOrder.Select(t => $"take_off_type/{t}").Append("take_off_all").ToArray(), clothes.Children!.Select(c => c.Id).ToArray());
        Assert.Equal(OfficialOrder.Select(t => $"menu.take_off.{t}").Append("menu.take_off.all_clothes").ToArray(), clothes.Children!.Select(c => c.LabelKey).ToArray());
        Assert.True(clothes.Enabled);
    }

    [Fact]
    public void A_clothing_type_is_enabled_only_while_one_of_that_type_is_worn()
    {
        var clothes = Clothes(ContextMenus.ForSelf(new SelfMenuContext(false, false, [Shirt, AlphaLow, Hat])));
        var enabled = clothes.Children!.Where(c => c.Enabled).Select(c => c.Id).ToArray();
        Assert.Equal(["take_off_type/shirt", "take_off_type/alpha", "take_off_all"], enabled);
    }

    [Fact]
    public void With_no_clothing_worn_every_type_is_disabled()
    {
        var clothes = Clothes(ContextMenus.ForSelf(new SelfMenuContext(false, false, [Hat])));
        Assert.All(clothes.Children!.Where(c => c.Id.StartsWith("take_off_type/", StringComparison.Ordinal)), c => Assert.False(c.Enabled));
    }

    private sealed class SelfRig
    {
        public readonly WireRig Rig = new();
        public readonly List<string> Calls = [];

        public SelfRig(IReadOnlyList<WornEntry> worn)
        {
            Rig.C.SelfHooks = new SelfHooks(() => { }, () => { }, () => worn, id => Calls.Add("take_off:" + id), () => false, () => { },
                TakeOffAllClothes: () => Calls.Add("all_clothes"));
            Rig.C.RightClick(Rig.Down(100, 100, ignoreSelf: false));
        }
    }

    [Fact]
    public void Choosing_a_type_takes_off_the_top_layer_of_that_type()
    {
        var s = new SelfRig([Shirt, AlphaLow, AlphaTop, Hat]);
        Assert.True(s.Rig.C.Choose("take_off_type/alpha"));
        Assert.Equal([$"take_off:{AlphaTop.Id}"], s.Calls);
    }

    [Fact]
    public void Choosing_a_type_that_is_not_worn_does_nothing()
    {
        var s = new SelfRig([Shirt]);
        Assert.False(s.Rig.C.Choose("take_off_type/alpha"));
        Assert.Empty(s.Calls);
    }

    [Fact]
    public void Choosing_all_clothes_runs_the_all_clothes_hook_once()
    {
        var s = new SelfRig([Shirt, AlphaLow, Hat]);
        Assert.True(s.Rig.C.Choose("take_off_all"));
        Assert.Equal(["all_clothes"], s.Calls);
    }

    // ---------------- right-clicking one's own worn object ----------------
    // The official viewer's menu for an own attachment (menu_attachment_self.xml) holds the attachment's own entries and then the own
    // avatar's part, Take Off and Clothes included: a mesh body or alpha layers can leave nothing else to right-click.

    private static readonly Guid WornItem = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000f9");

    private static SelfRig OnAttachment(IReadOnlyList<WornEntry> worn)
    {
        var s = new SelfRig(worn);
        s.Rig.Worn(10, 6, WornItem, i: new Viewer.Core.World.PrimInteraction(0, Viewer.Core.Interaction.ObjectFlagBits.YouOwner | Viewer.Core.Interaction.ObjectFlagBits.Copy));
        s.Rig.C.RightClick(s.Rig.DownAtWorn());
        return s;
    }

    [Fact]
    public void An_own_worn_objects_menu_also_holds_take_off_with_the_clothes_menu()
    {
        var s = OnAttachment([Shirt, AlphaLow, AlphaTop]);
        Assert.Equal(MenuKind.Object, s.Rig.C.Menu);
        var takeOff = Assert.Single(s.Rig.C.Entries, e => e.Id == "take_off");
        Assert.True(takeOff.Enabled);
        Assert.Contains(takeOff.Children!, e => e.Id == "clothes");
        Assert.Contains(s.Rig.C.Entries, e => e.Id == "detach");   // the object's own entries stay first
    }

    [Fact]
    public void Take_off_a_type_from_an_own_worn_objects_menu_takes_off_that_types_top_layer()
    {
        var s = OnAttachment([Shirt, AlphaLow, AlphaTop]);
        Assert.True(s.Rig.C.Choose("take_off_type/alpha"));
        Assert.Equal([$"take_off:{AlphaTop.Id}"], s.Calls);
    }

    [Fact]
    public void The_own_avatars_part_stays_in_an_own_worn_objects_menu_when_the_objects_properties_arrive()
    {
        var s = OnAttachment([Shirt, AlphaLow, AlphaTop]);
        // the menu is refreshed when the object's properties arrive (for its sale); the own avatar's part must stay in it
        s.Rig.C.Receive(new Viewer.Core.Session.ObjectPropertiesReceived(WireRig.G(10), "Example Body", "", Guid.Empty, Guid.Empty, Guid.Empty, null, 0, 0, 0, 0, false));
        Assert.Contains(s.Rig.C.Entries, e => e.Id == "take_off");
        Assert.True(s.Rig.C.Choose("take_off_type/alpha"));
        Assert.Equal([$"take_off:{AlphaTop.Id}"], s.Calls);
    }

    // ---------------- the planner: all clothes in one change ----------------

    [Fact]
    public void All_clothes_removes_every_clothing_link_and_keeps_body_parts_and_objects()
    {
        var r = new Rig();
        // the alpha's item is not loaded: the link's layer note says what it is (an alpha, layer 0)
        r.SetCof(LinkTo(1, ShirtA, "shirt a", "@400"), LinkTo(2, PantsC, "pants c", "@500"), LinkTo(3, 77, "alpha not loaded", "@1300"),
            LinkTo(4, ShapeS1, "shape one"), LinkTo(5, EyesE1, "eyes one"), LinkTo(6, ObjO1, "hat"));
        var p = OutfitPlanner.TakeOffAllClothes(r.Model);
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(new[] { Id(1001), Id(1002), Id(1003) }, p.Change!.RemoveLinks.OrderBy(x => x).ToArray());
        Assert.Empty(p.Change.Create);
        Assert.Empty(p.Change.Detach);
        Assert.Empty(p.Change.Attach);
    }

    [Fact]
    public void All_clothes_with_no_clothing_worn_is_refused()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ShapeS1, "shape one"), LinkTo(2, ObjO1, "hat"));
        Assert.Equal("outfit.not_worn", OutfitPlanner.TakeOffAllClothes(r.Model).Refusal);
        Assert.Equal("outfit.cof_unknown", OutfitPlanner.TakeOffAllClothes(new Rig(cofLoaded: false).Model).Refusal);
    }

    [Fact]
    public void The_worn_list_carries_each_entrys_type_layer_and_whether_it_is_an_object()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ShirtA, "shirt a", "@400"), LinkTo(2, ShirtB, "shirt b", "@401"), LinkTo(3, ObjO1, "hat"), LinkTo(4, ShapeS1, "shape one"));
        var worn = WornList.Of(r.Model);
        Assert.Equal([Id(ShirtB), Id(ShirtA), Id(ObjO1)], worn.Select(w => w.Id).OrderBy(i => i == Id(ObjO1) ? 1 : 0).ToArray());
        var top = Assert.Single(worn, w => w.Id == Id(ShirtB));
        Assert.Equal((WearableType.Shirt, 1, false), (top.Type, top.Layer, top.IsObject));
        Assert.True(Assert.Single(worn, w => w.Id == Id(ObjO1)).IsObject);
    }
}
