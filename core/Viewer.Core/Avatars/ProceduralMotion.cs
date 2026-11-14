// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>
/// A motion the avatar runs by itself, with no animation asset behind it (the official viewer's built-in motions: llvoavatar.cpp startDefaultMotions
/// and LLFlyAdjustMotion). It animates a few joints from the time since it started; <see cref="Priority"/> and the ease durations take part in the
/// blend as an animation's do, and an additive one is added on top of the blended pose (llpose.cpp LLJointStateBlender::blendJointStates).
/// </summary>
public interface IProceduralMotion
{
    string Name { get; }

    /// <summary>The joint priority, as an animation asset states it (0 low, 1 medium, 2 high, 3 higher, 4 highest).</summary>
    int Priority { get; }

    bool Additive { get; }
    float EaseIn { get; }
    float EaseOut { get; }

    /// <summary>The joints this motion turns, and the joints it moves.</summary>
    IReadOnlyList<string> RotationJoints { get; }
    IReadOnlyList<string> PositionJoints { get; }

    /// <summary>The joint's rotation and position <paramref name="t"/> seconds after the motion started.</summary>
    Quaternion Rotation(string joint, double t);
    Vector3 Position(string joint, double t);
}
