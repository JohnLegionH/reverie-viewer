// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Interaction;
using Viewer.Core.Inventory;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Outfit;
using Xunit;
using static Viewer.Core.Tests.OutfitPlannerTests;

namespace Viewer.Core.Tests;

/// <summary>
/// Taking a worn object off from the viewer's own menus: the item id a worn object carries, finding the own worn objects under the mouse,
/// the object's menu, the own avatar's Take Off menu, the plan and the action, and the command that reaches the transport.
/// Engine-free; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class DetachTests
{
    private static readonly Guid Item = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a1");
    private static readonly PrimInteraction Mine = new(0, ObjectFlagBits.YouOwner | ObjectFlagBits.Copy);
    private static readonly WornEntry Hat = new(Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000d2"), "Example Hat");
    private static readonly WornEntry Shirt = new(Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000d1"), "Example Shirt");

    // ---------------- the item a worn object came from ----------------

    [Fact]
    public void A_worn_object_update_carries_the_item_it_was_taken_from()
    {
        var p = new LibreMetaverse.Primitive { LocalID = 7, ID = new LibreMetaverse.UUID(WireRig.G(7)), NameValues = [new LibreMetaverse.NameValue($"AttachItemID STRING RW SV {Item}")] };
        Assert.Equal(Item, LmvTransport.ToPrimData(p).AttachItemId);
        Assert.Equal(Guid.Empty, LmvTransport.ToPrimData(new LibreMetaverse.Primitive { LocalID = 8, ID = new LibreMetaverse.UUID(WireRig.G(8)) }).AttachItemId);
    }

    [Fact]
    public void The_world_keeps_the_item_id_of_a_worn_root_object_and_has_none_for_a_plain_prim()
    {
        var r = new WireRig();
        r.Worn(10, 6, Item);
        r.Prim(11, new Vector3(5, 5, 5));
        var worn = r.World.Object(new ObjectId(r.World.Epoch, 10));
        Assert.Equal(6, worn?.AttachmentPoint);
        Assert.Equal(Item, worn?.AttachItemId);
        Assert.Null(r.World.Object(new ObjectId(r.World.Epoch, 11))?.AttachItemId);
    }

    // ---------------- finding the own worn objects under the mouse ----------------

    [Fact]
    public void The_picker_finds_a_worn_object_through_its_attachment_point_and_its_children_report_the_root()
    {
        var r = new WireRig();
        r.Worn(10, 6, Item);
        r.Prim(11, new Vector3(0, 0, 1.5f), Mine, parent: 10);
        var pick = r.DownAtWorn();
        Assert.Equal(PickKind.Object, pick.Kind);
        Assert.Equal(11u, pick.Hit!.LocalId);
        Assert.Equal(10u, pick.Hit.RootLocalId);
    }

    [Fact]
    public void Without_the_attachment_frame_a_worn_object_is_not_found_as_before()
    {
        var r = new WireRig();
        r.Worn(10, 6, Item, inPoint: new Vector3(50, 50, 30));   // where a frame that is not asked for would put it, were it taken as the region's origin
        var pick = ObjectPicker.Pick(r.World, new PickRay(new Vector3(50, 50, 60), new Vector3(0, 0, -1)), (_, _) => 20f, ignoreAgent: WireRig.Me);
        Assert.NotEqual(PickKind.Object, pick.Kind);
    }

    [Fact]
    public void An_object_worn_by_someone_else_is_not_offered_by_the_frame_the_host_gives_for_the_own_avatar()
    {
        var r = new WireRig();
        r.Worn(10, 6, Item, parent: 501);   // the other avatar's
        Func<ObjectState, Matrix4x4?> frameForOwnOnly = o => o.WearerAgentId == WireRig.Me ? Matrix4x4.CreateTranslation(50, 50, 30) : null;
        var ray = new PickRay(new Vector3(50, 50, 60), new Vector3(0, 0, -1));
        Assert.NotEqual(PickKind.Object, ObjectPicker.Pick(r.World, ray, (_, _) => 20f, ignoreAgent: WireRig.Me, attachmentFrame: frameForOwnOnly).Kind);
        r.Worn(12, 5, Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a2"));   // the own avatar's, found at the same spot
        Assert.Equal(12u, ObjectPicker.Pick(r.World, ray, (_, _) => 20f, ignoreAgent: WireRig.Me, attachmentFrame: frameForOwnOnly).Hit?.LocalId);
    }

    // ---------------- the menus ----------------

    [Fact]
    public void The_menu_of_a_worn_object_has_touch_inspect_edit_and_detach_and_nothing_for_a_world_object()
    {
        var entries = ContextMenus.ForObject(new ObjectMenuContext(Mine, Mine, Attachment: true, false, false, CanDetach: true));
        Assert.Equal(["touch", "inspect", "edit", "detach", "drop"], entries.Select(e => e.Id).ToArray());
        var detach = Assert.Single(entries, e => e.Id == "detach");
        Assert.Equal("menu.detach", detach.LabelKey);
        Assert.True(detach.Enabled);
        Assert.False(Assert.Single(ContextMenus.ForObject(new ObjectMenuContext(Mine, Mine, Attachment: true, false, false)), e => e.Id == "detach").Enabled);
        Assert.DoesNotContain(ContextMenus.ForObject(new ObjectMenuContext(Mine, Mine, false, false, false)), e => e.Id == "detach");
    }

    [Fact]
    public void The_take_off_menu_lists_the_worn_objects_under_detach_when_the_outfit_does_not_list_them()
    {
        var entries = ContextMenus.ForSelf(new SelfMenuContext(false, false, [Shirt], [Hat]));
        var takeOff = Assert.Single(entries, e => e.Id == "take_off");
        Assert.True(takeOff.Enabled);
        Assert.Equal(["clothes", $"take_off/{Shirt.Id}", "detach"], takeOff.Children!.Select(c => c.Id).ToArray());   // the Clothes menu first
        var detach = takeOff.Children![2];
        Assert.Equal("menu.detach", detach.LabelKey);
        Assert.Equal([$"detach/{Hat.Id}"], detach.Children!.Select(c => c.Id).ToArray());
        Assert.Equal(["Example Hat"], detach.Children!.Select(c => c.Literal).ToArray());
        // with only a worn object, Take Off is still there
        Assert.True(Assert.Single(ContextMenus.ForSelf(new SelfMenuContext(false, false, [], [Hat])), e => e.Id == "take_off").Enabled);
        // and without any, there is no Detach entry
        Assert.DoesNotContain(Assert.Single(ContextMenus.ForSelf(new SelfMenuContext(false, false, [Shirt], [])), e => e.Id == "take_off").Children!, c => c.Id == "detach");
    }

    // ---------------- choosing ----------------

    private sealed class DetachRig
    {
        public readonly WireRig Rig = new();
        public readonly List<Guid> Detached = new();

        public DetachRig()
        {
            Rig.C.SelfHooks = new SelfHooks(() => { }, () => { }, () => [], _ => { }, () => false, () => { },
                Detach: Detached.Add, Attachments: () => [Hat]);
        }
    }

    [Fact]
    public void Right_clicking_a_worn_object_opens_its_menu_and_detach_hands_its_item_to_the_hook_once()
    {
        var d = new DetachRig();
        d.Rig.Worn(10, 6, Item, i: Mine);
        d.Rig.C.RightClick(d.Rig.DownAtWorn());
        Assert.Equal(MenuKind.Object, d.Rig.C.Menu);
        Assert.Equal(["touch", "inspect", "edit", "detach", "drop"], d.Rig.C.Entries.Take(5).Select(e => e.Id).ToArray());   // the own avatar's part follows
        d.Rig.Sent.Clear();
        Assert.True(d.Rig.C.Choose("detach"));
        Assert.Equal([Item], d.Detached);
        Assert.DoesNotContain(d.Rig.Sent, i => i is TakeIntent or AttachWorldObjectIntent);
    }

    [Fact]
    public void A_worn_object_whose_item_is_not_known_cannot_be_detached_from_its_menu()
    {
        var d = new DetachRig();
        d.Rig.Worn(10, 6, Guid.Empty, i: Mine);
        d.Rig.C.RightClick(d.Rig.DownAtWorn());
        Assert.False(Assert.Single(d.Rig.C.Entries, e => e.Id == "detach").Enabled);
        Assert.False(d.Rig.C.Choose("detach"));
        Assert.Empty(d.Detached);
    }

    [Fact]
    public void Detach_from_the_own_avatars_take_off_menu_hands_that_item_to_the_hook()
    {
        var d = new DetachRig();
        d.Rig.C.RightClick(d.Rig.Down(100, 100, ignoreSelf: false));
        Assert.Equal(MenuKind.Self, d.Rig.C.Menu);
        Assert.True(d.Rig.C.Choose($"detach/{Hat.Id}"));
        Assert.Equal([Hat.Id], d.Detached);
    }

    // ---------------- the plan ----------------

    [Fact]
    public void Detaching_an_object_with_a_link_in_the_current_outfit_removes_the_link_and_detaches_it()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ObjO1, "hat"));
        var plan = OutfitPlanner.Detach(r.Model, Id(ObjO1), new HashSet<Guid> { Id(ObjO1) });
        Assert.True(plan.Ok);
        Assert.Equal([Id(1001)], plan.Change!.RemoveLinks.ToArray());
        Assert.Equal([Id(ObjO1)], plan.Change.Detach.ToArray());
    }

    [Fact]
    public void Detaching_an_object_worn_without_a_link_sends_only_the_detach()
    {
        var r = new Rig();
        var plan = OutfitPlanner.Detach(r.Model, Id(ObjO1), new HashSet<Guid> { Id(ObjO1) });
        Assert.True(plan.Ok);
        Assert.Equal(CofF, plan.Change!.CofId);
        Assert.Empty(plan.Change.RemoveLinks);
        Assert.Empty(plan.Change.Create);
        Assert.Equal([Id(ObjO1)], plan.Change.Detach.ToArray());
    }

    [Fact]
    public void Detach_refuses_what_is_not_worn_what_is_not_an_object_and_an_unknown_outfit_folder()
    {
        var r = new Rig();
        Assert.Equal("outfit.not_worn", OutfitPlanner.Detach(r.Model, Id(ObjO1), new HashSet<Guid>()).Refusal);
        r.SetCof(LinkTo(1, ShirtA, "shirt a", "@400"));
        Assert.Equal("outfit.not_wearable", OutfitPlanner.Detach(r.Model, Id(ShirtA), new HashSet<Guid> { Id(ShirtA) }).Refusal);
        var unknown = new Rig(cofLoaded: false);
        Assert.Equal("outfit.cof_unknown", OutfitPlanner.Detach(unknown.Model, Id(ObjO1), new HashSet<Guid> { Id(ObjO1) }).Refusal);
    }

    // ---------------- the action and the command ----------------

    [Fact]
    public void The_action_detaches_a_worn_object_through_the_pipeline_and_marks_it_worn()
    {
        var r = new Rig();
        var submitted = new List<ActionIntent>();
        var attached = new HashSet<Guid>();
        var actions = new OutfitActions(r.Model, i => { submitted.Add(i); return new ActionResult(ActionStatus.Sent, 1); }, () => attached);
        Assert.False(actions.CanDetach(Id(ObjO1)));
        Assert.False(actions.IsWorn(Id(ObjO1)));
        attached.Add(Id(ObjO1));
        Assert.True(actions.CanDetach(Id(ObjO1)));
        Assert.True(actions.IsWorn(Id(ObjO1)));
        Assert.False(actions.CanDetach(Id(ShirtA)));
        var res = actions.Detach(Id(ObjO1));
        Assert.True(res.Submitted);
        var intent = Assert.IsType<OutfitChangeIntent>(Assert.Single(submitted));
        Assert.Equal([Id(ObjO1)], intent.Change.Detach.ToArray());
    }

    [Fact]
    public void A_detach_from_the_menu_reaches_the_transport_as_one_outfit_command_with_the_item_to_detach()
    {
        var model = new Rig();
        var transport = new FakeTransport();
        var pipeline = new ActionPipeline(() => transport, () => true, new Viewer.Core.Diagnostics.CoreLog(new ListSink(), "test"));
        var actions = new OutfitActions(model.Model, pipeline.Submit, () => new HashSet<Guid> { Item });
        var d = new DetachRig();
        d.Rig.Worn(10, 6, Item, i: Mine);
        d.Rig.C.SelfHooks = d.Rig.C.SelfHooks! with { Detach = id => actions.Detach(id) };
        d.Rig.C.RightClick(d.Rig.DownAtWorn());
        Assert.True(d.Rig.C.Choose("detach"));
        var cmd = Assert.IsType<ChangeOutfitCommand>(Assert.Single(transport.Commands));
        Assert.Equal([Item], cmd.Change.Detach.ToArray());
        Assert.Empty(cmd.Change.RemoveLinks);
        Assert.Empty(cmd.Change.Attach);
    }
}
