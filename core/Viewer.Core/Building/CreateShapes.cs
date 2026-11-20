// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Meshing;

namespace Viewer.Core.Building;

/// <summary>The fifteen buttons of the Create tool, in the order the official build floater lists them (floater_tools.xml, ToolCube to
/// ToolGrass; llfloatertools.cpp toolData).</summary>
public enum CreateShape { Cube, Prism, Pyramid, Tetrahedron, Cylinder, Hemicylinder, Cone, Hemicone, Sphere, Hemisphere, Torus, Tube, Ring, Tree, Grass }

/// <summary>What one Create button asks the region to make: the protocol's pcode, the volume it is made of (null for a tree or grass), the
/// turn it is made with, and whether it arrives selected.</summary>
public sealed record CreateShapeSpec(CreateShape Shape, byte PCode, PrimShape? Volume, Quaternion Rotation, bool CreateSelected)
{
    /// <summary>A tree or a patch of grass: a plant the region draws, not a volume.</summary>
    public bool IsPlant => Volume is null;
}

/// <summary>
/// The shapes the Create tool makes, written from the official placer (lltoolplacer.cpp, LLToolPlacer::addObject, lines 159-410, read as
/// behaviour): each volume's profile, path, cut and top size as that function sets them, the quarter turn about the Y axis that a sphere
/// and the three torus kinds are made with, and what a tree and a patch of grass send instead of a volume (a pcode and a random species).
/// Only volumes arrive selected (create_selected, lines 212-220); plants do not.
/// </summary>
public static class CreateShapes
{
    /// <summary>The protocol's pcodes (llvolume.h: LL_PCODE_VOLUME 9, LL_PCODE_LEGACY 15 with 0x50 for grass and 0xF0 for trees).</summary>
    public const byte PCodeVolume = 9, PCodeGrass = 0x5F, PCodeTree = 0xFF;

    /// <summary>How many species of tree and of grass the region may be asked for (the official viewer's trees.xml lists 21 and grass.xml 6;
    /// the placer picks one at random, lltoolplacer.cpp 200-209).</summary>
    public const int TreeSpecies = 21, GrassSpecies = 6;

    /// <summary>The shapes in the order the buttons stand.</summary>
    public static IReadOnlyList<CreateShape> All { get; } = Enum.GetValues<CreateShape>();

    private const int Circle = 0, Square = 1, EqualTriangle = 3, HalfCircle = 5;
    private const int Line = 0x10, CircularPath = 0x20;

    // sphere and torus kinds are made turned a quarter about the Y axis (setQuat(90 degrees, y_axis))
    private static readonly Quaternion QuarterY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

    private static PrimShape V(int profile, int path, double scaleX = 1, double scaleY = 1, double shearX = 0, double profileBegin = 0,
        double profileEnd = 1, double pathEnd = 1) =>
        new() { ProfileCurve = profile, PathCurve = path, PathScaleX = scaleX, PathScaleY = scaleY, PathShearX = shearX, ProfileBegin = profileBegin,
            ProfileEnd = profileEnd, PathEnd = pathEnd };

    /// <summary>The spec of a button.</summary>
    public static CreateShapeSpec Of(CreateShape shape) => shape switch
    {
        CreateShape.Cube => Volume(shape, V(Square, Line)),
        // ratio (0, 1) and shear (-0.5, 0): lines 322-330
        CreateShape.Prism => Volume(shape, V(Square, Line, scaleX: 0, shearX: -0.5)),
        CreateShape.Pyramid => Volume(shape, V(Square, Line, 0, 0)),
        CreateShape.Tetrahedron => Volume(shape, V(EqualTriangle, Line, 0, 0)),
        CreateShape.Cylinder => Volume(shape, V(Circle, Line)),
        // begin and end of the profile cut at a quarter and three quarters (lines 362-370)
        CreateShape.Hemicylinder => Volume(shape, V(Circle, Line, profileBegin: 0.25, profileEnd: 0.75)),
        CreateShape.Cone => Volume(shape, V(Circle, Line, 0, 0)),
        CreateShape.Hemicone => Volume(shape, V(Circle, Line, 0, 0, profileBegin: 0.25, profileEnd: 0.75)),
        CreateShape.Sphere => Volume(shape, V(HalfCircle, CircularPath), QuarterY),
        // half of the path (setBeginAndEndT(0, 0.5), lines 302-310), not turned
        CreateShape.Hemisphere => Volume(shape, V(HalfCircle, CircularPath, pathEnd: 0.5)),
        CreateShape.Torus => Volume(shape, V(Circle, CircularPath, scaleY: 0.25), QuarterY),
        CreateShape.Tube => Volume(shape, V(Square, CircularPath, scaleY: 0.25), QuarterY),
        CreateShape.Ring => Volume(shape, V(EqualTriangle, CircularPath, scaleY: 0.25), QuarterY),
        CreateShape.Tree => new(shape, PCodeTree, null, Quaternion.Identity, false),
        CreateShape.Grass => new(shape, PCodeGrass, null, Quaternion.Identity, false),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static CreateShapeSpec Volume(CreateShape shape, PrimShape v, Quaternion? turn = null) =>
        new(shape, PCodeVolume, v, turn ?? Quaternion.Identity, true);

    /// <summary>The size a plant is made at: a patch of grass has a random size (10 to 30 m across, 1 to 3 m high; line 202), a tree the
    /// default half metre. <paramref name="next"/> gives numbers from 0 to 1.</summary>
    public static Vector3 PlantScale(CreateShape shape, Func<float> next) =>
        shape == CreateShape.Grass ? new(10f + next() * 20f, 10f + next() * 20f, 1f + next() * 2f) : DefaultBox.Scale;

    /// <summary>The species a plant is made as: a random one of those the viewer knows (the state field of the message). Volumes send 0.</summary>
    public static byte Species(CreateShape shape, Func<float> next) => shape switch
    {
        CreateShape.Tree => (byte)Math.Min(TreeSpecies - 1, (int)(next() * TreeSpecies)),
        CreateShape.Grass => (byte)Math.Min(GrassSpecies - 1, (int)(next() * GrassSpecies)),
        _ => 0,
    };
}
