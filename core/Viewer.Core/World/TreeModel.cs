// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Meshing;

namespace Viewer.Core.World;

/// <summary>A tree object as the protocol adapter reports it: its species is the first byte of the object's data.</summary>
public sealed record TreeData(uint LocalId, Guid FullId, int Species, Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>One species of the region's built-in trees: its texture and the numbers the shape comes from.</summary>
public sealed record TreeSpecies(string Name, Guid Texture, float Droop, float Twist, float Branches, int Depth, float ScaleStep, int TrunkDepth,
    float BranchLength, float TrunkLength, float LeafScale, float TrunkAspect, float BranchAspect, float Taper, float RepeatZ);

/// <summary>
/// Turns a tree object into the one prim that draws it: a mesh made by <see cref="TreeMesh"/> from the species (a cylinder for each trunk and
/// branch segment, four leaf quads at each branch tip, four trunk detail levels), wearing the species' texture on one face. The species'
/// numbers follow the official viewer's trees.xml and the shape follows llvotree.cpp (see <see cref="TreeMesh"/>); there is no wind.
/// </summary>
public static class TreeBuilder
{

    // from trees.xml: name, species id order, texture, droop, twist, branches, depth, scale step, trunk depth, branch length, trunk length, leaf scale,
    // trunk aspect, branch aspect, taper, trunk texture repeat
    private static readonly System.Collections.Immutable.ImmutableArray<TreeSpecies> Table =
    [
        new("Pine 1", Guid.Parse("0187babf-6c0d-5891-ebed-4ecab1426683"), 60f, 5f, 5f, 1, 0.7f, 6, 8f, 11.5f, 22f, 0.1f, 0.05f, 0.8f, 3f),
        new("Oak", Guid.Parse("8a515889-eac9-fb55-8eba-d2dc09eb32c8"), 35f, 3f, 4f, 3, 0.7f, 0, 3f, 3.8f, 7f, 0.15f, 0.07f, 0.3f, 4f),
        new("Tropical Bush 1", Guid.Parse("5bc11cd6-2f40-071e-a8da-0903394204f9"), 10f, 0f, 6f, 1, 0.5f, 1, 0.5f, 0.15f, 7.5f, 1f, 0.08f, 0.2f, 1f),
        new("Palm 1", Guid.Parse("ca4e8c27-473c-eb1c-2f5d-50ee3f07d85c"), 0f, 0f, 3f, 1, 0.5f, 0, 0.7f, 9f, 10f, 0.035f, 0.03f, 0.7f, 10f),
        new("Dogwood", Guid.Parse("64367bd1-697e-b3e6-0b65-3f862a577366"), 30f, 0f, 3f, 2, 0.7f, 1, 2.75f, 4f, 5.5f, 0.06f, 0.05f, 0.8f, 3f),
        new("Tropical Bush 2", Guid.Parse("cdd9a9fc-6d0b-f90d-8416-c72b6019bca8"), 10f, 0f, 3f, 1, 0.5f, 1, 0.5f, 0.15f, 6f, 1f, 0.08f, 0.2f, 1f),
        new("Palm 2", Guid.Parse("2d784476-d0db-9979-0cff-9408745a7cf3"), 0f, 0f, 3f, 1, 0.5f, 0, 0.7f, 10f, 7.5f, 0.035f, 0.03f, 0.6f, 12f),
        new("Cypress 1", Guid.Parse("fb2ae204-3fd1-df33-594f-c9f882830e66"), 30f, 0f, 3f, 4, 0.5f, 0, 10f, 10f, 70f, 0.05f, 0.03f, 0.5f, 6f),
        new("Cypress 2", Guid.Parse("30047cec-269d-408e-0c30-b2603b887268"), 30f, 0f, 3f, 4, 0.6f, 3, 7.5f, 10f, 35f, 0.05f, 0.04f, 0.5f, 5f),
        new("Pine 2", Guid.Parse("d691a01c-13b7-578d-57c0-5caef0b4e7e1"), 50f, 7.5f, 4f, 2, 0.7f, 6, 6f, 10f, 15.5f, 0.1f, 0.08f, 0.7f, 3f),
        new("Plumeria", Guid.Parse("6de37e4e-7029-61f5-54b8-f5e63f983f58"), 8f, 7f, 3f, 2, 0.6f, 0, 3f, 0.1f, 20f, 0.1f, 0.075f, 0.85f, 2f),
        new("Winter Pine 1", Guid.Parse("10d2a01a-0818-84b9-4b96-c2eb63256519"), 90f, 2.5f, 6f, 1, 0.66f, 8, 0f, 4f, 6.75f, 0.1f, 0.05f, 0.85f, 2f),
        new("Winter Aspen", Guid.Parse("7c0cf89b-44b1-1ce2-dd74-07102a98ac2a"), 85f, 3f, 5f, 1, 0.6f, 8, 3f, 4.5f, 8f, 0.06f, 0.05f, 0.8f, 2f),
        new("Winter Pine 2", Guid.Parse("67931331-0c02-4876-1255-28770896c6a2"), 140f, 5f, 6f, 1, 0.6f, 7, 0f, 3f, 5f, 0.1f, 0.05f, 0.5f, 2f),
        new("Eucalyptus", Guid.Parse("a6162133-724b-54df-a12f-51cd070ad6f3"), 20f, 5f, 3.6f, 4, 0.6f, 0, 12f, 8f, 33f, 0.15f, 0.08f, 0.675f, 3f),
        new("Fern", Guid.Parse("8872f2b8-31db-42d8-580a-b3e4a91262de"), 12f, 0f, 7f, 1, 0.5f, 0, 0.01f, 0f, 4f, 1f, 0.08f, 0.2f, 1f),
        new("Eelgrass", Guid.Parse("96b4de31-f4fa-337d-ec78-451e3609769e"), 0f, 0f, 5f, 1, 0.5f, 1, 0.5f, 0.15f, 5f, 1f, 0.08f, 0.2f, 1f),
        new("Sea Sword", Guid.Parse("5894e2e7-ab8d-edfa-e61c-18cf16854ba3"), 0f, 0f, 6f, 1, 0.7f, 1, 0f, 0f, 2f, 1f, 1f, 0f, 1f),
        new("Kelp 1", Guid.Parse("2caf1179-7861-6ff3-4b7d-46e17780bdfa"), -15f, 0f, 1f, 1, 1f, 3, 2.5f, 0.75f, 1.85f, 0.04f, 0.05f, 0.8f, 2f),
        new("Beach Grass 1", Guid.Parse("18fb888b-e8f1-dce7-7da7-321d651ea6b0"), 0f, 0f, 4f, 1, 0.7f, 1, 0f, 0f, 4f, 1f, 1f, 0f, 1f),
        new("Kelp 2", Guid.Parse("2a4880b6-b7a3-690a-2049-bfbe38eafb9f"), -15f, 0f, 1f, 1, 1f, 3, 2.5f, 1.35f, 2f, 0.025f, 0.05f, 0.8f, 2f),
    ];

    /// <summary>The species of a tree object; one that is not in the table is drawn as the first (as the official viewer does).</summary>
    public static TreeSpecies SpeciesOf(int species) => species >= 0 && species < Table.Length ? Table[species] : Table[0];

    /// <summary>The prims of <paramref name="tree"/>: one, the tree's root under its own local id, a mesh shaped like the tree.</summary>
    public static IReadOnlyList<PrimData> Build(TreeData tree)
    {
        var sp = SpeciesOf(tree.Species);
        var meshId = TreeMesh.IdFor(tree.Species, tree.Scale.Length());
        TreeMesh.TryGet(meshId, out _, out var centre, out var size);
        // the mesh is turned and planted in the tree's own frame; its box is centred on the object
        var world = tree.Position + Vector3.Transform(centre, tree.Rotation);
        var face = new FaceTexture(sp.Texture, 1f, 1f, 0f, 0f, 0f, TexGen.Default, false, 0f);
        var shape = PrimShape.Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = meshId };
        return [new PrimData(tree.LocalId, tree.FullId, 0, 0, world, tree.Rotation, size, Shape: shape, Textures: new FaceTextures(face),
            Materials: new FaceMaterials(new FaceMaterial(0, 0, TreeMesh.LeafMaterialId, Guid.Empty)))];
    }
}
