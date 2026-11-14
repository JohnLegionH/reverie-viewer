// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file class1\deferred\terrainF.glsl
 *
 * $LicenseInfo:firstyear=2007&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2007, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

// Adapted 2026 by Legion Builds. The terrain shader (TerrainCode) follows the main() of the official terrain fragment shader (terrainF.glsl): the ground layers are
// mixed by three reads of the two-dimensional ramp picture (alpha1, alpha2 and alphaFinal), written for the engine's shading language. The rest of this file is our own code.

// Copyright (c) 2026 Legion Builds

using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>Where terrain chunks go. Two representations exist so ADR-003's "measurements decide" can be measured; the viewer
/// uses the server one, moved the node one into the harness (NodeTerrainView.cs).</summary>
public interface ITerrainView
{
    string Kind { get; }
    int ChunkCount { get; }
    /// <summary>Engine resources this view holds right now (meshes + instances, or nodes + meshes).</summary>
    int LiveResources { get; }
    void Upload(TerrainChunk chunk);
    /// <summary>The view takes chunks prepared off the main thread (<see cref="HandOver"/>); the
    /// harness's node view does not (it packs arrays in the engine from the chunk itself).</summary>
    bool TakesPrepared { get; }
    /// <summary>A chunk built and encoded off the main thread; the view only makes the engine calls.</summary>
    void HandOver(int cx, int cy, PreparedSurface surface, bool composed);
    void Remove(int cx, int cy);
    /// <summary>The region's four detail textures (the caller owns them); chunks with a composition use
    /// them from the next upload on. Null: height-coloured ground.</summary>
    void SetDetailTextures(Texture2D[]? layers, Vector2 originOffset = default);
    /// <summary>The material made for the detail textures (null without them).</summary>
    ShaderMaterial? DetailMaterial { get; }
    /// <summary>Frees everything (region change, shutdown).</summary>
    void Clear();
}

internal static class ChunkArrays
{
    /// <summary>Fills <paramref name="arrays"/> with the chunk's surface arrays: the harness's reference only 
    /// (the node view, NodeTerrainView.cs, and --tr-meshprep's engine packing; the viewer hands terrain over as prepared
    /// bytes, <see cref="ServerTerrainView.Prepare"/>). Every packed array made from ours goes through
    /// <paramref name="held"/>, which the caller keeps until the engine call has returned.</summary>
    public static void Fill(global::Godot.Collections.Array arrays, TerrainChunk c, HeldValues held)
    {
        var v = new Vector3[c.VertexCount];
        var n = new Vector3[c.VertexCount];
        var col = new Color[c.VertexCount];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = new Vector3(c.Positions[i * 3], c.Positions[i * 3 + 1], c.Positions[i * 3 + 2]);
            n[i] = new Vector3(c.Normals[i * 3], c.Normals[i * 3 + 1], c.Normals[i * 3 + 2]);
            col[i] = new Color(c.Colors[i * 4], c.Colors[i * 4 + 1], c.Colors[i * 4 + 2], c.Colors[i * 4 + 3]);
        }
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = held.Add(v);
        arrays[(int)Mesh.ArrayType.Normal] = held.Add(n);
        arrays[(int)Mesh.ArrayType.Color] = held.Add(col);
        if (c.Composition is { } comp)
        {
            // the composition value rides in UV.x and the jitter in UV.y for the terrain shader
            var uv = new Vector2[c.VertexCount];
            for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(comp[i], c.Jitter is { } jit ? jit[i] : 0f);
            arrays[(int)Mesh.ArrayType.TexUV] = held.Add(uv);
        }
        arrays[(int)Mesh.ArrayType.Index] = held.Add(c.Indices);
    }

    /// <summary>
    /// Four detail textures repeating every 12 m in world X/Y (offset by
    /// the region's global origin), blended as the official terrain shader does: the composition value and the vertex's jitter are
    /// read from the two-dimensional ramp picture for the weights of the layers (ported from terrainF.glsl, see THIRD-PARTY-NOTICES.md),
    /// in sRGB and then converted to linear like prim faces; lit, rough, no specular.
    /// </summary>
    internal static readonly string TerrainCode = "shader_type spatial;\nrender_mode cull_disabled;\n" + FaceShader.ClassicGlobals + FaceShader.ProbeView(TerrainBody) + FaceShader.ClassicLight;

    private const string TerrainBody = @"
uniform sampler2D detail0 : filter_linear_mipmap, repeat_enable;
uniform sampler2D detail1 : filter_linear_mipmap, repeat_enable;
uniform sampler2D detail2 : filter_linear_mipmap, repeat_enable;
uniform sampler2D detail3 : filter_linear_mipmap, repeat_enable;
uniform sampler2D ramp : filter_linear, repeat_disable;
uniform vec2 origin_offset = vec2(0.0);
uniform float repeat_metres = 12.0;
varying vec2 tc;
varying vec2 comp;
vec3 srgb_to_linear(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
void vertex() {
    vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    tc = vec2(w.x, -w.z) / repeat_metres + origin_offset;
    comp = UV;   // x: the composition value, y: the vertex's jitter
}
void fragment() {
    vec2 st = vec2(tc.x, 1.0 - tc.y);
    // the ramp read at (value, jitter), (value - 1, jitter) and (value - 2, jitter): 1 at the start of its first axis, 0 at its end
    float a1 = texture(ramp, vec2(clamp(comp.x, 0.0, 1.0), comp.y)).r;
    float aF = texture(ramp, vec2(clamp(comp.x - 1.0, 0.0, 1.0), comp.y)).r;
    float a2 = texture(ramp, vec2(clamp(comp.x - 2.0, 0.0, 1.0), comp.y)).r;
    vec3 col = mix(mix(texture(detail3, st).rgb, texture(detail2, st).rgb, a2), mix(texture(detail1, st).rgb, texture(detail0, st).rgb, a1), aF);
    ALBEDO = srgb_to_linear(col);
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;
    if (" + FaceShader.GlowCamera + @") { ALBEDO = vec3(0.0); }   // the glow picture: terrain hides glow behind it and shows none
}
";

    public static ShaderMaterial TerrainMaterial(Texture2D[] layers, Vector2 originOffset)
    {
        // the process's kept terrain shader (warmed at start-up; never freed, so hold is not needed)
        var m = new ShaderMaterial { Shader = KeptShaders.Terrain };
        // the names are made once (EngineNames), not a temporary StringName per call; the layers are the caller's, and
        // the variants carrying them go with the drain, not a finalizer
        for (int i = 0; i < 4; i++) VariantDisposal.SetShaderParameter(m, EngineNames.Detail[i], layers[i]);
        m.SetShaderParameter(EngineNames.OriginOffset, originOffset);
        m.SetShaderParameter(EngineNames.Ramp, KeptShaders.TerrainRamp);   // the process's kept ramp picture
        return m;
    }

    public static StandardMaterial3D Material() => new()
    {
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        Roughness = 1f,
        MetallicSpecular = 0f,
    };
}

/// <summary>RenderingServer representation: one mesh RID + one instance RID per chunk in the given scenario, no nodes.</summary>
public sealed class ServerTerrainView : ITerrainView
{
    private readonly Rid _scenario;
    // The height-coloured ground material this view makes for itself, when a chunk first needs it, and releases when it is cleared (a
    // presenter's teardown clears its view, and a view may serve another presenter after that, which makes it again).
    private StandardMaterial3D? _ground;
    private StandardMaterial3D Ground => _ground ??= ChunkArrays.Material();
    private ShaderMaterial? _terrain;
    private readonly Dictionary<(int, int), (Rid Mesh, Rid Instance)> _chunks = new();
    // the glow picture's choice among the composed chunks: each chunk's bounding sphere (engine axes), the composed ones, and the ones drawn in it now
    private readonly Dictionary<(int, int), (System.Numerics.Vector3 Centre, float Radius)> _bounds = new();
    private readonly HashSet<(int, int)> _composed = new();
    private readonly HashSet<(int, int)> _inPicture = new();
    private static readonly Dictionary<Rid, ServerTerrainView> s_byScenario = new();

    /// <summary>Counts every change to the chunks, so the glow picture's choice is made again after one.</summary>
    internal int GlowVersion { get; private set; }

    /// <summary>The composed chunks the glow picture draws now (for tests and measurements).</summary>
    internal int GlowPictureChunks => _inPicture.Count;

    /// <summary>The terrain view drawing into this scenario, if one has been made (the prim layer's glow picture asks it which chunks it needs).</summary>
    internal static ServerTerrainView? For(Rid scenario) => s_byScenario.GetValueOrDefault(scenario);

    private uint LayerOf((int, int) key, bool composed) => ProbeCapture.Layer | (composed && _terrain is not null ? 1u | (_inPicture.Contains(key) ? GlowPass.InGlowPicture : 0u) : GlowPass.NotInGlowLayer);

    private void SetBounds((int, int) key, float[] aabb, bool composed)
    {
        _bounds[key] = (new System.Numerics.Vector3(aabb[0] + aabb[3] / 2, aabb[1] + aabb[4] / 2, aabb[2] + aabb[5] / 2), new System.Numerics.Vector3(aabb[3], aabb[4], aabb[5]).Length() / 2);
        if (composed) _composed.Add(key); else { _composed.Remove(key); _inPicture.Remove(key); }
        GlowVersion++;
    }

    /// <summary>Puts on the glow picture's layer the composed chunks that can be in front of one of the targets (and takes the others off it),
    /// or all of them when <paramref name="all"/>; see <see cref="GlowOccluders"/>.</summary>
    internal void UpdateGlowPicture(System.Numerics.Vector3 eye, IReadOnlyList<Viewer.Core.Residency.Bounds> targets, bool all)
    {
        foreach (var (key, c) in _chunks)
        {
            if (!_composed.Contains(key) || !_bounds.TryGetValue(key, out var b)) continue;
            bool want = all;
            var ball = new Viewer.Core.Residency.Bounds(b.Centre, b.Radius);
            for (int t = 0; t < targets.Count && !want; t++) want = Viewer.Core.World.GlowOccluders.CanHide(eye, targets[t], ball);
            if (want == _inPicture.Contains(key)) continue;
            if (want) _inPicture.Add(key); else _inPicture.Remove(key);
            RenderingServer.InstanceSetLayerMask(c.Instance, LayerOf(key, true));
        }
    }


    public void SetDetailTextures(Texture2D[]? layers, Vector2 originOffset = default)
    {
        var old = _terrain;
        _terrain = layers is null ? null : ChunkArrays.TerrainMaterial(layers, originOffset);
        if (old is null) return;
        // the chunks still drawn with the old material are moved to the new one (or the plain one) before it is freed:
        // the engine holds a mesh's material by RID only, and a mesh left on a freed material makes it report errors for
        // that chunk at each later use and at its free. The presenter rebuilds the chunks after a change of layers.
        var oldRid = old.GetRid();
        GlowVersion++;
        if (_chunks.Count > 0)   // with no chunk drawn, the plain material is not made just to be named
        {
            var to = (_terrain is not null ? (Material)_terrain : Ground).GetRid();
            foreach (var (key, c) in _chunks)
            {
                if (RenderingServer.MeshSurfaceGetMaterial(c.Mesh, 0) == oldRid) RenderingServer.MeshSurfaceSetMaterial(c.Mesh, 0, to);
                RenderingServer.InstanceSetLayerMask(c.Instance, LayerOf(key, _terrain is not null));
            }
        }
        old.Dispose();
    }

    /// <summary>Terrain drawn with the composed terrain shader is part of the glow picture (it hides glow behind it); terrain
    /// drawn with the plain material, before its detail textures arrive, is not.</summary>

    private Rid MaterialFor(TerrainChunk c) => (c.Composition is not null && _terrain is not null ? (Material)_terrain : Ground).GetRid();

    /// <summary>Where this view's chunks lie in the scene, for a view that draws a neighbouring region (its corner relative to the
    /// anchor region's, engine axes); identity for the anchor region's own view.</summary>
    private readonly Transform3D _placement = Transform3D.Identity;
    private bool _shown = true;

    /// <param name="scenario">The scene the chunks are drawn in.</param>
    /// <param name="offset">Where the region's corner lies relative to the anchor region's, in metres (x east, y north). Zero for
    /// the anchor's own view, which is the one the glow picture asks for; a neighbour's view is not registered for that.</param>
    public ServerTerrainView(Rid scenario, System.Numerics.Vector3 offset = default)
    {
        _scenario = scenario;
        if (offset == System.Numerics.Vector3.Zero)
            s_byScenario[scenario] = this;
        else
            _placement = new Transform3D(Basis.Identity, new Vector3(offset.X, offset.Z, -offset.Y));
    }

    /// <summary>Shows or hides every chunk (a neighbour beyond the draw distance is hidden, not freed).</summary>
    public void SetVisible(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        foreach (var (_, c) in _chunks)
            RenderingServer.InstanceSetVisible(c.Instance, shown);
    }

    /// <summary>The view's own placement of a new chunk instance.</summary>
    private void Place(Rid instance)
    {
        if (_placement != Transform3D.Identity) RenderingServer.InstanceSetTransform(instance, _placement);
        if (!_shown) RenderingServer.InstanceSetVisible(instance, false);
    }

    public string Kind => "server";
    public int ChunkCount => _chunks.Count;
    public int LiveResources => _chunks.Count * 2;

    public void Upload(TerrainChunk chunk)
    {
        var key = (chunk.ChunkX, chunk.ChunkY);
        if (_chunks.TryGetValue(key, out var c))
        {
            RenderingServer.MeshClear(c.Mesh);
            AddSurface(c.Mesh, key, chunk);
            RenderingServer.MeshSurfaceSetMaterial(c.Mesh, 0, MaterialFor(chunk));
            RenderingServer.InstanceSetLayerMask(c.Instance, LayerOf(key, chunk.Composition is not null));
            return;
        }
        Rid mesh = Rids.MeshCreate("terrain mesh");
        AddSurface(mesh, key, chunk);
        RenderingServer.MeshSurfaceSetMaterial(mesh, 0, MaterialFor(chunk));
        Rid inst = Rids.InstanceCreate2(mesh, _scenario, "terrain instance");
        RenderingServer.InstanceSetLayerMask(inst, LayerOf(key, chunk.Composition is not null));
        Place(inst);
        _chunks[key] = (mesh, inst);
    }

    /// <summary>The chunk encoded here, on the main thread where it is built, and handed over.</summary>
    private void AddSurface(Rid mesh, (int, int) key, TerrainChunk chunk)
    {
        var prepared = Prepare(chunk);
        PrimLayer.HandOver(mesh, prepared);   // the holding pattern
        SetBounds(key, prepared.Aabb, chunk.Composition is not null);
    }

    /// <summary>A chunk's one surface in the engine's layout (<see cref="TerrainSurface.Prepare"/>, engine-free).</summary>
    internal static PreparedSurface Prepare(TerrainChunk c) => TerrainSurface.Prepare(c);

    /// <summary>A chunk built and encoded off the main thread, handed over here (the engine calls only).</summary>
    public void HandOver(int cx, int cy, PreparedSurface surface, bool composed)
    {
        var key = (cx, cy);
        var material = (composed && _terrain is not null ? (Material)_terrain : Ground).GetRid();
        if (_chunks.TryGetValue(key, out var c))
        {
            RenderingServer.MeshClear(c.Mesh);
            PrimLayer.HandOver(c.Mesh, surface);
            SetBounds(key, surface.Aabb, composed);
            RenderingServer.MeshSurfaceSetMaterial(c.Mesh, 0, material);
            RenderingServer.InstanceSetLayerMask(c.Instance, LayerOf(key, composed));
            return;
        }
        Rid mesh = Rids.MeshCreate("terrain mesh");
        PrimLayer.HandOver(mesh, surface);
        SetBounds(key, surface.Aabb, composed);
        RenderingServer.MeshSurfaceSetMaterial(mesh, 0, material);
        Rid inst = Rids.InstanceCreate2(mesh, _scenario, "terrain instance");
        RenderingServer.InstanceSetLayerMask(inst, LayerOf(key, composed));
        Place(inst);
        _chunks[key] = (mesh, inst);
    }

    public bool TakesPrepared => true;
    public ShaderMaterial? DetailMaterial => _terrain;

    public void Remove(int cx, int cy)
    {
        if (!_chunks.Remove((cx, cy), out var c))
            return;
        _bounds.Remove((cx, cy));
        _composed.Remove((cx, cy));
        _inPicture.Remove((cx, cy));
        GlowVersion++;
        Rids.Free(c.Instance);
        Rids.Free(c.Mesh);
    }

    public void Clear()
    {
        foreach (var key in _chunks.Keys.ToList())
            Remove(key.Item1, key.Item2);
        // no chunk is drawn with the ground material now; the view made it, so the view releases it (made again if the view is used again)
        _ground?.Dispose();
        _ground = null;
    }
}
