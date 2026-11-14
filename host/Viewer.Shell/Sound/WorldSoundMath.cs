// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>Which level a sound in the world plays under: object sounds (the Sounds slider) or ambient ones (footsteps, wind).</summary>
public enum WorldSoundKind { Object, Ambient }

/// <summary>
/// The level and distance rules for sounds in the world, as the official viewer sets its audio engine up: a source's gain (0 to 1) times
/// its kind's level, an inverse-distance fall-off clamped at the reference distance (1 m) with rolloff factor 1 (5 with the camera under
/// water) and no far limit, a per-object cut-off radius, and a channel limit filled by priority (gain over the squared distance).
/// </summary>
public static class WorldSoundMath
{
    public const float ReferenceDistance = 1f;
    public const float Rolloff = 1f;
    public const float UnderwaterRolloff = 5f;
    public const float DopplerFactor = 1f;
    public const int MaxChannels = 30;

    /// <summary>A cut-off radius this small or smaller means no cut-off.</summary>
    public const float NoCutoffBelow = 0.1f;

    /// <summary>The fall-off with distance (1 at the reference distance and nearer).</summary>
    public static float DistanceGain(float distance, bool underwater)
    {
        // OpenAL's inverse distance, clamped: nearer than the reference distance counts as the reference distance
        float d = MathF.Max(distance, ReferenceDistance);
        float rolloff = underwater ? UnderwaterRolloff : Rolloff;
        return ReferenceDistance / (ReferenceDistance + rolloff * (d - ReferenceDistance));
    }

    /// <summary>A source's gain before distance: its own gain, clamped to 0..1, times its kind's level (0 when that kind is muted).</summary>
    public static float ChannelGain(float sourceGain, float kindLevel, bool kindMuted) =>
        kindMuted || !float.IsFinite(sourceGain) ? 0f : Math.Clamp(sourceGain, 0f, 1f) * Math.Clamp(kindLevel, 0f, 1f);

    /// <summary>The order sources get channels in: louder and nearer first.</summary>
    public static float Priority(float sourceGain, float distance) => sourceGain / MathF.Max(1f, distance * distance);

    /// <summary>Whether a source at this distance is inside its object's cut-off radius (always, for no cut-off).</summary>
    public static bool InsideCutoff(float distance, float cutoffRadius) => cutoffRadius <= NoCutoffBelow || distance < cutoffRadius;
}
