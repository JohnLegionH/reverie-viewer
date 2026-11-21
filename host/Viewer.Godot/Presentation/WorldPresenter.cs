// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// Presents one session's world. Subscribes to <see cref="WorldState.Changed"/> (network threads) through a
/// <see cref="PresentationCoordinator"/>, and on the main thread, within the per-frame budget, turns terrain patches into
/// dirty chunks and rebuilds them from the core's TerrainStore (<see cref="WorldState.TryGetPatch"/>). A region reset (a
/// new epoch) clears every chunk first, so no engine resource outlives its region. Objects are counted, not drawn, until
/// Owned by a node that calls <see cref="Frame"/> from its _Process; <see cref="Dispose"/> detaches and frees.
/// </summary>
public sealed class WorldPresenter : IPresentationSink, System.IDisposable
{
    private readonly WorldState _world;
    private readonly ITerrainView _view;
    private readonly SortedSet<(int Y, int X)> _dirty = new();
    private TerrainGrid? _grid;
    private (int X, int Y) _gridSize;
    private int _epoch = -1;

    public PresentationCoordinator Coordinator { get; }

    /// <summary>The main-thread time one frame of presentation work may take, shared by the queued changes and prim steps, the avatars' arrivals,
    /// bakes and shapes, and their posing (each takes what the ones before it left).</summary>
    public FrameBudget Budget { get; }

    /// <summary>The share of the frame's limit the shared budget plans for: one unit of work can run over its estimate, the work after the budget (attached objects, the disposal drain) is not counted in it, and the limit is not to be passed.</summary>
    public const double PlannedShare = 0.6;

    /// <summary>Test only (the arrival check's control, --tr-alter=nobudget): every presenter made from now on works without the shared budget, as before it existed.
    /// False in the viewer.</summary>
    public static bool NoSharedBudget { get; set; }
    public ITerrainView View => _view;
    public int PatchesPresented { get; private set; }
    public int ChunksBuilt { get; private set; }

    /// <summary>Milliseconds from the region's start (this presenter made, or its last
    /// region reset) to the last time every chunk was handed over (nothing dirty left); null until then.</summary>
    public double? TerrainCompleteMs { get; private set; }
    private long _regionStart = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Terrain chunks are built and encoded off the main thread (views that take prepared
    /// surfaces; the harness's node view does not). Null when chunks are built here.</summary>
    public TerrainPreparer? TerrainPrep { get; }
    /// <summary>Each snapshot's cost on the main thread, in ms (the first 8192).</summary>
    public List<double> SnapshotMs { get; } = new();
    /// <summary>Test only (the render harness's --tr-terrain-main-thread, the negative control of --tr-terrainprep): every
    /// presenter made from now on builds its chunks on the main thread, as before. False in the viewer.</summary>
    public static bool TerrainOnMainThread { get; set; }
    /// <summary>Test only (--tr-terrainprep): each chunk handed to the engine, as handed (null surface: removed).</summary>
    public System.Action<int, int, PreparedSurface?>? OnTerrainHandOver { get; set; }
    /// <summary>A dirty chunk's preparation starts only once its patches have settled
    /// (or it has waited the cap). Fed on both paths (its change log is a measurement); consulted only by the preparer's.</summary>
    public TerrainSettle Settle { get; } = new(SettleWindowMs ?? TerrainSettle.DefaultWindowMs);
    /// <summary>Test only (the render harness's --tr-terrain-settle-ms): the settle window of every presenter made from
    /// now on (0: every chunk starts at once, the earlier behaviour). Null in the viewer.</summary>
    public static double? SettleWindowMs { get; set; }
    /// <summary>Chunks being prepared (asked for and not handed over yet).</summary>
    public int TerrainInFlight => TerrainPrep?.InFlight ?? 0;
    public int DirtyChunks => _dirty.Count;

    /// <summary>The prims, when the presenter was given a layer for them.</summary>
    public PrimLayer? Prims { get; }

    /// <summary>The region's avatars, when the presenter was given a presenter for them.</summary>
    public AvatarPresenter? Avatars { get; }

    // the region's terrain detail textures, fetched without blocking through the session's texture path.
    private readonly TextureSource? _terrainTextures;
    private Viewer.Core.World.RegionTerrain? _terrainFor;
    private Texture2D[]? _details;

    /// <summary>The grid cell the detail textures' pattern is anchored on, when the region is drawn away from its own corner (a neighbour:
    /// the anchor region's cell); null: the region's own.</summary>
    public (int X, int Y)? DetailGridOverride { get; init; }

    /// <summary>Where each detail layer came from ("region", "default (unset)", "default (unavailable)"); null until ready.</summary>
    public string[]? DetailSources { get; private set; }
    /// <summary>The pixel size of each detail layer as drawn ("512x512"), and the pattern offset (fraction of a repeat) the shader uses; null until ready.</summary>
    public string[]? DetailSizes { get; private set; }
    public Vector2 DetailOffsetUsed { get; private set; }
    /// <summary>The region has terrain settings whose detail textures are still being fetched.</summary>
    public bool TerrainPending => _world.Region?.Terrain is not null && _details is null;

    // the region's water and environment (sky, sun, ambient, haze), when the region's are known.
    private readonly EnvironmentLayer? _environment;
    private double? _cloudClockAt;
    private System.Numerics.Vector2 _cloudScroll;

    /// <summary>A fixed cloud scroll (seconds at the frame's scroll rate) for a test render; null: the scroll follows the clock.</summary>
    public double? CloudScrollSeconds { get; init; }
    private string? _envLlsdFor;
    private string? _parcelLlsdFor;   // the parcel environment answer the environment in force was made from
    private float? _waterFor;
    private RegionEnvironment? _env;

    /// <summary>The day cycle the sky is taken from now (the parcel's, the region's or the default), null before the first frame (a development readout).</summary>
    public RegionEnvironment? CurrentEnvironment => _env;
    private double _appliedFraction = double.NaN;

    /// <summary>Test renders fix the time of day (deterministic goldens); null follows the region's own day cycle.</summary>
    public double? DayFractionOverride { get; set; }

    private SkyChoice _sky = SkyChoice.Region;
    /// <summary>The viewer user's time-of-day choice (Region follows the region's clock). A test render's
    /// <see cref="DayFractionOverride"/> wins over it.</summary>
    public SkyChoice Sky
    {
        get => _sky;
        set
        {
            if (_sky == value) return;
            _sky = value;
            _appliedFraction = double.NaN;   // applied again on the next frame
        }
    }

    /// <summary>
    /// The built-in skies loaded as settings assets. While it holds the frame of the choice in force, that frame is the sky (the
    /// official viewer's fixed environment); a choice it does not hold (no grid to fetch from: test renders, replays) shows the
    /// region's own day cycle at <see cref="SkyChoices.Fraction"/> instead.
    /// </summary>
    public FixedSkyStore? FixedSkies { get; set; }

    /// <summary>Draws the sky again on the next frame (a built-in sky just finished loading).</summary>
    public void RefreshSky() => _appliedFraction = double.NaN;

    /// <summary>The clouds keep their place instead of drifting (World > Environment > Pause Clouds).</summary>
    public bool CloudsPaused { get; set; }
    private PersonalLighting? _personal;
    /// <summary>The Personal Lighting window's settings, laid over the sky frame in force; null for none. Set to null when this presentation ends.</summary>
    public PersonalLighting? Personal
    {
        get => _personal;
        set
        {
            if (ReferenceEquals(_personal, value)) return;
            if (_personal is not null) _personal.Changed -= PersonalChanged;
            _personal = value;
            if (value is not null) value.Changed += PersonalChanged;
            PersonalChanged();
        }
    }
    private void PersonalChanged() => _appliedFraction = double.NaN;   // applied again on the next frame

    /// <summary>Where the environment came from ("region", "default:..."), and the day fraction last applied.</summary>
    public string? EnvironmentSource { get; private set; }
    public double? DayFractionApplied => double.IsNaN(_appliedFraction) ? null : _appliedFraction;
    public EnvironmentLayer? Environment => _environment;

    public WorldPresenter(WorldState world, ITerrainView view, double budgetMs = 4.0, PrimLayer? prims = null, TextureSource? terrainTextures = null, EnvironmentLayer? environment = null,
        AvatarPresenter? avatars = null)
    {
        Avatars = avatars;
        _world = world;
        _view = view;
        _terrainTextures = terrainTextures;
        _environment = environment;
        TerrainGrid.MainThreadId = System.Environment.CurrentManagedThreadId;   // made on the main thread
        if (view.TakesPrepared && !TerrainOnMainThread) TerrainPrep = new TerrainPreparer();
        Prims = prims;
        if (Prims is not null) Prims.Environment = environment;   // the water's place among blended faces
        if (Prims is not null && avatars is not null)
        {
            Prims.Attachments = avatars;   // what avatars wear follows their joints
            avatars.Scene.Carried = Prims.Moving;   // an avatar sitting on a moving object goes where the object is drawn
            avatars.LayerMade = Prims.RetryRiggedAttachments;   // rigged meshes found worn before the body existed are presented again once it does
            var primLayer = Prims;
            avatars.WornHeld = agent => primLayer.HoldsBurstOf(agent);   // a body part stays hidden while the pieces that covered it are still drawn
        }
        Prims?.MarkAll();
        Coordinator = new PresentationCoordinator(this, budgetMs) { Shared = Budget = new FrameBudget(budgetMs * PlannedShare) };
        if (Avatars is not null)
        {
            // an avatar's arrival and movement are taken at once, not behind the region's objects queued before them: the agent crossing into a
            // region with a hundred thousand objects would otherwise have no avatar to steer, follow or name for as long as those take
            // what an avatar wears is taken at once too: an object worn is marked for presentation (the prim layer takes it before any other)
            Coordinator.IsUrgent = c => PresentationPolicy.IsUrgent(c, Prims is not null, id => _world.Attachment(id) is not null);
            Coordinator.ApplyUrgent = c => PresentationPolicy.ApplyUrgent(c, x => Prims?.Apply(x), Avatars.Apply, Avatars.NoteWornChange);
        }
        if (NoSharedBudget) Coordinator.Shared = null;
        else
        {
            // land is presented ahead of the region's objects, and the queue leaves part of every frame to the deferred work
            PresentationPolicy.Configure(Coordinator);
            Coordinator.ApplyPrompt = ApplyTerrain;
        }
        if (Prims is not null && !NoSharedBudget) Prims.Budget = Budget;   // surfaces handed to the engine count against the same frame
        _world.Changed += Coordinator.Enqueue;
        if (Avatars is not null) Avatars.TakeChanges = () => TakeChanges();   // the body host takes every region's changes before it places a body
        // Whatever arrived before the presentation attached (a replay, or a login burst) is presented as one catch-up.
        MarkAllDirty("catch-up");
        // The avatars the world holds already (the user's own comes with the arrival) raised their changes before anyone listened: give
        // them to the avatars again, or the own avatar is never placed, steered or followed by the camera.
        if (Avatars is not null)
            foreach (var change in PresentationPolicy.CatchUp(world.Snapshot())) Coordinator.Enqueue(change);
    }

    private long _budgetFrame = -1, _changesFrame = -1;
    private DrainStats _changes;

    /// <summary>Starts the frame's shared budget, once per engine frame (the region's changes may be taken before its own frame runs).</summary>
    private void StartBudget()
    {
        long frame = (long)Engine.GetProcessFrames();
        if (frame == _budgetFrame) return;
        _budgetFrame = frame;
        Budget.Start();
    }

    /// <summary>
    /// Takes what the region has sent, once per engine frame: its queued changes, then the objects that move or spin between its updates carried to where
    /// they are now (before those sitting on them are placed). The body host runs it for every region that shares the avatars' bodies before any body is
    /// placed (<see cref="Viewer.Shell.SharedBodyFrame"/>), as the official viewer takes every update of a frame before it moves anything.
    /// </summary>
    internal DrainStats TakeChanges()
    {
        long frame = (long)Engine.GetProcessFrames();
        if (frame == _changesFrame) return _changes;
        _changesFrame = frame;
        StartBudget();
        _changes = Coordinator.RunFrame();
        Prims?.UpdateMoving();
        return _changes;
    }

    /// <summary>Main thread, once per frame (the owning node's _Process).</summary>
    public DrainStats Frame()
    {
        StartBudget();
        using var whole = FrameProbe.Begin(FrameProbe.WpFrame);
        using (FrameProbe.Begin(FrameProbe.WpDetails)) UpdateDetails();
        using (FrameProbe.Begin(FrameProbe.WpEnvironment)) UpdateEnvironment();
        using (FrameProbe.Begin(FrameProbe.PrimResidency)) Prims?.UpdateResidency();   // evictions and load grants; uploads run inside the frame budget below
        var stats = TakeChanges();
        Avatars?.Frame(NoSharedBudget ? null : Budget);   // after the changes: arrivals placed and posed in the frame they are known
        long after0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using (FrameProbe.Begin(FrameProbe.PrimAttached)) Prims?.UpdateAttached();   // what they wear goes with their joints
        // after the frame's upload budget, outside every timed unit: the Godot values the
        // frame's engine calls used, disposed pinned, a bounded slice (under 1 ms)
        using (FrameProbe.Begin(FrameProbe.WpDisposal)) LastDisposal = VariantDisposal.DrainFrame();
        if (Avatars is not null)   // the avatars keep this much of the next frame for what comes after them
            Avatars.AfterWorkMs += ((System.Diagnostics.Stopwatch.GetTimestamp() - after0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency - Avatars.AfterWorkMs) * 0.2;
        return stats;
    }

    /// <summary>What the last frame's disposal drain did.</summary>
    public DisposalDrain LastDisposal { get; private set; }

    /// <summary>The terrain's detail layers count against the texture budget, pinned (always in view).</summary>
    private readonly List<System.Guid> _pinned = new();

    /// <summary>
    /// Once all four detail layers are resolved (fetched and decoded through the guard, or our default
    /// when the region leaves one unset or it cannot be had), hand them to the view and rebuild the chunks with them.
    /// </summary>
    private void UpdateDetails()
    {
        var rt = _world.Region?.Terrain;
        if (!Equals(rt, _terrainFor))
        {
            FreeDetails();
            _terrainFor = rt;
        }
        if (rt is null || _details is not null)
            return;
        var decoded = new Viewer.Core.Imaging.DecodedTexture?[4];
        var sources = new string[4];
        for (int i = 0; i < 4; i++)
        {
            var id = rt.Details[i];
            if (id == System.Guid.Empty || _terrainTextures is null) { sources[i] = id == System.Guid.Empty ? "default (unset)" : "default (no texture source)"; continue; }
            var a = _terrainTextures.TryGet(id, out decoded[i]);
            if (a == MeshAvailability.Pending) return;   // not yet: the next frame asks again
            sources[i] = a == MeshAvailability.Ready ? "region" : "default (unavailable)";
        }
        var layers = new Texture2D[4];
        for (int i = 0; i < 4; i++)
        {
            var d = decoded[i];
            byte[] px = d?.Rgba ?? TerrainDetails.Pixels(i);
            int w = d?.Width ?? TerrainDetails.Size, h = d?.Height ?? TerrainDetails.Size;
            var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, px);
            img.GenerateMipmaps();
            layers[i] = ImageTexture.CreateFromImage(img);
            img.Dispose();
            if (Prims?.Residency is { } res)
            {
                var key = d is not null ? rt.Details[i] : new System.Guid(i + 1, 0x7e77, 0x0a1e, [0, 0, 0, 0, 0, 0, 0, 0]);   // our default layer i
                res.Pin(key, Viewer.Core.Residency.TextureFootprint.Bytes(w, h, true));
                _pinned.Add(key);
            }
        }
        var region = _world.Region!;
        // The detail pattern repeats in absolute grid metres. The shader reads each vertex's place in the scene, so a region drawn at an
        // offset from the anchor region starts the pattern from the anchor region's corner, not its own.
        var (gridX, gridY) = DetailGridOverride ?? (region.GridX, region.GridY);
        var (offX, offY) = Viewer.Core.World.TerrainComposition.DetailOffset(gridX, gridY);
        DetailOffsetUsed = new Vector2(offX, offY);
        _view.SetDetailTextures(layers, DetailOffsetUsed);
        _details = layers;
        DetailSizes = decoded.Select((d, i) => d is null ? $"{TerrainDetails.Size}x{TerrainDetails.Size} built-in" : $"{d.Width}x{d.Height} level {d.Level} of {d.LevelCount}, full {d.FullWidth}x{d.FullHeight}").ToArray();
        DetailSources = sources;
        MarkAllDirty("details");
    }

    private IReadOnlyList<Viewer.Core.World.WaterRect> _neighbourWater = [];
    private bool _waterPlanChanged;

    /// <summary>The other regions in view with their own water heights, in this region's frame: the water is drawn at each region's height over its own extent.</summary>
    public IReadOnlyList<Viewer.Core.World.WaterRect> NeighbourWater
    {
        get => _neighbourWater;
        set
        {
            if (System.Linq.Enumerable.SequenceEqual(value, _neighbourWater)) return;
            _neighbourWater = value.ToArray();
            _waterPlanChanged = true;
        }
    }

    /// <summary>
    /// When the region's water height or environment is known, show them (Docs/ENVIRONMENT-SPEC.md): the region's
    /// EEP environment, or the default when it serves none or cannot be read; the sky at the day fraction (fixed in test
    /// renders), re-applied when the fraction moves. Regions with neither (older captures) keep the plain stage.
    /// </summary>
    private void UpdateEnvironment()
    {
        if (_environment is null) return;
        var r = _world.Region;
        if (r is null || (r.WaterHeight is null && r.EnvironmentLlsd is null))
        {
            if (_environment.Frame is not null) _environment.Clear();
            _envLlsdFor = null; _parcelLlsdFor = null; _env = null; _appliedFraction = double.NaN;
            return;
        }
        bool changed = false;
        if (_env is null || !ReferenceEquals(_envLlsdFor, r.EnvironmentLlsd) || !ReferenceEquals(_parcelLlsdFor, r.ParcelEnvironmentLlsd))
        {
            _envLlsdFor = r.EnvironmentLlsd;
            _parcelLlsdFor = r.ParcelEnvironmentLlsd;
            string? why = r.EnvironmentMissing;
            var regionEnv = r.EnvironmentLlsd is { } llsd ? RegionEnvironment.FromLlsd(llsd, out why) : null;
            // the parcel's own environment, when its answer holds a day cycle, wins over the region's while the agent is in the parcel
            var parcelEnv = r.ParcelEnvironmentLlsd is { } parcelLlsd ? RegionEnvironment.FromLlsd(parcelLlsd, out _) : null;
            _env = parcelEnv ?? regionEnv;
            EnvironmentSource = parcelEnv is not null ? "parcel" : regionEnv is not null ? "region" : "default: " + (why ?? "none served");
            _env ??= RegionEnvironment.Default;
            changed = true;
        }
        if (r.WaterHeight != _waterFor) { _waterFor = r.WaterHeight; changed = true; }
        if (_waterPlanChanged) { _waterPlanChanged = false; changed = true; }
        if (_environment.TakeCloudsChanged()) changed = true;   // the clouds were switched on or off
        double now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        double f;
        SkyFrame frame;
        if (DayFractionOverride is { } fixedFraction) { f = fixedFraction; frame = _env.SkyAt(f); }
        else if (_sky != SkyChoice.Region && FixedSkies is { } fixedSkies && fixedSkies.TryGet(_sky, out var loadedSky) && loadedSky is not null) { f = _env.DayFraction(now); frame = loadedSky; }
        else frame = _env.SkyFor(_sky, now, out f);
        if (_personal is { IsActive: true } p) frame = p.Apply(frame);   // the Personal Lighting window's settings, over the frame in force
        _environment.SceneGamma = _personal?.Gamma ?? 1f;
        if (_environment.CloudsEnabled)
        {
            // the clouds move every frame: a fixed scroll for a test, else the scroll accumulates the frame's rate over the real time passed
            if (CloudScrollSeconds is { } fixedSeconds) _environment.SetCloudScroll(Viewer.Core.World.CloudScroll.Advance(default, frame.Cloud.ScrollRate, fixedSeconds));
            else
            {
                double dt = _cloudClockAt is { } last ? now - last : 0;
                _cloudClockAt = now;
                if (!CloudsPaused)
                    _cloudScroll = frame.Cloud.ScrollRate == System.Numerics.Vector2.Zero ? default : Viewer.Core.World.CloudScroll.Advance(_cloudScroll, frame.Cloud.ScrollRate, dt);
                _environment.SetCloudScroll(_cloudScroll);
            }
        }
        if (UpdateWaterNormalMap()) changed = true;
        if (UpdateSunTexture(frame)) changed = true;
        if (DayFractionOverride is null) _environment.SetWaterClock((float)(f * _env.DayLength));   // the ripples move every frame, not only when the sky is drawn again
        _environment.TickProbe((_view as ServerTerrainView)?.GlowVersion ?? 0);   // the sky probe's capture of the terrain and water, a face a frame
        if (!changed && !double.IsNaN(_appliedFraction) && System.Math.Abs(f - _appliedFraction) < 1.0 / 2000)
            return;
        _appliedFraction = f;
        _environment.NeighbourWater = _neighbourWater;
        _environment.Apply(_env, frame, r.WaterHeight, r.SizeX, r.SizeY, (float)(f * _env.DayLength));
    }

    private System.Guid _waterMapTried;

    /// <summary>What became of the water's normal map: waiting for it, in use, or the viewer-made one in use because the grid did not serve it.</summary>
    public string WaterMapStatus { get; private set; } = "not asked yet";

    /// <summary>
    /// Fetches the normal map the region's water settings name (the official default when they name none) and hands it to the water.
    /// The viewer-made map stays until it arrives, and for good if the grid cannot serve it. True when the water got a new map.
    /// </summary>
    private bool UpdateWaterNormalMap()
    {
        if (_environment is null || _terrainTextures is null || _env is null) return false;
        var id = _env.Water.NormalMap;
        if (id == _waterMapTried) return false;
        var a = _terrainTextures.TryGet(id, out var d);
        if (a == MeshAvailability.Pending) { WaterMapStatus = $"waiting for {id}"; return false; }   // not yet: the next frame asks again
        _waterMapTried = id;
        if (a != MeshAvailability.Ready || d is null) { WaterMapStatus = $"viewer-made map in use: {id} was not served ({a})"; return false; }
        WaterMapStatus = $"in use: {id}, {d.Width}x{d.Height}";
        _environment.SetWaterNormalMap(id, d.Rgba, d.Width, d.Height);
        return true;
    }

    private System.Guid _sunTried;

    /// <summary>What became of the sun's picture: waiting for it, in use, or the procedural disc in use because the grid did not serve it.</summary>
    public string SunTextureStatus { get; private set; } = "not asked yet";

    /// <summary>
    /// Fetches the picture the sky names for its sun (the official default sun picture when it names none) and hands it to the sky, which draws the sun's disc from it.
    /// The procedural disc stays until it arrives, and for good if the grid cannot serve it. True when the sky got a new picture.
    /// </summary>
    private bool UpdateSunTexture(Viewer.Core.World.SkyFrame frame)
    {
        if (_environment is null || _terrainTextures is null) return false;
        var id = Viewer.Core.World.SunDisc.TextureOf(frame);
        if (id == _sunTried) return false;
        var a = _terrainTextures.TryGet(id, out var d);
        if (a == MeshAvailability.Pending) { SunTextureStatus = $"waiting for {id}"; return false; }   // not yet: the next frame asks again
        _sunTried = id;
        if (a != MeshAvailability.Ready || d is null) { _environment.ClearSunTexture(); SunTextureStatus = $"procedural disc in use: {id} was not served ({a})"; return true; }
        SunTextureStatus = $"in use: {id}, {d.Width}x{d.Height}";
        _environment.SetSunTexture(id, d.Rgba, d.Width, d.Height);
        return true;
    }

    /// <summary>The engine objects the presentation made for its region, by kind: the terrain's detail textures and
    /// material, and the prim layer's (the lifecycle check names any that outlive a region change).</summary>
    internal IEnumerable<(string Kind, GodotObject Object)> EngineObjects()
    {
        if (_details is not null)
            foreach (var t in _details) yield return ("detail texture", t);
        if (_view.DetailMaterial is { } m) yield return ("terrain material", m);
        if (Prims is not null)
            foreach (var o in Prims.EngineObjects()) yield return o;
        if (Avatars is not null)
            foreach (var o in Avatars.EngineObjects()) yield return o;
    }

    private void FreeDetails()
    {
        if (_details is null) return;
        foreach (var k in _pinned) Prims?.Residency?.Unpin(k);
        _pinned.Clear();
        _view.SetDetailTextures(null);
        foreach (var t in _details) t.Dispose();
        _details = null;
        DetailSources = null;
        DetailSizes = null;
    }

    /// <summary>Runs frames until nothing is queued or dirty (test-render and measurements; never in the live loop).</summary>
    public void Flush()
    {
        double budget = Coordinator.BudgetMs;
        Coordinator.BudgetMs = double.MaxValue;
        var sharedBudget = Prims?.Budget;
        if (Prims is not null) Prims.Budget = null;   // no frame to share here: a surface the budget refused would never be handed over
        UpdateDetails();
        UpdateEnvironment();
        Prims?.UpdateResidency();
        while (Coordinator.Backlog > 0 || _dirty.Count > 0 || TerrainInFlight > 0 || (Prims?.Dirty ?? 0) > 0)
        {
            var s = Coordinator.RunFrame();
            // nothing to do here but wait for a chunk being prepared
            if (s.Applied + s.Steps == 0 && TerrainInFlight > 0) TerrainPrep!.WaitForArrival(System.TimeSpan.FromSeconds(10));
        }
        Coordinator.BudgetMs = budget;
        if (Prims is not null) Prims.Budget = sharedBudget;
        VariantDisposal.DrainAll("flush");   // test renders and measurements only; no frame budget here
    }

    public void Apply(WorldChange change)
    {
        using (FrameProbe.Begin(FrameProbe.WpApply))
        {
            Prims?.Apply(change);
            if (!Coordinator.AppliedEarly)   // the fast lane applied it already; applying an avatar state a second time would move the avatar back
                using (FrameProbe.Begin(FrameProbe.AvApply)) Avatars?.Apply(change);
        }
        if (change.Kind == WorldChangeKind.RegionReset)
        {
            _environment?.Clear();
            _envLlsdFor = null; _env = null; _waterFor = null; _appliedFraction = double.NaN;
            ApplyTerrain(change);   // already done when the prompt lane took the reset first
            VariantDisposal.DrainAll("region");   // a region change leaves nothing held
            return;
        }
        if (Coordinator.ApplyPrompt is not null)
            return;   // the prompt lane applies the terrain patches
        ApplyTerrain(change);
    }

    /// <summary>The terrain half of a change: a region reset clears the chunks (once per region: a reset the prompt lane took is seen again in the
    /// queue), a patch marks its chunk dirty. Cheap bookkeeping, so it can run ahead of the region's objects.</summary>
    private void ApplyTerrain(WorldChange change)
    {
        if (change.Kind == WorldChangeKind.RegionReset)
        {
            if (change.Id.Epoch <= _epoch)
                return;
            TerrainPrep?.Clear();   // nothing in flight from the old region is ever handed over
            Settle.Clear(newRegion: true);   // a new region starts with no pending chunks
            _view.Clear();
            // the detail textures and their material go after the chunks drawn with them (as in Dispose)
            FreeDetails();
            _terrainFor = null;
            _dirty.Clear();
            _grid = null;
            PatchesPresented = 0;
            _epoch = change.Id.Epoch;
            _regionStart = System.Diagnostics.Stopwatch.GetTimestamp();   // (measurement)
            TerrainCompleteMs = null;
            return;
        }
        if (change.Kind != WorldChangeKind.TerrainPatch || change.Id.Epoch < _epoch)
            return;
        _epoch = change.Id.Epoch;
        if (!EnsureGrid())
            return;
        if (change.PatchX < 0)
        {
            MarkAllDirty("all patches");
            return;
        }
        PatchesPresented++;
        Settle.PatchChanged(change.PatchX, change.PatchY);   // the chunks reading it wait from now
        // every chunk that reads the patch is built again: the ones across its edge hold edge heights and normals taken from it
        foreach (var (cx, cy) in _grid!.ChunksReading(change.PatchX, change.PatchY))
        {
            _dirty.Add((cy, cx));
            TerrainPrep?.Touch((cx, cy));   // a preparation running from its older patches is discarded on arrival
            Settle.Dirty((cx, cy));
        }
    }

    public bool Step()
    {
        if (TerrainPrep is not null)
        {
            // a chunk prepared off the main thread is handed over (one unit), else the free places take the next
            // dirty chunks (their snapshots), and the prims go on meanwhile
            if (_grid is not null && HandOverPrepared()) return true;
            if (_grid is not null) StartPreparations();
            return Prims?.Step() ?? false;
        }
        if (_dirty.Count == 0 || _grid is null)
            return Prims?.Step() ?? false;
        var (cy, cx) = _dirty.Min;
        _dirty.Remove(_dirty.Min);
        // a chunk's build (made from the patches and handed to the view) is one
        // upload unit of kind terrain, timed as prims, sculpts and mesh LODs are, so the unit rules see terrain too
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int gc0 = System.GC.CollectionCount(0);
        var pause0 = System.GC.GetTotalPauseDuration();
        var alloc = AllocationMeter.Shared.Begin(AllocKind.Terrain);   // (measurement only)
        var chunk = _grid.Build(cx, cy);
        if (chunk is null)
            _view.Remove(cx, cy);
        else
            _view.Upload(chunk);
        ChunksBuilt++;
        long allocBytes = alloc.End();
        Prims?.TerrainUnit(t0, gc0, pause0, chunk?.VertexCount ?? 0, allocBytes);
        if (_dirty.Count == 0) TerrainComplete();
        return true;
    }

    /// <summary>
    /// Hands the next chunk prepared off the main thread to the view: the engine calls only (the view's HandOver:
    /// The kept dictionary, the held values disposed after the frame's upload budget), or its removal when none of
    /// its patches is present. One terrain upload unit. A preparation that failed is built here, as before.
    /// </summary>
    private bool HandOverPrepared()
    {
        if (!TerrainPrep!.TryTake(out var r, out var key, out bool failed)) return false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int gc0 = System.GC.CollectionCount(0);
        var pause0 = System.GC.GetTotalPauseDuration();
        var alloc = AllocationMeter.Shared.Begin(AllocKind.Terrain);   // (measurement only)
        int vertices;
        if (failed)
        {
            var chunk = _grid!.Build(key.X, key.Y);
            if (chunk is null) _view.Remove(key.X, key.Y);
            else _view.Upload(chunk);
            vertices = chunk?.VertexCount ?? 0;
        }
        else if (r!.Surface is null)
        {
            _view.Remove(key.X, key.Y);
            vertices = 0;
        }
        else
        {
            _view.HandOver(key.X, key.Y, r.Surface, r.Composed);
            vertices = r.Vertices;
        }
        ChunksBuilt++;
        long allocBytes = alloc.End();
        Prims?.TerrainUnit(t0, gc0, pause0, vertices, allocBytes);
        OnTerrainHandOver?.Invoke(key.X, key.Y, failed ? null : r!.Surface);
        if (_dirty.Count == 0 && TerrainPrep.InFlight == 0) TerrainComplete();
        return true;
    }

    /// <summary>The dirty chunks, in the order they were built before (row by row), take the free places: each one's
    /// snapshot is taken here and its build and encoding handed to a worker. A chunk already being prepared stays dirty
    /// until its result arrives (discarded then as stale, it is prepared again from a new snapshot).</summary>
    private void StartPreparations()
    {
        if (_dirty.Count == 0 || !TerrainPrep!.HasRoom) return;
        List<(int Y, int X)>? started = null;
        bool pending = TerrainPending;   // the detail textures still being fetched
        foreach (var (cy, cx) in _dirty)
        {
            if (!TerrainPrep.HasRoom) break;
            if (TerrainPrep.IsInFlight((cx, cy))) continue;
            if (!Settle.Ready((cx, cy), pending, out bool byCap)) continue;   // its terrain is still arriving
            long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var snapshot = _grid!.Snapshot(cx, cy);
            if (SnapshotMs.Count < 8192) SnapshotMs.Add((System.Diagnostics.Stopwatch.GetTimestamp() - s0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            TerrainPrep.Start((cx, cy), snapshot.Prepare);
            Settle.Start((cx, cy), byCap);
            (started ??= new()).Add((cy, cx));
        }
        if (started is not null)
            foreach (var k in started) _dirty.Remove(k);
    }

    /// <summary>Every chunk is handed over, as of now.</summary>
    private void TerrainComplete() =>
        TerrainCompleteMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _regionStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The grid follows the region's size (a var region grows as patches arrive, then the map block decides).</summary>
    private bool EnsureGrid()
    {
        var region = _world.Region;
        if (region is null)
            return false;
        if (_grid is null || _gridSize != (region.SizeX, region.SizeY))
        {
            _grid = new TerrainGrid(region.SizeX, region.SizeY, 16, _world.TryGetPatch)
            {
                Composition = region.Terrain, OriginX = region.GridX * 256.0, OriginY = region.GridY * 256.0,   // the composition's world origin
                OutsideHeight = (gx, gy) => this.OutsideHeight?.Invoke(gx, gy),   // read when asked: the property is set after the constructor, which may already have made the grid
            };
            _gridSize = (region.SizeX, region.SizeY);
            TerrainPrep?.Clear();   // nothing prepared for the old grid is handed over
            _view.Clear();
            MarkAllDirty("grid");
        }
        return true;
    }

    /// <summary>The heights of the ground just outside this region, from the regions that lie there (null: none are known, the region's
    /// own edge is used). Set when the presenter is made; the grid it builds reads it.</summary>
    public System.Func<int, int, float?>? OutsideHeight { get; init; }

    /// <summary>Every chunk along the region's four edges is built again: a neighbour's edge heights changed, and the border vertices and
    /// normals read them.</summary>
    public void MarkEdgesDirty()
    {
        if (!EnsureGrid())
            return;
        int cx1 = _grid!.ChunksX - 1, cy1 = _grid.ChunksY - 1;
        for (int y = 0; y <= cy1; y++)
            for (int x = 0; x <= cx1; x++)
            {
                if (x != 0 && y != 0 && x != cx1 && y != cy1)
                    continue;
                _dirty.Add((y, x));
                TerrainPrep?.Touch((x, y));
                Settle.Dirty((x, y));
            }
    }

    private void MarkAllDirty(string cause)
    {
        if (!EnsureGrid())   // a size change re-enters here once, with the new grid in place
            return;
        Settle.AllDirty(cause);   // why every chunk is dirty again, and when
        for (int y = 0; y < _grid!.ChunksY; y++)
            for (int x = 0; x < _grid.ChunksX; x++)
            {
                _dirty.Add((y, x));
                TerrainPrep?.Touch((x, y));   // a preparation from older patches is discarded
                Settle.Dirty((x, y));   // held only by the patch changes it has seen
            }
    }

    /// <summary>No more changes from the world are queued (the region is being let go of).</summary>
    public void StopListening() => _world.Changed -= Coordinator.Enqueue;

    public void Dispose()
    {
        _world.Changed -= Coordinator.Enqueue;
        // Every step runs even if an earlier one throws (a skipped free leaves engine resources live at quit, and the
        // engine corrupts its heap then); the exceptions are reported and raised together at the end.
        TeardownSteps.RunAll(msg => Main.Log.Warn("teardown.step-failed", CoreLog.F("detail", msg)), (name, ms) => Main.Log.Info("teardown.step-slow", CoreLog.F("step", name), CoreLog.F("ms", Math.Round(ms, 1))),
            ("terrain preparation", () => TerrainPrep?.Dispose()),   // nothing in flight is handed over after this
            ("terrain view", _view.Clear),
            ("terrain details", FreeDetails),
            ("environment", () => _environment?.Clear()),
            ("prim instances", () => Prims?.FreeInstances()),   // before the avatars: worn meshes draw with the avatars' textures
            ("avatars", () => Avatars?.Dispose()),   // before the prims: its bakes are pinned in their residency
            ("prims", () => Prims?.Clear()),
            ("disposal queue", () => VariantDisposal.DrainAll("dispose")));   // the layers' dispose leaves nothing held
    }

    public object Describe() => new
    {
        view = _view.Kind,
        chunks = _view.ChunkCount,
        live_resources = _view.LiveResources + (Prims?.LiveResources ?? 0),
        prims = Prims?.Describe(),
        avatars = Avatars?.Describe(),
        dirty_chunks = _dirty.Count,
        terrain_prep = TerrainPrep is { } tp ? new { cap = tp.Cap, in_flight = tp.InFlight, running = tp.Running, prepared = tp.Prepared, stale = tp.Stale, discarded = tp.Discarded, failed = tp.Failed } : null,   // measurement: the terrain preparer's counters
        environment = EnvironmentSource,   // "region" when the region's answer parsed
        patches_presented = PatchesPresented,
        backlog = Coordinator.Backlog,
        backlog_age_ms = System.Math.Round(Coordinator.BacklogAgeMs, 2),
        budget_ms = Coordinator.BudgetMs,
        frames = Coordinator.Frames,
        frames_over_budget = Coordinator.FramesOverBudget,
        last_frame = new { applied = Coordinator.Last.Applied, steps = Coordinator.Last.Steps, spent_ms = System.Math.Round(Coordinator.Last.SpentMs, 3) },
    };
}

/// <summary>A fixed, deterministic stage for a region: environment, one directional light, an overview camera.</summary>
public static class Stage
{
    // the Environment resource made for each node here, kept for the node's life: reading it back from the node would hand out a fresh
    // managed wrapper each time, and one whose finalizer is running at that moment fails the call
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldEnvironment, global::Godot.Environment> s_environments = new();

    /// <summary>The Environment resource of a node made by <see cref="Environment()"/>, from the kept reference (the node's getter only for a node made elsewhere).</summary>
    public static global::Godot.Environment EnvironmentOf(WorldEnvironment node) => s_environments.TryGetValue(node, out var e) ? e : node.Environment;

    public static WorldEnvironment Environment()
    {
        var env = new global::Godot.Environment
        {
            BackgroundMode = global::Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.46f, 0.62f, 0.80f),
            AmbientLightSource = global::Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.55f, 0.58f, 0.62f),
            AmbientLightEnergy = 0.6f,
            TonemapMode = global::Godot.Environment.ToneMapper.Linear,
            GlowEnabled = false,
            SsaoEnabled = false,
            SsrEnabled = false,
            SdfgiEnabled = false,
            FogEnabled = false,
        };
        var node = new WorldEnvironment { Environment = env, Name = "environment" };
        s_environments.Add(node, env);
        return node;
    }

    public static DirectionalLight3D Sun() => new()
    {
        Name = "sun",
        RotationDegrees = new Vector3(-50f, -35f, 0f),
        LightEnergy = 1.1f,
        ShadowEnabled = false,
        Layers = 1u | GlowPass.InGlowPicture | ProbeCapture.Layer,   // the glow camera sees only the first two of these and the sky probe's capture camera the last, and a camera takes only the lights on layers it sees
    };

    /// <summary>The prim zoo's fixed camera (the zoo's 5 x 4 grid of 3 m cells from PrimZoo.Origin), from the south, above.</summary>
    public static void ZooCamera(Camera3D cam)
    {
        var o = PrimZoo.Origin;
        var target = new Vector3(o.X + 6f, o.Z + 0.5f, -(o.Y + 4.5f));
        cam.Fov = 50f;
        cam.Near = 0.1f;
        cam.Far = 200f;
        cam.Position = target + new Vector3(0f, 9f, 15f);
        cam.LookAt(target, Vector3.Up);
    }

    /// <summary>The mesh zoo's cameras: "near" (~7 m: the high LOD for its 2 m meshes) or "far" (~40 m, narrow FOV:
    /// the same framing at a lower LOD).</summary>
    /// <summary>The detail-at-a-distance scene's camera (<see cref="Viewer.Shell.DetailZoo"/>): at its point, looking east,
    /// 60 degrees, so the scene's distances are the distances the level rules measure.</summary>
    public static void DetailCamera(Camera3D cam)
    {
        var c = Viewer.Shell.DetailZoo.Camera;
        cam.Fov = 60f;
        cam.Near = 0.1f;
        cam.Far = 300f;
        cam.Position = new Vector3(c.X, c.Z, -c.Y);
        cam.LookAt(cam.Position + new Vector3(1f, 0f, 0f), Vector3.Up);
    }

    /// <summary>The layered-transparency scenes' camera (<see cref="Viewer.Shell.LayeredZoo"/>): at <paramref name="eye"/>
    /// (SL axes), looking east, level, 60 degrees.</summary>
    public static void LayeredCamera(Camera3D cam, System.Numerics.Vector3 eye)
    {
        cam.Fov = Viewer.Shell.LayeredZoo.FovDegrees;
        cam.Near = 0.1f;
        cam.Far = 300f;
        cam.Position = new Vector3(eye.X, eye.Z, -eye.Y);
        cam.LookAt(cam.Position + new Vector3(1f, 0f, 0f), Vector3.Up);
    }

    /// <summary>The glow scene's camera (<see cref="Viewer.Shell.GlowZoo"/>): at its eye (SL axes), looking east, level.</summary>
    public static void GlowCamera(Camera3D cam)
    {
        var eye = Viewer.Shell.GlowZoo.Eye;
        cam.Fov = Viewer.Shell.GlowZoo.FovDegrees;
        cam.Near = 0.1f;
        cam.Far = 300f;
        cam.Position = new Vector3(eye.X, eye.Z, -eye.Y);
        cam.LookAt(cam.Position + new Vector3(1f, 0f, 0f), Vector3.Up);
    }

    public static void ProjectorCamera(Camera3D cam)
    {
        var eye = Viewer.Shell.ProjectorZoo.Eye;
        cam.Fov = Viewer.Shell.ProjectorZoo.FovDegrees;
        cam.Near = 0.1f;
        cam.Far = 300f;
        cam.Position = new Vector3(eye.X, eye.Z, -eye.Y);
        cam.LookAt(cam.Position + new Vector3(1f, 0f, 0f), Vector3.Up);
    }

    public static void MeshCamera(Camera3D cam, string preset)
    {
        var o = MeshZoo.Origin;
        var target = new Vector3(o.X, o.Z, -o.Y);
        bool far = preset == "far";
        cam.Fov = far ? 11f : 60f;
        cam.Near = 0.1f;
        cam.Far = 300f;
        cam.Position = target + (far ? new Vector3(0f, 13f, 38f) : new Vector3(0f, 2.4f, 6.6f));
        cam.LookAt(target, Vector3.Up);
    }

    /// <summary>A fixed camera framed on a scene's unlinked, unattached objects (the median position, so a few far
    /// objects do not pull it away), from the south-west and above; deterministic for a given world state.</summary>
    public static void SceneCamera(Camera3D cam, Viewer.Core.World.WorldState world)
    {
        var roots = world.Snapshot().Objects.Values.Where(o => o.ParentLocalId == 0 && o.AttachmentPoint is null).Select(o => o.Position).ToList();
        if (roots.Count == 0) { Overview(cam, world.Region?.SizeX ?? 256, world.Region?.SizeY ?? 256); return; }
        float Median(IEnumerable<float> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
        var c = new System.Numerics.Vector3(Median(roots.Select(p => p.X)), Median(roots.Select(p => p.Y)), Median(roots.Select(p => p.Z)));
        float spread = Math.Clamp(Median(roots.Select(p => System.Numerics.Vector2.Distance(new(p.X, p.Y), new(c.X, c.Y)))) * 2.2f, 12f, 120f);
        var target = new Vector3(c.X, c.Z, -c.Y);
        cam.Fov = 60f;
        cam.Near = 0.2f;
        cam.Far = 1000f;
        cam.Position = target + new Vector3(-0.35f * spread, 0.55f * spread, 0.85f * spread);
        cam.LookAt(target, Vector3.Up);
    }

    /// <summary>Looks at the region centre from the south-west, high enough to see all of it.</summary>
    /// <summary>The colour chart's camera: orthographic, straight down on <paramref name="o"/> (SL), north up.</summary>
    public static void ColourCamera(Camera3D cam, System.Numerics.Vector3 o, float size)
    {
        cam.Projection = Camera3D.ProjectionType.Orthogonal;
        cam.Size = size;
        cam.Near = 0.1f;
        cam.Far = 100f;
        cam.Position = new Vector3(o.X, o.Z + 10f, -o.Y);
        cam.LookAt(new Vector3(o.X, o.Z, -o.Y), new Vector3(0, 0, -1));
    }

    public static void Overview(Camera3D cam, int sizeX, int sizeY)
    {
        float s = System.Math.Max(sizeX, sizeY);
        var target = new Vector3(sizeX / 2f, 20f, -sizeY / 2f);
        cam.Fov = 60f;
        cam.Near = 1f;
        cam.Far = s * 4f;
        cam.Position = target + new Vector3(-0.15f * s, 0.62f * s, 0.78f * s);
        cam.LookAt(target, Vector3.Up);
    }
}
