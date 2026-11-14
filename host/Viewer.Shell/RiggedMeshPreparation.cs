// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Viewer.Core.Meshing;

namespace Viewer.Shell;

/// <summary>A worn rigged mesh's level of detail ready for the main thread: its surfaces in the engine's layout with each vertex's joints
/// and weights, and its table of bind matrices and joint nodes (the rows a skinning shader reads, <see cref="SkinTables"/>).</summary>
public sealed record PreparedRiggedLod(IReadOnlyList<PreparedSurface> Surfaces, float[] Matrices, float[] Nodes) : PreparedMeshLod(Surfaces);

/// <summary>
/// The data work of a worn rigged mesh, made off the main thread (on <see cref="MeshLodPreparer"/>'s workers): the surface of every face
/// with its influences (<see cref="SkinnedSurfaceData"/>) and the mesh's table (<see cref="SkinTables.BuildTable"/>). What stays for the main thread
/// is the engine calls: creating the mesh, adding its surfaces and writing the table into the atlas texture. Reads only; safe on any thread.
/// </summary>
public static class RiggedMeshPreparation
{
    public static PreparedRiggedLod Prepare(IReadOnlyList<MeshFace> faces, IReadOnlyList<FaceInfluences> influences, AvatarRig rig, MeshSkinBlock skin)
    {
        if (faces.Count != influences.Count) throw new ArgumentException("a face needs its influences");
        var surfaces = new PreparedSurface[faces.Count];
        for (int f = 0; f < surfaces.Length; f++) surfaces[f] = SkinnedSurfaceData.Encode(faces[f], influences[f]);
        int n = skin.UsableJoints;
        var matrices = new float[n * SkinTables.FloatsPerMatrix];
        var nodes = new float[n];
        SkinTables.BuildTable(rig, skin, matrices, nodes);
        return new PreparedRiggedLod(surfaces, matrices, nodes);
    }
}
