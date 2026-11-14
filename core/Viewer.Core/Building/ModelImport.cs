// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Building;

/// <summary>An image a model file carries: its encoded bytes (PNG or JPEG) as the file holds them.</summary>
public sealed record ModelImage(string Name, byte[] Encoded);

/// <summary>A model's material: the base colour and its texture (an index into the model's images, -1 for none), and the PBR values a
/// glTF file gives (the other maps are -1 when absent or for a DAE file).</summary>
public sealed record ModelMaterial(string Name, Vector4 BaseColor, int BaseColorImage, bool DoubleSided = false,
    float Metallic = 1f, float Roughness = 1f, int MetallicRoughnessImage = -1, int NormalImage = -1, int EmissiveImage = -1,
    Vector3 Emissive = default, string AlphaMode = "OPAQUE", float AlphaCutoff = 0.5f)
{
    public static ModelMaterial Default { get; } = new("default", Vector4.One, -1);
}

/// <summary>One face of a mesh (one material): positions and normals in the viewer's axes (x forward, y left, z up, metres), UVs with v
/// going up as the viewer's textures do, triangle indices, and the material's index.</summary>
public sealed record ModelFace(float[] Positions, float[] Normals, float[] Uvs, int[] Indices, int Material)
{
    public int Vertices => Positions.Length / 3;
    public int Triangles => Indices.Length / 3;
}

/// <summary>One mesh of a model file, its node's whole transform already applied to its positions; it becomes one prim of the linkset.</summary>
public sealed record ModelMesh(string Name, IReadOnlyList<ModelFace> Faces)
{
    public int Triangles => Faces.Sum(f => f.Triangles);

    /// <summary>The smallest and largest corner of the mesh's positions.</summary>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var f in Faces)
            for (int i = 0; i + 2 < f.Positions.Length; i += 3)
            {
                var p = new Vector3(f.Positions[i], f.Positions[i + 1], f.Positions[i + 2]);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
        return Faces.Any(f => f.Positions.Length > 0) ? (min, max) : (Vector3.Zero, Vector3.Zero);
    }
}

/// <summary>A model file read for upload: its meshes, materials and images, or why it cannot be uploaded (a string-table key, with a detail).</summary>
public sealed record ImportedModel(IReadOnlyList<ModelMesh> Meshes, IReadOnlyList<ModelMaterial> Materials, IReadOnlyList<ModelImage> Images,
    string? Refusal = null, string? Detail = null)
{
    public bool Ok => Refusal is null;
    public int Triangles => Meshes.Sum(m => m.Triangles);

    public static ImportedModel Refused(string key, string? detail = null) => new([], [], [], key, detail);
}

/// <summary>Why a model file cannot be uploaded (keys of the string table).</summary>
public static class ModelRefusals
{
    public const string Format = "model.refused.format", Unreadable = "model.refused.unreadable", Rigged = "model.refused.rigged",
        NoGeometry = "model.refused.no_geometry", TooManyFaces = "model.refused.too_many_faces", TooManyVertices = "model.refused.too_many_vertices",
        MissingFile = "model.refused.missing_file", TooLarge = "model.refused.too_large";
}

/// <summary>Reads a model file by its extension: .glb and .gltf (glTF 2.0) or .dae (COLLADA 1.4).</summary>
public static class ModelImport
{
    /// <summary>The model files the upload offers.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".glb", ".gltf", ".dae"];

    /// <summary>The largest model file read.</summary>
    public const int MaxFileBytes = 256 * 1024 * 1024;

    /// <summary>The most faces (materials) one mesh may have: the most a prim's texture entry addresses for a mesh.</summary>
    public const int MaxFacesPerMesh = 8;

    /// <summary>The most vertices one face may have: its indices are written as 16-bit numbers.</summary>
    public const int MaxVerticesPerFace = 65535;

    public static bool IsSupportedFile(string path)
    {
        string name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        int dot = name.LastIndexOf('.');
        return dot > 0 && Extensions.Contains(name[dot..].ToLowerInvariant());
    }

    /// <summary>Reads <paramref name="bytes"/> as the file at <paramref name="path"/>; <paramref name="sibling"/> reads a file the model names
    /// beside it (a .gltf's buffers and images, a .dae's images), or null when there is none.</summary>
    public static ImportedModel Read(string path, byte[] bytes, Func<string, byte[]?> sibling)
    {
        if (!IsSupportedFile(path))
            return ImportedModel.Refused(ModelRefusals.Format);
        if (bytes.Length > MaxFileBytes)
            return ImportedModel.Refused(ModelRefusals.TooLarge);
        string ext = path[path.LastIndexOf('.')..].ToLowerInvariant();
        ImportedModel model;
        try
        {
            model = ext == ".dae" ? DaeReader.Read(bytes, sibling) : GltfReader.Read(bytes, sibling);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return ImportedModel.Refused(ModelRefusals.Unreadable, e.GetType().Name);
        }
        return model.Ok ? Validate(model) : model;
    }

    /// <summary>The limits every model meets, whatever file it came from.</summary>
    public static ImportedModel Validate(ImportedModel m)
    {
        var meshes = m.Meshes.Where(x => x.Triangles > 0).ToArray();
        if (meshes.Length == 0)
            return ImportedModel.Refused(ModelRefusals.NoGeometry);
        foreach (var mesh in meshes)
        {
            if (mesh.Faces.Count > MaxFacesPerMesh)
                return ImportedModel.Refused(ModelRefusals.TooManyFaces, mesh.Name);
            if (mesh.Faces.Any(f => f.Vertices > MaxVerticesPerFace))
                return ImportedModel.Refused(ModelRefusals.TooManyVertices, mesh.Name);
            foreach (var f in mesh.Faces)
                if (!f.Positions.All(float.IsFinite) || !f.Uvs.All(float.IsFinite) || f.Indices.Any(i => i < 0 || i >= f.Vertices) || f.Indices.Length % 3 != 0)
                    return ImportedModel.Refused(ModelRefusals.Unreadable, mesh.Name);
        }
        return m with { Meshes = meshes };
    }
}
