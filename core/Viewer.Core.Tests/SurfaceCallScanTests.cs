// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The viewer adds every surface from prepared bytes (RenderingServer.MeshAddSurface with a
/// dictionary it holds). No shipping source may call the engine's array packing (RenderingServer.MeshAddSurfaceFromArrays
/// or ArrayMesh.AddSurfaceFromArrays): the binding makes its own blend-shape array and LOD dictionary for it and holds
/// neither through the native call, and the GC freeing one mid-call was the crash. Only the render harness
/// keeps array packing: the engine's own packing is --tr-meshprep's reference, and NodeTerrainView is the ADR-003
/// comparison view (every argument held there).
/// </summary>
public class SurfaceCallScanTests
{
    /// <summary>The harness files allowed to pack arrays, relative to the repo root.</summary>
    private static readonly string[] Harness =
    [
        Path.Combine("host", "Viewer.Godot", "TestRender.cs"),
        Path.Combine("host", "Viewer.Godot", "Presentation", "NodeTerrainView.cs"),
    ];

    private static readonly Regex ArrayPacking = new(@"\bAddSurfaceFromArrays\s*\(|\bMeshAddSurfaceFromArrays\s*\(", RegexOptions.Compiled);

    private static IEnumerable<string> Sources()
    {
        foreach (var top in new[] { "host", "core", "tools" })
        {
            var dir = Path.Combine(TestPaths.RepoRoot, top);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(TestPaths.RepoRoot, f);
                var parts = rel.Split(Path.DirectorySeparatorChar);
                if (parts.Any(p => p is "bin" or "obj" or ".godot" or "build")) continue;
                // test projects never ship (the kept-collections scan's negative controls quote the old calls)
                if (parts.Any(p => p.EndsWith(".Tests", StringComparison.Ordinal))) continue;
                yield return rel;
            }
        }
    }

    /// <summary>The code of a line: a // comment (and so a /// doc line) removed.</summary>
    private static string Code(string line)
    {
        int c = line.IndexOf("//", StringComparison.Ordinal);
        return c < 0 ? line : line[..c];
    }

    private static List<string> Calls(string rel) =>
        File.ReadAllLines(Path.Combine(TestPaths.RepoRoot, rel))
            .Select((l, i) => (Line: Code(l), No: i + 1))
            .Where(x => ArrayPacking.IsMatch(x.Line))
            .Select(x => $"{rel}:{x.No}: {x.Line.Trim()}")
            .ToList();

    [Fact]
    public void No_shipping_source_packs_surface_arrays_in_the_engine()
    {
        var found = Sources().Where(rel => !Harness.Contains(rel, StringComparer.OrdinalIgnoreCase)).SelectMany(Calls).ToList();
        Assert.True(found.Count == 0, "array packing outside the harness:\n" + string.Join("\n", found));
    }

    [Fact]
    public void The_scan_sees_the_harness_reference_so_it_is_not_vacuous()
    {
        // the harness's engine-packing reference (--tr-meshprep) is a call the scan must find
        Assert.NotEmpty(Calls(Harness[0]));
        Assert.True(Sources().Count() > 100, "the scan found too few sources: the repo root is wrong");
    }

    [Fact]
    public void Only_the_harness_makes_the_node_terrain_view()
    {
        var makers = Sources().Where(rel => !Harness.Contains(rel, StringComparer.OrdinalIgnoreCase))
            .SelectMany(rel => File.ReadAllLines(Path.Combine(TestPaths.RepoRoot, rel)).Select((l, i) => (rel, l: Code(l), no: i + 1)))
            .Where(x => Regex.IsMatch(x.l, @"\bnew\s+NodeTerrainView\b"))
            .Select(x => $"{x.rel}:{x.no}").ToList();
        Assert.True(makers.Count == 0, "NodeTerrainView made outside the harness: " + string.Join(", ", makers));
    }
}
