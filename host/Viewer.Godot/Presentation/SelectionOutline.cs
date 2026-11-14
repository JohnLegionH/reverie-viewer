// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Godot.Presentation;

/// <summary>One selected prim at its place in the region: its state, position, rotation and whether it is the linkset's root.</summary>
public readonly record struct OutlinedPrim(ObjectState Prim, System.Numerics.Vector3 Position, System.Numerics.Quaternion Rotation, bool IsRoot);

/// <summary>
/// The highlight around the selected object: a slightly inflated copy of each selected prim's shape, drawn from the inside out (only its
/// back faces show) in one flat colour, so a band of that colour surrounds the object. The band is as thick on screen whatever the
/// distance: its thickness is the distance to the camera times 0.01, scaled by the view angle (the official viewer's rule, restated in
/// our own words; its scrolling stripes are not drawn). A selected root is yellow, a selected child blue. A sculpt or mesh prim is
/// outlined as a box.
/// </summary>
public partial class SelectionOutline : Node3D
{
    public const float ThicknessPerMetre = 0.01f, DefaultFovDegrees = 60f, MinThickness = 0.01f, Alpha = 0.8f;
    public static readonly Color RootColour = new(1f, 1f, 0f, Alpha), ChildColour = new(0.13f, 0.42f, 0.77f, Alpha);

    private Shader? _shader;
    private readonly Dictionary<uint, (MeshInstance3D Node, ShaderMaterial Material, System.Numerics.Vector3 Scale, PrimShape? Shape)> _nodes = new();
    private readonly PickShapeCache _shapes = new();

    /// <summary>How many prims are outlined now.</summary>
    public int Pieces => _nodes.Count;

    /// <summary>The prims to outline (empty clears the outline); the camera's place in engine coordinates and its field of view.</summary>
    public void Update(IReadOnlyList<OutlinedPrim> prims, Vector3 cameraPosition, float fovDegrees)
    {
        var keep = new HashSet<uint>();
        foreach (var p in prims)
        {
            uint id = p.Prim.Id.LocalId;
            keep.Add(id);
            if (!_nodes.TryGetValue(id, out var entry) || entry.Scale != p.Prim.Scale || !Equals(entry.Shape, p.Prim.Shape))
            {
                if (_nodes.TryGetValue(id, out var old)) { RemoveChild(old.Node); old.Node.QueueFree(); }
                var material = new ShaderMaterial { Shader = ShaderOnce() };
                var node = new MeshInstance3D { Name = "outline_" + id, Mesh = BuildMesh(p.Prim), MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                AddChild(node);
                entry = (node, material, p.Prim.Scale, p.Prim.Shape);
                _nodes[id] = entry;
            }
            var q = new Quaternion(p.Rotation.X, p.Rotation.Z, -p.Rotation.Y, p.Rotation.W).Normalized();
            var origin = new Vector3(p.Position.X, p.Position.Z, -p.Position.Y);
            entry.Node.Transform = new Transform3D(new Basis(q), origin);
            float distance = (origin - cameraPosition).Length();
            float thickness = Math.Max(MinThickness, distance * ThicknessPerMetre * (fovDegrees / DefaultFovDegrees));
            entry.Material.SetShaderParameter("grow", thickness);
            entry.Material.SetShaderParameter("colour", p.IsRoot ? RootColour : ChildColour);
        }
        foreach (var id in _nodes.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            RemoveChild(_nodes[id].Node);
            _nodes[id].Node.QueueFree();
            _nodes.Remove(id);
        }
    }

    private Shader ShaderOnce() => _shader ??= new Shader
    {
        Code = "shader_type spatial;\nrender_mode cull_back, unshaded, depth_draw_never;\nuniform vec4 colour : source_color = vec4(1.0, 1.0, 0.0, 0.8);\nuniform float grow = 0.02;\n" +
               "void vertex() { VERTEX += NORMAL * grow; }\nvoid fragment() { ALBEDO = colour.rgb; ALPHA = colour.a; }\n",
    };

    /// <summary>The prim's shape in the engine's axes with its size already applied (so the band is the same width on every side).</summary>
    private ImmediateMesh BuildMesh(ObjectState prim)
    {
        var mesh = new ImmediateMesh();
        var s = prim.Scale;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var face in _shapes.Faces(prim.Shape))
            foreach (int idx in face.Indices)
            {
                int i = idx * 3;
                float nx = face.Normals[i] / s.X, ny = face.Normals[i + 1] / s.Y, nz = face.Normals[i + 2] / s.Z;
                mesh.SurfaceSetNormal(new Vector3(nx, nz, -ny).Normalized());
                mesh.SurfaceAddVertex(new Vector3(face.Positions[i] * s.X, face.Positions[i + 2] * s.Z, -face.Positions[i + 1] * s.Y));
            }
        mesh.SurfaceEnd();
        return mesh;
    }
}

/// <summary>The faces of the shapes outlined so far (a sculpt or mesh prim uses the box).</summary>
internal sealed class PickShapeCache
{
    private readonly Viewer.Core.Interaction.PickGeometryCache _cache = new();
    public IReadOnlyList<FaceGeometry> Faces(PrimShape? shape) => _cache.Faces(shape);
}
