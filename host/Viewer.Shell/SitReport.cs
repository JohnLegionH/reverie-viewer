// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell;

/// <summary>Where the posed skeleton of a seated avatar is, in the region's frame (metres): the root the seat places, the pelvis, both hip
/// and knee joints, and both soles (the foot joints, which rest on the soles).</summary>
public sealed record SeatedPose(Vector3 Root, Vector3 Pelvis, Vector3 HipLeft, Vector3 HipRight, Vector3 KneeLeft, Vector3 KneeRight, Vector3 SoleLeft, Vector3 SoleRight);

/// <summary>
/// One line about a sit on an object, written once per sit (avatar.sit), so a seat that looks too high or too low can be read back from
/// the log: whether the object has a sit target, the offset the sit request sent, where the region put the avatar relative to the seat
/// (position and turn), the top of the prim under it, heights over that top measured from the posed skeleton (root, pelvis, knees, soles),
/// the thighs' angle from the level, the body's own height and the sit animation playing with its priority. Numbers only; no names.
/// </summary>
public sealed record SitReport(
    bool SitTarget,
    Vector3 SentOffset,
    Vector3 SeatPosition,
    Quaternion SeatRotation,
    float? PrimTop,
    SeatedPose Pose,
    float BodyHeight,
    string Animation,
    string AnimationKind,
    int? AnimationPriority)
{
    /// <summary>The angle of a thigh from the level, in degrees: 0 level, 90 hanging straight down (negative: the knee above the hip).</summary>
    public static float ThighAngleDeg(Vector3 hip, Vector3 knee)
    {
        var d = knee - hip;
        float across = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
        return across == 0 && d.Z == 0 ? 0f : MathF.Atan2(-d.Z, across) * 180f / MathF.PI;
    }

    /// <summary>The line's fields, in order, as name and value (heights over the top are null when no prim top was found under the avatar).
    /// Knees and soles are the mean of the two sides; the thigh angle is the mean of the two thighs.</summary>
    public IReadOnlyList<(string Key, object? Value)> Fields()
    {
        var forward = Vector3.Transform(Vector3.UnitX, SeatRotation);
        float yaw = MathF.Atan2(forward.Y, forward.X) * 180f / MathF.PI;
        float knee = (Pose.KneeLeft.Z + Pose.KneeRight.Z) / 2, sole = (Pose.SoleLeft.Z + Pose.SoleRight.Z) / 2;
        float thigh = (ThighAngleDeg(Pose.HipLeft, Pose.KneeLeft) + ThighAngleDeg(Pose.HipRight, Pose.KneeRight)) / 2;
        object? Over(float z) => PrimTop is float t ? R(z - t) : null;
        return
        [
            ("sit_target", SitTarget),
            ("sent_x", R(SentOffset.X)), ("sent_y", R(SentOffset.Y)), ("sent_z", R(SentOffset.Z)),
            ("seat_x", R(SeatPosition.X)), ("seat_y", R(SeatPosition.Y)), ("seat_z", R(SeatPosition.Z)),
            ("seat_yaw_deg", MathF.Round(yaw, 1)),
            ("prim_top", PrimTop is float top ? R(top) : null),
            ("root_over_top", Over(Pose.Root.Z)), ("pelvis_over_top", Over(Pose.Pelvis.Z)), ("knee_over_top", Over(knee)), ("sole_over_top", Over(sole)),
            ("thigh_deg", MathF.Round(thigh, 1)),
            ("body_height", R(BodyHeight)),
            ("animation", Animation), ("animation_kind", AnimationKind), ("animation_priority", AnimationPriority),
        ];
    }

    private static float R(float v) => MathF.Round(v, 3);
}
