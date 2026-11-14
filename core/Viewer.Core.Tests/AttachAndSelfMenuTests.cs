// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Interaction;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// "Attach To" on an object lying in the world and the menu of the own avatar. Both are menu data plus the controller's choice, run on an
/// in-memory world. Engine-free; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class AttachAndSelfMenuTests
{
    private static readonly PrimInteraction Mine = new(0, ObjectFlagBits.YouOwner | ObjectFlagBits.Copy);
    private static readonly PrimInteraction NotMine = new(0, ObjectFlagBits.Copy);

    private static ContextEntry Find(IReadOnlyList<ContextEntry> entries, string id) => Assert.Single(entries, e => e.Id == id);

    // ---------------- attach to ----------------

    [Fact]
    public void The_object_menu_has_attach_to_with_the_body_points_and_attach_hud_with_the_screen_points()
    {
        var entries = ContextMenus.ForObject(new ObjectMenuContext(Mine, Mine, false, false, false));
        var attach = Find(entries, "attach");
        Assert.True(attach.Enabled);
        Assert.NotNull(attach.Children);
        Assert.Contains(attach.Children!, c => c.Id == "attach/6" && c.LabelKey == "outfit.point.6");   // right hand
        Assert.DoesNotContain(attach.Children!, c => c.Id == "attach/35");   // a screen point is not on the body
        Assert.Equal(AttachmentMenu.BodyPoints.Count, attach.Children!.Count);
        var hud = Find(entries, "attach_hud");
        Assert.Equal(AttachmentMenu.HudPoints.Select(p => $"attach/{p}"), hud.Children!.Select(c => c.Id));
        Assert.Equal(8, hud.Children!.Count);
    }

    [Fact]
    public void Attach_to_is_for_the_owner_only_and_not_for_something_worn_or_sat_on()
    {
        Assert.False(Find(ContextMenus.ForObject(new ObjectMenuContext(NotMine, NotMine, false, false, false)), "attach").Enabled);
        Assert.False(Find(ContextMenus.ForObject(new ObjectMenuContext(Mine, Mine, false, SittingOnIt: true, Sitting: true)), "attach").Enabled);
        Assert.DoesNotContain(ContextMenus.ForObject(new ObjectMenuContext(Mine, Mine, Attachment: true, false, false)), e => e.Id is "attach" or "attach_hud");
    }

    [Fact]
    public void Choosing_a_point_attaches_the_root_object_there_through_the_pipeline()
    {
        var r = new WireRig();
        r.Prim(1, new Vector3(50, 50, 30), Mine);
        r.Prim(2, new Vector3(3, 0, 0), Mine, parent: 1);
        r.C.RightClick(r.Down(53, 50));
        r.Sent.Clear();
        Assert.True(r.C.Choose("attach/6"));
        Assert.Equal(new AttachWorldObjectIntent(1, 6), Assert.Single(r.Sent));
    }

    [Fact]
    public void An_object_that_is_not_the_users_cannot_be_attached_from_the_menu()
    {
        var r = new WireRig();
        r.Prim(1, new Vector3(50, 50, 30), NotMine);
        r.C.RightClick(r.Down(50, 50));
        r.Sent.Clear();
        Assert.False(r.C.Choose("attach/6"));
        Assert.Empty(r.Sent);
    }

    // ---------------- the own avatar's menu ----------------

    private static readonly WornEntry Shirt = new(Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000d1"), "Example Shirt");
    private static readonly WornEntry Hat = new(Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000d2"), "Example Hat");

    [Fact]
    public void The_own_avatar_menu_has_appearance_inventory_take_off_and_fly()
    {
        var entries = ContextMenus.ForSelf(new SelfMenuContext(Sitting: false, Flying: false, [Shirt, Hat]));
        Assert.Equal(["appearance", "inventory", "take_off", "detach_all", "fly"], entries.Select(e => e.Id).ToArray());
        var takeOff = Find(entries, "take_off");
        Assert.True(takeOff.Enabled);
        Assert.Equal(["clothes", $"take_off/{Shirt.Id}", $"take_off/{Hat.Id}"], takeOff.Children!.Select(c => c.Id).ToArray());   // the Clothes menu first
        Assert.Equal(["Example Shirt", "Example Hat"], takeOff.Children!.Skip(1).Select(c => c.Literal).ToArray());
    }

    [Fact]
    public void Stand_up_shows_only_while_seated_and_flying_turns_the_fly_entry_into_stop_flying()
    {
        var seated = ContextMenus.ForSelf(new SelfMenuContext(Sitting: true, Flying: false, []));
        Assert.Contains(seated, e => e.Id == "stand");
        Assert.DoesNotContain(seated, e => e.Id is "fly" or "stop_fly");   // no flight from a seat
        var standing = ContextMenus.ForSelf(new SelfMenuContext(Sitting: false, Flying: false, []));
        Assert.DoesNotContain(standing, e => e.Id == "stand");
        var flying = ContextMenus.ForSelf(new SelfMenuContext(Sitting: false, Flying: true, []));
        Assert.Contains(flying, e => e.Id == "stop_fly");
        Assert.DoesNotContain(flying, e => e.Id == "fly");
    }

    [Fact]
    public void Take_off_is_disabled_when_nothing_is_worn()
    {
        var entries = ContextMenus.ForSelf(new SelfMenuContext(false, false, []));
        Assert.False(Find(entries, "take_off").Enabled);
    }

    private sealed class SelfRig
    {
        public readonly WireRig Rig = new();
        public readonly List<string> Self = new();
        public bool Flying;

        public SelfRig(IReadOnlyList<WornEntry>? worn = null)
        {
            Rig.C.SelfHooks = new SelfHooks(() => Self.Add("appearance"), () => Self.Add("inventory"), () => worn ?? [Shirt, Hat],
                id => Self.Add("take_off:" + id), () => Flying, () => { Self.Add("fly"); Flying = !Flying; });
        }
    }

    [Fact]
    public void Right_clicking_the_own_avatar_opens_the_own_menu_and_its_entries_run_their_hooks()
    {
        var s = new SelfRig();
        s.Rig.C.RightClick(s.Rig.Down(100, 100, ignoreSelf: false));
        Assert.Equal(MenuKind.Self, s.Rig.C.Menu);
        Assert.Contains(s.Rig.C.Entries, e => e.Id == "appearance");
        Assert.True(s.Rig.C.Choose("appearance"));
        Assert.True(s.Rig.C.Choose("inventory"));
        Assert.True(s.Rig.C.Choose($"take_off/{Hat.Id}"));
        Assert.True(s.Rig.C.Choose("fly"));
        Assert.Equal(["appearance", "inventory", $"take_off:{Hat.Id}", "fly"], s.Self);
    }

    [Fact]
    public void Stand_up_from_the_own_menu_sends_the_stand_intent()
    {
        var s = new SelfRig();
        s.Rig.World.ApplyAvatar(WireRig.Sim, new Viewer.Core.World.AvatarData(500, WireRig.Me, "Me", new Vector3(0, 0, 0), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), ParentLocalId: 1));
        s.Rig.Prim(1, new Vector3(100, 100, 20));
        s.Rig.C.RightClick(new PickResult(PickKind.Avatar, null, s.Rig.World.FindAvatar(WireRig.Me), default, 0f));
        Assert.Equal(MenuKind.Self, s.Rig.C.Menu);
        s.Rig.Sent.Clear();
        Assert.True(s.Rig.C.Choose("stand"));
        Assert.IsType<StandIntent>(Assert.Single(s.Rig.Sent));
    }

    [Fact]
    public void Without_hooks_the_own_avatar_has_no_menu_as_before()
    {
        var r = new WireRig();
        r.C.RightClick(r.Down(100, 100, ignoreSelf: false));
        Assert.Equal(MenuKind.None, r.C.Menu);
        Assert.Empty(r.C.Entries);
    }
}
