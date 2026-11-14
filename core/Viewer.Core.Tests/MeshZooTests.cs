// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Meshing;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The committed mesh-zoo fixture and the presentation's non-blocking mesh source.</summary>
public class MeshZooTests
{
    public static string ZooPath => Path.Combine(MeshDecodeTests.MeshDir, "mesh-zoo.meshzoo.json");

    [Fact]
    public void The_committed_mesh_zoo_matches_its_generator_and_names_the_committed_assets()
    {
        string expected = MeshZoo.Serialise(MeshZoo.Make());
        if (Environment.GetEnvironmentVariable("NV_WRITE_MESH_ZOO") == "1")
            File.WriteAllText(ZooPath, expected);
        Assert.Equal(expected, File.ReadAllText(ZooPath).Replace("\r\n", "\n"));
        var (prims, assets) = MeshZoo.Parse(File.ReadAllText(ZooPath));
        Assert.Equal(3, prims.Count);
        Assert.All(prims, p => Assert.Equal(PrimShape.SculptTypeMesh, p.Shape.SculptType));
        Assert.All(prims, p => Assert.True(File.Exists(Path.Combine(MeshDecodeTests.MeshDir, assets[p.Shape.SculptId]))));
        Assert.Equal(6, prims[0].Colors.OverrideCount);
        Assert.Contains(prims, p => p.Colors[1].A < 1);
    }

    [Fact]
    public async Task The_mesh_source_is_pending_then_ready_and_announces_the_arrival()
    {
        var bytes = File.ReadAllBytes(Path.Combine(MeshDecodeTests.MeshDir, "mesh-sphere.llmesh"));
        var gate = new TaskCompletionSource();
        var id = MeshZoo.Assets.Keys.ElementAt(1);
        using var src = new MeshSource(new Viewer.Core.Assets.SessionMeshes(
            new Viewer.Core.Assets.SessionAssets(new Viewer.Core.Assets.SharedAssetStore(), _ => true, async (_, _) => { await gate.Task; return Viewer.Core.Assets.AssetFetchResult.Ok(bytes); }),
            (i, b) => { var r = LmvMesh.Decode(i, b); return (r.Geometry, r.Refusal); }));
        Assert.Equal(MeshAvailability.Pending, src.TryGet(id, out _));
        Assert.Equal(MeshAvailability.Pending, src.TryGet(id, out _));   // one fetch, not two
        Assert.Equal(1, src.Pending);
        gate.SetResult();
        var until = DateTime.UtcNow.AddSeconds(60);
        Guid arrived;
        while (!src.TryTakeArrival(out arrived) && DateTime.UtcNow < until) await Task.Delay(5);
        Assert.Equal(id, arrived);
        Assert.Equal(MeshAvailability.Ready, src.TryGet(id, out var g));
        Assert.Equal(4, g!.Lods.Count(l => l is not null));
        Assert.Equal(MeshAvailability.Pending, src.TryGet(Guid.NewGuid(), out _));
    }

    /// <summary>The decoded meshes the source keeps are counted, entries and array bytes, now and at
    /// the peak; a failed fetch is not an entry, and asking again for a kept mesh adds nothing.</summary>
    [Fact]
    public async Task The_mesh_source_counts_the_decoded_geometry_it_keeps()
    {
        var files = MeshZoo.Assets.ToList();
        using var src = new MeshSource(new Viewer.Core.Assets.SessionMeshes(
            new Viewer.Core.Assets.SessionAssets(new Viewer.Core.Assets.SharedAssetStore(), _ => true, (id, _) => Task.FromResult(
                MeshZoo.Assets.TryGetValue(id, out var f) ? Viewer.Core.Assets.AssetFetchResult.Ok(File.ReadAllBytes(Path.Combine(MeshDecodeTests.MeshDir, f))) : Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.NotFound))),
            (i, b) => { var r = LmvMesh.Decode(i, b); return (r.Geometry, r.Refusal); }));
        Assert.Equal(0, src.DecodedEntries);
        Assert.Equal(0, src.DecodedBytes);
        var missing = Guid.NewGuid();
        foreach (var (id, _) in files) src.TryGet(id, out _);
        src.TryGet(missing, out _);
        var until = DateTime.UtcNow.AddSeconds(60);
        while (src.Pending > 0 && DateTime.UtcNow < until) await Task.Delay(5);
        long expected = 0;
        foreach (var (id, _) in files)
        {
            Assert.Equal(MeshAvailability.Ready, src.TryGet(id, out var g));
            long b = 0;
            foreach (var lod in g!.Lods.Where(l => l is not null))
                foreach (var face in lod!.Faces) b += 4L * (face.Positions.Length + face.Normals.Length + face.Uvs.Length + face.Indices.Length);
            Assert.Equal(b, Viewer.Core.Residency.MeshFootprint.DecodedBytes(g));
            expected += b;
        }
        Assert.Equal(MeshAvailability.Unavailable, src.TryGet(missing, out _));
        Assert.Equal(files.Count, src.DecodedEntries);
        Assert.True(expected > 0);
        Assert.Equal(expected, src.DecodedBytes);
        Assert.Equal(expected, src.PeakDecodedBytes);
        foreach (var (id, _) in files) src.TryGet(id, out _);   // kept: asked again, nothing is added
        Assert.Equal(files.Count, src.DecodedEntries);
        Assert.Equal(expected, src.DecodedBytes);
    }
}
