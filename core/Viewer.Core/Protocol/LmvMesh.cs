// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Rendering;
using Viewer.Core.Assets;
using Viewer.Core.Meshing;

namespace Viewer.Core.Protocol;

/// <summary>The outcome of decoding one mesh asset: geometry, or a refusal reason (never both).</summary>
public sealed record MeshDecodeResult(MeshGeometry? Geometry, MeshRefusal? Refusal)
{
    public bool Ok => Geometry is not null;
}

/// <summary>
/// SL mesh assets decoded with LibreMetaverse's decoder (AssetMesh + FacetedMesh.TryDecodeFromAsset), the only
/// place those types are touched (ADR-005), into the core's <see cref="MeshGeometry"/>. Every asset passes
/// <see cref="MeshAssetGuard"/> first (the decoder's own allocations are unbounded), and the decoded result is checked
/// again (face, vertex and triangle limits, indices in range, finite numbers). Any exception is a refusal, never a crash.
/// </summary>
public static class LmvMesh
{
    // LibreMetaverse's detail levels by the core's LOD index (0 lowest.. 3 high): Low = lowest_lod... Highest = high_lod.
    private static readonly System.Collections.Immutable.ImmutableArray<DetailLevel> Levels = [DetailLevel.Low, DetailLevel.Medium, DetailLevel.High, DetailLevel.Highest];
    private static readonly System.Collections.Immutable.ImmutableArray<string> LodKeys = ["lowest_lod", "low_lod", "medium_lod", "high_lod"];

    public static MeshDecodeResult Decode(Guid assetId, byte[] asset, MeshLimits? limits = null)
    {
        limits ??= MeshLimits.Default;
        var refusal = MeshAssetGuard.Validate(asset, limits, out var parts);
        if (refusal is not null)
            return new(null, refusal);
        try
        {
            var am = new AssetMesh(new UUID(assetId), asset);
            if (!am.Decode())
                return new(null, new("decoder refused the asset"));
            var prim = new Primitive { Textures = new Primitive.TextureEntry(UUID.Zero), Scale = Vector3.One, PrimData = new Primitive.ConstructionData() };
            var lods = new MeshLod?[MeshGeometry.LodCount];
            MeshSkin? skin = null;
            var empty = MeshSkinReader.NoGeometryFlags(asset, limits);   // submeshes marked NoGeometry, which the decoder leaves out
            for (int i = 0; i < MeshGeometry.LodCount; i++)
            {
                if (!parts.Any(p => p.Name == LodKeys[i] && p.Size > 0 && p.Offset >= 0))
                    continue;
                if (!FacetedMesh.TryDecodeFromAsset(prim, am, Levels[i], out var fm) || fm is null)
                    continue;
                var lod = Convert(fm, limits, out var why);
                if (lod is null)
                    return new(null, new($"{LodKeys[i]}: {why}"));
                lods[i] = lod with { Faces = MeshSkinReader.Restore(lod.Faces, empty[i]) };   // an empty face stays a face (llvolume.cpp 2338-2347)
                if (fm.SkinData is { } s && skin is null)
                    skin = new MeshSkin(s.JointNames?.Length ?? 0, s.BindShapeMatrix is { Length: 16 }, fm.Faces.Sum(f => f.Weights?.Count ?? 0));
            }
            if (lods.All(l => l is null))
                return new(null, new("no level of detail decoded"));
            // the whole skin, read from the same bytes by the core's own reader (never throws; a refused block leaves the counts only)
            if (skin is not null) skin = skin with { Data = MeshSkinReader.Reconcile(MeshSkinReader.Read(asset, limits), lods) };
            return new(new MeshGeometry(lods, skin), null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new(null, new("decoder failed: " + e.GetType().Name));
        }
    }

    private static MeshLod? Convert(FacetedMesh fm, MeshLimits limits, out string? why)
    {
        why = null;
        if (fm.Faces.Count > limits.MaxFacesPerLod) { why = $"{fm.Faces.Count} faces (limit {limits.MaxFacesPerLod})"; return null; }
        var faces = new List<MeshFace>(fm.Faces.Count);
        long triangles = 0;
        foreach (var f in fm.Faces)
        {
            int nv = f.Vertices?.Count ?? 0, ni = f.Indices?.Count ?? 0;
            if (nv > limits.MaxVerticesPerFace) { why = $"{nv} vertices in a face"; return null; }
            if (ni % 3 != 0) { why = "index count not a multiple of 3"; return null; }
            triangles += ni / 3;
            if (triangles > limits.MaxTrianglesPerLod) { why = "too many triangles"; return null; }
            var pos = new float[nv * 3];
            var nrm = new float[nv * 3];
            var uv = new float[nv * 2];
            for (int i = 0; i < nv; i++)
            {
                var v = f.Vertices![i];
                pos[i * 3] = v.Position.X; pos[i * 3 + 1] = v.Position.Y; pos[i * 3 + 2] = v.Position.Z;
                nrm[i * 3] = v.Normal.X; nrm[i * 3 + 1] = v.Normal.Y; nrm[i * 3 + 2] = v.Normal.Z;
                uv[i * 2] = v.TexCoord.X; uv[i * 2 + 1] = v.TexCoord.Y;
            }
            if (!pos.All(float.IsFinite) || !uv.All(float.IsFinite)) { why = "non-finite vertex data"; return null; }
            for (int i = 0; i < nrm.Length; i++) if (!float.IsFinite(nrm[i])) nrm[i] = 0;
            var idx = new int[ni];
            // the large arrays made here, under the work running on this thread
            Diagnostics.AllocationMeter.Large(nv * 12L); Diagnostics.AllocationMeter.Large(nv * 12L); Diagnostics.AllocationMeter.Large(nv * 8L); Diagnostics.AllocationMeter.Large(ni * 4L);
            for (int i = 0; i < ni; i++)
            {
                idx[i] = f.Indices![i];
                if (idx[i] >= nv) { why = "index beyond the face's vertices"; return null; }
            }
            faces.Add(new MeshFace(pos, nrm, uv, idx));
        }
        return new MeshLod(faces);
    }

    /// <summary>
    /// The adapter's mesh fetch (the grid's mesh capability, via LibreMetaverse's AssetManager.RequestMeshAsync) as a
    /// <see cref="SessionAssets"/> fetcher: bytes only, decoded later by <see cref="Decode"/>. LibreMetaverse reports a
    /// missing asset and a failed fetch the same way, so both are NotFound.
    /// </summary>
    internal static async Task<AssetFetchResult> FetchAsync(GridClient client, Guid id, CancellationToken ct)
    {
        var am = await client.Assets.RequestMeshAsync(new UUID(id), ct).ConfigureAwait(false);
        return am?.AssetData is { Length: > 0 } bytes ? AssetFetchResult.Ok(bytes) : AssetFetchResult.Of(AssetFetchStatus.NotFound);
    }
}
