// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Meshing;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell.Build;
using Xunit;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Viewer.Core.Tests;

/// <summary>
/// The Build window's Features tab: the official ranges of the flexible, light, projector and physics values, which prims can be made
/// flexible, what the tab reads from a prim and from the region, what each change sends, and the messages they become. In-memory world, no
/// engine and no grid; each test builds its own, so the class runs in parallel.
/// </summary>
public class BuildFeaturesTabTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("4b1d2e3f-5a6b-4c7d-8e9f-0a1b2c3d4e01");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;
    private static readonly UUID Agent = new("1a2b3c4d-0000-4000-8000-000000000001"), Session = new("1a2b3c4d-0000-4000-8000-000000000002");

    internal sealed class Rig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public EditController C;

        public Rig()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new EditController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); });
            C.SelectTool(BuildTool.Edit);
        }

        public void Prim(uint local, Vector3 pos, uint flags, uint parent = 0, PrimShape? shape = null, PrimLight? light = null, PrimProjector? projector = null,
            FlexibleParams? flexible = null, int material = 3) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), parent, 0, pos, Quaternion.Identity, new Vector3(2, 2, 2), Shape: shape ?? new PrimShape(),
                Light: light, Projector: projector, Interaction: new PrimInteraction(0, flags), Material: material, Flexible: flexible));

        public PickResult At(float x, float y) => ObjectPicker.Pick(World, new PickRay(new Vector3(x, y, 60), new Vector3(0, 0, -1)), (_, _) => 20f, ignoreAgent: Me);

        public void Select(float x, float y, bool shift = false) => Assert.True(C.Click(At(x, y), shift));

        public T Last<T>() where T : ActionIntent => Assert.IsType<T>(Sent[^1]);
    }

    // ---------------- ranges ----------------

    [Fact]
    public void Flexible_settings_are_held_to_the_official_ranges()
    {
        var f = FeatureRules.Clamp(new FlexibleParams(7, -20f, 12f, -1f, 11f, new Vector3(20, -20, 3)));
        Assert.Equal(new FlexibleParams(3, -10f, 10f, 0f, 10f, new Vector3(10, -10, 3)), f);
        Assert.Equal(new FlexibleParams(0, 10f, 0f, 10f, 0f, Vector3.Zero), FeatureRules.Clamp(new FlexibleParams(-2, 15f, -3f, 40f, -1f, Vector3.Zero)));
        Assert.Equal(FlexibleParams.Default, FeatureRules.Clamp(FlexibleParams.Default));
    }

    [Fact]
    public void Light_and_projector_values_are_held_to_the_official_ranges()
    {
        Assert.Equal(new LightParams(new Vector3(1, 0, 0.5f), 1f, 20f, 2f), FeatureRules.Clamp(new LightParams(new Vector3(2, -1, 0.5f), 1.5f, 25f, 3f)));
        Assert.Equal(new LightParams(Vector3.One, 0f, 0f, 0f), FeatureRules.Clamp(new LightParams(Vector3.One, -1f, -5f, -1f)));
        var tex = G(77);
        Assert.Equal(new ProjectorParams(tex, 3f, -20f, 1f), FeatureRules.Clamp(new ProjectorParams(tex, 4f, -30f, 2f)));
        Assert.Equal(new ProjectorParams(tex, 0f, 20f, 0f), FeatureRules.Clamp(new ProjectorParams(tex, -1f, 25f, -1f)));
    }

    [Fact]
    public void Physics_values_are_held_to_the_official_ranges_and_an_unknown_shape_type_is_the_prims_own()
    {
        Assert.Equal(new PhysicsParams(FeatureRules.ShapePrim, -1f, 255f, 1f, 1f), FeatureRules.Clamp(new PhysicsParams(9, -5f, 300f, 0f, 2f)));
        Assert.Equal(new PhysicsParams(FeatureRules.ShapeConvex, 28f, 0f, 22587f, 0f), FeatureRules.Clamp(new PhysicsParams(FeatureRules.ShapeConvex, 40f, -1f, 30000f, -1f)));
        Assert.Equal(PhysicsParams.Default, FeatureRules.Clamp(PhysicsParams.Default));
    }

    [Fact]
    public void A_prim_on_a_straight_or_flexible_path_can_be_made_flexible_a_curved_path_a_sculpt_or_a_mesh_cannot()
    {
        Assert.True(FeatureRules.CanFlex(new PrimShape()));
        Assert.True(FeatureRules.CanFlex(new PrimShape { PathCurve = FeatureRules.PathFlexible }));
        Assert.False(FeatureRules.CanFlex(new PrimShape { PathCurve = 32 }));
        Assert.False(FeatureRules.CanFlex(new PrimShape { SculptType = 1, SculptId = G(9) }));
        Assert.False(FeatureRules.CanFlex(new PrimShape { SculptType = PrimShape.SculptTypeMesh, SculptId = G(9) }));
        Assert.False(FeatureRules.CanFlex(null));
    }

    // ---------------- what the tab reads ----------------

    [Fact]
    public void The_tab_reads_the_prims_flexible_light_projector_and_material_and_the_regions_physics_once_it_answers()
    {
        var r = new Rig();
        var flex = new FlexibleParams(3, 1f, 4f, 2f, 5f, new Vector3(0, 0, 1));
        r.Prim(1, new Vector3(10, 10, 30), Own, light: new PrimLight(new Vector3(1, 0.5f, 0), 0.8f, 6f, 1.25f, 0f),
            projector: new PrimProjector(G(40), 1.2f, 3f, 0.4f), flexible: flex, material: 1);
        Assert.Null(r.C.Features());
        r.Select(10, 10);
        var f = Assert.IsType<ObjectFeatures>(r.C.Features());
        Assert.Equal(flex, f.Flexible);
        Assert.Equal(new LightParams(new Vector3(1, 0.5f, 0), 0.8f, 6f, 1.25f), f.Light);
        Assert.Equal(new ProjectorParams(G(40), 1.2f, 3f, 0.4f), f.Projector);
        Assert.Equal(1, f.Material);
        Assert.True(f.CanFlex);
        Assert.Equal(PhysicsParams.Default, f.Physics);   // until the region answers
        var p = new PhysicsParams(FeatureRules.ShapeConvex, 2f, 0.3f, 500f, 0.9f);
        r.C.Receive(new ObjectPhysicsReceived(G(1), 1, p));
        Assert.Equal(p, r.C.Features()!.Physics);
        r.C.Receive(new ObjectPhysicsReceived(G(9), 9, PhysicsParams.Default));   // another object's answer changes nothing here
        Assert.Equal(p, r.C.Features()!.Physics);
    }

    [Fact]
    public void Selecting_asks_the_region_for_the_physics_of_the_selected_prims()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(20, 20, 30), Own);
        r.Select(10, 10);
        r.Select(20, 20, shift: true);
        Assert.True(r.C.RequestPhysics());
        Assert.Equal([G(1), G(2)], r.Last<RequestPhysicsIntent>().ObjectIds);
    }

    // ---------------- what each change sends ----------------

    [Fact]
    public void Ticking_Flexible_Path_sends_the_default_settings_and_clearing_it_sends_none_and_a_prim_that_cannot_be_modified_sends_nothing()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(20, 20, 30), ObjectFlagBits.Move);   // not ours, not modifiable
        r.Select(10, 10);
        Assert.True(r.C.SetFlexible(true));
        Assert.Equal(new SetFlexibleIntent(1, FlexibleParams.Default), r.Last<SetFlexibleIntent>());
        Assert.True(r.C.SetFlexible(true, new FlexibleParams(9, 0f, 0f, 0f, 0f, Vector3.Zero)));
        Assert.Equal(3, r.Last<SetFlexibleIntent>().Flexible!.Value.Softness);   // held to the range
        Assert.True(r.C.SetFlexible(false));
        Assert.Null(r.Last<SetFlexibleIntent>().Flexible);
        r.Select(20, 20);
        int before = r.Sent.Count(i => i is SetFlexibleIntent);
        Assert.False(r.C.SetFlexible(true));
        Assert.Equal(before, r.Sent.Count(i => i is SetFlexibleIntent));
    }

    [Fact]
    public void Light_on_sends_the_official_default_light_values_are_held_to_range_and_off_sends_none()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Select(10, 10);
        Assert.True(r.C.SetLight(true));
        Assert.Equal(new SetLightIntent(1, LightParams.Default, null), r.Last<SetLightIntent>());
        Assert.True(r.C.SetLight(true, new LightParams(Vector3.One, 1f, 30f, 1f), new ProjectorParams(G(40), 5f, 0f, 0f)));
        var sent = r.Last<SetLightIntent>();
        Assert.Equal(20f, sent.Light!.Value.Radius);
        Assert.Equal(3f, sent.Projector!.Value.Fov);
        Assert.True(r.C.SetLight(false));
        Assert.Equal(new SetLightIntent(1, null, null), r.Last<SetLightIntent>());
    }

    [Fact]
    public void Physics_values_are_sent_with_the_prims_own_flags_for_every_selected_prim()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own | ObjectFlagBits.Physics);
        r.Prim(2, new Vector3(20, 20, 30), Own);
        r.Select(10, 10);
        r.Select(20, 20, shift: true);
        var p = new PhysicsParams(FeatureRules.ShapeConvex, 2f, 0.3f, 500f, 0.9f);
        Assert.True(r.C.SetPhysics(p with { Friction = 999f }));
        var sent = r.Sent.OfType<SetPhysicsIntent>().ToList();
        Assert.Equal([1u, 2u], sent.Select(s => s.LocalId));
        Assert.True(sent[0].Physical);
        Assert.False(sent[1].Physical);
        Assert.All(sent, s => Assert.Equal(p with { Friction = 255f }, s.Physics));
    }

    [Fact]
    public void Material_is_sent_for_every_selected_prim_and_an_unknown_material_is_refused()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(20, 20, 30), Own);
        r.Select(10, 10);
        r.Select(20, 20, shift: true);
        Assert.True(r.C.SetMaterial(1));
        Assert.Equal([new SetObjectMaterialIntent(1, 1), new SetObjectMaterialIntent(2, 1)], r.Sent.OfType<SetObjectMaterialIntent>());
        Assert.False(r.C.SetMaterial(7));
        Assert.Equal(2, r.Sent.Count(i => i is SetObjectMaterialIntent));
    }

    // ---------------- the messages ----------------

    [Fact]
    public void The_flexible_block_is_packed_as_the_protocol_has_it_and_off_clears_it()
    {
        var f = new FlexibleParams(3, 0.3f, 2f, 1f, 4f, new Vector3(0, 0, 1));
        var on = LmvFeatures.Flexible(Agent, Session, 7, f).ObjectData[0];
        Assert.Equal(7u, on.ObjectLocalID);
        Assert.Equal(LmvFeatures.ParamFlexible, on.ParamType);
        Assert.True(on.ParamInUse);
        var expected = new Primitive.FlexibleData { Softness = 3, Gravity = 0.3f, Drag = 2f, Wind = 1f, Tension = 4f, Force = new LibreMetaverse.Vector3(0, 0, 1) }.GetBytes();
        Assert.Equal(expected, on.ParamData);
        Assert.Equal((uint)expected.Length, on.ParamSize);
        var off = LmvFeatures.Flexible(Agent, Session, 7, null).ObjectData[0];
        Assert.Equal(LmvFeatures.ParamFlexible, off.ParamType);
        Assert.False(off.ParamInUse);
    }

    [Fact]
    public void The_light_and_its_image_go_as_two_blocks_and_no_projector_clears_the_image()
    {
        var light = new LightParams(new Vector3(1, 0.5f, 0.25f), 0.8f, 6f, 1.25f);
        var proj = new ProjectorParams(G(40), 1.2f, 3f, 0.4f);
        var both = LmvFeatures.Light(Agent, Session, 7, light, proj).ObjectData;
        Assert.Equal(2, both.Length);
        Assert.Equal((LmvFeatures.ParamLight, true), (both[0].ParamType, both[0].ParamInUse));
        var expectLight = new Primitive.LightData { Color = new Color4(1f, 0.5f, 0.25f, 0.8f), Radius = 6f, Falloff = 1.25f, Cutoff = 0f, Intensity = 0.8f }.GetBytes();
        Assert.Equal(expectLight, both[0].ParamData);
        Assert.Equal((LmvFeatures.ParamLightImage, true), (both[1].ParamType, both[1].ParamInUse));
        var expectImage = new Primitive.LightImage { LightTexture = new UUID(G(40)), Params = new LibreMetaverse.Vector3(1.2f, 3f, 0.4f) }.GetBytes();
        Assert.Equal(expectImage, both[1].ParamData);
        var noImage = LmvFeatures.Light(Agent, Session, 7, light, null).ObjectData;
        Assert.Equal((LmvFeatures.ParamLightImage, false), (noImage[1].ParamType, noImage[1].ParamInUse));
        var off = LmvFeatures.Light(Agent, Session, 7, null, null).ObjectData;
        Assert.Equal([(LmvFeatures.ParamLight, false), (LmvFeatures.ParamLightImage, false)], off.Select(b => (b.ParamType, b.ParamInUse)));
    }

    [Fact]
    public void Physics_settings_ride_in_the_flag_updates_extra_physics_block_and_material_in_ObjectMaterial()
    {
        var m = LmvFeatures.Physics(Agent, Session, 7, new PhysicsParams(FeatureRules.ShapeConvex, 2f, 0.3f, 500f, 0.9f), physical: true, temporary: false, phantom: true);
        Assert.Equal((7u, true, false, true), (m.AgentData.ObjectLocalID, m.AgentData.UsePhysics, m.AgentData.IsTemporary, m.AgentData.IsPhantom));
        var x = Assert.Single(m.ExtraPhysics);
        Assert.Equal((FeatureRules.ShapeConvex, 500f, 0.3f, 0.9f, 2f), (x.PhysicsShapeType, x.Density, x.Friction, x.Restitution, x.GravityMultiplier));
        var mat = LmvFeatures.Material(Agent, Session, 7, 1).ObjectData;
        Assert.Equal((7u, (byte)1), (Assert.Single(mat).ObjectLocalID, mat[0].Material));
    }

    [Fact]
    public void The_regions_physics_answer_is_read_per_object_and_the_request_names_each_object()
    {
        var body = LmvFeatures.PhysicsRequest([G(1), G(2)]);
        Assert.Equal([G(1), G(2)], Assert.IsType<OSDArray>(body["object_ids"]).Select(o => o.AsUUID().Guid));
        var reply = new OSDMap
        {
            [G(1).ToString()] = new OSDMap
            {
                ["PhysicsShapeType"] = OSD.FromInteger(2), ["Density"] = OSD.FromReal(500), ["Friction"] = OSD.FromReal(0.3),
                ["Restitution"] = OSD.FromReal(0.9), ["GravityMultiplier"] = OSD.FromReal(2),
            },
            ["not-an-id"] = new OSDMap(),
        };
        var read = Assert.Single(LmvFeatures.ParsePhysics(reply));
        Assert.Equal(G(1), read.Id);
        Assert.Equal(new PhysicsParams(2, 2f, 0.3f, 500f, 0.9f), read.Physics);
        Assert.Empty(LmvFeatures.ParsePhysics(null));
    }

    [Fact]
    public void A_prims_flexible_block_reaches_the_world_and_a_prim_without_one_has_none()
    {
        var p = new Primitive { PrimData = new Primitive.ConstructionData(), Flexible = new Primitive.FlexibleData { Softness = 3, Gravity = 1f, Drag = 4f, Wind = 2f, Tension = 5f, Force = new LibreMetaverse.Vector3(0, 0, 1) } };
        Assert.Equal(new FlexibleParams(3, 1f, 4f, 2f, 5f, new Vector3(0, 0, 1)), LmvTransport.ToFlexible(p));
        Assert.Null(LmvTransport.ToFlexible(new Primitive { PrimData = new Primitive.ConstructionData() }));
    }
}
