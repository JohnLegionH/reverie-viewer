// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Building;

/// <summary>What the model is given for physics: nothing (the official default, which uploads a single convex hull) or its lowest LOD.</summary>
public enum PhysicsSource { None, LowestLod }

/// <summary>The settings of one model upload (shared by every file of a bulk upload, the scale per file).</summary>
public sealed record ModelSettings(bool AutoLods = true, PhysicsSource Physics = PhysicsSource.None, bool IncludeTextures = false, float Scale = 1f)
{
    public const float MinScale = 0.01f, MaxScale = 100f;
}

/// <summary>
/// The levels of detail of an uploaded model. The official viewer generates each LOD below the high one automatically, each aiming at a third
/// of the triangles of the one above (llmodelpreview.cpp, decimation 3; behaviour only). The simplifier here is the viewer's own: vertex
/// clustering on a grid, made as fine as it can be while the triangle count stays at or under the target. A cluster keeps one of its own
/// vertices, so a simplified mesh never leaves the bounds of the original.
/// </summary>
public static class ModelLods
{
    public const int Decimation = 3;

    /// <summary>LOD indices as the mesh asset orders them: 0 lowest, 1 low, 2 medium, 3 high.</summary>
    public const int Count = 4;

    /// <summary>The triangle target of each LOD (index 0 lowest .. 3 high) for a high LOD of <paramref name="high"/> triangles.</summary>
    public static int[] Targets(int high)
    {
        var t = new int[Count];
        for (int lod = 0; lod < Count; lod++)
            t[lod] = Math.Max(1, (int)(high / Math.Pow(Decimation, Count - 1 - lod)));
        t[Count - 1] = high;
        return t;
    }

    /// <summary>The four LODs of a mesh (index 0 lowest .. 3 high): the high one as given; the others simplified to their targets when
    /// <paramref name="auto"/>, else the high one again.</summary>
    public static IReadOnlyList<ModelFace>[] Build(ModelMesh mesh, bool auto)
    {
        var lods = new IReadOnlyList<ModelFace>[Count];
        lods[Count - 1] = mesh.Faces;
        var targets = Targets(mesh.Triangles);
        for (int lod = Count - 2; lod >= 0; lod--)
            lods[lod] = auto ? Simplify(mesh.Faces, targets[lod]) : mesh.Faces;
        return lods;
    }

    /// <summary>The faces simplified to at most <paramref name="target"/> triangles in all, each face keeping its share (at least one triangle
    /// when it can; a face may end up empty, which the asset writes as a face without geometry).</summary>
    public static IReadOnlyList<ModelFace> Simplify(IReadOnlyList<ModelFace> faces, int target)
    {
        int total = faces.Sum(f => f.Triangles);
        if (total <= target)
            return faces;
        var output = new ModelFace[faces.Count];
        int left = target;
        for (int i = 0; i < faces.Count; i++)
        {
            int share = i == faces.Count - 1 ? left : (int)((long)target * faces[i].Triangles / Math.Max(1, total));
            share = Math.Clamp(share, 0, left);
            output[i] = SimplifyFace(faces[i], share);
            left -= output[i].Triangles;
        }
        return output;
    }

    private static ModelFace SimplifyFace(ModelFace f, int target)
    {
        if (f.Triangles <= target)
            return f;
        if (target <= 0)
            return f with { Positions = [], Normals = [], Uvs = [], Indices = [] };
        // the finest grid whose result fits: a search over the cells per side
        int lo = 1, hi = 1024;
        ModelFace best = Cluster(f, 1);
        if (best.Triangles > target)
            best = f with { Positions = [], Normals = [], Uvs = [], Indices = [] };
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var c = Cluster(f, mid);
            if (c.Triangles <= target) { best = c; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }

    private static ModelFace Cluster(ModelFace f, int cells)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int i = 0; i < f.Vertices; i++)
        {
            var p = new Vector3(f.Positions[i * 3], f.Positions[i * 3 + 1], f.Positions[i * 3 + 2]);
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
        var size = Vector3.Max(max - min, new Vector3(1e-6f));
        var cellOf = new Dictionary<(int, int, int), int>();
        var remap = new int[f.Vertices];
        var keep = new List<int>();
        for (int i = 0; i < f.Vertices; i++)
        {
            var p = (new Vector3(f.Positions[i * 3], f.Positions[i * 3 + 1], f.Positions[i * 3 + 2]) - min) / size * cells;
            var key = (Math.Min((int)p.X, cells - 1), Math.Min((int)p.Y, cells - 1), Math.Min((int)p.Z, cells - 1));
            if (!cellOf.TryGetValue(key, out var rep))
            {
                rep = keep.Count;
                keep.Add(i);
                cellOf[key] = rep;
            }
            remap[i] = rep;
        }
        var tris = new HashSet<(int, int, int)>();
        var idx = new List<int>();
        for (int t = 0; t + 2 < f.Indices.Length; t += 3)
        {
            int a = remap[f.Indices[t]], b = remap[f.Indices[t + 1]], c = remap[f.Indices[t + 2]];
            if (a == b || b == c || a == c) continue;
            // one triangle per set of corners, whatever its rotation
            var k = a < b && a < c ? (a, b, c) : b < c ? (b, c, a) : (c, a, b);
            if (!tris.Add(k)) continue;
            idx.Add(a); idx.Add(b); idx.Add(c);
        }
        var pos = new float[keep.Count * 3]; var nrm = new float[keep.Count * 3]; var uv = new float[keep.Count * 2];
        for (int i = 0; i < keep.Count; i++)
        {
            Array.Copy(f.Positions, keep[i] * 3, pos, i * 3, 3);
            Array.Copy(f.Normals, keep[i] * 3, nrm, i * 3, 3);
            Array.Copy(f.Uvs, keep[i] * 2, uv, i * 2, 2);
        }
        return new ModelFace(pos, nrm, uv, idx.ToArray(), f.Material);
    }
}

/// <summary>The grid's answer to a model's first step: the uploader, the fee and the weights (land impact is the resource cost), or an error.</summary>
public sealed record ModelTicket(Uri? Uploader, int Price, double LandImpact, double Streaming, double Physics, double Simulation, string? Error)
{
    public bool Ok => Uploader is not null && Error is null;
}

/// <summary>The grid's two-step model upload: the first step with the whole request (fee and weights), the second with its asset resources.</summary>
public interface IModelUploadService
{
    Task<ModelTicket> RequestModelAsync(ModelUploadPlan plan, CancellationToken ct);
    Task<UploadDone> SendModelAsync(Uri uploader, ModelUploadPlan plan, CancellationToken ct);
}

/// <summary>One face of an uploaded prim: its colour and, when textures go with the model, its texture (an index into the plan's textures, -1 for none).</summary>
public sealed record PlannedFace(Vector4 Color, int Image, bool Fullbright = false);

/// <summary>One prim of the uploaded linkset: its mesh (an index into the plan's meshes), its place, size and faces.</summary>
public sealed record PlannedInstance(int Mesh, string MeshName, Vector3 Position, Quaternion Rotation, Vector3 Scale, int PhysicsShapeType, IReadOnlyList<PlannedFace> Faces);

/// <summary>Everything a model upload sends: the mesh assets, the textures (J2C; empty when they are not included), the prims.</summary>
public sealed record ModelUploadPlan(string Name, string Description, Guid FolderId, Guid TextureFolderId, IReadOnlyList<byte[]> Meshes,
    IReadOnlyList<byte[]> Textures, IReadOnlyList<PlannedInstance> Instances, int ExpectedCost = 0)
{
    public int Triangles { get; init; }
}

/// <summary>One model of a bulk upload: its name and how its plan is made.</summary>
public sealed record ModelJob(string Name, Func<ModelUploadPlan?> Plan, string? Refusal = null);

/// <summary>A model's state in a bulk upload, with the fee and weights the grid stated.</summary>
public sealed record ModelItemStatus(UploadItemState State, string? Error = null, Guid ItemId = default, ModelTicket? Fee = null);

/// <summary>
/// The upload of one or several models. Each model's fee and weights are asked first (the first step) and shown; nothing is sent until
/// <see cref="RunAsync"/>. Models then go one after the other, each asked again at the fee shown and sent; one that fails is marked and the
/// rest go on. A model refused before upload (its file could not be read) is marked with its reason and never sent.
/// </summary>
public sealed class ModelUploadBatch
{
    private readonly IModelUploadService _service;
    private readonly ModelItemStatus[] _states;
    private readonly ModelUploadPlan?[] _plans;
    private readonly object _gate = new();

    public ModelUploadBatch(IModelUploadService service, IReadOnlyList<ModelJob> jobs)
    {
        _service = service;
        Jobs = jobs;
        _plans = new ModelUploadPlan?[jobs.Count];
        _states = jobs.Select(j => j.Refusal is null ? new ModelItemStatus(UploadItemState.Waiting) : new ModelItemStatus(UploadItemState.Failed, j.Refusal)).ToArray();
    }

    public IReadOnlyList<ModelJob> Jobs { get; }

    public IReadOnlyList<ModelItemStatus> States { get { lock (_gate) return _states.ToArray(); } }

    /// <summary>The sum of the fees of the models that can go, once every one is known; null before.</summary>
    public int? TotalFee
    {
        get
        {
            var s = States;
            var going = s.Where((x, i) => Jobs[i].Refusal is null && x.State != UploadItemState.Failed).ToArray();
            return going.All(x => x.Fee is not null) ? going.Sum(x => Math.Max(0, x.Fee!.Price)) : null;
        }
    }

    /// <summary>How many were uploaded, after <see cref="RunAsync"/>.</summary>
    public int Uploaded => States.Count(s => s.State == UploadItemState.Done);

    public event Action? Changed;

    private void Set(int i, ModelItemStatus s)
    {
        lock (_gate) _states[i] = s;
        Changed?.Invoke();
    }

    private ModelUploadPlan? PlanOf(int i)
    {
        if (_plans[i] is null)
        {
            try { _plans[i] = Jobs[i].Plan(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _plans[i] = null; }
        }
        return _plans[i];
    }

    public async Task CheckFeesAsync(CancellationToken ct)
    {
        for (int i = 0; i < Jobs.Count; i++)
        {
            if (Jobs[i].Refusal is not null) continue;
            var plan = PlanOf(i);
            if (plan is null) { Set(i, new ModelItemStatus(UploadItemState.Failed, ModelRefusals.Unreadable)); continue; }
            ModelTicket t;
            try { t = await _service.RequestModelAsync(plan with { ExpectedCost = 0 }, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { t = new ModelTicket(null, 0, 0, 0, 0, 0, ex.GetType().Name); }
            Set(i, t.Ok ? new ModelItemStatus(UploadItemState.Waiting, null, default, t) : new ModelItemStatus(UploadItemState.Failed, t.Error ?? "refused"));
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        for (int i = 0; i < Jobs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var st = States[i];
            if (st.State != UploadItemState.Waiting || st.Fee is not { } fee)
                continue;   // refused, failed its fee, or its fee was never shown
            var plan = PlanOf(i)!;
            try
            {
                Set(i, st with { State = UploadItemState.Sending });
                var t = await _service.RequestModelAsync(plan with { ExpectedCost = fee.Price }, ct).ConfigureAwait(false);
                if (!t.Ok) { Set(i, st with { State = UploadItemState.Failed, Error = t.Error ?? "refused" }); continue; }
                if (t.Price != fee.Price) { Set(i, st with { State = UploadItemState.Failed, Error = "price_changed", Fee = t }); continue; }
                var done = await _service.SendModelAsync(t.Uploader!, plan, ct).ConfigureAwait(false);
                Set(i, done.Ok ? st with { State = UploadItemState.Done, ItemId = done.ItemId } : st with { State = UploadItemState.Failed, Error = done.Error ?? "failed" });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Set(i, st with { State = UploadItemState.Failed, Error = ex.GetType().Name });
            }
        }
    }
}

/// <summary>
/// The price of an image upload, as the official viewer works it out from the account's benefits (llagentbenefits.cpp; behaviour only): an
/// image of more than 1024×1024 pixels costs the 2K price (the lowest of the large texture prices, or the plain texture price when the grid
/// lists none), any other the texture price. A grid that sends no benefits (OpenSim) leaves the price to the upload's own answer.
/// </summary>
public static class TexturePrice
{
    /// <summary>The smallest area that costs the 2K price.</summary>
    public const int Min2KArea = 1024 * 1024 + 1;

    /// <summary>The price of an image arriving at <paramref name="width"/> by <paramref name="height"/>; null when the grid sent no benefits.</summary>
    public static int? For(int width, int height, int? textureCost, IReadOnlyList<int>? largeTextureCosts)
    {
        if (textureCost is not { } plain || plain < 0)
            return null;
        if ((long)width * height >= Min2KArea)
            return largeTextureCosts is { Count: > 0 } large ? large.Min() : plain;
        return plain;
    }

    /// <summary>Whether the upload window offers its "Allow 2048" choice: when the grid prices 2K images (it lists large texture prices), or
    /// when it sends no benefits at all (OpenSim, which sets no size limit and prices the upload in its own answer).</summary>
    public static bool Offers2K(int? textureCost, IReadOnlyList<int>? largeTextureCosts) => textureCost is null || largeTextureCosts is { Count: > 0 };
}
