// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Meshing;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>What makes two faces draw alike but for their tint: the texture the face shows (the placeholder's id while it loads), its transform, whether it is
/// fullbright and its glow. Faces with the same key share one merged material; the tint is carried per vertex.</summary>
public readonly record struct TexKey(Guid Tex, float RU, float RV, float OU, float OV, float Rot, bool Fullbright, float Glow);

/// <summary>
/// The draw census and the merging of still objects (part of <see cref="PrimLayer"/>).
///
/// Merging: an object that has not changed for <see cref="MergeStillSeconds"/> and whose faces are all opaque, legacy-textured with a planar-free
/// mapping, and not worn, rigged or a light, is a member of the cell (<see cref="MergeCellMetres"/> metres a side, in SL axes) its centre lies in. A
/// cell's still members are built, off the main thread, into one mesh with a surface for each <see cref="TexKey"/>, the tint of each face in the
/// vertex colour (<see cref="CellMerge"/>); the finished mesh is handed to the engine in slices of at most <see cref="MergeSliceMs"/> and swapped in
/// whole. The members keep their own instances (all the layer's other work, the textures' residency, the glow picture's choice, still runs on them);
/// while a cell is drawn merged they are hidden. When a member changes (moves, is recoloured, retextured, changes level of detail, goes away) the
/// cell is taken apart in the same step, so the next frame shows every member on its own as before, and the cell is rebuilt, without that member,
/// once the members have been quiet. Blended faces, worn objects, rigged meshes, lights and projectors, objects with a texture animation, planar-mapped
/// faces, faces with a legacy material or a PBR material, and objects larger than a quarter of a cell are never merged (a face's data carries no media
/// flag yet; a media face would have to be excluded here too). On by default, off with the start-up option --merge=off or the settings file: <see cref="MergeStill"/>.
///
/// Grouping the faces of a region's cells by texture and material into shared draws follows the idea of the official viewer's face batching
/// (llvovolume.cpp, genDrawInfo: faces sorted by what breaks a batch, one vertex buffer and one draw for each run, colour per vertex). No code is taken from it.
/// </summary>
public sealed partial class PrimLayer
{
    // ------------------------------------------------------------------ the engine materials, by what the merge needs of them

    private readonly Dictionary<Material, TexKey> _texKeyOf = new(ReferenceEqualityComparer.Instance);   // opaque, non-planar legacy-textured face materials
    private readonly Dictionary<TexKey, ShaderMaterial> _mergedMaterials = new();

    // ------------------------------------------------------------------ what each presented object looks like

    private sealed class Presented
    {
        public string Kind = "";
        public Vector3 Origin;
        public float Radius;
        public Material[] Materials = [];
        public bool[] Blended = [];
        public bool Rigged, Attached, Light;
        // the merge's data
        public bool Eligible;
        public string Reason = "";
        public IReadOnlyList<MeshFace>? Faces;
        public Transform3D Xf;
        public Rgba[] Tints = [];
        public TexKey[] Keys = [];
        public (int X, int Y, int Z) Cell;
        public int Version;
        public double StillSince;
    }
    private readonly Dictionary<uint, Presented> _presented = new();

    /// <summary>Count the layer's draws by kind and material (<see cref="DrawCensus"/>): the layer keeps what each object looks like.</summary>
    public static bool CensusOn;

    /// <summary>Merge still objects (see the class summary). On by default; <c>MergeSetting</c> (the start-up option --merge=off or the settings file) turns it off. The picture is the same either way, the draw calls are not.</summary>
    public static bool MergeStill = true;
    /// <summary>The side of a merge cell, metres.</summary>
    public static float MergeCellMetres = 32f;
    /// <summary>How long an object must be unchanged before it joins a cell, and how long a cell must be quiet before it is rebuilt, seconds.</summary>
    public static double MergeStillSeconds = 3.0;
    /// <summary>The longest the main thread spends on one cell's hand-over to the engine in one call, milliseconds (the avatars' slice).</summary>
    public const double MergeSliceMs = 4.0;
    private const int MergeMinMembers = 2;

    private static readonly Dictionary<string, Dictionary<string, int>> NoCounts = new();

    private static double Seconds => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Called at the end of presenting an object: records what it looks like, and, when merging is on, keeps its cell's membership right.</summary>
    private void AfterPresent(uint local, ObjectState o, Transform3D xf, int surfaces, string kind, IReadOnlyList<MeshFace>? faces, Rgba[] tints, bool rigged, bool attached)
    {
        if (!CensusOn && !MergeStill && _cells.Count == 0) return;
        int n = Math.Min(surfaces, MaxSurfaces);
        var row = new Presented
        {
            Kind = kind, Origin = xf.Origin, Radius = o.Scale.Length() / 2, Rigged = rigged, Attached = attached, Light = o.Light is not null,
            Materials = new Material[n], Blended = new bool[n],
        };
        Array.Copy(_faceMaterials, row.Materials, n);
        Array.Copy(_faceBlended, row.Blended, n);
        // why an object is not merged, in the order the rules are checked (counted by MergeReasons)
        string reason = !MergeStill ? "merging-off" : faces is null ? (kind == "mesh" ? "mesh" : "no-faces") : (faces.Count != surfaces || n != surfaces || n == 0) ? "face-count"
            : rigged ? "rigged" : attached ? "attached" : Carried(o) ? "moving" : o.Light is not null ? "light" : o.TextureAnim is not null ? "texture-animation"
            : row.Radius > MergeCellMetres / 4f ? "larger-than-a-quarter-cell" : "";
        bool eligible = reason == "";
        if (eligible)
        {
            row.Keys = new TexKey[n];
            for (int f = 0; f < n && eligible; f++)
            {
                if (row.Blended[f]) { eligible = false; reason = "blended-face"; }
                else if (!_texKeyOf.TryGetValue(row.Materials[f], out row.Keys[f])) { eligible = false; reason = NotMergeableBecause(local, o); }
                else if (tints[f].A < 0.999f) { eligible = false; reason = "translucent-tint"; }
            }
        }
        row.Reason = eligible ? "eligible" : reason;
        _presented.TryGetValue(local, out var old);
        if (!eligible)
        {
            if (old is { Eligible: true }) Withdraw(local, old);
            if (CensusOn || MergeStill) _presented[local] = row;
            else _presented.Remove(local);
            return;
        }
        row.Eligible = true;
        row.Faces = faces;
        row.Xf = xf;
        row.Tints = tints.Length == n ? tints : tints.Take(n).ToArray();
        row.Cell = CellMerge.CellOf(new System.Numerics.Vector3(xf.Origin.X, -xf.Origin.Z, xf.Origin.Y), MergeCellMetres);
        if (old is { Eligible: true } && old.Cell == row.Cell && ReferenceEquals(old.Faces, row.Faces) && old.Xf == row.Xf && old.Tints.AsSpan().SequenceEqual(row.Tints) && old.Keys.AsSpan().SequenceEqual(row.Keys))
        {
            old.Kind = row.Kind; old.Materials = row.Materials; old.Blended = row.Blended; old.Origin = row.Origin; old.Radius = row.Radius;   // nothing drawn has changed: the cell stays
            return;
        }
        double now = Seconds;
        if (old is { Eligible: true }) Withdraw(local, old);
        row.Version = (old?.Version ?? 0) + 1;
        row.StillSince = now;
        _presented[local] = row;
        if (!_cells.TryGetValue(row.Cell, out var cell)) _cells[row.Cell] = cell = new Cell(row.Cell);
        cell.All.Add(local);
        cell.Dirty = true;
        cell.ChangedAt = now;
        _dirtyCells.Add(cell);
    }

    /// <summary>The presented objects by why they are or are not merged: the reasons of <see cref="AfterPresent"/>, and for the eligible ones whether they are in a built cell, waiting to be still, or alone in their cell (measurement).</summary>
    public string MergeReasons()
    {
        var counts = new SortedDictionary<string, int>();
        double now = Seconds;
        foreach (var (local, p) in _presented)
        {
            string r = p.Reason;
            if (p.Eligible)
            {
                r = _cells.TryGetValue(p.Cell, out var c) && c.Built.Contains(local) ? "merged" : now - p.StillSince < MergeStillSeconds ? "eligible-not-still-yet" : _cells.TryGetValue(p.Cell, out var c2) && c2.All.Count < MergeMinMembers ? "eligible-alone-in-cell" : "eligible-cell-waiting";
            }
            counts[r] = counts.GetValueOrDefault(r) + 1;
        }
        return string.Join(",", counts.Select(kv => kv.Key + ":" + kv.Value));
    }

    /// <summary>Why an object's face material cannot be merged: what the object draws with instead of a plain, opaque, legacy-textured face (measurement).</summary>
    private string NotMergeableBecause(uint local, ObjectState o) =>
        _pbrFaces.TryGetValue(local, out var pbr) && pbr.Drawn > 0 ? "material-pbr"
        : _legacyFaces.ContainsKey(local) ? "material-legacy-normal-or-specular"
        : o.Textures is null ? "material-untextured-colour"
        : o.Textures.Default.TexGen == TexGen.Planar || o.Textures.Overrides.Any(t => t is { TexGen: TexGen.Planar }) ? "material-planar-mapping"
        : o.Colors is { } c && Enumerable.Range(0, MaxSurfaces).Any(i => c[i].A < 0.999f) ? "material-translucent-colour"
        : "material-other(glow-or-alpha-texture)";

    private void ForgetPresented(uint local)
    {
        if (_presented.Remove(local, out var old) && old.Eligible) Withdraw(local, old);
    }

    private void Withdraw(uint local, Presented old)
    {
        if (!_cells.TryGetValue(old.Cell, out var cell)) return;
        cell.All.Remove(local);
        if (cell.Built.Contains(local)) Dissolve(cell);
        cell.Dirty = true;
        cell.ChangedAt = Seconds;
        _dirtyCells.Add(cell);
    }

    // ------------------------------------------------------------------ the cells

    private sealed class Cell((int X, int Y, int Z) key)
    {
        public readonly (int X, int Y, int Z) Key = key;
        public readonly HashSet<uint> All = new();
        public HashSet<uint> Built = new();             // the members drawn by the merged mesh now (their own instances are hidden)
        public List<Rid> Meshes = new(), Instances = new();   // one mesh and instance for each run of at most MaxMeshSurfaces surfaces
        public bool Dirty;
        public double ChangedAt;
        public Task<List<(TexKey Key, MergedSurface Surface)>>? Task;
        public Dictionary<uint, int> TaskVersions = new();
        public List<(TexKey Key, MergedSurface Surface)>? Ready;   // built, being handed to the engine
        public List<Rid> NewMeshes = new();
        public int Handed;
        public List<(TexKey Key, int Surface, PartRange[] Parts)> Map = new();   // merged geometry back to its object and face
        public uint Mask = uint.MaxValue;
    }
    private readonly Dictionary<(int, int, int), Cell> _cells = new();
    private readonly HashSet<Cell> _dirtyCells = new();

    // measurement: what the merge did and the most the main thread spent in one call of UpdateMerged
    public int MergedCells => _cells.Values.Count(c => c.Instances.Count > 0 && c.Built.Count > 0);
    public int MergedObjects => _cells.Values.Sum(c => c.Built.Count);

    /// <summary>Merged cells whose instance is still alive in the engine (tests: none may remain once the layer's instances are freed).</summary>
    public int MergedCellInstances => _cells.Values.Sum(c => c.Instances.Count);
    public int MergeBuilds { get; private set; }
    public int MergeTakenApart { get; private set; }
    public int MergeDiscarded { get; private set; }
    public double WorstMergeMs { get; private set; }
    public int MergedSurfaces => _cells.Values.Sum(c => c.Built.Count > 0 ? c.Map.Count : 0);
    /// <summary>Surfaces the engine holds in the merged cells' meshes (tests: as many as <see cref="MergedSurfaces"/>), and the meshes they are in.</summary>
    public int MergedSurfacesDrawn => _cells.Values.Sum(c => c.Meshes.Sum(m => RenderingServer.MeshGetSurfaceCount(m)));
    public int MergedMeshes => _cells.Values.Sum(c => c.Meshes.Count);
    public bool MergeBusy => _cells.Values.Any(c => c.Task is not null || c.Ready is not null);
    /// <summary>Objects that could be merged now: eligible, in a cell of at least two eligible objects, quiet for the still time.</summary>
    public int MergeableObjects { get { double now = Seconds; return _cells.Values.Sum(c => { int n = c.All.Count(l => _presented.TryGetValue(l, out var p) && p.Eligible && now - p.StillSince >= MergeStillSeconds); return n >= MergeMinMembers ? n : 0; }); } }
    /// <summary>Objects in a cell of at least two eligible objects that have not been quiet for the still time yet: each joins a merged cell once it has been (tests wait for this to reach 0, so a scene with nothing to merge is settled at once).</summary>
    public int MergeWaitingObjects
    {
        get
        {
            double now = Seconds;
            return _cells.Values.Sum(c =>
            {
                int eligible = c.All.Count(l => _presented.TryGetValue(l, out var p) && p.Eligible);
                if (eligible < MergeMinMembers) return 0;
                int still = c.All.Count(l => _presented.TryGetValue(l, out var p) && p.Eligible && now - p.StillSince >= MergeStillSeconds);
                return eligible - still;
            });
        }
    }
    public int MergeEligibleObjects => _presented.Values.Count(p => p.Eligible);

    /// <summary>Takes a cell apart: its members are drawn on their own again, this step.</summary>
    private void Dissolve(Cell cell)
    {
        if (cell.Built.Count == 0) return;
        foreach (var m in cell.Built)
            if (_instances.TryGetValue(m, out var inst)) RenderingServer.InstanceSetVisible(inst.Instance, true);
        foreach (var i in cell.Instances) RenderingServer.InstanceSetVisible(i, false);
        cell.Built = new HashSet<uint>();
        cell.Map.Clear();
        MergeTakenApart++;
    }

    /// <summary>Main thread, each frame: finishes cells whose build is done (a slice of hand-over per call), starts the builds that are due, and keeps each merged
    /// cell on the glow picture's layer when any of its members is.</summary>
    public void UpdateMerged()
    {
        if (!MergeStill && _cells.Count == 0) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        double now = Seconds;
        foreach (var cell in _cells.Values)
        {
            if (cell.Task is { IsCompleted: true } done)
            {
                cell.Task = null;
                if (done.IsFaulted) { MergeDiscarded++; cell.Dirty = true; _dirtyCells.Add(cell); }
                else if (!StillCurrent(cell)) { MergeDiscarded++; cell.Dirty = true; cell.ChangedAt = now; _dirtyCells.Add(cell); }
                else { cell.Ready = done.Result; cell.Handed = 0; FreeNewMeshes(cell); }
            }
            if (cell.Ready is not null && HandOverSlice(cell, t0)) break;   // one cell's hand-over a call, within the slice
        }
        if (MergeStill && _dirtyCells.Count > 0)
            foreach (var cell in _dirtyCells.ToList())
            {
                if (cell.Task is not null || cell.Ready is not null) continue;
                if (now - cell.ChangedAt < MergeStillSeconds) continue;
                var members = cell.All.Where(l => _presented.TryGetValue(l, out var p) && p.Eligible && now - p.StillSince >= MergeStillSeconds && _instances.ContainsKey(l)).ToList();
                if (members.Count < MergeMinMembers) { if (cell.All.Count == 0 && cell.Built.Count == 0) { _dirtyCells.Remove(cell); } continue; }
                if (cell.Built.Count > 0 && cell.Built.SetEquals(members)) { cell.Dirty = false; _dirtyCells.Remove(cell); continue; }
                if (cell.Built.Count > 0) Dissolve(cell);
                StartBuild(cell, members);
                cell.Dirty = false;
                _dirtyCells.Remove(cell);
            }
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms > WorstMergeMs) WorstMergeMs = ms;
    }

    private bool StillCurrent(Cell cell)
    {
        foreach (var (l, v) in cell.TaskVersions)
            if (!_presented.TryGetValue(l, out var p) || !p.Eligible || p.Version != v || !_instances.ContainsKey(l)) return false;
        return true;
    }

    private void StartBuild(Cell cell, List<uint> members)
    {
        float size = MergeCellMetres;
        // the cell's own space: its centre, engine axes
        var origin = new System.Numerics.Vector3((cell.Key.X + 0.5f) * size, (cell.Key.Z + 0.5f) * size, -(cell.Key.Y + 0.5f) * size);
        cell.TaskVersions = new Dictionary<uint, int>();
        var work = new List<(uint Local, Presented Row)>(members.Count);
        foreach (var l in members) { var p = _presented[l]; cell.TaskVersions[l] = p.Version; work.Add((l, p)); }
        MergeBuilds++;
        cell.Task = Task.Run(() =>
        {
            var groups = new Dictionary<TexKey, List<MergePart>>();
            foreach (var (local, p) in work)
            {
                var b = p.Xf.Basis;
                var m = new System.Numerics.Matrix4x4(b.Column0.X, b.Column0.Y, b.Column0.Z, 0, b.Column1.X, b.Column1.Y, b.Column1.Z, 0, b.Column2.X, b.Column2.Y, b.Column2.Z, 0,
                    p.Xf.Origin.X - origin.X, p.Xf.Origin.Y - origin.Y, p.Xf.Origin.Z - origin.Z, 1);
                for (int f = 0; f < p.Keys.Length; f++)
                {
                    if (!groups.TryGetValue(p.Keys[f], out var list)) groups[p.Keys[f]] = list = new List<MergePart>();
                    var t = p.Tints[f];
                    list.Add(new MergePart(p.Faces![f], m, t.R, t.G, t.B, t.A, local, f));
                }
            }
            var result = new List<(TexKey, MergedSurface)>();
            foreach (var (key, parts) in groups.OrderBy(g => g.Key.Tex).ThenBy(g => g.Key.Glow).ThenBy(g => g.Key.Fullbright))
                foreach (var s in CellMerge.Build(parts)) result.Add((key, s));
            return result;
        });
    }
    /// <summary>The most surfaces the engine lets one mesh hold (RenderingServer.MAX_MESH_SURFACES, which the engine's mesh storage checks in mesh_add_surface).
    /// The official viewer draws every face whatever their number, so a cell with more surfaces is drawn from several meshes, each with an instance.</summary>
    public const int MaxMeshSurfaces = 256;

    private static void FreeNewMeshes(Cell cell)
    {
        foreach (var m in cell.NewMeshes) Rids.Free(m);
        cell.NewMeshes = new List<Rid>();
    }

    /// <summary>Gives the finished cell's surfaces to the engine until the slice is used; when all are in, swaps the cell's meshes and hides its members.
    /// True when the slice is spent.</summary>
    private bool HandOverSlice(Cell cell, long sliceStart)
    {
        var ready = cell.Ready!;
        while (cell.Handed < ready.Count)
        {
            if (cell.Handed % MaxMeshSurfaces == 0) cell.NewMeshes.Add(Rids.MeshCreate("merged cell mesh"));   // a mesh holds MaxMeshSurfaces at most: the next run goes in another
            var (key, s) = ready[cell.Handed];
            var mesh = cell.NewMeshes[^1];
            HandOver(mesh, s.Surface);
            RenderingServer.MeshSurfaceSetMaterial(mesh, cell.Handed % MaxMeshSurfaces, MergedMaterial(key).GetRid());
            cell.Handed++;
            if ((System.Diagnostics.Stopwatch.GetTimestamp() - sliceStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= MergeSliceMs && cell.Handed < ready.Count) return true;
        }
        // all in: the members may have changed while the surfaces were handed over one slice at a time
        if (!StillCurrent(cell)) { FreeNewMeshes(cell); cell.Ready = null; MergeDiscarded++; cell.Dirty = true; cell.ChangedAt = Seconds; _dirtyCells.Add(cell); return false; }
        float size = MergeCellMetres;
        var at = new Vector3((cell.Key.X + 0.5f) * size, (cell.Key.Z + 0.5f) * size, -(cell.Key.Y + 0.5f) * size);
        var oldMeshes = cell.Meshes;
        for (int i = 0; i < cell.NewMeshes.Count; i++)
        {
            if (i >= cell.Instances.Count) cell.Instances.Add(Rids.InstanceCreate2(cell.NewMeshes[i], _scenario, "merged cell"));
            else RenderingServer.InstanceSetBase(cell.Instances[i], cell.NewMeshes[i]);
            RenderingServer.InstanceSetTransform(cell.Instances[i], new Transform3D(Basis.Identity, at));
        }
        for (int i = cell.Instances.Count - 1; i >= cell.NewMeshes.Count; i--)   // fewer runs than before: the spare instances go
        {
            Rids.Free(cell.Instances[i]);
            cell.Instances.RemoveAt(i);
        }
        cell.Meshes = cell.NewMeshes;
        foreach (var old in oldMeshes) Rids.Free(old);
        cell.NewMeshes = new List<Rid>();
        cell.Built = new HashSet<uint>(cell.TaskVersions.Keys);
        cell.Map = new List<(TexKey, int, PartRange[])>();
        for (int i = 0; i < ready.Count; i++) cell.Map.Add((ready[i].Key, i, ready[i].Surface.Parts));
        cell.Ready = null;
        cell.Mask = uint.MaxValue;
        SyncMergedMask(cell);
        foreach (var i in cell.Instances) RenderingServer.InstanceSetVisible(i, true);
        foreach (var m in cell.Built)
            if (_instances.TryGetValue(m, out var inst)) RenderingServer.InstanceSetVisible(inst.Instance, false);
        return true;
    }

    /// <summary>The material a merged surface is drawn with: the face shader reading the vertex colour as the tint.</summary>
    private ShaderMaterial MergedMaterial(TexKey key)
    {
        if (_mergedMaterials.TryGetValue(key, out var m)) return m;
        bool ready = key.Tex != Guid.Empty && _gpuTextures.ContainsKey(key.Tex);
        m = new ShaderMaterial { Shader = KeptShaders.FaceTinted(key.Fullbright) };
        VariantDisposal.SetShaderParameter(m, EngineNames.Tex, ready ? _gpuTextures[key.Tex] : Placeholder());
        m.SetShaderParameter(EngineNames.Tint, new Vector4(1f, 1f, 1f, 1f));
        m.SetShaderParameter(EngineNames.Rep, new Vector2(key.RU, key.RV));
        m.SetShaderParameter(EngineNames.Off, new Vector2(key.OU, key.OV));
        m.SetShaderParameter(EngineNames.Rot, key.Rot);
        m.SetShaderParameter(EngineNames.Planar, false);
        if (key.Glow > 0f) m.SetShaderParameter(EngineNames.GlowW, key.Glow);
        return _mergedMaterials[key] = m;
    }

    private void SyncMergedMask(Cell cell)
    {
        if (cell.Instances.Count == 0 || cell.Built.Count == 0) return;
        uint mask = 1u;
        foreach (var m in cell.Built)
            if (_inGlowPicture.Contains(m)) { mask |= GlowPass.InGlowPicture; break; }
        if (mask == cell.Mask) return;
        cell.Mask = mask;
        foreach (var i in cell.Instances) RenderingServer.InstanceSetLayerMask(i, mask);
    }

    /// <summary>After the glow picture's choice: each merged cell is on the glow layer when any member is (a superset of what its members chose, so the picture is the same).</summary>
    private void SyncMergedMasks()
    {
        foreach (var cell in _cells.Values) SyncMergedMask(cell);
    }

    /// <summary>The object and face a triangle of a merged cell's surface belongs to (for picking), or null.</summary>
    public (uint Local, int Face)? ObjectOfMergedTriangle((int X, int Y, int Z) cell, int surface, int triangle)
    {
        if (!_cells.TryGetValue(cell, out var c) || surface < 0 || surface >= c.Map.Count) return null;
        foreach (var r in c.Map[surface].Parts)
            if (triangle >= r.FirstTriangle && triangle < r.FirstTriangle + r.Triangles) return (r.Local, r.FaceIndex);
        return null;
    }

    /// <summary>The cells that are drawn merged now (their keys), for tests.</summary>
    public IReadOnlyList<(int X, int Y, int Z)> MergedCellKeys => _cells.Values.Where(c => c.Built.Count > 0).Select(c => c.Key).ToList();

    /// <summary>Hands over the merged cells' instances and forgets them (the cells' meshes stay for <see cref="FreeMerged"/>).</summary>
    private void TakeMergedInstances(List<Rid> into)
    {
        foreach (var c in _cells.Values)
            if (c.Instances.Count > 0) { into.AddRange(c.Instances); c.Instances = new List<Rid>(); }
    }

    private void FreeMerged()
    {
        foreach (var c in _cells.Values)
        {
            foreach (var i in c.Instances) Rids.Free(i);
            foreach (var m in c.Meshes) Rids.Free(m);
            FreeNewMeshes(c);
        }
        _cells.Clear();
        _dirtyCells.Clear();
        _presented.Clear();
        foreach (var m in _mergedMaterials.Values) m.Dispose();
        _mergedMaterials.Clear();
        _texKeyOf.Clear();
    }

    // ------------------------------------------------------------------ the census

    /// <summary>
    /// What the layer holds, by kind and material, and what merging would leave of it. Faces are counted as the engine draws them: one draw call
    /// each for an unblended face (the engine's own culling is not applied: the counts are of everything held), blended faces separately.
    /// <paramref name="cam"/> adds the same counts for the objects whose bounding sphere meets the camera's view volume.
    /// For each cell size the draws left when the opaque faces of still objects are merged per (cell, material): <c>eligible</c> follows the rule this layer
    /// merges by, <c>any_opaque</c> merges every unblended, unrigged, unworn face whatever its material, with its material as the key (colour aside).
    /// </summary>
    public object DrawCensus(Camera3D? cam, float[] cellSizes)
    {
        var planes = cam?.GetFrustum();
        bool InView(Presented p)
        {
            if (planes is null) return true;
            foreach (var pl in planes) if (pl.DistanceTo(p.Origin) > p.Radius) return false;
            return true;
        }
        var texOther = new HashSet<Material>(_texMaterials.Values, ReferenceEqualityComparer.Instance);
        var legacyMats = new HashSet<Material>(_legacyMaterials.Values, ReferenceEqualityComparer.Instance);
        var pbrMats = new HashSet<Material>(_pbrMaterials.Values, ReferenceEqualityComparer.Instance);
        string ClassOf(Material m) =>
            _texKeyOf.ContainsKey(m) ? "legacy_textured_opaque" : texOther.Contains(m) ? "legacy_textured_blended_or_planar"
            : legacyMats.Contains(m) ? "legacy_material" : pbrMats.Contains(m) ? "pbr" : m is StandardMaterial3D ? "colour_only" : "other";
        object KeyOf(Material m) => _texKeyOf.TryGetValue(m, out var k) ? k : m;

        var kinds = new[] { "prim", "sculpt", "mesh" };
        var byKind = kinds.ToDictionary(k => k, k => new int[5]);   // objects, faces, opaque, blended, faces in view
        var byClass = new Dictionary<string, int>();
        var distinct = new HashSet<object>(); var distinctNoTint = new HashSet<object>();
        int objects = 0, faces = 0, opaque = 0, blended = 0, inViewFaces = 0, inViewOpaque = 0, rigged = 0, attached = 0, lights = 0;
        foreach (var p in _presented.Values)
        {
            objects++;
            if (p.Rigged) rigged++;
            if (p.Attached) attached++;
            if (p.Light) lights++;
            bool view = InView(p);
            var row = byKind.TryGetValue(p.Kind, out var r) ? r : byKind[p.Kind] = new int[5];
            row[0]++;
            for (int f = 0; f < p.Materials.Length; f++)
            {
                faces++; row[1]++;
                if (view) { inViewFaces++; row[4]++; }
                if (p.Blended[f]) { blended++; row[3]++; }
                else { opaque++; row[2]++; if (view) inViewOpaque++; }
                string c = ClassOf(p.Materials[f]);
                byClass[c] = byClass.GetValueOrDefault(c) + 1;
                distinct.Add(p.Materials[f]);
                distinctNoTint.Add(KeyOf(p.Materials[f]));
            }
        }
        object Merged(float size, bool eligibleOnly)
        {
            var groups = new HashSet<(int, int, int, object)>();
            int left = 0, mergedFaces = 0;
            foreach (var p in _presented.Values)
            {
                bool ok = eligibleOnly
                    ? p.Materials.Length > 0 && !p.Rigged && !p.Attached && !p.Light && p.Radius <= size / 4f && p.Kind != "mesh" && p.Materials.Select((m, f) => !p.Blended[f] && _texKeyOf.ContainsKey(m)).All(x => x)
                    : p.Materials.Length > 0 && !p.Rigged && !p.Attached && !p.Light && p.Radius <= size / 4f;
                var cell = CellMerge.CellOf(new System.Numerics.Vector3(p.Origin.X, -p.Origin.Z, p.Origin.Y), size);
                for (int f = 0; f < p.Materials.Length; f++)
                {
                    if (!ok || p.Blended[f]) { left++; continue; }
                    mergedFaces++;
                    groups.Add((cell.X, cell.Y, cell.Z, KeyOf(p.Materials[f])));
                }
            }
            return new { cell_m = size, draws_left = left + groups.Count, merged_faces = mergedFaces, merged_draws = groups.Count };
        }
        return new
        {
            objects, faces, opaque_faces = opaque, blended_faces = blended, faces_in_view = inViewFaces, opaque_faces_in_view = inViewOpaque,
            rigged_objects = rigged, worn_objects = attached, light_objects = lights,
            by_kind = byKind.ToDictionary(k => k.Key, k => new { objects = k.Value[0], faces = k.Value[1], opaque = k.Value[2], blended = k.Value[3], faces_in_view = k.Value[4] }),
            by_material_class = byClass,
            distinct_materials = distinct.Count, distinct_materials_ignoring_tint = distinctNoTint.Count,
            merged_eligible = cellSizes.Select(s => Merged(s, true)).ToArray(),
            merged_any_opaque = cellSizes.Select(s => Merged(s, false)).ToArray(),
            merge = new { on = MergeStill, merged_cells = MergedCells, merged_objects = MergedObjects, merged_surfaces = MergedSurfaces, eligible_objects = MergeEligibleObjects, builds = MergeBuilds, taken_apart = MergeTakenApart, discarded = MergeDiscarded, worst_main_thread_ms = Math.Round(WorstMergeMs, 3) },
        };
    }
}
