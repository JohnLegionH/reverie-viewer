// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>
/// Sitting on an object, as the official viewer does it. Written from a description of its behaviour; none of its code is copied.
/// <para>The sit request's offset (llviewermenu.cpp handle_object_sit sends the pick's object offset, which llagentcamera.cpp
/// calcFocusOffset makes): not the surface point that was clicked, but a point the camera can "tumble" about. Of the object's three
/// axes, the one whose plane through the object's centre faces the camera most squarely is chosen (the camera direction in the object's
/// frame, each part divided by the object's size along it; the largest wins, x before y before z on a tie). The pointer's ray meets that
/// plane; the point is pulled back along the camera's line of sight into the object's box where it lies outside (by the axis that lies
/// furthest outside), and turned back into region axes. Then, unless the camera is inside the object's box, it moves towards the clicked
/// surface point by a share that grows with how deep the surface point lies along the camera's view compared with its distance: none at a
/// tenth or less, all of it at seven tenths or more. A region without a sit target on the object places the avatar from this point (OpenSim
/// puts the avatar's centre half its physics box above it), so the point decides how high the avatar sits.</para>
/// <para>A seated avatar's root (llvoavatar.cpp updateRootPositionAndRotation, its branch for an avatar sitting on an object): exactly
/// the place the region states for it relative to the seat, plus the hover offset turned by the avatar's rotation. The standing branch's
/// terms (half the body's size, the pelvis-to-foot height, the ground under it, the Hover parameter) are not used while seated; the pose
/// then comes from the sit animation alone.</para>
/// </summary>
public static class SeatPlacement
{
    /// <summary>The offset a sit request carries: where on the object the click points, from the object's position, in region axes.
    /// <paramref name="cameraAt"/> is the camera's view direction; <paramref name="hit"/> the clicked surface point on the pointer's ray.</summary>
    public static Vector3 SitOffset(Vector3 objectPosition, Quaternion objectRotation, Vector3 objectScale, Vector3 cameraOrigin, Vector3 cameraAt, Vector3 hit)
    {
        var inverse = Quaternion.Inverse(objectRotation);
        var extents = Vector3.Max(objectScale, new Vector3(0.001f));
        var surface = hit - objectPosition;

        // the plane through the centre that faces the camera most squarely
        var toObject = Vector3.Transform(objectPosition - cameraOrigin, inverse);
        if (toObject.LengthSquared() > 0) toObject = Vector3.Normalize(toObject);
        var share = Vector3.Abs(toObject / extents);
        var axis = Largest(share);
        var normal = Vector3.Transform(axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ, objectRotation);

        // where the pointer's ray meets it (llviewerwindow.cpp mousePointOnPlaneGlobal): along the ray by the plane's distance over the
        // ray's share of the normal; a ray that runs along the plane is scaled by the distance to the plane's point over the ray's share of
        // the direction to that point instead
        var ray = hit - cameraOrigin;
        if (ray.LengthSquared() > 0) ray = Vector3.Normalize(ray);
        var toPlane = objectPosition - cameraOrigin;
        float along = Vector3.Dot(normal, ray);
        float scale = Vector3.Dot(normal, toPlane) / along;
        if (MathF.Abs(along) < 0.00001f)
            scale = toPlane.Length() / Vector3.Dot(toPlane.LengthSquared() > 0 ? Vector3.Normalize(toPlane) : Vector3.Zero, ray);
        var focus = cameraOrigin + scale * ray;

        // pulled back along the line of sight into the object's box
        var sight = Vector3.Transform(focus - cameraOrigin, inverse);
        var local = Vector3.Transform(focus - objectPosition, inverse);
        var clip = Vector3.Zero;
        for (int i = 0; i < 3; i++)
        {
            float v = local[i], half = extents[i] / 2;
            float outside = v > 0 ? MathF.Max(0, v - half) : MathF.Min(0, v + half);
            clip[i] = MathF.Abs(sight[i]) < 1e-4f ? 0 : outside / sight[i];
        }
        local -= clip[Largest(Vector3.Abs(clip))] * sight;
        var offset = Vector3.Transform(local, objectRotation);

        // towards the clicked surface by how deep it lies along the view, unless the camera is inside the object
        var inCamera = Vector3.Transform(cameraOrigin - objectPosition, inverse);
        bool inside = MathF.Abs(inCamera.X) <= extents.X / 2 && MathF.Abs(inCamera.Y) <= extents.Y / 2 && MathF.Abs(inCamera.Z) <= extents.Z / 2;
        if (inside)
            return surface;
        float depth = cameraAt.LengthSquared() > 0 ? MathF.Abs(Vector3.Dot(surface, Vector3.Normalize(cameraAt))) : 0;
        float distance = Vector3.Distance(hit, cameraOrigin);
        float bias = distance > 0 ? Math.Clamp((depth / distance - 0.1f) / 0.6f, 0f, 1f) : 0f;
        return Vector3.Lerp(offset, surface, bias);
    }

    /// <summary>Where a seated avatar's root (the pelvis at rest) goes: <paramref name="seatPlace"/>, the place the region states for it
    /// carried into the region's frame through its seat, plus the hover offset <paramref name="hover"/> (metres up the avatar's own z)
    /// turned by the avatar's drawn <paramref name="rotation"/>.</summary>
    public static Vector3 SeatedRoot(Vector3 seatPlace, Quaternion rotation, float hover) =>
        seatPlace + Vector3.Transform(new Vector3(0, 0, hover), rotation);

    // the index of the largest part: x before y before z on a tie, as a strict comparison chain gives it
    private static int Largest(Vector3 v) => v.X > v.Y && v.X > v.Z ? 0 : v.Y > v.Z ? 1 : 2;


    /// <summary>The grid's built-in sit animations (llanimationstates.cpp: sit, sit_female, sit_generic, sit_ground, sit_ground_constrained).</summary>
    public static readonly IReadOnlySet<Guid> DefaultSits = new HashSet<Guid>
    {
        Guid.Parse("1a5fe8ac-a804-8a5d-7cbd-56bd83184568"), Guid.Parse("b1709c8d-ecd3-54a1-4f28-d55ac0840782"),
        Guid.Parse("245f3c54-f1c0-bf2e-811f-46d8eeb386e7"), Guid.Parse("1c7600d6-661f-b87b-efe2-d7421eb93c86"),
        Guid.Parse("1a2bd58e-87ff-0df8-0b4c-53e047b0bb6e"),
    };

    /// <summary>What kind of sit animation <paramref name="animation"/> is: "default" (a built-in sit), "ao" (one the animation overrider
    /// started), "other" (a seat's own, or anything else), "none" (no animation).</summary>
    public static string SitKind(Guid animation, bool startedByAo) =>
        animation == Guid.Empty ? "none" : DefaultSits.Contains(animation) ? "default" : startedByAo ? "ao" : "other";
}
