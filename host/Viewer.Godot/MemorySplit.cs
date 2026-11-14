// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Viewer.Godot;

/// <summary>
/// What the viewer holds in video memory besides the residency's textures, from the engine's own
/// counters (RenderingServer.GetRenderingInfo, 4.7 class reference): VIDEO_MEM_USED is "always greater than the sum of"
/// TEXTURE_MEM_USED and BUFFER_MEM_USED on Forward+ ("miscellaneous data not accounted for by those two metrics");
/// BUFFER_MEM_USED "includes vertex data, uniform buffers, and many miscellaneous buffer types". Also the rendering device's
/// allocator view (RenderingDevice.GetMemoryUsage; on Vulkan "calculated by Vulkan Memory Allocator"). The split per sample:
/// the residency's resident bytes (its own estimate: RGBA8 with a third for mipmaps), other texture memory (the engine's
/// texture total minus those: render targets, sky, water, placeholders), buffers, and the remainder. Measurement only.
/// </summary>
internal sealed class MemorySplit
{
    public readonly record struct Sample(long Video, long Texture, long Buffer, long Resident, long RdTotal, long RdTextures, long RdBuffers)
    {
        public long OtherTexture => Texture - Resident;
        public long Remainder => Video - Texture - Buffer;
        /// <summary>What the live budget subtracts: everything the engine holds that is not the residency's textures.</summary>
        public long NonTexture => Video - Resident;
    }

    private readonly List<Sample> _samples = new();
    private Sample? _peak;

    public static Sample Read(long residentBytes)
    {
        var rd = RenderingServer.GetRenderingDevice();
        return new Sample(
            (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed),
            (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed),
            (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed),
            residentBytes,
            rd is null ? -1 : (long)rd.GetMemoryUsage(RenderingDevice.MemoryType.Total),
            rd is null ? -1 : (long)rd.GetMemoryUsage(RenderingDevice.MemoryType.Textures),
            rd is null ? -1 : (long)rd.GetMemoryUsage(RenderingDevice.MemoryType.Buffers));
    }

    public Sample Add(long residentBytes)
    {
        var s = Read(residentBytes);
        _samples.Add(s);
        if (_peak is not { } p || s.Video > p.Video) _peak = s;
        return s;
    }

    private static double Mib(long b) => Math.Round(b / 1048576.0, 1);

    private static object Split(Sample s) => new
    {
        video_mib = Mib(s.Video), texture_mib = Mib(s.Texture), buffer_mib = Mib(s.Buffer),
        resident_textures_mib = Mib(s.Resident), other_texture_mib = Mib(s.OtherTexture), remainder_mib = Mib(s.Remainder),
        non_texture_mib = Mib(s.NonTexture),
        rd_total_mib = Mib(s.RdTotal), rd_textures_mib = Mib(s.RdTextures), rd_buffers_mib = Mib(s.RdBuffers),
    };

    private static object Range(IEnumerable<long> v)
    {
        var a = v.OrderBy(x => x).ToList();
        return a.Count == 0 ? new { min = 0.0, p50 = 0.0, max = 0.0 } : new { min = Mib(a[0]), p50 = Mib(a[a.Count / 2]), max = Mib(a[^1]) };
    }

    /// <summary>The split at the start (first path frame), at peak video memory and at the end, and the range of the
    /// non-texture figure and of the other-texture part over every sample (their spread says whether a live reading is steady).</summary>
    public object Summary() => _samples.Count == 0 ? new { samples = 0 } : new
    {
        samples = _samples.Count,
        start = Split(_samples[0]), at_peak_video = Split(_peak!.Value), end = Split(_samples[^1]),
        non_texture_range = Range(_samples.Select(s => s.NonTexture)),
        other_texture_range = Range(_samples.Select(s => s.OtherTexture)),
        buffer_range = Range(_samples.Select(s => s.Buffer)),
        remainder_range = Range(_samples.Select(s => s.Remainder)),
    };
}
