// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Reflection;
using Viewer.Core.Meshing;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// Our mesh limits (<see cref="MeshLimits"/>, <see cref="MeshSkinReader"/>) must be no looser than the limits the protocol
/// library applies to the same assets, so moving to a library version that bounds its own decoders never leaves our
/// guard as the weaker one. The library's limits are public static fields; they are read by reflection here because no
/// library type may be used outside the protocol folder. A library version that has no such fields fails the lookup,
/// which is the point: it is a version whose decoders are not bounded.
/// </summary>
public class LibraryMeshLimitsTests(ITestOutputHelper output)
{
    private static object? LibraryValue(string typeName, string field)
    {
        foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "LibreMetaverse*.dll"))
        {
            Assembly asm;
            try { asm = Assembly.LoadFrom(dll); } catch (BadImageFormatException) { continue; }
            var type = asm.GetType(typeName, throwOnError: false);
            var f = type?.GetField(field, BindingFlags.Public | BindingFlags.Static);
            if (f is not null) return f.GetValue(null);
        }
        return null;
    }

    private static readonly (string Type, string Field, Func<MeshLimits, long> Ours)[] Pairs =
    [
        ("LibreMetaverse.Assets.AssetMesh", "MaxParts", l => l.MaxHeaderParts),
        ("LibreMetaverse.Assets.AssetMesh", "MaxInflatedPartBytes", l => l.MaxInflatedPartBytes),
        ("LibreMetaverse.Assets.AssetMesh", "MaxInflatedAssetBytes", l => l.MaxInflatedTotalBytes),
        ("LibreMetaverse.Assets.AssetMesh", "MaxDecodedElements", l => l.MaxLlsdNodes),
        ("LibreMetaverse.Rendering.FacetedMesh", "MaxFaces", l => l.MaxFacesPerLod),
        ("LibreMetaverse.Rendering.FacetedMesh", "MaxVerticesPerFace", l => l.MaxVerticesPerFace),
        ("LibreMetaverse.Rendering.FacetedMesh", "MaxTrianglesPerFace", l => l.MaxTrianglesPerLod),
        ("LibreMetaverse.Rendering.FacetedMesh", "MaxSkinJoints", _ => MeshSkinReader.MaxListedJoints),
    ];

    [Fact]
    public void The_library_bounds_its_own_mesh_decoders_with_public_limits()
    {
        var missing = Pairs.Where(p => LibraryValue(p.Type, p.Field) is null).Select(p => $"{p.Type}.{p.Field}").ToList();
        Assert.True(missing.Count == 0, "the library has no limit named: " + string.Join(", ", missing));
    }

    [Fact]
    public void Our_mesh_limits_are_no_looser_than_the_libraries()
    {
        var ours = MeshLimits.Default;
        var looser = new List<string>();
        foreach (var (type, field, mine) in Pairs)
        {
            if (LibraryValue(type, field) is not { } theirs) { looser.Add($"{type}.{field}: not found"); continue; }
            long limit = Convert.ToInt64(theirs);
            output.WriteLine($"{field}: ours {mine(ours)}, library {limit}");
            if (mine(ours) > limit) looser.Add($"{field}: ours {mine(ours)} > library {limit}");
        }
        Assert.True(looser.Count == 0, string.Join("; ", looser));
    }

    /// <summary>The same comparison on the numbers the 3.2.0 sources declare, so it also holds on a library that does not
    /// expose the fields.</summary>
    [Fact]
    public void Our_mesh_limits_are_no_looser_than_the_numbers_3_2_0_declares()
    {
        var o = MeshLimits.Default;
        Assert.True(o.MaxHeaderParts <= 64);
        Assert.True(o.MaxInflatedPartBytes <= 16 * 1024 * 1024);
        Assert.True(o.MaxInflatedTotalBytes <= 64L * 1024 * 1024);
        Assert.True(o.MaxLlsdNodes <= 2 * 1024 * 1024);
        Assert.True(o.MaxFacesPerLod <= 8);
        Assert.True(o.MaxVerticesPerFace <= 65536);
        Assert.True(o.MaxTrianglesPerLod <= 1024 * 1024);
        Assert.True(MeshSkinReader.MaxListedJoints <= 256);
    }
}
