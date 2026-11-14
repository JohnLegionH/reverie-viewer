// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;
using V3 = System.Numerics.Vector3;

namespace Viewer.Core.Tests;

/// <summary>
/// Parity oracle: the gateway's MotionFieldsTests (web-viewer bb91ba5, WV-4b), 9 of 10, adapted from the wire bodies to
/// the core's world state and adapter conversions. Every_new_field_is_camelCase_on_the_wire stays in the gateway (JSON).
/// </summary>
public class MotionFieldsTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");

    private static WorldState NewWorld() => new(new CoreLog(new ListSink(), "test"));

    private static PrimData Prim(uint local, V3 angularVelocity = default, TextureAnim? anim = null) =>
        new(local, new Guid((int)local, 0, 0, new byte[8]), 0, 0, new V3(1, 2, 3), System.Numerics.Quaternion.Identity, V3.One,
            AngularVelocity: angularVelocity, TextureAnim: anim);

    // ---------------- terse updates ----------------

    [Fact]
    public void A_terse_update_with_velocity_produces_object_moved_carrying_it()
    {
        var w = NewWorld();
        var changes = new List<WorldChange>();
        w.Changed += changes.Add;
        w.ApplyObject(Sim, Prim(42));
        w.ApplyMove(Sim, 42, false, new V3(1, 2, 3), System.Numerics.Quaternion.Identity, new V3(0.5f, 0, 0), new V3(0, 0, -9.8f), new V3(0, 0, 1.5f));

        Assert.Equal(WorldChangeKind.ObjectMoved, changes[^1].Kind);
        var o = w.Object(new ObjectId(w.Epoch, 42))!;
        Assert.Equal(0.5f, o.Velocity.X);
        Assert.Equal(-9.8f, MotionFields.NonZero(o.Acceleration)!.Value.Z);
        Assert.Equal(1.5f, MotionFields.NonZero(o.AngularVelocity)!.Value.Z);
    }

    [Fact]
    public void A_still_object_carries_no_motion_fields_at_all()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Prim(1));
        var o = w.Object(new ObjectId(w.Epoch, 1))!;
        Assert.Null(MotionFields.NonZero(o.Acceleration));
        Assert.Null(MotionFields.NonZero(o.AngularVelocity));
        Assert.Null(o.TargetOmega);
        Assert.Null(o.TextureAnim);
    }

    // ---------------- full updates ----------------

    [Fact]
    public void A_full_update_with_TargetOmega_carries_it_as_axis_and_rate()
    {
        var w = NewWorld();
        w.ApplyObject(Sim, Prim(7, angularVelocity: new V3(0, 0, 2.5f)));
        var omega = w.Object(new ObjectId(w.Epoch, 7))!.TargetOmega;
        Assert.NotNull(omega);
        Assert.Equal(2.5f, omega!.Value.Rate, 4);
        Assert.Equal(0f, omega.Value.Axis.X, 4);
        Assert.Equal(0f, omega.Value.Axis.Y, 4);
        Assert.Equal(1f, omega.Value.Axis.Z, 4);
    }

    [Fact]
    public void A_diagonal_omega_keeps_its_direction_and_its_magnitude()
    {
        var omega = MotionFields.OmegaOf(new V3(3, 4, 0))!.Value;   // |v| = 5
        Assert.Equal(5f, omega.Rate, 4);
        Assert.Equal(0.6f, omega.Axis.X, 4);
        Assert.Equal(0.8f, omega.Axis.Y, 4);
    }

    [Fact]
    public void A_prim_that_is_not_spinning_has_no_TargetOmega() => Assert.Null(MotionFields.OmegaOf(V3.Zero));

    // ---------------- texture animation (through the adapter, on in-process LibreMetaverse objects) ----------------

    [Fact]
    public void A_texture_animation_is_passed_through_with_its_raw_flags()
    {
        var flags = Primitive.TextureAnimMode.ANIM_ON | Primitive.TextureAnimMode.LOOP | Primitive.TextureAnimMode.SMOOTH;
        var p = new Primitive
        {
            LocalID = 9,
            ID = UUID.Random(),
            PrimData = new Primitive.ConstructionData { PCode = PCode.Prim },
            TextureAnim = new Primitive.TextureAnimation { Face = 2, Flags = flags, SizeX = 4, SizeY = 4, Start = 0, Length = 16, Rate = 1.5f },
        };
        var w = NewWorld();
        w.ApplyObject(Sim, LmvTransport.ToPrimData(p));
        var anim = w.Object(new ObjectId(w.Epoch, 9))!.TextureAnim;
        Assert.NotNull(anim);
        Assert.Equal(2, anim!.Face);
        Assert.Equal((uint)flags, anim.Flags);
        Assert.Equal(4, anim.SizeX);
        Assert.Equal(4, anim.SizeY);
        Assert.Equal(16f, anim.Length);
        Assert.Equal(1.5f, anim.Rate);
    }

    [Fact]
    public void ALL_SIDES_becomes_minus_one()
    {
        var anim = new Primitive.TextureAnimation { Face = 0xFF, Flags = Primitive.TextureAnimMode.ANIM_ON };
        Assert.Equal(-1, LmvTransport.ToTextureAnim(anim)!.Face);
    }

    [Fact]
    public void No_animation_is_flags_zero_and_sends_nothing()
    {
        // TextureAnimation is a struct, so every prim has one; flags 0 is how the simulator says "none".
        Assert.Null(LmvTransport.ToTextureAnim(new Primitive.TextureAnimation()));
    }

    // ---------------- avatars ----------------

    [Fact]
    public void Avatar_moved_carries_angular_velocity_when_the_avatar_is_turning()
    {
        var w = NewWorld();
        w.ApplyAvatar(Sim, new AvatarData(7, Guid.NewGuid(), "A", new V3(1, 1, 1), System.Numerics.Quaternion.Identity, V3.One));
        w.ApplyMove(Sim, 7, true, new V3(1, 1, 1), System.Numerics.Quaternion.Identity, new V3(3.2f, 0, 0), V3.Zero, new V3(0, 0, 0.9f));
        var a = w.Avatar(new ObjectId(w.Epoch, 7))!;
        Assert.Equal(3.2f, a.Velocity.X);
        Assert.Equal(0.9f, MotionFields.NonZero(a.AngularVelocity)!.Value.Z);
    }
}
