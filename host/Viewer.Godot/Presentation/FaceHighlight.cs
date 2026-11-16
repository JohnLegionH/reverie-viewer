// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Meshing;

namespace Viewer.Godot.Presentation;

/// <summary>One prim with the faces Select Face has chosen on it.</summary>
public readonly record struct HighlightedFaces(OutlinedPrim Prim, IReadOnlyCollection<int> Faces);

/// <summary>
/// The Select Face highlight: each chosen face, and no other, is drawn again over itself with a marker of white squares at half opacity,
/// laid out by the face's own texture coordinates, so the faces that a texture change will reach are plain to see on every object. The
/// official viewer marks selected faces the same way (white at half opacity over the face's own triangles, in its texture layout, not
/// animated; pipeline.cpp renderSelectedFaces, read as behaviour); the marker pattern here is drawn by the shader, not taken from it.
/// </summary>
public partial class FaceHighlight : Node3D
{
    /// <summary>The marker's colour: white at half opacity.</summary>
    public static readonly Color Colour = new(1f, 1f, 1f, 0.5f);
    /// <summary>How far the marker sits out from the face along its normal, in metres, so it is drawn over the face.</summary>
    public const float Lift = 0.002f;

    private Shader? _shader;
    private ShaderMaterial? _material;
    private readonly Dictionary<uint, (MeshInstance3D Node, string Key)> _nodes = new();
    private readonly PickShapeCache _shapes = new();
    private int _faces;

    /// <summary>How many faces are highlighted now.</summary>
    public int Pieces => _faces;

    /// <summary>The chosen faces to highlight (empty clears it).</summary>
    public void Update(IReadOnlyList<HighlightedFaces> faces)
    {
        var keep = new HashSet<uint>();
        int count = 0;
        foreach (var h in faces)
        {
            var p = h.Prim;
            uint id = p.Prim.Id.LocalId;
            var chosen = SelectedFaceGeometry.Of(_shapes.Faces(p.Prim.Shape), h.Faces);
            if (chosen.Count == 0)
                continue;
            keep.Add(id);
            count += chosen.Count;
            string key = string.Join(",", chosen.Select(f => f.Face)) + "|" + p.Prim.Scale + "|" + p.Prim.Shape?.GetHashCode();
            if (!_nodes.TryGetValue(id, out var entry) || entry.Key != key)
            {
                if (_nodes.TryGetValue(id, out var old)) { RemoveChild(old.Node); old.Node.QueueFree(); }
                var node = new MeshInstance3D { Name = "faces_" + id, Mesh = BuildMesh(chosen, p.Prim.Scale), MaterialOverride = MaterialOnce(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                AddChild(node);
                entry = (node, key);
                _nodes[id] = entry;
            }
            var q = new Quaternion(p.Rotation.X, p.Rotation.Z, -p.Rotation.Y, p.Rotation.W).Normalized();
            entry.Node.Transform = new Transform3D(new Basis(q), new Vector3(p.Position.X, p.Position.Z, -p.Position.Y));
        }
        foreach (var id in _nodes.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            RemoveChild(_nodes[id].Node);
            _nodes[id].Node.QueueFree();
            _nodes.Remove(id);
        }
        _faces = count;
    }

    private ShaderMaterial MaterialOnce()
    {
        _shader ??= new Shader
        {
            // the marker: four squares to each unit of the face's texture coordinates, white, with rounded corners and a clear cross of
            // gaps between them; half opacity, drawn over the face, unlit
            Code = "shader_type spatial;\nrender_mode cull_back, unshaded, depth_draw_never, blend_mix;\nuniform vec4 colour : source_color = vec4(1.0, 1.0, 1.0, 0.5);\nuniform float lift = 0.002;\n" +
                   "void vertex() { VERTEX += NORMAL * lift; }\n" +
                   "void fragment() {\n  vec2 cell = fract(UV * 2.0) - 0.5;\n  vec2 d = abs(cell) - vec2(0.36);\n" +
                   "  float r = 0.08;\n  float dist = length(max(d + r, 0.0)) - r;\n  float square = 1.0 - smoothstep(-0.01, 0.01, dist);\n" +
                   "  ALBEDO = colour.rgb;\n  ALPHA = colour.a * square;\n}\n",
        };
        if (_material is null)
        {
            _material = new ShaderMaterial { Shader = _shader };
            _material.SetShaderParameter("colour", Colour);
            _material.SetShaderParameter("lift", Lift);
        }
        return _material;
    }

    /// <summary>The chosen faces in the engine's axes with the prim's size applied, with their texture coordinates.</summary>
    private static ImmediateMesh BuildMesh(IReadOnlyList<FaceGeometry> faces, System.Numerics.Vector3 s)
    {
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var face in faces)
            foreach (int idx in face.Indices)
            {
                int i = idx * 3, u = idx * 2;
                float nx = face.Normals[i] / s.X, ny = face.Normals[i + 1] / s.Y, nz = face.Normals[i + 2] / s.Z;
                mesh.SurfaceSetNormal(new Vector3(nx, nz, -ny).Normalized());
                mesh.SurfaceSetUV(new Vector2(face.Uvs[u], face.Uvs[u + 1]));
                mesh.SurfaceAddVertex(new Vector3(face.Positions[i] * s.X, face.Positions[i + 2] * s.Z, -face.Positions[i + 1] * s.Y));
            }
        mesh.SurfaceEnd();
        return mesh;
    }
}
