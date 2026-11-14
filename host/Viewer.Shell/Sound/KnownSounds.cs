// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>
/// The sound ids an OpenSim grid carries in its default asset set (OpenSim's CollisionSoundsAssetSet): its own ids for the 49 material
/// pairs and 7 terrain sounds its region plays when things collide, and the official land step id, which it carries as a sand step. It
/// carries none of the official collision sounds, so none of the official material steps either: an OpenSim grid answers "not found" for
/// those unless its operator added them.
/// </summary>
public static class OpenSimSounds
{
    // OpenSim's default asset set (CollisionSoundsAssetSet.xml): its own ids for the material pairs and terrain sounds its region plays on collisions
    private static readonly string[] OpenSimCollisionIds =
    [
        "be7295c0-a158-11e1-b3dd-0800200c9a66",   // stone and stone
        "be7295c0-a158-11e1-b3dd-0800201c9a66",   // stone and metal
        "be7295c0-a158-11e1-b3dd-0800202c9a66",   // stone and glass
        "be7295c0-a158-11e1-b3dd-0800203c9a66",   // stone and wood
        "be7295c0-a158-11e1-b3dd-0800204c9a66",   // stone and flesh
        "be7295c0-a158-11e1-b3dd-0800205c9a66",   // stone and plastic
        "be7295c0-a158-11e1-b3dd-0800206c9a66",   // stone and rubber
        "be7295c0-a158-11e1-b3dd-0801200c9a66",   // metal and stone
        "be7295c0-a158-11e1-b3dd-0801201c9a66",   // metal and metal
        "be7295c0-a158-11e1-b3dd-0801202c9a66",   // metal and glass
        "be7295c0-a158-11e1-b3dd-0801203c9a66",   // metal and wood
        "be7295c0-a158-11e1-b3dd-0801204c9a66",   // metal and flesh
        "be7295c0-a158-11e1-b3dd-0801205c9a66",   // metal and plastic
        "be7295c0-a158-11e1-b3dd-0801206c9a66",   // metal and rubber
        "be7295c0-a158-11e1-b3dd-0802200c9a66",   // glass and stone
        "be7295c0-a158-11e1-b3dd-0802201c9a66",   // glass and metal
        "be7295c0-a158-11e1-b3dd-0802202c9a66",   // glass and glass
        "be7295c0-a158-11e1-b3dd-0802203c9a66",   // glass and wood
        "be7295c0-a158-11e1-b3dd-0802204c9a66",   // glass and flesh
        "be7295c0-a158-11e1-b3dd-0802205c9a66",   // glass and plastic
        "be7295c0-a158-11e1-b3dd-0802206c9a66",   // glass and rubber
        "be7295c0-a158-11e1-b3dd-0803200c9a66",   // wood and stone
        "be7295c0-a158-11e1-b3dd-0803201c9a66",   // wood and metal
        "be7295c0-a158-11e1-b3dd-0803202c9a66",   // wood and glass
        "be7295c0-a158-11e1-b3dd-0803203c9a66",   // wood and wood
        "be7295c0-a158-11e1-b3dd-0803204c9a66",   // wood and flesh
        "be7295c0-a158-11e1-b3dd-0803205c9a66",   // wood and plastic
        "be7295c0-a158-11e1-b3dd-0803206c9a66",   // wood and rubber
        "be7295c0-a158-11e1-b3dd-0804200c9a66",   // flesh and stone
        "be7295c0-a158-11e1-b3dd-0804201c9a66",   // flesh and metal
        "be7295c0-a158-11e1-b3dd-0804202c9a66",   // flesh and glass
        "be7295c0-a158-11e1-b3dd-0804203c9a66",   // flesh and wood
        "be7295c0-a158-11e1-b3dd-0804204c9a66",   // flesh and flesh
        "be7295c0-a158-11e1-b3dd-0804205c9a66",   // flesh and plastic
        "be7295c0-a158-11e1-b3dd-0804206c9a66",   // flesh and rubber
        "be7295c0-a158-11e1-b3dd-0805200c9a66",   // plastic and stone
        "be7295c0-a158-11e1-b3dd-0805201c9a66",   // plastic and metal
        "be7295c0-a158-11e1-b3dd-0805202c9a66",   // plastic and glass
        "be7295c0-a158-11e1-b3dd-0805203c9a66",   // plastic and wood
        "be7295c0-a158-11e1-b3dd-0805204c9a66",   // plastic and flesh
        "be7295c0-a158-11e1-b3dd-0805205c9a66",   // plastic and plastic
        "be7295c0-a158-11e1-b3dd-0805206c9a66",   // plastic and rubber
        "be7295c0-a158-11e1-b3dd-0806200c9a66",   // rubber and stone
        "be7295c0-a158-11e1-b3dd-0806201c9a66",   // rubber and metal
        "be7295c0-a158-11e1-b3dd-0806202c9a66",   // rubber and glass
        "be7295c0-a158-11e1-b3dd-0806203c9a66",   // rubber and wood
        "be7295c0-a158-11e1-b3dd-0806204c9a66",   // rubber and flesh
        "be7295c0-a158-11e1-b3dd-0806205c9a66",   // rubber and plastic
        "be7295c0-a158-11e1-b3dd-0806206c9a66",   // rubber and rubber
        "be7295c0-a158-11e1-b3dd-0807200c9a66",   // terrain and stone
        "be7295c0-a158-11e1-b3dd-0807201c9a66",   // terrain and metal
        "be7295c0-a158-11e1-b3dd-0807202c9a66",   // terrain and glass
        "be7295c0-a158-11e1-b3dd-0807203c9a66",   // terrain and wood
        "be7295c0-a158-11e1-b3dd-0807204c9a66",   // terrain and flesh
        "be7295c0-a158-11e1-b3dd-0807205c9a66",   // terrain and plastic
        "be7295c0-a158-11e1-b3dd-0807206c9a66",   // terrain and rubber
    ];

    /// <summary>The collision sounds of OpenSim's set (56).</summary>
    public static IReadOnlySet<Guid> Collisions { get; } = OpenSimCollisionIds.Select(Guid.Parse).ToHashSet();

    /// <summary>Every sound of OpenSim's set (57): the collision sounds and the land step.</summary>
    public static IReadOnlySet<Guid> Provided { get; } = Collisions.Append(StepSounds.OnLand).ToHashSet();
}

/// <summary>
/// The collision sounds the collision switch silences: the official viewer's table (28 distinct sounds, the material steps among them)
/// and OpenSim's own collision sounds, so the switch works on either kind of grid. The official viewer drops a region's one-shot sound
/// whose id is in its table while its collision sounds are off.
/// </summary>
public static class CollisionSounds
{
    // the official collision table (llmaterialtable.cpp, the sound ids of sound_ids.cpp)
    private static readonly string[] OfficialIds =
    [
        "9538f37c-456e-4047-81be-6435045608d4",   // stone and stone
        "aca261d8-e145-4610-9e20-9eff990f2c12",   // stone and metal
        "24babf58-7156-4841-9a3f-761bdbb8e237",   // stone and glass
        "8c0f84c3-9afd-4396-b5f5-9bca2c911c20",   // stone and wood
        "55c3e0ce-275a-46fa-82ff-e0465f5e8703",   // stone and flesh
        "0642fba6-5dcf-4d62-8e7b-94dbb529d117",   // stone and plastic
        "25a863e8-dc42-4e8a-a357-e76422ace9b5",   // stone and rubber
        "9e5c1297-6eed-40c0-825a-d9bcd86e3193",   // metal and metal
        "bc4a4348-cfcc-4e5e-908e-8a52a8915fe6",   // metal and glass
        "874a26fd-142f-4173-8c5b-890cd846c74d",   // metal and wood
        "14209133-4961-4acc-9649-53fc38ee1667",   // metal and flesh
        "e534761c-1894-4b61-b20c-658a6fb68157",   // metal and plastic
        "8761f73f-6cf9-4186-8aaa-0948ed002db1",   // metal and rubber
        "6a45ba0b-5775-4ea8-8513-26008a17f873",   // glass and glass
        "6e3fb0f7-6d9c-42ca-b86b-1122ff562d7d",   // glass and wood
        "75872e8c-bc39-451b-9b0b-042d7ba36cba",   // glass and flesh
        "992a6d1b-8c77-40e0-9495-4098ce539694",   // glass and plastic
        "2de4da5a-faf8-46be-bac6-c4d74f1e5767",   // glass and rubber
        "063c97d3-033a-4e9b-98d8-05c8074922cb",   // wood and wood
        "be582e5d-b123-41a2-a150-454c39e961c8",   // wood and flesh
        "c70141d4-ba06-41ea-bcbc-35ea81cb8335",   // wood and plastic
        "7d1826f4-24c4-4aac-8c2e-eff45df37783",   // wood and rubber
        "dce5fdd4-afe4-4ea1-822f-dd52cac46b08",   // flesh and flesh
        "51011582-fbca-4580-ae9e-1a5593f094ec",   // flesh and plastic
        "68d62208-e257-4d0c-bbe2-20c9ea9760bb",   // flesh and rubber
        "153c8bf7-fb89-4d89-b263-47e58b1b4774",   // rubber and rubber
        "75cf3ade-9a5b-4c4d-bb35-f9799bda7fb2",   // rubber and plastic
        "0e24a717-b97e-4b77-9c94-b59a5a88b2da",   // plastic and plastic
    ];

    public static IReadOnlySet<Guid> Official { get; } = OfficialIds.Select(Guid.Parse).ToHashSet();

    public static bool IsCollision(Guid id) => Official.Contains(id) || OpenSimSounds.Collisions.Contains(id);
}

/// <summary>An Ogg Vorbis stream's channels and sample rate, from its identification header.</summary>
public readonly record struct OggVorbisInfo(int Channels, int SampleRate);

/// <summary>Reads the identification header of an Ogg Vorbis stream (the first packet of its first page, as the Vorbis I specification lays
/// it out): the packet type 1, "vorbis", version 0, then the channel count and the sample rate.</summary>
public static class OggVorbis
{
    public static OggVorbisInfo? Read(ReadOnlySpan<byte> b)
    {
        // the page header: "OggS", version 0, type, granule (8), serial (4), sequence (4), checksum (4), segment count, segment table
        if (b.Length < 27 || b[0] != (byte)'O' || b[1] != (byte)'g' || b[2] != (byte)'g' || b[3] != (byte)'S' || b[4] != 0)
            return null;
        int segments = b[26];
        int at = 27 + segments;
        // the identification packet: type 1, "vorbis", version (4, zero), channels (1), sample rate (4, little-endian)
        if (b.Length < at + 16 || b[at] != 1 || !b.Slice(at + 1, 6).SequenceEqual("vorbis"u8))
            return null;
        if (b[at + 7] != 0 || b[at + 8] != 0 || b[at + 9] != 0 || b[at + 10] != 0)
            return null;
        int channels = b[at + 11];
        int rate = b[at + 12] | b[at + 13] << 8 | b[at + 14] << 16 | b[at + 15] << 24;
        return channels > 0 && rate > 0 ? new OggVorbisInfo(channels, rate) : null;
    }
}

/// <summary>The sounds the Sound Tuner offers: the interface sounds in the official order, then our footstep (played when a grid lacks a
/// step sound).</summary>
public static class SoundTunerList
{
    /// <summary>Our footstep's file.</summary>
    public const string Footstep = "world-footstep.wav";

    public static IReadOnlyList<string> Files { get; } = UiSoundCatalog.All.Select(i => i.File).Append(Footstep).ToArray();
}
