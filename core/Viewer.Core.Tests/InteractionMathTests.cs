// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;
using LM = LibreMetaverse;

namespace Viewer.Core.Tests;

/// <summary>The seat camera, the About Land rows, the library-to-core mapping of click data and the new intents. Independent members; run in parallel.</summary>
public class InteractionMathTests
{
    private static readonly Guid Owner = Guid.Parse("0f3f2d4e-7a41-4c1d-9a55-5b0f2f6a1e11");

    // ---------------- the seat camera ----------------

    [Fact]
    public void The_seat_camera_is_the_objects_pose_plus_the_offsets_turned_with_it()
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var cam = SitCamera.Compute(new Vector3(100, 100, 20), turn, new Vector3(2, 0, 1), new Vector3(0, 0, 1))!.Value;
        Assert.Equal(100f, cam.Eye.X, 3);
        Assert.Equal(102f, cam.Eye.Y, 3);   // +x of the object is the region's +y after the quarter turn
        Assert.Equal(21f, cam.Eye.Z, 3);
        Assert.Equal(new Vector3(100, 100, 21), cam.Focus);
    }

    [Fact]
    public void Offsets_that_are_nearly_equal_set_no_camera()
    {
        Assert.Null(SitCamera.Compute(Vector3.Zero, Quaternion.Identity, new Vector3(1, 1, 1), new Vector3(1, 1, 1.0005f)));
        Assert.NotNull(SitCamera.Compute(Vector3.Zero, Quaternion.Identity, new Vector3(1, 1, 1), new Vector3(1, 1, 1.01f)));
    }

    // ---------------- About Land ----------------

    private static ParcelDetailsReceived Parcel(bool group = false, bool pass = false) =>
        new(4, "Home Plot", "A plot", Owner, group, 4096, UseAccessList: true, UseBanList: true, GroupAccessOnly: false,
            DenyAnonymous: true, DenyAgeUnverified: false, UsePassList: pass, PassPrice: 10, PassHours: 1.5f);

    private static string? Value(IReadOnlyList<LandRow> rows, string key) => rows.FirstOrDefault(r => r.LabelKey == key)?.Value;

    [Fact]
    public void About_land_lists_name_owner_and_area()
    {
        var rows = LandInfo.Rows(Parcel(), id => id == Owner ? "Someone Resident" : null);
        Assert.Equal("Home Plot", Value(rows, "land.name"));
        Assert.Equal("Someone Resident", Value(rows, "land.owner"));
        Assert.Equal("4096", Value(rows, "land.area"));
    }

    [Fact]
    public void About_land_shows_the_owner_by_id_while_the_name_is_unknown_and_marks_group_ownership()
    {
        Assert.Equal(Owner.ToString(), Value(LandInfo.Rows(Parcel(), _ => null), "land.owner"));
        Assert.Equal("land.group_owned", Value(LandInfo.Rows(Parcel(group: true), _ => "x"), "land.owner"));
    }

    [Fact]
    public void About_land_states_the_access_and_ban_settings()
    {
        var rows = LandInfo.Rows(Parcel(), _ => null);
        Assert.Equal("land.on", Value(rows, "land.access_list"));
        Assert.Equal("land.on", Value(rows, "land.ban_list"));
        Assert.Equal("land.off", Value(rows, "land.group_only"));
        Assert.Equal("land.on", Value(rows, "land.payment_required"));
        Assert.Equal("land.off", Value(rows, "land.age_required"));
        Assert.Equal("land.off", Value(rows, "land.pass_list"));
    }

    [Fact]
    public void About_land_adds_the_pass_price_and_hours_only_when_passes_are_sold()
    {
        Assert.Null(Value(LandInfo.Rows(Parcel(pass: false), _ => null), "land.pass_price"));
        var rows = LandInfo.Rows(Parcel(pass: true), _ => null);
        Assert.Equal("10", Value(rows, "land.pass_price"));
        Assert.Equal("1.5", Value(rows, "land.pass_hours"));
    }

    // ---------------- library values to core values ----------------

    [Fact]
    public void A_plain_prim_carries_no_interaction_block()
    {
        Assert.Null(LmvTransport.ToInteraction(new LM.Primitive()));
    }

    [Fact]
    public void Click_action_flags_and_text_are_carried_over()
    {
        var p = new LM.Primitive
        {
            ClickAction = LM.ClickAction.Sit,
            Flags = LM.PrimFlags.Touch | LM.PrimFlags.ObjectYouOwner,
            Text = "Sit here",
            TextColor = new LM.Color4(1f, 0.5f, 0f, 0.8f),
        };
        var i = LmvTransport.ToInteraction(p)!;
        Assert.Equal(ClickAction.Sit, i.ClickAction);
        Assert.Equal(ObjectFlagBits.HandleTouch | ObjectFlagBits.YouOwner, i.Flags & (ObjectFlagBits.HandleTouch | ObjectFlagBits.YouOwner));
        Assert.Equal("Sit here", i.HoverText);
        Assert.Equal(HoverTextRules.Pack(255, 128, 0, 204), i.HoverColour);
    }

    [Fact]
    public void The_regions_move_and_transfer_flags_are_carried_over_so_edits_and_take_can_read_them()
    {
        var i = LmvTransport.ToInteraction(new LM.Primitive { Flags = LM.PrimFlags.ObjectMove | LM.PrimFlags.ObjectTransfer | LM.PrimFlags.ObjectModify })!;
        Assert.NotNull(i);
        Assert.Equal(ObjectFlagBits.Move | ObjectFlagBits.Transfer | ObjectFlagBits.Modify, i.Flags);
    }

    [Fact]
    public void A_prim_with_only_text_still_carries_it()
    {
        var i = LmvTransport.ToInteraction(new LM.Primitive { Text = "hi", TextColor = new LM.Color4(1f, 1f, 1f, 1f) })!;
        Assert.Equal("hi", i.HoverText);
        Assert.Equal(ClickAction.Touch, i.ClickAction);
    }

    // ---------------- intents ----------------

    private sealed class Rig
    {
        public FakeTransport Transport = new();
        public ActionPipeline Pipeline;
        public Rig() => Pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
    }

    private static readonly TouchSurface Surface = new(new(0.25f, 0.75f), new(0.5f, 0.5f), 2, new Vector3(10, 11, 12), Vector3.UnitZ, Vector3.UnitX);

    [Fact]
    public void A_grab_and_a_release_reach_the_transport_with_the_surface_data()
    {
        var r = new Rig();
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new GrabIntent(77, Surface, new Vector3(1, 2, 3))).Status);
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new ReleaseIntent(77, Surface)).Status);
        Assert.Equal([new GrabCommand(77, new Vector3(1, 2, 3), Surface), (TransportCommand)new ReleaseCommand(77, Surface)], r.Transport.Commands);
    }

    [Fact]
    public void A_grab_of_no_object_or_with_a_broken_surface_is_refused_and_nothing_is_sent()
    {
        var r = new Rig();
        Assert.Equal(ActionStatus.Invalid, r.Pipeline.Submit(new GrabIntent(0, Surface)).Status);
        Assert.Equal(ActionStatus.Invalid, r.Pipeline.Submit(new GrabIntent(5, Surface with { Normal = new Vector3(float.NaN, 0, 0) })).Status);
        Assert.Equal(ActionStatus.Invalid, r.Pipeline.Submit(new ReleaseIntent(0, Surface)).Status);
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void The_surface_when_nothing_was_hit_is_the_official_empty_one()
    {
        var none = TouchSurface.None;
        Assert.Equal(-1, none.Face);
        Assert.Equal(new System.Numerics.Vector2(-1f, -1f), none.Uv);
        Assert.Equal(Vector3.Zero, none.Normal);
        Assert.True(none.IsFinite);
    }

    [Fact]
    public void Sit_on_ground_properties_take_and_parcel_requests_become_their_commands()
    {
        var r = new Rig();
        r.Pipeline.Submit(new SitGroundIntent());
        r.Pipeline.Submit(new ObjectPropertiesIntent(9));
        r.Pipeline.Submit(new TakeIntent(9, Copy: false));
        r.Pipeline.Submit(new TakeIntent(9, Copy: true));
        r.Pipeline.Submit(new ParcelInfoIntent(12.5f, 30f));
        Assert.Equal<TransportCommand>([new SitGroundCommand(), new RequestObjectPropertiesCommand(9), new TakeObjectCommand(9, false), new TakeObjectCommand(9, true), new RequestParcelCommand(12.5f, 30f)], r.Transport.Commands);
    }

    [Fact]
    public void Taking_or_inspecting_no_object_is_refused()
    {
        var r = new Rig();
        Assert.Equal(ActionStatus.Invalid, r.Pipeline.Submit(new TakeIntent(0, false)).Status);
        Assert.Equal(ActionStatus.Invalid, r.Pipeline.Submit(new ObjectPropertiesIntent(0)).Status);
        Assert.Equal(ActionStatus.Invalid, r.Pipeline.Submit(new ParcelInfoIntent(-1f, 5f)).Status);
        Assert.Empty(r.Transport.Commands);
    }
}
