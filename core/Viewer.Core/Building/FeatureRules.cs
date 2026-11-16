// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Building;

/// <summary>A flexible prim's settings (the Features tab's Flexible Path): softness is the number of sections, the rest are the forces
/// the viewer animates it with.</summary>
public readonly record struct FlexibleParams(int Softness, float Gravity, float Drag, float Wind, float Tension, Vector3 Force)
{
    /// <summary>The settings a prim gets when Flexible Path is ticked: two sections, gravity 0.3, drag 2, no wind, tension 1, no force.</summary>
    public static readonly FlexibleParams Default = new(2, 0.3f, 2f, 0f, 1f, Vector3.Zero);
}

/// <summary>A prim's light: its colour, intensity (0 to 1), radius in metres and falloff.</summary>
public readonly record struct LightParams(Vector3 Color, float Intensity, float Radius, float Falloff)
{
    /// <summary>The light a prim gets when Light is ticked: white, full intensity, 10 m, falloff 0.75.</summary>
    public static readonly LightParams Default = new(Vector3.One, 1f, 10f, 0.75f);
}

/// <summary>A light's projector: the image it throws (empty for none), its field of view in radians, focus and ambiance.</summary>
public readonly record struct ProjectorParams(Guid Texture, float Fov, float Focus, float Ambiance)
{
    /// <summary>A projector with no image: a quarter-turn field of view, no focus, no ambiance.</summary>
    public static readonly ProjectorParams Default = new(Guid.Empty, MathF.PI / 2f, 0f, 0f);
}

/// <summary>A prim's physics settings: the shape the physics engine uses, its gravity multiplier, friction, density in kg/m³ and
/// restitution.</summary>
public readonly record struct PhysicsParams(byte ShapeType, float Gravity, float Friction, float Density, float Restitution)
{
    /// <summary>A prim's physics settings before anything is changed: the prim's own shape, normal gravity, friction 0.6, the density of
    /// water and restitution 0.5.</summary>
    public static readonly PhysicsParams Default = new(FeatureRules.ShapePrim, 1f, 0.6f, 1000f, 0.5f);
}

/// <summary>Everything the Features tab shows for one prim. <see cref="Flexible"/>, <see cref="Light"/> and <see cref="Projector"/> are null
/// when the prim has none.</summary>
public sealed record ObjectFeatures(FlexibleParams? Flexible, LightParams? Light, ProjectorParams? Projector, PhysicsParams Physics, int Material,
    bool CanFlex, bool Physical);

/// <summary>
/// The ranges the Features tab holds its values to, and its lists, as the official viewer has them (llprimitive.cpp's flexible and light
/// limits, floater_tools.xml's Features spinners and lists; read as behaviour).
/// </summary>
public static class FeatureRules
{
    /// <summary>The physics shape types: the prim's own shape, none (the prim is left out of the linkset's physics shape), a convex hull.</summary>
    public const byte ShapePrim = 0, ShapeNone = 1, ShapeConvex = 2;

    /// <summary>The materials, in the protocol's numbering (Stone 0 to Rubber 6), with the string-table keys the list shows.</summary>
    public static readonly IReadOnlyList<string> Materials = ["stone", "metal", "glass", "wood", "flesh", "plastic", "rubber"];

    /// <summary>The flexible settings held to their ranges: softness 0 to 3, gravity -10 to 10, drag, wind and tension 0 to 10, force -10 to 10.</summary>
    public static FlexibleParams Clamp(FlexibleParams f) => f;

    /// <summary>The light held to its ranges: each colour channel and the intensity 0 to 1, radius 0 to 20 m, falloff 0 to 2.</summary>
    public static LightParams Clamp(LightParams l) => l;

    /// <summary>The projector held to its ranges: field of view 0 to 3, focus -20 to 20, ambiance 0 to 1.</summary>
    public static ProjectorParams Clamp(ProjectorParams p) => p;

    /// <summary>The physics settings held to their ranges: a known shape type, gravity -1 to 28, friction 0 to 255, density 1 to 22587,
    /// restitution 0 to 1.</summary>
    public static PhysicsParams Clamp(PhysicsParams p) => p;

    /// <summary>The shape's path curve once Flexible Path is ticked (the flexible path) or cleared (a straight line).</summary>
    public const byte PathFlexible = 0x80, PathLine = 0x10;

    /// <summary>Whether a prim can be made flexible: a prim that is not a sculpt or mesh, whose path is a straight line or already flexible.</summary>
    public static bool CanFlex(Viewer.Core.Meshing.PrimShape? shape) => false;
}
