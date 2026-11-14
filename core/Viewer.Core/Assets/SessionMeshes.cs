// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Meshing;

namespace Viewer.Core.Assets;

/// <summary>A transport that can fetch mesh asset bytes (the adapter's mesh capability).</summary>
public interface IMeshTransport
{
    Task<AssetFetchResult> FetchMeshAsync(Guid id, CancellationToken ct);
}

public sealed record MeshFetchResult(AssetFetchStatus Status, MeshGeometry? Geometry, MeshRefusal? Refusal = null)
{
    /// <summary>Fetched but refused by the decoder's limits (reported as Failed with a reason).</summary>
    public bool Refused => Refusal is not null;

    /// <summary>Where the bytes came from (<see cref="AssetFetchResult.Source"/>; null: the transport).</summary>
    public string? Source { get; init; }
}

/// <summary>
/// One session's meshes. Bytes come through that session's <see cref="SessionAssets"/> (per-session authorisation
/// first, per-session failures, cancellation on disposal: ADR-006); decoding is the injected decoder (the
/// adapter's <c>LmvMesh.Decode</c>, guarded and bounded). Decoded geometry is kept per session, by asset id.
/// </summary>
public sealed class SessionMeshes(SessionAssets assets, Func<Guid, byte[], (MeshGeometry? Geometry, MeshRefusal? Refusal)> decode) : IDisposable
{
    private readonly Dictionary<Guid, Task<MeshFetchResult>> _decoded = new();
    private readonly object _lock = new();

    public Task<MeshFetchResult> GetAsync(Guid id, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_decoded.TryGetValue(id, out var t) && t.IsCompletedSuccessfully && t.Result.Status == AssetFetchStatus.Ok)
                return t;
            // on the thread pool whatever the caller's context: a shared-store hit completes at once, so without
            // the hop the copy and the decode (engine-free) ran on the caller's thread, the engine's main thread
            var task = Task.Run(() => LoadAsync(id, ct));
            _decoded[id] = task;
            return task;
        }
    }

    private async Task<MeshFetchResult> LoadAsync(Guid id, CancellationToken ct)
    {
        var r = await assets.GetAsync(id, ct).ConfigureAwait(false);
        if (r.Status != AssetFetchStatus.Ok)
            return new(r.Status, null);
        // the copy and the decode counted as mesh decode on this thread
        var alloc = Diagnostics.AllocationMeter.Shared.Begin(Diagnostics.AllocKind.MeshDecode);
        (MeshGeometry? g, MeshRefusal? refusal) = (null, null);
        try
        {
            Diagnostics.AllocationMeter.Large(r.Bytes.Length);
            (g, refusal) = decode(id, r.Bytes.ToArray());
        }
        finally { alloc.End(); }
        return g is not null ? new(AssetFetchStatus.Ok, g) { Source = r.Source } : new(AssetFetchStatus.Failed, null, refusal) { Source = r.Source };
    }

    /// <summary>Drops the asset's decoded geometry once its load has finished (the host let it go);
    /// the next <see cref="GetAsync"/> fetches and decodes it again (the shared store and the content store first).</summary>
    public void Forget(Guid id)
    {
        lock (_lock)
            if (_decoded.TryGetValue(id, out var t) && t.IsCompleted)
                _decoded.Remove(id);
    }

    public void Dispose()
    {
        assets.Dispose();
        lock (_lock) _decoded.Clear();
    }
}
