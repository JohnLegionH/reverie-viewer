// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The adapter's LibreMetaverse -> core conversions, on in-process LibreMetaverse objects (no network, no grid): the
/// fields the world state relies on survive translation, including the attachment point carried in PrimData.
/// </summary>
public class AdapterTranslationTests
{
    [Fact]
    public void A_prim_translates_with_its_ids_parent_attachment_point_and_motion()
    {
        var id = UUID.Random();
        var p = new Primitive
        {
            LocalID = 501,
            ID = id,
            ParentID = 900,
            Position = new Vector3(1, 2, 3),
            Rotation = new Quaternion(0, 0, 0.7071068f, 0.7071068f),
            Scale = new Vector3(0.5f, 0.5f, 0.5f),
            Velocity = new Vector3(4, 5, 6),
            AngularVelocity = new Vector3(0, 0, 1),
            PrimData = new Primitive.ConstructionData { PCode = PCode.Prim, AttachmentPoint = AttachmentPoint.Chest },
        };
        var d = LmvTransport.ToPrimData(p);
        Assert.Equal(501u, d.LocalId);
        Assert.Equal(id.Guid, d.FullId);
        Assert.Equal(900u, d.ParentLocalId);
        Assert.Equal((int)AttachmentPoint.Chest, d.AttachmentPoint);
        Assert.Equal(new System.Numerics.Vector3(1, 2, 3), d.Position);
        Assert.Equal(0.7071068f, d.Rotation.Z);
        Assert.Equal(new System.Numerics.Vector3(4, 5, 6), d.Velocity);
        Assert.Equal(new System.Numerics.Vector3(0, 0, 1), d.AngularVelocity);
    }

    [Fact]
    public void An_avatar_translates_with_its_agent_id_name_and_velocity()
    {
        var id = UUID.Random();
        var a = new Avatar { LocalID = 900, ID = id, Position = new Vector3(128, 128, 25), Velocity = new Vector3(3, 0, 0), Rotation = Quaternion.Identity };
        var d = LmvTransport.ToAvatarData(a);
        Assert.Equal(900u, d.LocalId);
        Assert.Equal(id.Guid, d.AgentId);
        Assert.Equal(new System.Numerics.Vector3(3, 0, 0), d.Velocity);   // map oracle AvatarUpdateVelocityTests: a full avatar update carries velocity
    }
}
