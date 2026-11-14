// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
// Part of Viewer.Motion, which is licensed LGPL-2.1 only (see NOTICE.md). This file has no Linden source.
using System.Numerics;

namespace Viewer.Motion;

/// <summary>Where one avatar is drawn between the region's updates. Implemented by <see cref="ObjectMotion"/> (the official viewer's
/// interpolation, used for the user's own avatar) and by the older smoothing in Viewer.Core (other avatars). Times are seconds on the
/// caller's clock; not thread-safe.</summary>
public interface IAvatarMotion
{
    /// <summary>The region stated this position, velocity and rotation at <paramref name="now"/>.</summary>
    void Update(Vector3 position, Vector3 velocity, Quaternion rotation, double now);

    /// <summary>As <see cref="Update"/>, taken at once with no smoothing (the position changed frame: a sit or a stand).</summary>
    void Snap(Vector3 position, Vector3 velocity, Quaternion rotation, double now);

    /// <summary>Where the avatar is drawn at <paramref name="now"/>, and its rotation.</summary>
    (Vector3 Position, Quaternion Rotation) At(double now);

    /// <summary>The velocity the drawn position is carried at.</summary>
    Vector3 Velocity(double now);

    /// <summary>The position the region last stated, before any smoothing.</summary>
    Vector3 Stated { get; }

    /// <summary>Updates received, and how many of them were taken at once rather than smoothed (the first, and sits and stands).</summary>
    int Updates { get; }
    int Snaps { get; }

    /// <summary>Stops carrying the avatar along its velocity until the region says it stopped. Not called when a movement key
    /// comes up: a released avatar coasts on the region's updates.</summary>
    void Halt(double now);

    /// <summary>A movement key is held again: updates are taken as they come.</summary>
    void Resume();

    bool Halted { get; }
}
