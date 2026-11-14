// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Core.Session;
using Xunit;
using static Viewer.Core.Tests.InventoryModelTests;

namespace Viewer.Core.Tests;

/// <summary>
/// Wearing, adding, taking off, attaching and wearing an outfit: the changes the planner works out from the inventory, the
/// rules that refuse some, and the intents that carry a change through the action pipeline. Offline; every member builds its
/// own model, so the class runs in parallel.
/// </summary>
public class OutfitPlannerTests
{
    internal static readonly Guid CofF = Guid.Parse("00000000-0000-4000-8000-0000000000e1");
    internal static readonly Guid MyOutfitsF = Guid.Parse("00000000-0000-4000-8000-0000000000e2");
    internal static readonly Guid ClothF = Guid.Parse("00000000-0000-4000-8000-0000000000e3");
    internal static readonly Guid BodyF = Guid.Parse("00000000-0000-4000-8000-0000000000e4");
    internal static readonly Guid ObjF = Guid.Parse("00000000-0000-4000-8000-0000000000e5");
    internal static readonly Guid TrashF = Guid.Parse("00000000-0000-4000-8000-0000000000e6");
    internal static readonly Guid OutfitF = Guid.Parse("00000000-0000-4000-8000-0000000000e7");
    internal static readonly Guid OutfitF2 = Guid.Parse("00000000-0000-4000-8000-0000000000e8");

    internal static Guid Id(int n) => Guid.Parse($"20000000-0000-4000-8000-{n:x12}");

    // item numbers
    internal const int ShirtA = 1, ShirtB = 2, PantsC = 3, JacketD = 4, ShapeS1 = 5, ShapeS2 = 6, EyesE1 = 7, HairH1 = 8, ObjO1 = 9, ObjO2 = 10, NoteN = 11,
        ShirtInTrash = 12;

    internal static InventoryItemData Thing(int n, Guid parent, string name, ItemKind kind, WearableType wt = WearableType.None) =>
        new(Id(n), parent, name, kind, kind switch { ItemKind.Clothing => 5, ItemKind.Bodypart => 13, ItemKind.Object => 6, _ => 7 }, 18, (uint)(wt == WearableType.None ? 0 : (int)wt),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(n));

    internal static InventoryItemData LinkTo(int linkNo, int target, string name, string desc = "") =>
        new(Id(1000 + linkNo), CofF, name, ItemKind.Link, 24, 18, 0, null, Id(target), desc);

    internal sealed class Rig
    {
        public readonly InventoryModel Model;
        public readonly List<InventoryItemData> Cof = new();

        public Rig(bool cofLoaded = true, int attachmentLimit = -1)
        {
            var requests = new List<(Guid, Guid)>();
            Model = new InventoryModel((f, o) => requests.Add((f, o)), 64);
            Model.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot,
                new[]
                {
                    F(Root, Guid.Empty, "", FolderKind.Root), F(CofF, Root, "Current Outfit", FolderKind.CurrentOutfit), F(MyOutfitsF, Root, "My Outfits", FolderKind.MyOutfits),
                    F(ClothF, Root, "Clothing", FolderKind.Clothing), F(BodyF, Root, "Body Parts", FolderKind.Bodypart), F(ObjF, Root, "Objects", FolderKind.Object),
                    F(TrashF, Root, "Trash", FolderKind.Trash), F(OutfitF, MyOutfitsF, "Party", FolderKind.Outfit), F(OutfitF2, MyOutfitsF, "Work", FolderKind.Outfit),
                },
                new[] { F(LibRoot, Guid.Empty, "", FolderKind.Root), F(LibObjects, LibRoot, "Objects", FolderKind.Object) }, attachmentLimit));
            Put(ClothF, Thing(ShirtA, ClothF, "shirt a", ItemKind.Clothing, WearableType.Shirt), Thing(ShirtB, ClothF, "shirt b", ItemKind.Clothing, WearableType.Shirt),
                Thing(PantsC, ClothF, "pants c", ItemKind.Clothing, WearableType.Pants), Thing(JacketD, ClothF, "jacket d", ItemKind.Clothing, WearableType.Jacket));
            Put(BodyF, Thing(ShapeS1, BodyF, "shape one", ItemKind.Bodypart, WearableType.Shape), Thing(ShapeS2, BodyF, "shape two", ItemKind.Bodypart, WearableType.Shape),
                Thing(EyesE1, BodyF, "eyes one", ItemKind.Bodypart, WearableType.Eyes), Thing(HairH1, BodyF, "hair one", ItemKind.Bodypart, WearableType.Hair));
            Put(ObjF, Thing(ObjO1, ObjF, "hat", ItemKind.Object), Thing(ObjO2, ObjF, "wand", ItemKind.Object), Thing(NoteN, ObjF, "a note", ItemKind.Notecard));
            Put(TrashF, Thing(ShirtInTrash, TrashF, "old shirt", ItemKind.Clothing, WearableType.Shirt));
            Put(OutfitF);
            Put(OutfitF2);
            Model.Apply(new InventoryFolderFetched(LibObjects, true, 2, 0, Array.Empty<InventoryFolderData>(),
                new[] { Thing(99, LibObjects, "library shirt", ItemKind.Clothing, WearableType.Shirt) }));
            if (cofLoaded)
                SetCof();
        }

        public void Put(Guid folder, params InventoryItemData[] items) =>
            Model.Apply(new InventoryFolderFetched(folder, true, 2, items.Length, Array.Empty<InventoryFolderData>(), items));

        public void SetCof(params InventoryItemData[] links)
        {
            Cof.Clear();
            Cof.AddRange(links);
            Put(CofF, links);
        }

        public static InventoryItemData InOutfit(Guid folder, int linkNo, int target, string name, string desc = "") =>
            new(Id(2000 + linkNo), folder, name, ItemKind.Link, 24, 18, 0, null, Id(target), desc);
    }

    private static NewOutfitLink[] Created(OutfitPlan p) => p.Change!.Create.ToArray();

    // ---------------- reading what is worn ----------------

    [Fact]
    public void An_item_is_worn_when_the_current_outfit_folder_holds_a_link_to_it()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ShirtA, "shirt a", "@400"));
        Assert.True(OutfitPlanner.IsWorn(r.Model, Id(ShirtA)));
        Assert.False(OutfitPlanner.IsWorn(r.Model, Id(ShirtB)));
    }

    [Fact]
    public void The_wearable_type_comes_from_the_low_byte_of_the_flags_and_only_for_clothing_and_body_parts()
    {
        Assert.Equal(WearableType.Jacket, OutfitPlanner.WearableTypeOf(Thing(1, ClothF, "x", ItemKind.Clothing, WearableType.Jacket)));
        Assert.Equal(WearableType.Eyes, OutfitPlanner.WearableTypeOf(Thing(1, BodyF, "x", ItemKind.Bodypart, WearableType.Eyes)));
        Assert.Equal(WearableType.None, OutfitPlanner.WearableTypeOf(Thing(1, ObjF, "x", ItemKind.Object)));
        Assert.Equal(WearableType.Universal, OutfitPlanner.WearableTypeOf(Thing(1, ClothF, "x", ItemKind.Clothing, WearableType.Universal) with { Flags = 0xFFFFFF00u | 16u }));
        Assert.True(OutfitPlanner.IsBodyPart(WearableType.Shape) && OutfitPlanner.IsBodyPart(WearableType.Eyes));
        Assert.False(OutfitPlanner.IsBodyPart(WearableType.Shirt) || OutfitPlanner.IsBodyPart(WearableType.Physics) || OutfitPlanner.IsBodyPart(WearableType.None));
    }

    // ---------------- wear and add ----------------

    [Fact]
    public void Wearing_a_shirt_when_none_is_worn_makes_one_link_with_the_first_layer_order()
    {
        var r = new Rig();
        var p = OutfitPlanner.Wear(r.Model, Id(ShirtA), add: false);
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(CofF, p.Change!.CofId);
        var link = Assert.Single(Created(p));
        Assert.Equal(new NewOutfitLink(Id(ShirtA), "shirt a", "@400"), link);
        Assert.Empty(p.Change.RemoveLinks);
        Assert.Empty(p.Change.Attach);
    }

    [Fact]
    public void Wearing_replaces_the_topmost_layer_of_that_type_and_adding_keeps_it_and_stacks_above()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ShirtA, "shirt a", "@400"));
        var replace = OutfitPlanner.Wear(r.Model, Id(ShirtB), add: false);
        Assert.Equal(new[] { Id(1001) }, replace.Change!.RemoveLinks.ToArray());
        Assert.Equal("@400", Assert.Single(Created(replace)).Description);

        var add = OutfitPlanner.Wear(r.Model, Id(ShirtB), add: true);
        Assert.Empty(add.Change!.RemoveLinks);
        Assert.Equal("@401", Assert.Single(Created(add)).Description);
    }

    [Fact]
    public void A_body_part_always_replaces_every_link_of_its_type_whether_worn_or_added()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ShapeS1, "shape one", ""), LinkTo(2, EyesE1, "eyes one", ""));
        foreach (bool add in new[] { false, true })
        {
            var p = OutfitPlanner.Wear(r.Model, Id(ShapeS2), add);
            Assert.True(p.Ok, p.Refusal);
            Assert.Equal(new[] { Id(1001) }, p.Change!.RemoveLinks.ToArray());   // the eyes stay
            Assert.Equal(Id(ShapeS2), Assert.Single(Created(p)).TargetId);
        }
    }

    [Fact]
    public void At_the_layer_limit_adding_removes_the_topmost_of_that_type_and_with_none_of_that_type_it_is_refused()
    {
        var r = new Rig();
        var many = Enumerable.Range(0, OutfitPlanner.MaxClothingLayers).Select(i => LinkTo(100 + i, JacketD, "jacket d", "@" + (800 + i))).ToArray();
        r.SetCof(many);
        var swap = OutfitPlanner.Wear(r.Model, Id(ShirtA), add: true);
        Assert.False(swap.Ok);
        Assert.Equal("outfit.too_many", swap.Refusal);   // no shirt to give way

        r.SetCof(many.Take(OutfitPlanner.MaxClothingLayers - 1).Append(LinkTo(500, ShirtB, "shirt b", "@400")).ToArray());
        var p = OutfitPlanner.Wear(r.Model, Id(ShirtA), add: true);
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(new[] { Id(1500) }, p.Change!.RemoveLinks.ToArray());
    }

    [Theory]
    [InlineData(NoteN, "outfit.not_wearable")]
    [InlineData(ShirtInTrash, "outfit.in_trash")]
    [InlineData(777, "outfit.not_wearable")]
    public void Items_that_cannot_be_worn_are_refused_with_a_reason(int item, string refusal)
    {
        var r = new Rig();
        var p = OutfitPlanner.Wear(r.Model, Id(item), add: false);
        Assert.False(p.Ok);
        Assert.Equal(refusal, p.Refusal);
    }

    [Fact]
    public void An_item_already_worn_is_refused_and_so_is_everything_before_the_current_outfit_is_known()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ShirtA, "shirt a", "@400"));
        Assert.Equal("outfit.already_worn", OutfitPlanner.Wear(r.Model, Id(ShirtA), false).Refusal);
        var unknown = new Rig(cofLoaded: false);
        unknown.Model.Invalidate(CofF);   // load state: loading
        Assert.Equal("outfit.cof_unknown", OutfitPlanner.Wear(unknown.Model, Id(ShirtA), false).Refusal);
        Assert.Equal("outfit.cof_unknown", OutfitPlanner.TakeOff(unknown.Model, Id(ShirtA)).Refusal);
        Assert.Equal("outfit.cof_unknown", OutfitPlanner.Attach(unknown.Model, Id(ObjO1), 0, false).Refusal);
    }

    [Fact]
    public void A_link_in_the_users_inventory_is_worn_through_the_item_it_points_to()
    {
        var r = new Rig();
        r.Put(OutfitF, Rig.InOutfit(OutfitF, 1, PantsC, "pants c"));
        var p = OutfitPlanner.Wear(r.Model, Id(2001), add: false);
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(Id(PantsC), Assert.Single(Created(p)).TargetId);
        Assert.Equal("@500", Created(p)[0].Description);
    }

    // ---------------- take off ----------------

    [Fact]
    public void Taking_off_clothing_removes_every_link_to_it()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ShirtA, "shirt a", "@400"), LinkTo(2, ShirtA, "shirt a", "@401"), LinkTo(3, PantsC, "pants c", "@500"));
        var p = OutfitPlanner.TakeOff(r.Model, Id(ShirtA));
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(new[] { Id(1001), Id(1002) }, p.Change!.RemoveLinks.OrderBy(x => x).ToArray());
        Assert.Empty(p.Change.Create);
        Assert.Empty(p.Change.Detach);
    }

    [Fact]
    public void A_body_part_cannot_be_taken_off_and_an_item_not_worn_cannot_either()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ShapeS1, "shape one"), LinkTo(2, EyesE1, "eyes one"), LinkTo(3, HairH1, "hair one"));
        Assert.Equal("outfit.bodypart", OutfitPlanner.TakeOff(r.Model, Id(ShapeS1)).Refusal);
        Assert.Equal("outfit.bodypart", OutfitPlanner.TakeOff(r.Model, Id(EyesE1)).Refusal);
        Assert.Equal("outfit.not_worn", OutfitPlanner.TakeOff(r.Model, Id(ShirtA)).Refusal);
    }

    [Fact]
    public void Taking_off_an_attachment_removes_its_link_and_takes_the_object_off()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ObjO1, "hat"));
        var p = OutfitPlanner.TakeOff(r.Model, Id(ObjO1));
        Assert.True(p.Ok, p.Refusal);
        Assert.Equal(new[] { Id(1001) }, p.Change!.RemoveLinks.ToArray());
        Assert.Equal(new[] { Id(ObjO1) }, p.Change.Detach.ToArray());
    }

    // ---------------- attach ----------------

    [Fact]
    public void Attaching_an_object_links_it_and_asks_for_it_on_the_chosen_point_with_or_without_keeping_what_is_there()
    {
        var r = new Rig();
        var home = OutfitPlanner.Attach(r.Model, Id(ObjO1), 0, add: false);
        Assert.True(home.Ok, home.Refusal);
        Assert.Equal(new AttachRequest(Id(ObjO1), 0, false), Assert.Single(home.Change!.Attach));
        Assert.Equal(new NewOutfitLink(Id(ObjO1), "hat", ""), Assert.Single(Created(home)));
        var added = OutfitPlanner.Attach(r.Model, Id(ObjO2), 5, add: true);
        Assert.Equal(new AttachRequest(Id(ObjO2), 5, true), Assert.Single(added.Change!.Attach));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(56)]
    [InlineData(99)]
    public void An_attachment_point_outside_the_avatars_points_is_refused(int point)
    {
        var r = new Rig();
        Assert.Equal("outfit.bad_point", OutfitPlanner.Attach(r.Model, Id(ObjO1), point, false).Refusal);
    }

    [Fact]
    public void Only_objects_can_be_attached_and_not_ones_already_worn()
    {
        var r = new Rig();
        Assert.Equal("outfit.not_wearable", OutfitPlanner.Attach(r.Model, Id(ShirtA), 0, false).Refusal);
        r.SetCof(LinkTo(1, ObjO1, "hat"));
        Assert.Equal("outfit.already_worn", OutfitPlanner.Attach(r.Model, Id(ObjO1), 0, false).Refusal);
    }

    // ---------------- wear an outfit ----------------

    [Fact]
    public void Wearing_an_outfit_replaces_clothing_and_attachments_swaps_body_parts_it_names_and_keeps_the_others()
    {
        var r = new Rig();
        var oldBase = new InventoryItemData(Id(1900), CofF, "Work", ItemKind.LinkFolder, 25, 8, 0, null, OutfitF2);
        r.SetCof(LinkTo(1, ShirtB, "shirt b", "@400"), LinkTo(2, ShapeS1, "shape one"), LinkTo(3, EyesE1, "eyes one"), LinkTo(4, ObjO1, "hat"), oldBase);
        r.Put(OutfitF, Rig.InOutfit(OutfitF, 1, ShirtA, "shirt a", "@400"), Rig.InOutfit(OutfitF, 2, PantsC, "pants c", "@500"),
            Rig.InOutfit(OutfitF, 3, ShapeS2, "shape two"), Rig.InOutfit(OutfitF, 4, ObjO2, "wand"));
        var p = OutfitPlanner.WearOutfit(r.Model, OutfitF);
        Assert.True(p.Ok, p.Refusal);
        var c = p.Change!;
        Assert.Equal(new[] { Id(1001), Id(1002), Id(1004), Id(1900) }.OrderBy(x => x), c.RemoveLinks.OrderBy(x => x));   // eyes stay
        var made = c.Create.Select(l => (l.TargetId, l.Description, l.IsFolder)).ToHashSet();
        Assert.Contains((Id(ShirtA), "@400", false), made);
        Assert.Contains((Id(PantsC), "@500", false), made);
        Assert.Contains((Id(ShapeS2), "", false), made);
        Assert.Contains((Id(ObjO2), "", false), made);
        Assert.Contains((OutfitF, "", true), made);   // the link that says which outfit this look came from
        Assert.Equal(5, c.Create.Count);
        Assert.Equal(new[] { Id(ObjO1) }, c.Detach.ToArray());
        Assert.Equal(new AttachRequest(Id(ObjO2), 0, false), Assert.Single(c.Attach));
    }

    [Fact]
    public void Wearing_an_outfit_does_not_remove_and_remake_what_both_already_have()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ShirtA, "shirt a", "@400"), LinkTo(4, ObjO1, "hat"));
        r.Put(OutfitF, Rig.InOutfit(OutfitF, 1, ShirtA, "shirt a", "@400"), Rig.InOutfit(OutfitF, 4, ObjO1, "hat"), Rig.InOutfit(OutfitF, 5, PantsC, "pants c", "@500"));
        var c = OutfitPlanner.WearOutfit(r.Model, OutfitF).Change!;
        Assert.Empty(c.RemoveLinks);
        Assert.Empty(c.Detach);
        Assert.Empty(c.Attach);
        Assert.Equal(new[] { Id(PantsC), OutfitF }.OrderBy(x => x), c.Create.Select(l => l.TargetId).OrderBy(x => x));
    }

    [Fact]
    public void An_outfit_with_two_body_parts_of_one_type_gives_one_and_clothing_is_capped_at_the_layer_limit()
    {
        var r = new Rig();
        var links = new List<InventoryItemData> { Rig.InOutfit(OutfitF, 1, ShapeS1, "shape one"), Rig.InOutfit(OutfitF, 2, ShapeS2, "shape two") };
        var extra = new List<InventoryItemData>();
        for (int i = 0; i < OutfitPlanner.MaxClothingLayers + 5; i++)
        {
            extra.Add(Thing(300 + i, ClothF, "jacket " + i, ItemKind.Clothing, WearableType.Jacket));
            links.Add(Rig.InOutfit(OutfitF, 100 + i, 300 + i, "jacket " + i));
        }
        r.Put(ClothF, extra.ToArray());
        r.Put(OutfitF, links.ToArray());
        var c = OutfitPlanner.WearOutfit(r.Model, OutfitF).Change!;
        Assert.Single(c.Create, l => l.TargetId == Id(ShapeS1) || l.TargetId == Id(ShapeS2));
        Assert.Equal(OutfitPlanner.MaxClothingLayers, c.Create.Count(l => l.Description.StartsWith("@8")));
    }

    [Fact]
    public void An_empty_outfit_a_system_folder_and_an_unloaded_outfit_are_refused()
    {
        var r = new Rig();
        Assert.Equal("outfit.empty", OutfitPlanner.WearOutfit(r.Model, OutfitF).Refusal);
        Assert.Equal("outfit.no_outfit", OutfitPlanner.WearOutfit(r.Model, ClothF).Refusal);
        Assert.Equal("outfit.no_outfit", OutfitPlanner.WearOutfit(r.Model, Guid.NewGuid()).Refusal);
        var fresh = new Rig(cofLoaded: false);
        fresh.Model.Apply(new InventoryFolderFetched(OutfitF, false, -1, -1, Array.Empty<InventoryFolderData>(), Array.Empty<InventoryItemData>()));
        Assert.Equal("outfit.not_loaded", OutfitPlanner.WearOutfit(fresh.Model, OutfitF).Refusal);
    }

    // ---------------- save ----------------

    [Fact]
    public void Saving_the_current_look_needs_something_worn_and_cleans_the_name_with_a_default()
    {
        var r = new Rig();
        Assert.Equal("outfit.empty", OutfitPlanner.SaveOutfit(r.Model, "Mine").Refusal);
        r.SetCof(LinkTo(1, ShirtA, "shirt a", "@400"));
        var (plan, refusal) = OutfitPlanner.SaveOutfit(r.Model, "  Evening | wear ");
        Assert.Null(refusal);
        Assert.Equal(new OutfitSavePlan(CofF, MyOutfitsF, "Evening   wear"), plan);
        Assert.Equal("New Outfit", OutfitPlanner.SaveOutfit(r.Model, "   ").Plan!.Name);
        Assert.Equal("outfit.bad_name", OutfitPlanner.SaveOutfit(r.Model, new string('x', 64)).Refusal);
    }

    [Fact]
    public void An_account_with_no_my_outfits_folder_gets_one_made_under_the_root_when_it_saves()
    {
        var model = new InventoryModel((_, _) => { }, 64);
        model.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot,
            new[] { F(Root, Guid.Empty, "", FolderKind.Root), F(CofF, Root, "Current Outfit", FolderKind.CurrentOutfit) },
            new[] { F(LibRoot, Guid.Empty, "", FolderKind.Root) }));
        model.Apply(new InventoryFolderFetched(CofF, true, 2, 1, Array.Empty<InventoryFolderData>(), new[] { LinkTo(1, ShirtA, "shirt a", "@400") }));
        var (plan, refusal) = OutfitPlanner.SaveOutfit(model, "Mine");
        Assert.Null(refusal);
        Assert.Equal(new OutfitSavePlan(CofF, Guid.Empty, "Mine", Root), plan);
    }

    // ---------------- the current-outfit view ----------------

    [Fact]
    public void The_current_view_groups_attachments_then_body_parts_then_clothing_by_type_with_the_highest_layer_first()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ShirtA, "shirt a", "@400"), LinkTo(2, ShirtB, "shirt b", "@401"), LinkTo(3, PantsC, "pants c", "@500"),
            LinkTo(4, ShapeS1, "shape one"), LinkTo(5, EyesE1, "eyes one"), LinkTo(6, ObjO2, "wand"), LinkTo(7, ObjO1, "hat"),
            new InventoryItemData(Id(1900), CofF, "Party", ItemKind.LinkFolder, 25, 8, 0, null, OutfitF));
        var e = OutfitPlanner.Current(r.Model);
        Assert.Equal(new[] { "hat", "wand", "eyes one", "shape one", "shirt b", "shirt a", "pants c" }, e.Select(x => x.Name).ToArray());
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2, 2 }, e.Select(x => x.Section).ToArray());
        Assert.All(e.Where(x => x.Section == 1), x => Assert.False(x.CanTakeOff));
        Assert.All(e.Where(x => x.Section != 1), x => Assert.True(x.CanTakeOff));
        Assert.Equal(WearableType.Shirt, e.First(x => x.Name == "shirt b").Type);
        Assert.Equal(1, e.First(x => x.Name == "shirt b").Layer);
    }

    [Fact]
    public void A_link_whose_item_is_not_loaded_is_still_listed_by_the_type_its_description_names()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, 4242, "far jacket", "@801"));
        var e = Assert.Single(OutfitPlanner.Current(r.Model));
        Assert.Equal(("far jacket", 2, WearableType.Jacket, 1), (e.Name, e.Section, e.Type, e.Layer));
    }

    // ---------------- intents and the pipeline ----------------

    private static (ActionPipeline Pipeline, FakeTransport Transport) Pipe()
    {
        var t = new FakeTransport();
        return (new ActionPipeline(() => t, () => true, new CoreLog(new ListSink(), "test")), t);
    }

    [Fact]
    public void A_good_plan_becomes_one_command_carrying_that_plan()
    {
        var r = new Rig();
        var plan = OutfitPlanner.Wear(r.Model, Id(ShirtA), false).Change!;
        var (p, t) = Pipe();
        Assert.Equal(ActionStatus.Sent, p.Submit(new OutfitChangeIntent(plan)).Status);
        var cmd = Assert.IsType<ChangeOutfitCommand>(Assert.Single(t.Commands));
        Assert.Same(plan, cmd.Change);
        Assert.Equal(ActionStatus.Sent, p.Submit(new OutfitSaveIntent(CofF, MyOutfitsF, " Evening ")).Status);
        Assert.Equal(new SaveOutfitCommand(CofF, MyOutfitsF, "Evening"), t.Commands[1]);
    }

    public static TheoryData<ActionIntent, string> Bad => new()
    {
        { new OutfitChangeIntent(new OutfitChange(Guid.Empty, new[] { new NewOutfitLink(Id(1), "x", "") }, [], [], [])), "outfit.no_cof" },
        { new OutfitChangeIntent(new OutfitChange(CofF, [], [], [], [])), "outfit.nothing" },
        { new OutfitChangeIntent(new OutfitChange(CofF, [], [], new[] { new AttachRequest(Id(1), 56, false) }, [])), "outfit.bad_point" },
        { new OutfitChangeIntent(new OutfitChange(CofF, [], [], new[] { new AttachRequest(Guid.Empty, 1, false) }, [])), "outfit.no_item" },
        { new OutfitChangeIntent(new OutfitChange(CofF, new[] { new NewOutfitLink(Guid.Empty, "x", "") }, [], [], [])), "outfit.no_item" },
        { new OutfitChangeIntent(new OutfitChange(CofF, [], new[] { Guid.Empty }, [], [])), "outfit.no_item" },
        { new OutfitChangeIntent(new OutfitChange(CofF, Enumerable.Range(1, 126).Select(i => new NewOutfitLink(Id(i), "x", "")).ToArray(), [], [], [])), "outfit.too_many" },
        { new OutfitSaveIntent(Guid.Empty, MyOutfitsF, "x"), "outfit.no_cof" },
        { new OutfitSaveIntent(CofF, Guid.Empty, "x"), "outfit.no_folder" },
        { new OutfitSaveIntent(CofF, Guid.Empty, "x", Guid.Empty), "outfit.no_folder" },
        { new OutfitSaveIntent(CofF, MyOutfitsF, new string('n', 64)), "outfit.bad_name" },
    };

    [Theory]
    [MemberData(nameof(Bad))]
    public void A_bad_outfit_intent_is_refused_before_anything_is_sent(ActionIntent intent, string code)
    {
        var (p, t) = Pipe();
        var result = p.Submit(intent);
        Assert.Equal(ActionStatus.Invalid, result.Status);
        Assert.Contains(result.Issues!, i => i.Code == code);
        Assert.Empty(t.Commands);
    }
}
