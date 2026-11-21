// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;

namespace Viewer.Core.Camera;

/// <summary>What part of a target the camera looks at.</summary>
public enum AimPoint { Head, Chest, Body, Centre, Feet }

/// <summary>A camera pose in region coordinates (x east, y north, z up) and its vertical field of view in degrees.</summary>
public readonly record struct AimPose(Vector3 Eye, Vector3 Target, float FovDeg);

/// <summary>A box on the screen in pixels (top left is 0,0). <see cref="InFront"/> is false when some corner of the target is behind the camera.</summary>
public readonly record struct ScreenBox(float Left, float Top, float Right, float Bottom, bool InFront)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
}

/// <summary>What <see cref="CameraAim.Check"/> found: how much of the frame the target fills, how far from the middle it is, and why it failed.</summary>
public readonly record struct FrameReport(float Fill, float VisibleShare, float CentreOffset, bool Pass, string[] Reasons);

/// <summary>
/// Aiming a test camera at a person or an object: where the eye goes for a given distance, angle around and angle above, where the
/// target lands on the screen, and whether a picture taken from there shows the target large enough and in the middle. Pure
/// arithmetic, so the live scripts' pictures can be judged without opening them.
/// </summary>
public static class CameraAim
{
    /// <summary>The yaw (radians, counter-clockwise from east) an avatar's rotation faces.</summary>
    public static float YawOf(Quaternion rotation)
    {
        var forward = Vector3.Transform(Vector3.UnitX, rotation);
        return MathF.Atan2(forward.Y, forward.X);
    }

    // The default skeleton's head, from the character definition (avatar_skeleton.xml): the head joint stands at the sum of the joints' heights up the chain
    // (pelvis, torso, chest, neck, head: 1.682703 m above the soles) and the skull joint 0.079 m above it with its end 0.033 m further (1.794703 m). The
    // official viewer aims its customise-avatar camera at the head joint (llagentcamera.cpp, mHeadp->getWorldPosition()); that joint is the base of the
    // head, so a camera aimed at it frames the neck and shoulders. The aim command's head point is the middle of the head: halfway from the head joint to the top of the skull.
    private const float HeadJointHeight = 1.682703f, SkullTopHeight = 1.794703f;

    /// <summary>The middle and half sizes of the box that stands for the part of an avatar the camera aims at.</summary>
    public static (Vector3 Centre, Vector3 HalfExtents) AvatarBox(AimPoint kind, Vector3 soles, float height)
    {
        float k = height / 1.8f;   // the sizes below are for a person 1.8 m tall and grow with the height
        return kind switch
        {
            AimPoint.Head => (soles + new Vector3(0, 0, height * ((HeadJointHeight + SkullTopHeight) / 2f / SkullTopHeight)), new Vector3(0.11f, 0.11f, 0.13f) * k),
            AimPoint.Chest => (soles + new Vector3(0, 0, height * 0.72f), new Vector3(0.2f, 0.14f, 0.25f) * k),
            AimPoint.Feet => (soles + new Vector3(0, 0, 0.12f * k), new Vector3(0.22f, 0.22f, 0.12f) * k),   // both shoes, from the soles to above the ankles
            _ => (soles + new Vector3(0, 0, height * 0.5f), new Vector3(0.25f * k, 0.2f * k, height * 0.5f)),
        };
    }

    /// <summary>The eight corners of a box with the given middle and half sizes, turned by yaw about the vertical axis.</summary>
    public static Vector3[] Corners(Vector3 centre, Vector3 halfExtents, float yawRad)
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawRad);
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3((i & 1) == 0 ? -halfExtents.X : halfExtents.X, (i & 2) == 0 ? -halfExtents.Y : halfExtents.Y, (i & 4) == 0 ? -halfExtents.Z : halfExtents.Z);
            corners[i] = centre + Vector3.Transform(local, turn);
        }
        return corners;
    }

    /// <summary>
    /// The camera pose that looks at <paramref name="target"/> from <paramref name="distance"/> away. <paramref name="aroundDeg"/> is
    /// measured from the direction the target faces (0 in front of it, 90 on its left, 180 behind it), <paramref name="aboveDeg"/> is
    /// the angle above the horizontal (negative looks up at the target).
    /// </summary>
    public static AimPose Pose(Vector3 target, float distance, float aroundDeg, float aboveDeg, float facingYawRad, float fovDeg)
    {
        float around = facingYawRad + float.DegreesToRadians(aroundDeg), above = float.DegreesToRadians(aboveDeg);
        var direction = new Vector3(MathF.Cos(above) * MathF.Cos(around), MathF.Cos(above) * MathF.Sin(around), MathF.Sin(above));
        return new AimPose(target + direction * distance, target, fovDeg);
    }

    /// <summary>Where a point lands on a frame of the given size, in pixels; false when it is behind the camera.</summary>
    public static bool Project(AimPose pose, float frameWidth, float frameHeight, Vector3 point, out Vector2 pixel)
    {
        pixel = default;
        var forward = Vector3.Normalize(pose.Target - pose.Eye);
        var right = Vector3.Cross(forward, Vector3.UnitZ);
        right = right.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(right);   // looking straight up or down
        var up = Vector3.Cross(right, forward);
        var d = point - pose.Eye;
        float depth = Vector3.Dot(d, forward);
        if (depth <= 0.01f)
            return false;
        float tanHalf = MathF.Tan(float.DegreesToRadians(pose.FovDeg) / 2f);
        float aspect = frameWidth / frameHeight;
        float ndcX = Vector3.Dot(d, right) / (depth * tanHalf * aspect), ndcY = Vector3.Dot(d, up) / (depth * tanHalf);
        pixel = new Vector2((ndcX * 0.5f + 0.5f) * frameWidth, (0.5f - ndcY * 0.5f) * frameHeight);
        return true;
    }

    /// <summary>The smallest screen box holding all the points.</summary>
    public static ScreenBox BoxOf(AimPose pose, float frameWidth, float frameHeight, IEnumerable<Vector3> points)
    {
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        bool allInFront = true, any = false;
        foreach (var p in points)
        {
            if (!Project(pose, frameWidth, frameHeight, p, out var px))
            {
                allInFront = false;
                continue;
            }
            any = true;
            left = MathF.Min(left, px.X); right = MathF.Max(right, px.X);
            top = MathF.Min(top, px.Y); bottom = MathF.Max(bottom, px.Y);
        }
        return any ? new ScreenBox(left, top, right, bottom, allInFront) : new ScreenBox(0, 0, 0, 0, false);
    }

    /// <summary>
    /// Judges a target's box in a frame. It fails with the reasons "behind_camera", "too_small" (the box's longer side fills less than
    /// <paramref name="minFill"/> of the frame side it runs along), "cut_off" (less than 90 % of the box is inside the frame) and
    /// "off_centre" (the box's middle is further than <paramref name="maxCentreOffset"/> of the frame size from the frame's middle).
    /// </summary>
    public static FrameReport Check(ScreenBox box, float frameWidth, float frameHeight, float minFill, float maxCentreOffset)
    {
        if (!box.InFront)
            return new FrameReport(0f, 0f, 1f, false, ["behind_camera"]);
        float fill = MathF.Max(box.Width / frameWidth, box.Height / frameHeight);
        float insideW = MathF.Max(0f, MathF.Min(box.Right, frameWidth) - MathF.Max(box.Left, 0f));
        float insideH = MathF.Max(0f, MathF.Min(box.Bottom, frameHeight) - MathF.Max(box.Top, 0f));
        float area = box.Width * box.Height;
        float visible = area <= 1e-6f ? (insideW > 0 || insideH > 0 ? 1f : 0f) : insideW * insideH / area;
        float offset = MathF.Max(MathF.Abs((box.Left + box.Right) / 2f - frameWidth / 2f) / frameWidth, MathF.Abs((box.Top + box.Bottom) / 2f - frameHeight / 2f) / frameHeight);
        var reasons = new List<string>();
        if (fill < minFill) reasons.Add("too_small");
        if (visible < 0.9f) reasons.Add("cut_off");
        if (offset > maxCentreOffset) reasons.Add("off_centre");
        return new FrameReport(fill, visible, offset, reasons.Count == 0, reasons.ToArray());
    }
}
