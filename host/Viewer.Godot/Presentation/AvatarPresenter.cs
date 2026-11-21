// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// Draws the avatars of one session's region. The <see cref="AvatarScene"/> (engine-free) says who is there and where;
/// this makes and frees what the engine draws for them: one skinned body each in an <see cref="AvatarLayer"/> (made when
/// the first avatar arrives, once the body is loaded), its server bakes (fetched through the session's texture path,
/// counted against the texture budget while drawn; the default skin until they arrive), the one animation chosen for it
/// (fetched through the <see cref="AnimationSource"/>). The avatar's name is drawn by the name tag overlay, not here. A departed avatar's resources are freed
/// at once, and a region change frees everything, the layer included: nothing made for one region outlives it. Main
/// thread only.
/// </summary>
public sealed partial class AvatarPresenter : IDisposable, IAttachmentFrames
{
    private sealed class DrawnAvatar
    {
        public int Id = -1;
        public int Users;                            // entries (of every region's presenter) that name this drawn avatar: one body, shared across a crossing
        public AvatarPresenter? DriverP;             // the presenter and entry that place and pose it; a passive entry only names it
        public AvatarEntry? DriverE;
        public int BakesVersion = -1;
        public Guid? Clip;
        public readonly MotionController Motions = new();   // the avatar's playing motions, their fades and the mix that poses it
        public AvatarShape? BodyShape;                // the shape its body was morphed by (null: the shared body)
        public int HandPose = HandMotion.Relaxed;     // the hand pose its body was made with
        public AvatarDefaultMotions? Defaults;       // the built-in motions every avatar runs (sway, breathing, eyes, head, hands, the flying roll)
        public double? Epoch;                        // the layer clock's zero for this avatar (its first update)
        public double MotionClock;                   // the motions' clock as of the last update
        public readonly WalkPlayback Walk = new();   // the walk cycle's playback rate for the clip it plays
        public FootSpeedTable? Feet;                 // the planted foot's speed of that clip (walk motions only)
        public bool WalkMotion;                      // the clip is a walk, run or turn cycle (whether or not a foot table could be made)
        public string? WalkWhy;                      // why a walk motion has no foot table
        public readonly Dictionary<string, (Guid Id, Texture2D? Texture, bool Done)> Bakes = new();
        public readonly Dictionary<string, BakeLevel> Levels = new();   // the discard level each slot's texture is at, and what is known of its picture
        public bool WornChecked;
        public int ShapeVersion = -1;                  // the entry's ShapeVersion last given to the layer
        public Task<PreparedSkinnedSurface[]?>? Morph;   // the avatar's morphed body surfaces being made off the main thread (null: none pending)
        public Task<FaceSurfaces?>? Face;              // the head and eyelash surfaces being remade off the main thread for a new expression weight (null: none pending)
        public PreparedSkinnedSurface[]? Surfaces;     // the surfaces of the own body last handed over (null: the shared body); a new face replaces only its own
        public int BodyGeneration, FaceGeneration;     // which body a face being made belongs to: a face made for an older body is dropped
        public List<object>? FaceTrace;                // the expression weights at each change, while a trace is asked for (development builds)
        public bool FaceDirty;                         // the expression weights changed since the face was last made
        public string[]? MorphHashes;                  // a fingerprint of each surface of the morphed body last handed over (null: none yet; empty: the shared body)
        public HashSet<string> Worn = new();          // body bake slots whose parts a worn mesh hides (its faces name that bake)
        public readonly HashSet<string> Transparent = new();   // body bake slots whose own bake has no drawn pixel (BakeAlphaMask)
        public readonly HashSet<string> SkippedForWorn = new();   // body bake slots left without a texture because a worn mesh draws their bake; loaded if the mesh goes
        public readonly Dictionary<string, (Guid Id, Texture2D Texture)> LastGood = new();   // another avatar's last drawn bake per slot, kept on the part while a new one arrives (BaseBodyRule)
        public AvatarArrivalGate? Gate;                // another avatar is drawn only once it is fully loaded (made at its first frame)
        public readonly BodyUncoverDelay Uncover = new();   // a part a removed worn mesh stopped hiding is uncovered only after a moment (other avatars)
        public string PartsDrawn = "all=D";               // what each body part was last given, for the NV_BODY_FRAMES trace: slot=B (bake), L (last good bake), D (default skin), H (not drawn)
        public string LastBodyTrace = "";              // the last state the NV_BODY_FRAMES trace wrote for it
    }

    /// <summary>NV_BODY_FRAMES=1: a development aid that writes a <c>body.frame</c> line, with the engine's frame number, whenever what is drawn of another
    /// avatar's system body changes: whether the avatar is shown, and what each body part is drawn with. Off by default; nothing is built without it.</summary>
    private static readonly bool s_bodyFrames = System.Environment.GetEnvironmentVariable("NV_BODY_FRAMES") == "1";

    private void TraceBody(AvatarEntry e, DrawnAvatar d)
    {
        if (!s_bodyFrames || e.IsSelf || _layer is null || d.Id < 0) return;
        bool shown = _layer.IsVisible(d.Id);
        string state = (shown ? "shown " : "not-shown ") + d.PartsDrawn;
        if (state == d.LastBodyTrace) return;
        d.LastBodyTrace = state;
        Main.Log.Info("body.frame", CoreLog.F("frame", WornFrameTrace.Frame), CoreLog.F("local", e.Id.LocalId), CoreLog.F("agent", WornFrameTrace.Short(e.AgentId)),
            CoreLog.F("shown", shown ? 1 : 0), CoreLog.F("parts", d.PartsDrawn), CoreLog.F("bakes", e.Bakes.Count));
    }

    /// <summary>Where one bake slot's texture is: the discard level on the GPU (-1: none yet), the full picture's size and level count once a decode has told
    /// them (a bake is taken to be a 1024 square with six levels until then), and when the level last changed.</summary>
    private sealed class BakeLevel
    {
        public int Level = -1, Asked = -1, FullW = 1024, FullH = 1024, Levels = 6;
        public double ChangedAt = double.NegativeInfinity;
        /// <summary>Whether this bake's alpha hides its body part, decided once from a fixed resolution (not from the level that is on the GPU).</summary>
        public readonly BakeAlphaMask Mask = new();
    }

    /// <summary>The bake slots the default body's parts are textured from.</summary>
    private static readonly string[] Slots = AvatarBody.DefaultParts.Select(p => p.Bake).Distinct().ToArray();

    private static readonly bool s_wornTrace = System.Environment.GetEnvironmentVariable("NV_WORN_TRACE") == "1";
    private static string WornTraceVector(System.Numerics.Vector3 v) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v.X:0.####},{v.Y:0.####},{v.Z:0.####}");
    private static string WornTraceQuaternion(System.Numerics.Quaternion q) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{q.X:0.####},{q.Y:0.####},{q.Z:0.####},{q.W:0.####}");
    private readonly WorldState _world;
    private readonly WornRecheck _recheck = new();   // avatars (local ids) that must work out again what their worn meshes hide
    private readonly Dictionary<ObjectId, (uint Wearer, Guid[] Faces)> _wornFaces = new();   // each worn piece's wearer and face textures as last seen
    private readonly Rid _scenario;
    private readonly Task<LoadedBody?>? _body;
    private readonly AnimationSource? _animations;
    private readonly TextureSource? _textures;
    private readonly Viewer.Core.Residency.TextureResidency? _residency;
    private Func<double> _now;
    private AvatarLayer? _layer;
    /// <summary>Called when the avatars' body is whole and avatars can be drawn (the skeleton and joint textures worn rigged meshes skin by now exist).</summary>
    public Action? LayerMade { get; set; }

    private AvatarLayer? _building;   // the first body's layer while its mesh is handed to the engine a surface a frame (with a frame budget); not used before it is whole

    /// <summary>Test only (the lifecycle's negative control): the first departed avatar's first bake texture is kept, never freed. False in the viewer.</summary>
    internal bool LeakBakeForNegativeControl { get; init; }
    internal Texture2D? LeakedBake => _leakedBakes.FirstOrDefault();

    /// <summary>A development aid: the picture of the bake an avatar is drawn with in a slot, as the engine holds it (at the discard level it was fetched at); null when none is drawn.</summary>
    internal Image? BakePicture(Guid agentId, string slot)
    {
        var entry = Scene.Entries.FirstOrDefault(e => e.AgentId == agentId);
        if (entry?.Handle is not DrawnAvatar d || !d.Bakes.TryGetValue(slot, out var bake) || bake.Texture is not { } texture) return null;
        var image = texture.GetImage();
        if (image is null) return null;
        if (image.IsCompressed()) image.Decompress();
        image.Convert(Image.Format.Rgba8);
        return image;
    }
    private readonly List<Texture2D> _leakedBakes = new();   // the negative control keeps two of a departed avatar's bake textures: the lifecycle check allows one stray engine object, and which texture comes first depends on the order the bakes arrived in

    /// <summary>A body read and its surfaces prepared, off the main thread.</summary>
    public sealed record LoadedBody(AvatarBody Body, PreparedSkinnedSurface[] Surfaces);

    /// <param name="body">The body being loaded (<see cref="LoadBody"/>); null draws no bodies (labels only).</param>
    /// <param name="clock">Seconds (test renders pass a fixed clock); default a stopwatch started here.</param>
    public AvatarPresenter(Rid scenario, WorldState world, Guid selfAgentId, Task<LoadedBody?>? body,
        AnimationSource? animations, TextureSource? textures, Viewer.Core.Residency.TextureResidency? residency, Func<double>? clock = null)
    {
        OfficialBodyRules = clock is null;
        if (clock is null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            clock = () => sw.Elapsed.TotalSeconds;
        }
        _now = clock;
        _scenario = scenario;
        _body = body;
        _animations = animations;
        _textures = textures;
        _residency = residency;
        _world = world;
        Scene = new AvatarScene(world, selfAgentId, clock);
    }

    public AvatarScene Scene { get; }

    /// <summary>An avatar was given to this presenter's region by another region (main thread).</summary>
    public event Action<Guid>? HandedOver;

    /// <summary>The user's own avatar has a motion that ran out on its own (an animation that does not loop reached its end); the argument is the animation's id.</summary>
    public event Action<Guid>? SelfMotionRanOut;

    private HandoffLedger? _handoffs;
    private readonly HashSet<Guid> _awaiting = new();

    /// <summary>The ledger shared by every region's presenters: an avatar another region takes over stays drawn here until that region draws it (and the other way round).</summary>
    public HandoffLedger? Handoffs
    {
        get => _handoffs;
        set
        {
            _handoffs = value;
            Scene.HoldHandoffs = value is not null;
            Scene.OwnMotion = value?.OwnMotion;   // the own avatar is one moving object across the regions that share the ledger
        }
    }

    /// <summary>The soles' position the user's own avatar was last moved to in the scene (what the frame draws), for the frame-time log.</summary>
    public System.Numerics.Vector3 SelfDrawnSoles { get; private set; }

    /// <summary>The body bake slots whose parts are not drawn for this avatar: those a worn mesh hides and those whose own bake has no drawn pixel.</summary>
    public string[] HiddenSlots(AvatarEntry e) => e.Handle is DrawnAvatar d ? d.Worn.Union(d.Transparent).Order().ToArray() : [];

    /// <summary>Why bodies are not drawn (no character folder, or it could not be read); null while loading or drawn.</summary>
    public string? BodyMissing => _body is null ? "no character folder configured" : _body.IsCompleted && _body.Result is null ? "the character folder could not be read" : null;

    /// <summary>Reads the character folder and prepares the body's surfaces off the main thread; null if it cannot be read.</summary>
    public static Task<LoadedBody?> LoadBody(string folder, Action<Exception>? failed = null) => Task.Run(() =>
    {
        try
        {
            var body = AvatarBody.Load(folder);
            return new LoadedBody(body, AvatarLayer.Prepare(body));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failed?.Invoke(ex);
            return (LoadedBody?)null;
        }
    });

    /// <summary>A world change (main thread, from the coordinator). A departure or region change frees at once.</summary>
    public void Apply(WorldChange change)
    {
        if (Scene.IsStaleReset(change)) return;   // this visit's avatars came first through the fast lane: the reset would only throw them away and the body with them
        int visit = Scene.Epoch;
        Scene.Apply(change);
        NoteHandoff(change);
        NoteWorn(change);
        FreeDeparted();
        // a region change: its reset, or the first avatar of the new visit when that came ahead of the reset (the fast lane)
        if (change.Kind == WorldChangeKind.RegionReset || (visit >= 0 && Scene.Epoch > visit))
        {
            if (_host is null)
            {
                _layer?.Dispose();   // the body's mesh and materials go with the region; the next arrival makes them again
                _layer = null;
            }
            _building?.Dispose();
            _building = null;
        }
    }

    /// <summary>Once a frame: arrivals made, bakes and animations taken as they arrive, every avatar placed and posed.</summary>
    /// <summary>The longest <see cref="Frame"/> of this presenter on its own (the world presenter's other work excluded), and how many ran over 4 ms.</summary>
    public double FrameMsMax { get; private set; }
    public int FramesOver4Ms { get; private set; }

    /// <summary>The time left for this presenter's work when it started its frame (the world presenter's shared allowance); null: no limit.</summary>
    private FrameBudget? _budget;

    /// <summary>What the work after the avatars' in the frame (the worn objects following their joints, the held values' disposal) took lately, in milliseconds;
    /// the world presenter keeps it up to date.</summary>
    public double AfterWorkMs { get; set; } = 0.5;

    /// <summary>A margin on top of that for the last unit of work running over its estimate, in milliseconds.</summary>
    public const double ReserveMarginMs = 0.4;

    /// <summary>What a frame keeps for the work after the avatars'.</summary>
    private double ReserveAfterMs => AfterWorkMs + ReserveMarginMs;

    /// <summary>The arrival work a frame starts (a body added, the worn meshes checked, a shape worked out) and the time left for it: past
    /// <see cref="ReserveAfterMs"/> left no more is started in the frame once one unit has been (that unit always is, so arrivals cannot stall).</summary>
    private int _arrivalUnitsThisFrame;

    // what each kind of arrival unit has cost lately (milliseconds a unit): a unit starts only when that, and what comes after it, fits the frame
    private const int UnitAdd = 0, UnitWorn = 1, UnitBakes = 2, UnitShape = 3;
    private readonly UnitCostModel[] _unitCosts = [new(), new(), new(), new()];
    private bool _arrivalRefused, _arrivalForceFirst;
    /// <summary>Arrival units put off to a later frame because they did not fit this one (measurement).</summary>
    public int ArrivalUnitsDeferred { get; private set; }

    private bool MayStartArrivalUnit(int kind)
    {
        if (_budget is null) return true;
        double estimate = Math.Max(ArrivalUnitMs, _unitCosts[kind].EstimateMs(1));
        if (_budget.RemainingMs > ReserveAfterMs + estimate) return true;
        if (_arrivalUnitsThisFrame == 0 && _arrivalForceFirst) return true;   // refused last frame and nothing ran in it: this one goes ahead, so arrivals cannot stall
        _arrivalRefused = true;
        ArrivalUnitsDeferred++;
        return false;
    }

    private void UnitDone(int kind, long startedAt) =>
        _unitCosts[kind].Record(1, (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>Time a unit of arrival work is taken to need, before it is started (a body added, a shape worked out: about a quarter of a millisecond measured).</summary>
    public const double ArrivalUnitMs = 0.3;

    /// <summary>Takes this region's changes for the frame, once per engine frame (its world presenter's); the body host runs it for every region before it
    /// places any body.</summary>
    internal Action? TakeChanges { get; set; }

    /// <summary>The budget of the frame in progress (the body host passes it on to the presenters it drives).</summary>
    internal FrameBudget? CurrentBudget => _budget;

    public void Frame(FrameBudget? budget = null)
    {
        _budget = budget;
        long frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_host is null) FrameCore();
        else
        {
            // one frame for every region that shares the body: the first presenter to run drives all of them, then the layer poses once
            // one tick for the engine frame, run by whichever presenter frames first: not by a rule about which presenter ran last (a neighbour that joined the body in the middle of a frame
            // would otherwise become the one that ticks, and the pose is the shared layer's, not its own)
            long engineFrame = (long)Engine.GetProcessFrames();
            if (_host.LastEngineFrame != engineFrame)
            {
                _host.LastEngineFrame = engineFrame;
                _host.Tick(this);
            }
        }
        double frameMs = (System.Diagnostics.Stopwatch.GetTimestamp() - frameStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        FrameMsMax = Math.Max(FrameMsMax, frameMs);
        if (frameMs > 4.0) FramesOver4Ms++;
    }

    /// <summary>One frame of this presenter's work on its own (no body host): the avatars driven, then the layer posed.</summary>
    private void FrameCore() => Drive(_budget, poseLayer: true);

    /// <summary>The body host drives every presenter that shares the body: <paramref name="budget"/> is the frame's.</summary>
    internal void Drive(FrameBudget? budget) => Drive(budget, poseLayer: false);

    /// <summary>The layer's pose for the frame, once the host has driven every presenter.</summary>
    internal void FrameLayer()
    {
        using (FrameProbe.Begin(FrameProbe.AvLayerFrame))
            (_host is not null ? _host.Layer : _layer)?.Frame(_now(), _budget is null ? double.PositiveInfinity : Math.Max(0, _budget.RemainingMs - ReserveAfterMs));   // the shared body is the host's, whichever presenter ran first this frame (that one may have no avatar of its own)
    }

    private void Drive(FrameBudget? budget, bool poseLayer)
    {
        _budget = budget;
        _arrivalForceFirst = _arrivalRefused && _arrivalUnitsThisFrame == 0;
        _arrivalRefused = false;
        _arrivalUnitsThisFrame = 0;
        using var whole = FrameProbe.Begin(FrameProbe.AvFrame);
        PollHandoffs();
        using (FrameProbe.Begin(FrameProbe.AvFree)) FreeDeparted();
        using (FrameProbe.Begin(FrameProbe.AvAnimations)) _animations?.Pump();
        double now = _now();
        if (_animations is not null && now - _animationUseAt >= 1.0)
        {
            // once a second: the clips this region's avatars play are reported, and the cache drops the ones nobody plays once it holds too many
            _animationUseAt = now;
            _animations.Uses.Report(this, Scene.Entries.SelectMany(e => e.Playing.Select(p => p.AnimationId)));
            _animations.Purge();
        }
        var (all, dirty) = _recheck.Take();
        _handedThisFrame = 0;
        MorphHandOverMsLastFrame = 0;
        _bakeUploadsThisFrame = 0;
        _bakeUploadMsThisFrame = 0;
        if (_host is not null)
        {
            // the body is the host's, built once for every region
            if (_layer is null && Scene.Count > 0)
                using (FrameProbe.Begin(FrameProbe.AvLayer))
                    if (_host.Build(_budget is not null) && _host.Layer is { } shared)
                    {
                        _layer = shared;
                        LayerMade?.Invoke();
                    }
        }
        else if (_layer is null && Scene.Count > 0 && _body is { IsCompletedSuccessfully: true, Result: { } loaded })
            using (FrameProbe.Begin(FrameProbe.AvLayer))
            {
                _building ??= new AvatarLayer(_scenario, loaded.Body, loaded.Surfaces,
                    _skin ??= new SkinTextures(loaded.Body.Skeleton.Joints.Count + loaded.Body.Skeleton.AttachmentPoints.Count), deferSurfaces: _budget is not null);
                if (_building.Step())   // a surface a call: the avatars wait for the whole body
                {
                    _layer = _building;
                    _building = null;
                    LayerMade?.Invoke();
                }
            }
        foreach (var e in Scene.Entries)
        {
            var d = e.Handle as DrawnAvatar;
            if (d is null) e.Handle = d = new DrawnAvatar();
            if (_host is not null && _layer is not null && d.Id < 0 && _host.Find(e.AgentId) is DrawnAvatar shared && shared.Id >= 0 && !ReferenceEquals(shared, d))
            {
                // the agent's body is drawn already (by the region it came from): this entry names that one, it is not drawn again
                e.Handle = d = shared;
                d.Users++;
            }
            if (_layer is not null && d.Id < 0)
            {
                if (!MayStartArrivalUnit(UnitAdd)) continue;   // the rest of this frame's time goes on: the body is added next frame
                _arrivalUnitsThisFrame++;
                long addAt = System.Diagnostics.Stopwatch.GetTimestamp();
                using (FrameProbe.Begin(FrameProbe.AvAdd)) d.Id = _layer.Add(default, 0, null, 0);
                UnitDone(UnitAdd, addAt);
                d.Clip = null;
                d.BakesVersion = -1;   // a body made later than its avatar takes the bakes already resolved
                d.Users = 1;
                _host?.Share(e.AgentId, d);
            }
            if (_host is not null && d.Id >= 0 && !Drives(e, d)) continue;   // another region's entry places and poses this body; this one only names it
            if ((all || !d.WornChecked || dirty.Contains(e.Id.LocalId)) && _layer is not null && d.Id >= 0 && !Scene.IsHeld(e))
            {
                if (MayStartArrivalUnit(UnitWorn))
                {
                    _arrivalUnitsThisFrame++;
                    long wornAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool wornChanged;
                    using (FrameProbe.Begin(FrameProbe.AvWorn)) wornChanged = UpdateWorn(e, d);
                    if (wornChanged) ApplyBakes(e, d);
                    if (d.Uncover.Pending) _recheck.Retry(e.Id.LocalId);   // worked out again next frame until the uncovered part may show
                    UnitDone(UnitWorn, wornAt);
                }
                else _recheck.Retry(e.Id.LocalId);   // next frame
            }
            // a new set of bakes is a unit of arrival work (the fetches and materials for its slots); the avatars not reached this frame are next frame's
            if (d.BakesVersion == e.BakesVersion || MayStartArrivalUnit(UnitBakes))
            {
                bool bakesUnit = d.BakesVersion != e.BakesVersion;
                if (bakesUnit) _arrivalUnitsThisFrame++;
                long bakesAt = System.Diagnostics.Stopwatch.GetTimestamp();
                using (FrameProbe.Begin(FrameProbe.AvBakes)) UpdateBakes(e, d);
                if (bakesUnit) UnitDone(UnitBakes, bakesAt);
            }
            if (d.ShapeVersion == e.ShapeVersion || MayStartArrivalUnit(UnitShape))
            {
                bool shapeUnit = d.ShapeVersion != e.ShapeVersion;
                if (shapeUnit) _arrivalUnitsThisFrame++;
                long shapeAt = System.Diagnostics.Stopwatch.GetTimestamp();
                using (FrameProbe.Begin(FrameProbe.AvShape)) UpdateShape(e, d);
                if (shapeUnit) UnitDone(UnitShape, shapeAt);
            }
            using (FrameProbe.Begin(FrameProbe.AvMorph)) PumpMorph(d);
            using (FrameProbe.Begin(FrameProbe.AvClip)) UpdateClip(e, d, now);
            using (FrameProbe.Begin(FrameProbe.AvMorph)) UpdateFace(d);
            using var placeScope = FrameProbe.Begin(FrameProbe.AvPlace);
            // a seated avatar is placed by its root: the scene needs the drawing's pelvis-above-soles height to hand the soles back
            if (e.Seat != 0 && _layer is not null && d.Id >= 0 && _layer.TryPelvisAboveSoles(d.Id, out float pelvisAboveSoles)) e.SetPelvisAboveSoles(pelvisAboveSoles);
            bool placed = Scene.Place(e, out var soles, out float yaw);
            if (placed && d.Walk.Active && _layer is not null && d.Id >= 0)
            {
                var v = e.Motion.Velocity(Scene.Now);
                _layer.SetPlayingSkew(d.Id, d.Walk.Step(now, new System.Numerics.Vector2(v.X, v.Y), yaw, HoldWalkWithClock));   // the cycle plays at the rate the avatar's ground speed asks for
            }
            if (placed && d.Defaults is { Started: true } && _layer is not null && d.Id >= 0) StepDefaults(e, d, now, yaw);
            if (!e.IsSelf && OfficialBodyRules) e.Loading = !ArrivalGateOpen(e, d, now);   // another avatar is drawn, with what it wears, once it is fully loaded
            bool shown = placed && Scene.Draws(e);   // the own avatar is left out in mouse-look: the body and what it wears (its name tag is the tag overlay's)
            if (_layer is not null && d.Id >= 0)
            {
                _layer.SetVisible(d.Id, shown);
                if (placed) { _layer.Move(d.Id, soles, yaw, Scene.GroundGap(e, soles)); if (e.IsSelf) SelfDrawnSoles = soles; }
                TraceBody(e, d);
            }
        }
        if (poseLayer) FrameLayer();
    }

    /// <summary>Whether this entry places and poses the (shared) body <paramref name="d"/>. One entry does at a time: the one of the region the agent is in; the entry a
    /// region keeps for an agent that has gone on to another region only names the body (so the pieces that region still draws ride the same skeleton).</summary>
    private bool Drives(AvatarEntry e, DrawnAvatar d)
    {
        if (ReferenceEquals(d.DriverP, this) && ReferenceEquals(d.DriverE, e)) return true;
        bool liveDriver = d.DriverP is { } p && d.DriverE is { } de && ReferenceEquals(p.Scene.ByLocalId(de.Id.LocalId), de);
        if (liveDriver && !(!Scene.IsHeld(e) && d.DriverP!.Scene.IsHeld(d.DriverE!))) return false;
        d.DriverP = this;
        d.DriverE = e;
        return true;
    }

    /// <summary>Where an attachment point is now (<see cref="IAttachmentFrames"/>): the avatar's skeleton as the last
    /// <see cref="Frame"/> posed it. False for an avatar not drawn (no body yet, or not placed), and for a point the skeleton lacks
    /// or a HUD point.</summary>
    public bool TryPoint(uint wearerLocalId, int attachmentPointId, out System.Numerics.Matrix4x4 world)
    {
        world = default;
        if (_layer is null || Scene.ByLocalId(wearerLocalId) is not { } wearer || !Scene.Draws(wearer) || wearer.Handle is not DrawnAvatar { Id: >= 0 } d) return false;
        int node = AttachmentPlacement.TargetNode(_layer.Rig, attachmentPointId);
        if (node < 0 || _layer.Rig.IsScreen[node]) return false;
        return _layer.TryNodeWorld(d.Id, node, out world);
    }

    /// <summary>A foot joint (mFootLeft or mFootRight, the joints the official viewer reads for footsteps): its place across as drawn and its
    /// height where the official viewer puts it (<see cref="AvatarLayer.TryNodeOfficialHeight"/>).</summary>
    public bool TryFootOfficial(uint localId, bool left, out System.Numerics.Vector3 at)
    {
        at = default;
        if (_layer is null || Scene.ByLocalId(localId) is not { } e || !Scene.Draws(e) || e.Handle is not DrawnAvatar { Id: >= 0 } d) return false;
        int node = _layer.Rig.Find(left ? "mFootLeft" : "mFootRight");
        if (node < 0 || !_layer.TryNodeWorld(d.Id, node, out var world) || !_layer.TryNodeOfficialHeight(d.Id, node, out float z)) return false;
        at = new System.Numerics.Vector3(world.M41, world.M42, z);
        return true;
    }

    /// <summary>For checks: where the avatar's pelvis joint is drawn (region frame), its drawn soles, and its shape's pelvis-to-foot height.</summary>
    public bool TryPelvis(uint localId, out System.Numerics.Vector3 pelvis, out System.Numerics.Vector3 soles, out float pelvisToFoot)
    {
        pelvis = soles = default; pelvisToFoot = 0;
        if (_layer is null || Scene.ByLocalId(localId)?.Handle is not DrawnAvatar { Id: >= 0 } d) return false;
        int node = _layer.Rig.Find("mPelvis");
        if (node < 0 || !_layer.TryNodeWorld(d.Id, node, out var world) || !_layer.TryDrawnSoles(d.Id, out soles)) return false;
        pelvis = world.Translation;
        if (_layer.TryShape(d.Id, out _, out _, out var measure) && measure is { } m) pelvisToFoot = m.PelvisToFoot;
        return true;
    }

    /// <summary>The animation that wins the avatar's pose (the highest base priority of those at hand, the latest started on a tie), its
    /// priority, and how long after it starts it reaches the pose it holds (a clip that plays once into a pose and holds its last frame, as
    /// the grid's sits do, loops from its end: its loop-in point; 0 for any other); false when none of its animations is at hand.</summary>
    public bool TryTopAnimation(uint localId, out Guid animation, out int priority, out float settles)
    {
        animation = Guid.Empty; priority = 0; settles = 0f;
        if (_animations is null || Scene.ByLocalId(localId) is not { } e) return false;
        if (AnimationChoice.Choose(e.Playing, id => _animations.TryGet(id, out var c) == MeshAvailability.Ready ? c!.Priority : null) is not { } top
            || _animations.TryGet(top, out var clip) != MeshAvailability.Ready || clip is null) return false;
        animation = top; priority = clip.Priority;
        settles = clip.Loop && clip.LoopOut - clip.LoopIn < 1e-4f ? Math.Clamp(clip.LoopIn, 0f, clip.Duration) : 0f;
        return true;
    }

    /// <summary>For the sit line: the seated avatar's posed skeleton in the region's frame, measured as the last frame drew it (the root
    /// the seat places, the pelvis, hips, knees and the foot joints, which rest on the soles). False while it is not drawn or not seated.</summary>
    public bool TrySeatedPose(uint localId, out Viewer.Shell.SeatedPose pose)
    {
        pose = null!;
        if (_layer is null || Scene.ByLocalId(localId) is not { Seat: not 0 } e || e.Handle is not DrawnAvatar { Id: >= 0 } d) return false;
        if (!Scene.Place(e, out var soles, out _) || !_layer.TryPelvisAboveSoles(d.Id, out float above)) return false;
        var rig = _layer.Rig;
        var at = new System.Numerics.Vector3[7];
        string[] joints = ["mPelvis", "mHipLeft", "mHipRight", "mKneeLeft", "mKneeRight", "mFootLeft", "mFootRight"];
        for (int i = 0; i < joints.Length; i++)
        {
            int node = rig.Find(joints[i]);
            if (node < 0 || !_layer.TryNodeWorld(d.Id, node, out var world)) return false;
            at[i] = world.Translation;
        }
        pose = new Viewer.Shell.SeatedPose(soles + new System.Numerics.Vector3(0, 0, above), at[0], at[1], at[2], at[3], at[4], at[5], at[6]);
        return true;
    }

    /// <summary>The avatar's own body height from its shape (the official body size; null while it has no shape) and its hover.</summary>
    public bool TryOwnBody(uint localId, out float height, out float hover)
    {
        height = 0; hover = 0;
        if (_layer is null || Scene.ByLocalId(localId)?.Handle is not DrawnAvatar { Id: >= 0 } d || _layer.ShapeOf(d.Id) is null) return false;
        if (!_layer.TryShape(d.Id, out _, out hover, out var measure) || measure is not { } m) return false;
        height = m.Height;
        return true;
    }

    /// <summary>An attachment point's place as drawn and its joint's height where the official viewer puts it
    /// (<see cref="AvatarLayer.TryNodeOfficialHeight"/>), for footsteps.</summary>
    public bool TryPointOfficial(uint wearerLocalId, int attachmentPointId, out System.Numerics.Matrix4x4 world, out float z)
    {
        z = 0;
        if (!TryPoint(wearerLocalId, attachmentPointId, out world) || _layer is null || Scene.ByLocalId(wearerLocalId)?.Handle is not DrawnAvatar { Id: >= 0 } d)
            return false;
        return _layer.TryNodeOfficialHeight(d.Id, AttachmentPlacement.TargetNode(_layer.Rig, attachmentPointId), out z);
    }

    /// <summary>Where an avatar's soles are drawn (<see cref="AvatarLayer.TryDrawnSoles"/>), by its local id.</summary>
    public bool TryDrawnSoles(uint localId, out System.Numerics.Vector3 soles)
    {
        soles = default;
        return _layer is not null && Scene.ByLocalId(localId)?.Handle is DrawnAvatar { Id: >= 0 } d && _layer.TryDrawnSoles(d.Id, out soles);
    }

    /// <inheritdoc cref="IAttachmentFrames.TrySkeleton"/>
    public bool TrySkeleton(uint wearerLocalId, out object layer, out int row)
    {
        layer = null!;
        row = -1;
        if (_layer is null || Scene.ByLocalId(wearerLocalId) is not { } wearer || !Scene.Draws(wearer) || wearer.Handle is not DrawnAvatar { Id: >= 0 } d) return false;
        layer = _layer;
        row = d.Id;
        return true;
    }

    /// <summary>The bodies of <paramref name="agent"/> shown now, each as the layer that draws it and its row there (the same body held by two entries counts once).</summary>
    public IEnumerable<(object Layer, int Row)> DrawnInstances(Guid agent)
    {
        if (_layer is null) yield break;
        foreach (var e in Scene.Entries)
            if (e.AgentId == agent && e.Handle is DrawnAvatar { Id: >= 0 } d && _layer.IsVisible(d.Id)) yield return (_layer, d.Id);
    }

    /// <summary>An attachment point of a body of <paramref name="agent"/> that is shown now, as it is posed (false when none is shown).</summary>
    public bool TryDrawnPoint(Guid agent, int attachmentPointId, out System.Numerics.Matrix4x4 world)
    {
        world = default;
        if (_layer is null) return false;
        int node = AttachmentPlacement.TargetNode(_layer.Rig, attachmentPointId);
        if (node < 0 || _layer.Rig.IsScreen[node]) return false;
        foreach (var (_, row) in DrawnInstances(agent))
            return _layer.TryNodeWorld(row, node, out world);
        return false;
    }

    /// <summary>Where the head joint of a body of <paramref name="agent"/> shown now is, as it is posed (region metres); false when none is shown.</summary>
    internal bool TryDrawnHead(Guid agent, out System.Numerics.Vector3 head)
    {
        head = default;
        if (_layer is null) return false;
        int node = _layer.Rig.Find("mHead");
        if (node < 0) return false;
        foreach (var (_, row) in DrawnInstances(agent))
        {
            if (!_layer.TryNodeWorld(row, node, out var world)) return false;
            head = world.Translation;
            return true;
        }
        return false;
    }

    /// <summary>A short fingerprint of the vertex and attribute data of each surface of a morphed body (null: the shared body, no surfaces).</summary>
    internal static string[] Fingerprints(PreparedSkinnedSurface[]? surfaces) =>
        surfaces is null ? [] : surfaces.Select(s => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([.. s.Surface.VertexData, .. s.Surface.AttributeData]))[..16]).ToArray();

    private static readonly string[] FaceJointPrefixes = ["mHead", "mNeck", "mSkull", "mEye", "mFace", "mTeeth", "mTongue"];

    /// <summary>A development aid: what the avatar of <paramref name="agent"/> is drawn with now, to compare with the same set drawn offline. The visual-parameter
    /// weights the shaped skeleton holds, the head and face joints' positions in the head's own frame (so a turned or tilted head changes none of them), the head's and neck's
    /// turn, tilt and roll in degrees, and a fingerprint of each surface of the morphed body last handed over. Null when no body is drawn.</summary>
    /// <summary>Draws <paramref name="agent"/>'s body as a mask (<see cref="AvatarLayer.SetMask"/>; null: as before) and gives its head joint's
    /// place in the region as drawn now. False when this presenter draws no body for the agent.</summary>
    internal bool SetMask(Guid agent, Material? mask, bool hiddenToo, out System.Numerics.Matrix4x4 headWorld)
    {
        headWorld = default;
        if (_layer is null) return false;
        int head = _layer.Rig.Find("mHead");
        foreach (var e in Scene.Entries)
        {
            if (e.AgentId != agent || e.Handle is not DrawnAvatar { Id: >= 0 } d) continue;
            if (!_layer.SetMask(d.Id, mask, hiddenToo)) continue;
            _layer.TryNodeWorld(d.Id, head, out headWorld);
            return true;
        }
        return false;
    }

    /// <summary>The expression weights <paramref name="agent"/>'s face took at each change since the trace was started, with the clock and the latest expression's start; <paramref name="reset"/>
    /// starts a new trace. Null when the avatar is not drawn. Development aid.</summary>
    internal object? ExpressionTrace(Guid agent, bool reset)
    {
        foreach (var e in Scene.Entries)
        {
            if (e.AgentId != agent || e.Handle is not DrawnAvatar { Id: >= 0 } d) continue;
            if (reset) d.FaceTrace = new List<object>();
            return new { clock = d.MotionClock, active = d.Motions.Expressions.Active, start = d.Motions.Expressions.LastStart, own_body = _layer?.IsMorphed(d.Id) ?? false, trace = d.FaceTrace?.ToArray() ?? [] };
        }
        return null;
    }

    internal object? ShapeDump(Guid agent)
    {
        if (_layer is null) return null;
        int head = _layer.Rig.Find("mHead"), neck = _layer.Rig.Find("mNeck");
        foreach (var e in Scene.Entries)
        {
            if (e.AgentId != agent || e.Handle is not DrawnAvatar { Id: >= 0 } d) continue;
            if (head < 0 || !_layer.TryNodeWorld(d.Id, head, out var headWorld) || !System.Numerics.Matrix4x4.Invert(headWorld, out var inverse)) continue;
            var joints = new SortedDictionary<string, float[]>(StringComparer.Ordinal);
            for (int node = 0; node < _layer.Rig.JointCount; node++)
            {
                string name = _layer.Rig.NodeName(node);
                if (!FaceJointPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) || !_layer.TryNodeWorld(d.Id, node, out var w)) continue;
                var p = System.Numerics.Vector3.Transform(w.Translation, inverse);
                joints[name] = [MathF.Round(p.X * 1000, 3), MathF.Round(p.Y * 1000, 3), MathF.Round(p.Z * 1000, 3)];   // millimetres
            }
            float[] Angles(int node)
            {
                if (node < 0 || !_layer.TryJointRotation(d.Id, node, out var q)) return [];
                float yaw = MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z));
                float pitch = MathF.Asin(Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1f, 1f));
                float roll = MathF.Atan2(2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y));
                const float Deg = 180f / MathF.PI;
                return [MathF.Round(yaw * Deg, 2), MathF.Round(pitch * Deg, 2), MathF.Round(roll * Deg, 2)];
            }
            var shape = _layer.ShapeOf(d.Id);
            var weights = shape?.Weights.OrderBy(kv => kv.Key).Select(kv => new[] { kv.Key, MathF.Round(kv.Value, 6) }).ToArray() ?? [];
            string weightsHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(";", weights.Select(w => $"{w[0]:0}={w[1]:0.000000}"))))) [..16];
            return new
            {
                weights_hash = weightsHash,
                weight_count = weights.Length,
                weights,
                male = shape?.Male,
                hover = shape?.Hover,
                head_turn_tilt_roll_deg = Angles(head),
                neck_turn_tilt_roll_deg = Angles(neck),
                joints_in_head_frame_mm = joints,
                morph_surface_hashes = d.MorphHashes,
                morph_pending = d.Morph is not null,
                morph_shape_is_current = ReferenceEquals(d.BodyShape, shape),
                shape_version_current = d.ShapeVersion == e.ShapeVersion,
            };
        }
        return null;
    }

    /// <summary>The rigged meshes the avatar of <paramref name="agent"/> wears (the mesh asset of each worn object) and what they do to
    /// the skeleton (<see cref="AvatarLoginDump.Describe"/>); null when no body is drawn.</summary>
    internal object? WornDump(Guid agent)
    {
        if (_layer is null) return null;
        foreach (var e in Scene.Entries)
        {
            if (e.AgentId != agent || e.Handle is not DrawnAvatar { Id: >= 0 } d || _layer.WearOf(d.Id) is not { } wear) continue;
            return new
            {
                rigged_objects = wear.Objects,
                rigged_mesh_assets = wear.Meshes.OrderBy(kv => kv.Key).Select(kv => kv.Value.ToString()).ToArray(),
                joint_overrides = AvatarLoginDump.Describe(wear.Overrides, _layer.Rig),
            };
        }
        return null;
    }

    /// <summary>Where the lowest point of the seat is drawn now: the pelvis volume's point a constrained ground sit holds to the ground (region metres);
    /// false when no body is shown. A development aid for checking a sit against the ground.</summary>
    internal bool TryDrawnSeat(Guid agent, out System.Numerics.Vector3 seat)
    {
        seat = default;
        if (_layer is null) return false;
        int node = _layer.Rig.Find("PELVIS");
        if (node < 0) return false;
        foreach (var (_, row) in DrawnInstances(agent))
        {
            if (!_layer.TryNodeWorld(row, node, out var world)) return false;
            seat = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0, 0, -0.9f), world);
            return true;
        }
        return false;
    }

    private static readonly string[] LimbVolumes = ["L_HAND", "R_HAND", "L_FOOT", "R_FOOT", "L_LOWER_LEG", "R_LOWER_LEG"];

    /// <summary>Where the origins of the hand, foot and lower-leg collision volumes of a body of <paramref name="agent"/> shown now are drawn (region metres), by volume
    /// name. A development aid for checking a ground sit against the ground; null when no body is shown.</summary>
    internal SortedDictionary<string, float[]>? DrawnLimbs(Guid agent)
    {
        if (_layer is null) return null;
        foreach (var (_, row) in DrawnInstances(agent))
        {
            var limbs = new SortedDictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var name in LimbVolumes)
            {
                int node = _layer.Rig.Find(name);
                if (node < 0 || !_layer.TryNodeWorld(row, node, out var w)) continue;
                limbs[name] = [w.Translation.X, w.Translation.Y, w.Translation.Z];
            }
            return limbs;
        }
        return null;
    }

    /// <summary>Where the middle of the body this presenter draws for <paramref name="agent"/> is, and its heading (<see cref="AvatarScene.DrawnPose"/>).</summary>
    internal bool TryDrawnPose(Guid agent, out System.Numerics.Vector3 centre, out System.Numerics.Quaternion heading)
    {
        centre = default;
        heading = System.Numerics.Quaternion.Identity;
        foreach (var e in Scene.Entries)
            if (e.AgentId == agent && e.Handle is DrawnAvatar { Id: >= 0 } d && ReferenceEquals(d.DriverE, e) && ReferenceEquals(d.DriverP, this) && Scene.DrawnPose(e, out centre, out heading)) return true;
        return false;
    }

    /// <summary>Where a body of <paramref name="agent"/> is drawn (its feet) and where the agent is by this presenter's scene now (<see cref="AvatarScene.Place"/>), the drawn one
    /// from the layer's last frame. False when no body of the agent is shown, or the scene does not hold it live.</summary>
    public bool TryDrawnSoles(Guid agent, out System.Numerics.Vector3 drawn, out System.Numerics.Vector3 expected)
    {
        drawn = expected = default;
        if (_layer is null) return false;
        bool haveDrawn = false, haveExpected = false;
        foreach (var e in Scene.Entries)
        {
            if (e.AgentId != agent || e.Handle is not DrawnAvatar { Id: >= 0 } d) continue;
            if (!haveDrawn && _layer.TryDrawnSoles(d.Id, out drawn)) haveDrawn = true;
            if (!haveExpected && !Scene.IsHeld(e) && Scene.Place(e, out expected, out _)) haveExpected = true;
        }
        return haveDrawn && haveExpected;
    }

    /// <summary>The place a rigged mesh worn by a body of <paramref name="agent"/> that is shown now is skinned into, as it is posed.</summary>
    public bool TryDrawnRigged(Guid agent, out System.Numerics.Matrix4x4 world)
    {
        world = default;
        if (_layer is null) return false;
        foreach (var (_, row) in DrawnInstances(agent))
            return _layer.TryRigged(row, out _, out world);
        return false;
    }

    /// <summary>The body and skeleton shared by every region's presenter (one drawn avatar across regions); null: this presenter has its own.</summary>
    public AvatarBodyHost? Host
    {
        get => _host;
        init
        {
            _host = value;
            if (value is null) return;
            _now = value.Clock;   // the layer's poses are worked out from one clock
            value.Join(this);
        }
    }

    private readonly AvatarBodyHost? _host;

    private SkinTextures? _skin;

    /// <summary>The skeleton the avatars are drawn with; null until the body is read (<see cref="IAttachmentFrames"/>).</summary>
    public AvatarRig? Rig => _layer?.Rig;

    /// <summary>The textures the skinning of worn rigged meshes reads; null until the first avatar is drawn. They last as long as this presenter,
    /// across region changes, so a material that names them stays valid.</summary>
    public SkinTextures? SkinTextures => _host is not null ? _host.Skin : _skin;

    private int DrawnId(uint wearerLocalId) => _layer is not null && Scene.ByLocalId(wearerLocalId)?.Handle is DrawnAvatar { Id: >= 0 } d ? d.Id : -1;

    public bool WearRigged(uint wearerLocalId, uint local, Guid asset, Viewer.Core.Meshing.MeshSkinBlock skin) =>
        DrawnId(wearerLocalId) is int id && id >= 0 && _layer!.WearRigged(id, local, asset, skin);

    public void UnwearRigged(uint wearerLocalId, uint local)
    {
        if (DrawnId(wearerLocalId) is int id && id >= 0) _layer!.UnwearRigged(id, local);
    }

    /// <summary>Whether the avatar with this local id is kept drawn only because another region took it over and has not drawn it yet.</summary>
    public bool WearerHeld(uint wearerLocalId) => Scene.ByLocalId(wearerLocalId) is { } e && Scene.IsHeld(e);

    /// <inheritdoc cref="IAttachmentFrames.WearerIsOther"/>
    public bool WearerIsOther(uint wearerLocalId) => OfficialBodyRules && Scene.ByLocalId(wearerLocalId) is { IsSelf: false } e && e.Handle is DrawnAvatar { Id: >= 0 };

    public bool TryRigged(uint wearerLocalId, out int row, out System.Numerics.Matrix4x4 world)
    {
        row = -1;
        world = default;
        return Scene.ByLocalId(wearerLocalId) is { } wearer && Scene.Draws(wearer) && DrawnId(wearerLocalId) is int id && id >= 0 && _layer!.TryRigged(id, out row, out world);
    }

    /// <summary>A handoff change: the avatar this region lost is now held (told to the ledger); the avatar this region was given is waited for
    /// (<see cref="PollHandoffs"/> tells the ledger once it is drawn).</summary>
    private void NoteHandoff(WorldChange change)
    {
        if (_handoffs is null || !change.Handoff) return;
        if (change.Kind == WorldChangeKind.AvatarRemoved)
        {
            foreach (var held in Scene.Held)
                if (held.Id == change.Id) _handoffs.Hold(held.AgentId, piece: false);
        }
        else if (change.Kind is WorldChangeKind.AvatarAdded or WorldChangeKind.AvatarChanged or WorldChangeKind.AvatarMoved
                 && _world.Avatar(change.Id) is { } given)
        {
            _handoffs.Expect(given.AgentId);
            HandedOver?.Invoke(given.AgentId);
            _awaiting.Add(given.AgentId);
        }
    }

    /// <summary>Each frame: a held avatar is let go of once the ledger says so; an avatar given to this region is reported once it is drawn.</summary>
    private void PollHandoffs()
    {
        if (_handoffs is null) return;
        if (Scene.Held.Count > 0)
            foreach (var held in Scene.Held)
            {
                if (_handoffs.TryRelease(held.AgentId)) { Scene.Release(held.Id); continue; }
                // the held copy is the same avatar: it is drawn where the agent is now, in every frame (the official viewer has one drawable that moves)
                if (_handoffs.Locate?.Invoke(held.AgentId) is { } now) Scene.Follow(held, now);
            }
        if (_awaiting.Count == 0) return;
        foreach (var agent in _awaiting.ToList())
        {
            var entry = Scene.Entries.FirstOrDefault(e => e.AgentId == agent && !Scene.IsHeld(e));
            if (entry is null) { _awaiting.Remove(agent); continue; }   // it left again before it was drawn
            if (entry.Handle is DrawnAvatar { Id: >= 0 } && _layer is not null && Scene.Draws(entry))
            {
                _handoffs.Entered(agent);
                _awaiting.Remove(agent);
            }
        }
    }

    /// <summary>A worn object was applied ahead of the queue: only the wearer is marked (the change itself reaches the avatar's state through the queue).</summary>
    public void NoteWornChange(WorldChange change) => NoteWorn(change);

    /// <summary>A world change that may change what an avatar's worn meshes hide, on the triggers of the official viewer's updateMeshVisibility
    /// (<see cref="WornRecheck"/>): a worn piece attached, or detached (its wearer is known from when it was seen; an unknown one marks every avatar),
    /// an appearance message applied, and a worn piece's face texture going to or from a baked id.</summary>
    private void NoteWorn(WorldChange change)
    {
        if (change.Kind == WorldChangeKind.AppearanceChanged)
        {
            foreach (var e in Scene.Entries)
                if (e.AgentId == change.AgentId) _recheck.AppearanceApplied(e.Id.LocalId);
            return;
        }
        if (change.Kind == WorldChangeKind.ObjectRemoved)
        {
            if (change.Handoff && _handoffs is not null) return;   // a piece handed over is still worn here until the other region draws it
            if (_wornFaces.Remove(change.Id, out var gone)) _recheck.Detached(gone.Wearer);
            else _recheck.Detached(null);
            return;
        }
        if (change.Kind is not (WorldChangeKind.ObjectAdded or WorldChangeKind.ObjectChanged)) return;
        if (_world.Object(change.Id) is not { } o) { if (s_wornTrace) Main.Log.Info("worn.note", CoreLog.F("kind", change.Kind.ToString()), CoreLog.F("local", change.Id.LocalId), CoreLog.F("found", 0)); return; }
        if (s_wornTrace) Main.Log.Info("worn.note", CoreLog.F("kind", change.Kind.ToString()), CoreLog.F("local", change.Id.LocalId), CoreLog.F("parent", o.ParentLocalId), CoreLog.F("point", o.AttachmentPoint?.ToString() ?? "-"), CoreLog.F("tex", o.Textures is null ? 0 : 1),
            CoreLog.F("pos", WornTraceVector(o.Position)), CoreLog.F("rot", WornTraceQuaternion(o.Rotation)), CoreLog.F("scale", WornTraceVector(o.Scale)));   // pos and rot as the region sent them: a root's from its attachment point, a child's from its root
        uint wearer;
        if (o.AttachmentPoint is not null) wearer = o.ParentLocalId;
        else if (o.ParentLocalId != 0 && _world.Object(new ObjectId(o.Id.Epoch, o.ParentLocalId)) is { AttachmentPoint: not null } root) wearer = root.ParentLocalId;
        else return;
        var faces = new List<Guid>();
        if (o.Textures is { } t)
        {
            faces.Add(t.Default.TextureId);
            foreach (var f in t.Overrides)
                if (f is { } x) faces.Add(x.TextureId);
        }
        if (_wornFaces.TryGetValue(o.Id, out var seen) && seen.Wearer == wearer) _recheck.FacesChanged(wearer, seen.Faces, faces);
        else
        {
            _recheck.Attached(wearer);   // first seen on this wearer
            if (Scene.ByLocalId(wearer)?.Handle is DrawnAvatar { Gate: { } gate }) gate.WornArrived(_now());   // a piece arriving before the first showing starts the wait again
        }
        _wornFaces[o.Id] = (wearer, faces.ToArray());
    }

    /// <summary>Whether a burst of removed worn pieces of the agent is still drawn by this region's objects (the region's prim layer answers); null: none.</summary>
    public Func<Guid, bool>? WornHeld { get; set; }

    /// <summary>The official fully-loaded rule for another avatar (<see cref="AvatarArrivalGate"/>): a part missing (no shape, or no head, upper or lower
    /// bake) or, before its first showing, a bake it names not decided yet.</summary>
    private bool ArrivalGateOpen(AvatarEntry e, DrawnAvatar d, double now)
    {
        d.Gate ??= new AvatarArrivalGate(now);
        bool missing = e.VisualParams is not { Count: > 0 } || !e.Bakes.ContainsKey("head") || !e.Bakes.ContainsKey("upper") || !e.Bakes.ContainsKey("lower");
        bool pending = d.BakesVersion != e.BakesVersion
            || e.Bakes.Keys.Any(slot => Slots.Contains(slot) && !(d.Bakes.TryGetValue(slot, out var b) && (b.Texture is not null || b.Done)));
        return d.Gate.Update(now, missing, pending);
    }

    /// <summary>The body parts the avatar's worn meshes hide (<see cref="BodyVisibility"/>); true when the set changed.</summary>
    private bool UpdateWorn(AvatarEntry e, DrawnAvatar d)
    {
        d.WornChecked = true;
        var faces = new List<Guid>();
        foreach (var root in _world.AttachmentRoots(e.Id.LocalId))
            foreach (var part in _world.Linkset(root.Id).Prepend(root))
                if (part.Textures is { } t)
                {
                    faces.Add(t.Default.TextureId);
                    foreach (var f in t.Overrides)
                        if (f is { } x) faces.Add(x.TextureId);
                }
        var hidden = BodyVisibility.HiddenSlots(faces);
        if (s_wornTrace) Main.Log.Info("worn.update", CoreLog.F("avatar", e.Id.LocalId), CoreLog.F("roots", _world.AttachmentRoots(e.Id.LocalId).Count), CoreLog.F("faces", faces.Count), CoreLog.F("hidden", string.Join("+", hidden.Order())));
        if (d.Users > 1) hidden = hidden.Union(d.Worn).ToHashSet();   // while the region left still draws pieces, what they hid stays hidden; the pieces this region has so far must not uncover it
        if (!e.IsSelf && OfficialBodyRules)
        {
            // a part a removed mesh stopped hiding shows only after a moment, and not while a burst of the avatar's removed pieces is still drawn
            if (WornHeld?.Invoke(e.AgentId) == true) hidden = d.Uncover.Next(d.Worn, hidden, _now(), holding: true).ToHashSet();
            else hidden = d.Uncover.Next(d.Worn, hidden, _now()).ToHashSet();
        }
        if (hidden.SetEquals(d.Worn)) return false;
        d.Worn = hidden.ToHashSet();
        return true;
    }

    /// <summary>The avatar's shaped skeleton from its visual parameters, when they change (the male/female choice, the proportions, the hover height).</summary>
    private void UpdateShape(AvatarEntry e, DrawnAvatar d)
    {
        if (_layer is null || d.Id < 0 || d.ShapeVersion == e.ShapeVersion) return;
        d.ShapeVersion = e.ShapeVersion;
        AvatarShape? shape = null;
        if (e.VisualParams is { Count: > 0 } vp && _body is { IsCompletedSuccessfully: true, Result: { } loaded } && loaded.Body.Shape is not null)
            shape = AvatarShape.From(_layer.Rig, loaded.Body.Shape, vp, AvatarShape.HoverOverride(e.IsSelf, e.Hover));
        StartBodyMorph(d, shape);
        _layer.SetShape(d.Id, shape);
        // the body's own height and hover, which the official viewer seats it by
        if (shape is not null && _layer.TryShape(d.Id, out _, out float hover, out var measure) && measure is { } m) e.SetOwnBody(m.Height, hover);
        else e.SetOwnBody(0f, 0f);
    }

    /// <summary>Makes the avatar's own body off the main thread: its shape's morphs (none without one) and its hands in the pose its animations ask for. A body with a relaxed
    /// hand pose and no shape is the shared one. The avatar keeps its last body until the new one is ready.</summary>
    private void StartBodyMorph(DrawnAvatar d, AvatarShape? shape)
    {
        d.Morph = null;   // a result still being made belongs to the old appearance and is dropped when it ends
        d.BodyShape = shape;
        int pose = d.HandPose = d.Defaults?.Hand.Wanted ?? HandMotion.Relaxed;
        d.BodyGeneration++;
        var expressions = d.Motions.Expressions;
        var face = expressions.Weights.Count > 0 ? new Dictionary<string, float>(expressions.Weights) : null;   // a copy: the weights change on the main thread
        d.FaceDirty = false;   // this body carries the face as it is now
        if (_body is not { IsCompletedSuccessfully: true, Result: { } loaded } || loaded.Body.Shape is not { } def || (shape is null && pose == HandMotion.Relaxed && !expressions.Active))
        {
            _layer!.SetMorphed(d.Id, null);
            d.Surfaces = null;
            d.MorphHashes = [];
            return;
        }
        // the body's own shape (its morph targets) is worked out off the main thread
        var s = shape ?? AvatarShape.Neutral(_layer!.Rig);
        bool shapeless = shape is null;
        d.Morph = Task.Run(() =>
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var alloc = Viewer.Core.Diagnostics.AllocationMeter.Shared.Begin(Viewer.Core.Diagnostics.AllocKind.MorphPrepare);   // (measurement only)
            var made = AvatarLayer.PrepareMorphed(loaded.Body, def, s, pose, shapeless, face);
            alloc.End();
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            lock (_morphLock) { _morphPrepareMsLast = ms; _morphPrepareMsMax = Math.Max(_morphPrepareMsMax, ms); }
            return made;
        });
    }

    /// <summary>The most morphed bodies handed to the engine in one frame (each costs one engine call a surface).</summary>
    public const int MorphHandOversPerFrame = 1;

    /// <summary>Main-thread time of the last frame's hand-over of a morphed body, in milliseconds (0 when none was handed over).</summary>
    public double MorphHandOverMsLastFrame { get; private set; }

    /// <summary>Morphed bodies handed over since this presenter was made.</summary>
    public int MorphHandOvers { get; private set; }

    private int _handedThisFrame;
    private readonly object _morphLock = new();
    private (double Last, double Max) PrepareMs() { lock (_morphLock) return (_morphPrepareMsLast, _morphPrepareMsMax); }
    private double _morphPrepareMsLast, _morphPrepareMsMax, _morphHandOverMsTotal, _morphHandOverMsMax;

    private IReadOnlySet<string>? _faceTypes;

    /// <summary>One frame of the avatar's facial expressions: their weights at the motions' clock, and when they changed, the head and eyelashes made again off the
    /// main thread and handed over with the avatar's other surfaces as they were (one remake at a time; the latest weights win). An avatar on the shared body gets
    /// its own body first, which carries the face. Main thread.</summary>
    private void UpdateFace(DrawnAvatar d)
    {
        if (_layer is null || d.Id < 0) return;
        if (d.Motions.Expressions.Update(d.MotionClock))
        {
            d.FaceDirty = true;
            if (d.FaceTrace is { Count: < 4000 } trace)
                trace.Add(new { t = d.MotionClock, start = d.Motions.Expressions.LastStart, w = d.Motions.Expressions.Weights.Where(kv => kv.Value != 0f || kv.Key == Emotes.ClosedMouth).ToDictionary(kv => kv.Key, kv => kv.Value) });
        }
        if (d.Face is { IsCompleted: true } done)
        {
            d.Face = null;
            if (done.IsCompletedSuccessfully && done.Result is { } made && d.FaceGeneration == d.BodyGeneration && d.Morph is null && d.Surfaces is { } kept)
            {
                // the body with its new head and eyelashes and every other surface as it was: handed over as any new body is (PumpMorph)
                var body = (PreparedSkinnedSurface[])kept.Clone();
                for (int k = 0; k < made.Parts.Length; k++) body[made.Parts[k]] = made.Surfaces[k];
                d.Morph = Task.FromResult<PreparedSkinnedSurface[]?>(body);
            }
        }
        if (!d.FaceDirty || d.Face is not null || d.Morph is not null) return;
        if (d.Surfaces is null) { StartBodyMorph(d, d.BodyShape); return; }
        if (_body is not { IsCompletedSuccessfully: true, Result: { } loaded } || loaded.Body.Shape is not { } def) return;
        d.FaceDirty = false;
        var types = _faceTypes ??= BodyMorph.MeshTypesOf(def, AvatarFaceSurfaces.ExpressionParams);
        var named = AvatarFaceSurfaces.Named(AvatarLayer.HandWeights(d.HandPose), new Dictionary<string, float>(d.Motions.Expressions.Weights));
        var shape = d.BodyShape ?? AvatarShape.Neutral(_layer.Rig);
        bool shapeless = d.BodyShape is null;
        d.FaceGeneration = d.BodyGeneration;
        d.Face = Task.Run(() => AvatarFaceSurfaces.Prepare(loaded.Body, def, shape, named, shapeless, types));
    }

    /// <summary>Takes a finished morphed body into the layer, at most <see cref="MorphHandOversPerFrame"/> a frame; a failed one leaves the avatar on the shared body.</summary>
    private void PumpMorph(DrawnAvatar d)
    {
        if (d.Morph is not { IsCompleted: true } task || _layer is null || d.Id < 0 || _handedThisFrame >= MorphHandOversPerFrame) return;
        d.Morph = null;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _layer.SetMorphed(d.Id, task.IsCompletedSuccessfully ? task.Result : null);
        d.Surfaces = task.IsCompletedSuccessfully ? task.Result : null;
        d.MorphHashes = Fingerprints(task.IsCompletedSuccessfully ? task.Result : null);
        double handMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        MorphHandOverMsLastFrame += handMs;
        _morphHandOverMsTotal += handMs;
        _morphHandOverMsMax = Math.Max(_morphHandOverMsMax, handMs);
        MorphHandOvers++;
        _handedThisFrame++;
    }

    /// <summary>The camera the bakes are sized for (the avatar's pixel area is each bake's size claim, as the official viewer's: <see cref="AvatarBakeSizing"/>).
    /// Null: every bake at its full size.</summary>
    public Viewer.Core.Residency.ResidencyCamera? BakeView { get; set; }

    /// <summary>Test only (the negative control for the arrival check): bakes are decoded whole and wrapped, mipmapped and uploaded on the main
    /// thread as many a frame as are ready, at full size, as before bakes were prepared off the main thread. False in the viewer.</summary>
    internal bool LegacyBakeUploads { get; init; }

    /// <summary>Test only (the negative control for the held-once check): a bake slot a worn mesh or its own alpha hides still gets a texture of this presenter's, as
    /// before such slots were left without one. False in the viewer.</summary>
    internal bool KeepHiddenBakeTextures { get; init; }

    /// <summary>The most main-thread time, in milliseconds, a frame starts bake uploads in (one upload always may start): an upload is the engine
    /// image's GPU copy of a prepared level, a millisecond or two for a 1024 square; the rest of the frame's 4 ms is the morphed bodies' hand-over and the poses.</summary>
    public const double BakeUploadBudgetMs = 2.0;

    /// <summary>A bake that has gone to a finer level is not coarsened again for this long, so an avatar passing at a distance does not thrash.</summary>
    public const double BakeCoarsenDwellSeconds = 1.0;

    private int _bakeUploadsThisFrame;
    private double _bakeUploadMsThisFrame;

    /// <summary>Bake uploads since this presenter was made, the longest one's main-thread time, and how often a bake changed level.</summary>
    public int BakeUploads { get; private set; }
    public int BakeLevelChanges { get; private set; }
    public double BakeUploadMsMax { get; private set; }
    public double BakeUploadMsTotal { get; private set; }

    /// <summary>The discard level this avatar's bake in <paramref name="slot"/> is at on the GPU (-1: none).</summary>
    public int BakeLevelOf(AvatarEntry e, string slot) => e.Handle is DrawnAvatar d && d.Levels.TryGetValue(slot, out var l) ? l.Level : -1;

    /// <summary>The GPU bytes of the bakes now on the avatars (each with its mip chain, at the level it is at).</summary>
    public long BakeGpuBytes => Scene.Entries.Sum(e => e.Handle is DrawnAvatar d
        ? d.Bakes.Where(kv => kv.Value.Texture is not null).Sum(kv => d.Levels.TryGetValue(kv.Key, out var l) && l.Level >= 0
            ? Viewer.Core.Residency.TextureFootprint.Bytes(Viewer.Core.Imaging.TextureLevels.Size(l.FullW, l.Level), Viewer.Core.Imaging.TextureLevels.Size(l.FullH, l.Level), true) : 0L)
        : 0L);

    /// <summary>Every bake of every avatar that is available is on its body at the level its avatar's size wants, and none is still being asked for.</summary>
    public bool BakesAtWantedLevels
    {
        get
        {
            if (LegacyBakeUploads) return true;
            foreach (var e in Scene.Entries)
            {
                if (e.Handle is not DrawnAvatar d) continue;
                double claim = double.NaN;
                foreach (var kv in d.Bakes)
                {
                    if (kv.Value.Done && kv.Value.Texture is null) continue;   // not available
                    if (kv.Value.Texture is null) return false;                // still on its way
                    if (!d.Levels.TryGetValue(kv.Key, out var lv) || lv.Asked >= 0) return false;
                    if (double.IsNaN(claim)) claim = BakeClaim(e);
                    if (lv.Level != AvatarBakeSizing.WantedLevel(lv.FullW, lv.FullH, lv.Levels, claim)) return false;
                }
            }
            return true;
        }
    }

    /// <summary>The size claim of an avatar's bakes: its pixel area from <see cref="BakeView"/> (0 for another avatar out of view; the viewer's own is always drawn),
    /// or without a view an unbounded claim, which wants the full picture.</summary>
    private double BakeClaim(AvatarEntry e)
    {
        if (BakeView is not { } cam) return double.MaxValue;
        if (!Scene.Place(e, out var soles, out _)) return 0;
        float height = e.Size.Z > 0.1f ? e.Size.Z : AvatarScene.DefaultHeight;
        var size = e.Size.Z > 0.1f ? e.Size : new System.Numerics.Vector3(0.45f, 0.6f, height);
        var centre = soles + new System.Numerics.Vector3(0, 0, height / 2);
        return e.IsSelf
            ? AvatarBakeSizing.PixelArea(cam.Position, centre, size, cam.ViewportHeight, cam.FovY, cam.Aspect)
            : AvatarBakeSizing.ClaimFor(cam, centre, size);
    }

    private void UpdateBakes(AvatarEntry e, DrawnAvatar d)
    {
        if (d.BakesVersion != e.BakesVersion)
        {
            // the new set: slots whose bake is unchanged keep their texture; the rest are asked for
            var keep = new Dictionary<string, (Guid, Texture2D?, bool)>();
            foreach (var slot in Slots)
                if (e.Bakes.TryGetValue(slot, out var id))
                    keep[slot] = d.Bakes.TryGetValue(slot, out var had) && had.Id == id && !d.SkippedForWorn.Contains(slot) ? had : (id, null, false);
            var dropped = d.Bakes.Where(kv => !keep.TryGetValue(kv.Key, out var k) || k.Item2 != kv.Value.Texture).Select(kv => kv.Value).ToList();
            if (!e.IsSelf && OfficialBodyRules)
                foreach (var kv in d.Bakes)
                    if (kv.Value.Texture is { } oldTex && keep.TryGetValue(kv.Key, out var next) && next.Item2 is null)
                    {
                        // the slot names a new bake: the old one stays on the part until the new one is drawn (the official last-known-good bake)
                        if (d.LastGood.TryGetValue(kv.Key, out var older)) FreeBake(older.Id, older.Texture);
                        d.LastGood[kv.Key] = (kv.Value.Id, oldTex);
                        dropped.Remove(kv.Value);
                    }
            List<(Guid Id, Texture2D Texture)>? noLonger = null;
            foreach (var slot in d.LastGood.Keys.ToList())
                if (!keep.ContainsKey(slot) && d.LastGood.Remove(slot, out var gone)) (noLonger ??= new()).Add(gone);   // the slot names no bake now: the part is not drawn
            foreach (var slot in d.Levels.Keys.ToList())
                if (!keep.TryGetValue(slot, out var k) || k.Item2 is null)
                {
                    // a level fetched for the old picture and not yet uploaded goes with it
                    if (d.Levels[slot].Asked >= 0 && d.Bakes.TryGetValue(slot, out var old)) _textures?.ReleaseLevel(old.Id, d.Levels[slot].Asked);
                    d.Levels.Remove(slot);   // a new picture starts again from its first decode
                }
            d.Bakes.Clear();
            foreach (var kv in keep) d.Bakes[kv.Key] = kv.Value;
            d.BakesVersion = e.BakesVersion;
            ApplyBakes(e, d);   // the materials of dropped bakes go before their textures
            foreach (var b in dropped) FreeBake(b.Id, b.Texture);
            if (noLonger is not null) foreach (var b in noLonger) FreeBake(b.Id, b.Texture);
        }
        if (_textures is null) return;
        bool changed = false;
        List<Texture2D>? retired = null;
        List<string>? lastGoodDone = null;
        double claim = double.NaN;
        foreach (var slot in d.Bakes.Keys.ToList())
        {
            var (id, tex, done) = d.Bakes[slot];
            if (LegacyBakeUploads)
            {
                if (done) continue;
                var a0 = _textures.TryGet(id, out var whole);
                if (a0 == MeshAvailability.Pending) continue;
                if (a0 == MeshAvailability.Ready && whole is not null)
                {
                    if (BakeAlpha.HidesRegion(whole.Rgba)) d.Transparent.Add(slot); else d.Transparent.Remove(slot);   // a bake with no drawn pixel hides its region
                    var legacy = Image.CreateFromData(whole.Width, whole.Height, false, Image.Format.Rgba8, whole.Rgba);
                    legacy.GenerateMipmaps();
                    tex = ImageTexture.CreateFromImage(legacy);
                    legacy.Dispose();
                    _residency?.Pin(id, Viewer.Core.Residency.TextureFootprint.Bytes(whole.Width, whole.Height, true));
                    changed = true;
                }
                _textures.Release(id);   // the GPU has it; no CPU copy is kept
                d.Bakes[slot] = (id, tex, true);
                continue;
            }
            if (id == BaseBodyRule.Invisible && OfficialBodyRules) { if (!done) { d.Bakes[slot] = (id, null, true); changed = true; } continue; }   // the invisible texture: the part is not drawn, nothing to fetch
            if (!d.Levels.TryGetValue(slot, out var lv)) d.Levels[slot] = lv = new BakeLevel();
            if (d.Worn.Contains(slot) && !KeepHiddenBakeTextures) { MaskOnly(d, slot, id, tex, lv); continue; }   // a worn mesh draws this bake itself: no texture of ours
            if (d.SkippedForWorn.Remove(slot))
            {
                // the mesh is gone: the part is drawn again, so the bake is loaded
                d.Bakes[slot] = (id, null, false);
                tex = null; done = false;
            }
            if (done && tex is null) continue;   // asked for and not available (or its own alpha hides the part): the default skin stays
            if (double.IsNaN(claim)) claim = BakeClaim(e);
            int wanted = AvatarBakeSizing.WantedLevel(lv.FullW, lv.FullH, lv.Levels, claim);
            int ask = lv.Asked;   // a level already asked for is finished first (its decode would otherwise sit unclaimed in the source)
            if (ask < 0)
            {
                if (tex is not null && lv.Level == wanted) continue;
                if (tex is not null && wanted > lv.Level && _now() - lv.ChangedAt < BakeCoarsenDwellSeconds) continue;   // coarsening waits a moment
                ask = wanted;
            }
            MeshAvailability availability;
            Viewer.Core.Imaging.DecodedTexture? decoded;
            object? prepared;
            using (FrameProbe.Begin(FrameProbe.AvBakeFetch)) availability = _textures.TryGetLevel(id, ask, out decoded, out prepared);
            lv.Asked = ask;
            if (availability == MeshAvailability.Pending) continue;
            if (availability != MeshAvailability.Ready || decoded is null)
            {
                _textures.ReleaseLevel(id, ask);
                lv.Asked = -1;
                d.Bakes[slot] = (id, tex, true);   // not available: no retry, the default skin stays
                continue;
            }
            // the level is ready off the main thread; the GPU copy is the main thread's, a time budget a frame
            if (_bakeUploadsThisFrame > 0 && (_bakeUploadMsThisFrame >= BakeUploadBudgetMs || (_budget is not null && _budget.RemainingMs <= ReserveAfterMs))) continue;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            lv.FullW = decoded.FullWidth; lv.FullH = decoded.FullHeight; lv.Levels = decoded.LevelCount;
            lv.Mask.Offer(decoded.Width, decoded.Height, decoded.Alpha, prepared is PreparedUpload pu0 ? pu0.HidesAtMask : BakeAlphaMask.HidesAt(decoded.Rgba, decoded.Width, decoded.Height, decoded.FullWidth, decoded.FullHeight));
            if (!lv.Mask.Known && !SettleMask(id, lv)) continue;   // a coarse level of a bake with alpha: the mask's own level is on its way
            if (!KeepHiddenBakeTextures && !BakeAlphaMask.NeedsGpuTexture(false, lv.Mask))
            {
                // its own alpha hides the part: nothing is drawn from it, so it gets no texture (and one it had is let go once the materials stop naming it)
                _textures.ReleaseLevel(id, ask);
                lv.Asked = -1;
                d.Transparent.Add(slot);
                d.Bakes[slot] = (id, null, true);
                if (tex is not null) { (retired ??= new()).Add(tex); _residency?.Unpin(id); }
                lv.Level = -1;
                changed = true;
                if (d.LastGood.ContainsKey(slot)) (lastGoodDone ??= new()).Add(slot);
                continue;
            }
            if (lv.Mask.Hides) d.Transparent.Add(slot); else d.Transparent.Remove(slot);   // (a hiding bake reaches here only in the negative control)
            var uploadProbe = FrameProbe.Begin(FrameProbe.AvBakeUpload);
            Image img;
            bool owned = false;
            if (prepared is PreparedUpload pu) img = pu.Image;
            else if (decoded.Mips is { } mips) { img = Image.CreateFromData(decoded.Width, decoded.Height, true, Image.Format.Rgba8, mips); owned = true; }
            else { img = Image.CreateFromData(decoded.Width, decoded.Height, false, Image.Format.Rgba8, decoded.Rgba); img.GenerateMipmaps(); owned = true; }
            var made = ImageTexture.CreateFromImage(img);
            if (owned) img.Dispose();
            _residency?.Pin(id, Viewer.Core.Residency.TextureFootprint.Bytes(decoded.Width, decoded.Height, true));
            lv.FullW = decoded.FullWidth; lv.FullH = decoded.FullHeight; lv.Levels = decoded.LevelCount;
            if (tex is not null) (retired ??= new()).Add(tex);
            _textures.ReleaseLevel(id, ask);   // the GPU has it; no CPU copy is kept
            lv.Asked = -1;
            if (tex is not null) BakeLevelChanges++;
            lv.Level = decoded.Level;
            lv.ChangedAt = _now();
            d.Bakes[slot] = (id, made, true);
            changed = true;
            if (d.LastGood.ContainsKey(slot)) (lastGoodDone ??= new()).Add(slot);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            uploadProbe.End();
            _bakeUploadsThisFrame++;
            _bakeUploadMsThisFrame += ms;
            BakeUploads++;
            BakeUploadMsTotal += ms;
            BakeUploadMsMax = Math.Max(BakeUploadMsMax, ms);
        }
        if (changed) ApplyBakes(e, d);
        if (lastGoodDone is not null)
            foreach (var slot in lastGoodDone) FreeLastGood(d, slot);   // the new bake is drawn now (or hides the part): the old one goes, after the materials stopped naming it
        if (retired is not null) foreach (var old in retired) old.Dispose();   // after the materials name the new textures
    }

    /// <summary>A slot whose body part a worn mesh hides: the mesh's own faces draw the bake (the prim layer holds it), so this presenter keeps no texture for it,
    /// only whether its alpha hides the part, decided from the mask's resolution. A texture it already had goes (the materials stopped naming it when the mesh
    /// was noticed, <see cref="UpdateWorn"/>).</summary>
    private void MaskOnly(DrawnAvatar d, string slot, Guid id, Texture2D? tex, BakeLevel lv)
    {
        d.SkippedForWorn.Add(slot);
        // a level fetched before the mesh was noticed and left for a later frame (the frame's time ran out) is no longer wanted
        if (lv.Asked >= 0 && _textures is not null) { _textures.ReleaseLevel(id, lv.Asked); lv.Asked = -1; }
        if (tex is not null)
        {
            d.Bakes[slot] = (id, null, true);
            FreeBake(id, tex);
            lv.Level = -1;
        }
        if (!lv.Mask.Known && !(_textures is not null && SettleMask(id, lv)))
        {
            if (_textures is null) d.Bakes[slot] = (id, null, true);   // no texture source: nothing will come
            return;   // the mask's level is on its way; the slot stays unsettled until it is decided
        }
        if (lv.Mask.Hides) d.Transparent.Add(slot); else d.Transparent.Remove(slot);
        d.Bakes[slot] = (id, null, true);
    }

    /// <summary>Decodes the level that decides the bake's alpha mask (a coarse level that arrived first could not). True once the mask is known; false while that level is on its way.</summary>
    private bool SettleMask(Guid id, BakeLevel lv)
    {
        int level = lv.Mask.LevelNeeded(lv.FullW, lv.FullH, lv.Levels) ?? 0;
        var availability = _textures!.TryGetLevel(id, level, out var decoded, out var prepared);
        if (availability == MeshAvailability.Pending) return false;
        if (availability == MeshAvailability.Ready && decoded is not null)
        {
            lv.FullW = decoded.FullWidth; lv.FullH = decoded.FullHeight; lv.Levels = decoded.LevelCount;
            lv.Mask.Offer(decoded.Width, decoded.Height, decoded.Alpha, prepared is PreparedUpload p ? p.HidesAtMask : BakeAlphaMask.HidesAt(decoded.Rgba, decoded.Width, decoded.Height, decoded.FullWidth, decoded.FullHeight));
            // a level as fine as the mask asks for that still cannot say (no pixels to read): taken to show
            if (!lv.Mask.Known && decoded.Level <= BakeAlphaMask.LevelFor(decoded.FullWidth, decoded.FullHeight, decoded.LevelCount))
                lv.Mask.Offer(decoded.Width, decoded.Height, Viewer.Core.Imaging.AlphaClass.Blended, false);
        }
        else lv.Mask.Offer(0, 0, Viewer.Core.Imaging.AlphaClass.Blended, false);   // not available: the bake is taken to show
        _textures.ReleaseLevel(id, level);
        return lv.Mask.Known;
    }

    private void ApplyBakes(AvatarEntry e, DrawnAvatar d)
    {
        if (_layer is null || d.Id < 0) return;
        using var probe = FrameProbe.Begin(FrameProbe.AvApplyBakes);
        var textures = new Dictionary<string, Texture2D>();
        var hidden = new HashSet<string>();
        var drawn = s_bodyFrames ? new List<string>() : null;
        foreach (var slot in Slots)
        {
            var bake = d.Bakes.TryGetValue(slot, out var b) ? b : default;
            Guid? named = e.Bakes.TryGetValue(slot, out var id) ? id : null;
            var draw = OfficialBodyRules
                ? BaseBodyRule.Decide(new BodyPartState(e.IsSelf, named, d.Worn.Contains(slot), d.Transparent.Contains(slot), bake.Texture is not null, d.LastGood.ContainsKey(slot)))
                : d.Worn.Contains(slot) || d.Transparent.Contains(slot) ? BodyPartDraw.Hidden : bake.Texture is not null ? BodyPartDraw.Bake : BodyPartDraw.DefaultSkin;
            if (draw == BodyPartDraw.Hidden) hidden.Add(slot);
            else if (draw == BodyPartDraw.Bake) textures[slot] = bake.Texture!;
            else if (draw == BodyPartDraw.LastGood) textures[slot] = d.LastGood[slot].Texture;
            drawn?.Add(slot + "=" + draw switch { BodyPartDraw.Hidden => "H", BodyPartDraw.Bake => "B", BodyPartDraw.LastGood => "L", _ => "D" });
        }
        _layer.SetBakes(d.Id, textures, hidden);
        if (drawn is not null) d.PartsDrawn = string.Join(" ", drawn);
    }

    /// <summary>Lets go of another avatar's last good bake of a slot (its new bake is drawn, or hides the part); the materials no longer name it.</summary>
    private void FreeLastGood(DrawnAvatar d, string slot)
    {
        if (d.LastGood.Remove(slot, out var old)) FreeBake(old.Id, old.Texture);
    }

    /// <summary>Whether another avatar's body follows the official viewer's rules (<see cref="BaseBodyRule"/>, <see cref="AvatarArrivalGate"/>, the removal hold
    /// and its uncover delay). On in the viewer, which gives no clock; a test scene gives its own clock (often a fixed one, with no texture source) and
    /// has it off unless it sets it, so scenes that check posing or attachments on stand-in bodies keep drawing them.</summary>
    internal bool OfficialBodyRules { get; init; }

    private void FreeBake(Guid id, Texture2D? texture)
    {
        if (texture is null) return;
        _residency?.Unpin(id);
        texture.Dispose();
    }

    private void UpdateClip(AvatarEntry e, DrawnAvatar d, double now)
    {
        if (_layer is null || d.Id < 0) return;
        Guid? chosen = null;
        AnimationClip? clip = null;
        bool motionsChanged = false;
        // the motions' clock is the avatar layer's own (time since the first update, with the walk cycle's playback skew), so a fade follows what is drawn
        d.Epoch ??= now;
        d.MotionClock = now - d.Epoch.Value + (d.Walk.Active ? d.Walk.Skew : 0);
        // the expression animations the region names are played as expressions: they need no animation file of their own
        d.Motions.Expressions.Signal(e.Playing.Select(p => (p.AnimationId, p.Sequence)), d.MotionClock);
        if (_animations is not null)
        {
            foreach (var id in AnimationChoice.Wanted(e.Playing)) _animations.TryGet(id, out _);
            chosen = AnimationChoice.Choose(e.Playing, id => _animations.TryGet(id, out var c) == MeshAvailability.Ready ? c!.Priority : null);
            if (chosen is { } cid) _animations.TryGet(cid, out clip);
            // the motions the region says play and that are at hand start, fade and stop as the official viewer's motion controller does them
            _signalled.Clear();
            foreach (var p in e.Playing)
                if (_animations.TryGet(p.AnimationId, out var pc) == MeshAvailability.Ready && pc is not null) _signalled.Add(new SignalledMotion(p.AnimationId, pc, p.Sequence));
            // when the region names no animation that is at hand, the built-in stand plays (the choice's fallback): it is the one motion, and it
            // eases out when a real one arrives
            if (_signalled.Count == 0 && chosen is { } fallback && clip is not null) _signalled.Add(new SignalledMotion(fallback, clip, 0));
            // the always-on motions start with the avatar, whoever it is (llvoavatar.cpp startDefaultMotions); a joint no animation turns starts from the skeleton's own rotation
            if (d.Defaults is null)
            {
                var rig = _layer.Rig;
                d.Motions.DefaultRotation = joint => rig.Find(joint) is var n and >= 0 ? rig.DefaultRotation[n] : null;
                d.Defaults = new AvatarDefaultMotions(e.AgentId.GetHashCode(), DefaultMotionSet.All & ~TestMotionsOff);
                d.Defaults.Start(d.Motions, d.MotionClock);
                if (e.IsSelf) d.Motions.MotionRanOut += id => SelfMotionRanOut?.Invoke(id);
                motionsChanged = true;
            }
            motionsChanged |= d.Motions.Update(d.MotionClock, _signalled);
        }
        bool chosenChanged = chosen != d.Clip;
        if (!motionsChanged && !chosenChanged) return;
        d.Clip = chosen;
        // a new mix replaces the old one on the same clock: an animation that goes on keeps its phase, and the cycle's playback skew stays
        using (FrameProbe.Begin(FrameProbe.AvClipSet)) _layer.SetClip(d.Id, d.Motions.Mix, -(d.Epoch ?? now), keepSkew: !chosenChanged);
        if (!chosenChanged) return;
        d.WalkMotion = chosen is { } walkId && clip is not null && AgentAnimations.IsWalkMotion(walkId);
        using (FrameProbe.Begin(FrameProbe.AvClipFeet)) d.Feet = d.WalkMotion ? FeetOf(clip!) : null;
        d.WalkWhy = d.WalkMotion && d.Feet is null
            ? _layer.Rig.Find("mAnkleLeft") < 0 || _layer.Rig.Find("mAnkleRight") < 0 ? "the skeleton has no ankle joints" : $"the clip has no cycle (duration {clip!.Duration}, loop {clip.Loop}, in {clip.LoopIn}, out {clip.LoopOut})"
            : null;
        d.Walk.Start(now, clip, d.Feet);   // a walk motion's rate starts from nothing, as the official viewer's walk adjust does
    }

    private readonly List<SignalledMotion> _signalled = new();

    /// <summary>Test only: a frame at the same clock time as the last one does not move a walk cycle's playback, so a picture does not depend on how many frames were run
    /// while the test's clock stood still. False in the viewer, whose clock always runs.</summary>
    internal bool HoldWalkWithClock { get; init; }

    /// <summary>Test-only (--tr-motions-off): built-in motions left out of every avatar, to see in a picture what each one moves. None in the viewer.</summary>
    public static DefaultMotionSet TestMotionsOff = DefaultMotionSet.None;

    /// <summary>One frame of an avatar's built-in motions: the flying roll follows the avatar's turn rate and speed while the region has it in the air (not sitting, no walk
    /// animation playing), and the hand pose follows the animations' hand poses. The mix is posed again when the roll starts or stops.</summary>
    private void StepDefaults(AvatarEntry e, DrawnAvatar d, double now, float yaw)
    {
        var v = e.Motion.Velocity(Scene.Now);
        bool walk = e.Playing.Any(p => AgentAnimations.IsWalkMotion(p.AnimationId));
        int? hand = HandMotion.Request(_signalled.OrderBy(s => s.Sequence).Select(s => (s.Clip.HandPose, s.Clip.Priority)));
        // the head motion keeps the head facing where the root does: it needs the pelvis's and the neck's parent's turn from the pose the last frame made
        var rig = _layer!.Rig;
        int pelvis = rig.Find("mPelvis"), neck = rig.Find("mNeck");
        int neckParent = neck >= 0 ? rig.Parent(neck) : -1;
        System.Numerics.Quaternion? pelvisTurn = pelvis >= 0 && _layer.TryJointRotation(d.Id, pelvis, out var pr) ? pr : null;
        System.Numerics.Quaternion? chestTurn = neckParent >= 0 && _layer.TryJointRotation(d.Id, neckParent, out var cr) ? cr : null;
        if (d.Defaults!.Step(d.Motions, d.MotionClock, yaw, v.Length(), walk, AgentAnimations.InAir(e.Playing), e.Seat != 0, hand, pelvisTurn, chestTurn))
            _layer!.SetClip(d.Id, d.Motions.Mix, -(d.Epoch ?? now), keepSkew: true);
        // a new hand pose is a new body (the official viewer blends the two poses' morphs over a fifth of a second; this one changes at once)
        if (d.Defaults.Hand.Wanted != d.HandPose && d.Morph is null) StartBodyMorph(d, d.BodyShape);
    }

    private readonly Dictionary<AnimationClip, FootSpeedTable?> _feet = new(ReferenceEqualityComparer.Instance);

    /// <summary>The planted foot's speed over the clip's cycle, worked out once per clip.</summary>
    private FootSpeedTable? FeetOf(AnimationClip clip)
    {
        if (_layer is null) return null;
        if (!_feet.TryGetValue(clip, out var table)) _feet[clip] = table = FootSpeedTable.Of(_layer.Rig, clip);
        return table;
    }

    private void FreeDeparted()
    {
        while (Scene.TakeDeparted(out var e))
        {
            if (e.Handle is not DrawnAvatar d) continue;
            if (WornFrameTrace.On) Main.Log.Info("worn.avatar-gone", CoreLog.F("frame", WornFrameTrace.Frame), CoreLog.F("local", e.Id.LocalId), CoreLog.F("agent", e.AgentId), CoreLog.F("users", d.Users));
            if (_host is not null && d.Users > 1)
            {
                // another region's entry still names this body: it stays drawn, and what it hides under worn meshes is worked out again
                d.Users--;
                if (ReferenceEquals(d.DriverE, e)) { d.DriverP = null; d.DriverE = null; }
                d.WornChecked = false;
                e.Handle = null;
                continue;
            }
            if (_host is not null && d.Id >= 0) _host.Unshare(e.AgentId, d);
            if (d.Id >= 0) _layer?.Remove(d.Id);
            foreach (var (slot, lv) in d.Levels)
                if (lv.Asked >= 0 && d.Bakes.TryGetValue(slot, out var asked)) _textures?.ReleaseLevel(asked.Id, lv.Asked);   // fetched, never uploaded
            foreach (var b in d.Bakes.Values)
            {
                if (LeakBakeForNegativeControl && _leakedBakes.Count < 2 && b.Texture is not null)
                {
                    _leakedBakes.Add(b.Texture);   // the negative control: kept, so the lifecycle check must name it
                    continue;
                }
                FreeBake(b.Id, b.Texture);
            }
            d.Bakes.Clear();
            foreach (var lg in d.LastGood.Values) FreeBake(lg.Id, lg.Texture);
            d.LastGood.Clear();
            e.Handle = null;
        }
    }

    /// <summary>Avatars drawn (a body each), bake textures, and the layer's engine resources.</summary>
    public int Drawn => _host is null ? _layer?.Count ?? 0 : Scene.Entries.Count(e => e.Handle is DrawnAvatar { Id: >= 0 } d && ReferenceEquals(d.DriverP, this) && ReferenceEquals(d.DriverE, e));

    /// <summary>Whether the user's own avatar is drawn by this presenter now.</summary>
    public bool SelfDrawn => AgentDrawn(Guid.Empty, self: true);

    /// <summary>Whether this presenter has the avatar of <paramref name="agent"/> (or the user's own with <paramref name="self"/>).</summary>
    public bool AgentDrawn(Guid agent, bool self = false) =>
        _layer is not null && Scene.Entries.Any(e => (self ? e.IsSelf : e.AgentId == agent) && e.Handle is DrawnAvatar { Id: >= 0 });

    /// <summary>Main-thread milliseconds the avatar layer's last frame spent on posing (working poses out, or giving them to workers) and on handing
    /// poses to the engine; for reports.</summary>
    public double PoseMsLastFrame => (_layer?.PoseMsLastFrame ?? 0) + (_layer?.UploadMsLastFrame ?? 0);
    public bool PosedOnWorkers => _layer?.PosedOnWorkersLastFrame ?? false;
    /// <summary>Avatars drawn from a body of their own (their shape morphs applied).</summary>
    public int Morphed => _layer is null ? 0 : Scene.Entries.Count(e => e.Handle is DrawnAvatar { Id: >= 0 } d && _layer.IsMorphed(d.Id));
    /// <summary>Avatars whose shaped skeleton has been worked out from their current visual parameters.</summary>
    public int Shaped => Scene.Entries.Count(e => e.Handle is DrawnAvatar { Id: >= 0 } d && e.VisualParams is { Count: > 0 } && d.ShapeVersion == e.ShapeVersion);
    /// <summary>Drawn avatars whose own body is still on its way: the shape has not reached the layer yet, the morphed body is still being prepared off the main
    /// thread (or waits for its hand-over), the hand pose the animations ask for is not the one the body was made with, or the animation it wants has not started (a clip that starts later starts at a later time on the clock). A picture taken while this is above
    /// zero shows the previous body, so it differs from run to run with the time the worker took.</summary>
    public int BodiesPending => Scene.Entries.Count(e => e.Handle is DrawnAvatar { Id: >= 0 } d
        && (d.Morph is not null || d.Face is not null || d.FaceDirty || d.ShapeVersion != e.ShapeVersion || (d.Defaults is { } df && df.Hand.Wanted != d.HandPose)
            || (_animations is not null && AnimationChoice.Choose(e.Playing, id => _animations.TryGet(id, out var c) == MeshAvailability.Ready ? c!.Priority : null) != d.Clip)));
    /// <summary>True when every drawn avatar shows the pose for the presenter's clock now (poses are worked out on workers and handed over later).</summary>
    public bool PosesCurrent => (_host is not null ? _host.Layer : _layer) is not { } l || l.PosesCurrent(_now());

    /// <summary>The body bake slots whose own bake has no drawn pixel for this avatar (an alpha bake hiding its region), without those a worn mesh hides.</summary>
    public string[] TransparentSlots(AvatarEntry e) => e.Handle is DrawnAvatar d ? d.Transparent.Order().ToArray() : [];
    public int BakeTextures => Scene.Entries.Sum(e => e.Handle is DrawnAvatar d ? d.Bakes.Values.Count(b => b.Texture is not null) : 0);
    /// <summary>Bake slots that are decided: on the body as a texture, or without one because a worn mesh or the bake's own alpha hides the part (or the bake is not available).</summary>
    public int BakeSlotsSettled => Scene.Entries.Sum(e => e.Handle is DrawnAvatar d ? d.Bakes.Values.Count(b => b.Texture is not null || b.Done) : 0);
    /// <summary>Whether this presenter holds a GPU texture for the bake <paramref name="id"/> (one an avatar's body is drawn with).</summary>
    public bool HoldsBakeTexture(Guid id) => Scene.Entries.Any(e => e.Handle is DrawnAvatar d && d.Bakes.Values.Any(b => b.Id == id && b.Texture is not null));
    public int LiveResources => (_layer is null ? 0 : _layer.LiveResources + 1) + BakeTextures;

    /// <summary>Everything made for the region's avatars, for the lifecycle check.</summary>
    internal IEnumerable<(string Kind, GodotObject Object)> EngineObjects()
    {
        if (_layer is not null && _host is null)
            foreach (var o in _layer.EngineObjects()) yield return o;
        if (_skin is not null)
            foreach (var o in _skin.EngineObjects()) yield return o;
        foreach (var e in Scene.Entries)
            if (e.Handle is DrawnAvatar d)
            {
                foreach (var b in d.Bakes.Values)
                    if (b.Texture is not null) yield return ("avatar bake texture", b.Texture);
                foreach (var lg in d.LastGood.Values) yield return ("avatar last good bake texture", lg.Texture);
            }
    }

    /// <summary>A development aid: what this presenter holds for one agent: the worn and transparent slots it hides, the animation it chose
    /// against the ones the region says play, and each bake slot's id with whether its picture is on the GPU.</summary>
    public object? ProbeOf(Guid agent)
    {
        var e = Scene.Entries.FirstOrDefault(x => x.AgentId == agent);
        if (e is null) return null;
        var d = e.Handle as DrawnAvatar;
        static string Short(Guid g) => g.ToString("N")[..8];
        return new
        {
            held = Scene.IsHeld(e), drawn = d is { Id: >= 0 },
            drives = _host is null || d is null || (ReferenceEquals(d.DriverP, this) && ReferenceEquals(d.DriverE, e)), host = _host is not null,
            roots_now = _world.AttachmentRoots(e.Id.LocalId).Count,
            hidden_now = BodyVisibility.HiddenSlots(_world.AttachmentRoots(e.Id.LocalId).SelectMany(r => _world.Linkset(r.Id).Prepend(r))
                .Where(x => x.Textures is not null).SelectMany(x => x.Textures!.Overrides.Select(f => f?.TextureId ?? x.Textures.Default.TextureId).Append(x.Textures.Default.TextureId))).Order().ToArray(),
            worn_checked = d?.WornChecked ?? false, users = d?.Users ?? 0,
            worn = d is null ? [] : d.Worn.Order().ToArray(), transparent = d is null ? [] : d.Transparent.Order().ToArray(),
            clip = d?.Clip is { } c ? Short(c) : null,
            clip_ready = d?.Clip is { } cc && _animations is not null && _animations.TryGet(cc, out _) == MeshAvailability.Ready,
            playing = e.Playing.Select(p => new { id = Short(p.AnimationId), name = AgentAnimations.Names.GetValueOrDefault(p.AnimationId), seq = p.Sequence,
                ready = _animations is not null && _animations.TryGet(p.AnimationId, out _) == MeshAvailability.Ready }).ToArray(),
            mixed = d is not null && d.Motions.Mix is not null && d.Motions.States(d.MotionClock).Count >= 2,
            weights = d is null ? [] : d.Motions.States(d.MotionClock).Select(m => new { id = Short(m.Id), inst = m.Instance, w = Math.Round(m.Weight, 3), stopped = m.Stopped }).ToArray(),
            motions = _animations is null ? [] : e.Playing.Select(p => _animations.TryGet(p.AnimationId, out var c) == MeshAvailability.Ready && c is not null
                ? new { id = Short(p.AnimationId), priority = c.Priority, duration = Math.Round(c.Duration, 2), loop = c.Loop, ease_in = c.EaseIn, joints = c.Tracks.Count,
                    names = string.Join(",", c.Tracks.Select(t => t.Joint + ":" + t.Priority).Take(40)) }
                : null).Where(x => x is not null).ToArray(),
            defaults = d?.Defaults is not { Started: true } df ? null : DefaultsProbe(d, df),
            bakes_version = d?.BakesVersion ?? -1, entry_bakes_version = e.BakesVersion,
            slots = d is null ? [] : d.Bakes.OrderBy(b => b.Key).Select(b => new { slot = b.Key, id = Short(b.Value.Id), on_gpu = b.Value.Texture is not null, done = b.Value.Done }).ToArray(),
        };
    }

    /// <summary>A development aid: what the avatar's built-in motions give now: the joints they turn as the mix poses them, the blink weights, the hand pose and the flying roll.</summary>
    private static object DefaultsProbe(DrawnAvatar d, AvatarDefaultMotions df)
    {
        double t = d.MotionClock;
        float[]? Turn(string joint)
        {
            var track = d.Motions.Mix?.Tracks.FirstOrDefault(x => x.Joint == joint);
            if (track is null || track.RotationTimes.Length == 0) return null;
            var q = AnimationClip.RotationAt(track, (float)t);
            return [MathF.Round(q.X, 5), MathF.Round(q.Y, 5), MathF.Round(q.Z, 5), MathF.Round(q.W, 5)];
        }
        var (bl, br) = df.Eye.Blink(t);
        var (wy, wp) = df.Eye.Wander(t);
        return new
        {
            t = Math.Round(t, 3),
            running = new[] { "head_rot", "eye_rot", "body_noise", "breathe_rot", "pelvis_fix", "fly_adjust" }.Where(n => d.Motions.IsRunning(n, t)).ToArray(),
            chest = Turn("mChest"), torso = Turn("mTorso"), neck = Turn("mNeck"), head = Turn("mHead"), eye_left = Turn("mEyeLeft"), eye_right = Turn("mEyeRight"), pelvis = Turn("mPelvis"),
            blink = new[] { MathF.Round(bl, 3), MathF.Round(br, 3) }, wander = new[] { MathF.Round(wy, 4), MathF.Round(wp, 4) },
            hand = new { current = df.Hand.Current, wanted = df.Hand.Wanted, applied = d.HandPose, morph = HandMotion.MorphName(d.HandPose), blending = df.Hand.Blending },
            fly_roll = MathF.Round(df.Fly.Roll, 4),
        };
    }

    public object Describe() => new
    {
        avatars = Scene.Count, morph = new { hand_overs = MorphHandOvers, hand_over_ms_total = Math.Round(_morphHandOverMsTotal, 3), hand_over_ms_max = Math.Round(_morphHandOverMsMax, 3), prepare_ms_last = Math.Round(PrepareMs().Last, 3), prepare_ms_max = Math.Round(PrepareMs().Max, 3) }, drawn = Drawn, bake_textures = BakeTextures, live_resources = LiveResources,
        arrivals = Scene.Arrivals, departures = Scene.Departures, body = BodyMissing ?? (_body is { IsCompleted: false } ? "loading" : "ready"),
        animations = _animations is null ? null : new { ready = _animations.Ready, unavailable = _animations.Unavailable, in_flight = _animations.InFlight, waiting = _animations.Waiting, peak_in_flight = _animations.PeakInFlight, max_in_flight = _animations.MaxInFlight },
        each = Scene.Entries.Select(e =>
        {
            bool placed = Scene.Place(e, out var soles, out float yaw);
            var d = e.Handle as DrawnAvatar;
            return new
            {
                self = e.IsSelf, name = e.Name, placed, soles = new[] { soles.X, soles.Y, soles.Z }, yaw = MathF.Round(yaw, 3), seat = e.Seat,
                playing = e.Playing.Count, clip = d?.Clip?.ToString(), bakes = e.Bakes.Count, bakes_drawn = d?.Bakes.Values.Count(b => b.Texture is not null) ?? 0, hidden = d is null ? [] : d.Worn.Union(d.Transparent).Order().ToArray(),
                updates = e.Motion.Updates,
                walk = d is not { WalkMotion: true } ? null : new { active = d.Walk.Active, rate = MathF.Round(d.Walk.Rate, 3), skew_s = Math.Round(d.Walk.Skew, 3), foot_mean_ms = MathF.Round(d.Feet?.Mean ?? 0, 3), foot_peak_ms = MathF.Round(d.Feet?.Peak ?? 0, 3), why_not = d.WalkWhy },
                shape = d is { Id: >= 0 } && _layer is not null && _layer.TryShape(d.Id, out var male, out var hover, out var measure) ? new { applied = e.VisualParams is { Count: > 0 }, male, hover_m = MathF.Round(hover, 3), height_m = MathF.Round(measure?.Height ?? 0, 3), height_ft = MathF.Round((measure?.Height ?? 0) / 0.3048f, 2), firestorm_height_m = MathF.Round(measure?.FirestormHeight ?? 0, 3), firestorm_height_ft = MathF.Round(measure?.FirestormFeet ?? 0, 2), morphed = d is { Id: >= 0 } && _layer.IsMorphed(d.Id), pelvis_to_foot_m = MathF.Round(measure?.PelvisToFoot ?? 0, 3), params_count = e.VisualParams?.Count ?? 0 } : null,
            };
        }).ToArray(),
    };

    private double _animationUseAt = double.NegativeInfinity;

    public void Dispose()
    {
        _animations?.Uses.Release(this);
        Apply(new WorldChange(WorldChangeKind.RegionReset, new ObjectId(int.MaxValue, 0)));   // every entry departs and is freed
        _host?.Leave(this);
        if (_host is null) _layer?.Dispose();   // a shared body is the host's
        _layer = null;
        _building?.Dispose();
        _building = null;
        _skin?.Dispose();
        _skin = null;
    }
}
