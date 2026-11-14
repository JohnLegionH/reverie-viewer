// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Viewer.Shell;

public enum RegionKind { Image, Mapped, Private }

/// <summary>One committed region of the process's address space: its kind, its size, its owner (the module or mapped file's name; empty for a private region)
/// and the address it starts at.</summary>
public readonly record struct MemoryRegion(RegionKind Kind, long Bytes, string Owner, long Address = 0);

/// <summary>A process heap's summary.</summary>
public readonly record struct HeapInfo(long Allocated, long Committed, long Reserved);

public sealed record NativeMemorySample(IReadOnlyList<MemoryRegion> Regions, IReadOnlyList<HeapInfo> Heaps);

/// <summary>Committed bytes of one kind and owner, and how many regions they came in.</summary>
public readonly record struct NativeGroupRow(string Kind, string Owner, long Bytes, int Regions);

/// <summary>
/// Groups committed regions for the long run's native memory sample: loaded images by module, mapped files by file, private regions by size
/// class. Each kind keeps its <c>topOwners</c> largest owners and sums the rest into "(other)".
/// </summary>
public static class NativeMemoryGroups
{
    private const long KiB = 1024, MiB = 1024 * KiB;

    public static string SizeClass(long bytes) =>
        bytes < 64 * KiB ? "private <64 KiB"
        : bytes < MiB ? "private 64 KiB to <1 MiB"
        : bytes < 16 * MiB ? "private 1 MiB to <16 MiB"
        : "private 16 MiB and above";

    public static IReadOnlyList<NativeGroupRow> Group(IEnumerable<MemoryRegion> regions, int topOwners)
    {
        var rows = new List<NativeGroupRow>();
        foreach (var kind in new[] { RegionKind.Image, RegionKind.Mapped, RegionKind.Private })
        {
            var owners = regions.Where(r => r.Kind == kind)
                .GroupBy(r => kind switch
                {
                    RegionKind.Private => SizeClass(r.Bytes),
                    RegionKind.Mapped when r.Owner.Length == 0 => "(pagefile)",
                    _ => r.Owner,
                }, StringComparer.OrdinalIgnoreCase)
                .Select(g => new NativeGroupRow(kind.ToString().ToLowerInvariant(), g.Key, g.Sum(r => r.Bytes), g.Count()))
                .OrderByDescending(r => r.Bytes).ThenBy(r => r.Owner, StringComparer.Ordinal)
                .ToList();
            rows.AddRange(owners.Take(topOwners));
            if (owners.Count > topOwners)
                rows.Add(new NativeGroupRow(kind.ToString().ToLowerInvariant(), "(other)", owners.Skip(topOwners).Sum(r => r.Bytes), owners.Skip(topOwners).Sum(r => r.Regions)));
        }
        return rows;
    }
}

/// <summary>Reads the running process's committed regions (VirtualQuery) and heap summaries (HeapSummary) with Windows calls of its own. Windows only.</summary>
public static class NativeMemorySampler
{
    private const uint MemCommit = 0x1000, MemImage = 0x1000000, MemMapped = 0x40000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapSummaryNative
    {
        public uint Size;
        public UIntPtr Allocated, Committed, Reserved, MaxReserve;
    }

    [DllImport("kernel32.dll")] private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation info, UIntPtr length);
    [DllImport("kernel32.dll")] private static extern uint GetProcessHeaps(uint count, [Out] IntPtr[] heaps);
    [DllImport("kernel32.dll")] private static extern bool HeapSummary(IntPtr heap, uint flags, ref HeapSummaryNative summary);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] private static extern uint GetMappedFileNameW(IntPtr process, IntPtr address, StringBuilder name, uint size);

    public static NativeMemorySample Sample()
    {
        if (!OperatingSystem.IsWindows()) return new NativeMemorySample([], []);
        var regions = new List<MemoryRegion>(4096);
        var names = new Dictionary<long, string>();
        var me = GetCurrentProcess();
        var text = new StringBuilder(520);
        long address = 0;
        int size = Marshal.SizeOf<MemoryBasicInformation>();
        while (VirtualQuery((IntPtr)address, out var info, (UIntPtr)size) != UIntPtr.Zero)
        {
            long length = (long)info.RegionSize;
            if (info.State == MemCommit)
            {
                var kind = info.Type == MemImage ? RegionKind.Image : info.Type == MemMapped ? RegionKind.Mapped : RegionKind.Private;
                string owner = "";
                if (kind != RegionKind.Private)
                {
                    long key = (long)info.AllocationBase;
                    if (!names.TryGetValue(key, out owner!))
                    {
                        text.Clear();
                        owner = GetMappedFileNameW(me, info.BaseAddress, text, (uint)text.Capacity) > 0 ? Path.GetFileName(text.ToString()) : "";
                        names[key] = owner;
                    }
                }
                regions.Add(new MemoryRegion(kind, length, owner, (long)info.BaseAddress));
            }
            long next = address + length;
            if (next <= address) break;
            address = next;
        }
        var heaps = new List<HeapInfo>();
        var handles = new IntPtr[1024];
        uint count = GetProcessHeaps((uint)handles.Length, handles);
        for (int i = 0; i < Math.Min(count, handles.Length); i++)
        {
            var summary = new HeapSummaryNative { Size = (uint)Marshal.SizeOf<HeapSummaryNative>() };
            if (HeapSummary(handles[i], 0, ref summary))
                heaps.Add(new HeapInfo((long)summary.Allocated, (long)summary.Committed, (long)summary.Reserved));
        }
        return new NativeMemorySample(regions, heaps);
    }
}
