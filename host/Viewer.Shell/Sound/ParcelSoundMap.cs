// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell.Sound;

/// <summary>
/// Whether a sound at a point can be heard by the parcel rule ("sounds only within parcel"), as the official viewer decides it: a point in
/// the agent's own parcel (same region, its 4 m square set in the parcel's bitmap) is always heard; elsewhere it is not heard when the
/// agent's parcel keeps its sounds in, or when the point's square carries the sound-local bit of its region's parcel overlay.
/// Main thread only.
/// </summary>
public sealed class ParcelSoundMap
{
    /// <summary>The size of one parcel square, in metres.</summary>
    public const float GridStep = 4f;

    /// <summary>The overlay's "sounds only within parcel" bit.</summary>
    public const byte SoundLocalBit = 0x20;

    /// <summary>The parcel the agent stands in: its region, whether it keeps its sounds in, its bitmap and the region's width in metres.</summary>
    public void SetAgentParcel(ulong regionHandle, bool soundLocal, byte[]? bitmap, int regionWidth = 256)
    {
        _agentRegion = regionHandle;
        _agentSoundLocal = soundLocal;
        _agentBitmap = bitmap;
        _agentPerEdge = Math.Max(1, (int)(regionWidth / GridStep));
    }

    /// <summary>One quarter of a region's overlay (chunks 0 to 3, row by row).</summary>
    public void SetOverlay(ulong regionHandle, int chunk, byte[] data, int regionWidth = 256)
    {
        int perEdge = Math.Max(1, (int)(regionWidth / GridStep));
        if (chunk < 0 || chunk > 3)
            return;
        if (!_overlays.TryGetValue(regionHandle, out var o) || o.PerEdge != perEdge)
            _overlays[regionHandle] = o = (perEdge, new byte[perEdge * perEdge]);
        int chunkBytes = o.Cells.Length / 4;
        Array.Copy(data, 0, o.Cells, chunk * chunkBytes, Math.Min(chunkBytes, data.Length));
    }

    /// <summary>Forgets a region's overlay (the region is gone).</summary>
    public void Forget(ulong regionHandle) => _overlays.Remove(regionHandle);

    /// <summary>Whether a sound at a region-local point of a region can be heard.</summary>
    public bool CanHear(ulong regionHandle, Vector3 local)
    {
        if (InAgentParcel(regionHandle, local))
            return true;
        if (_agentSoundLocal)
            return false;
        return !SoundLocalAt(regionHandle, local);
    }

    private ulong _agentRegion;
    private bool _agentSoundLocal;
    private byte[]? _agentBitmap;
    private int _agentPerEdge = 64;
    private readonly Dictionary<ulong, (int PerEdge, byte[] Cells)> _overlays = new();

    private static bool Cell(Vector3 local, int perEdge, out int index)
    {
        int col = (int)MathF.Floor(local.X / GridStep), row = (int)MathF.Floor(local.Y / GridStep);
        index = row * perEdge + col;
        return col >= 0 && row >= 0 && col < perEdge && row < perEdge;
    }

    // the bitmap has one bit per square, row by row, the lowest bit of each byte first
    private bool InAgentParcel(ulong regionHandle, Vector3 local) =>
        regionHandle == _agentRegion && _agentBitmap is { } b && Cell(local, _agentPerEdge, out int i) && i / 8 < b.Length && (b[i / 8] & (1 << (i % 8))) != 0;

    private bool SoundLocalAt(ulong regionHandle, Vector3 local) =>
        _overlays.TryGetValue(regionHandle, out var o) && Cell(local, o.PerEdge, out int i) && (o.Cells[i] & SoundLocalBit) != 0;
}
