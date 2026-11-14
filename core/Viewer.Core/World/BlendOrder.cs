// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// The order blended (alpha-blend) faces are drawn in, as the official viewer orders them (its behaviour, written
/// from its documented rules in our own terms):
/// <list type="bullet">
/// <item>Every blended face is drawn on its own, farthest first. A face's distance is measured along the camera's view
/// axis to the centre of the face's box, moved toward the camera by a quarter of the box's size along that axis (a
/// face that is long along the view counts nearer than its centre). Several blended faces of one object, and the
/// prims of a linkset, are each placed by their own face; fullbright faces are ordered with the rest.</item>
/// <item>The water splits the order in two. A face with any part on the far side of the water from the camera is
/// drawn before the water, cut to that side; a face with any part on the camera's side is drawn after the water,
/// cut to that side. A face crossing the water is drawn twice, once for each side.</item>
/// </list>
/// Not reproduced: the official viewer sorts faces within each node of its scene tree and the nodes by their own
/// distance, so faces of different nodes whose distances interleave can come out of order there; here every
/// blended face is sorted against every other.
/// The engine draws its transparent list largest depth first; <see cref="Depth"/> gives each face's depth in that list,
/// with the water at <see cref="WaterDepth"/> and the far side beyond it.
/// </summary>
public static class BlendOrder
{
    /// <summary>A side of the water plane.</summary>
    public enum Side : byte { Above = 0, Below = 1 }

    /// <summary>The water's depth in the draw order: beyond every face on the camera's side (no face is this far).</summary>
    public const float WaterDepth = 4096f;

    /// <summary>Added to the depth of a face on the far side of the water, so it is drawn before the water.</summary>
    public const float FarSideDepth = 2 * WaterDepth;

    /// <summary>
    /// A face's distance in the order: along the view axis <paramref name="at"/> (unit length) from the eye to the
    /// centre of the face's box, less a quarter of the box's <paramref name="size"/> along that axis (per axis: size
    /// times the axis component squared).
    /// </summary>
    public static float Key(Vector3 eye, Vector3 at, Vector3 centre, Vector3 size) =>
        Vector3.Dot(centre - eye, at) - 0.25f * (size.X * at.X * at.X + size.Y * at.Y * at.Y + size.Z * at.Z * at.Z);

    /// <summary>The sides of the water a face spanning <paramref name="low"/>..<paramref name="high"/> (height) is drawn
    /// on: above when any part is at or above the water, below when any part is at or below it. Without water every
    /// face is drawn once, above.</summary>
    public static (bool Above, bool Below) Sides(float low, float high, float? water) =>
        water is not { } h ? (true, false) : (high >= h, low <= h);

    /// <summary>Whether a face on <paramref name="side"/> is on the far side of the water from an eye above or below it.</summary>
    public static bool FarSide(Side side, bool eyeAbove) => side == (eyeAbove ? Side.Below : Side.Above);

    /// <summary>A face's depth in the engine's transparent list (largest drawn first): its key, plus
    /// <see cref="FarSideDepth"/> on the far side of the water.</summary>
    public static float Depth(float key, Side side, bool eyeAbove) => FarSide(side, eyeAbove) ? FarSideDepth + key : key;

    /// <summary>The order the engine draws faces with these depths in: indices, largest depth first; equal depths keep
    /// their given order.</summary>
    public static int[] Order(IReadOnlyList<float> depths) =>
        Enumerable.Range(0, depths.Count).OrderByDescending(i => depths[i]).ThenBy(i => i).ToArray();
}
