// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Notifications;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Block, wired from the places the official viewer offers it: the avatar and object menus, the script dialog and the permission
/// request, and the block list that decides which prompts are dropped. Engine-free; every member builds its own objects, so the class
/// runs in parallel.
/// </summary>
public class BlockWiringTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid ObjA = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a1");
    private static readonly Guid Owner = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000b2");
    private static readonly Guid Item = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000c3");

    // ---------------- the menus ----------------

    [Fact]
    public void The_avatar_menu_offers_block_and_after_a_block_unblock()
    {
        var plain = ContextMenus.ForAvatar(new AvatarMenuContext(IsSelf: false, IsFriend: false));
        Assert.Contains(plain, e => e.Id == "block" && e.Enabled);
        Assert.DoesNotContain(plain, e => e.Id == "unblock");
        var blocked = ContextMenus.ForAvatar(new AvatarMenuContext(IsSelf: false, IsFriend: false, IsBlocked: true));
        Assert.Contains(blocked, e => e.Id == "unblock" && e.Enabled);
        Assert.DoesNotContain(blocked, e => e.Id == "block");
    }

    [Fact]
    public void The_object_menu_offers_block_and_after_a_block_unblock()
    {
        var item = new PrimInteraction(0, ObjectFlagBits.YouOwner);
        var plain = ContextMenus.ForObject(new ObjectMenuContext(item, item, false, false, false));
        Assert.Contains(plain, e => e.Id == "block" && e.Enabled);
        var blocked = ContextMenus.ForObject(new ObjectMenuContext(item, item, false, false, false, IsBlocked: true));
        Assert.Contains(blocked, e => e.Id == "unblock");
        Assert.DoesNotContain(blocked, e => e.Id == "block");
    }

    [Fact]
    public void Choosing_block_on_a_person_blocks_that_resident_and_the_menu_then_offers_unblock()
    {
        var r = new WireRig();
        r.C.RightClick(r.Down(120, 100));
        Assert.True(r.C.Choose("block"));
        Assert.Equal([$"block:Resident:{WireRig.Other}"], r.Calls);
        r.C.RightClick(r.Down(120, 100));
        Assert.Contains(r.C.Entries, e => e.Id == "unblock");
        Assert.True(r.C.Choose("unblock"));
        Assert.Equal($"unblock:Resident:{WireRig.Other}", r.Calls[^1]);
        Assert.Empty(r.Sent);   // the hooks run the block through the pipeline; the controller sends nothing of its own
    }

    [Fact]
    public void Choosing_block_on_an_object_blocks_the_whole_object_by_its_root_id()
    {
        var r = new WireRig();
        r.Prim(1, new Vector3(50, 50, 30));
        r.Prim(2, new Vector3(3, 0, 0), parent: 1);
        r.C.RightClick(r.Down(53, 50));
        Assert.True(r.C.Choose("block"));
        Assert.Equal([$"block:Object:{WireRig.G(1)}"], r.Calls);
    }

    // ---------------- prompts ----------------

    private sealed class Rig
    {
        public readonly NotificationModel Model;
        public readonly ScriptPrompts Prompts;
        public readonly List<ActionIntent> Sent = new();
        public readonly List<(Guid Id, string Name)> BlockedObjects = new();

        public Rig()
        {
            Model = new NotificationModel(() => 1000);
            Prompts = new ScriptPrompts(Model, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, () => 1000, () => "Test User")
            {
                BlockObject = (id, name) => BlockedObjects.Add((id, name)),
            };
        }

        public ScriptDialogReceived Dialog(Guid obj, Guid owner) =>
            new(Here, obj, owner, "Vendor", "Some Owner", false, new MessageBody("Pick one", MessageChannel.LocalChat), -5, ["Yes", "No"]);
    }

    [Fact]
    public void A_script_dialog_has_a_block_button_that_blocks_its_object_and_sends_no_reply()
    {
        var r = new Rig();
        r.Prompts.Receive(r.Dialog(ObjA, Owner));
        var n = Assert.Single(r.Model.List);
        Assert.True(n.CanBlock);
        Assert.True(r.Model.Answer(n.Id, NotificationAnswer.BlockIndex));
        Assert.Equal([(ObjA, "Vendor")], r.BlockedObjects);
        Assert.Empty(r.Sent);
        Assert.Empty(r.Model.List);
    }

    [Fact]
    public void A_permission_request_has_a_block_button_that_blocks_its_object_and_gives_no_permission()
    {
        var r = new Rig();
        r.Prompts.Receive(new ScriptPermissionRequested(Here, ObjA, Item, "Vendor", "Some Owner", 4 | 16));
        var n = Assert.Single(r.Model.List);
        Assert.True(n.CanBlock);
        Assert.True(r.Model.Answer(n.Id, NotificationAnswer.BlockIndex));
        Assert.Equal([(ObjA, "Vendor")], r.BlockedObjects);
        var answer = Assert.IsType<ScriptPermissionAnswerIntent>(Assert.Single(r.Sent));   // the script is told no, so it does not wait for ever
        Assert.Equal(0, answer.Granted);
    }

    [Fact]
    public void A_notification_that_is_not_a_prompt_has_no_block_button()
    {
        var r = new Rig();
        r.Prompts.Receive(new LoadUrlRequested(Here, ObjA, Owner, false, "Vendor", new MessageBody("Visit", MessageChannel.LocalChat), "https://example.org/"));
        Assert.False(Assert.Single(r.Model.List).CanBlock);
    }

    [Fact]
    public void The_block_list_decides_which_prompts_are_dropped_by_object_and_by_owner()
    {
        var r = new Rig();
        var list = new MuteList(WireRig.Me);
        r.Prompts.UseMuteList(list);
        r.Prompts.Receive(r.Dialog(ObjA, Owner));
        Assert.Single(r.Model.List);   // nothing blocked: shown
        r.Model.Remove(r.Model.List[0].Id);

        list.Add(new MuteEntry(MuteKind.Object, ObjA, "Vendor", 0));
        r.Prompts.Receive(r.Dialog(ObjA, Owner));
        Assert.Empty(r.Model.List);
        Assert.Equal(1, r.Prompts.Muted);

        var other = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a9");
        list.Add(new MuteEntry(MuteKind.Resident, Owner, "Some Owner", 0));
        r.Prompts.Receive(r.Dialog(other, Owner));   // another object, blocked owner
        Assert.Empty(r.Model.List);
        Assert.Equal(2, r.Prompts.Muted);

        r.Prompts.Receive(r.Dialog(other, Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000b9")));   // neither is blocked
        Assert.Single(r.Model.List);
    }
}
