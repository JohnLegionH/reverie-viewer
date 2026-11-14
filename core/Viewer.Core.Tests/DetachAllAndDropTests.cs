// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Interaction;
using Viewer.Core.Inventory;
using Viewer.Core.World;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Outfit;
using Xunit;
using static Viewer.Core.Tests.OutfitPlannerTests;

namespace Viewer.Core.Tests;

/// <summary>
/// Detach All and Drop on the worn-object menus. The official viewer's own-avatar menu has Detach All (menu_avatar_self.xml, which runs
/// LLAppearanceMgr::removeAllAttachmentsFromAvatar: every worn object's outfit link goes and each is detached); an attachment's menu has
/// Drop (menu_attachment_self.xml, Attachment.Drop), which asks first (the AttachmentDrop notification) and then sends ObjectDrop for the
/// object's root (llselectmgr.cpp, LLSelectMgr::sendDropAttachment). No engine and no grid; each test builds its own state, so the class runs in parallel.
/// </summary>
public class DetachAllAndDropTests
{
    private static readonly Guid Item = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a1");
    private static readonly PrimInteraction Mine = new(0, ObjectFlagBits.YouOwner | ObjectFlagBits.Copy);
    private static readonly WornEntry Hat = new(Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000d2"), "Example Hat");

    [Fact]
    public void Detach_all_removes_every_worn_objects_link_and_detaches_each_and_leaves_clothing_alone()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ObjO1, "hat"), LinkTo(2, ShirtA, "shirt a", "@400"));
        var worn = new HashSet<Guid> { Id(ObjO1), Id(ObjO2) };   // the second object is worn without a link (put on from the ground)
        var plan = OutfitPlanner.DetachAll(r.Model, worn);
        Assert.True(plan.Ok, plan.Refusal);
        Assert.Equal([Id(1001)], plan.Change!.RemoveLinks.ToArray());
        Assert.Equal(new[] { Id(ObjO1), Id(ObjO2) }.OrderBy(g => g), plan.Change.Detach.OrderBy(g => g));
        Assert.Empty(plan.Change.Create);
    }

    [Fact]
    public void Detach_all_with_nothing_worn_is_refused()
    {
        var r = new Rig();
        Assert.Equal("outfit.not_worn", OutfitPlanner.DetachAll(r.Model, new HashSet<Guid>()).Refusal);
    }

    [Fact]
    public void The_own_menu_offers_detach_all_only_while_something_is_worn()
    {
        var with = ContextMenus.ForSelf(new SelfMenuContext(false, false, [], [Hat]));
        Assert.True(Assert.Single(with, e => e.Id == "detach_all").Enabled);
        Assert.Equal("menu.detach_all", Assert.Single(with, e => e.Id == "detach_all").LabelKey);
        Assert.False(Assert.Single(ContextMenus.ForSelf(new SelfMenuContext(false, false, [], [])), e => e.Id == "detach_all").Enabled);
    }

    [Fact]
    public void A_locked_object_is_greyed_in_the_own_menus_detach_and_take_off_entries()
    {
        var locked = new WornEntry(Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000d3"), "Example Collar", IsObject: true);
        var menu = ContextMenus.ForSelf(new SelfMenuContext(false, false, [locked], [Hat, locked], CanDetach: id => id != locked.Id));
        var takeOff = Assert.Single(menu, e => e.Id == "take_off");
        var detach = Assert.Single(takeOff.Children!, e => e.Id == "detach");
        Assert.False(Assert.Single(detach.Children!, e => e.Id == $"detach/{locked.Id}").Enabled);
        Assert.True(Assert.Single(detach.Children!, e => e.Id == $"detach/{Hat.Id}").Enabled);
        Assert.False(Assert.Single(takeOff.Children!, e => e.Id == $"take_off/{locked.Id}").Enabled);
        Assert.True(Assert.Single(menu, e => e.Id == "detach_all").Enabled);   // the hat can still go
        var onlyLocked = ContextMenus.ForSelf(new SelfMenuContext(false, false, [], [locked], CanDetach: id => id != locked.Id));
        Assert.False(Assert.Single(onlyLocked, e => e.Id == "detach_all").Enabled);
    }

    [Fact]
    public void A_locked_object_cannot_be_detached_and_detach_all_leaves_it_on()
    {
        var r = new Rig();
        r.SetCof(LinkTo(1, ObjO1, "hat"));
        var submitted = new List<ActionIntent>();
        var worn = new HashSet<Guid> { Id(ObjO1), Id(ObjO2) };
        var actions = new OutfitActions(r.Model, i => { submitted.Add(i); return new ActionResult(ActionStatus.Sent, 1); }, () => worn,
            canDetach: id => id != Id(ObjO1));
        Assert.False(actions.CanDetach(Id(ObjO1)));
        Assert.True(actions.CanDetach(Id(ObjO2)));
        Assert.Equal((false, "rlv.detach"), (actions.Detach(Id(ObjO1)).Submitted, actions.Detach(Id(ObjO1)).Code));
        Assert.True(actions.DetachAll().Submitted);
        var change = Assert.IsType<OutfitChangeIntent>(Assert.Single(submitted)).Change;
        Assert.Equal([Id(ObjO2)], change.Detach.ToArray());
        Assert.Empty(change.RemoveLinks);   // the locked object's link stays
    }

    [Fact]
    public void Choosing_detach_all_runs_the_hook_once()
    {
        var rig = new WireRig();
        int calls = 0;
        rig.C.SelfHooks = new SelfHooks(() => { }, () => { }, () => [], _ => { }, () => false, () => { }, Attachments: () => [Hat], DetachAll: () => calls++);
        rig.C.RightClick(rig.Down(100, 100, ignoreSelf: false));
        Assert.True(rig.C.Choose("detach_all"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_worn_objects_menu_offers_drop_that_asks_before_it_sends_anything()
    {
        var entries = ContextMenus.ForObject(new ObjectMenuContext(Mine, Mine, Attachment: true, false, false, CanDetach: true));
        var drop = Assert.Single(entries, e => e.Id == "drop");
        Assert.True(drop.Enabled);
        Assert.Equal([ "drop/confirm" ], drop.Children!.Select(c => c.Id).ToArray());
        Assert.False(Assert.Single(ContextMenus.ForObject(new ObjectMenuContext(Mine, Mine, Attachment: true, false, false)), e => e.Id == "drop").Enabled);
        Assert.DoesNotContain(ContextMenus.ForObject(new ObjectMenuContext(Mine, Mine, false, false, false)), e => e.Id == "drop");
    }

    [Fact]
    public void Confirming_a_drop_submits_one_drop_for_the_objects_root_and_the_first_step_sends_nothing()
    {
        var rig = new WireRig();
        rig.C.SelfHooks = new SelfHooks(() => { }, () => { }, () => [], _ => { }, () => false, () => { }, Detach: _ => { });
        rig.Worn(10, 6, Item, i: Mine);
        rig.Prim(11, new Vector3(0, 0, 1.5f), Mine, parent: 10);
        rig.C.RightClick(rig.DownAtWorn());
        rig.Sent.Clear();
        Assert.False(rig.C.Choose("drop"), "the entry itself only opens the question");
        Assert.DoesNotContain(rig.Sent, i => i is DropAttachmentIntent);
        Assert.True(rig.C.Choose("drop/confirm"));
        Assert.Equal(new DropAttachmentIntent(10), Assert.Single(rig.Sent.OfType<DropAttachmentIntent>()));
    }

    [Fact]
    public void The_drop_intent_needs_an_object_and_becomes_the_drop_command()
    {
        Assert.Contains(new DropAttachmentIntent(0).Validate(), i => i.Code == "drop.no_object");
        Assert.Empty(new DropAttachmentIntent(7).Validate());
        Assert.Equal(new DropAttachmentCommand(7), new DropAttachmentIntent(7).ToCommand());
        Assert.True(LmvTransport.IsKnownCommand(new DropAttachmentCommand(7)));
    }

    [Fact]
    public void The_object_drop_message_carries_the_agent_the_session_and_the_root_local_id()
    {
        var agent = new LibreMetaverse.UUID(Guid.Parse("11111111-2222-4333-8444-555555555555"));
        var session = new LibreMetaverse.UUID(Guid.Parse("66666666-7777-4888-8999-aaaaaaaaaaaa"));
        var packet = LmvTransport.BuildObjectDrop(agent, session, 4242);
        Assert.Equal(agent, packet.AgentData.AgentID);
        Assert.Equal(session, packet.AgentData.SessionID);
        Assert.Equal(4242u, Assert.Single(packet.ObjectData).ObjectLocalID);
    }

    [Fact]
    public void A_drop_from_the_pipeline_reaches_the_transport_as_the_drop_command()
    {
        var transport = new FakeTransport();
        var pipeline = new ActionPipeline(() => transport, () => true, new Viewer.Core.Diagnostics.CoreLog(new ListSink(), "test"));
        Assert.Equal(ActionStatus.Sent, pipeline.Submit(new DropAttachmentIntent(77)).Status);
        Assert.Equal(new DropAttachmentCommand(77), transport.Commands[^1]);
    }
}
