// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell.Build;

/// <summary>Where the mirror plane stands: through the middle of the mirrored parts' box, or through the selection's pivot point.</summary>
public enum MirrorPlane { SelectionCentre, Pivot }

/// <summary>One part's place and turn in the region (<see cref="Parent"/> is 0 for a root, else the local id of its root).</summary>
public readonly record struct MirrorPart(uint Local, uint Parent, Vector3 Position, Quaternion Rotation);

/// <summary>
/// The Mirror row's rules as pure arithmetic. A part's place goes to its image on the other side of the plane. A reflection is not a
/// rotation, so a part's turn is replaced by the proper rotation that puts a part symmetric about its own middle planes (a box, a cylinder,
/// a sphere, most prims) onto the image of the old one: the image of the part's frame is a left-handed frame, and flipping the local axis
/// that lies most nearly along the plane's normal makes it right-handed again, which leaves an unturned part unturned. Mirroring twice
/// therefore gives the starting place and turn back. A part that is not symmetric (a sculpt, a mesh, a twisted or sheared prim) is only
/// placed this way; its shape is the next step's work. Our own code, no source adapted.
/// </summary>
public static class MirrorRules
{
    /// <summary>Whether Mirror is offered: something to mirror, none of it worn, every part movable by the agent.</summary>
    public static ArrangeRefusal Refusal(int parts, bool anyAttachment, bool allMovable) =>
        anyAttachment ? ArrangeRefusal.Attachment : parts == 0 ? ArrangeRefusal.TooFew : !allMovable ? ArrangeRefusal.NoMove : ArrangeRefusal.None;

    /// <summary>The plane's normal: the chosen axis of the grid's frame, in the region's frame.</summary>
    public static Vector3 Normal(ArrangeAxis axis, Quaternion frame) =>
        Vector3.Normalize(Vector3.Transform(axis switch { ArrangeAxis.X => Vector3.UnitX, ArrangeAxis.Y => Vector3.UnitY, _ => Vector3.UnitZ }, frame));

    /// <summary>The image of a place about the plane through <paramref name="point"/> with the given normal.</summary>
    public static Vector3 Reflect(Vector3 position, Vector3 point, Vector3 normal)
    {
        var n = Vector3.Normalize(normal);
        return position - 2f * Vector3.Dot(position - point, n) * n;
    }

    /// <summary>The turn of the image of a part turned by <paramref name="rotation"/>: the reflection of the part's frame, with the local
    /// axis most nearly along the normal flipped so that the result is a proper rotation (the lowest axis wins a tie).</summary>
    public static Quaternion Rotation(Quaternion rotation, Vector3 normal)
    {
        var n = Vector3.Normalize(normal);
        var q = Quaternion.Normalize(rotation);
        var axes = new[] { Vector3.Transform(Vector3.UnitX, q), Vector3.Transform(Vector3.UnitY, q), Vector3.Transform(Vector3.UnitZ, q) };
        float best = axes.Max(a => MathF.Abs(Vector3.Dot(a, n)));
        int flip = Array.FindIndex(axes, a => MathF.Abs(Vector3.Dot(a, n)) >= best - 1e-4f);
        var image = axes.Select(a => a - 2f * Vector3.Dot(a, n) * n).ToArray();
        image[flip] = -image[flip];
        var m = new Matrix4x4(image[0].X, image[0].Y, image[0].Z, 0, image[1].X, image[1].Y, image[1].Z, 0, image[2].X, image[2].Y, image[2].Z, 0, 0, 0, 0, 1);
        var turned = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        return turned.W < 0 ? new Quaternion(-turned.X, -turned.Y, -turned.Z, -turned.W) : turned;
    }

    /// <summary>Every part's image about the plane, in the same order with the same ids.</summary>
    public static IReadOnlyList<MirrorPart> Mirror(IReadOnlyList<MirrorPart> parts, Vector3 point, Vector3 normal) =>
        [.. parts.Select(p => p with { Position = Reflect(p.Position, point, normal), Rotation = Rotation(p.Rotation, normal) })];
}
