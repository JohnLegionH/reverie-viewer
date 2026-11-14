// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Packets;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The sound messages as the protocol adapter reads them, from packets laid out as the region sends them (SoundTrigger, AttachedSound,
/// AttachedSoundGainChange, PreloadSound, ParcelOverlay, the sound fields of an object update, and the parcel's sound-local flag and
/// bitmap). Plain .NET, nothing shared, so the class runs in parallel.
/// </summary>
public class SoundMessageTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Sound = Guid.Parse("0f0e0d0c-0b0a-4908-8706-050403020100");
    private static readonly Guid Owner = Guid.Parse("9a1b2c3d-1111-4222-8333-444455556666");
    private static readonly Guid Obj = Guid.Parse("7c6b5a49-3827-4615-a4b3-c2d1e0f1a2b3");
    private static readonly Guid Parent = Guid.Parse("1e2d3c4b-5a69-4788-96a5-b4c3d2e1f001");

    [Fact]
    public void A_sound_trigger_keeps_every_field_of_its_block()
    {
        var p = new SoundTriggerPacket();
        p.SoundData.SoundID = new UUID(Sound);
        p.SoundData.OwnerID = new UUID(Owner);
        p.SoundData.ObjectID = new UUID(Obj);
        p.SoundData.ParentID = new UUID(Parent);
        p.SoundData.Handle = 256256UL << 32 | 512000UL;
        p.SoundData.Position = new Vector3(10.5f, 20.25f, 30f);
        p.SoundData.Gain = 0.75f;
        var e = LmvTransport.ToSoundTrigger(Here, p);
        Assert.NotNull(e);
        Assert.Equal((Sound, Owner, Obj, Parent), (e!.SoundId, e.OwnerId, e.ObjectId, e.ParentId));
        Assert.Equal(256256UL << 32 | 512000UL, e.RegionHandle);
        Assert.Equal(new System.Numerics.Vector3(10.5f, 20.25f, 30f), e.Position);
        Assert.Equal(0.75f, e.Gain);
        Assert.Same(Here, e.Sim);
    }

    [Theory]
    [InlineData((byte)SoundFlagBits.Loop)]
    [InlineData((byte)(SoundFlagBits.Loop | SoundFlagBits.SyncMaster))]
    [InlineData((byte)(SoundFlagBits.Loop | SoundFlagBits.SyncSlave))]
    [InlineData((byte)SoundFlagBits.Queue)]
    [InlineData((byte)SoundFlagBits.Stop)]
    [InlineData((byte)0)]
    public void An_attached_sound_keeps_its_ids_gain_and_flags(byte flags)
    {
        var p = new AttachedSoundPacket();
        p.DataBlock.SoundID = new UUID(Sound);
        p.DataBlock.ObjectID = new UUID(Obj);
        p.DataBlock.OwnerID = new UUID(Owner);
        p.DataBlock.Gain = 0.5f;
        p.DataBlock.Flags = flags;
        var e = LmvTransport.ToAttachedSound(Here, p);
        Assert.NotNull(e);
        Assert.Equal((Obj, Sound, Owner, 0.5f, flags), (e!.ObjectId, e.SoundId, e.OwnerId, e.Gain, e.Flags));
    }

    [Fact]
    public void A_gain_change_names_the_object_and_its_new_gain()
    {
        var p = new AttachedSoundGainChangePacket();
        p.DataBlock.ObjectID = new UUID(Obj);
        p.DataBlock.Gain = 0.2f;
        var e = LmvTransport.ToAttachedSoundGain(Here, p);
        Assert.NotNull(e);
        Assert.Equal((Obj, 0.2f), (e!.ObjectId, e.Gain));
    }

    [Fact]
    public void A_preload_gives_one_event_per_block()
    {
        var p = new PreloadSoundPacket
        {
            DataBlock =
            [
                new PreloadSoundPacket.DataBlockBlock { ObjectID = new UUID(Obj), OwnerID = new UUID(Owner), SoundID = new UUID(Sound) },
                new PreloadSoundPacket.DataBlockBlock { ObjectID = new UUID(Parent), OwnerID = new UUID(Owner), SoundID = new UUID(Obj) },
            ],
        };
        var e = LmvTransport.ToPreloads(Here, p);
        Assert.Equal(2, e.Count);
        Assert.Equal((Obj, Sound, Owner), (e[0].ObjectId, e[0].SoundId, e[0].OwnerId));
        Assert.Equal((Parent, Obj), (e[1].ObjectId, e[1].SoundId));
    }

    [Fact]
    public void A_parcel_overlay_keeps_its_quarter_and_bytes()
    {
        var data = new byte[1024];
        data[5] = 0x20;
        var p = new ParcelOverlayPacket();
        p.ParcelData.SequenceID = 2;
        p.ParcelData.Data = data;
        var e = LmvTransport.ToParcelOverlay(Here, p);
        Assert.NotNull(e);
        Assert.Equal(2, e!.Chunk);
        Assert.Equal(0x20, e.Data[5]);
        Assert.Equal(1024, e.Data.Length);
    }

    [Fact]
    public void An_object_update_with_a_sound_gives_its_sound_fields_and_one_without_gives_none()
    {
        var prim = new Primitive { ID = new UUID(Obj), Sound = new UUID(Sound), OwnerID = new UUID(Owner), SoundGain = 0.8f, SoundFlags = SoundFlags.Loop, SoundRadius = 12f };
        var e = LmvTransport.ToObjectSound(Here, prim);
        Assert.NotNull(e);
        Assert.Equal((Obj, Sound, Owner, 0.8f, (byte)SoundFlagBits.Loop, 12f), (e!.ObjectId, e.SoundId, e.OwnerId, e.Gain, e.Flags, e.CutoffRadius));
        Assert.Null(LmvTransport.ToObjectSound(Here, new Primitive { ID = new UUID(Obj) }));
    }

    [Fact]
    public void The_agents_parcel_carries_its_sound_local_flag_and_bitmap()
    {
        var bitmap = new byte[512];
        bitmap[0] = 1;
        var e = LmvTransport.ToAgentParcel(0, "Lot", 7, Here, soundLocal: true, bitmap: bitmap);
        Assert.NotNull(e);
        Assert.True(e!.SoundLocal);
        Assert.Same(bitmap, e.Bitmap);
    }
}
