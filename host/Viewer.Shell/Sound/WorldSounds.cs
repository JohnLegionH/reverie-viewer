// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;

namespace Viewer.Shell.Sound;

/// <summary>What plays sounds in the world: the engine side. Voices are numbered by the service. Main thread only.</summary>
public interface IWorldSoundOutput
{
    /// <summary>Starts a voice on a sound's bytes (Ogg Vorbis from the grid, or one of our WAV files). False when they do not decode.</summary>
    bool Start(int voice, Guid soundId, byte[] bytes, bool loop);

    /// <summary>Stops a voice (nothing when it is not playing).</summary>
    void Stop(int voice);

    /// <summary>Where a voice is (metres, in the frame of the agent's region) and its gain (distance included, the master level not).</summary>
    void Place(int voice, Vector3 position, float gain);

    /// <summary>Whether a voice is still playing.</summary>
    bool IsPlaying(int voice);

    /// <summary>How far into its sound a voice is, in seconds (a loop going round shows as this going back).</summary>
    double PlaybackPosition(int voice);
}

/// <summary>Where a thing is: its region and a point local to that region.</summary>
public readonly record struct SoundPlace(ulong RegionHandle, Vector3 Local);

/// <summary>What a source is for the checks: a one-shot, an object's own sound, a footstep, or someone typing.</summary>
public enum WorldSoundOrigin { Trigger, Attached, Step, Typing }

/// <summary>Where a source stands: waiting for its sound, waiting for its sync master, playing, silent (and why), or finished.</summary>
public enum WorldSoundState { Fetching, WaitingSync, Playing, Silent, Done }

/// <summary>One source as the automation and the tests see it.</summary>
public sealed record WorldSoundView(int Voice, WorldSoundOrigin Origin, Guid ObjectId, Guid SoundId, WorldSoundState State, Vector3 Position,
    float Gain, bool Loop, string? Why);

/// <summary>One decision about a message or an event, for the checks: what came in and what became of it.</summary>
public sealed record WorldSoundDecision(WorldSoundOrigin Origin, Guid ObjectId, Guid SoundId, string Outcome);

/// <summary>
/// Sounds in the world, as the official viewer plays them (see each rule's comment): one-shots from the region (SoundTrigger), each
/// object's own sound (AttachedSound and the sound fields of object updates: play, loop, queue, sync, stop, gain changes), preloads,
/// footsteps and other avatars' typing. It applies the parcel rule, the block list's object-sound mutes, cut-off radii, the channel
/// limit and the distance fall-off, fetches each sound once through the grid's sounds, and tells the engine side what to play where.
/// Main thread only: <see cref="Receive"/> takes the session's events, <see cref="Update"/> runs every frame.
/// </summary>
public sealed partial class WorldSounds
{
    /// <summary>How long an object's sound waits for an object the viewer does not know yet.</summary>
    public const double PostponeSeconds = 15;

    /// <summary>How long a one-shot that never got a channel lives; a silenced one; a playing non-loop at most.</summary>
    public const double MaxUnplayedSeconds = 15, MaxMutedSeconds = 11, MaxAgeSeconds = 60;

    /// <summary>How long an object's sound stays after its object has gone from view.</summary>
    public const double ObjectGoneSeconds = 5;

    /// <summary>How many decisions <see cref="Recent"/> keeps.</summary>
    public const int RecentLimit = 256;

    // the members are in WorldSounds.Engine.cs
}
