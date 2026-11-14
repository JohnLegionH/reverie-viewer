// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Avatars;

/// <summary>
/// The slots of the bind-table atlas: one slot (a pair of atlas rows) for each rigged mesh asset worn, shared by every wearer, counted by
/// users and free for another asset when the last user goes. The capacity grows with demand, by a quarter (at least <see cref="GrowStep"/>
/// tables) each time it is full, up to a limit; past the limit an asset is refused (-1) until a slot is freed. The lowest free slot is
/// handed out first, so the slots in use gather at the bottom and <see cref="Trim"/> can give the empty top back. Engine-free; the texture
/// that holds the rows is <c>SkinTextures</c>'s.
/// </summary>
public sealed class TableAllocator
{
    public const int StartTables = 16, GrowStep = 64, DefaultMaxTables = 8192;

    /// <summary>Texels in a table row: a joint's matrix is <see cref="SkinTables.TexelsPerMatrix"/> texels, a mesh lists up to 110 joints.</summary>
    public const int RowTexels = 110 * SkinTables.TexelsPerMatrix, BytesPerTexel = 16, RowsPerTable = 2;

    private readonly Dictionary<Guid, (int Slot, int Users)> _slots = [];
    private readonly PriorityQueue<int, int> _free = new();
    private int _next;   // slots below this one have been handed out at least once

    /// <param name="growthDivisor">The capacity grows by 1 / this of itself (at least <see cref="GrowStep"/>) when full: 4, a quarter, by default; 2 grows in
    /// halves, for an atlas kept on the device, where a larger step costs only the video memory and means fewer, and so fewer large, growths.</param>
    public TableAllocator(int maxTables = DefaultMaxTables, int startTables = StartTables, int growthDivisor = 4)
    {
        GrowthDivisor = Math.Max(2, growthDivisor);
        MaxTables = maxTables;
        Capacity = Math.Min(startTables, maxTables);
    }

    public int MaxTables { get; }
    public int GrowthDivisor { get; }
    public int Capacity { get; private set; }
    public int InUse => _slots.Count;

    /// <summary>Bytes a texture of <paramref name="tables"/> tables holds (RGBA float).</summary>
    public static long Bytes(int tables) => (long)tables * RowsPerTable * RowTexels * BytesPerTexel;

    public bool Contains(Guid asset) => _slots.ContainsKey(asset);

    /// <summary>The users counted for the asset (0 when it has no slot).</summary>
    public int Users(Guid asset) => _slots.TryGetValue(asset, out var have) ? have.Users : 0;

    /// <summary>The asset's slot, one more user counted; made the first time (<paramref name="isNew"/>). -1 at the limit.</summary>
    public int Acquire(Guid asset, out bool isNew)
    {
        isNew = false;
        if (_slots.TryGetValue(asset, out var have))
        {
            _slots[asset] = (have.Slot, have.Users + 1);
            return have.Slot;
        }
        int slot;
        if (_free.TryDequeue(out slot, out _)) { }
        else
        {
            if (_next >= Capacity)
            {
                if (Capacity >= MaxTables) return -1;
                int grown = Math.Max(Capacity + GrowStep, Capacity + Capacity / GrowthDivisor);
                Capacity = Math.Min(RoundUp(grown), MaxTables);
            }
            slot = _next++;
        }
        _slots[asset] = (slot, 1);
        isNew = true;
        return slot;
    }

    /// <summary>One user of the asset's slot is gone; true when it was the last and the slot is free again.</summary>
    public bool Release(Guid asset)
    {
        if (!_slots.TryGetValue(asset, out var have)) return false;
        if (have.Users > 1) { _slots[asset] = (have.Slot, have.Users - 1); return false; }
        _slots.Remove(asset);
        _free.Enqueue(have.Slot, have.Slot);
        return true;
    }

    /// <summary>Gives the empty top of the atlas back when under a quarter of it is in use: the capacity falls to just above the highest slot in
    /// use (never below the starting size), and the slots above it are forgotten. Returns the capacity.</summary>
    public int Trim()
    {
        if (Capacity <= StartTables || InUse * 4 >= Capacity) return Capacity;
        int top = -1;
        foreach (var (slot, _) in _slots.Values) top = Math.Max(top, slot);
        int target = Math.Max(Math.Min(StartTables, MaxTables), RoundUp(top + 1));
        if (target >= Capacity) return Capacity;
        Capacity = target;
        _next = Math.Min(_next, top + 1);
        var keep = new List<int>();
        while (_free.TryDequeue(out var slot, out _)) if (slot <= top) keep.Add(slot);
        foreach (var slot in keep) _free.Enqueue(slot, slot);
        return Capacity;
    }

    private static int RoundUp(int tables) => (tables + GrowStep - 1) / GrowStep * GrowStep;
}
