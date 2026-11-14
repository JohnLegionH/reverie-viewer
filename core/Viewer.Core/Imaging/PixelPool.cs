// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Imaging;

/// <summary>
/// A bounded pool of pixel buffers for decode output (the level's
/// RGBA) and the mip chain, so level decodes stop allocating a fresh large array for every
/// level and the collections those allocations cause pause the main thread less.
///   - Exact lengths only: a buffer is handed out only for a request of exactly its length (texture levels repeat a few
///     sizes), so a caller never gets a longer array with an earlier texture's bytes past the end of its own. Every caller
///     writes every byte of what it rents (checked by tests: decode A, return it, decode a smaller or truncated B: B's
///     pixels are the ones B decodes to on a fresh buffer).
///   - Bounded: at most <see cref="CapBytes"/> held; a buffer returned above the cap is let go (left to the collector).
///   - Buffers under <see cref="MinPooledBytes"/> are not pooled (small levels allocate cheaply and would crowd the pool).
///   - A buffer returned twice is kept once, so it can never be handed to two renters.
/// Thread-safe. The viewer returns a level's buffers once its engine image exists (the texture source); a buffer
/// never returned is simply collected.
/// </summary>
public sealed class PixelPool
{
    /// <summary>64 MiB, sized from the measurements (section 14): a level decode with its chain allocated 9.3 MiB
    /// of our own for a 1024x1024 texture and 37.3 MiB for a 2048x2048 one, and (when a many-layer bake level was a full decode scaled down) 8.0-8.3 MiB, on top of
    /// CoreJ2K's own 13-26 MiB (75 MiB for a 2048x2048 level 0). 64 MiB keeps one 2048x2048 level's buffers plus two or three 1024x1024 ones between decodes;
    /// what is returned above it is left to the collector, as before.</summary>
    public const long DefaultCapBytes = 64L << 20;

    /// <summary>64 KiB: a 128x128 RGBA level. Smaller buffers are allocated each time.</summary>
    public const int MinPooledBytes = 64 * 1024;

    /// <summary>The process-wide pool the decoder uses.</summary>
    public static PixelPool Shared { get; } = new(DefaultCapBytes);

    private readonly object _lock = new();
    private readonly Dictionary<int, Stack<byte[]>> _free = new();
    private readonly HashSet<byte[]> _pooled = new(ReferenceEqualityComparer.Instance);
    private long _held, _peakHeld, _reused, _allocated, _allocatedBytes, _returned, _letGo;

    public PixelPool(long capBytes)
    {
        if (capBytes < 0) throw new ArgumentOutOfRangeException(nameof(capBytes));
        CapBytes = capBytes;
    }

    public long CapBytes { get; }

    /// <summary>A buffer of exactly <paramref name="length"/> bytes: a pooled one of that length when there is one, else a
    /// new one. Its contents are unspecified: the caller writes every byte.</summary>
    public byte[] Rent(int length)
    {
        if (length >= MinPooledBytes)
            lock (_lock)
                if (_free.TryGetValue(length, out var stack) && stack.Count > 0)
                {
                    var b = stack.Pop();
                    _pooled.Remove(b);
                    _held -= b.Length;
                    _reused++;
                    return b;
                }
        lock (_lock) { _allocated++; _allocatedBytes += length; }
        Diagnostics.AllocationMeter.Large(length);   // (measurement only)
        return new byte[length];
    }

    /// <summary>Hands a buffer back (null, small ones and ones above the cap are let go). The caller must not use it after.</summary>
    public void Return(byte[]? buffer)
    {
        if (buffer is null || buffer.Length < MinPooledBytes) return;
        lock (_lock)
        {
            if (_pooled.Contains(buffer)) return;   // returned twice: kept once
            if (_held + buffer.Length > CapBytes) { _letGo++; return; }
            if (!_free.TryGetValue(buffer.Length, out var stack)) _free[buffer.Length] = stack = new Stack<byte[]>();
            stack.Push(buffer);
            _pooled.Add(buffer);
            _held += buffer.Length;
            if (_held > _peakHeld) _peakHeld = _held;
            _returned++;
        }
    }

    public PixelPoolStats Stats
    {
        get { lock (_lock) return new(_held, _peakHeld, _reused, _allocated, _allocatedBytes, _returned, _letGo); }
    }
}

/// <summary>Counts since the pool was made: bytes held now and at most, buffers reused from the pool, buffers allocated
/// (and their bytes), buffers returned and kept, buffers returned above the cap and let go.</summary>
public sealed record PixelPoolStats(long HeldBytes, long PeakHeldBytes, long Reused, long Allocated, long AllocatedBytes, long Returned, long LetGo);
