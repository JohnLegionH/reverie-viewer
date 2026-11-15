// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.Building;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The Texture tab's messages as the region reads them: ObjectImage carries the prim's whole texture entry with the edited
/// faces set (the rest kept), and ModifyMaterialParams one entry per face. No grid; the class runs in parallel.</summary>
public class FaceMessageTests
{
    private static readonly Guid Wood = Guid.Parse("8955f4e3-71f0-4b39-9c1e-3f2a6b1d0c11");
    private static readonly Guid Gold = Guid.Parse("6e7d8c9b-0a1f-4e2d-9c3b-4a5f6e7d8c9b");

    [Fact]
    public void ObjectImage_carries_the_edited_faces_and_keeps_the_others()
    {
        var current = new Primitive.TextureEntry(new UUID(Wood));
        var other = current.CreateFace(3);
        other.Glow = 0.5f;
        var settings = new FaceSettings(Gold, new Rgba(1, 0, 0, 0.6f), 2, 3, 0.25f, -0.5f, 1.2f, TexGen.Planar, true, 0.3f);
        var packet = LmvEdit.Image(UUID.Random(), UUID.Random(), 77, current, [new FaceEdit(1, settings)]);
        var block = Assert.Single(packet.ObjectData);
        Assert.Equal(77u, block.ObjectLocalID);
        var te = new Primitive.TextureEntry(block.TextureEntry, 0, block.TextureEntry.Length);
        var f = te.GetFace(1);
        Assert.Equal(Gold, f.TextureID.Guid);
        Assert.Equal(MappingType.Planar, f.TexMapType);
        Assert.True(f.Fullbright);
        Assert.Equal(2f, f.RepeatU, 2);
        Assert.Equal(3f, f.RepeatV, 2);
        Assert.Equal(0.25f, f.OffsetU, 3);
        Assert.Equal(-0.5f, f.OffsetV, 3);
        Assert.Equal(0.6f, f.RGBA.A, 2);
        Assert.Equal(0.3f, f.Glow, 2);
        Assert.Equal(Wood, te.GetFace(0).TextureID.Guid);           // the default face stays
        Assert.Equal(0.5f, te.GetFace(3).Glow, 2);                    // and so does another face's own setting
        // the region's copy is read from the same bytes and is not changed by this one
        Assert.Equal(Wood, current.GetFace(1).TextureID.Guid);
    }

    [Fact]
    public void A_terse_update_with_a_texture_entry_updates_the_objects_faces_and_one_without_does_not()
    {
        // a region sends a change of textures alone as an ImprovedTerseObjectUpdate carrying the texture entry (OpenSim's
        // LLClientView: PrimUpdateFlags.Textures may use the terse update); the object's faces must follow it
        var prim = new Primitive { LocalID = 12, ID = UUID.Random(), Textures = new Primitive.TextureEntry(new UUID(Wood)), Scale = LibreMetaverse.Vector3.One,
            PrimData = new Primitive.ConstructionData { PCode = PCode.Prim }, Position = new LibreMetaverse.Vector3(1, 2, 3) };
        var te = new Primitive.TextureEntry(new UUID(Wood));
        var f = te.CreateFace(0);
        f.TextureID = new UUID(Gold);
        f.TexMapType = MappingType.Planar;
        var update = new ObjectMovementUpdate { LocalID = 12, Avatar = false, Position = new LibreMetaverse.Vector3(4, 5, 6), Rotation = LibreMetaverse.Quaternion.Identity, Textures = te };
        var data = LmvTransport.TerseWithTextures(prim, update);
        Assert.NotNull(data);
        Assert.Equal(Gold, data!.Textures![0].TextureId);
        Assert.Equal(TexGen.Planar, data.Textures[0].TexGen);
        Assert.Equal(Wood, data.Textures[1].TextureId);
        Assert.Equal(new System.Numerics.Vector3(4, 5, 6), data.Position);
        Assert.Null(LmvTransport.TerseWithTextures(prim, update with { Textures = null }));
        Assert.Null(LmvTransport.TerseWithTextures(prim, update with { Avatar = true }));
    }

    [Fact]
    public void ModifyMaterialParams_names_each_face_with_its_material_and_override()
    {
        var obj = UUID.Random();
        var body = LmvEdit.MaterialParams(obj, [0, 2], new UUID(Gold), "{\"x\":1}");
        Assert.Equal(2, body.Count);
        var first = (LibreMetaverse.StructuredData.OSDMap)body[0];
        Assert.Equal(obj, first["object_id"].AsUUID());
        Assert.Equal(0, first["side"].AsInteger());
        Assert.Equal(Gold, first["asset_id"].AsUUID().Guid);
        Assert.Equal("{\"x\":1}", first["gltf_json"].AsString());
        var off = (LibreMetaverse.StructuredData.OSDMap)LmvEdit.MaterialParams(obj, [1], UUID.Zero, null)[0];
        Assert.Equal(UUID.Zero, off["asset_id"].AsUUID());
        Assert.False(off.ContainsKey("gltf_json"));
    }
}
