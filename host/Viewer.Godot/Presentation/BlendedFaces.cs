// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// Blended faces in the official viewer's order (<see cref="BlendOrder"/>). The engine sorts its transparent list per
/// instance, by the distance from the eye to the instance, farthest first; every surface of one instance shares that
/// place. So each blended face is drawn by an instance of its own: an object's first blended face by the object's own
/// instance, every further one by a face instance, and each instance is given the place its face's key wants (its
/// sorting offset, against the distance to the instance's origin), refreshed every frame from the camera. A face
/// instance draws a mesh of its one surface, and the own instance a mesh of the object's surfaces less those drawn by
/// face instances (part meshes, made once per mesh and set of surfaces from the object's surface data), so each blended
/// face is handed to the engine once. When the surface data is not at hand (a mesh whose decoded geometry was let go),
/// the object's whole mesh is used instead, every surface an instance does not draw given the skip material (which
/// draws nothing, but is still handed to the engine). The water sits between the far side and the camera's side
/// (<see cref="EnvironmentLayer.OrderWater"/>); a face crossing it is drawn twice, each copy cut to its side by the
/// face shaders' water cut (the instance parameter water_clip). The own instance's surface materials are set here.
/// Main thread only.
/// </summary>
internal sealed class BlendedFaces(Rid scenario)
{
    /// <summary>The layer mask an object's face instances are given when they are made (the glow picture's layer is part of it).</summary>
    public Func<uint, uint>? LayerOf { get; init; }

    /// <summary>Gives every face instance of the object this layer mask (the object's own instance included).</summary>
    public void SetLayerMask(uint local, uint mask)
    {
        if (!_objects.TryGetValue(local, out var units)) return;
        foreach (var u in units) if (!u.Own) RenderingServer.InstanceSetLayerMask(u.Instance, mask);
    }

    /// <summary>One place in the order: the instance drawing one blended surface of an object, on one side of the water.</summary>
    private sealed class Unit
    {
        public Rid Instance, Mesh;   // Mesh: the mesh the instance draws (a part mesh, or the object's whole mesh)
        public bool Own;          // the object's own instance (not freed here)
        public int Surface;
        public BlendOrder.Side Side;
        public bool Cut;          // drawn twice (crossing the water): cut to its side
        public Vector3 Centre, Size, Origin;   // the face's box (engine axes, world) and the instance's origin
        public float Applied = float.NaN;      // the sorting offset last given to the engine
        public bool CutApplied;
    }

    /// <summary>A part mesh: some surfaces of a mesh (bit i: surface i), and where each of the mesh's surfaces is in
    /// it (-1: not in it).</summary>
    private sealed record Part(Rid Mesh, int[] Index, long Bytes);

    private readonly Dictionary<uint, List<Unit>> _objects = new();
    private readonly List<Unit> _scratch = new();
    private readonly Dictionary<(Rid Mesh, uint Keep), Part> _parts = new();
    // own instances drawing a part mesh: the instance, and the part it draws
    private readonly Dictionary<uint, (Rid Instance, Rid Mesh)> _ownParts = new();
    private readonly Part?[] _faceParts = new Part?[MaxPlaces];
    private const int MaxPlaces = 64;

    /// <summary>Objects with a blended face; places in the order (blended faces, a crossing face counting twice);
    /// face instances made here; sorting offsets handed to the engine since the layer was made.</summary>
    public int Objects => _objects.Count;
    public int Units { get; private set; }
    public int FaceInstances { get; private set; }
    /// <summary>Part meshes held, made since the layer was made, and their surface bytes (vertex, attribute and index
    /// data, as handed to the engine); objects drawn with their whole mesh and skipped surfaces because their surface
    /// data was not at hand.</summary>
    public int PartMeshes => _parts.Count;
    public int PartsMade { get; private set; }
    public long PartBytes { get; private set; }
    public long WholeMeshFallbacks { get; private set; }
    public long OffsetsSet { get; private set; }
    /// <summary>The last frame's ordering: its time (ms) and the offsets it handed over.</summary>
    public double LastMs { get; private set; }
    public int LastSet { get; private set; }

    /// <summary>
    /// The object's surface materials (<paramref name="materials"/>) and blended surfaces (<paramref name="blended"/>,
    /// by surface) after it is presented: its places in the order, face instances made, reused or freed, the mesh each
    /// instance draws, and the own instance's surface materials. <paramref name="bounds"/>: each surface's box in the
    /// mesh's space (centre, size); null when unknown (the object is then left to the engine's own order).
    /// <paramref name="shaderFace"/>: whether a surface's material can be cut at the water (the face shaders; the
    /// untextured colour materials cannot, and are drawn once). <paramref name="source"/>: the mesh's surface data, asked
    /// for only when a part mesh it needs is not made yet; null, or returning null, when it is not at hand.
    /// </summary>
    public void Set(uint local, Rid own, Rid mesh, int surfaces, Transform3D xf, Material[] materials, bool[] blended, bool[] shaderFace,
        (Vector3 Centre, Vector3 Size)[]? bounds, float? water, Vector3? objScale, Func<PreparedMeshLod?>? source)
    {
        _scratch.Clear();
        if (bounds is not null)
            for (int f = 0; f < surfaces && f < bounds.Length; f++)
            {
                if (!blended[f]) continue;
                var centre = xf * bounds[f].Centre;
                var b = xf.Basis;
                var s = bounds[f].Size;
                var size = new Vector3(
                    Math.Abs(b.X.X) * s.X + Math.Abs(b.Y.X) * s.Y + Math.Abs(b.Z.X) * s.Z,
                    Math.Abs(b.X.Y) * s.X + Math.Abs(b.Y.Y) * s.Y + Math.Abs(b.Z.Y) * s.Z,
                    Math.Abs(b.X.Z) * s.X + Math.Abs(b.Y.Z) * s.Y + Math.Abs(b.Z.Z) * s.Z);
                var (above, below) = BlendOrder.Sides(centre.Y - size.Y / 2, centre.Y + size.Y / 2, water);
                bool cut = above && below && shaderFace[f];
                if (above || !below) _scratch.Add(new Unit { Surface = f, Side = BlendOrder.Side.Above, Cut = cut, Centre = centre, Size = size, Origin = xf.Origin });
                if (below && (cut || !above)) _scratch.Add(new Unit { Surface = f, Side = BlendOrder.Side.Below, Cut = cut, Centre = centre, Size = size, Origin = xf.Origin });
            }
        _objects.TryGetValue(local, out var had);
        if (_scratch.Count == 0)
        {
            OwnDraws(local, own, mesh, null, surfaces, materials, 0, objScale);
            if (had is not null) Remove(local, own);
            return;
        }
        if (_scratch.Count > MaxPlaces) _scratch.RemoveRange(MaxPlaces, _scratch.Count - MaxPlaces);
        // the own instance draws its first blended surface and every surface that is not blended; the other blended
        // surfaces are drawn by their face instances. Each instance draws a part mesh of its surfaces, when the
        // surface data is at hand; otherwise every instance draws the whole mesh with the surfaces it does not draw
        // skipped
        uint further = 0;
        for (int i = 1; i < _scratch.Count; i++)
            if (_scratch[i].Surface != _scratch[0].Surface) further |= 1u << _scratch[i].Surface;
        uint all = surfaces >= 32 ? uint.MaxValue : (1u << surfaces) - 1;
        Part? ownPart = null;
        bool parts = true, asked = false;
        PreparedMeshLod? data = null;   // the surface data, asked for once
        if (further != 0) parts = (ownPart = PartFor(mesh, all & ~further, source, ref data, ref asked)) is not null;
        for (int i = 1; i < _scratch.Count && parts; i++)
            parts = (_faceParts[i] = PartFor(mesh, 1u << _scratch[i].Surface, source, ref data, ref asked)) is not null;
        if (!parts) WholeMeshFallbacks++;
        bool rebased = OwnDraws(local, own, mesh, ownPart, surfaces, materials, parts ? 0 : further, objScale);
        var units = new List<Unit>(_scratch.Count);
        for (int i = 0; i < _scratch.Count; i++)
        {
            var u = _scratch[i];
            var old = had is not null && i < had.Count ? had[i] : null;
            if (i == 0)
            {
                u.Own = true;
                u.Instance = own;
                u.Mesh = ownPart?.Mesh ?? mesh;
                if (old is not null && old.Own && old.Instance == own) { u.Applied = rebased ? float.NaN : old.Applied; u.CutApplied = old.CutApplied || rebased; }
                if (old is not null && !old.Own) FreeFace(old);
            }
            else
            {
                var part = parts ? _faceParts[i] : null;
                u.Mesh = part?.Mesh ?? mesh;
                if (old is not null && !old.Own)
                {
                    u.Instance = old.Instance; u.Applied = old.Applied; u.CutApplied = old.CutApplied;
                    if (old.Mesh != u.Mesh)
                    {
                        RenderingServer.InstanceSetBase(u.Instance, u.Mesh);   // another surface, mesh or LOD
                        u.Applied = float.NaN;   // its place and cut given again
                        u.CutApplied |= old.CutApplied;
                    }
                }
                else
                {
                    u.Instance = Rids.InstanceCreate2(u.Mesh, scenario, "blended face instance");
                    if (LayerOf is { } layerOf) RenderingServer.InstanceSetLayerMask(u.Instance, layerOf(local));
                    FaceInstances++;
                    u.CutApplied = false;
                }
                RenderingServer.InstanceSetTransform(u.Instance, xf);
                if (objScale is { } sc) RenderingServer.InstanceGeometrySetShaderParameter(u.Instance, EngineNames.ObjScale, sc);
                if (part is not null)
                    RenderingServer.InstanceSetSurfaceOverrideMaterial(u.Instance, 0, materials[u.Surface].GetRid());
                else
                    for (int f = 0; f < surfaces; f++)
                        RenderingServer.InstanceSetSurfaceOverrideMaterial(u.Instance, f, (f == u.Surface ? materials[f] : KeptShaders.SkipMaterial).GetRid());
            }
            if (u.Cut || u.CutApplied)
            {
                // which side the copy keeps: +1 above the water, -1 below it, 0 the whole face
                var clip = new Vector2(u.Cut ? (u.Side == BlendOrder.Side.Above ? 1f : -1f) : 0f, water ?? 0f);
                RenderingServer.InstanceGeometrySetShaderParameter(u.Instance, EngineNames.WaterClip, clip);
                u.CutApplied = u.Cut;
            }
            units.Add(u);
        }
        Array.Clear(_faceParts);
        if (had is not null)
            for (int i = units.Count; i < had.Count; i++)
                if (!had[i].Own) FreeFace(had[i]);
        Units += units.Count - (had?.Count ?? 0);
        _objects[local] = units;
    }

    /// <summary>
    /// The own instance's mesh and surface materials: <paramref name="part"/>'s mesh when it draws a part (its surfaces'
    /// materials at their places in the part), else the object's whole mesh with every surface's material, those in
    /// <paramref name="skipped"/> (drawn by face instances) given the skip material. Whether the instance's mesh changed
    /// (its object scale is then given again here, and its place and water cut by the caller).
    /// </summary>
    private bool OwnDraws(uint local, Rid own, Rid mesh, Part? part, int surfaces, Material[] materials, uint skipped, Vector3? objScale)
    {
        var want = part?.Mesh ?? mesh;
        var now = _ownParts.TryGetValue(local, out var had) && had.Instance == own ? had.Mesh : mesh;
        bool rebased = now != want;
        if (rebased)
        {
            RenderingServer.InstanceSetBase(own, want);
            if (objScale is { } sc) RenderingServer.InstanceGeometrySetShaderParameter(own, EngineNames.ObjScale, sc);
        }
        if (part is null) _ownParts.Remove(local);
        else _ownParts[local] = (own, part.Mesh);
        for (int f = 0; f < surfaces; f++)
        {
            int at = part is null ? f : f < part.Index.Length ? part.Index[f] : -1;
            if (at < 0) continue;
            var m = f < 32 && (skipped & (1u << f)) != 0 ? KeptShaders.SkipMaterial : materials[f];
            RenderingServer.InstanceSetSurfaceOverrideMaterial(own, at, m.GetRid());
        }
        return rebased;
    }

    /// <summary>The part mesh of <paramref name="mesh"/>'s surfaces in <paramref name="keep"/> (bit i: surface i), made
    /// from <paramref name="source"/> the first time it is wanted and kept with the mesh; null when the surface data is
    /// not at hand. <paramref name="data"/> and <paramref name="asked"/>: the source's answer, so it is asked once for
    /// an object's parts.</summary>
    private Part? PartFor(Rid mesh, uint keep, Func<PreparedMeshLod?>? source, ref PreparedMeshLod? data, ref bool asked)
    {
        if (_parts.TryGetValue((mesh, keep), out var p)) return p;
        if (!asked) { data = source?.Invoke(); asked = true; }
        if (data is not { } lod) return null;
        var kept = new List<PreparedSurface>();
        var index = new int[lod.Surfaces.Count];
        long bytes = 0;
        for (int f = 0; f < index.Length; f++)
        {
            if (f >= 32 || (keep & (1u << f)) == 0) { index[f] = -1; continue; }
            index[f] = kept.Count;
            var s = lod.Surfaces[f];
            kept.Add(s);
            bytes += s.VertexData.Length + s.AttributeData.Length + s.IndexData.Length;
        }
        if (kept.Count == 0) return null;
        p = new Part(PrimLayer.HandOver(new PreparedMeshLod(kept), out _), index, bytes);
        _parts[(mesh, keep)] = p;
        PartsMade++;
        PartBytes += bytes;
        return p;
    }

    /// <summary>The object moved from <paramref name="was"/> to <paramref name="now"/> (an attachment following its joint): its face
    /// instances go with it, and its places in the order are carried to the new position.</summary>
    public void Move(uint local, Transform3D was, Transform3D now)
    {
        if (!_objects.TryGetValue(local, out var units)) return;
        var delta = now * was.AffineInverse();
        foreach (var u in units)
        {
            u.Centre = delta * u.Centre;
            u.Origin = now.Origin;
            if (!u.Own) RenderingServer.InstanceSetTransform(u.Instance, now);
        }
    }

    /// <summary>Shows or hides the object's face instances (its own instance is the caller's).</summary>
    public void SetVisible(uint local, bool visible)
    {
        if (!_objects.TryGetValue(local, out var units)) return;
        foreach (var u in units)
            if (!u.Own) RenderingServer.InstanceSetVisible(u.Instance, visible);
    }

    /// <summary>The object's places are gone (removed, or no face blended any more): face instances freed; the own
    /// instance, when it is kept (<paramref name="own"/> valid), goes back to the engine's own order.</summary>
    public void Remove(uint local, Rid own = default)
    {
        _ownParts.Remove(local);   // (a kept own instance was given its whole mesh back by OwnDraws)
        if (!_objects.Remove(local, out var units)) return;
        Units -= units.Count;
        foreach (var u in units)
        {
            if (!u.Own) FreeFace(u);
            else if (own.IsValid && u.Instance == own)
            {
                RenderingServer.InstanceSetPivotData(own, 0f, true);
                if (u.CutApplied) RenderingServer.InstanceGeometrySetShaderParameter(own, EngineNames.WaterClip, Vector2.Zero);
            }
        }
    }

    private void FreeFace(Unit u)
    {
        Rids.Free(u.Instance);
        FaceInstances--;
    }

    /// <summary>Hands over every face instance (not the own ones, which are the layer's) and forgets the places; the part meshes stay for <see cref="Clear"/>.</summary>
    public void TakeFaceInstances(List<Rid> into)
    {
        foreach (var units in _objects.Values)
            foreach (var u in units)
                if (!u.Own) { into.Add(u.Instance); FaceInstances--; }
        _objects.Clear();
        _ownParts.Clear();
        Units = 0;
    }

    /// <summary>Every face instance and part mesh freed (region change, shutdown); the own instances are the layer's.</summary>
    public void Clear()
    {
        foreach (var units in _objects.Values)
            foreach (var u in units)
                if (!u.Own) FreeFace(u);
        _objects.Clear();
        _ownParts.Clear();
        foreach (var p in _parts.Values) Rids.Free(p.Mesh);
        _parts.Clear();
        PartBytes = 0;
        Units = 0;
    }

    /// <summary>Each object with a blended face: its places in the order and the centre of its first face's box (engine
    /// axes; measurement and review views).</summary>
    public IEnumerable<(uint Local, int Places, Vector3 Centre)> Report()
    {
        foreach (var (local, units) in _objects) yield return (local, units.Count, units[0].Centre);
    }

    /// <summary>The objects with a place in the order (their re-presentation follows a water change).</summary>
    public IEnumerable<uint> Locals => _objects.Keys;

    /// <summary>
    /// Every place given its depth for this view: the key along the view axis <paramref name="at"/> from
    /// <paramref name="eye"/>, beyond the water on its far side; handed to the engine as the instance's sorting offset
    /// (the engine's depth is the distance from the eye to the instance's origin, less the offset) when it moved.
    /// </summary>
    public void Order(Vector3 eye, Vector3 at, float? water)
    {
        long t0 = Stopwatch.GetTimestamp();
        bool eyeAbove = water is not { } h || eye.Y >= h;
        var e = new System.Numerics.Vector3(eye.X, eye.Y, eye.Z);
        var a = new System.Numerics.Vector3(at.X, at.Y, at.Z);
        int set = 0;
        foreach (var units in _objects.Values)
            foreach (var u in units)
            {
                float key = BlendOrder.Key(e, a, new System.Numerics.Vector3(u.Centre.X, u.Centre.Y, u.Centre.Z), new System.Numerics.Vector3(u.Size.X, u.Size.Y, u.Size.Z));
                float offset = eye.DistanceTo(u.Origin) - BlendOrder.Depth(key, u.Side, eyeAbove);
                if (!(Math.Abs(offset - u.Applied) <= 1e-3f))
                {
                    RenderingServer.InstanceSetPivotData(u.Instance, offset, false);
                    u.Applied = offset;
                    set++;
                }
            }
        LastSet = set;
        OffsetsSet += set;
        LastMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }
}
