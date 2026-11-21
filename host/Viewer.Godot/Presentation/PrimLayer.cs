// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Meshing;
using Viewer.Core.Residency;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// Prims from the core's world state, on the RenderingServer (chosen for terrain by measurement; same reasons
/// here: deterministic freeing, no node per prim). One mesh RID per distinct shape (one surface per prim face, from
/// <see cref="PrimGeometry.Build"/>), one instance RID per prim, per-face legacy colour and alpha as surface override
/// materials (one material per distinct colour). Linked children are placed by their root's transform (SL: child
/// position and rotation are local to the root; scale is not inherited). Objects worn on an avatar (a root attachment
/// and the prims linked below it) hang from the avatar's attachment point: they are placed by <see cref="Attachments"/>
/// and follow the point's joint every frame (<see cref="UpdateAttached"/>); a rigged mesh worn is skinned in the face shader from the
/// wearer's joint matrices (<see cref="SkinTextures"/>) and drawn at the wearer's place; one the layer cannot skin (no full skin read, no
/// skeleton yet, no row left) is counted and not drawn.
/// Without <see cref="Attachments"/> what is worn is counted and skipped. Prims without shape data (older captures)
/// are counted and skipped.
/// Coordinates: SL (x east, y north, z up) -> engine (x, z, -y), the same mapping as the terrain.
/// Mesh objects (SculptType 5) are drawn from their decoded asset (<see cref="MeshSource"/>): one mesh RID per
/// (asset, LOD), the LOD chosen by the official viewer's rule (<see cref="MeshLodRule"/>) from the distance to
/// <see cref="Camera"/> and re-checked by <see cref="UpdateLods"/> when the camera moves; a missing LOD falls back.
/// An object whose asset is still being fetched waits (Pending) and is re-presented when it arrives.
/// Sculpts (SculptType 1-4) are drawn from their map, which arrives through the texture path (the guard):
/// one mesh RID per (map, type and flags, LOD, outcome) from <see cref="SculptMesher"/> (the official sampler),
/// the LOD by the official rule with the sculpt radius bias; face 0 is textured and coloured like any prim face. A
/// missing map shows the official stand-in sphere; a refused map or a zero-area sculpt draws nothing (counted).
/// </summary>
public sealed partial class PrimLayer
{
    private readonly Rid _scenario;
    private readonly WorldState _world;
    private readonly Dictionary<(PrimShape Shape, int Lod), (Rid Mesh, int Surfaces)> _meshes = new();   // by shape and level of detail
    private readonly Dictionary<uint, (Rid Instance, PrimShape Shape, int Lod)> _instances = new();
    private readonly Dictionary<(Guid Asset, int Lod), (Rid Mesh, int Surfaces, long Bytes, int Triangles)> _meshAssets = new();
    private readonly Dictionary<uint, (Guid Asset, Vector3 Origin, float Radius, int Lod)> _meshObjects = new();
    private readonly Dictionary<Guid, HashSet<uint>> _waiting = new();
    private readonly Dictionary<(Guid Map, int Type, int Lod, SculptOutcome Outcome), Rid> _sculptMeshes = new();
    private readonly Dictionary<uint, (Guid Map, Vector3 Origin, float Radius, int Lod, int Triangles)> _sculptObjects = new();
    private readonly Dictionary<uint, (PrimShape Shape, Vector3 Origin, float Radius, int Lod)> _primObjects = new();   // ordinary prims: the level they are built at, as the official viewer builds a volume at the level of detail its distance gives
    private readonly MeshSource? _meshSource;
    // mesh LOD surface data prepared off the main thread; the objects waiting for each (asset, LOD)
    // (where they are, to tell whether they still want it when it is ready), and the ready ones not handed over yet
    private readonly MeshLodPreparer? _preparer;
    private readonly Dictionary<(Guid Asset, int Lod), Dictionary<uint, (Vector3 Origin, float Radius, uint Wearer)>> _lodWaiting = new();   // Wearer: the avatar a worn piece hangs from (0: not worn), so the level is judged again from the same distance it was asked for
    private readonly HashSet<(Guid Asset, int Lod)> _handoverPending = new();
    // when each asset's decoded geometry was last needed to build a LOD; the assets let go
    // this frame (reused); the busy test (made once); the keeper's frame count (its deterministic clock in test renders)
    private readonly DecodedMeshKeeper _keeper = new();
    private readonly List<Guid> _letGo = new();
    private readonly Func<Guid, bool> _meshBusy;
    private long _keeperFrame;
    // textures (one GPU texture per asset for this session's layer), textured materials, objects waiting for a texture.
    private readonly TextureSource? _textureSource;
    private readonly Dictionary<Guid, ImageTexture> _gpuTextures;

    /// <summary>A development aid: the picture the engine holds for a texture, at the level it is at now; null when the texture is not on the GPU.</summary>
    internal Image? TexturePicture(Guid id)
    {
        if (!_gpuTextures.TryGetValue(id, out var texture)) return null;
        var image = texture.GetImage();
        if (image is null) return null;
        if (image.IsCompressed()) image.Decompress();
        image.Convert(Image.Format.Rgba8);
        return image;
    }
    private readonly Dictionary<(Guid Tex, Rgba Tint, float RU, float RV, float OU, float OV, float Rot, bool Planar, bool Fullbright, FaceAlpha Alpha, float Glow), ShaderMaterial> _texMaterials = new();
    private readonly Dictionary<Guid, HashSet<uint>> _waitingTex = new();
    private ImageTexture? _placeholder;
    // texture residency. _gpuTextures holds one GPU texture "slot" per asset, bound by the
    // materials; an eviction swaps the slot's data for a tiny placeholder in the texture's average colour
    // (RenderingServer.TextureReplace: same RID, so no material is rebuilt) and a reload swaps the full image back.
    private readonly Dictionary<Guid, Viewer.Core.Imaging.AlphaClass> _alpha;
    private readonly HashSet<Guid> _alphaMasks;   // the textures whose alpha is a cut-out mask (the hub's)
    private readonly Dictionary<Guid, HashSet<uint>> _waitingGrant = new();
    private readonly Dictionary<uint, int> _texUserFaces = new();
    private readonly HashSet<Guid> _reloading;
    private readonly Queue<Guid> _reupload;
    // the discard level asked of the texture source for each texture with a load or level change in flight
    private readonly Dictionary<Guid, int> _requested;
    // the texture state shared with the other regions' layers (see TextureHub); _gpuTextures, _alpha, _reloading, _reupload and _requested are the hub's
    private readonly int _space;
    private long _frame;
    private readonly System.Diagnostics.Stopwatch _uploadClock = new();
    private readonly Dictionary<Rgba, StandardMaterial3D> _materials = new();
    // PBR materials: the engine material per resolved material (with which of its textures are on the GPU), each
    // material asset with a face's override applied (the override JSON is the same string while it is unchanged), the
    // objects waiting for a material asset, and each object's faces drawn with PBR and of those not opaque
    private readonly Dictionary<(PbrMaterial Material, bool Planar, int Ready, float Glow), ShaderMaterial> _pbrMaterials = new();

    /// <summary>The objects with a glowing face (see <see cref="GlowPass"/>), each as the bounding sphere of its instance (engine
    /// axes): the glow picture is drawn while one of them is in view.</summary>
    private readonly Dictionary<uint, (Vector3 Centre, float Radius)> _glowSpheres = new();
    /// <summary>A test-only switch (render runs): the first object with a textured face whose centre is within <c>Radius</c> of
    /// <c>Point</c> (engine axes) draws its face 0 with the glow value <c>Glow</c>, whatever its own texture entry says.
    /// Null in the viewer.</summary>
    public static (Vector3 Point, float Radius, float Glow)? TestForceGlow;
    /// <summary>The official settings RenderAutoMaskAlphaDeferred and RenderAutoMaskAlphaNonDeferred (both on by default): a face without a material whose texture's alpha is all or nothing is drawn
    /// as a cut-out. The test switch --tr-automask=off turns both off (the negative control).</summary>
    public static AutoMaskSettings AutoMaskAlpha = AutoMaskSettings.Official;
    private uint? _forcedGlowLocal;
    /// <summary>The object the test switch chose (its centre in engine axes), or null while none has.</summary>
    public (uint Local, Vector3 Centre)? ForcedGlowObject => _forcedGlowLocal is { } l && _glowSpheres.TryGetValue(l, out var g) ? (l, g.Centre) : null;
    public int GlowObjects => _glowSpheres.Count;
    private int _extraTargetsSig;

    /// <summary>Adds this layer's glowing objects' bounding spheres (engine axes) to <paramref name="into"/>.</summary>
    internal void AppendGlowTargets(List<Bounds> into)
    {
        foreach (var s in _glowSpheres.Values) into.Add(new Bounds(new System.Numerics.Vector3(s.Centre.X, s.Centre.Y, s.Centre.Z), s.Radius));
    }
    public Dictionary<uint, (Vector3 Centre, float Radius)>.ValueCollection GlowSpheres => _glowSpheres.Values;

    // every placed object's bounding sphere (engine axes), the objects on the glow picture's layer now, and the arrays
    // UpdateGlowPicture works in (kept between frames)
    private readonly Dictionary<uint, (Vector3 Centre, float Radius)> _balls = new();
    private readonly HashSet<uint> _inGlowPicture = new();
    private readonly List<Bounds> _glowTargets = new();
    private readonly GlowChooser _glowChooser = new();
    private Bounds[] _ballList = new Bounds[256];
    private uint[] _ballLocals = new uint[256];
    private bool[] _ballSelected = new bool[256];
    private System.Numerics.Vector3 _glowEye;
    private bool _glowDirty = true;
    private bool _drewAll;
    private int _terrainVersion = -1;

    /// <summary>A measuring switch (render runs): the time of each run of the object choice (<see cref="UpdateGlowPicture"/>, the frames it did work) is kept in <see cref="GlowChoiceMs"/>.</summary>
    public static bool MeasureChoice;
    public readonly List<double> GlowChoiceMs = new();

    /// <summary>A test-only switch (render runs): every object is drawn in the glow picture, as before objects were chosen, so a run can compare the two pictures.</summary>
    public static bool TestGlowDrawAll;

    /// <summary>The layer mask an object's instances have: the view's layer, and the glow picture's while the object is drawn there.</summary>
    private uint GlowLayerOf(uint local) => (_projectorPrims.Contains(local) ? ProjectorLights.LampLayer : 1u) | (_inGlowPicture.Contains(local) ? GlowPass.InGlowPicture : 0u);

    /// <summary>The objects that carry a projector: they are on <see cref="ProjectorLights.LampLayer"/>, so no projector's shadow takes them.</summary>
    private readonly HashSet<uint> _projectorPrims = new();

    /// <summary>
    /// Puts on the glow picture's layer the objects with a glowing face and every object that can be in front of one from
    /// <paramref name="eye"/> (engine axes, <see cref="GlowOccluders"/>), and takes every other object off it, so the picture
    /// draws a few objects instead of the whole scene. Called each frame the pass draws; it does its work only when the eye
    /// has moved or an object has been placed, moved or removed since.
    /// </summary>
    public void UpdateGlowPicture(Vector3 eye, IReadOnlyList<Bounds>? alsoTargets = null)
    {
        var e = new System.Numerics.Vector3(eye.X, eye.Y, eye.Z);
        bool all = TestGlowDrawAll;
        var terrain = ServerTerrainView.For(_scenario);
        int terrainVersion = terrain?.GlowVersion ?? 0;
        int extraSig = 0;
        if (alsoTargets is not null) foreach (var t in alsoTargets) extraSig = HashCode.Combine(extraSig, t);
        if (!_glowDirty && e == _glowEye && all == _drewAll && terrainVersion == _terrainVersion && extraSig == _extraTargetsSig) return;
        _extraTargetsSig = extraSig;
        long choice0 = MeasureChoice ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _terrainVersion = terrainVersion;
        _glowDirty = false;
        _drewAll = all;
        _glowEye = e;
        _glowTargets.Clear();
        foreach (var s in _glowSpheres.Values) _glowTargets.Add(new Bounds(new System.Numerics.Vector3(s.Centre.X, s.Centre.Y, s.Centre.Z), s.Radius));
        if (alsoTargets is not null) _glowTargets.AddRange(alsoTargets);   // other regions' glowing objects: what is in front of them is on the picture too
        if (_ballList.Length < _balls.Count)
        {
            int n = Math.Max(_balls.Count, _ballList.Length * 2);
            _ballList = new Bounds[n];
            _ballLocals = new uint[n];
            _ballSelected = new bool[n];
        }
        int count = 0;
        foreach (var (local, b) in _balls)
        {
            _ballLocals[count] = local;
            _ballList[count++] = new Bounds(new System.Numerics.Vector3(b.Centre.X, b.Centre.Y, b.Centre.Z), b.Radius);
        }
        _glowChooser.Select(e, _glowTargets, new ArraySegment<Bounds>(_ballList, 0, count), _ballSelected);
        terrain?.UpdateGlowPicture(e, _glowTargets, all);
        for (int i = 0; i < count; i++)
        {
            uint local = _ballLocals[i];
            bool want = all || _ballSelected[i] || _glowSpheres.ContainsKey(local);
            if (want == _inGlowPicture.Contains(local)) continue;
            if (want) _inGlowPicture.Add(local); else _inGlowPicture.Remove(local);
            uint mask = GlowLayerOf(local);
            if (_instances.TryGetValue(local, out var inst)) RenderingServer.InstanceSetLayerMask(inst.Instance, mask);
            _blend.SetLayerMask(local, mask);
        }
        SyncMergedMasks();
        if (MeasureChoice) GlowChoiceMs.Add((System.Diagnostics.Stopwatch.GetTimestamp() - choice0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>The number of objects the glow picture draws now (for tests and measurements).</summary>
    public int GlowPictureObjects => _inGlowPicture.Count;
    private readonly Dictionary<(Guid Asset, string? Override), PbrMaterial> _resolved = new();
    private readonly Dictionary<Guid, HashSet<uint>> _waitingMat = new();
    private readonly Dictionary<uint, (int Drawn, int NotOpaque, int Beyond)> _pbrFaces = new();
    private readonly Dictionary<uint, Guid[]> _pbrIds = new();   // the PBR material assets each object's faces name (in use by this layer while the object is here)
    private long _materialSweepAt;
    // the legacy fallback: PBR faces that have shown their base colour (they stay on their PBR material), faces drawing
    // their legacy texture until then, and faces drawn on the placeholder with neither
    private readonly HashSet<(uint Local, int Face)> _pbrSwitched = new();
    private readonly Dictionary<(uint Local, int Face), Guid> _fallbackFaces = new();
    private readonly HashSet<(uint Local, int Face)> _pbrFacesWaiting = new();
    private readonly HashSet<Guid> _fallbackTextures = new();
    private ImageTexture? _black;
    // legacy materials: the engine material per face look (with which of its textures are on the GPU), the objects
    // waiting for a legacy material the region has not sent yet, and the faces drawn with one now
    private readonly Dictionary<(LegacyMaterial Material, FaceTexture Face, Rgba Tint, FaceAlpha Alpha, Guid Tex, int Ready, byte Shiny), ShaderMaterial> _legacyMaterials = new();
    private readonly Dictionary<Guid, HashSet<uint>> _waitingLegacy = new();
    private readonly Dictionary<uint, int> _legacyFaces = new();
    // objects waiting to be presented, nearest the camera first (what an avatar wears goes before all of them)
    private readonly NearestFirstSet _dirty;
    private Vector3 _ringsFor = new(float.NaN, 0, 0);   // where the camera was when the waiting objects were numbered
    private const float RingMetres = 16f, RenumberMetres = 64f;

    /// <summary>The ring an object waits in: -1 for what an avatar wears (few, and what its wearer is waiting to see), else its root's distance
    /// from the camera in 16 m steps.</summary>
    private int Ring(uint local)
    {
        var o = _world.Object(new ObjectId(_world.Epoch, local));
        if (o is null || _world.Attachment(o.Id) is not null)
            return -1;
        for (int depth = 0; depth < 8 && o.ParentLocalId != 0 && _world.Object(new ObjectId(_world.Epoch, o.ParentLocalId)) is { } parent; depth++)
            o = parent;
        var p = o.Position;
        return (int)System.Math.Min(Camera.DistanceTo(new Vector3(p.X, p.Z, -p.Y)) / RingMetres, 1 << 20);
    }

    /// <summary>The camera has gone far from where the waiting objects were numbered: they are numbered again from here.</summary>
    private void RenumberDirty()
    {
        if (_dirty.Count == 0) { _ringsFor = Camera; return; }
        if (!float.IsNaN(_ringsFor.X) && Camera.DistanceTo(_ringsFor) < RenumberMetres) return;
        _ringsFor = Camera;
        _dirty.Rebucket();
    }
    private readonly HashSet<uint> _skipped = new();
    // objects worn on avatars: where each hangs (its wearer, the attachment point and its own frame in the point's frame) and the
    // transform last given to the engine; they follow their joint every frame (UpdateAttached). Rigged meshes worn are not drawn
    // yet: the objects and the wearers they were counted for.
    private sealed class Worn
    {
        public uint Wearer;
        public int Point;
        public System.Numerics.Matrix4x4 InPoint;
        public System.Numerics.Vector3 Scale;
        public Transform3D Last;
        public bool Shown = true;
        public Vector3 BoundsAt;   // where the object was when its textures were last given their bounds (the residency judges them there)
    }
    // how far a worn object may go from where its textures' bounds were given before they are given again (the object is presented again)
    private const float WornBoundsMetres = 4f;
    private readonly Dictionary<uint, Worn> _worn = new();

    /// <summary>The objects that move or spin between the region's updates, and the transform each was last drawn with (its own and
    /// its linked children's).</summary>
    private readonly ObjectInterpolator _moving;
    private readonly Dictionary<uint, Transform3D> _carriedXf = new();
    public ObjectInterpolator Moving => _moving;
    private readonly HashSet<uint> _riggedWorn = new();
    private readonly HashSet<uint> _riggedWearers = new();
    // rigged meshes worn and drawn: each object's wearer, mesh asset and skin, its row in the atlas and, once its wearer has it, the
    // avatar's row; the engine meshes made for them (asset, LOD) with their vertex influences; the skinning materials of faces by look
    private sealed class Rigged
    {
        public uint Wearer;
        public Guid Asset;
        public MeshSkinBlock Block = null!;
        public int Slot = -1;          // the mesh's pair of rows in the atlas
        public bool Registered;        // the wearer has been told (its joint overrides apply, it has a row)
        public bool Shown;
        public int Row = -1;           // the wearer's row of joint matrices, as last given to the instance
        public Transform3D Last;
        public Vector3 BoundsAt;
    }
    private readonly Dictionary<uint, Rigged> _rigged = new();
    private readonly Dictionary<(Guid Asset, int Lod), (Rid Mesh, int Surfaces)> _riggedMeshes = new();
    // a rigged mesh's surfaces and table are prepared off the main thread, as a plain mesh LOD's surfaces are (_preparer): the objects wearing it wait
    // for the level they asked for, and a ready one that no object takes is let go when nothing is dirty
    private readonly MeshLodPreparer? _riggedPreparer;
    private readonly Dictionary<(Guid Asset, int Lod), HashSet<uint>> _riggedWaiting = new();
    private readonly HashSet<(Guid Asset, int Lod)> _riggedHandoverPending = new();
    // a rigged mesh being given to the engine, one surface per step; once all are in (Done) it keeps its prepared table until the first object takes it
    private sealed class RiggedHandover
    {
        public readonly Rid Mesh;
        public readonly PreparedRiggedLod Prepared;
        public int Next;
        public bool Done;
        public RiggedHandover(Rid mesh, PreparedRiggedLod prepared) => (Mesh, Prepared) = (mesh, prepared);
    }
    private readonly Dictionary<(Guid Asset, int Lod), RiggedHandover> _riggedHandovers = new();
    private int _stepRiggedParts;

    /// <summary>The frame's shared time allowance (set by the presenter that owns this layer). When set, a surface or a mesh is handed to the engine only
    /// if its estimated time fits what is left of the frame (<see cref="FrameBudget.TryBeginUnit"/>); one that does not waits for the next frame.</summary>
    public FrameBudget? Budget { get; set; }
    private readonly UnitCostModel _handOverCosts = new();   // what handing a surface to the engine takes for its size
    private bool _deferredByBudget;                         // this step put a unit off to the next frame
    /// <summary>Surfaces whose hand-over was put off to a later frame because they did not fit the frame (measurement).</summary>
    public int HandOversDeferred { get; private set; }

    /// <summary>A surface's size for the cost model: the bytes the engine is given.</summary>
    private static double SurfaceBytes(PreparedSurface s) => s.VertexData.Length + s.AttributeData.Length + s.IndexData.Length;

    /// <summary>True when the frame's budget lets this surface be handed over now (always true without a shared budget); counts the deferral otherwise.</summary>
    private bool MayHandOver(PreparedSurface s)
    {
        if (Budget is null || Budget.TryBeginUnit(_handOverCosts.EstimateMs(SurfaceBytes(s)))) return true;
        _deferredByBudget = true;
        HandOversDeferred++;
        return false;
    }

    /// <summary>Hands one surface to the engine and teaches the cost model what that took.</summary>
    private void HandOverMeasured(Rid mesh, PreparedSurface s)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        HandOver(mesh, s);
        _handOverCosts.Record(SurfaceBytes(s), (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }
    private readonly Dictionary<(Guid Tex, Rgba Tint, float RU, float RV, float OU, float OV, float Rot, bool Planar, bool Fullbright, FaceAlpha Alpha, float Glow), ShaderMaterial> _skinTexMaterials = new();
    private readonly Dictionary<(LegacyMaterial Material, FaceTexture Face, Rgba Tint, FaceAlpha Alpha, Guid Tex, int Ready, byte Shiny), ShaderMaterial> _skinLegacyMaterials = new();
    private readonly Dictionary<Rgba, ShaderMaterial> _skinColourMaterials = new();
    private ImageTexture? _white;
    // the radius an avatar's rigged meshes are given for choosing a level of detail: the official viewer uses the diagonal of the avatar's
    // animated bounding box measured from the avatar's distance (LLVOVolume::calcLOD, "rigged mesh attached to a regular avatar"); an
    // upright avatar's box is about 0.5 by 0.6 by 1.9 metres, so two metres
    // the box a rigged mesh's instance is culled by: it moves within the avatar's reach, around the avatar's pelvis (engine axes)
    private static readonly Aabb RiggedBox = new(new Vector3(-2f, -2f, -2f), new Vector3(4f, 4f, 4f));
    // blended faces in the official order: each mesh's surface boxes (mesh space: centre, size), the materials that
    // blend, the places in the order, and the water height the places were made for; the scratch per presented object
    private readonly Dictionary<Rid, (Vector3 Centre, Vector3 Size)[]> _surfaceBounds = new();
    private readonly HashSet<Material> _blendMaterials = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Material> _maskMaterials = new(ReferenceEqualityComparer.Instance);   // textured materials that draw a cut-out
    private readonly Dictionary<uint, int> _maskedFaces = new();   // per object: faces drawn as an automatic cut-out
    private readonly Dictionary<Guid, HashSet<uint>> _maskDependents = new();   // per texture with alpha: the objects whose faces were drawn by the cut-out rule with it
    /// <summary>Faces drawn as a cut-out because their texture's alpha is all or nothing (the official automatic alpha mask); reported with the layer's counts.</summary>
    public int AutoMaskedFaces => _maskedFaces.Values.Sum();
    private readonly BlendedFaces _blend;
    private float? _blendWater;
    private bool _blendWaterKnown;
    private readonly Material[] _faceMaterials = new Material[MaxSurfaces];
    private readonly bool[] _faceBlended = new bool[MaxSurfaces], _faceShader = new bool[MaxSurfaces];
    private const int MaxSurfaces = 32;

    public PrimLayer(Rid scenario, WorldState world, MeshSource? meshSource = null, TextureSource? textureSource = null, long? textureBudgetBytes = null, TextureHub? hub = null)
    {
        _dirty = new NearestFirstSet(Ring);
        Hub = hub ?? new TextureHub(textureSource, textureBudgetBytes ?? TextureBudget.Minimum);
        _space = Hub.Attach(this);
        _gpuTextures = Hub.Slots;
        _alpha = Hub.Alpha;
        _alphaMasks = Hub.AlphaMasks;
        _reloading = Hub.Reloading;
        _reupload = Hub.Reupload;
        _requested = Hub.Requested;
        _scenario = scenario;
        _lights = new ProjectorLights(scenario);
        _world = world;
        _moving = new ObjectInterpolator(world)
        {
            LandHeight = p => Viewer.Motion.TerrainHeight.Bilinear((ix, iy) => BanWallLayer.GroundFrom(world, ix, iy), p.X, p.Y),
        };
        _blend = new BlendedFaces(scenario) { LayerOf = GlowLayerOf };
        _partSource = PartSource;
        _meshSource = meshSource;
        if (meshSource is not null) { _preparer = new MeshLodPreparer(); _riggedPreparer = new MeshLodPreparer(); }
        _meshBusy = MeshBusy;
        if (meshSource is not null) meshSource.Clock = () => KeeperNow;   // re-decodes soon after their let-go
        _textureSource = textureSource;
        Residency = Hub.Residency;
        // the engine image and the average colour are made on the decode's background task (once: the first layer's settings stand)
        if (textureSource is not null && ReferenceEquals(Hub.Owner, this))
            textureSource.Prepare = d => ImageOnMainThread || MipmapsOnMainThread || d.Mips is null ? null : PreparedUpload.Make(d);
    }

    /// <summary>The texture state this layer shares with the other regions' layers (its own when it was made with none).</summary>
    public TextureHub Hub { get; }

    /// <summary>The texture residency manager (null without a texture source); the hub's, so shared with every layer on the hub.</summary>
    public TextureResidency? Residency { get; }

    /// <summary>The session's PBR material assets. Null: a face with a PBR material keeps its legacy look (as do faces
    /// whose material is still being fetched, or could not be read). PBR faces need the texture source too.</summary>
    public MaterialSource? MaterialAssets { get; init; }

    /// <summary>A face's PBR texture slots beyond the base colour are residency users of their own: the face's user id
    /// plus this times the slot (the base colour slot is the face's own user).</summary>
    private const int SlotUser = 256;

    /// <summary>Faces drawn with their PBR material now; of those, faces whose alpha mode is blend or mask (drawn in that
    /// mode, <see cref="FaceLooks.Pbr"/>); objects waiting for a material asset.</summary>
    public int PbrFaces => _pbrFaces.Values.Sum(p => p.Drawn);
    public int PbrFacesNotOpaque => _pbrFaces.Values.Sum(p => p.NotOpaque);
    /// <summary>PBR material entries on face numbers the object's geometry does not have (nothing to draw them on).</summary>
    public int PbrFacesBeyondGeometry => _pbrFaces.Values.Sum(p => p.Beyond);
    public int PbrMaterials => _pbrMaterials.Count;

    /// <summary>In-view detail maps (PBR faces' normal, metallic-roughness and emissive slots; legacy materials' normal and
    /// specular maps), and of those how many are not at their desired level yet (no image, a coarser level, or a level change in flight).</summary>
    public (int InView, int BelowLevel) DetailMapsInView()
    {
        if (Residency is not { } r) return (0, 0);
        var v = r.DetailMaps.Where(r.IsVisible).ToList();
        return (v.Count, v.Count(id => !r.AtDesired(id)));
    }
    public int MaterialsWaiting => _waitingMat.Values.Sum(w => w.Count);

    /// <summary>Faces drawn with their legacy material now; the engine materials made for them; objects waiting for a
    /// legacy material the region has not sent yet.</summary>
    public int LegacyMaterialFaces => _legacyFaces.Values.Sum();
    public int LegacyMaterialResources => _legacyMaterials.Count;
    public int LegacyMaterialsWaiting => _waitingLegacy.Values.Sum(w => w.Count);

    /// <summary>A PBR face whose base colour is not on screen yet shows its legacy texture when that texture is on the GPU
    /// already or another user wants it anyway (never fetched for the fallback alone), and switches to its PBR material
    /// once, when the base colour is on screen. Off: the face is drawn on the placeholder until then (the control).</summary>
    public bool LegacyFallback { get; init; } = true;

    /// <summary>PBR faces drawing their legacy texture now; PBR faces drawn on the placeholder now (neither their base
    /// colour nor a legacy texture to show); faces that went from their legacy texture to their PBR material; the textures
    /// ever drawn as a fallback.</summary>
    public int FallbackFaces => _fallbackFaces.Count;
    public int PbrFacesWaiting => _pbrFacesWaiting.Count;
    public int FallbackSwitches { get; private set; }
    public IReadOnlyCollection<Guid> FallbackTextures => _fallbackTextures;

    /// <summary>The texture a face's residency user shows instead of its own while it has none (a PBR face's legacy
    /// texture), or the null id (measurement).</summary>
    public Guid FallbackOf(TextureUser user) =>
        user.Kind == TextureUserKind.Face && user.Space == _space && user.Face < SlotUser && _fallbackFaces.TryGetValue((user.Id, user.Face), out var g) ? g : Guid.Empty;

    /// <summary>The camera the texture priorities are measured from (SL axes); null: a wide view at <see cref="Camera"/>.</summary>
    public ResidencyCamera? ResidencyView { get; set; }

    /// <summary>A Godot camera as the manager's camera (engine (x, y, z) -> SL (x, -z, y)). An orthographic
    /// camera becomes the perspective camera that sees it the same way: pulled far back along its view (1000 view heights),
    /// with the field of view that spans its view height there, so on-screen sizes (and so discard levels) match the
    /// orthographic view to within 0.1 % across its depth range.</summary>
    public static ResidencyCamera ViewOf(Camera3D cam, int width, int height)
    {
        static System.Numerics.Vector3 Sl(Vector3 e) => new(e.X, -e.Z, e.Y);
        var b = cam.GlobalTransform.Basis;
        var forward = System.Numerics.Vector3.Normalize(Sl(-b.Z));
        var up = System.Numerics.Vector3.Normalize(Sl(b.Y));
        if (cam.Projection == Camera3D.ProjectionType.Perspective)
            return new ResidencyCamera(Sl(cam.GlobalPosition), forward, up, Mathf.DegToRad(cam.Fov), (float)width / height, cam.Near, cam.Far, height);
        float back = 1000f * cam.Size;   // Size: the orthographic view's height in metres (Godot's default keep-height aspect)
        float fov = 2f * MathF.Atan(cam.Size / (2f * back));
        return new ResidencyCamera(Sl(cam.GlobalPosition) - forward * back, forward, up, fov, (float)width / height, back + cam.Near, back + cam.Far, height);
    }

    /// <summary>GPU upload / swap time spent since the last <see cref="UpdateResidency"/> (ms).</summary>
    public double UploadMsThisFrame { get; private set; }

    /// <summary>Every texture upload since the last <see cref="UpdateResidency"/>: its size (pixels at the level
    /// uploaded) and its main-thread time (ms), for the per-size upload measurement.</summary>
    public List<UploadSample> UploadSamples { get; } = new();

    /// <summary>One upload unit. First: the texture's first upload (its slot created) rather than a
    /// level change or reload; Prepared: its engine image was made off the main thread with the decode (else here);
    /// ImageMs: the part of <see cref="Ms"/> spent getting that image; Gcs: .NET collections (any generation) during it;
    /// GcPauseMs: the time the runtime had managed threads paused for collections during it (GC.GetTotalPauseDuration).
    /// Mesh: the unit made a mesh LOD's GPU mesh (Width: its vertices, Height: its triangles), so
    /// the upload-unit figures see mesh builds beside texture uploads. (measurement): Kind: texture, mesh (a
    /// mesh LOD), prim (a prim shape's first build; Width: its surfaces) or sculpt (a sculpt mesh's first build, its
    /// sampling included; Width: 1). Terrain (a terrain chunk's build; Width: its vertices).</summary>
    /// <remarks><c>AllocBytes</c>, the managed bytes the unit allocated on the main
    /// thread (<see cref="Viewer.Core.Diagnostics.AllocationMeter"/>; counted work nested in it taken out).</remarks>
    public readonly record struct UploadSample(int Width, int Height, double Ms, bool First, bool Prepared, double ImageMs, int Gcs, double GcPauseMs, bool Mesh = false, string Kind = "texture", long AllocBytes = 0);

    /// <summary>1 (measurement only): one mesh LOD build on the main thread, split into
    /// PrepareMs (the surface data encoded on the main thread: C# work), EngineMs (the RenderingServer calls: MeshCreate and
    /// MeshAddSurface) and the rest of Ms (the Godot dictionary and byte arrays the calls take, the footprint). Prepared: the
    /// surface data was made off the main thread (then PrepareMs is 0).</summary>
    public readonly record struct MeshBuild(int Vertices, int Triangles, int Surfaces, double Ms, double PrepareMs, double EngineMs, bool Prepared);

    /// <summary>Surfaces this process has added to the engine through the prim layer's
    /// surface functions (prims, sculpts, mesh LODs), for --tr-surfacestress.</summary>
    internal static long SurfacesAdded;

    /// <summary>Every mesh LOD build since the layer was made (the first 4096).</summary>
    public List<MeshBuild> MeshBuilds { get; } = new();

    /// <summary>Per mesh asset, milliseconds from its decode arriving (<see cref="MeshSource.ArrivedAt"/>)
    /// to its first LOD's GPU mesh (the first time an object can draw it).</summary>
    public List<double> MeshDecodeToDrawMs { get; } = new();
    private readonly HashSet<Guid> _meshDrawn = new();

    /// <summary>Measurement control only (render harness, --tr-legacy-upload): build the mipmaps on the main thread
    /// at upload time, as did, so the upload cost can be compared before and after on the same build.</summary>
    public bool MipmapsOnMainThread { get; set; }

    /// <summary>Measurement control only (render harness, --tr-part1-upload): make the engine image and the
    /// average colour on the main thread at upload time, as did.</summary>
    public bool ImageOnMainThread { get; set; }

    /// <summary>Measurement control only (render harness, --tr-mesh-main-thread): build every mesh LOD on the
    /// main thread when it is first drawn, as (the negative control of the checks).</summary>
    public bool MeshPrepareOnMainThread { get; set; }
    /// <summary>The residency manager's clock. Null (the app, and measurements): the engine's monotonic
    /// clock, so residence and thrash are measured in the user's time. Set (deterministic test renders): the clock advances
    /// 1/fps per <see cref="UpdateResidency"/>, so the same run makes the same decisions (at 60, frame-counted rule).</summary>
    public double? SimulatedFps { get; set; }

    private TimeSpan Now => SimulatedFps is { } fps
        ? TimeSpan.FromTicks((long)Math.Floor(_frame * (double)TimeSpan.TicksPerSecond / fps))
        : TimeSpan.FromTicks((long)Time.GetTicksUsec() * 10);

    /// <summary>The decoded-geometry keeper's clock: with <see cref="SimulatedFps"/>, 1/fps per
    /// <see cref="UpdateResidency"/> (with or without textures); otherwise the engine's monotonic clock.</summary>
    private TimeSpan KeeperNow => SimulatedFps is { } fps
        ? TimeSpan.FromTicks((long)Math.Floor(_keeperFrame * (double)TimeSpan.TicksPerSecond / fps))
        : TimeSpan.FromTicks((long)Time.GetTicksUsec() * 10);

    /// <summary>A mesh's decoded geometry is kept only while it is needed to build
    /// a LOD, and let go 10 s after the last LOD request, preparation start or build needed it (<see cref="DecodedMeshKeeper"/>);
    /// a LOD not on the GPU later is built from the geometry decoded again (the stores first), the object drawing the LOD
    /// it has meanwhile. No drawn LOD is released. Test only: false keeps every decoded geometry for the session,
    /// as before (the harness's negative control).</summary>
    public bool DecodedBound { get; set; } = true;

    /// <summary>Assets whose decoded geometry the keeper holds now (needed within 10 s, or busy).</summary>
    public int DecodedKept => _keeper.Tracked;

    /// <summary>The app's budget, following the engine's non-texture video memory; applied on every
    /// <see cref="UpdateResidency"/>. Null (test renders): the fixed budget given at construction.</summary>
    public LiveTextureBudget? LiveBudget { get; set; }

    /// <summary>The engine's current non-texture video memory, given the residency's resident bytes.</summary>
    public Func<long, long> NonTextureMemory { get; set; } = VideoMemory.NonTexture;

    /// <summary>The number of actions the last <see cref="UpdateResidency"/> issued.</summary>
    public int LastResidencyActions { get; private set; }
    public int TexturesAwaitingGrant => _waitingGrant.Values.Sum(w => w.Count);

    /// <summary>
    ///Main thread, once per frame: re-scores the users (bounded), then applies the manager's decisions. An eviction
    /// swaps the slot to its placeholder and drops the decoded copy; a load starts the fetch (content store first, then the
    /// network) and the upload happens as one unit of <see cref="Step"/> work when it arrives (the frame budget).
    /// </summary>
    private TimeSpan? _lastResidencyAt;
    private bool _residencyTimed;

    public int UpdateResidency()
    {
        UploadMsThisFrame = 0;
        UploadSamples.Clear();
        _keeperFrame++;
        LetGoDecodedMeshes();   // before this frame's work, so a request in it keeps what it needs
        if (Residency is null) return 0;
        _frame++;
        if (!ReferenceEquals(Hub.Owner, this)) return LastResidencyActions = 0;   // the manager is updated once a frame, by the hub's owner
        var sl = new System.Numerics.Vector3(Camera.X, -Camera.Z, Camera.Y);
        var view = ResidencyView ?? new ResidencyCamera(sl, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ, 3.1f, 16f / 9, 0.1f, 4096f, 360);
        var now = Now;
        using (FrameProbe.Begin(FrameProbe.PrimResBudget))
            if (LiveBudget is { Live: true } live) Residency.SetBudget(live.Update(NonTextureMemory(Residency.ResidentBytes), now));
        IReadOnlyList<ResidencyAction> actions;
        if (SimulatedFps is null)
        {
            // The app limits the manager's work in a frame to 0.05 x the last frame's length, between 2 and 5 ms, as the official viewer does for
            // its texture work; what does not fit is done on later frames. Deterministic test renders keep the unbounded, frame-counted work.
            double interval = _lastResidencyAt is { } last ? (now - last).TotalSeconds : 0.0167;
            _lastResidencyAt = now;
            Residency.FrameBudget = TimeSpan.FromSeconds(System.Math.Clamp(0.05 * interval, 0.002, 0.005));
            if (!_residencyTimed)
            {
                _residencyTimed = true;
                Residency.SetUsersPerFrame(int.MaxValue);
            }
        }
        using (FrameProbe.Begin(FrameProbe.PrimResUpdate)) actions = Residency.Update(view, now);
        using (FrameProbe.Begin(FrameProbe.PrimResActions))
            foreach (var a in actions)
            {
                if (a.Kind == ResidencyActionKind.Evict) EvictTexture(a.Texture);
                else if (a.Kind == ResidencyActionKind.Level) ChangeLevel(a.Texture, a.Level);
                else LoadTexture(a.Texture, a.Level);
            }
        return LastResidencyActions = actions.Count;
    }

    private void EvictTexture(Guid id)
    {
        if (!_gpuTextures.TryGetValue(id, out var slot)) return;
        _uploadClock.Restart();
        uint c = PlaceholderColour.ForPlaceholder(Residency!.AverageOf(id) ?? PlaceholderColour.Of(128, 128, 128, 255));
        var img = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        img.Fill(Color.Color8((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c));
        var small = RenderingServer.Texture2DCreate(img);
        RenderingServer.TextureReplace(slot.GetRid(), small);   // the slot keeps its RID; the full image is freed
        img.Dispose();
        _textureSource!.Release(id);
        _reloading.Remove(id);
        _requested.Remove(id);
        UploadMsThisFrame += _uploadClock.Elapsed.TotalMilliseconds;
    }

    private void LoadTexture(Guid id, int level)
    {
        LetGoOfSuperseded(id, level);
        _requested[id] = level;
        if (_gpuTextures.ContainsKey(id))
        {
            // a reload: fetched again (content store first), swapped back in by Step when it arrives
            _textureSource!.Release(id);
            if (_textureSource.TryGetLevel(id, level, out _) == MeshAvailability.Ready) _reupload.Enqueue(id);
            else _reloading.Add(id);
            return;
        }
        // a first load (at a coarse level first): start the fetch; the faces waiting for the grant are re-presented when it arrives
        var availability = _textureSource!.TryGetLevel(id, level, out _);
        Hub.Granted(id, availability);   // every region's faces that wait for this texture
    }

    /// <summary>A first load was granted: the faces of this layer that waited for the grant now wait for the data (or are re-presented, it being there).</summary>
    internal void GrantArrived(Guid id, MeshAvailability availability)
    {
        if (!_waitingGrant.Remove(id, out var objs)) return;
        if (availability == MeshAvailability.Pending)
        {
            if (!_waitingTex.TryGetValue(id, out var set)) _waitingTex[id] = set = new HashSet<uint>();
            set.UnionWith(objs);
        }
        else
            foreach (var o in objs) _dirty.Add(o);
    }

    /// <summary>A texture's level changed whether its alpha is a cut-out mask: this layer's faces drawn by the cut-out rule with it are presented again.</summary>
    internal void MaskChanged(Guid id)
    {
        if (_maskDependents.TryGetValue(id, out var objs))
            foreach (var o in objs) _dirty.Add(o);
    }

    /// <summary>A texture's data arrived: this layer's faces waiting for it are re-presented.</summary>
    internal void TextureArrived(Guid id)
    {
        if (_waitingTex.Remove(id, out var tobjs))
            foreach (var w in tobjs) _dirty.Add(w);
    }

    /// <summary>A level asked for earlier and not uploaded yet is not the one wanted now: its decoded copy (or the decode still running) is let go, so none stays held.</summary>
    private void LetGoOfSuperseded(Guid id, int wanted)
    {
        if (_requested.TryGetValue(id, out int earlier) && earlier != wanted) _textureSource!.ReleaseLevel(id, earlier);
    }

    /// <summary>A resident texture's level changes (a step down under pressure, or a refinement): decoded off the
    /// main thread, swapped into the same slot by <see cref="Step"/> when it arrives.</summary>
    private void ChangeLevel(Guid id, int level)
    {
        if (!_gpuTextures.ContainsKey(id)) return;
        LetGoOfSuperseded(id, level);
        _requested[id] = level;
        var availability = _textureSource!.TryGetLevel(id, level, out _);
        if (availability == MeshAvailability.Ready) _reupload.Enqueue(id);
        else if (availability == MeshAvailability.Pending) _reloading.Add(id);
        else { _requested.Remove(id); Residency!.LoadFailed(id); }
    }

    /// <summary>A reload or a level change arrives: the new image replaces the slot's (same RID, no material rebuilt).</summary>
    private void Reupload(Guid id)
    {
        if (!_gpuTextures.TryGetValue(id, out var slot) || !_requested.TryGetValue(id, out int level)) return;
        if (Residency!.PendingLevelOf(id) != level) return;   // superseded (another level asked since) or evicted
        var availability = _textureSource!.TryGetLevel(id, level, out var d);
        if (availability == MeshAvailability.Pending) { _reloading.Add(id); return; }
        _requested.Remove(id);
        if (availability != MeshAvailability.Ready) { Residency.LoadFailed(id); return; }
        _uploadClock.Restart();
        int gc0 = GC.CollectionCount(0);
        var pause0 = GC.GetTotalPauseDuration();
        var alloc = AllocationMeter.Shared.Begin(AllocKind.Texture);   // (measurement only)
        var (img, average, owned) = ImageFor(id, level, d!);
        double imageMs = _uploadClock.Elapsed.TotalMilliseconds;
        var full = RenderingServer.Texture2DCreate(img);
        RenderingServer.TextureReplace(slot.GetRid(), full);
        if (owned) img.Dispose();
        Residency.Uploaded(id, d!.Level, d.LevelCount, d.FullWidth, d.FullHeight, TextureFootprint.Bytes(d.Width, d.Height, true), average, Now);
        // the official viewer analyses the alpha of each level it uploads: a coarse level of a cut-out texture is not a mask, the full one may be
        if (_alpha.GetValueOrDefault(id) != Viewer.Core.Imaging.AlphaClass.Opaque && _alphaMasks.Contains(id) != d.IsAlphaMask)
        {
            if (d.IsAlphaMask) _alphaMasks.Add(id); else _alphaMasks.Remove(id);
            Hub.AlphaMaskChanged(id);
        }
        _textureSource.ReleaseLevel(id, level);   // the CPU keeps no copy of what is on the GPU
        double ms = _uploadClock.Elapsed.TotalMilliseconds;
        UploadMsThisFrame += ms;
        UploadSamples.Add(new UploadSample(d.Width, d.Height, ms, false, !owned, imageMs, GC.CollectionCount(0) - gc0, (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds, AllocBytes: alloc.End()));
    }

    /// <summary>The engine image and average colour for an upload: the ones made off the main thread
    /// with the decode when there are (owned by the texture source, disposed when the level is released), else made here.</summary>
    private (Image Image, uint Average, bool Owned) ImageFor(Guid id, int requestedLevel, Viewer.Core.Imaging.DecodedTexture d)
    {
        if (_textureSource!.TryGetLevel(id, requestedLevel, out _, out var prepared) == MeshAvailability.Ready && prepared is PreparedUpload p)
            return (p.Image, p.Average, false);
        return (GpuImage(d), PlaceholderColour.Average(d.Rgba), true);
    }

    /// <summary>The GPU image of a decoded level. Its mipmap chain was built with the decode, off the main
    /// thread (the same box filter as the engine's); the main thread only wraps it and uploads.</summary>
    private Image GpuImage(Viewer.Core.Imaging.DecodedTexture d)
    {
        if (d.Mips is { } mips && !MipmapsOnMainThread)
            return Image.CreateFromData(d.Width, d.Height, true, Image.Format.Rgba8, mips);
        var img = Image.CreateFromData(d.Width, d.Height, false, Image.Format.Rgba8, d.Rgba);
        img.GenerateMipmaps();
        return img;
    }

    public int GpuTextures => _gpuTextures.Count;
    /// <summary>Whether this layer holds a GPU texture for the asset <paramref name="id"/> (a face draws with it).</summary>
    public bool HasGpuTexture(Guid id) => _gpuTextures.ContainsKey(id);

    /// <summary>The engine objects the layer made and holds, by kind (the lifecycle check names any that
    /// outlive a region change).</summary>
    internal IEnumerable<(string Kind, GodotObject Object)> EngineObjects()
    {
        foreach (var t in _gpuTextures.Values) yield return ("texture", t);
        if (_placeholder is not null) yield return ("placeholder texture", _placeholder);
        if (_black is not null) yield return ("black texture", _black);
        foreach (var m in _texMaterials.Values) yield return ("textured material", m);
        foreach (var m in _pbrMaterials.Values) yield return ("PBR material", m);
        foreach (var m in _legacyMaterials.Values) yield return ("legacy material", m);
        foreach (var m in _skinTexMaterials.Values) yield return ("skinned textured material", m);
        foreach (var m in _skinLegacyMaterials.Values) yield return ("skinned legacy material", m);
        foreach (var m in _skinColourMaterials.Values) yield return ("skinned colour material", m);
        if (_white is not null) yield return ("white texture", _white);
        foreach (var m in _materials.Values) yield return ("colour material", m);
    }

    /// <summary>Lifecycle negative control only (--tr-alter=leaktexture): <see cref="Clear"/> keeps one texture
    /// referenced instead of freeing it, the way a missed release would leak it. The lifecycle check must fail.</summary>
    public bool LeakTextureForNegativeControl { get; init; }
    private static readonly List<ImageTexture> LeakedForNegativeControl = new();

    public int TexturedMaterials => _texMaterials.Count;
    public int TexturesWaiting => _waitingTex.Values.Sum(w => w.Count);

    /// <summary>The camera's position in engine space (the LOD rule measures from it).</summary>
    public Vector3 Camera { get; set; } = new(0, 1000, 0);

    /// <summary>The mesh LOD factor (the viewer's setting; the official viewer's RenderVolumeLODFactor), clamped as it clamps it.</summary>
    public float LodFactor { get => _lodFactor; set => _lodFactor = MeshLodRule.Clamp(value); }
    private float _lodFactor = MeshLodRule.DefaultLodFactor;
    /// <summary>The camera's vertical field of view in radians: a narrower one (zoomed in) keeps more detail, as the
    /// official viewer does. The default when only a position is given.</summary>
    public float LodFovY { get; set; } = MeshLodRule.DefaultFovY;

    /// <summary>The camera the LOD rule measures from: its position and, for a perspective camera, its field of view
    /// (an orthographic camera keeps the default).</summary>
    public void SetCamera(Camera3D cam)
    {
        ViewCamera = cam;
        Camera = cam.GlobalPosition;
        LodFovY = cam.Projection == Camera3D.ProjectionType.Perspective ? Mathf.DegToRad(cam.Fov) : MeshLodRule.DefaultFovY;
    }
    /// <summary>The camera blended faces are ordered for (<see cref="UpdateBlendOrder"/>); the environment whose water
    /// takes its place among them.</summary>
    public Camera3D? ViewCamera { get; set; }
    public EnvironmentLayer? Environment { get; set; }

    /// <summary>
    /// Blended faces in the official order for the camera's view as it is now (<see cref="BlendedFaces"/>), and the
    /// water between the far side and the camera's side. Call once a frame after the camera has moved, before drawing.
    /// When the region's water height changes, the objects with blended faces are presented again (which side of the
    /// water each face is drawn on follows it).
    /// </summary>
    public void UpdateBlendOrder()
    {
        UpdateMerged();   // (merged cells of still objects: finished and started here, once a frame)
        _lights.Cull(Camera, ViewCamera is { } vc && vc.IsInsideTree() ? -vc.GlobalTransform.Basis.Z : new Vector3(0f, 0f, -1f));
        var water = _world.Region?.WaterHeight;
        if (!_blendWaterKnown || water != _blendWater)
        {
            if (_blendWaterKnown) foreach (var l in _blend.Locals) _dirty.Add(l);
            _blendWater = water;
            _blendWaterKnown = true;
        }
        if (ViewCamera is not { } cam || !cam.IsInsideTree()) return;
        var eye = cam.GlobalPosition;
        _blend.Order(eye, -cam.GlobalBasis.Z.Normalized(), water);
        Environment?.OrderWater(eye);
        if (BlendOrderMs.Count < 1 << 16) BlendOrderMs.Add(_blend.LastMs);
    }

    /// <summary>Objects with a blended face; blended faces in the order (a face crossing the water counted twice); the
    /// face instances drawing an object's further blended faces; sorting offsets handed to the engine; each ordering's
    /// time (ms, measurement).</summary>
    public int BlendObjects => _blend.Objects;
    public int BlendPlaces => _blend.Units;
    public int BlendFaceInstances => _blend.FaceInstances;
    public long BlendOffsetsSet => _blend.OffsetsSet;
    public List<double> BlendOrderMs { get; } = new();
    /// <summary>The part meshes blended faces are drawn with (held, made, their surface bytes), and the objects drawn
    /// with their whole mesh and skipped surfaces instead (<see cref="BlendedFaces"/>).</summary>
    public int BlendPartMeshes => _blend.PartMeshes;
    public int BlendPartsMade => _blend.PartsMade;
    public long BlendPartBytes => _blend.PartBytes;
    public long BlendWholeMeshFallbacks => _blend.WholeMeshFallbacks;

    // the presented object's surface data for its part meshes, when at hand: the surfaces prepared for its mesh in this
    // presentation, else its mesh LOD's decoded faces, else its prim shape (made again; a prim's geometry is
    // deterministic). A mesh whose geometry was let go has none (BlendedFaces then draws the whole mesh).
    private PreparedMeshLod? _srcPrepared;
    private IReadOnlyList<MeshFace>? _srcFaces;
    private PrimShape? _srcShape;
    private int _srcLod = 3;
    private readonly Func<PreparedMeshLod?> _partSource;
    private PreparedMeshLod? PartSource() =>
        _srcPrepared ?? (_srcFaces is { } f ? MeshSurfaceData.Prepare(f) : _srcShape is { } s ? PrimSurfaces(s, _srcLod) : null);
    /// <summary>Each object with a blended face: its places in the order and where its first blended face is (engine axes).</summary>
    public IEnumerable<(uint Local, int Places, Vector3 Centre)> BlendReport() => _blend.Report();

    public int MeshObjects => _meshObjects.Count;
    public int MeshAssetResources => _meshAssets.Count;
    /// <summary>The mesh LODs on the GPU (one mesh per asset and LOD) and their bytes
    /// (<see cref="MeshFootprint"/>), now and at their peak since the layer was made.</summary>
    public int MeshLodsHeld => _meshAssets.Count;
    /// <summary>The distinct (asset, LOD) pairs the mesh objects draw now.</summary>
    public int DistinctDrawnMeshLods => _meshObjects.Values.Select(m => (m.Asset, m.Lod)).Distinct().Count();
    public long MeshBytesHeld { get; private set; }
    public int PeakMeshLodsHeld { get; private set; }
    public long PeakMeshBytesHeld { get; private set; }
    /// <summary>Mesh LODs released because no object drew them (not at a region change), made again after that, and
    /// made again within 2 s of their release (thrash).</summary>
    public long MeshLodsReleased => 0;   // no LOD is released before a region change yet (is not built)
    public long MeshLodsRebuilt => 0;
    public long MeshLodThrash => 0;
    /// <summary>Objects waiting for their mesh: its fetch and decode, or its LOD's surface data (an object switching
    /// LOD keeps drawing the one it has meanwhile).</summary>
    public int MeshesWaiting => _waiting.Values.Sum(w => w.Count) + _lodWaiting.Values.Sum(w => w.Count);

    /// <summary>The mesh LOD preparer (null without a mesh source): in flight, held, dropped, running.</summary>
    public MeshLodPreparer? MeshPreparer => _preparer;

    /// <summary>Objects drawing a previous LOD while the one they want is prepared (never nothing).</summary>
    public int MeshObjectsOnPreviousLod => _meshObjects.Count(kv => _lodWaiting.Any(w => w.Key.Asset == kv.Value.Asset && w.Key.Lod != kv.Value.Lod && w.Value.ContainsKey(kv.Key)));
    public int SkippedMeshUnavailable { get; private set; }
    public int SculptObjects => _sculptObjects.Count;
    /// <summary>The LOD each sculpt is drawn at, by local id (for the sidecar and tests).</summary>
    public int[] SculptLods => _sculptObjects.OrderBy(kv => kv.Key).Select(kv => kv.Value.Lod).ToArray();
    public int SculptResources => _sculptMeshes.Count;
    public int SculptPlaceholders { get; private set; }
    public int SkippedSculptEmpty { get; private set; }

    /// <summary>The mesh objects at each LOD (index = LOD), the triangles the mesh and sculpt objects draw now (prims are
    /// left out: their detail does not follow the distance), and the mesh LODs' bytes on the GPU by LOD (measurement).</summary>
    public int[] MeshObjectsByLod => Enumerable.Range(0, MeshGeometry.LodCount).Select(l => _meshObjects.Values.Count(m => m.Lod == l)).ToArray();
    public long MeshTrianglesDrawn => _meshObjects.Values.Sum(m => (long)(_meshAssets.TryGetValue((m.Asset, m.Lod), out var a) ? a.Triangles : 0));
    public long SculptTrianglesDrawn => _sculptObjects.Values.Sum(s => (long)s.Triangles);
    public long[] MeshBytesByLod => Enumerable.Range(0, MeshGeometry.LodCount).Select(l => _meshAssets.Where(kv => kv.Key.Lod == l).Sum(kv => kv.Value.Bytes)).ToArray();

    /// <summary>The LOD each mesh object is drawn at (for the sidecar and tests).</summary>
    /// <summary>What a drawn mesh object is drawn at and why (a development aid): its asset, the level drawn, the levels the asset has, the level wanted now, and the distance and radius that level is chosen from.</summary>
    public (Guid Asset, int Lod, byte Available, int Wanted, float Distance, float Radius)? MeshReport(uint local)
    {
        if (!_meshObjects.TryGetValue(local, out var m)) return null;
        byte mask = 0;
        _meshSource?.TryGetLods(m.Asset, out mask);
        return (m.Asset, m.Lod, mask, WantedLod(m.Asset, m.Origin, m.Radius, local) ?? -1, MeshLodDistance(local, m.Origin), m.Radius);
    }

    public IReadOnlyDictionary<uint, int> MeshLods => _meshObjects.ToDictionary(kv => kv.Key, kv => kv.Value.Lod);

    /// <summary>Main thread, each frame: marks mesh objects whose LOD would change from the current camera position.</summary>
    public void UpdateLods()
    {
        RenumberDirty();
        UpdateDrawDistance();
        foreach (var (local, m) in _meshObjects)
            if ((_rigged.TryGetValue(local, out var wornRigged) ? WantedRiggedLod(m.Asset, wornRigged.Wearer) : WantedLod(m.Asset, m.Origin, m.Radius, local)) is int lod && lod != m.Lod && !WaitingFor(local, m.Asset, lod))
                _dirty.Add(local);
        // ordinary prims: an eighth of them each frame (every prim is looked at every eight frames, the cost spread)
        int slice = _primLodSlice++ % PrimLodSlices;
        foreach (var (local, pr) in _primObjects)
            if ((int)(local % PrimLodSlices) == slice && PrimLod(pr.Origin, pr.Radius) != pr.Lod)
                _dirty.Add(local);
        foreach (var (local, s) in _sculptObjects)
            if (SculptLod(s.Origin, s.Radius) != s.Lod)
                _dirty.Add(local);
    }

    /// <summary>The object already waits for that LOD's surface data (it is re-presented when that is ready).</summary>
    private bool WaitingFor(uint local, Guid asset, int lod) => _lodWaiting.TryGetValue((asset, lod), out var w) && w.ContainsKey(local);

    /// <summary>From the LODs the asset has (known once it was decoded, also after its geometry was let go); starts
    /// no fetch or decode.</summary>
    /// <param name="wearer">The avatar the piece hangs from when the caller knows it (a waiter records it: a first build is removed while it
    /// waits, which forgets the piece's worn record); 0: look it up.</param>
    private int? WantedLod(Guid asset, Vector3 origin, float radius, uint local, uint wearer = 0)
    {
        if (_meshSource is null || !_meshSource.TryGetLods(asset, out var lods))
            return null;
        return MeshGeometry.Resolve(lods, DetailFor(asset, wearer != 0 ? WearerLodDistance(wearer, origin) : MeshLodDistance(local, origin), radius));
    }

    /// <summary>The distance a mesh object's level of detail is chosen with: a worn piece takes its wearer's box (as a rigged one does,
    /// <see cref="RiggedAssessment.WornDistance"/>), anything else its own place.</summary>
    private float MeshLodDistance(uint local, Vector3 origin) =>
        _worn.TryGetValue(local, out var w) ? WearerLodDistance(w.Wearer, origin) : Camera.DistanceTo(origin);

    /// <summary>The level a rigged mesh wants now: from its wearer's distance and box as they are this frame (the object's own place stays where it was presented).</summary>
    private int? WantedRiggedLod(Guid asset, uint wearer)
    {
        if (_meshSource is null || !_meshSource.TryGetLods(asset, out var lods)) return null;
        return MeshGeometry.Resolve(lods, MeshLodRule.Detail(WearerLodDistance(wearer, Vector3.Zero), WearerLodRadius(wearer), LodFactor, LodFovY));
    }

    /// <summary>An asset whose decoded geometry must be kept: an object waits for it (its decode) or for one of its
    /// LODs, or a preparation of one of its LODs is asked for or ready (a failed one is built here from the geometry).</summary>
    private bool MeshBusy(Guid asset)
    {
        if (_waiting.ContainsKey(asset)) return true;
        for (int lod = 0; lod < MeshGeometry.LodCount; lod++)
            if (_lodWaiting.ContainsKey((asset, lod)) || (_preparer?.IsPending((asset, lod)) ?? false)
                || _riggedWaiting.ContainsKey((asset, lod)) || (_riggedPreparer?.IsPending((asset, lod)) ?? false)) return true;
        return false;
    }

    /// <summary>Lets go the decoded geometry of every asset not needed for 10 s and
    /// not busy. Its LODs on the GPU stay; the source keeps which LODs it has.</summary>
    private void LetGoDecodedMeshes()
    {
        if (_meshSource is null || !DecodedBound || _keeper.Tracked == 0) return;
        _letGo.Clear();
        var now = KeeperNow;
        if (_keeper.Collect(now, _meshBusy, _letGo) == 0) return;
        foreach (var asset in _letGo) _meshSource.Release(asset, now);
    }

    public int Instances => _instances.Count;

    /// <summary>How many worn rigged meshes are shown on their wearer now.</summary>
    public int RiggedShown => _rigged.Values.Count(r => r.Shown);

    /// <summary>Whether the object with this local id has an instance in the scene now.</summary>
    public bool Draws(uint local) => _instances.ContainsKey(local);
    public int Meshes => _meshes.Count;

    /// <summary>The level each ordinary prim was built at (tests).</summary>
    public IReadOnlyDictionary<uint, int> PrimObjectLods => _primObjects.ToDictionary(kv => kv.Key, kv => kv.Value.Lod);
    public int Materials => _materials.Count;
    public int LiveResources => _instances.Count + _blend.FaceInstances + _blend.PartMeshes + _meshes.Count + _meshAssets.Count + _sculptMeshes.Count + _gpuTextures.Count + _texMaterials.Count + _skinTexMaterials.Count + _skinLegacyMaterials.Count + _skinColourMaterials.Count + _riggedMeshes.Count + (_white is null ? 0 : 1) + _pbrMaterials.Count + _legacyMaterials.Count;

    /// <summary>The detail a mesh is drawn at: a tree by the official tree rule (its trunk detail), any other mesh by the object detail rule.</summary>
    private int DetailFor(Guid asset, float distance, float radius)
    {
        if (TreeMesh.TryInfo(asset, out var species, out var length))
        {
            float side = length / MathF.Sqrt(3f);
            return TreeLod.MeshDetail(TreeLod.TrunkLod(species, new System.Numerics.Vector3(side, side, side), distance, LodFactor));
        }
        return MeshLodRule.Detail(distance, radius, LodFactor, LodFovY);
    }

    private int SculptLod(Vector3 origin, float radius) => System.Math.Clamp(MeshLodRule.Detail(Camera.DistanceTo(origin), radius, LodFactor, LodFovY), 0, 3);

    /// <summary>The level an ordinary prim is built at, from its distance and radius (calcLOD, llvovolume.cpp 1455-1560).</summary>
    private int PrimLod(Vector3 origin, float radius) => System.Math.Clamp(MeshLodRule.Detail(Camera.DistanceTo(origin), radius, LodFactor, LodFovY), 0, 3);

    private int _primLodSlice;
    private const int PrimLodSlices = 8;
    public int Dirty => _dirty.Count;
    public int SkippedSculptOrMesh { get; private set; }
    public int SkippedNoShape { get; private set; }
    public int SkippedAttachments { get; private set; }

    /// <summary>Where the avatars' attachment points are; null draws no attachments (they are counted and skipped).</summary>
    public IAttachmentFrames? Attachments { get; set; }

    /// <summary>Called once for each worn rigged mesh and reason it cannot be drawn (not once per frame): who wears it, where, which asset and why.
    /// A temporary reason (<see cref="RiggedAssessment.IsTemporary"/>) is followed by another try; the others stay until the asset changes.</summary>
    public Action<RiggedNotDrawn>? RiggedAttachmentMet { get; set; }

    private readonly HashSet<(uint Local, RiggedWhy Why)> _riggedReported = new();

    /// <summary>Called once for each worn rigged mesh the first time it is drawn: who wears it, where, which asset, at which level of detail.</summary>
    public Action<RiggedDrawn>? RiggedAttachmentDrawn { get; set; }

    private readonly HashSet<uint> _riggedDrawnReported = new();

    /// <summary>Worn objects drawn on an avatar; rigged meshes worn that are not drawn; avatars wearing at least one of them.</summary>
    public int AttachedObjects => _worn.Count;
    public int SkippedRiggedAttachments => _riggedWorn.Count;

    /// <summary>The avatars' body is whole, so the skeleton and joint textures a worn rigged mesh skins by exist: the ones found worn before that
    /// (counted in <see cref="SkippedRiggedAttachments"/>) are presented again.</summary>
    public void RetryRiggedAttachments()
    {
        foreach (var local in _riggedWorn) _dirty.Add(local);
        _riggedWorn.Clear();
    }

    /// <summary>Rigged meshes worn that are drawn skinned on their avatar, and the engine meshes made for them.</summary>
    public int RiggedObjects => _rigged.Count;
    public int RiggedMeshes => _riggedMeshes.Count;
    public int RiggedWearers => _riggedWearers.Count;

    public void MarkAll()
    {
        foreach (var o in _world.Snapshot().Objects.Keys)
            _dirty.Add(o);
    }

    public void Apply(WorldChange c)
    {
        switch (c.Kind)
        {
            case WorldChangeKind.RegionReset:
                ClearBurstHold();
                Clear();
                break;
            case WorldChangeKind.AvatarRemoved when !c.Handoff:
                ReleaseBurstOf(c.Id.LocalId);   // the wearer left: its held pieces go with it, in this frame
                break;
            case WorldChangeKind.LegacyMaterialsReceived:
                // the objects waiting for a material that has now arrived are drawn again with it
                foreach (var id in _waitingLegacy.Keys.ToList())
                    if (_world.LegacyMaterial(id) is not null && _waitingLegacy.Remove(id, out var objs))
                        foreach (var w in objs) _dirty.Add(w);
                break;
            case WorldChangeKind.AppearanceChanged:
                // the wearer's bakes changed: what its worn meshes' bake-on-mesh faces draw changes with them
                if (_world.Avatar(c.Id) is { } wearer)
                    foreach (var root in _world.AttachmentRoots(wearer.Id.LocalId))
                        foreach (var part in _world.Linkset(root.Id).Prepend(root))
                            if (part.Textures is { } worn && (NamesBake(worn.Default) || worn.Overrides.Any(f => f is { } x && NamesBake(x))))
                                _dirty.Add(part.Id.LocalId);
                break;
            case WorldChangeKind.ObjectAdded or WorldChangeKind.ObjectChanged or WorldChangeKind.ObjectMoved or WorldChangeKind.ObjectRemoved:
                if (WornFrameTrace.On && c.Kind != WorldChangeKind.ObjectMoved) TraceChange(c);
                if (CarryMoving) _moving.Note(c.Id, c.Kind != WorldChangeKind.ObjectMoved, MovingClock());   // an update sets a carried object at once
                if (c.Kind == WorldChangeKind.ObjectRemoved && HoldHandedOver(c)) break;   // another region took it over: it stays drawn until that region draws it
                if (c.Kind == WorldChangeKind.ObjectRemoved && HoldBurstRemoval(c)) break;   // another avatar's piece removed in a burst: drawn a moment longer
                if (c.Kind != WorldChangeKind.ObjectRemoved) { ForgetHeld(c); ForgetBurstHeld(c.Id.LocalId); ExpectHandedOver(c); NoteWearer(c); }
                _dirty.Add(c.Id.LocalId);
                // a moved or changed root moves its children
                if (c.Kind != WorldChangeKind.ObjectRemoved)
                    foreach (var child in _world.Linkset(c.Id))
                        _dirty.Add(child.Id.LocalId);
                break;
        }
    }

    /// <summary>The longest main-thread step so far, and the steps over the frame's budget (measurement only; <see cref="ResetStepTimes"/>).</summary>
    public double WorstStepMs { get; private set; }
    public int StepsOverBudget { get; private set; }
    public List<double> LongSteps { get; } = new();   // the steps over the budget, in order (at most 64)
    public void ResetStepTimes() { (WorstStepMs, StepsOverBudget, MostRiggedPartsInAStep) = (0, 0, 0); LongSteps.Clear(); }

    /// <summary>The most parts of a rigged mesh's build that one step did (a surface given to the engine, or a bind table copied into the atlas);
    /// a step does at most one, however many rigged meshes arrive together (measurement only; <see cref="ResetStepTimes"/>).</summary>
    public int MostRiggedPartsInAStep { get; private set; }

    /// <summary>Once a second: tells the material source which assets this layer's faces name, runs one step of its flush (what no layer's face names is
    /// dropped after 30 s, see <see cref="MaterialFlush"/>), and lets go of the resolved materials of assets no face names any more.</summary>
    private void SweepMaterials(long now)
    {
        if (MaterialAssets is not { } source || now - _materialSweepAt < System.Diagnostics.Stopwatch.Frequency) return;
        _materialSweepAt = now;
        source.Uses.Report(this, _pbrIds.Values.SelectMany(ids => ids));
        source.Flush(now / (double)System.Diagnostics.Stopwatch.Frequency);
        if (_resolved.Count > 0)
            foreach (var key in _resolved.Keys.Where(k => !source.Uses.InUse(k.Asset)).ToList())
                _resolved.Remove(key);
    }

    /// <summary>One unit of deferred work: (re)present one dirty prim. False when none is dirty.</summary>
    public bool Step()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _stepRiggedParts = 0;
        SweepMaterials(t0);
        bool worked;
        using (FrameProbe.Begin(FrameProbe.PrimStep)) worked = StepCore();
        if (_deferredByBudget) { _deferredByBudget = false; worked = false; }   // a unit put off to the next frame ends this frame's steps
        if (_stepRiggedParts > MostRiggedPartsInAStep) MostRiggedPartsInAStep = _stepRiggedParts;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms > WorstStepMs) WorstStepMs = ms;
        if (ms > StepBudgetMs) { StepsOverBudget++; if (LongSteps.Count < 64) LongSteps.Add(System.Math.Round(ms, 2)); }
        return worked;
    }

    /// <summary>The presentation frame's budget (<see cref="WorldPresenter"/>'s default), the length a step is judged against.</summary>
    public const double StepBudgetMs = 4.0;

    private bool StepCore()
    {
        var arrivalsProbe = FrameProbe.Begin(FrameProbe.PrimArrivals);
        // this layer's own queue of arrivals: the source is shared by every region's layer, and an arrival one layer took must still reach the others
        while (_meshSource is not null && (_meshFeed ??= _meshSource.Subscribe()).TryTake(out var arrived))
            if (_waiting.Remove(arrived, out var objs))
                foreach (var w in objs) _dirty.Add(w);
        // a LOD's surface data is ready: the objects that still want it are re-presented (and take it); if none does,
        // it is let go
        while (_preparer is not null && _preparer.TryTakeArrival(out var ready))
        {
            bool wanted = false;
            if (_lodWaiting.Remove(ready, out var waiters))
                foreach (var (w, at) in waiters)
                    if (_world.Object(new ObjectId(_world.Epoch, w)) is { Shape: { } sh } && sh.SculptId == ready.Asset && WantedLod(ready.Asset, _meshObjects.TryGetValue(w, out var mo) ? mo.Origin : at.Origin, at.Radius, w, at.Wearer) == ready.Lod)
                    {
                        _dirty.Add(w);
                        wanted = true;
                    }
            if (wanted) _handoverPending.Add(ready);
            else _preparer.Drop(ready);
        }
        // a rigged mesh's data is ready: the objects waiting for it are re-presented (and take it)
        while (_riggedPreparer is not null && _riggedPreparer.TryTakeArrival(out var riggedReady))
        {
            if (_riggedWaiting.Remove(riggedReady, out var wearers))
            {
                foreach (var w in wearers) _dirty.Add(w);
                _riggedHandoverPending.Add(riggedReady);
            }
            else _riggedPreparer.Drop(riggedReady);
        }
        if (MaterialAssets is not null && !ReferenceEquals(_materialFeedOf, MaterialAssets))
        {
            _materialFeed?.Leave();
            (_materialFeed, _materialFeedOf) = (MaterialAssets.Subscribe(), MaterialAssets);
        }
        while (_materialFeed is not null && _materialFeed.TryTake(out var marrived))
            if (_waitingMat.Remove(marrived, out var mobjs))
                foreach (var w in mobjs) _dirty.Add(w);
        Hub.DrainArrivals();   // one drain for every region's layer
        arrivalsProbe.End();
        if (_reupload.TryDequeue(out var reload))
        {
            Reupload(reload);   // one texture swapped back in per unit of work
            return true;
        }
        if (_dirty.Count == 0)
        {
            // ready data its objects did not take after all (the camera moved on) is let go
            foreach (var key in _handoverPending)
                if (!_lodWaiting.ContainsKey(key)) _preparer!.Drop(key);
            _handoverPending.Clear();
            foreach (var key in _riggedHandoverPending) _riggedPreparer!.Drop(key);
            _riggedHandoverPending.Clear();
            // a mesh whose objects went away before it was whole is let go; a whole one no longer needs its prepared table
            foreach (var (key, h) in _lodHandovers.ToList()) { Rids.Free(h.Mesh); _lodHandovers.Remove(key); }   // a mesh no object wants any more, whole or not
            foreach (var (key, h) in _riggedHandovers.ToList())
            {
                if (!h.Done) { Rids.Free(h.Mesh); }
                _riggedHandovers.Remove(key);
            }
            return false;
        }
        uint local = _dirty.Nearest;
        _dirty.Remove(local);
        (_srcPrepared, _srcFaces, _srcShape, _srcLod) = (null, null, null, 3);   // this object's surface data, when at hand
        var worldProbe = FrameProbe.Begin(FrameProbe.PrimWorld);
        var o = _world.Object(new ObjectId(_world.Epoch, local));
        _skipped.Remove(local);
        _skipReason.Remove(local);
        if (o is null && _held.ContainsKey(local)) { worldProbe.End(); return true; }   // handed over: the other region's layer draws it, this one until then
        if (o is null && KeepIfMovedOn(local)) { worldProbe.End(); return true; }       // the world let go of it for another region's world, and the removal is still queued
        if (o is null) { worldProbe.End(); Remove(local); return true; }
        AttachedPlacement? att = null;   // where the object hangs, when it is worn
        if (o.AttachmentPoint is not null || o.ParentLocalId != 0)
        {
            att = _world.Attachment(o.Id);
            if (att is not null && Attachments is null) { Skip(local, "worn, and the layer has no attachment frames", () => SkippedAttachments++); return true; }
            if (att is null && _held.ContainsKey(local) && _instances.ContainsKey(local)) return true;   // its wearer was handed to another region: drawn on until that region draws it
            if (att is null && KeepIfWearerMovedOn(local)) return true;   // its wearer went to another region before this piece did: drawn on until that region draws it
            if (att is null && (o.AttachmentPoint is not null || WornByAbsentAvatar(o))) { Remove(local); return true; }   // placed when its wearer arrives
        }
        if (o.Shape is null) { Skip(local, "no shape sent", () => SkippedNoShape++); return true; }
        bool isMesh = o.Shape.SculptType == PrimShape.SculptTypeMesh && _meshSource is not null;
        bool isSculpt = o.Shape.IsSculpt && _textureSource is not null;
        if (o.Shape.SculptType != 0 && !isMesh && !isSculpt) { Skip(local, "a sculpt or mesh type the viewer does not draw (not a mesh, and no sculpt map)", () => SkippedSculptOrMesh++); return true; }
        if (!isMesh) ForgetRigged(local);   // an object that was a rigged mesh and is not any more
        Transform3D xf;
        Worn? worn = null;
        Rigged? rigged = null;
        if (att is not null)
        {
            worn = new Worn { Wearer = att.WearerLocalId, Point = att.Point, InPoint = AttachmentPlacement.ObjectInPoint(att.Position, att.Rotation), Scale = o.Scale };
            bool onAvatar = Attachments!.TryPoint(worn.Wearer, worn.Point, out var point);
            xf = AttachedTransform(onAvatar ? worn.InPoint * point : worn.InPoint, o.Scale);
            worn.Shown = onAvatar;   // not on an avatar yet (its body not drawn): made hidden, shown by UpdateAttached
        }
        else if (!TryWorldTransform(o, out xf)) { worldProbe.End(); Remove(local); return true; }   // root not here yet: re-marked when it arrives (Apply)
        worldProbe.End();
        if (att is null && !float.IsPositiveInfinity(_gate.Distance))
        {
            // the draw distance decides what is built: beyond it nothing is presented (UpdateDrawDistance presents it when it comes within)
            var gateAt = Num(xf.Origin);
            float gateRadius = MeshLodRule.Radius(o.Scale);
            if (!_gate.Hold(local, gateAt, gateRadius, Num(Camera)))
            {
                Remove(local);   // forgets it too
                _gate.HoldBeyond(local, gateAt, gateRadius);
                return true;
            }
        }

        Rid mesh;
        int surfaces, lod = -1;
        IReadOnlyList<MeshFace>? mergeFaces = null;   // the faces' geometry, for the merge (prims and sculpts)
        if (isMesh)
        {
            var asset = o.Shape.SculptId;
            float radius = MeshLodRule.Radius(o.Scale);
            int detail = DetailFor(asset, att is not null ? WearerLodDistance(att.WearerLocalId, xf.Origin) : Camera.DistanceTo(xf.Origin), radius);
            // a wanted LOD already on the GPU needs no decoded geometry (the asset's LODs stay known after a let-go)
            if (_meshSource!.TryGetLods(asset, out var known) && MeshGeometry.Resolve(known, detail) is int onGpu && _meshAssets.TryGetValue((asset, onGpu), out var gpu))
            {
                lod = onGpu;
                (mesh, surfaces) = (gpu.Mesh, gpu.Surfaces);
                _meshObjects[local] = (asset, xf.Origin, radius, lod);
                goto placed;
            }
            var availability = _meshSource.TryGet(asset, out var g);
            if (availability == MeshAvailability.Pending)
            {
                if (!_waiting.TryGetValue(asset, out var set)) _waiting[asset] = set = new HashSet<uint>();
                set.Add(local);
                // its geometry was let go and is decoded again: an object drawing one of its LODs keeps drawing it
                // (re-presented when the decode arrives); a first draw waits as before
                if (_instances.TryGetValue(local, out var cur) && cur.Shape == o.Shape && _meshObjects.TryGetValue(local, out var was) && was.Asset == asset
                    && _meshAssets.TryGetValue((asset, cur.Lod), out var kept))
                {
                    lod = cur.Lod;
                    (mesh, surfaces) = (kept.Mesh, kept.Surfaces);
                    _meshObjects[local] = (asset, xf.Origin, radius, lod);
                    goto placed;
                }
                // a rigged piece worn keeps its skinned mesh in its own state, not in the LOD table above: it stays as drawn until the decode returns
                // (the official viewer keeps drawing an object's current volume until the new one is ready, llvovolume.cpp lodOrSculptChanged)
                if (att is not null && _rigged.ContainsKey(local) && _instances.TryGetValue(local, out var drawnRigged) && drawnRigged.Shape == o.Shape
                    && _meshObjects.TryGetValue(local, out var wasRigged) && wasRigged.Asset == asset)
                    return true;
                Remove(local);
                return true;
            }
            if (availability == MeshAvailability.Unavailable) { Skip(local, "the mesh asset is unavailable", () => SkippedMeshUnavailable++); return true; }
            if (att is not null && RiggedAssessment.IsRigged(g!))
            {
                // a rigged mesh worn follows the avatar's whole skeleton, not one joint: skinned in the shader at the avatar's place
                _keeper.Needed(asset, KeeperNow);
                var riggedProbe = FrameProbe.Begin(FrameProbe.PrimRigged);
                var built = RiggedMesh(local, att, asset, g, xf.Origin, out var skinned, out mesh, out surfaces, out lod, out var why);
                riggedProbe.End();
                if (built == RiggedBuild.Ready)
                {
                    rigged = skinned;
                    worn = null;   // it does not follow a point
                    _riggedWorn.Remove(local);
                    _riggedReported.RemoveWhere(r => r.Local == local);
                    if (_riggedDrawnReported.Add(local))
                        RiggedAttachmentDrawn?.Invoke(new RiggedDrawn(att.WearerLocalId, att.WearerAgentId, _world.Avatar(new ObjectId(_world.Epoch, att.WearerLocalId))?.Name ?? "", att.Point, asset, lod, skinned.Block.UsableJoints));
                    _meshObjects[local] = (asset, xf.Origin, WearerLodRadius(att.WearerLocalId), lod);
                    goto placed;
                }
                if (built == RiggedBuild.Pending)
                {
                    Remove(local);   // drawn once its data is ready (re-presented on arrival)
                    return true;
                }
                // not drawn (the reason says why): counted, and reported once for the reason
                Remove(local);
                _riggedWorn.Add(local);
                _riggedWearers.Add(att.WearerLocalId);
                if (_riggedReported.Add((local, why)))
                    RiggedAttachmentMet?.Invoke(new RiggedNotDrawn(att.WearerLocalId, att.WearerAgentId, _world.Avatar(new ObjectId(_world.Epoch, att.WearerLocalId))?.Name ?? "", att.Point, asset, why, RiggedAssessment.IsTemporary(why)));
                return true;
            }
            ForgetRigged(local);   // a mesh that is no longer a rigged one
            // a worn mesh whose skin could not be read is drawn as a plain mesh at its attachment point, as the official viewer draws it; reported once
            if (att is not null && g!.Skin is not null && _riggedDrawnReported.Add(local))
                RiggedAttachmentDrawn?.Invoke(new RiggedDrawn(att.WearerLocalId, att.WearerAgentId, _world.Avatar(new ObjectId(_world.Epoch, att.WearerLocalId))?.Name ?? "", att.Point, asset, g.Resolve(detail), 0, RiggedWhy.SkinNotRead));
            _keeper.Needed(asset, KeeperNow);   // a LOD request from the decoded geometry
            lod = g!.Resolve(detail);
            var key = (asset, lod);
            if (_meshAssets.TryGetValue(key, out var have)) { (mesh, surfaces) = (have.Mesh, have.Surfaces); _srcFaces = g.Lods[lod]!.Faces; }
            else if (TakeLodHandover(key, out bool preparationFailed) is { } staged)
            {
                _handoverPending.Remove(key);
                if (StageLodHandover(asset, lod, g.Lods[lod]!, staged))
                    (mesh, surfaces) = (_meshAssets[key].Mesh, _meshAssets[key].Surfaces);
                else
                {
                    // its surfaces go to the engine over several frames: an object drawing another level keeps it, any other shows once the mesh is whole
                    _dirty.Add(local);
                    if (_instances.TryGetValue(local, out var drawn) && drawn.Shape == o.Shape && _meshObjects.TryGetValue(local, out var drawnObj) && drawnObj.Asset == asset
                        && _meshAssets.TryGetValue((asset, drawn.Lod), out var kept))
                    {
                        lod = drawn.Lod;
                        (mesh, surfaces) = (kept.Mesh, kept.Surfaces);
                    }
                    else { Remove(local); return true; }
                }
            }
            else if (preparationFailed)
            {
                _handoverPending.Remove(key);
                (mesh, surfaces) = MeshAssetFor(asset, lod, g.Lods[lod]!, null, handover: true);   // its preparation failed: made here
            }
            else if (_preparer is null || MeshPrepareOnMainThread) (mesh, surfaces) = MeshAssetFor(asset, lod, g.Lods[lod]!);
            else
            {
                // the LOD's surface data is prepared off the main thread. An object switching LOD keeps drawing the LOD
                // it has until the new one is ready; a first build shows the object once its data is ready.
                _preparer.Request(key, g.Lods[lod]!.Faces);   // (marked needed above, with the request)
                if (!_lodWaiting.TryGetValue(key, out var waiters)) _lodWaiting[key] = waiters = new();
                waiters[local] = (xf.Origin, radius, att?.WearerLocalId ?? 0u);
                if (_instances.TryGetValue(local, out var cur) && cur.Shape == o.Shape && _meshObjects.TryGetValue(local, out var was) && was.Asset == asset
                    && _meshAssets.TryGetValue((asset, cur.Lod), out var previous))
                {
                    lod = cur.Lod;
                    (mesh, surfaces) = (previous.Mesh, previous.Surfaces);
                }
                else { Remove(local); return true; }
            }
            _meshObjects[local] = (asset, xf.Origin, radius, lod);
        }
        else if (isSculpt)
        {
            var map = o.Shape.SculptId;
            var availability = _textureSource!.TryGet(map, out var decoded);
            if (availability == MeshAvailability.Pending)
            {
                Remove(local);
                if (!_waitingTex.TryGetValue(map, out var set)) _waitingTex[map] = set = new HashSet<uint>();
                set.Add(local);
                return true;
            }
            var bias = SculptMesher.LodBias(o.Shape);
            float radius = (o.Scale * bias).Length();
            int slod = SculptLod(xf.Origin, radius);
            int type = o.Shape.SculptType | o.Shape.SculptFlags;
            long sculpt0 = System.Diagnostics.Stopwatch.GetTimestamp();   // the unit's time
            int sculptGc = GC.CollectionCount(0);
            var sculptPause = GC.GetTotalPauseDuration();
            var sculptAlloc = AllocationMeter.Shared.Begin(AllocKind.Sculpt);   // (measurement only)
            SculptResult r;
            using (FrameProbe.Begin(FrameProbe.PrimGeometry)) r = SculptMesher.FromMap(decoded, _textureSource.IsMissing(map), type, slod);
            if (r.Face is null) { sculptAlloc.End(); Skip(local, "the sculpt map gives no surface", () => SkippedSculptEmpty++); return true; }
            var key = (map, type, slod, r.Outcome);
            bool sculptBuilt = !_sculptMeshes.ContainsKey(key);
            if (!_sculptMeshes.TryGetValue(key, out mesh))
            {
                if (r.Outcome == SculptOutcome.SpherePlaceholder) SculptPlaceholders++;
                var sculptSurfaces = SculptSurfaces(r.Face);   // encoded here, where the sculpt is made
                _sculptMeshes[key] = mesh = HandOver(sculptSurfaces, out _);
                _surfaceBounds[mesh] = SurfaceBounds(sculptSurfaces);
            }
            long sculptBytes = sculptAlloc.End();
            if (sculptBuilt) GeometryUnit("sculpt", sculpt0, sculptGc, sculptPause, 1, sculptBytes);
            surfaces = 1;
            mergeFaces = [r.Face];
            _sculptObjects[local] = (map, xf.Origin, radius, slod, r.Face.Triangles);
            lod = slod * 4 + (int)r.Outcome;   // the instance key: LOD and outcome
        }
        else
        {
            float primRadius = PrimLodGroup.Radius(o.Shape, new System.Numerics.Vector3(o.Scale.X, o.Scale.Y, o.Scale.Z));
            int plod = PrimLod(xf.Origin, primRadius);
            bool primBuilt = !_meshes.ContainsKey((o.Shape, plod));   // a first build is a unit of its own
            long prim0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int primGc = GC.CollectionCount(0);
            var primPause = GC.GetTotalPauseDuration();
            var primAlloc = AllocationMeter.Shared.Begin(AllocKind.Prim);   // (measurement only)
            using (FrameProbe.Begin(FrameProbe.PrimGeometry)) (mesh, surfaces) = MeshFor(o.Shape, plod);
            _srcShape = o.Shape;
            _srcLod = plod;
            lod = plod;
            _primObjects[local] = (o.Shape, xf.Origin, primRadius, plod);
            if (MergeStill) mergeFaces = PrimFacesOf(o.Shape, plod);
            long primBytes = primAlloc.End();
            if (primBuilt) GeometryUnit("prim", prim0, primGc, primPause, surfaces, primBytes);
        }
    placed:
        var stepProbe = FrameProbe.Begin(FrameProbe.PrimInstance);
        if (_instances.TryGetValue(local, out var inst) && (inst.Shape != o.Shape || inst.Lod != lod))
        {
            Rids.Free(inst.Instance);
            _instances.Remove(local);
        }
        if (!_instances.TryGetValue(local, out inst))
        {
            inst = (Rids.InstanceCreate2(mesh, _scenario, "prim instance"), o.Shape, lod);
            _instances[local] = inst;
            _inGlowPicture.Remove(local);   // a new instance starts on the view's layer only
        }
        RenderingServer.InstanceSetTransform(inst.Instance, xf);
        if (worn is null && rigged is null && Carried(o)) _carriedXf[local] = xf; else _carriedXf.Remove(local);
        if (rigged is not null)
        {
            RenderingServer.InstanceSetCustomAabb(inst.Instance, RiggedBox);
            RenderingServer.InstanceGeometrySetShaderParameter(inst.Instance, EngineNames.SkinRow, rigged.Slot);
            rigged.Row = -1;   // the instance is new or re-made: UpdateAttached gives it the wearer's row and place
            rigged.Last = default;
            rigged.BoundsAt = xf.Origin;
            Show(local, inst.Instance, false);   // hidden until its wearer is drawn
            rigged.Shown = false;
        }
        stepProbe.End();
        stepProbe = FrameProbe.Begin(FrameProbe.PrimLight);
        PresentLight(local, o, xf);
        stepProbe.End();
        stepProbe = FrameProbe.Begin(FrameProbe.PrimMaterialSets);
        _balls[local] = (xf.Origin, o.Scale.Length() / 2);
        _glowDirty = true;
        var colors = o.Colors ?? new FaceColors(Rgba.White);
        if (att is not null) o = WithBakeOnMesh(o, att.WearerAgentId);   // faces naming a bake placeholder show the wearer's bake
        bool textured = o.Textures is not null && _textureSource is not null;
        var pbr = textured && rigged is null ? PbrFor(o, local, surfaces) : NoPbr(local);   // a rigged face draws its legacy texture entry
        var legacy = textured ? LegacyFor(o, local, surfaces, pbr) : NoLegacy(local);
        stepProbe.End();
        stepProbe = FrameProbe.Begin(FrameProbe.PrimUsers);
        if (textured)
        {
            RenderingServer.InstanceGeometrySetShaderParameter(inst.Instance, EngineNames.ObjScale,new Vector3(o.Scale.X, o.Scale.Y, o.Scale.Z));   // SL axes, for planar
            // each textured face is a user of its texture, with the object's bounding sphere (SL axes)
            var bounds = new Bounds(new System.Numerics.Vector3(xf.Origin.X, -xf.Origin.Z, xf.Origin.Y), o.Scale.Length() / 2);
            int had = _texUserFaces.GetValueOrDefault(local);
            for (int f = surfaces; f < had; f++) RemoveFaceUsers(local, f);
            ForgetPbrFaces(local, surfaces);
            // every texture of a face is a user with the face's repeats (the smaller of the two directions), as the
            // official viewer reads them from the face for each of its textures: a face showing part of its texture
            // needs more texels, one repeating it needs fewer (spec 4.2)
            for (int f = 0; f < surfaces; f++)
            {
                var face = o.Textures![f];
                float repeats = System.Math.Min(System.Math.Abs(face.RepeatU), System.Math.Abs(face.RepeatV));
                if (pbr?[f] is { } pm)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        var user = new TextureUser(TextureUserKind.Face, local, f + SlotUser * k, _space);
                        var slot = (PbrSlot)k;
                        // the normal, metallic-roughness and emissive slots are detail maps: fetched after base colours
                        if (pm.Texture(slot) is var id && id != Guid.Empty) Residency!.SetUser(user, id, bounds, repeats, use: slot == PbrSlot.BaseColor ? Viewer.Core.Net.TextureFetchClass.PbrBaseColour : Viewer.Core.Net.TextureFetchClass.Detail);
                        else Residency!.RemoveUser(user);
                    }
                    continue;
                }
                Residency!.SetUser(new TextureUser(TextureUserKind.Face, local, f, _space), face.TextureId, bounds, repeats);
                // a legacy material's normal and specular maps are detail maps too (slots 1 and 2): fetched after faces'
                // own textures and PBR base colours
                var lm = legacy?[f];
                SetMapUser(local, f, 1, lm?.Normal, bounds, repeats);
                SetMapUser(local, f, 2, lm?.Specular, bounds, repeats);
                Residency.RemoveUser(new TextureUser(TextureUserKind.Face, local, f + SlotUser * 3, _space));
            }
            _texUserFaces[local] = surfaces;
        }
        else if (_texUserFaces.Remove(local, out int was))
            for (int f = 0; f < was; f++) RemoveFaceUsers(local, f);
        if (!textured) ForgetPbrFaces(local, 0);
        stepProbe.End();
        stepProbe = FrameProbe.Begin(FrameProbe.PrimFaces);
        bool glows = false;
        int masked = 0;
        if (textured && TestForceGlow is { } force && _forcedGlowLocal is null && (xf.Origin - force.Point).Length() < force.Radius) _forcedGlowLocal = local;
        for (int f = 0; f < surfaces; f++)
        {
            var face = textured ? o.Textures![f] : default;
            if (textured && _forcedGlowLocal == local && f == 0) face = face with { Glow = TestForceGlow!.Value.Glow };   // the test switch: this one face glows
            if (textured && FaceGlow.Quantise(face.Glow) > 0f) glows = true;
            if (textured && pbr?[f] is null) ForgetPbrFaces(local, f, f + 1);
            var material = rigged is not null ? RiggedFace(textured ? face : null, textured ? legacy?[f] : null, colors[f], local, o.Materials?[f].Shiny ?? 0)
                : !textured ? MaterialFor(colors[f])
                : pbr?[f] is { } pm ? PbrFace(pm, face, colors[f], local, f)
                : legacy?[f] is { } lm ? LegacyFace(lm, face, colors[f], local, o.Materials?[f].Shiny ?? 0)
                : TexturedMaterial(face, colors[f], local);
            if (_maskMaterials.Contains(material)) masked++;
            if (f < MaxSurfaces)
            {
                _faceMaterials[f] = material;
                _faceBlended[f] = rigged is null && _blendMaterials.Contains(material);   // a rigged face is drawn by its one instance
                _faceShader[f] = material is ShaderMaterial;
            }
            else RenderingServer.InstanceSetSurfaceOverrideMaterial(inst.Instance, f, material.GetRid());
        }
        if (masked > 0) _maskedFaces[local] = masked; else _maskedFaces.Remove(local);
        if (glows) _glowSpheres[local] = (xf.Origin, o.Scale.Length() / 2); else _glowSpheres.Remove(local);
        // each blended face its own place in the order, and the own instance's materials (BlendedFaces sets them: the
        // mesh it draws may be a part of the object's mesh); a part mesh made here is a unit of its own
        stepProbe.End();
        stepProbe = FrameProbe.Begin(FrameProbe.PrimBlend);
        long blend0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int blendGc = GC.CollectionCount(0);
        var blendPause = GC.GetTotalPauseDuration();
        int partsBefore = _blend.PartsMade;
        _blend.Set(local, inst.Instance, mesh, Math.Min(surfaces, MaxSurfaces), xf, _faceMaterials, _faceBlended, _faceShader, _surfaceBounds.GetValueOrDefault(mesh),
            _world.Region?.WaterHeight, textured ? new Vector3(o.Scale.X, o.Scale.Y, o.Scale.Z) : null, _partSource);
        if (_blend.PartsMade != partsBefore) GeometryUnit("blend", blend0, blendGc, blendPause, _blend.PartsMade - partsBefore, 0);
        stepProbe.End();
        if (worn is not null)
        {
            worn.Last = xf;
            worn.BoundsAt = xf.Origin;
            _worn[local] = worn;
            if (!worn.Shown) Show(local, inst.Instance, false);
        }
        else _worn.Remove(local);
        if (CensusOn || MergeStill || _cells.Count > 0)
        {
            var tints = new Rgba[Math.Min(surfaces, MaxSurfaces)];
            for (int f = 0; f < tints.Length; f++) tints[f] = colors[f];
            using (FrameProbe.Begin(FrameProbe.PrimAfter)) AfterPresent(local, o, xf, surfaces, isMesh ? "mesh" : isSculpt ? "sculpt" : "prim", mergeFaces, tints, rigged is not null, att is not null);
        }
        return true;
    }

    private readonly Dictionary<(PrimShape, int), IReadOnlyList<MeshFace>> _primFaces = new();

    /// <summary>A prim shape's faces (SL axes), made once for the merge.</summary>
    private IReadOnlyList<MeshFace> PrimFacesOf(PrimShape shape, int lod)
    {
        if (!_primFaces.TryGetValue((shape, lod), out var faces))
            _primFaces[(shape, lod)] = faces = PrimGeometry.Build(shape, PrimLodGroup.Scales[Math.Clamp(lod, 0, 3)]).Select(f => new MeshFace(f.Positions, f.Normals, f.Uvs, f.Indices)).ToList();
        return faces;
    }

    /// <summary>A prim's or sculpt's first build as one upload unit, so the path reports can
    /// split the units over 4 ms by kind.</summary>
    private void GeometryUnit(string kind, long t0, int gc0, TimeSpan pause0, int surfaces, long allocBytes)
    {
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        UploadMsThisFrame += ms;
        UploadSamples.Add(new UploadSample(surfaces, 0, ms, true, false, 0, GC.CollectionCount(0) - gc0, (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds, Kind: kind, AllocBytes: allocBytes));
    }

    /// <summary>A terrain chunk's build (<see cref="WorldPresenter.Step"/>) as one
    /// upload unit of kind terrain (Width: its vertices, 0 for a removed chunk).</summary>
    public void TerrainUnit(long t0, int gc0, TimeSpan pause0, int vertices, long allocBytes) => GeometryUnit("terrain", t0, gc0, pause0, vertices, allocBytes);

    private readonly Dictionary<uint, string> _skipReason = new();

    private void Skip(uint local, string reason, System.Action count)
    {
        Remove(local);
        _skipReason[local] = reason;
        if (_skipped.Add(local))
            count();
    }

    /// <summary>The object, or the root it hangs from, is a child of something that is not an object: an avatar that is not here
    /// (yet). Nothing can place it until the avatar arrives; its arrival marks the objects again.</summary>
    private bool WornByAbsentAvatar(ObjectState o)
    {
        for (int depth = 0; depth < 8 && o.ParentLocalId != 0; depth++)
        {
            if (_world.Object(new ObjectId(o.Id.Epoch, o.ParentLocalId)) is not { } parent)
                return depth > 0;   // the first link missing is a root still to come; further up it is the wearer
            o = parent;
        }
        return false;
    }

    /// <summary>A worn object's engine transform from its frame in the region (SL axes) and its scale.</summary>
    private static Transform3D AttachedTransform(in System.Numerics.Matrix4x4 frame, System.Numerics.Vector3 scale)
    {
        var r = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.CreateFromRotationMatrix(frame));
        var q = new Quaternion(r.X, r.Z, -r.Y, r.W).Normalized();
        var basis = new Basis(q) * Basis.FromScale(new Vector3(scale.X, scale.Z, scale.Y));
        return new Transform3D(basis, new Vector3(frame.M41, frame.M43, -frame.M42));
    }

    private void Show(uint local, Rid instance, bool shown)
    {
        RenderingServer.InstanceSetVisible(instance, shown);
        _blend.SetVisible(local, shown);
    }

    /// <summary>Main thread, each frame after the avatars are posed: every worn object goes to its attachment point's place. An object
    /// whose avatar is not drawn is hidden until it is.</summary>
    public void UpdateAttached()
    {
        PollHandoffs();
        ReleaseDueBursts();
        if (Attachments is null) return;
        if (_rigged.Count > 0) UpdateRigged(Attachments);
        if (_worn.Count == 0) return;
        foreach (var (local, w) in _worn)
        {
            if (!_instances.TryGetValue(local, out var inst)) continue;
            bool onAvatar = Attachments.TryPoint(w.Wearer, w.Point, out var point);
            if (onAvatar != w.Shown) { w.Shown = onAvatar; Show(local, inst.Instance, onAvatar); }
            if (!onAvatar) continue;
            var xf = AttachedTransform(w.InPoint * point, w.Scale);
            if (xf == w.Last) continue;
            _blend.Move(local, w.Last, xf);
            RenderingServer.InstanceSetTransform(inst.Instance, xf);
            w.Last = xf;
            // its textures' bounds were given where it was presented: when it has gone far from there it is presented again, so the
            // residency judges its textures where it is now
            if (xf.Origin.DistanceTo(w.BoundsAt) > WornBoundsMetres) { _dirty.Add(local); w.BoundsAt = xf.Origin; }
            // the level of detail follows the distance to where the object is now
            if (_meshObjects.TryGetValue(local, out var m)) _meshObjects[local] = (m.Asset, xf.Origin, m.Radius, m.Lod);
            if (_sculptObjects.TryGetValue(local, out var s)) _sculptObjects[local] = (s.Map, xf.Origin, s.Radius, s.Lod, s.Triangles);
            if (_primObjects.TryGetValue(local, out var po)) _primObjects[local] = (po.Shape, xf.Origin, po.Radius, po.Lod);
        }
    }

    /// <summary>Off in the render test driver: its pictures must not depend on how long a scene took to draw, so there an object
    /// stays where the region states it.</summary>
    public static bool CarryMoving = true;

    /// <summary>The clock the carried objects run on (seconds).</summary>
    public Func<double> MovingClock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Whether the object, or the root it is linked to, is carried between updates.</summary>
    private bool Carried(ObjectState o) => _moving.Count > 0 && (_moving.Carries(o.Id.LocalId) || (o.ParentLocalId != 0 && _moving.Carries(o.ParentLocalId)));

    /// <summary>Main thread, each frame: the objects that move or spin between the region's updates go to where they are now, with
    /// their linked children. Only the transform changes; the object is not presented again.</summary>
    public void UpdateMoving()
    {
        if (_moving.Count == 0) return;
        var changed = _moving.Frame(MovingClock());
        for (int i = 0; i < changed.Count; i++)
        {
            var o = _world.Object(new ObjectId(_world.Epoch, changed[i]));
            if (o is null) continue;
            MoveCarried(o);
            if (o.ParentLocalId == 0)
                foreach (var child in _world.Linkset(o.Id))
                    MoveCarried(child);
        }
    }

    private void MoveCarried(ObjectState o)
    {
        uint local = o.Id.LocalId;
        if (!_carriedXf.TryGetValue(local, out var was) || !_instances.TryGetValue(local, out var inst)) return;   // not presented yet: placed when it is
        if (!TryWorldTransform(o, out var xf) || xf == was) return;
        _blend.Move(local, was, xf);
        RenderingServer.InstanceSetTransform(inst.Instance, xf);
        _carriedXf[local] = xf;
        // the level of detail follows the distance to where the object is now
        if (_meshObjects.TryGetValue(local, out var m)) _meshObjects[local] = (m.Asset, xf.Origin, m.Radius, m.Lod);
        if (_sculptObjects.TryGetValue(local, out var s)) _sculptObjects[local] = (s.Map, xf.Origin, s.Radius, s.Lod, s.Triangles);
        if (_primObjects.TryGetValue(local, out var po)) _primObjects[local] = (po.Shape, xf.Origin, po.Radius, po.Lod);
    }

    private bool TryWorldTransform(ObjectState o, out Transform3D xf)
    {
        var pos = o.Position;
        var rot = o.Rotation;
        if (_moving.Count > 0 && _moving.TryPlace(o.Id.LocalId, out var carriedPos, out var carriedRot)) (pos, rot) = (carriedPos, carriedRot);
        if (o.ParentLocalId != 0)
        {
            var parent = _world.Object(new ObjectId(o.Id.Epoch, o.ParentLocalId));
            if (parent is null) { xf = default; return false; }
            var (parentPos, parentRot) = (parent.Position, parent.Rotation);
            if (_moving.Count > 0 && _moving.TryPlace(o.ParentLocalId, out var rootPos, out var rootRot)) (parentPos, parentRot) = (rootPos, rootRot);
            pos = parentPos + System.Numerics.Vector3.Transform(pos, parentRot);
            rot = parentRot * rot;
        }
        var q = new Quaternion(rot.X, rot.Z, -rot.Y, rot.W).Normalized();
        var basis = new Basis(q) * Basis.FromScale(new Vector3(o.Scale.X, o.Scale.Z, o.Scale.Y));
        xf = new Transform3D(basis, new Vector3(pos.X, pos.Z, -pos.Y));
        return true;
    }

    private (Rid, int) MeshFor(PrimShape shape, int lod)
    {
        if (_meshes.TryGetValue((shape, lod), out var m))
            return m;
        using var meshProbe = FrameProbe.Begin(FrameProbe.PrimMeshAsset);
        // encoded here, on the main thread, where the prim's geometry is made; the engine is handed the bytes
        var prepared = PrimSurfaces(shape, lod);
        var mesh = HandOver(prepared, out _);
        _surfaceBounds[mesh] = SurfaceBounds(prepared);
        _srcPrepared = prepared;
        return _meshes[(shape, lod)] = (mesh, prepared.Surfaces.Count);
    }

    /// <summary>A prim shape's surfaces in the engine's layout, one per face (<see cref="PrimGeometry.Build"/>).</summary>
    internal static PreparedMeshLod PrimSurfaces(PrimShape shape, int lod = 3) =>
        MeshSurfaceData.Prepare(PrimGeometry.Build(shape, PrimLodGroup.Scales[Math.Clamp(lod, 0, 3)]).Select(f => new MeshFace(f.Positions, f.Normals, f.Uvs, f.Indices)).ToList());

    /// <summary>Each surface's box in the mesh's space (engine axes): its centre and size.</summary>
    private static (Vector3 Centre, Vector3 Size)[] SurfaceBounds(PreparedMeshLod lod)
    {
        var b = new (Vector3, Vector3)[lod.Surfaces.Count];
        for (int i = 0; i < b.Length; i++)
        {
            var a = lod.Surfaces[i].Aabb;
            var size = new Vector3(a[3], a[4], a[5]);
            b[i] = (new Vector3(a[0], a[1], a[2]) + size / 2, size);
        }
        return b;
    }

    /// <summary>A sculpt's one surface in the engine's layout.</summary>
    internal static PreparedMeshLod SculptSurfaces(MeshFace face) => MeshSurfaceData.Prepare([face]);

    // a mesh LOD being given to the engine a few surfaces a frame; once all are in it is a mesh asset
    private sealed class LodHandover
    {
        public readonly Rid Mesh;
        public readonly PreparedMeshLod Prepared;
        public readonly double[] Sizes;
        public int Next;
        public double EngineMs, Ms;
        public int Gen0;
        public double PauseMs;
        public LodHandover(Rid mesh, PreparedMeshLod prepared)
        {
            (Mesh, Prepared) = (mesh, prepared);
            Sizes = prepared.Surfaces.Select(SurfaceBytes).ToArray();
        }
    }
    private readonly Dictionary<(Guid Asset, int Lod), LodHandover> _lodHandovers = new();

    /// <summary>The LOD's hand-over in progress, or a new one from the surfaces just prepared off the main thread; null when there is neither (or the
    /// preparation failed, which the caller makes here).</summary>
    private LodHandover? TakeLodHandover((Guid Asset, int Lod) key, out bool preparationFailed)
    {
        preparationFailed = false;
        if (_lodHandovers.TryGetValue(key, out var staged)) return staged;
        if (_preparer is null || MeshPrepareOnMainThread || !_preparer.TryTake(key, out var prepared)) return null;
        if (prepared is null) { preparationFailed = true; return null; }
        return _lodHandovers[key] = new LodHandover(Rids.MeshCreate("prim mesh"), prepared);
    }

    /// <summary>Gives the engine the LOD's next surfaces while the frame's budget allows (all of them without one). True when the mesh is whole.</summary>
    private bool StageLodHandover(Guid asset, int lod, MeshLod geometry, LodHandover st)
    {
        var surfaces = st.Prepared.Surfaces;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int gc0 = GC.CollectionCount(0);
        var pause0 = GC.GetTotalPauseDuration();
        var alloc = AllocationMeter.Shared.Begin(AllocKind.Mesh);   // (measurement only)
        int first = st.Next, vertices = 0, triangles = 0;
        void Give(int i)
        {
            using (FrameProbe.Begin(FrameProbe.PrimMeshAsset)) st.EngineMs += HandOver(st.Mesh, surfaces[i]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            System.Threading.Interlocked.Increment(ref SurfacesAdded);
            vertices += surfaces[i].VertexCount;
            triangles += surfaces[i].IndexCount / 3;
        }
        if (Budget is null)
            while (st.Next < surfaces.Count) Give(st.Next++);
        else if (st.Next < surfaces.Count && StagedHandOver.Run(st.Sizes, ref st.Next, Budget, _handOverCosts, Give) == 0)
        {
            _deferredByBudget = true;
            HandOversDeferred++;
        }
        long bytes = alloc.End();
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (st.Next > first)
        {
            UploadMsThisFrame += ms;
            UploadSamples.Add(new UploadSample(vertices, triangles, ms, true, true, 0, GC.CollectionCount(0) - gc0, (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds, Mesh: true, Kind: "mesh", AllocBytes: bytes));
            st.Ms += ms;
        }
        if (st.Next < surfaces.Count)
        {
            if (st.Next > first) _deferredByBudget = true;   // the rest waits for the next frame
            return false;
        }
        _lodHandovers.Remove((asset, lod));
        FinishMeshAsset(asset, lod, geometry, st.Prepared, st.Mesh, off: true, prepareMs: 0, st.EngineMs, st.Ms);
        return true;
    }

    /// <summary>A mesh LOD's GPU mesh, made once per (asset, LOD). From its surface data prepared off the main thread
    /// (<paramref name="prepared"/>: the main thread only hands it over); when that preparation failed (null with
    /// <paramref name="handover"/>), or there is no preparer, made here from the geometry as before.</summary>
    private (Rid, int) MeshAssetFor(Guid asset, int lod, MeshLod geometry, PreparedMeshLod? prepared = null, bool handover = false)
    {
        if (_meshAssets.TryGetValue((asset, lod), out var m))
            return (m.Mesh, m.Surfaces);
        using var meshProbe = FrameProbe.Begin(FrameProbe.PrimMeshAsset);
        _keeper.Needed(asset, KeeperNow);   // a build from the decoded geometry
        _uploadClock.Restart();
        int gc0 = GC.CollectionCount(0);
        var pause0 = GC.GetTotalPauseDuration();
        var alloc = AllocationMeter.Shared.Begin(AllocKind.Mesh);   // (measurement only)
        double prepareMs = 0, engineMs;
        bool off = prepared is not null;   // prepared off the main thread
        if (prepared is null)
        {
            long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
            prepared = MeshSurfaceData.Prepare(geometry.Faces);   // encoded here (no preparer, or its preparation failed)
            prepareMs = (System.Diagnostics.Stopwatch.GetTimestamp() - p0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
        Rid built = HandOver(prepared, out engineMs);
        double ms = _uploadClock.Elapsed.TotalMilliseconds;
        UploadMsThisFrame += ms;
        int vertices = geometry.Faces.Sum(f => f.Vertices);
        UploadSamples.Add(new UploadSample(vertices, geometry.Triangles, ms, true, off, prepareMs, GC.CollectionCount(0) - gc0, (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds, Mesh: true, Kind: "mesh", AllocBytes: alloc.End()));
        return FinishMeshAsset(asset, lod, geometry, prepared, built, off, prepareMs, engineMs, ms);
    }

    /// <summary>A mesh LOD whole on the engine's side: its bounds, its place among the mesh assets and the figures kept for reports.</summary>
    private (Rid, int) FinishMeshAsset(Guid asset, int lod, MeshLod geometry, PreparedMeshLod prepared, Rid built, bool off, double prepareMs, double engineMs, double ms)
    {
        long bytes = MeshFootprint.Bytes(geometry.Faces);
        _surfaceBounds[built] = SurfaceBounds(prepared);
        var m = (built, geometry.Faces.Count, bytes, geometry.Triangles);
        _meshAssets[(asset, lod)] = m;
        _srcPrepared = prepared;
        int vertices = geometry.Faces.Sum(f => f.Vertices);
        if (MeshBuilds.Count < 4096) MeshBuilds.Add(new MeshBuild(vertices, geometry.Triangles, geometry.Faces.Count, ms, prepareMs, engineMs, off));
        if (_meshDrawn.Add(asset) && _meshSource?.ArrivedAt(asset) is long arrived)
            MeshDecodeToDrawMs.Add((System.Diagnostics.Stopwatch.GetTimestamp() - arrived) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        MeshBytesHeld += bytes;
        PeakMeshBytesHeld = Math.Max(PeakMeshBytesHeld, MeshBytesHeld);
        PeakMeshLodsHeld = Math.Max(PeakMeshLodsHeld, _meshAssets.Count);
        return (built, geometry.Faces.Count);
    }

    /// <summary>One mesh RID with a surface per face (SL axes to engine axes; SL winds outward faces
    /// anticlockwise, Godot clockwise, so the encoder reverses every triangle). Encoded on the calling (main)
    /// thread and handed over: a mesh LOD whose off-thread preparation failed, and the --tr-mesh-main-thread control.</summary>
    internal static Rid SurfaceMesh(IReadOnlyList<MeshFace> faces) => SurfaceMesh(faces, out _, out _);

    /// <summary>
    /// The main thread's part of a mesh LOD build: the surfaces prepared off the main thread
    /// (<see cref="MeshSurfaceData"/>, the engine's own surface layout) handed to the engine as they are. The Godot
    /// dictionary and byte arrays are made here, on the main thread, as are the RenderingServer calls (the thread model
    /// stays at its default; see <see cref="PreparedUpload"/>).
    /// </summary>
    internal static Rid HandOver(PreparedMeshLod lod, out double engineMs)
    {
        long engine = 0, t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Rid mesh = Rids.MeshCreate("prim mesh");
        engine += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        foreach (var s in lod.Surfaces)
        {
            engine += HandOver(mesh, s);
            System.Threading.Interlocked.Increment(ref SurfacesAdded);
        }
        engineMs = engine * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return mesh;
    }

    /// <summary>
    /// One surface already in the engine's layout added to <paramref name="mesh"/>; every surface
    /// the viewer draws (mesh LODs, prims, sculpts, terrain, water) reaches the engine here, on the main thread. The
    /// viewer never asks the engine to pack arrays (MeshAddSurfaceFromArrays): that binding makes its own blend-shape array
    /// and LOD dictionary and holds neither through the call (the crash; SurfaceCallScanTests). Returns
    /// the engine call's own time (Stopwatch ticks). The byte arrays and the AABB become variants with
    /// native storage; each is held until the engine call has returned (HeldValues). The keys are made once
    /// (EngineNames); ints and longs have no native storage. The surface dictionary is the
    /// process's kept one (made at startup, never disposed), pinned while in use and cleared after the call; the held
    /// values go to the disposal queue, disposed pinned after the frame's upload budget, never inside this unit.
    /// </summary>
    internal static unsafe long HandOver(Rid mesh, PreparedSurface s)
    {
        var a = s.Aabb;
        long engine;
        using var held = new HeldValues();
        var k = KeptCollections.Handover;   // kept for the process, pinned while in use, cleared after the call
        var d = k.Surface;
        fixed (byte* pd = &KeptCollections.Pin(d))
        {
            try
            {
                d[EngineNames.Primitive] = (int)RenderingServer.PrimitiveType.Triangles;
                d[EngineNames.Format] = (long)s.Format;
                d[EngineNames.VertexData] = held.Add(s.VertexData);
                d[EngineNames.VertexCount] = s.VertexCount;
                d[EngineNames.Aabb] = held.Add(new Aabb(new Vector3(a[0], a[1], a[2]), new Vector3(a[3], a[4], a[5])));
                if (s.AttributeData.Length > 0) d[EngineNames.AttributeData] = held.Add(s.AttributeData);
                if (s.IndexCount > 0) { d[EngineNames.IndexData] = held.Add(s.IndexData); d[EngineNames.IndexCount] = s.IndexCount; }
                long e0 = System.Diagnostics.Stopwatch.GetTimestamp();
                RenderingServer.MeshAddSurface(mesh, d);
                engine = System.Diagnostics.Stopwatch.GetTimestamp() - e0;
            }
            finally { k.Clear(); }
        }
        GC.KeepAlive(held);
        return engine;
    }

    /// <summary>As <see cref="SurfaceMesh(IReadOnlyList{MeshFace})"/>, timed (measurement): the encoding on this
    /// thread, and the RenderingServer calls.</summary>
    private static Rid SurfaceMesh(IReadOnlyList<MeshFace> faces, out double prepareMs, out double engineMs)
    {
        long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var prepared = MeshSurfaceData.Prepare(faces);
        prepareMs = (System.Diagnostics.Stopwatch.GetTimestamp() - p0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return HandOver(prepared, out engineMs);
    }

    /// <summary>The mesh LOD builds, the largest three and a typical one (the median by vertices),
    /// with each one's split, and the decode-to-first-draw times.</summary>
    public object MeshBuildReport()
    {
        static object Row(MeshBuild b) => new { vertices = b.Vertices, triangles = b.Triangles, surfaces = b.Surfaces, ms = Math.Round(b.Ms, 3), prepare_ms = Math.Round(b.PrepareMs, 3),
            engine_ms = Math.Round(b.EngineMs, 3), other_ms = Math.Round(b.Ms - b.PrepareMs - b.EngineMs, 3), prepared_off_thread = b.Prepared };
        static object? Dist(List<double> v)
        {
            if (v.Count == 0) return null;
            v.Sort();
            return new { n = v.Count, p50 = Math.Round(v[v.Count / 2], 3), p95 = Math.Round(v[Math.Min(v.Count - 1, (int)Math.Ceiling(v.Count * 0.95) - 1)], 3), max = Math.Round(v[^1], 3) };
        }
        var bySize = MeshBuilds.OrderByDescending(b => b.Vertices).ToList();
        return new
        {
            builds = MeshBuilds.Count, over_4ms = MeshBuilds.Count(b => b.Ms > 4), prepared_off_thread = MeshBuilds.Count(b => b.Prepared),
            main_thread_prepare_ms_total = Math.Round(MeshBuilds.Sum(b => b.PrepareMs), 2), engine_ms_total = Math.Round(MeshBuilds.Sum(b => b.EngineMs), 2), ms_total = Math.Round(MeshBuilds.Sum(b => b.Ms), 2),
            largest = bySize.Take(3).Select(Row).ToArray(), typical = bySize.Count == 0 ? null : Row(bySize[bySize.Count / 2]),
            ms = Dist(MeshBuilds.Select(b => b.Ms).ToList()), decode_to_first_draw_ms = Dist(MeshDecodeToDrawMs.ToList()),
        };
    }

    /// <summary>
    /// A face's textured material. Legacy SL alpha: blended when the texture is not opaque (the class: Binary or
    /// Blended; masking arrives with materials) or the colour's alpha is below 1; opaque otherwise. A texture still
    /// loading is drawn with the neutral placeholder and the face is re-presented when it arrives.
    /// </summary>
    private Material TexturedMaterial(FaceTexture ft, Rgba tint, uint local, bool skinned = false, bool pbrFallback = false)
    {
        var id = ft.TextureId;
        bool ready = Granted(id, local) is not null;
        // blended, cut-out or opaque: the plain rule with the official automatic alpha mask (a rigged face, and a PBR face showing its legacy texture for now, are never masked)
        var alpha = FaceLooks.Plain(ready ? _alpha[id] : null, new AutoMaskFace(tint.A, ft.Glow, ready && _alphaMasks.Contains(id), skinned, ft.Fullbright, Hud: false, HasPbrMaterial: pbrFallback, LegacyMaterialBlends: false), AutoMaskAlpha);
        bool blend = alpha == FaceAlpha.Blend;
        if (ready && !skinned && !pbrFallback && _alpha[id] != Viewer.Core.Imaging.AlphaClass.Opaque) Wait(_maskDependents, id, local);   // the cut-out rule read this texture: drawn again if its level changes the answer
        float glow = FaceGlow.Quantise(ft.Glow);
        var key = (ready ? ft.TextureId : Guid.Empty, tint, ft.RepeatU, ft.RepeatV, ft.OffsetU, ft.OffsetV, ft.Rotation, ft.TexGen == TexGen.Planar, ft.Fullbright, alpha, glow);
        var cache = skinned ? _skinTexMaterials : _texMaterials;
        if (cache.TryGetValue(key, out var m))
            return m;
        m = new ShaderMaterial { Shader = skinned ? KeptShaders.FaceSkinned(ft.Fullbright, blend) : alpha == FaceAlpha.Mask ? KeptShaders.FaceMasked(ft.Fullbright) : KeptShaders.Face(ft.Fullbright, blend) };   // the process's kept variant (warmed at start-up)
        if (skinned) SkinParameters(m);
        else if (blend) _blendMaterials.Add(m);
        else if (alpha == FaceAlpha.Mask) _maskMaterials.Add(m);
        // the names are made once (EngineNames), not a temporary StringName per call; the texture is held by
        // _gpuTextures or _placeholder, and the variant carrying it goes with the drain, not a finalizer; the other
        // values have no native storage
        VariantDisposal.SetShaderParameter(m, EngineNames.Tex, ready ? _gpuTextures[id] : Placeholder());
        m.SetShaderParameter(EngineNames.Tint, new Vector4(tint.R, tint.G, tint.B, tint.A));   // raw sRGB (a Color would be converted to linear)
        m.SetShaderParameter(EngineNames.Rep, new Vector2(ft.RepeatU, ft.RepeatV));
        m.SetShaderParameter(EngineNames.Off, new Vector2(ft.OffsetU, ft.OffsetV));
        m.SetShaderParameter(EngineNames.Rot, ft.Rotation);
        m.SetShaderParameter(EngineNames.Planar, ft.TexGen == TexGen.Planar);
        if (glow > 0f) m.SetShaderParameter(EngineNames.GlowW, glow);
        if (!skinned && alpha == FaceAlpha.Opaque && ft.TexGen != TexGen.Planar)
            _texKeyOf[m] = new TexKey(ready ? ft.TextureId : Guid.Empty, ft.RepeatU, ft.RepeatV, ft.OffsetU, ft.OffsetV, ft.Rotation, ft.Fullbright, glow);   // what the merge groups faces by (the tint aside)
        return cache[key] = m;
    }

    /// <summary>
    /// A texture's slot once it has one, else null. A texture is fetched only once the residency manager grants it
    /// (priority order, within the budget); until then the face waits on a placeholder and is re-presented when it
    /// arrives. Once it has a slot, the slot is bound whatever its residency.
    /// </summary>
    private ImageTexture? Granted(Guid id, uint local)
    {
        if (_gpuTextures.TryGetValue(id, out var have)) return have;
        if (Residency!.StateOf(id) == ResidencyState.Loading)
        {
            var availability = _textureSource!.TryGetLevel(id, _requested.GetValueOrDefault(id, TextureResidency.Coarsest), out var decoded);
            if (availability == MeshAvailability.Ready) return GpuTexture(id, decoded!);
            if (availability == MeshAvailability.Pending) Wait(_waitingTex, id, local);
            else { _requested.Remove(id); Residency.LoadFailed(id); }   // missing or refused: stays on the placeholder
        }
        else if (!Residency.HasFailed(id))
        {
            _textureSource?.Timeline?.Mark(id, Viewer.Core.Assets.TextureStage.Wanted);   // waiting for the residency's grant
            Wait(_waitingGrant, id, local);
        }
        return null;
    }

    /// <summary>
    /// Each face's PBR material (the asset with the face's glTF override applied) where the face has one and its asset
    /// is read; null when no face has. A face whose asset is still being fetched keeps its legacy look and the object is
    /// re-presented when the asset arrives; a face whose asset could not be read keeps its legacy look.
    /// </summary>
    private PbrMaterial?[]? PbrFor(ObjectState o, uint local, int surfaces)
    {
        if (MaterialAssets is null || o.Materials is null) return NoPbr(local);
        PbrMaterial?[]? r = null;
        int drawn = 0, notOpaque = 0;
        List<Guid>? named = null;
        for (int f = 0; f < surfaces; f++)
        {
            var id = o.Materials[f].RenderMaterialId;
            if (id == Guid.Empty) continue;
            (named ??= []).Add(id);
            var availability = MaterialAssets.TryGet(id, out var asset);
            if (availability == MeshAvailability.Pending) { Wait(_waitingMat, id, local); continue; }
            if (availability != MeshAvailability.Ready) continue;
            var over = o.MaterialOverrides?[f];
            if (!_resolved.TryGetValue((id, over), out var m)) _resolved[(id, over)] = m = asset!.WithOverride(over);
            (r ??= new PbrMaterial?[surfaces])[f] = m;
            drawn++;
            if (m.AlphaMode != PbrAlphaMode.Opaque) notOpaque++;
        }
        int beyond = 0;
        for (int f = surfaces; f < o.Materials.Overrides.Count; f++)
            if (o.Materials.Overrides[f] is { } x && x.RenderMaterialId != Guid.Empty) beyond++;
        if (drawn == 0 && beyond == 0) _pbrFaces.Remove(local);
        else _pbrFaces[local] = (drawn, notOpaque, beyond);
        if (named is null) _pbrIds.Remove(local);
        else _pbrIds[local] = [.. named];
        return r;
    }

    private PbrMaterial?[]? NoPbr(uint local)
    {
        _pbrFaces.Remove(local);
        _pbrIds.Remove(local);
        return null;
    }

    /// <summary>
    /// Each face's legacy material where the face names one (and has no PBR material drawn) and the region has sent it;
    /// null when no face has. A face whose material has not arrived keeps its plain look and the object is drawn again
    /// when it arrives.
    /// </summary>
    private LegacyMaterial?[]? LegacyFor(ObjectState o, uint local, int surfaces, PbrMaterial?[]? pbr)
    {
        if (o.Materials is null) return NoLegacy(local);
        LegacyMaterial?[]? r = null;
        int drawn = 0;
        for (int f = 0; f < surfaces; f++)
        {
            var id = o.Materials[f].MaterialId;
            if (id == Guid.Empty || pbr?[f] is not null) continue;
            if (_world.LegacyMaterial(id) is not { } m) { Wait(_waitingLegacy, id, local); continue; }
            (r ??= new LegacyMaterial?[surfaces])[f] = m;
            drawn++;
        }
        if (drawn == 0) _legacyFaces.Remove(local);
        else _legacyFaces[local] = drawn;
        return r;
    }

    private LegacyMaterial?[]? NoLegacy(uint local)
    {
        _legacyFaces.Remove(local);
        return null;
    }

    /// <summary>A legacy material map's residency user (slot 1 normal, 2 specular), in the detail-map fetch class, with
    /// the map's own repeats; removed when the face has no such map.</summary>
    private void SetMapUser(uint local, int f, int slot, MaterialMap? map, Bounds bounds, float repeats)
    {
        var user = new TextureUser(TextureUserKind.Face, local, f + SlotUser * slot, _space);
        if (map is { } m && m.TextureId != Guid.Empty)
            Residency!.SetUser(user, m.TextureId, bounds, repeats, use: Viewer.Core.Net.TextureFetchClass.Detail);
        else Residency!.RemoveUser(user);
    }

    /// <summary>
    /// A legacy-material face's material (<see cref="FaceLooks.Legacy"/>): the legacy material shader in the face's alpha
    /// mode, with the face texture, tint and placement as a plain face, and the material's normal and specular maps once
    /// each is on the GPU (until then: no normal map, the specular colour alone). The alpha mode is judged from the
    /// texture's alpha once it is on screen (the placeholder has none). The face is drawn again when each arrives.
    /// </summary>
    private Material LegacyFace(LegacyMaterial lm, FaceTexture ft, Rgba tint, uint local, byte shiny, bool skinned = false)
    {
        var id = ft.TextureId;
        bool ready = Granted(id, local) is not null;
        var look = FaceLooks.Legacy(lm, tint.A, ready ? _alpha[id] : null, shiny);
        int maps = 0;
        if (look.HasNormalMap && Granted(look.Normal.TextureId, local) is not null) maps |= 1;
        if (look.HasSpecularMap && Granted(look.Specular.TextureId, local) is not null) maps |= 2;
        var key = (lm, ft, tint, look.Alpha, ready ? id : Guid.Empty, maps, shiny);
        var cache = skinned ? _skinLegacyMaterials : _legacyMaterials;
        if (cache.TryGetValue(key, out var m))
            return m;
        m = new ShaderMaterial { Shader = skinned ? KeptShaders.LegacySkinned(ft.Fullbright, look.Alpha) : KeptShaders.Legacy(ft.Fullbright, look.Alpha) };   // the process's kept variant (warmed at start-up)
        if (skinned) SkinParameters(m);
        else if (look.Alpha == FaceAlpha.Blend) _blendMaterials.Add(m);
        // textures are held by _gpuTextures or _placeholder; the variant carrying each goes with the drain
        VariantDisposal.SetShaderParameter(m, EngineNames.Tex, ready ? _gpuTextures[id] : Placeholder());
        m.SetShaderParameter(EngineNames.Tint, new Vector4(tint.R, tint.G, tint.B, tint.A));   // raw sRGB, as a plain face
        m.SetShaderParameter(EngineNames.Rep, new Vector2(ft.RepeatU, ft.RepeatV));
        m.SetShaderParameter(EngineNames.Off, new Vector2(ft.OffsetU, ft.OffsetV));
        m.SetShaderParameter(EngineNames.Rot, ft.Rotation);
        m.SetShaderParameter(EngineNames.Planar, ft.TexGen == TexGen.Planar);
        if ((maps & 1) != 0)
        {
            VariantDisposal.SetShaderParameter(m, EngineNames.NrmTex, _gpuTextures[look.Normal.TextureId]);
            m.SetShaderParameter(EngineNames.NrmXf, new Vector4(look.Normal.RepeatU, look.Normal.RepeatV, look.Normal.OffsetU, look.Normal.OffsetV));
            m.SetShaderParameter(EngineNames.NrmRot, look.Normal.Rotation);
        }
        m.SetShaderParameter(EngineNames.UseNormal, (maps & 1) != 0);
        if ((maps & 2) != 0)
        {
            VariantDisposal.SetShaderParameter(m, EngineNames.SpecTex, _gpuTextures[look.Specular.TextureId]);
            m.SetShaderParameter(EngineNames.SpecXf, new Vector4(look.Specular.RepeatU, look.Specular.RepeatV, look.Specular.OffsetU, look.Specular.OffsetV));
            m.SetShaderParameter(EngineNames.SpecRot, look.Specular.Rotation);
        }
        m.SetShaderParameter(EngineNames.UseSpec, (maps & 2) != 0);
        m.SetShaderParameter(EngineNames.SpecCol, new Vector3(look.SpecularColour.R, look.SpecularColour.G, look.SpecularColour.B));
        m.SetShaderParameter(EngineNames.Gloss, look.Glossiness);
        m.SetShaderParameter(EngineNames.AlphaCutoff, look.Cutoff);
        if (FaceGlow.Quantise(ft.Glow) > 0f) m.SetShaderParameter(EngineNames.GlowW, FaceGlow.Quantise(ft.Glow));
        return cache[key] = m;
    }

    /// <summary>
    /// What a PBR face draws (<see cref="LegacyFallback"/>): its PBR material once its base colour is on screen, from
    /// then on; until then its legacy texture (as a legacy face draws it) when that texture is on the GPU, else its PBR
    /// material on the placeholder. A legacy texture another user wants is waited for, so the face is presented again
    /// when it arrives; it is never asked for here.
    /// </summary>
    private Material PbrFace(PbrMaterial pm, FaceTexture ft, Rgba tint, uint local, int f)
    {
        var face = (local, f);
        var baseId = pm.Texture(PbrSlot.BaseColor);
        bool baseOn = baseId == Guid.Empty || Granted(baseId, local) is not null;   // waits for the base colour too
        var legacy = ft.TextureId;
        bool usable = LegacyFallback && legacy != Guid.Empty && legacy != baseId;
        bool legacyOn = usable && _gpuTextures.ContainsKey(legacy);
        bool elsewhere = usable && !legacyOn && Residency!.HasUserOtherThan(legacy, u => u.Kind == TextureUserKind.Face && u.Space == _space && u.Id == local && u.Face % SlotUser == f);
        var (draw, wait) = Viewer.Core.Residency.LegacyFallback.Choose(LegacyFallback, _pbrSwitched.Contains(face), baseOn, legacyOn, elsewhere);
        if (wait)
        {
            // another user has it on its way: picked up here if it is ready (the upload that user would make), else
            // the face waits for it as that user does; a texture still waiting for its grant is not marked as wanted here
            if (Residency!.StateOf(legacy) == ResidencyState.Loading) { if (Granted(legacy, local) is not null) draw = PbrFaceDraw.Legacy; }
            else Wait(_waitingGrant, legacy, local);
        }
        if (draw == PbrFaceDraw.Legacy)
        {
            _fallbackFaces[face] = legacy;
            _fallbackTextures.Add(legacy);
            _pbrFacesWaiting.Remove(face);
            return TexturedMaterial(ft, tint, local, pbrFallback: true);
        }
        if (_fallbackFaces.Remove(face)) FallbackSwitches++;
        if (baseOn)
        {
            _pbrSwitched.Add(face);
            _pbrFacesWaiting.Remove(face);
        }
        else _pbrFacesWaiting.Add(face);
        return PbrFaceMaterial(pm, ft.TexGen == TexGen.Planar, local, FaceGlow.Quantise(ft.Glow));
    }

    /// <summary>Forgets the fallback state of an object's faces from <paramref name="from"/> (up to <paramref name="to"/>):
    /// faces gone, or no longer drawn with a PBR material.</summary>
    private void ForgetPbrFaces(uint local, int from, int to = int.MaxValue)
    {
        if (_pbrSwitched.Count == 0 && _fallbackFaces.Count == 0 && _pbrFacesWaiting.Count == 0) return;
        // an object has at most MaxSurfaces faces: each is looked up, so the cost does not grow with the faces of every other object
        // (a scan of all three tables made releasing thousands of objects at once take seconds)
        for (int f = Math.Max(from, 0); f < Math.Min(to, MaxSurfaces); f++)
        {
            _pbrSwitched.Remove((local, f));
            _pbrFacesWaiting.Remove((local, f));
            _fallbackFaces.Remove((local, f));
        }
    }

    /// <summary>
    /// A PBR face's material: the PBR shader (single- or double-sided) with the material's factors and transforms, and
    /// each slot's texture once it is on the GPU. Until then the base colour slot shows the neutral placeholder, the
    /// emissive slot black, the normal map is off and the metallic-roughness slot is white (the factors alone); the face
    /// is re-presented when each arrives. The material's alpha mode picks the shader variant (opaque, blend, or mask at its
    /// cutoff: <see cref="FaceLooks.Pbr"/>).
    /// </summary>
    private Material PbrFaceMaterial(PbrMaterial pm, bool planar, uint local, float glow)
    {
        int ready = 0;
        for (int k = 0; k < 4; k++)
            if (pm.Texture((PbrSlot)k) is var id && id != Guid.Empty && Granted(id, local) is not null) ready |= 1 << k;
        var key = (pm, planar, ready, glow);
        if (_pbrMaterials.TryGetValue(key, out var m))
            return m;
        var (alpha, cutoff) = FaceLooks.Pbr(pm);
        m = new ShaderMaterial { Shader = KeptShaders.Pbr(pm.DoubleSided, alpha) };   // the process's kept variant (warmed at start-up)
        if (alpha == FaceAlpha.Blend) _blendMaterials.Add(m);
        if (alpha == FaceAlpha.Mask) m.SetShaderParameter(EngineNames.AlphaCutoff, cutoff);
        for (int k = 0; k < 4; k++)
        {
            var slot = (PbrSlot)k;
            var id = pm.Texture(slot);
            // textures are held by _gpuTextures, _placeholder or _black; the variant carrying each goes with the drain
            if ((ready & (1 << k)) != 0) VariantDisposal.SetShaderParameter(m, EngineNames.PbrTex[k], _gpuTextures[id]);
            else if (id != Guid.Empty && slot == PbrSlot.BaseColor) VariantDisposal.SetShaderParameter(m, EngineNames.PbrTex[k], Placeholder());
            else if (id != Guid.Empty && slot == PbrSlot.Emissive) VariantDisposal.SetShaderParameter(m, EngineNames.PbrTex[k], Black());
            var t = pm.Transform(slot);
            m.SetShaderParameter(EngineNames.PbrXf[k], new Vector4(t.ScaleU, t.ScaleV, t.Rotation, 0));
            m.SetShaderParameter(EngineNames.PbrOff[k], new Vector2(t.OffsetU, t.OffsetV));
        }
        m.SetShaderParameter(EngineNames.UseNormal, (ready & (1 << (int)PbrSlot.Normal)) != 0);
        m.SetShaderParameter(EngineNames.BaseFactor, new Vector4(pm.BaseColor.R, pm.BaseColor.G, pm.BaseColor.B, pm.BaseColor.A));   // linear, as glTF
        m.SetShaderParameter(EngineNames.EmisFactor, new Vector3(pm.Emissive.R, pm.Emissive.G, pm.Emissive.B));
        m.SetShaderParameter(EngineNames.Metallic, pm.Metallic);
        m.SetShaderParameter(EngineNames.Roughness, pm.Roughness);
        m.SetShaderParameter(EngineNames.Planar, planar);
        if (glow > 0f) m.SetShaderParameter(EngineNames.GlowW, glow);
        return _pbrMaterials[key] = m;
    }

    private ImageTexture Black()
    {
        if (_black is not null) return _black;
        var img = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        img.Fill(new Color(0f, 0f, 0f, 1f));
        _black = ImageTexture.CreateFromImage(img);
        img.Dispose();
        return _black;
    }

    private void RemoveFaceUsers(uint local, int f)
    {
        for (int k = 0; k < 4; k++) Residency?.RemoveUser(new TextureUser(TextureUserKind.Face, local, f + SlotUser * k, _space));
    }

    private static void Wait(Dictionary<Guid, HashSet<uint>> waiting, Guid id, uint local)
    {
        if (!waiting.TryGetValue(id, out var set)) waiting[id] = set = new HashSet<uint>();
        set.Add(local);
    }

    /// <summary>The texture's slot, created with the full image at its first (granted) upload; reported to the manager.</summary>
    private ImageTexture GpuTexture(Guid id, Viewer.Core.Imaging.DecodedTexture d)
    {
        if (_gpuTextures.TryGetValue(id, out var t))
            return t;
        _uploadClock.Restart();
        int gc0 = GC.CollectionCount(0);
        var pause0 = GC.GetTotalPauseDuration();
        var alloc = AllocationMeter.Shared.Begin(AllocKind.Texture);   // (measurement only)
        var (img, average, owned) = ImageFor(id, _requested.GetValueOrDefault(id, TextureResidency.Coarsest), d);
        double imageMs = _uploadClock.Elapsed.TotalMilliseconds;
        t = ImageTexture.CreateFromImage(img);
        if (_projectorTextures.Contains(id)) KeepProjectorSource(id, img, d.FullWidth);   // from the engine image: a level prepared off the main thread has no pixels of its own
        if (owned) img.Dispose();
        _alpha[id] = d.Alpha;
        if (d.IsAlphaMask) _alphaMasks.Add(id); else _alphaMasks.Remove(id);
        Residency?.Uploaded(id, d.Level, d.LevelCount, d.FullWidth, d.FullHeight, TextureFootprint.Bytes(d.Width, d.Height, true), average, Now);
        _textureSource?.NoteUploaded(id);   // the texture's first time on screen
        if (_requested.Remove(id, out int level)) _textureSource?.ReleaseLevel(id, level);
        double ms = _uploadClock.Elapsed.TotalMilliseconds;
        UploadMsThisFrame += ms;
        UploadSamples.Add(new UploadSample(d.Width, d.Height, ms, true, !owned, imageMs, GC.CollectionCount(0) - gc0, (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds, AllocBytes: alloc.End()));
        return _gpuTextures[id] = t;
    }

    private ImageTexture Placeholder()
    {
        if (_placeholder is not null) return _placeholder;
        var img = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        img.Fill(new Color(0.5f, 0.5f, 0.5f, 1f));
        _placeholder = ImageTexture.CreateFromImage(img);
        img.Dispose();
        return _placeholder;
    }

    /// <summary>The joint-matrix textures a skinning material reads (the wearer's presenter owns them for as long as it lives).</summary>
    private void SkinParameters(ShaderMaterial m)
    {
        var tex = Attachments!.SkinTextures!;
        VariantDisposal.SetShaderParameter(m, EngineNames.SkinBones, tex.Bones);
        VariantDisposal.SetShaderParameter(m, EngineNames.SkinAtlas, tex.Atlas);
    }

    private static readonly IReadOnlyDictionary<string, Guid> NoBakes = System.Collections.Frozen.FrozenDictionary<string, Guid>.Empty;

    private static bool NamesBake(FaceTexture f) => Viewer.Core.Avatars.BakeChannels.IsBakeId(f.TextureId);

    /// <summary>The object with every face that names a bake placeholder (bake on mesh) naming what it stands for on this wearer instead:
    /// the wearer's bake for that slot, or the default texture while there is none (<see cref="BakeOnMesh"/>). The faces then go the
    /// ordinary way, so a bake is fetched, drawn grey until it arrives, and drawn when it has. The object itself in the world state is not changed.</summary>
    private ObjectState WithBakeOnMesh(ObjectState o, Guid wearer)
    {
        if (o.Textures is not { } t) return o;
        var bakes = _world.Appearance(wearer)?.Bakes ?? NoBakes;
        bool any = false;
        FaceTexture Map(FaceTexture f)
        {
            var r = Viewer.Core.Avatars.BakeOnMesh.Resolve(f.TextureId, bakes);
            if (r.Kind == Viewer.Core.Avatars.BomKind.NotBake) return f;
            any = true;
            return f with { TextureId = r.Id };
        }
        var def = Map(t.Default);
        var faces = t.Overrides.Select(f => f is { } x ? Map(x) : (FaceTexture?)null).ToArray();
        return any ? o with { Textures = new FaceTextures(def, faces) } : o;
    }

    /// <summary>A rigged mesh face's material: skinning, with the face's texture, or its legacy material, or just its colour on white.</summary>
    private Material RiggedFace(FaceTexture? ft, LegacyMaterial? lm, Rgba tint, uint local, byte shiny)
    {
        // a bake-on-mesh face has already been given the texture it stands for (WithBakeOnMesh)
        if (ft is { } face) return lm is { } legacy ? LegacyFace(legacy, face, tint, local, shiny, skinned: true) : TexturedMaterial(face, tint, local, skinned: true);
        if (_skinColourMaterials.TryGetValue(tint, out var m)) return m;
        m = new ShaderMaterial { Shader = KeptShaders.FaceSkinned(false, tint.A < 0.999f) };
        SkinParameters(m);
        VariantDisposal.SetShaderParameter(m, EngineNames.Tex, _white ??= WhiteTexture());
        m.SetShaderParameter(EngineNames.Tint, new Vector4(tint.R, tint.G, tint.B, tint.A));
        return _skinColourMaterials[tint] = m;
    }

    private static ImageTexture WhiteTexture()
    {
        var img = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        img.Fill(Colors.White);
        var t = ImageTexture.CreateFromImage(img);
        img.Dispose();
        return t;
    }

    private enum RiggedBuild { Ready, Pending, Unavailable }

    /// <summary>Rigged meshes worn that found the bind-table atlas at its limit: not drawn until a table is freed.</summary>
    private readonly HashSet<uint> _awaitingTable = [];

    /// <summary>A rigged mesh worn: the engine mesh for the level of detail its wearer's distance asks for (made once for an asset and level, with
    /// each vertex's joints and weights), the mesh's rows in the atlas, and the object's state. The surfaces and the table are prepared off the main
    /// thread (<see cref="RiggedMeshPreparation"/>): <see cref="RiggedBuild.Pending"/> while that runs (an object already drawn at another level keeps
    /// that one), and the main thread only hands them to the engine. <see cref="RiggedBuild.Unavailable"/> when it cannot be skinned now: the skeleton
    /// or the joint textures are not there yet, the decoder read no full skin, the level has no weights or they do not match its faces, or no
    /// row is left in the atlas.</summary>
    private RiggedBuild RiggedMesh(uint local, AttachedPlacement att, Guid asset, MeshGeometry g, Vector3 near, out Rigged state, out Rid mesh, out int surfaces, out int lod, out RiggedWhy why)
    {
        why = RiggedWhy.Drawable;
        state = null!;
        mesh = default;
        surfaces = 0;
        lod = -1;
        if (Attachments is not { SkinTextures: { } textures, Rig: { } rig }) { why = RiggedWhy.WaitingForBody; return RiggedBuild.Unavailable; }
        // the level of detail follows the wearer, as the official viewer's does for a rigged mesh: its distance, and the size of its box
        lod = g.Resolve(MeshLodRule.Detail(WearerLodDistance(att.WearerLocalId, near), WearerLodRadius(att.WearerLocalId), LodFactor, LodFovY));
        why = RiggedAssessment.Assess(g, lod);
        if (why != RiggedWhy.Drawable) return RiggedBuild.Unavailable;
        var data = g.Skin!.Data!;
        var block = data.Block!;
        var geometry = g.Lods[lod]!;
        var influences = data.Lods[lod]!;
        var key = (asset, lod);
        PreparedRiggedLod? prepared = null;
        if (!_riggedMeshes.TryGetValue(key, out var built))
        {
            if (!_riggedHandovers.TryGetValue(key, out var handover))
            {
                if (_riggedPreparer is not null && !MeshPrepareOnMainThread)
                {
                    if (_riggedPreparer.TryTake(key, out var ready))
                    {
                        _riggedHandoverPending.Remove(key);
                        prepared = ready as PreparedRiggedLod;   // null: the preparation failed, made here below
                    }
                    else
                    {
                        _riggedPreparer.Request(key, () =>
                        {
                            var counted = AllocationMeter.Shared.Begin(AllocKind.RiggedPrepare);   // (measurement only)
                            try { return RiggedMeshPreparation.Prepare(geometry.Faces, influences, rig, block); }
                            finally { counted.End(); }
                        });
                        if (!_riggedWaiting.TryGetValue(key, out var waiters)) _riggedWaiting[key] = waiters = new();
                        waiters.Add(local);
                        return RiggedWaiting(local, att, asset, out state, out mesh, out surfaces, out lod);
                    }
                }
                if (prepared is null) { using var prepProbe = FrameProbe.Begin(FrameProbe.PrimRiggedPrep); prepared = RiggedMeshPreparation.Prepare(geometry.Faces, influences, rig, block); }   // here when there is no preparer or its preparation failed
                _riggedHandovers[key] = handover = new RiggedHandover(Rids.MeshCreate("rigged mesh handover"), prepared);
            }
            // the engine mesh is given one surface per step, and the bind table is copied in a step of its own: a step does what fits the
            // frame's budget however many rigged meshes arrive together. The object stays in the queue and is presented again for the next part.
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int gc = GC.CollectionCount(0);
            var pause = GC.GetTotalPauseDuration();
            var alloc = AllocationMeter.Shared.Begin(AllocKind.Mesh);   // (measurement only)
            if (handover.Next < handover.Prepared.Surfaces.Count)
            {
                if (!MayHandOver(handover.Prepared.Surfaces[handover.Next]))
                {
                    _dirty.Add(local);   // presented again next frame, when the surface fits (or goes first)
                    return RiggedWaiting(local, att, asset, out state, out mesh, out surfaces, out lod);
                }
                using (FrameProbe.Begin(FrameProbe.PrimRiggedHand)) HandOverMeasured(handover.Mesh, handover.Prepared.Surfaces[handover.Next++]);
                _stepRiggedParts++;
            }
            if (handover.Next >= handover.Prepared.Surfaces.Count)
            {
                _riggedMeshes[key] = (handover.Mesh, geometry.Faces.Count);
                handover.Done = true;
            }
            GeometryUnit("rigged", t0, gc, pause, 1, alloc.End());
            _dirty.Add(local);
            return RiggedWaiting(local, att, asset, out state, out mesh, out surfaces, out lod);
        }
        if (_riggedHandovers.TryGetValue(key, out var finished) && finished.Done) prepared = finished.Prepared;   // its table, made off the main thread
        if (!_rigged.TryGetValue(local, out state!) || state.Asset != asset)
        {
            ForgetRigged(local);
            if (prepared is not null) _stepRiggedParts++;
            int slot;
            using (FrameProbe.Begin(FrameProbe.PrimRiggedTable)) slot = textures.AcquireTable(asset, rig, block, prepared);
            if (prepared is not null) _riggedHandovers.Remove(key);
            if (slot < 0) { _awaitingTable.Add(local); why = RiggedWhy.TableAtlasFull; return RiggedBuild.Unavailable; }   // the atlas is at its limit: presented again when a table is freed
            state = new Rigged { Wearer = att.WearerLocalId, Asset = asset, Block = block, Slot = slot };
            _rigged[local] = state;
        }
        else state.Wearer = att.WearerLocalId;
        (mesh, surfaces) = built;
        return RiggedBuild.Ready;
    }

    /// <summary>The radius a worn rigged mesh's level of detail is chosen with: the diagonal of its wearer's box (the avatar's size as the region
    /// states it), not the attachment's own size.</summary>
    private float WearerLodRadius(uint wearerLocalId) =>
        RiggedAssessment.WearerRadius(_world.Avatar(new ObjectId(_world.Epoch, wearerLocalId))?.Scale ?? System.Numerics.Vector3.Zero);

    /// <summary>The distance a worn rigged mesh's level of detail is chosen with: the camera to its wearer (<paramref name="fallback"/>, the
    /// attachment's place, while the wearer is not known).</summary>
    private float WearerLodDistance(uint wearerLocalId, Vector3 fallback)
    {
        // the official viewer takes it from the camera to the nearest point of the wearer's box, 0.01 m at least (LLDrawable::updateDistance)
        if (Attachments is not { } frames || !frames.TryRigged(wearerLocalId, out _, out var place)) return Camera.DistanceTo(fallback);
        var centre = AttachedTransform(place, System.Numerics.Vector3.One).Origin;
        var size = _world.Avatar(new ObjectId(_world.Epoch, wearerLocalId))?.Scale ?? System.Numerics.Vector3.Zero;
        var cam = Camera;
        return RiggedAssessment.WornDistance(new System.Numerics.Vector3(cam.X, -cam.Z, cam.Y), default, new System.Numerics.Vector3(centre.X, -centre.Z, centre.Y), size);   // the region's axes
    }

    /// <summary>The object's mesh at the level it wants is not ready (still being prepared, or being handed to the engine a surface at a time): an
    /// object already drawn at another level keeps it, and any other draws nothing yet.</summary>
    private RiggedBuild RiggedWaiting(uint local, AttachedPlacement att, Guid asset, out Rigged state, out Rid mesh, out int surfaces, out int lod)
    {
        (mesh, surfaces, lod) = (default, 0, -1);
        if (_instances.TryGetValue(local, out var cur) && _rigged.TryGetValue(local, out state!) && state.Asset == asset
            && _riggedMeshes.TryGetValue((asset, cur.Lod), out var previous))
        {
            state.Wearer = att.WearerLocalId;
            lod = cur.Lod;
            (mesh, surfaces) = previous;
            return RiggedBuild.Ready;
        }
        state = null!;
        return RiggedBuild.Pending;
    }

    /// <summary>The object is no longer a rigged mesh worn (removed, or it changed): its wearer forgets its overrides, the atlas its table.</summary>
    private void ForgetRigged(uint local)
    {
        if (!_rigged.Remove(local, out var r)) return;
        if (r.Registered) Attachments?.UnwearRigged(r.Wearer, local);
        if (r.Slot >= 0 && Attachments?.SkinTextures?.ReleaseTable(r.Asset) == true && _awaitingTable.Count > 0)
        {
            foreach (var waiting in _awaitingTable) _dirty.Add(waiting);   // a table is free: the meshes that found none try again
            _awaitingTable.Clear();
        }
    }

    /// <summary>Each rigged mesh worn goes where its wearer is, with the wearer's row of joint matrices; hidden while the wearer is not drawn.
    /// A new object tells its wearer (its joint overrides apply from the next pose).</summary>
    private void UpdateRigged(IAttachmentFrames frames)
    {
        foreach (var (local, r) in _rigged)
        {
            if (!_instances.TryGetValue(local, out var inst)) continue;
            if (!r.Registered) r.Registered = frames.WearRigged(r.Wearer, local, r.Asset, r.Block);
            int row = -1;
            System.Numerics.Matrix4x4 place = default;
            bool onAvatar = r.Registered && frames.TryRigged(r.Wearer, out row, out place);
            if (onAvatar != r.Shown) { r.Shown = onAvatar; Show(local, inst.Instance, onAvatar); }
            if (!onAvatar) continue;
            if (row != r.Row)
            {
                r.Row = row;
                RenderingServer.InstanceGeometrySetShaderParameter(inst.Instance, EngineNames.AvatarRow, row);
            }
            var xf = AttachedTransform(place, System.Numerics.Vector3.One);
            if (xf == r.Last) continue;
            RenderingServer.InstanceSetTransform(inst.Instance, xf);
            r.Last = xf;
            if (xf.Origin.DistanceTo(r.BoundsAt) > WornBoundsMetres) { _dirty.Add(local); r.BoundsAt = xf.Origin; }
            if (_meshObjects.TryGetValue(local, out var m)) _meshObjects[local] = (m.Asset, xf.Origin, m.Radius, m.Lod);
        }
    }

    private StandardMaterial3D MaterialFor(Rgba c)
    {
        if (_materials.TryGetValue(c, out var m))
            return m;
        m = ColourMaterial(c);
        if (m.Transparency != BaseMaterial3D.TransparencyEnum.Disabled) _blendMaterials.Add(m);
        return _materials[c] = m;
    }

    /// <summary>An untextured face's material in colour <paramref name="c"/> (also made by the warm-up and kept by
    /// <see cref="KeptShaders"/>, so the engine keeps its shader).</summary>
    internal static StandardMaterial3D ColourMaterial(Rgba c) => new()
    {
        AlbedoColor = new Color(c.R, c.G, c.B, c.A),
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // winding is verified with textures; both sides lit for now
        Roughness = 0.9f,
        Transparency = c.A < 0.999f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
    };

    /// <summary>Test hook (--tr-lights=): "omni" draws projectors as plain point lights; "sun", "ambient" and "none" draw no prim lights.</summary>
    public static string? TestLights;

    private const int ProjectorFace = 1 << 20;   // the projector picture's user slot on an object (faces use 0 to 31 and their slot offsets)
    private readonly ProjectorLights _lights;

    /// <summary>The prim's local light or projector as an engine light (<see cref="LightRules"/>); the projector's picture is a texture user of the object
    /// and is on the light once granted (the light shines without it meanwhile).</summary>
    private void PresentLight(uint local, ObjectState o, Transform3D xf)
    {
        var e = o.Light is { } l && TestLights is not ("sun" or "ambient" or "none") ? LightRules.ForEngine(l, TestLights == "omni" ? null : o.Projector, o.Scale) : null;
        Rid tex = default;
        if (e is { Beam: not null } && o.Projector is { } p && TestLights != "omni" && o.Light is { } pl && _textureSource is not null && Residency is not null)
        {
            var bounds = new Bounds(new System.Numerics.Vector3(xf.Origin.X, -xf.Origin.Z, xf.Origin.Y), LightRules.Reach(pl));
            Residency.SetUser(new TextureUser(TextureUserKind.Face, local, ProjectorFace, _space), p.Texture, bounds, 1f);
            _projectorTextures.Add(p.Texture);   // before the texture is uploaded, so its upload keeps a small copy for the picture
            if (Granted(p.Texture, local) is { } raw)
                tex = _projectorSources.TryGetValue(p.Texture, out var src) ? ProjectorPictureFor(local, p.Texture, e.Value.Beam!.Value, src).GetRid()
                    : raw.GetRid();   // the texture was already on the GPU when asked for (a face uses it): drawn as it is, its corners cut by the cone
        }
        else
        {
            Residency?.RemoveUser(new TextureUser(TextureUserKind.Face, local, ProjectorFace, _space));
            ForgetProjectorPicture(local);
        }
        _lights.Set(local, e, xf.Origin, xf.Basis.Orthonormalized(), tex, o.Light?.Radius ?? 0f);
        bool lamp = e is { IsSpot: true };
        if (lamp || _projectorPrims.Contains(local))
        {
            if (lamp) _projectorPrims.Add(local); else _projectorPrims.Remove(local);
            uint mask = GlowLayerOf(local);
            if (_instances.TryGetValue(local, out var li)) RenderingServer.InstanceSetLayerMask(li.Instance, mask);
            _blend.SetLayerMask(local, mask);
        }
        DisposeRetiredPictures();
    }

    // The textures used as a projector's picture, a small copy of each kept when it is uploaded (at most ProjectorSourceSize a side),
    // and the picture made for each light (ProjectorPicture: the window in its canvas, blurred by the focus, with the ambiance).
    private readonly HashSet<Guid> _projectorTextures = new();
    private readonly Dictionary<Guid, (byte[] Rgba, int Width, int Height, int FullWidth)> _projectorSources = new();
    private readonly Dictionary<uint, (Guid Texture, Beam Beam, ImageTexture Picture)> _projectorPictures = new();
    private const int ProjectorSourceSize = 256;
    private readonly List<ImageTexture> _retiredPictures = new();

    /// <summary>Frees the pictures no light uses any more. Called after the light has been given its new picture or removed: freeing a texture a light still names crashes the engine.</summary>
    private void DisposeRetiredPictures()
    {
        foreach (var t in _retiredPictures) t.Dispose();
        _retiredPictures.Clear();
    }

    private void KeepProjectorSource(Guid id, Image img, int fullWidth)
    {
        // the image's bytes hold level 0 first, then its mip chain when it has one
        if (ProjectorPicture.SourceFrom(img.GetData(), img.GetWidth(), img.GetHeight(), ProjectorSourceSize) is { } s)
            _projectorSources[id] = (s.Rgba, s.Width, s.Height, fullWidth);
    }

    private ImageTexture ProjectorPictureFor(uint local, Guid id, Beam beam, (byte[] Rgba, int Width, int Height, int FullWidth) src)
    {
        if (_projectorPictures.TryGetValue(local, out var have) && have.Texture == id && have.Beam == beam) return have.Picture;
        ForgetProjectorPicture(local);
        var bytes = ProjectorPicture.Render(src.Rgba, src.Width, src.Height, beam, src.FullWidth);
        var img = Image.CreateFromData(ProjectorPicture.Size, ProjectorPicture.Size, false, Image.Format.Rgba8, bytes);
        var tex = ImageTexture.CreateFromImage(img);
        img.Dispose();
        _projectorPictures[local] = (id, beam, tex);
        return tex;
    }

    private void ForgetProjectorPicture(uint local)
    {
        if (_projectorPictures.Remove(local, out var old)) _retiredPictures.Add(old.Picture);   // freed once no light uses it (DisposeRetiredPictures)
    }

    private void RemoveLight(uint local)
    {
        ForgetProjectorPicture(local);
        Residency?.RemoveUser(new TextureUser(TextureUserKind.Face, local, ProjectorFace, _space));
        _lights.Remove(local);
        DisposeRetiredPictures();
    }

    /// <summary>The engine lights held for prims' lights (omni, spot).</summary>
    public (int Omni, int Spot) LightKinds => _lights.Kinds();

    private void Remove(uint local, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (WornFrameTrace.On && IsWornLocal(local))
            WornFrameTrace.Event(this, "remove", local, _wearerAgent.GetValueOrDefault(local).Full, $"line={line} held={_held.ContainsKey(local)}");
        using (FrameProbe.Begin(FrameProbe.PrimRemove)) RemoveCore(local);
    }

    private void RemoveCore(uint local)
    {
        if (_texUserFaces.Remove(local, out int faces))
            for (int f = 0; f < faces; f++) RemoveFaceUsers(local, f);
        _pbrFaces.Remove(local);
        _pbrIds.Remove(local);
        _legacyFaces.Remove(local);
        _maskedFaces.Remove(local);
        _glowSpheres.Remove(local);
        RemoveLight(local);
        _projectorPrims.Remove(local);
        _balls.Remove(local);
        _inGlowPicture.Remove(local);
        _glowDirty = true;
        ForgetPbrFaces(local, 0);
        ForgetPresented(local);
        _gate.Forget(local);
        _blend.Remove(local);   // its face instances (its own instance is freed here)
        if (_instances.Remove(local, out var inst))
            Rids.Free(inst.Instance);
        _meshObjects.Remove(local);
        _sculptObjects.Remove(local);
        _primObjects.Remove(local);
        _worn.Remove(local);
        _wearerAgent.Remove(local);
        _partWearer.Remove(local);
        ForgetRigged(local);
    }

    /// <summary>
    /// Frees every prim instance and leaves the meshes and materials for <see cref="Clear"/>. Worn rigged meshes draw with the avatar layer's bind-table
    /// and bake textures; freeing a texture a live instance still draws with makes the engine rebuild that instance's uniform set on the freed texture
    /// and log an error for each. The presenter calls this before the avatar layer is disposed.
    /// </summary>
    public void FreeInstances()
    {
        foreach (var rid in TakeInstances()) Rids.Free(rid);
    }

    /// <summary>
    /// Every engine instance the layer draws with, handed over and forgotten by the layer: the prims' own instances, the blended faces' separate
    /// instances and the merged cells' instances. Meshes, materials and textures stay for <see cref="Clear"/>: the engine wants everything that
    /// draws with a resource gone before the resource is (instances, then meshes and materials, then textures). The caller frees the list, all at
    /// once (<see cref="FreeInstances"/>) or a slice a frame (a dropped neighbour region).
    /// </summary>
    public List<Rid> TakeInstances()
    {
        var all = new List<Rid>(_instances.Count + _blend.FaceInstances + _cells.Count);
        foreach (var i in _instances.Values) all.Add(i.Instance);
        _instances.Clear();
        _blend.TakeFaceInstances(all);
        TakeMergedInstances(all);
        return all;
    }

    /// <summary>Leaves the hub (the region this layer draws is gone for good): after <see cref="Clear"/>, so the layer's users and textures are already let go.</summary>
    public void Detach()
    {
        _meshFeed?.Leave();
        _materialFeed?.Leave();
        Hub.Detach(this);
    }

    private ArrivalFeed? _meshFeed, _materialFeed;
    private MaterialSource? _materialFeedOf;

    /// <summary>Frees every instance, mesh and material (region change, shutdown).</summary>
    public void Clear()
    {
        _glowSpheres.Clear();
        _moving.Clear();
        _carriedXf.Clear();
        _lights.Clear();   // before the pictures: a light must not name a freed texture
        foreach (var p in _projectorPictures.Values) p.Picture.Dispose();
        _projectorPictures.Clear();
        DisposeRetiredPictures();
        _balls.Clear();
        _inGlowPicture.Clear();
        _projectorPrims.Clear();
        _glowDirty = true;
        FreeMerged();
        _primFaces.Clear();
        _blend.Clear();
        _surfaceBounds.Clear();
        _blendMaterials.Clear();
        _maskMaterials.Clear();
        _maskedFaces.Clear();
        _maskDependents.Clear();
        _blendWaterKnown = false;
        foreach (var i in _instances.Values) Rids.Free(i.Instance);
        _instances.Clear();
        foreach (var m in _meshes.Values) Rids.Free(m.Mesh);
        _meshes.Clear();
        foreach (var m in _meshAssets.Values) Rids.Free(m.Mesh);
        _meshAssets.Clear();
        MeshBytesHeld = 0;
        _meshObjects.Clear();
        _worn.Clear();
        _held.Clear();
        _awaiting.Clear();
        _wearerAgent.Clear();
        _partWearer.Clear();
        _riggedWorn.Clear();
        _riggedReported.Clear();
        _riggedDrawnReported.Clear();
        _awaitingTable.Clear();
        _riggedWearers.Clear();
        foreach (var local in _rigged.Keys.ToList()) ForgetRigged(local);
        foreach (var m in _riggedMeshes.Values) Rids.Free(m.Mesh);
        _riggedMeshes.Clear();
        foreach (var m in _skinTexMaterials.Values) m.Dispose();
        _skinTexMaterials.Clear();
        foreach (var m in _skinLegacyMaterials.Values) m.Dispose();
        _skinLegacyMaterials.Clear();
        foreach (var m in _skinColourMaterials.Values) m.Dispose();
        _skinColourMaterials.Clear();
        _white?.Dispose();
        _white = null;
        foreach (var m in _sculptMeshes.Values) Rids.Free(m);
        _sculptMeshes.Clear();
        _sculptObjects.Clear();
        _primObjects.Clear();
        SculptPlaceholders = SkippedSculptEmpty = 0;
        _waiting.Clear();
        _preparer?.Clear();   // nothing asked for or prepared outlives its region
        _riggedPreparer?.Clear();
        _riggedWaiting.Clear();
        _riggedHandoverPending.Clear();
        foreach (var h in _riggedHandovers.Values.Where(h => !h.Done)) Rids.Free(h.Mesh);
        _riggedHandovers.Clear();
        foreach (var h in _lodHandovers.Values) Rids.Free(h.Mesh);
        _lodHandovers.Clear();
        // a region change or dispose lets every decoded geometry go (with the bound; without it, kept as before)
        _keeper.Clear();
        if (DecodedBound) _meshSource?.ReleaseAll();
        _lodWaiting.Clear();
        _handoverPending.Clear();
        SkippedMeshUnavailable = 0;
        foreach (var m in _materials.Values) m.Dispose();
        _materials.Clear();
        foreach (var m in _texMaterials.Values) m.Dispose();
        _texMaterials.Clear();
        foreach (var m in _pbrMaterials.Values) m.Dispose();
        _pbrMaterials.Clear();
        foreach (var m in _legacyMaterials.Values) m.Dispose();
        _legacyMaterials.Clear();
        _waitingLegacy.Clear();
        _legacyFaces.Clear();
        _resolved.Clear();
        _waitingMat.Clear();
        _pbrFaces.Clear();
        _pbrIds.Clear();
        MaterialAssets?.Uses.Release(this);
        _pbrSwitched.Clear();
        _fallbackFaces.Clear();
        _pbrFacesWaiting.Clear();
        bool soleLayer = Hub.Layers.Count <= 1;   // the only layer on its hub frees every slot; with others, only what no other region uses goes
        if (soleLayer)
        {
            if (LeakTextureForNegativeControl && _gpuTextures.Count > 0) LeakedForNegativeControl.Add(_gpuTextures.Values.First());
            foreach (var t in _gpuTextures.Values)
                if (!LeakedForNegativeControl.Contains(t)) t.Dispose();
            _gpuTextures.Clear();
        }
        _placeholder?.Dispose();
        _placeholder = null;
        _black?.Dispose();
        _black = null;
        _waitingTex.Clear();
        _waitingGrant.Clear();
        _texUserFaces.Clear();
        if (soleLayer)
        {
            _alpha.Clear();
            _alphaMasks.Clear();
            _reloading.Clear();
            _reupload.Clear();
            _requested.Clear();
            Residency?.Clear();   // no residency state outlives its region
        }
        else if (Residency is not null)
            foreach (var a in Residency.DropSpace(_space, Now))   // what no other region uses is evicted; the rest stays as it is
                if (a.Kind == ResidencyActionKind.Evict) EvictTexture(a.Texture);
        _dirty.Clear();
        _skipped.Clear();
        SkippedAttachments = SkippedNoShape = SkippedSculptOrMesh = 0;
    }

    /// <summary>The level of detail each drawn rigged mesh is at.</summary>
    private IEnumerable<int> RiggedLods() => _rigged.Keys.Where(l => _instances.ContainsKey(l)).Select(l => _instances[l].Lod);

    public object Describe() => new
    {
        instances = Instances, meshes = Meshes, materials = Materials, dirty = Dirty, live_resources = LiveResources,
        skipped_sculpt_or_mesh = SkippedSculptOrMesh, skipped_no_shape = SkippedNoShape, skipped_attachments = SkippedAttachments,
        attached_objects = AttachedObjects, skipped_rigged_attachments = SkippedRiggedAttachments, rigged_wearers = RiggedWearers,
        rigged_objects = RiggedObjects, rigged_meshes = RiggedMeshes, skinned_materials = _skinTexMaterials.Count + _skinLegacyMaterials.Count + _skinColourMaterials.Count, rigged_shown = _rigged.Values.Count(r => r.Shown),
        rigged_lod_min = RiggedLods().DefaultIfEmpty(-1).Min(), rigged_lod_max = RiggedLods().DefaultIfEmpty(-1).Max(),
        mesh_objects = MeshObjects, mesh_resources = MeshAssetResources, meshes_waiting = MeshesWaiting, skipped_mesh_unavailable = SkippedMeshUnavailable,
        rigged_lods_preparing = _riggedPreparer?.InFlight ?? 0, mesh_lods_preparing = _preparer?.InFlight ?? 0, mesh_lods_prepared_held = _preparer?.Held ?? 0, mesh_lods_prepared_dropped = _preparer?.Dropped ?? 0,
        mesh_bytes = MeshBytesHeld, mesh_lods_released = MeshLodsReleased, mesh_lods_rebuilt = MeshLodsRebuilt, mesh_lod_thrash = MeshLodThrash,
        mesh_lods = MeshLods.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray(),
        gpu_textures = GpuTextures, textured_materials = TexturedMaterials, textures_waiting = TexturesWaiting,
        auto_masked_faces = AutoMaskedFaces, legacy_material_faces = LegacyMaterialFaces, legacy_materials = LegacyMaterialResources, legacy_materials_waiting = LegacyMaterialsWaiting,
        pbr_faces = PbrFaces, pbr_faces_not_opaque = PbrFacesNotOpaque, pbr_faces_beyond_geometry = PbrFacesBeyondGeometry, pbr_materials = PbrMaterials, materials_waiting = MaterialsWaiting,
        material_assets_ready = MaterialAssets?.Ready ?? 0, material_assets_unavailable = MaterialAssets?.Unavailable ?? 0,
        sculpt_objects = SculptObjects, sculpt_resources = SculptResources, sculpt_placeholders = SculptPlaceholders, skipped_sculpt_empty = SkippedSculptEmpty,
        sculpt_lods = SculptLods,
        blend_objects = BlendObjects, blend_places = BlendPlaces, blend_face_instances = BlendFaceInstances,
        blend_part_meshes = BlendPartMeshes, blend_parts_made = BlendPartsMade, blend_part_bytes = BlendPartBytes, blend_whole_mesh_fallbacks = BlendWholeMeshFallbacks,
        residency = Residency is not { } r ? null : new
        {
            budget_bytes = r.BudgetBytes, resident_bytes = r.ResidentBytes, textures = r.Textures, resident = r.Resident, loading = r.Loading,
            on_placeholders = r.OnPlaceholders, evictions = r.Evictions, reloads = r.Reloads, thrash = r.Thrash, users = r.Users,
            steps_down = r.StepsDown, steps_up = r.StepsUp, refining = r.Refining,
            // the view against its desired levels
            visible = r.All.Count(r.IsVisible), visible_at_desired = r.All.Where(r.IsVisible).Count(r.AtDesired),
            visible_placeholders = r.All.Count(id => r.IsVisible(id) && r.StateOf(id) != Viewer.Core.Residency.ResidencyState.Resident && !r.HasFailed(id)),
            failed = r.All.Count(r.HasFailed),
            priority_ms = System.Math.Round(r.LastPriorityMs, 4),
        },
    };
}
