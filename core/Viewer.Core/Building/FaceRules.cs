// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;

namespace Viewer.Core.Building;

/// <summary>Everything the Texture tab sets on one face: texture, colour and alpha, repeats, offset, rotation (radians), mapping,
/// full bright and glow.</summary>
public readonly record struct FaceSettings(Guid TextureId, Rgba Colour, float RepeatU, float RepeatV, float OffsetU, float OffsetV, float Rotation,
    TexGen TexGen, bool Fullbright, float Glow);

/// <summary>A change made on the Texture tab: only the fields given change; the rest of each face stays as it is.</summary>
/// <param name="Colour">The colour's red, green and blue, 0..1 (the alpha is <paramref name="Transparency"/>'s).</param>
/// <param name="Transparency">Percent, 0 (opaque) to 100.</param>
public sealed record FaceChange(Guid? TextureId = null, Vector3? Colour = null, float? Transparency = null, float? Glow = null, bool? Fullbright = null,
    TexGen? TexGen = null, float? RepeatU = null, float? RepeatV = null, float? OffsetU = null, float? OffsetV = null, float? RotationDegrees = null)
{
    public FaceSettings ApplyTo(FaceSettings f) => f;
}

/// <summary>One face's settings as an edit sends them.</summary>
public readonly record struct FaceEdit(int Face, FaceSettings Settings);

/// <summary>The face rules of the Texture tab.</summary>
public static class FaceRules
{
    /// <summary>A face's settings as the world holds them.</summary>
    public static FaceSettings Settings(ObjectState o, int face) => default;

    public static (float U, float V) RepeatsForPerMetre(float perMetre, int face, Vector3 scale) => (0, 0);

    public static float PerMetre(float repeatsU, int face, Vector3 scale) => 0;

    public static Vector2 PlanarUv(Vector3 objectPosition, Quaternion objectRotation, Vector3 localNormal, FaceSettings f, Vector3 world) => Vector2.Zero;
}
