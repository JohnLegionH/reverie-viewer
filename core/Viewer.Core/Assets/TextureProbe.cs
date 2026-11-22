// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using Viewer.Core.Imaging;
using Viewer.Core.Net;

namespace Viewer.Core.Assets;

/// <summary>What one probe run measured: its concurrency, the wall time for all its requests, and each request's timing.</summary>
public sealed record TextureProbeResult(int Concurrency, double WallMs, TimelineSummary Timing);

/// <summary>A transport that can fetch textures straight from the region's texture capability, bypassing the protocol
/// library's HTTP client (the direct-capability comparison).</summary>
public interface ITextureCapabilityProbe
{
    /// <summary>Null when the region has no texture capability.</summary>
    Task<TextureProbeResult?> ProbeTexturesAsync(IReadOnlyList<Guid> ids, int concurrency, long? to, CancellationToken ct);
}

/// <summary>A transport that can say where it fetches textures from now (a development reading).</summary>
public interface ITextureRouteProbe
{
    /// <summary>The login reply's appearance service, whether the current region says it bakes avatars, the region's texture capability,
    /// and the address each id would be fetched from.</summary>
    (string? AppearanceService, bool RegionBakes, Uri? TextureCapability, IReadOnlyList<(Guid Id, Uri? Address)> Routes) TextureRoutes(IEnumerable<Guid> ids);
}

/// <summary>
/// Fetches textures straight from a texture capability with a plain bounded client, at a fixed
/// concurrency, every request timed (<see cref="RangeHttp"/>, bounded by <see cref="J2kLimits.MaxBytes"/>). It separates
/// the grid's own time from queueing in our client. Read-only GETs, what a viewer does; the concurrency is never above
/// <see cref="MaxConcurrency"/>, the official viewer's texture-fetch concurrency per host (TextureFetchConcurrency 8,
/// llappcorehttp.cpp; behaviour only). Measurement only: reached through the development build's automation.
/// </summary>
public static class TextureProbe
{
    public const int MaxConcurrency = 8;

    /// <param name="to">The last byte of each request (for example the first chunk's), or null for the whole file.</param>
    public static async Task<TextureProbeResult> RunAsync(HttpClient client, Func<Guid, Uri> uriFor, IReadOnlyList<Guid> ids, int concurrency, long? to, CancellationToken ct)
    {
        int n = Math.Clamp(concurrency, 1, MaxConcurrency);
        var timeline = new TextureTimeline();
        using var gate = new SemaphoreSlim(n, n);
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { await RangeHttp.GetAsync(client, uriFor(id), 0, to, J2kLimits.Default.MaxBytes, ct, timeline.BeginRequest(id, "probe")).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return new(n, Math.Round(sw.Elapsed.TotalMilliseconds, 1), timeline.Summary());
    }
}
