// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The click behaviour end to end on an in-memory world: what the mouse earns, what a left click sends, what each menu entry runs.
/// No engine and no grid; each test builds its own world, so the class runs in parallel.
/// </summary>
public class InteractionControllerTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly RegionInfo Region = new("Here", 1000, 1000, 256, 256, false);
    private static readonly Guid Me = Guid.Parse("0f3f2d4e-7a41-4c1d-9a55-5b0f2f6a1e11");
    private static readonly Guid Other = Guid.Parse("0f3f2d4e-7a41-4c1d-9a55-5b0f2f6a1e22");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);

    private sealed class Rig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public List<string> Social = new();
        public InteractionController C;
        public bool Friend;
        public List<ObjectId> Opened = new(), Inspected = new();
        public List<Guid> AvatarInspected = new();
        public int AboutLand, AppearanceOpened;

        public Rig()
        {
            World.BeginRegion(Sim, Region);
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            World.ApplyAvatar(Sim, new AvatarData(501, Other, "Other", new Vector3(120, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new InteractionController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, Me,
                new SocialHooks(a => Social.Add("im:" + a), a => Social.Add("friend:" + a), a => Social.Add("offer:" + a), a => Social.Add("request:" + a), () => Social.Add("home")),
                _ => Friend);
            C.OpenRequested += Opened.Add;
            C.InspectRequested += Inspected.Add;
            C.AvatarInspectRequested += AvatarInspected.Add;
            C.AboutLandRequested += () => AboutLand++;
            C.AppearanceRequested += () => AppearanceOpened++;
        }

        public void Prim(uint local, Vector3 pos, PrimInteraction? i = null, uint parent = 0, Quaternion? rot = null, Vector3? scale = null) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), parent, 0, pos, rot ?? Quaternion.Identity, scale ?? new Vector3(2, 2, 2), Interaction: i));

        /// <summary>A pick that can land on the own avatar too.</summary>
        public PickResult DownOnAnyone(float x, float y) =>
            ObjectPicker.Pick(World, new PickRay(new Vector3(x, y, 60), new Vector3(0, 0, -1)), (_, _) => 20f);

        public PickResult Down(float x, float y) =>
            ObjectPicker.Pick(World, new PickRay(new Vector3(x, y, 60), new Vector3(0, 0, -1)), (_, _) => 20f, ignoreAgent: Me);
    }

    private static PrimInteraction Touchy => new(0, ObjectFlagBits.HandleTouch);

    // ---------------- the cursor ----------------

    [Fact]
    public void The_cursor_follows_what_is_under_the_mouse()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Touchy);
        r.Prim(2, new Vector3(30, 10, 30), new PrimInteraction(ClickAction.Sit, 0));
        r.Prim(3, new Vector3(50, 10, 30));
        r.C.Hover(r.Down(10, 10)); Assert.Equal(HoverCursor.Touch, r.C.Cursor);
        r.C.Hover(r.Down(30, 10)); Assert.Equal(HoverCursor.Sit, r.C.Cursor);
        r.C.Hover(r.Down(50, 10)); Assert.Equal(HoverCursor.Arrow, r.C.Cursor);
        r.C.Hover(r.Down(70, 70)); Assert.Equal(HoverCursor.Arrow, r.C.Cursor);
    }

    // ---------------- left click ----------------

    [Fact]
    public void A_left_click_on_a_touchable_object_sends_a_grab_then_a_release_with_the_surface_data()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Touchy);
        var pick = r.Down(10, 10);
        r.C.LeftDown(pick);
        var grab = Assert.IsType<GrabIntent>(Assert.Single(r.Sent));
        Assert.Equal(1u, grab.LocalId);
        Assert.Equal(pick.Hit!.Face, grab.Surface.Face);
        Assert.Equal(pick.Hit.Position, grab.Surface.Position);
        Assert.Equal(pick.Hit.Normal, grab.Surface.Normal);
        Assert.Equal(pick.Hit.Binormal, grab.Surface.Binormal);
        Assert.Equal(pick.Hit.St, grab.Surface.St);
        Assert.Equal(Vector3.Zero, grab.GrabOffset);
        r.C.LeftUp(r.Down(10, 10));
        var release = Assert.IsType<ReleaseIntent>(r.Sent[1]);
        Assert.Equal(1u, release.LocalId);
    }

    [Fact]
    public void A_plain_object_is_touched_too_because_the_default_action_is_touch()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30));
        r.C.LeftDown(r.Down(10, 10));
        Assert.IsType<GrabIntent>(Assert.Single(r.Sent));
    }

    [Fact]
    public void Touching_a_child_prim_grabs_the_child_with_the_roots_offset_in_the_roots_frame()
    {
        var r = new Rig();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        r.Prim(1, new Vector3(50, 50, 30), Touchy, rot: turn);
        r.Prim(2, new Vector3(3, 0, 0), parent: 1);   // 3 m along the root's x, which is the region's +y after the quarter turn
        r.C.LeftDown(r.Down(50, 53));
        var grab = Assert.IsType<GrabIntent>(Assert.Single(r.Sent));
        Assert.Equal(2u, grab.LocalId);
        // root position minus the child's, turned back into the root's frame: minus 3 along the root's x
        Assert.Equal(-3f, grab.GrabOffset.X, 2);
        Assert.Equal(0f, grab.GrabOffset.Y, 2);
    }

    [Fact]
    public void A_left_click_on_a_sit_action_object_sits_unless_already_seated()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Sit, 0));
        r.C.LeftDown(r.Down(10, 10));
        var sit = Assert.IsType<SitIntent>(Assert.Single(r.Sent));
        Assert.Equal(G(1), sit.ObjectId);

        r.World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(0, 0, 0), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), ParentLocalId: 1));
        r.Sent.Clear();
        r.C.LeftDown(r.Down(10, 10));
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void A_left_click_on_an_openable_object_opens_it_and_only_asks_for_its_contents_and_properties()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Open, ObjectFlagBits.YouOwner));
        r.C.LeftDown(r.Down(10, 10));
        Assert.Equal(new ObjectId(r.World.Epoch, 1), Assert.Single(r.Opened));
        Assert.All(r.Sent, i => Assert.True(i is RequestContentsIntent or ObjectPropertiesIntent));
    }

    [Fact]
    public void Actions_this_viewer_does_not_run_are_counted_and_send_nothing()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Zoom, 0));
        r.C.LeftDown(r.Down(10, 10));
        Assert.Empty(r.Sent);
        Assert.Equal(1, r.C.UnhandledClicks);
    }

    [Fact]
    public void A_disabled_click_action_does_nothing()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Disabled, ObjectFlagBits.HandleTouch));
        r.C.LeftDown(r.Down(10, 10));
        Assert.Empty(r.Sent);
        Assert.Equal(0, r.C.UnhandledClicks);
    }

    [Fact]
    public void A_left_click_on_land_or_an_avatar_sends_nothing_and_clears_the_selection()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30));
        r.C.RightClick(r.Down(10, 10));
        Assert.NotNull(r.C.Selected);
        r.Sent.Clear();
        r.C.LeftDown(r.Down(70, 70));
        r.C.LeftUp(r.Down(70, 70));
        Assert.Empty(r.Sent);
        Assert.Null(r.C.Selected);
    }

    [Fact]
    public void The_release_is_only_sent_for_a_grab_that_was_sent()
    {
        var r = new Rig();
        r.C.LeftUp(r.Down(70, 70));
        Assert.Empty(r.Sent);
    }

    // ---------------- right click on an object ----------------

    [Fact]
    public void A_right_click_on_an_object_selects_its_root_asks_for_its_properties_and_opens_the_object_menu()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(50, 50, 30), Touchy);
        r.Prim(2, new Vector3(3, 0, 0), parent: 1);
        r.C.RightClick(r.Down(53, 50));
        Assert.Equal(MenuKind.Object, r.C.Menu);
        Assert.Equal(new ObjectId(r.World.Epoch, 1), r.C.Selected);
        var props = Assert.IsType<ObjectPropertiesIntent>(Assert.Single(r.Sent));
        Assert.Equal(1u, props.LocalId);
        Assert.Contains("touch", r.C.Entries.Select(e => e.Id));
    }

    [Fact]
    public void Touch_from_the_menu_sends_a_grab_and_a_release_of_the_clicked_prim()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Touchy);
        r.C.RightClick(r.Down(10, 10));
        r.Sent.Clear();
        Assert.True(r.C.Choose("touch"));
        Assert.IsType<GrabIntent>(r.Sent[0]);
        Assert.IsType<ReleaseIntent>(r.Sent[1]);
    }

    // the camera of the pick: straight above the click, looking down along the pick ray; and another one, somewhere else
    private static readonly (Vector3 Origin, Vector3 At) PickCamera = (new Vector3(10.4f, 10.3f, 60), new Vector3(0, 0, -1));
    private static readonly (Vector3 Origin, Vector3 At) MovedCamera = (new Vector3(4, 10, 34), Vector3.Normalize(new Vector3(6, 0, -4)));

    [Fact]
    public void Sit_Here_from_the_menu_sends_the_official_point_of_the_right_click_even_if_the_camera_moved_before_the_choice()
    {
        // the official viewer works the point out when the right-click picks (LLPickInfo's object offset), not when the entry is chosen
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30));
        var cam = PickCamera;
        r.C.Camera = () => cam;
        var pick = r.Down(10.4f, 10.3f);
        r.C.RightClick(pick);
        var expected = SeatPlacement.SitOffset(new Vector3(10, 10, 30), Quaternion.Identity, new Vector3(2, 2, 2), PickCamera.Origin, PickCamera.At, pick.Hit!.Position);
        cam = MovedCamera;
        r.Sent.Clear();
        Assert.True(r.C.Choose("sit"));
        var sit = Assert.IsType<SitIntent>(Assert.Single(r.Sent));
        Assert.Equal(expected.X, sit.Offset.X, 4);
        Assert.Equal(expected.Y, sit.Offset.Y, 4);
        Assert.Equal(expected.Z, sit.Offset.Z, 4);
    }

    [Fact]
    public void A_sit_from_the_menu_or_a_click_hands_the_keyboard_back_to_the_world()
    {
        // the official viewer puts the keyboard in the world after a sit (lltoolpie.cpp, gFocusMgr.setKeyboardFocus(NULL)), so the arrow keys
        // reach the avatar once it stands; the host releases the engine's focus when told
        var r = new Rig();
        int released = 0;
        r.C.SitSent += () => released++;
        r.Prim(1, new Vector3(10, 10, 30), Touchy);
        r.Prim(2, new Vector3(30, 10, 30), new PrimInteraction(ClickAction.Sit, 0));
        r.C.RightClick(r.Down(10, 10));
        Assert.True(r.C.Choose("sit"));
        Assert.Equal(1, released);
        r.C.LeftDown(r.Down(30, 10));
        Assert.Equal(2, released);
        Assert.Equal(2, r.Sent.OfType<SitIntent>().Count());
        // nothing else hands it back: a touch does not
        r.C.RightClick(r.Down(10, 10));
        Assert.True(r.C.Choose("touch"));
        Assert.Equal(2, released);
    }

    [Fact]
    public void A_click_on_a_sit_action_object_sends_the_official_point_of_the_click()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(ClickAction.Sit, 0));
        r.C.Camera = () => PickCamera;
        var pick = r.Down(10.4f, 10.3f);
        r.C.LeftDown(pick);
        var sit = Assert.IsType<SitIntent>(Assert.Single(r.Sent));
        var expected = SeatPlacement.SitOffset(new Vector3(10, 10, 30), Quaternion.Identity, new Vector3(2, 2, 2), PickCamera.Origin, PickCamera.At, pick.Hit!.Position);
        Assert.Equal(expected.X, sit.Offset.X, 4);
        Assert.Equal(expected.Y, sit.Offset.Y, 4);
        Assert.Equal(expected.Z, sit.Offset.Z, 4);
        // camera far above a 2 m box: the level plane through the centre, under the click
        Assert.Equal(0.4f, sit.Offset.X, 3);
        Assert.Equal(0.3f, sit.Offset.Y, 3);
        Assert.Equal(0f, sit.Offset.Z, 3);
    }

    [Fact]
    public void Take_back_by_id_takes_each_own_root_once_with_no_pick_and_leaves_what_may_not_be_taken()
    {
        var r = new Rig();
        uint own = ObjectFlagBits.YouOwner | ObjectFlagBits.Copy;
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(0, own));
        r.Prim(2, new Vector3(1, 0, 0), new PrimInteraction(0, own), parent: 1);   // a child of 1: its root is taken, once
        r.Prim(3, new Vector3(40, 40, 30), new PrimInteraction(0, own));           // another own box, far from any pick
        r.Prim(4, new Vector3(50, 50, 30), new PrimInteraction(0, 0));             // someone else's: never taken
        var taken = r.C.TakeBack([1, 2, 3, 4, 99]);                                  // 99 is not in the region
        Assert.Equal([1u, 3u], taken);
        Assert.Equal([new TakeIntent(1, false), new TakeIntent(3, false)], r.Sent.Cast<TakeIntent>().ToArray());
    }

    [Fact]
    public void Sit_stand_take_and_take_copy_from_the_menu_run_their_intents()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(0, ObjectFlagBits.YouOwner | ObjectFlagBits.Copy));
        r.C.RightClick(r.Down(10, 10));
        r.Sent.Clear();
        Assert.True(r.C.Choose("sit"));
        Assert.Equal(G(1), Assert.IsType<SitIntent>(r.Sent[0]).ObjectId);
        Assert.True(r.C.Choose("take"));
        Assert.Equal(new TakeIntent(1, false), r.Sent[1]);
        Assert.True(r.C.Choose("take_copy"));
        Assert.Equal(new TakeIntent(1, true), r.Sent[2]);
    }

    [Fact]
    public void While_seated_on_the_object_the_menu_offers_stand_and_it_sends_a_stand()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30));
        r.World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(0, 0, 0), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), ParentLocalId: 1));
        r.C.RightClick(r.Down(10, 10));
        Assert.True(r.C.Sitting);
        Assert.Contains("stand", r.C.Entries.Select(e => e.Id));
        r.Sent.Clear();
        Assert.True(r.C.Choose("stand"));
        Assert.IsType<StandIntent>(Assert.Single(r.Sent));
    }

    [Fact]
    public void Inspect_and_open_from_the_menu_raise_their_requests()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), new PrimInteraction(0, ObjectFlagBits.YouOwner));
        r.C.RightClick(r.Down(10, 10));
        Assert.True(r.C.Choose("inspect"));
        Assert.True(r.C.Choose("open"));
        Assert.Single(r.Inspected);
        Assert.Single(r.Opened);
    }

    [Fact]
    public void A_disabled_entry_does_nothing()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30));   // no touch handler, no ownership
        r.C.RightClick(r.Down(10, 10));
        r.Sent.Clear();
        Assert.False(r.C.Choose("touch"));
        Assert.False(r.C.Choose("take"));
        Assert.Empty(r.Sent);
    }

    // ---------------- right click on a person and on land ----------------

    [Fact]
    public void The_person_menu_runs_the_existing_social_actions()
    {
        var r = new Rig();
        r.C.RightClick(r.Down(120, 100));
        Assert.Equal(MenuKind.Avatar, r.C.Menu);
        Assert.True(r.C.Choose("im"));
        Assert.True(r.C.Choose("add_friend"));
        Assert.True(r.C.Choose("offer_teleport"));
        Assert.True(r.C.Choose("request_teleport"));
        Assert.Equal([$"im:{Other}", $"friend:{Other}", $"offer:{Other}", $"request:{Other}"], r.Social);
        Assert.True(r.C.Choose("inspect"));
        Assert.Equal(Other, Assert.Single(r.AvatarInspected));
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void The_own_avatars_menu_has_an_appearance_entry_and_choosing_it_asks_for_the_appearance_window()
    {
        var r = new Rig();
        // the own avatar's menu is the self menu (stand, fly, take off, inventory) once the host gives it its hooks; its Appearance entry
        // opens the Appearance window
        r.C.SelfHooks = new SelfHooks(() => r.AppearanceOpened++, () => { }, () => [], _ => { }, () => false, () => { });
        r.C.RightClick(r.DownOnAnyone(100, 100));
        Assert.Equal(MenuKind.Self, r.C.Menu);
        Assert.Contains("appearance", r.C.Entries.Select(e => e.Id));
        Assert.True(r.C.Choose("appearance"));
        Assert.Equal(1, r.AppearanceOpened);
        Assert.Empty(r.Social);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Another_persons_menu_has_no_appearance_entry()
    {
        var r = new Rig();
        r.C.RightClick(r.Down(120, 100));
        Assert.DoesNotContain(r.C.Entries, e => e.Id == "appearance");
        Assert.False(r.C.Choose("appearance"));
        Assert.Equal(0, r.AppearanceOpened);
    }

    [Fact]
    public void The_land_menu_sits_sets_home_and_asks_about_the_land()
    {
        var r = new Rig();
        r.C.RightClick(r.Down(101, 100));   // 1 m from the own avatar: within reach
        Assert.Equal(MenuKind.Land, r.C.Menu);
        Assert.True(r.C.Choose("sit_here"));
        Assert.IsType<SitGroundIntent>(Assert.Single(r.Sent));
        Assert.True(r.C.Choose("set_home"));
        Assert.Equal(["home"], r.Social);
        r.Sent.Clear();
        Assert.True(r.C.Choose("about_land"));
        var parcel = Assert.IsType<ParcelInfoIntent>(Assert.Single(r.Sent));
        Assert.Equal(101f, parcel.X, 1);
        Assert.Equal(100f, parcel.Y, 1);
        Assert.Equal(1, r.AboutLand);
    }

    [Fact]
    public void Sit_here_on_land_far_from_the_avatar_is_refused()
    {
        var r = new Rig();
        r.C.RightClick(r.Down(70, 70));
        Assert.False(r.C.Choose("sit_here"));
        Assert.Empty(r.Sent);
    }

    // ---------------- answers ----------------

    [Fact]
    public void Properties_are_kept_for_the_selected_object_only()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30));
        r.C.RightClick(r.Down(10, 10));
        r.C.Receive(new ObjectPropertiesReceived(G(99), "Other", "", Me, Me, Guid.Empty, null, 0, 0, 0, 0, false));
        Assert.Null(r.C.Properties);
        r.C.Receive(new ObjectPropertiesReceived(G(1), "Box A", "a box", Me, Me, Guid.Empty, null, 0, 0, 0, 0, false));
        Assert.Equal("Box A", r.C.Properties!.Name);
    }

    [Fact]
    public void A_sit_response_sets_the_seat_camera_and_standing_clears_it()
    {
        var r = new Rig();
        r.C.Receive(new SitResponded(G(1), new Vector3(2, 0, 1), new Vector3(0, 0, 1), true, false, default, Quaternion.Identity));
        Assert.Equal(new SeatCameraState(G(1), new Vector3(2, 0, 1), new Vector3(0, 0, 1), true), r.C.Seat);
        r.C.Stand();
        Assert.Null(r.C.Seat);
        Assert.IsType<StandIntent>(Assert.Single(r.Sent));
    }

    [Fact]
    public void A_sit_response_with_offsets_too_close_sets_no_camera_but_still_forces_mouselook()
    {
        var r = new Rig();
        r.C.Receive(new SitResponded(G(1), new Vector3(1, 1, 1), new Vector3(1, 1, 1), true, false, default, Quaternion.Identity));
        Assert.True(r.C.Seat!.ForceMouselook);
    }

    [Fact]
    public void The_parcel_answer_is_kept_for_About_Land()
    {
        var r = new Rig();
        var details = new ParcelDetailsReceived(4, "Plot", "", Me, false, 4096, false, false, false, false, false, false, 0, 0f);
        r.C.Receive(details);
        Assert.Equal(details, r.C.Parcel);
    }
}
