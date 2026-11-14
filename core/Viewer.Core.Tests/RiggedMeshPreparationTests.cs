// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The data work of a worn rigged mesh made off the main thread: its surfaces in the engine's layout with each vertex's joints and weights
/// (<see cref="SkinnedSurfaceData"/>) and its table of bind matrices (<see cref="SkinTables.BuildTable"/>), prepared together by
/// <see cref="RiggedMeshPreparation"/> and handed over by the same preparer that serves plain mesh LODs. Engine-free; the preparations wait on
/// nothing a test must time, so each test polls for the result. Pure, runs in parallel.
/// </summary>
public class RiggedMeshPreparationTests
{
    private const string SkeletonXml = """
        <linden_skeleton version="2.0">
          <bone name="mPelvis" pos="0 0 1" rot="0 0 0" scale="1 1 1" support="base" aliases="hip">
            <bone name="mTorso" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mChest" pos="0 0 0.2" rot="0 0 0" scale="1 1 1" support="base"/>
            </bone>
          </bone>
        </linden_skeleton>
        """;

    private const string DefinitionXml = """
        <linden_avatar>
          <attachment_point id="1" group="6" name="Chest" joint="mChest" position="0.15 0 -0.1" rotation="0 90 90"/>
        </linden_avatar>
        """;

    private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-00000000000a");

    private static AvatarRig Rig() => new(CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml), Encoding.UTF8.GetBytes(DefinitionXml)));

    private static MeshSkinBlock Block() =>
        new(["mChest", "mTorso"], [Matrix4x4.CreateTranslation(0, 0, -1.3f), Matrix4x4.CreateTranslation(0, 0, -1.1f)], Matrix4x4.CreateTranslation(0.5f, 0, 0), [], 0f, false, false);

    private static MeshFace Triangle(float z = 0) => new([0, 0, z, 1, 0, z, 0, 1, z], [0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 0, 1, 0, 0, 1], [0, 1, 2]);

    private static FaceInfluences Influences() => new(3, [0, 1, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0], [0.25f, 0.75f, 0, 0, 1, 0, 0, 0, 0.5f, 0.5f, 0, 0], 0);

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(2);
        Assert.True(done(), "timed out");
    }

    [Fact]
    public void The_surfaces_are_what_the_skinned_encoder_makes_for_each_face()
    {
        IReadOnlyList<MeshFace> faces = [Triangle(), Triangle(1)];
        IReadOnlyList<FaceInfluences> influences = [Influences(), Influences()];
        var prepared = RiggedMeshPreparation.Prepare(faces, influences, Rig(), Block());
        Assert.Equal(2, prepared.Surfaces.Count);
        for (int f = 0; f < 2; f++)
        {
            var expected = SkinnedSurfaceData.Encode(faces[f], influences[f]);
            Assert.Equal(expected.Format, prepared.Surfaces[f].Format);
            Assert.Equal(expected.VertexData, prepared.Surfaces[f].VertexData);
            Assert.Equal(expected.AttributeData, prepared.Surfaces[f].AttributeData);
            Assert.Equal(expected.IndexData, prepared.Surfaces[f].IndexData);
        }
    }

    [Fact]
    public void The_table_is_what_the_table_builder_makes_for_the_skin()
    {
        var rig = Rig();
        var block = Block();
        var prepared = RiggedMeshPreparation.Prepare([Triangle()], [Influences()], rig, block);
        var matrices = new float[block.UsableJoints * SkinTables.FloatsPerMatrix];
        var nodes = new float[block.UsableJoints];
        SkinTables.BuildTable(rig, block, matrices, nodes);
        Assert.Equal(matrices, prepared.Matrices);
        Assert.Equal(nodes, prepared.Nodes);
    }

    [Fact]
    public void Faces_and_influences_that_do_not_pair_up_are_refused()
    {
        Assert.Throws<ArgumentException>(() => RiggedMeshPreparation.Prepare([Triangle(), Triangle()], [Influences()], Rig(), Block()));
    }

    [Fact]
    public async Task The_preparer_runs_the_given_work_and_hands_back_its_result_once()
    {
        using var p = new MeshLodPreparer();
        var rig = Rig();
        var block = Block();
        int runs = 0;
        PreparedMeshLod Work() { Interlocked.Increment(ref runs); return RiggedMeshPreparation.Prepare([Triangle()], [Influences()], rig, block); }
        p.Request((A, 1), Work);
        p.Request((A, 1), Work);   // asked twice: prepared once
        await Until(() => p.Held == 1);
        Assert.True(p.TryTakeArrival(out var key));
        Assert.Equal((A, 1), key);
        Assert.True(p.TryTake((A, 1), out var prepared));
        var rigged = Assert.IsType<PreparedRiggedLod>(prepared);
        Assert.Equal(block.UsableJoints * SkinTables.FloatsPerMatrix, rigged.Matrices.Length);
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Work_that_throws_is_a_failed_preparation_the_host_builds_itself()
    {
        using var p = new MeshLodPreparer();
        p.Request((A, 0), () => throw new InvalidOperationException("no"));
        await Until(() => p.Held == 1);
        Assert.True(p.TryTake((A, 0), out var prepared));
        Assert.Null(prepared);
        Assert.Equal(1, p.Failed);
    }
}
