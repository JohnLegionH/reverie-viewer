// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Diagnostics;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The native memory sample of the long run: the process's committed address space grouped by kind (loaded images, mapped files, private
/// regions) and by owner (the module, the mapped file, the private region's size class), plus the process heaps' summaries.
/// </summary>
[Collection(nameof(NativeMemoryTests))]   // serial: it measures the whole process's memory, which any test running beside it moves
public class NativeMemoryTests
{
    private const long MiB = 1048576;

    [Fact]
    public void Regions_are_summed_by_kind_and_owner_largest_first()
    {
        var rows = NativeMemoryGroups.Group(
        [
            new(RegionKind.Image, 3 * MiB, "coreclr.dll"), new(RegionKind.Image, 1 * MiB, "coreclr.dll"), new(RegionKind.Image, 2 * MiB, "vulkan-1.dll"),
            new(RegionKind.Mapped, 5 * MiB, "store.bin"),
        ], topOwners: 10);
        var images = rows.Where(r => r.Kind == "image").ToArray();
        Assert.Equal(["coreclr.dll", "vulkan-1.dll"], images.Select(r => r.Owner).ToArray());
        Assert.Equal([4 * MiB, 2 * MiB], images.Select(r => r.Bytes).ToArray());
        Assert.Equal(2, images[0].Regions);
        Assert.Equal(5 * MiB, rows.Single(r => r.Kind == "mapped").Bytes);
    }

    [Fact]
    public void Owners_beyond_the_top_are_summed_into_other()
    {
        var regions = Enumerable.Range(1, 6).Select(i => new MemoryRegion(RegionKind.Image, i * MiB, $"m{i}.dll")).ToArray();
        var images = NativeMemoryGroups.Group(regions, topOwners: 3).Where(r => r.Kind == "image").ToArray();
        Assert.Equal(["m6.dll", "m5.dll", "m4.dll", "(other)"], images.Select(r => r.Owner).ToArray());
        Assert.Equal((1 + 2 + 3) * MiB, images[3].Bytes);
        Assert.Equal(3, images[3].Regions);
        Assert.Equal(regions.Sum(r => r.Bytes), images.Sum(r => r.Bytes));
    }

    [Theory]
    [InlineData(4096L, "private <64 KiB")]
    [InlineData(65535L, "private <64 KiB")]
    [InlineData(65536L, "private 64 KiB to <1 MiB")]
    [InlineData(1048575L, "private 64 KiB to <1 MiB")]
    [InlineData(1048576L, "private 1 MiB to <16 MiB")]
    [InlineData(16777215L, "private 1 MiB to <16 MiB")]
    [InlineData(16777216L, "private 16 MiB and above")]
    public void Private_regions_are_grouped_by_size_class(long bytes, string owner)
    {
        var row = NativeMemoryGroups.Group([new(RegionKind.Private, bytes, "")], topOwners: 5).Single();
        Assert.Equal("private", row.Kind);
        Assert.Equal(owner, row.Owner);
        Assert.Equal(1, row.Regions);
    }

    [Fact]
    public void A_mapped_region_with_no_file_is_pagefile_backed()
    {
        var row = NativeMemoryGroups.Group([new(RegionKind.Mapped, MiB, "")], topOwners: 5).Single();
        Assert.Equal("(pagefile)", row.Owner);
    }

    [Fact]
    public void The_sample_of_this_process_accounts_for_its_private_memory()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sample = NativeMemorySampler.Sample();
        Assert.True(sample.Regions.Count > 100);
        using var p = Process.GetCurrentProcess();
        long privateCommit = sample.Regions.Where(r => r.Kind == RegionKind.Private).Sum(r => r.Bytes);
        // the private commit the process reports, to within the allocations made while the two figures were taken
        Assert.InRange(privateCommit, p.PrivateMemorySize64 * 0.8, p.PrivateMemorySize64 * 1.2);
        Assert.Contains(sample.Regions, r => r.Kind == RegionKind.Image && r.Owner.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(sample.Heaps);
        Assert.All(sample.Heaps, h => Assert.True(h.Committed >= h.Allocated));
    }

    [Fact]
    public void A_large_native_allocation_shows_in_the_private_total_and_in_the_heap_summaries()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The block is found by its address rather than by the change in the process's whole private total: the garbage collector
        // returns memory to the system on its own schedule, so the total can fall between two samples by more than the block adds.
        long Heaps(NativeMemorySample s) => s.Heaps.Sum(h => h.Committed);
        long PrivateCovering(NativeMemorySample s, long start, long end) =>
            s.Regions.Where(r => r.Kind == RegionKind.Private && r.Address < end && r.Address + r.Bytes > start)
                     .Sum(r => Math.Min(end, r.Address + r.Bytes) - Math.Max(start, r.Address));
        var before = NativeMemorySampler.Sample();
        IntPtr block = System.Runtime.InteropServices.Marshal.AllocHGlobal(64 * 1048576);
        try
        {
            for (long off = 0; off < 64 * MiB; off += 4096) System.Runtime.InteropServices.Marshal.WriteByte(block, (int)off, 1);
            var after = NativeMemorySampler.Sample();
            long start = (long)block, end = start + 64 * MiB;
            long counted = PrivateCovering(after, start, end);
            Assert.True(counted >= 64 * MiB - 4096, $"the private regions cover {counted / MiB} MiB of the 64 MiB block");
            Assert.True(after.Regions.Where(r => r.Kind == RegionKind.Private).Sum(r => r.Bytes) >= counted, "the private total holds the block");
            Assert.True(Heaps(after) - Heaps(before) >= 48 * MiB, $"heaps grew by {(Heaps(after) - Heaps(before)) / MiB} MiB");
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(block); }
    }
}

[CollectionDefinition(nameof(NativeMemoryTests), DisableParallelization = true)]
public class NativeMemoryTestsCollection;
