// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// HARNESS ONLY: the ADR-003 comparison view, made only by the render harness (--tr-view=nodes);
/// the viewer draws terrain through ServerTerrainView. It packs arrays in the engine (ArrayMesh.AddSurfaceFromArrays)
/// with every argument held; SurfaceCallScanTests keeps it and that call out of shipping code. Its
/// collections are its own kept set (below), outside KeptCollections, which holds only the shipping paths' set; the
/// kept-collections scan treats this file as harness, as TestRender.cs.
/// Scene-node representation: one MeshInstance3D + ArrayMesh per chunk, under one parent node. A replaced or removed mesh
/// is disposed explicitly: measured (lifecycle, 20 region changes), leaving it to the .NET GC kept the C# wrappers,
/// and with them the meshes' render buffers, alive (+17 objects, +2.4 MB buffer memory at the end of the run).
/// </summary>
public sealed partial class NodeTerrainView : Node3D, ITerrainView
{
    // each chunk's node with the mesh this view made for it: the mesh is kept here, not read back from the node, because
    // MeshInstance3D.Mesh makes the binding hand out a managed wrapper that its finalizer may be freeing at that moment
    // ("Handle is not initialized", seen as an engine error in 3 of 26 lifecycle runs)
    private readonly Dictionary<(int, int), (MeshInstance3D Node, ArrayMesh Mesh)> _chunks = new();
    private readonly StandardMaterial3D _material = ChunkArrays.Material();
    private ShaderMaterial? _terrain;

    public string Kind => "nodes";

    /// <summary>The harness's comparison view packs arrays in the engine from the chunk; the presenter builds its
    /// chunks on the main thread as before.</summary>
    public bool TakesPrepared => false;
    public ShaderMaterial? DetailMaterial => _terrain;
    public void HandOver(int cx, int cy, PreparedSurface surface, bool composed) => throw new System.NotSupportedException("the node view takes chunks, not prepared surfaces");

    public void SetDetailTextures(Texture2D[]? layers, Vector2 originOffset = default)
    {
        _terrain?.Dispose();
        _terrain = layers is null ? null : ChunkArrays.TerrainMaterial(layers, originOffset);
    }
    public int ChunkCount => _chunks.Count;
    public int LiveResources => _chunks.Count * 2;

    public void Upload(TerrainChunk chunk)
    {
        var mesh = new ArrayMesh();
        // the surface array is held (and freed) by 'using': passed inline, only its native handle is referenced, and the
        // GC may finalize it DURING the native call (seen once in 4 lifecycle runs: array.cpp _ref !success, then 0xC0000005).
        // the blend shapes and LODs too: the binding makes its own when none is given and does not hold them
        // this view's own arrays, typed blend shapes and LODs, made once and never
        // disposed, pinned while in use (the typed array through the untyped one under it) and cleared after the call
        using (var held = new HeldValues())   // the packed arrays made from the chunk's, held through the sets and the call
        {
            var k = Kept;
            unsafe
            {
                fixed (byte* pa = &KeptCollections.Pin(k.Arrays))
                fixed (byte* pb = &KeptCollections.Pin(k.BlendShapesUntyped))
                fixed (byte* pt = &KeptCollections.Pin(k.BlendShapes))
                fixed (byte* pl = &KeptCollections.Pin(k.Lods))
                {
                    try
                    {
                        ChunkArrays.Fill(k.Arrays, chunk, held);
                        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, k.Arrays, k.BlendShapes, k.Lods);
                    }
                    finally { k.Clear(); }
                }
            }
            System.GC.KeepAlive(held);
        }
        mesh.SurfaceSetMaterial(0, chunk.Composition is not null && _terrain is not null ? _terrain : _material);
        if (_chunks.TryGetValue((chunk.ChunkX, chunk.ChunkY), out var kept))
        {
            kept.Node.Mesh = mesh;
            _chunks[(chunk.ChunkX, chunk.ChunkY)] = (kept.Node, mesh);
            kept.Mesh.Dispose();
        }
        else
        {
            var mi = new MeshInstance3D { Mesh = mesh, Name = $"terrain_{chunk.ChunkX}_{chunk.ChunkY}" };
            AddChild(mi);
            _chunks[(chunk.ChunkX, chunk.ChunkY)] = (mi, mesh);
        }
    }

    public void Remove(int cx, int cy)
    {
        if (!_chunks.Remove((cx, cy), out var kept))
            return;
        RemoveChild(kept.Node);
        kept.Node.Mesh = null;
        kept.Mesh.Dispose();
        kept.Node.Free();
    }

    public void Clear()
    {
        foreach (var key in _chunks.Keys.ToList())
            Remove(key.Item1, key.Item2);
    }

    public override void _ExitTree() => Clear();

    /// <summary>The view's arrays for <c>ArrayMesh.AddSurfaceFromArrays</c> (pattern, main thread only): its blend
    /// shapes are a typed array, pinned through the untyped array under it. Kept for the process.</summary>
    private sealed class KeptSet
    {
        public readonly global::Godot.Collections.Array Arrays = new();
        public readonly global::Godot.Collections.Array<global::Godot.Collections.Array> BlendShapes = new();
        public readonly global::Godot.Collections.Dictionary Lods = new();
        public global::Godot.Collections.Array BlendShapesUntyped => (global::Godot.Collections.Array)BlendShapes;

        public void Clear()
        {
            Arrays.Clear();
            BlendShapes.Clear();
            Lods.Clear();
        }
    }

    private static KeptSet? _kept;
    private static KeptSet Kept => _kept ??= new KeptSet();
}
