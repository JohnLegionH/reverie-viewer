// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// Avatars drawn through the RenderingServer: one mesh for the body (one skinned surface per part, shared by every
/// avatar), and per avatar one skeleton (one bone per palette entry) and one instance with that skeleton attached. Each
/// frame <see cref="Frame"/> poses every avatar on the main thread and hands the engine its palette's skinning
/// transforms (one SkeletonBoneSetTransform per entry) and its place (one InstanceSetTransform): palette + 1 engine calls
/// an avatar. The engine skins on the GPU. Parts are drawn with the face shader (opaque; the eyelashes blended), white
/// and tinted until a bake is given. Avatars are added and removed by id at any time; <see cref="Dispose"/> frees what
/// is left. Main thread only.
/// </summary>
public sealed class AvatarLayer : IDisposable
{
    private sealed class Avatar
    {
        public required Rid Skeleton;
        public required Rid Instance;
        public required AvatarPose Pose;
        public double Phase;
        public double Skew;                         // seconds the clip's clock is moved off the real one by the walk cycle's playback rate
        public System.Numerics.Vector3 Position;
        public float Yaw;
        public bool Moved = true;
        public bool Visible = true;
        public List<Material> Materials = [];
        public required AvatarJoints Joints;       // the animated joints attachments follow
        public JointTrack?[] Tracks = [];
        public ConstraintState? Constraints;        // what the clip's constraints keep from frame to frame (hands and feet held to the ground), null for a clip with none
        public ConstraintPose? ConstraintsShown;    // the constraints' result as the joints were last posed (read by the worn objects that follow the joints)
        public AnimationClip? Clip;
        public System.Numerics.Matrix4x4 World = System.Numerics.Matrix4x4.Identity;   // the avatar's place in the region (SL axes)
        public required RiggedWear Wear;            // the rigged meshes it wears: their joint overrides and pelvis offset
        public long LastPosed = PoseSweep.Never;    // the frame it was last posed in (PoseSweep.Never: not yet)
        public int Row = -1;                        // its row of joint matrices for the skinning shader, while it wears a rigged mesh
        public float GroundGap;                     // how far the soles are above the terrain under them: the ground a clip's ground constraint pulls the pelvis to
        public float Lift;                          // the pelvis offset the instance was last placed with
        public AvatarShape? Shape;                  // its shaped skeleton (visual parameters); null: the skeleton as the file has it
        public IReadOnlySet<string> Hidden = new HashSet<string>();   // the bake slots whose body parts are not drawn
        public Rid MorphMesh;                       // the avatar's own body mesh while its shape morphs the body; default: it shares the layer's mesh
        public Rid[] Overrides = [];                // the override material of each surface (default: none), kept to set again when the mesh changes
        public bool Ready;                          // posed at least once: the instance is not drawn before, so a body never shows unposed
        public bool InFlight;                       // a pose job for it is with the workers
        public double PoseShown = double.NaN;       // the playing time of the pose last handed to the engine (NaN: none yet)
        public long Generation;                     // counts up whenever what a pose depends on changes (clip, shape, worn meshes): a job made before is dropped
        public bool Removed;
        public int Key;                             // its id in this layer (for the log)
        public JointOverrides? OverridesSnap;      // a copy of the worn meshes' overrides for the workers to read; null: made again when next needed
    }

    private readonly Rid _scenario;
    private readonly AvatarBody _body;
    private readonly Rid _mesh;
    private readonly List<Material> _materials = [];
    private readonly ImageTexture _white;
    private readonly Dictionary<int, Avatar> _avatars = new();
    private int _next;
    private readonly System.Numerics.Matrix4x4[] _skin;
    private readonly Dictionary<AnimationClip, AvatarPose> _poses = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AnimationClip, JointTrack?[]> _tracks = new(ReferenceEqualityComparer.Instance);
    private AvatarPose? _still;
    private JointTrack?[]? _stillTracks;
    private readonly SkinTextures? _skinTextures;
    private readonly AvatarShape _neutral;
    private readonly ConstraintSolver _constraints;
    private readonly float _neutralPelvisToFoot;
    private readonly System.Numerics.Matrix4x4[] _worlds;
    private PoseWorkers? _workers;
    private readonly PoseFailures _failures;

    /// <summary>Where a pose error is reported (once per avatar and cause). Default: the engine's error output.</summary>
    public Action<string>? PoseLog { get; set; }

    /// <summary>True (the default): with more drawn avatars than <see cref="MinPosedPerFrame"/> and a time budget, the poses and rows of joint matrices are worked out
    /// on worker threads from snapshots and the main thread only hands the finished ones to the engine. False: they are worked out on the main thread, as
    /// before (the control of the arrival checks, <c>--tr-alter=poseonmain</c>).</summary>
    public static bool PoseOnWorkers { get; set; } = true;

    private readonly IReadOnlyList<PreparedSkinnedSurface> _surfaces;
    private int _surfacesHanded;

    /// <summary>The body's surfaces prepared off the main thread (<see cref="Prepare"/>). With <paramref name="deferSurfaces"/> the layer is made without
    /// handing them to the engine: <see cref="Step"/> hands one over each call (the body of a first arrival is then made over several frames), and the
    /// layer is not used before it returns true.</summary>
    public AvatarLayer(Rid scenario, AvatarBody body, IReadOnlyList<PreparedSkinnedSurface> surfaces, SkinTextures? skin = null, bool deferSurfaces = false)
    {
        _scenario = scenario;
        _body = body;
        _failures = new PoseFailures(line => { if (PoseLog is { } log) log(line); else GD.PrintErr(line); });
        Rig = new AvatarRig(body.Skeleton);
        _skinTextures = skin;
        _constraints = new ConstraintSolver(Rig);
        _neutral =AvatarShape.Neutral(Rig);
        _neutralPelvisToFoot = _neutral.Measure(null)?.PelvisToFoot ?? 0f;
        _worlds = new System.Numerics.Matrix4x4[Rig.NodeCount + 1];
        _skin = new System.Numerics.Matrix4x4[body.Palette.Length];
        var img = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        img.Fill(Colors.White);
        _white = ImageTexture.CreateFromImage(img);
        img.Dispose();
        _mesh = Rids.MeshCreate("avatar mesh");
        _surfaces = surfaces;
        if (!deferSurfaces) while (!Step()) { }
    }

    /// <summary>Hands the engine the next of the body's surfaces and makes its material; true once every surface is in (and on every call after).
    /// Without <c>deferSurfaces</c> the constructor has done it all.</summary>
    public bool Step()
    {
        if (_surfacesHanded < _surfaces.Count)
        {
            int i = _surfacesHanded++;
            HandOver(_mesh, _surfaces[i]);
            var m = PartMaterial(_body.Parts[i], _white, _body.Parts[i].BakeSlot == "eyes" ? Colors.White : SkinTone);   // an eye is white until its bake arrives, not skin-coloured
            _materials.Add(m);
            RenderingServer.MeshSurfaceSetMaterial(_mesh, i, m.GetRid());
        }
        return _surfacesHanded >= _surfaces.Count;
    }

    /// <summary>The default body's colour before any bake (a neutral skin tone, sRGB).</summary>
    /// <summary>The colour of another avatar whose look never arrived: plain grey, as the official viewer shows a missing look.</summary>
    public static readonly Color MissingLookGrey = new(0.6f, 0.6f, 0.6f);
    public static readonly Color SkinTone = new(0.80f, 0.66f, 0.56f);

    /// <summary>The body as every avatar shares it: its hands in the relaxed pose, which is the pose an avatar's hands take unless an animation asks for another (llhandmotion.cpp: with
    /// no pose asked for the hand motion shows Hands_Relaxed). The body is as its mesh files have it where they carry no such morph.</summary>
    public static PreparedSkinnedSurface[] Prepare(AvatarBody body)
    {
        var hands = body.Shape is { } def ? BodyMorph.Compute(body, def, AvatarShape.Neutral(new AvatarRig(body.Skeleton)), HandWeights(HandMotion.Relaxed), onlyNamed: true) : null;
        return body.Parts.Select((p, i) => hands is null ? AvatarSurfaceData.Encode(p, body.Palette.Length) : AvatarSurfaceData.Encode(p, hands[i], body.Palette.Length)).ToArray();
    }

    /// <summary>The body's surfaces as its mesh files have them, with no morph applied (what the packing checks compare against the engine's own packing of the same arrays).</summary>
    public static PreparedSkinnedSurface[] PrepareUnshaped(AvatarBody body) => body.Parts.Select(p => AvatarSurfaceData.Encode(p, body.Palette.Length)).ToArray();

    /// <summary>The morph weights that put the hands in <paramref name="pose"/> (null for the spread pose, which is the mesh as it is).</summary>
    public static IReadOnlyDictionary<string, float>? HandWeights(int pose) => HandMotion.MorphName(pose) is { } name ? new Dictionary<string, float> { [name] = 1f } : null;

    public int Count => _avatars.Count;

    /// <summary>The body's skeleton as a node list (joints, then attachment points): what attachments name.</summary>
    public AvatarRig Rig { get; }
    public int PaletteSize => _body.Palette.Length;
    public int Surfaces => _body.Parts.Count;

    /// <summary>Engine calls made by the last <see cref="Frame"/>, and its main-thread time posing (the clips evaluated,
    /// the transforms made) and handing over (the engine calls), in milliseconds.</summary>
    public int CallsLastFrame { get; private set; }
    public double PoseMsLastFrame { get; private set; }
    public double UploadMsLastFrame { get; private set; }

    /// <summary>Adds an avatar standing at <paramref name="position"/> (SL region metres, the soles' height) facing
    /// <paramref name="yaw"/> radians about up from east; <paramref name="clip"/> null keeps the default pose.
    /// <paramref name="bakes"/>: a texture per bake slot ("head", "upper", "lower", "eyes", "hair"). Returns the avatar's
    /// id (ids count up from 0 and are never reused).</summary>
    public int Add(System.Numerics.Vector3 position, float yaw, AnimationClip? clip, double phase, IReadOnlyDictionary<string, Texture2D>? bakes = null)
    {
        var skel = Rids.SkeletonCreate("avatar skeleton");
        RenderingServer.SkeletonAllocateData(skel, _body.Palette.Length);
        var inst = Rids.InstanceCreate2(_mesh, _scenario, "avatar instance");
        RenderingServer.InstanceSetVisible(inst, false);   // drawn from its first pose on (MakeReady)
        RenderingServer.InstanceSetLayerMask(inst, GlowPass.NotInGlowLayer);   // not part of the glow picture
        RenderingServer.InstanceAttachSkeleton(inst, skel);
        // the skinned body moves within its box: a fixed box around the root (2.4 m tall, 2 m wide) keeps culling simple
        RenderingServer.InstanceSetCustomAabb(inst, new Aabb(new Vector3(-1f, -1.4f, -1f), new Vector3(2f, 2.6f, 2f)));
        var wear = new RiggedWear(Rig);
        var a = new Avatar { Skeleton = skel, Instance = inst, Pose = PoseFor(clip), Phase = phase, Position = position, Yaw = yaw, Joints = new AvatarJoints(Rig) { Overrides = wear.Overrides }, Clip = clip, Wear = wear };
        a.Tracks = TracksFor(clip);
        if (bakes is not null) ApplyBakes(a, bakes);
        int id = _next++;
        a.Key = id;
        _avatars[id] = a;
        return id;
    }

    private AvatarPose PoseFor(AnimationClip? clip) =>
        clip is null ? (_still ??= new AvatarPose(_body, null)) : clip.Transient ? new AvatarPose(_body, clip) : (_poses.TryGetValue(clip, out var p) ? p : _poses[clip] = new AvatarPose(_body, clip));   // a mix made for one avatar is not kept

    private JointTrack?[] TracksFor(AnimationClip? clip) =>
        clip is null ? (_stillTracks ??= AvatarJoints.TracksFor(Rig, null)) : clip.Transient ? AvatarJoints.TracksFor(Rig, clip) : (_tracks.TryGetValue(clip, out var t) ? t : _tracks[clip] = AvatarJoints.TracksFor(Rig, clip));

    /// <summary>Gives the avatar its shaped skeleton (null: the skeleton as the file has it). The default body and the joints the worn meshes and attachments follow both take it from the next <see cref="Frame"/>.</summary>
    public void SetShape(int avatar, AvatarShape? shape)
    {
        var a = _avatars[avatar];
        a.Shape = shape;
        a.Joints.Shape = shape;
        a.LastPosed = PoseSweep.Never;   // posed in the next frame whatever the budget: a change of shape shows at once
        a.Generation++;                  // and a pose being worked out on a worker with the old shape is dropped
    }

    /// <summary>The avatar's shape as drawn: male or female, the hover height, and the body height and pelvis-to-foot figures with the worn meshes' joint overrides applied.
    /// The figures are the skeleton's own while no visual parameters have arrived. For reports and checks.</summary>
    /// <summary>How far above the soles it is given (<see cref="Move"/>) the avatar's pelvis is drawn at rest: the stock body's
    /// pelvis-to-foot height plus the avatar's lift (its shape's longer or shorter legs, its hover and any worn mesh's pelvis offset).
    /// False for an avatar that is not here.</summary>
    public bool TryPelvisAboveSoles(int avatar, out float height)
    {
        height = 0f;
        if (!_avatars.TryGetValue(avatar, out var a)) return false;
        height = _body.Skeleton.Joints[0].Position.Z - _body.FootHeight + a.Lift;
        return true;
    }

    public bool TryShape(int avatar, out bool male, out float hover, out BodyMeasure? measure)
    {
        male = false; hover = 0f; measure = null;
        if (!_avatars.TryGetValue(avatar, out var a)) return false;
        var shape = a.Shape ?? _neutral;
        male = shape.Male; hover = shape.Hover;
        measure = shape.Measure(a.Wear.Overrides.MeshCount > 0 ? a.Wear.Overrides : null);
        return true;
    }

    /// <summary>The shaped skeleton the avatar is posed with now (null: none given, or not here).</summary>
    public AvatarShape? ShapeOf(int avatar) => _avatars.TryGetValue(avatar, out var a) ? a.Shape : null;

    /// <summary>The rigged meshes the avatar wears and the joints they move; null when the avatar is not here.</summary>
    public RiggedWear? WearOf(int avatar) => _avatars.TryGetValue(avatar, out var a) ? a.Wear : null;

    public bool Contains(int avatar) => _avatars.ContainsKey(avatar);

    /// <summary>Whether the avatar is shown now (the checks that count drawn bodies read this).</summary>
    public bool IsVisible(int avatar) => _avatars.TryGetValue(avatar, out var a) && a.Visible;

    /// <summary>Where the avatar is drawn: its feet, in the region's frame (SL axes), as the last <see cref="Frame"/> placed it. False when it is not drawn.</summary>
    public bool TryDrawnSoles(int avatar, out System.Numerics.Vector3 soles)
    {
        soles = default;
        if (!_avatars.TryGetValue(avatar, out var a) || !a.Visible) return false;
        soles = new System.Numerics.Vector3(a.World.M41, a.World.M42, a.World.M43 - a.Lift - (_body.Skeleton.Joints[0].Position.Z - _body.FootHeight));
        return true;
    }

    /// <summary>The world matrix of a node of the avatar's skeleton as the last <see cref="Frame"/> posed it: the node's own frame
    /// to the region's (SL axes, row vectors, metres). False for an avatar that is not drawn. Main thread, after <see cref="Frame"/>.</summary>
    public bool TryNodeWorld(int avatar, int node, out System.Numerics.Matrix4x4 world)
    {
        world = default;
        if (!_avatars.TryGetValue(avatar, out var a) || !a.Visible || node < 0 || node >= Rig.NodeCount) return false;
        world = a.Joints.World(node) * a.World;
        return true;
    }

    /// <summary>The height of a node of the avatar's skeleton where the official viewer puts it (in the region's frame, last pose): the
    /// avatar's soles (its place less half its height) plus the node's height over the pelvis at rest, plus the pelvis's height over the
    /// soles that its shape, hover and worn meshes give it (<see cref="FootPlacement"/>), so a foot joint of any shape rests on the soles.
    /// False for an avatar that is not here.</summary>
    public bool TryNodeOfficialHeight(int avatar, int node, out float z)
    {
        z = 0;
        if (!_avatars.TryGetValue(avatar, out var a) || !a.Visible || node < 0 || node >= Rig.NodeCount) return false;
        float soles = a.World.M43 - a.Lift - (_body.Skeleton.Joints[0].Position.Z - _body.FootHeight);
        z = FootPlacement.NodeHeight(soles, a.Joints, node, _neutralPelvisToFoot + a.Lift);
        return true;
    }

    /// <summary>The rotation of a node of the avatar's skeleton in the avatar's own frame (the pelvis frame the body is drawn in) as the last pose put it, without its scale. False for
    /// an avatar that is not here. Main thread; the pose it reads is the one the attachments follow.</summary>
    public bool TryJointRotation(int avatar, int node, out System.Numerics.Quaternion rotation)
    {
        rotation = System.Numerics.Quaternion.Identity;
        if (!_avatars.TryGetValue(avatar, out var a) || node < 0 || node >= Rig.NodeCount) return false;
        if (!System.Numerics.Matrix4x4.Decompose(a.Joints.World(node), out _, out var q, out _)) return false;
        rotation = q;
        return true;
    }

    /// <summary>The textures the skinning of worn rigged meshes reads; null when this layer was made without them (no rigged mesh is drawn).</summary>
    public SkinTextures? Skin => _skinTextures;

    /// <summary>The avatar's rigged mesh object <paramref name="local"/> wears <paramref name="asset"/>: its joint overrides and pelvis offset
    /// take effect from the next <see cref="Frame"/>, and the avatar gets a row of joint matrices. False when the avatar is not here or no row
    /// is left.</summary>
    public bool WearRigged(int avatar, uint local, Guid asset, Viewer.Core.Meshing.MeshSkinBlock skin)
    {
        if (_skinTextures is null || !_avatars.TryGetValue(avatar, out var a)) return false;
        bool newRow = a.Row < 0;
        if (newRow && (a.Row = _skinTextures.AcquireRow()) < 0) return false;
        int before = a.Wear.Version;
        a.Wear.Add(local, asset, skin);
        // a piece that adds nothing to the joints (the same mesh is already worn: the one a region hands over to the next, or a mesh with no overrides) leaves the pose
        // and a pose being worked out as they are; the official viewer recomputes only when an override changed (addAttachmentOverridesForObject)
        if (!newRow && a.Wear.Version == before) return true;
        a.LastPosed = PoseSweep.Never;   // its joint overrides apply in the next pose, which is not left to the budget's turn
        a.Generation++;
        a.OverridesSnap = null;
        return true;
    }

    /// <summary>The object no longer wears a rigged mesh; its overrides go, and the avatar's row when it wears no other.</summary>
    public void UnwearRigged(int avatar, uint local)
    {
        if (!_avatars.TryGetValue(avatar, out var a)) return;
        int before = a.Wear.Version;
        a.Wear.Remove(local);
        bool rowGoes = a.Wear.Objects == 0 && a.Row >= 0;
        if (rowGoes || a.Wear.Version != before)
        {
            a.LastPosed = PoseSweep.Never;
            a.Generation++;
            a.OverridesSnap = null;
        }
        if (rowGoes)
        {
            _skinTextures?.ReleaseRow(a.Row);
            a.Row = -1;
        }
    }

    /// <summary>The row of joint matrices and the place (SL axes, row vectors, metres; with the pelvis offset) a rigged mesh the avatar wears is
    /// skinned into, as the last <see cref="Frame"/> posed it. False for an avatar that is not drawn or has no row.</summary>
    public bool TryRigged(int avatar, out int row, out System.Numerics.Matrix4x4 world)
    {
        row = -1;
        world = default;
        if (!_avatars.TryGetValue(avatar, out var a) || !a.Visible || a.Row < 0) return false;
        row = a.Row;
        world = a.World;
        return true;
    }

    public void Move(int avatar, System.Numerics.Vector3 position, float yaw, float groundGap = 0f)
    {
        var a = _avatars[avatar];
        if (a.Position == position && a.Yaw == yaw && a.GroundGap == groundGap) return;
        a.Position = position;
        a.GroundGap = groundGap;
        a.Yaw = yaw;
        a.Moved = true;
    }

    /// <summary>Plays <paramref name="clip"/> (null: the default pose) from <paramref name="phase"/> seconds.</summary>
    public void SetClip(int avatar, AnimationClip? clip, double phase, bool keepSkew = false)
    {
        var a = _avatars[avatar];
        if (_failures.IsDropped(clip)) clip = null;   // a motion that failed once is not played again (the avatar stands in its default pose)
        a.Pose = PoseFor(clip);
        a.Clip = clip;
        a.Tracks = TracksFor(clip);
        a.Phase = phase;
        if (!keepSkew) a.Skew = 0;
        a.LastPosed = PoseSweep.Never;   // a newly chosen animation starts in the next frame
        a.Generation++;
    }

    /// <summary>Moves the clock of the avatar's clip <paramref name="skew"/> seconds off the real one (the walk cycle's playback rate).</summary>
    public void SetPlayingSkew(int avatar, double skew) => _avatars[avatar].Skew = skew;

    /// <summary>Shows or hides an avatar (a seated avatar whose seat is not known yet is hidden).</summary>
    public void SetVisible(int avatar, bool visible)
    {
        var a = _avatars[avatar];
        if (a.Visible == visible) return;
        a.Visible = visible;
        RenderingServer.InstanceSetVisible(a.Instance, visible && a.Ready);
    }

    /// <summary>Textures the avatar's parts with <paramref name="bakes"/> (by bake slot); a part whose slot has none keeps the
    /// default skin. The materials of its previous bakes are freed (the textures are the caller's).</summary>
    public void SetBakes(int avatar, IReadOnlyDictionary<string, Texture2D> bakes, IReadOnlySet<string>? hidden = null, IReadOnlySet<string>? grey = null)
    {
        var a = _avatars[avatar];
        for (int i = 0; i < _body.Parts.Count; i++) RenderingServer.InstanceSetSurfaceOverrideMaterial(a.Instance, i, default);
        foreach (var m in a.Materials) m.Dispose();
        a.Materials.Clear();
        a.Hidden = hidden ?? new HashSet<string>();
        ApplyBakes(a, bakes, grey);
    }

    /// <summary>The render layer a body is moved to while it is drawn as a mask (<see cref="SetMask"/>): no other instance uses it.</summary>
    public const uint MaskLayer = 1u << 19;

    /// <summary>Draws the avatar's body with <paramref name="mask"/> on every part it draws, on <see cref="MaskLayer"/> only, so a camera that sees
    /// only that layer pictures the body's outline and nothing else; null puts its own materials and layer back. Parts that are not drawn
    /// stay undrawn unless <paramref name="hiddenToo"/> is set (then the whole body shows). False when there is no such avatar. Main thread.</summary>
    public bool SetMask(int avatar, Material? mask, bool hiddenToo = false)
    {
        if (!_avatars.TryGetValue(avatar, out var a)) return false;
        RenderingServer.InstanceSetLayerMask(a.Instance, mask is null ? GlowPass.NotInGlowLayer : MaskLayer);
        var skip = KeptShaders.SkipMaterial.GetRid();
        for (int i = 0; i < _body.Parts.Count; i++)
        {
            var own = i < a.Overrides.Length ? a.Overrides[i] : default;
            RenderingServer.InstanceSetSurfaceOverrideMaterial(a.Instance, i, mask is null || (own == skip && !hiddenToo) ? own : mask.GetRid());
        }
        return true;
    }

    private void ApplyBakes(Avatar a, IReadOnlyDictionary<string, Texture2D> bakes, IReadOnlySet<string>? grey = null)
    {
        a.Overrides = new Rid[_body.Parts.Count];
        for (int i = 0; i < _body.Parts.Count; i++)
        {
            if (a.Hidden.Contains(_body.Parts[i].BakeSlot))
                a.Overrides[i] = KeptShaders.SkipMaterial.GetRid();   // the part is not drawn
            else if (bakes.TryGetValue(_body.Parts[i].BakeSlot, out var tex))
            {
                var m = PartMaterial(_body.Parts[i], tex, Colors.White);
                a.Materials.Add(m);
                a.Overrides[i] = m.GetRid();
            }
            else if (grey is not null && grey.Contains(_body.Parts[i].BakeSlot))
            {
                var m = PartMaterial(_body.Parts[i], _white, MissingLookGrey);   // the look never came: the default shape in plain grey, no skin texture
                a.Materials.Add(m);
                a.Overrides[i] = m.GetRid();
            }
            else continue;
            RenderingServer.InstanceSetSurfaceOverrideMaterial(a.Instance, i, a.Overrides[i]);
        }
    }

    /// <summary>The body's surfaces with an avatar's shape morphs applied, prepared off the main thread (reads the body only); null when the
    /// shape morphs nothing, so the avatar keeps the layer's shared mesh.</summary>
    public static PreparedSkinnedSurface[]? PrepareMorphed(AvatarBody body, ShapeDefinition def, AvatarShape shape, int handPose = HandMotion.Relaxed, bool shapeless = false,
        IReadOnlyDictionary<string, float>? face = null)
    {
        // an avatar whose hands are not relaxed needs its own body even when its shape morphs nothing, since the shared body has relaxed hands
        // the face's expression weights (by name) ride along with the hand pose's
        var morphed = BodyMorph.Compute(body, def, shape, AvatarFaceSurfaces.Named(HandWeights(handPose), face), onlyNamed: shapeless);
        if (morphed is null) return handPose == HandMotion.Relaxed ? null : body.Parts.Select(p => AvatarSurfaceData.Encode(p, body.Palette.Length)).ToArray();
        return body.Parts.Select((p, i) => AvatarSurfaceData.Encode(p, morphed[i], body.Palette.Length)).ToArray();
    }

    /// <summary>Gives the avatar its own body mesh made of <paramref name="surfaces"/> (<see cref="PrepareMorphed"/>); null returns it to the shared mesh.
    /// The previous own mesh is freed. Main thread; costs one engine hand-over per surface.</summary>
    public void SetMorphed(int avatar, PreparedSkinnedSurface[]? surfaces)
    {
        if (!_avatars.TryGetValue(avatar, out var a)) return;
        var old = a.MorphMesh;
        if (surfaces is null)
        {
            if (!old.IsValid) return;
            a.MorphMesh = default;
            RenderingServer.InstanceSetBase(a.Instance, _mesh);
        }
        else
        {
            var own = Rids.MeshCreate("avatar morphed mesh");
            for (int i = 0; i < surfaces.Length; i++)
            {
                HandOver(own, surfaces[i]);
                RenderingServer.MeshSurfaceSetMaterial(own, i, _materials[i].GetRid());
            }
            a.MorphMesh = own;
            RenderingServer.InstanceSetBase(a.Instance, own);
        }
        // a new base mesh starts with no override materials and no skeleton in some engine versions: set both again
        RenderingServer.InstanceAttachSkeleton(a.Instance, a.Skeleton);
        for (int i = 0; i < a.Overrides.Length; i++)
            if (a.Overrides[i].IsValid) RenderingServer.InstanceSetSurfaceOverrideMaterial(a.Instance, i, a.Overrides[i]);
        if (old.IsValid) Rids.Free(old);
    }

    /// <summary>True while the avatar draws its own morphed body mesh.</summary>
    public bool IsMorphed(int avatar) => _avatars.TryGetValue(avatar, out var a) && a.MorphMesh.IsValid;

    /// <summary>Frees one avatar's instance, skeleton and bake materials.</summary>
    public void Remove(int avatar)
    {
        if (!_avatars.Remove(avatar, out var a)) return;
        _failures.Forget(a);
        a.Removed = true;  // a pose being worked out for it is dropped when it comes back
        Free(a);
    }

    private void Free(Avatar a)
    {
        Rids.Free(a.Instance);
        Rids.Free(a.Skeleton);
        if (a.MorphMesh.IsValid) Rids.Free(a.MorphMesh);
        a.MorphMesh = default;
        if (a.Row >= 0) _skinTextures?.ReleaseRow(a.Row);
        a.Row = -1;
        foreach (var m in a.Materials) m.Dispose();
        a.Materials.Clear();
    }

    /// <summary>Engine resources held for avatars: an instance and a skeleton each, and their bake materials.</summary>
    public int LiveResources => _avatars.Values.Sum(a => 2 + (a.MorphMesh.IsValid ? 1 : 0) + a.Materials.Count);

    /// <summary>The materials this layer made (the body's own and every avatar's bake materials), for the lifecycle check.</summary>
    internal IEnumerable<(string Kind, GodotObject Object)> EngineObjects()
    {
        foreach (var m in _materials) yield return ("avatar body material", m);
        foreach (var a in _avatars.Values)
            foreach (var m in a.Materials) yield return ("avatar bake material", m);
        yield return ("avatar default skin", _white);
    }

    /// <summary>The fewest avatars a frame poses however little time is left (about a millisecond at most): a scene of a few avatars is posed whole in
    /// every frame, and a crowd's slowest avatar is posed within a bounded number of frames.</summary>
    public const int MinPosedPerFrame = 6;

    /// <summary>The floor of <see cref="MinPosedPerFrame"/> holds only while this much time has not gone in the frame's posing (one avatar is always posed), so a crowd the
    /// budget has no room for does not cost a frame six poses on top of everything else.</summary>
    public const double ForcedPoseCapMs = 1.0;

    /// <summary>The most avatars a frame gives the pose workers. Posing more often costs the engine's own skinning updates (measured: 100 avatars posed every frame
    /// took the render thread about a third longer than a quarter of them), so a crowd's avatars are posed in turn, a share of them each frame, as the main-thread budget did.</summary>
    public const int MaxPosesPerFrame = 25;

    private readonly PoseSweep _sweep = new();
    private Avatar[] _drawn = [];
    private long[] _stamps = [];
    private bool[] _busy = [];
    private int[] _plan = [];
    private long _frameNumber;
    private double _flushMsAverage;   // what handing the skin textures to the engine took lately: kept out of the posing budget

    /// <summary>Avatars posed by the last <see cref="Frame"/>, and of those drawn the ones that kept the pose of an earlier frame. On workers, "posed" counts
    /// the finished poses handed to the engine in the frame.</summary>
    public int PosedLastFrame { get; private set; }
    public int SkippedLastFrame { get; private set; }

    /// <summary>True while the last <see cref="Frame"/> had the poses worked out on worker threads, and the pose jobs not yet handed over.</summary>
    public bool PosedOnWorkersLastFrame { get; private set; }
    public int PoseJobsInFlight => _workers?.InFlight ?? 0;

    /// <summary>True when every avatar's pose handed to the engine is the one for <paramref name="seconds"/> and none is still being worked out.</summary>
    public bool PosesCurrent(double seconds) => _avatars.Values.All(a => !a.InFlight && a.PoseShown == seconds + a.Phase + a.Skew);

    /// <summary>Worker threads for posing: the processors beyond the two the main and render threads use, from one to three.</summary>
    private static int WorkerThreadCount() => Math.Clamp(System.Environment.ProcessorCount - 2, 1, 3);

    /// <summary>An avatar's first pose is in: the instance is drawn (it is not before, so a body never shows unposed).</summary>
    private void MakeReady(Avatar a)
    {
        if (a.Ready) return;
        a.Ready = true;
        if (a.Visible) RenderingServer.InstanceSetVisible(a.Instance, true);
    }

    /// <summary>Poses the avatars at <paramref name="seconds"/> (plus each one's own phase) and hands the engine the transforms. With
    /// <paramref name="poseBudgetMs"/> short of what posing every drawn avatar takes, the ones posed longest ago are posed first and the rest keep
    /// the pose they have until a later frame (<see cref="PoseSweep"/>); an avatar never posed always is, and one avatar always is. Every avatar's place
    /// follows its movement in every frame, posed or not. With more than <see cref="MinPosedPerFrame"/> avatars drawn and a budget, the poses are worked
    /// out on worker threads instead (<see cref="PoseOnWorkers"/>): each frame hands the engine the poses finished since the last, within the budget,
    /// and gives the workers the avatars that have none in hand.</summary>
    public void Frame(double seconds, double poseBudgetMs = double.PositiveInfinity)
    {
        long pose = 0, upload = 0;
        int calls = 0;
        // the root hangs the body from hip height: the instance sits that far above the soles
        float hip = _body.Skeleton.Joints[0].Position.Z - _body.FootHeight;
        _frameNumber++;
        int n = 0;
        foreach (var a in _avatars.Values)
        {
            if (!a.Visible) continue;
            if (n == _drawn.Length)
            {
                int size = Math.Max(16, n * 2);
                Array.Resize(ref _drawn, size); Array.Resize(ref _stamps, size); Array.Resize(ref _busy, size); Array.Resize(ref _plan, size);
            }
            _drawn[n] = a;
            _busy[n] = a.InFlight;
            _stamps[n++] = a.LastPosed;
        }
        bool all = double.IsPositiveInfinity(poseBudgetMs);
        poseBudgetMs = Math.Max(0, poseBudgetMs - _flushMsAverage);
        bool onWorkers = PoseOnWorkers && !all && n > MinPosedPerFrame;
        if (!onWorkers && _workers is { InFlight: > 0 })
        {
            // back on the main thread (a small scene, or no budget): what the workers have is finished and handed over first, so none is left behind
            upload += DrainWorkers(ref calls);
            for (int k = 0; k < n; k++) { _busy[k] = _drawn[k].InFlight; _stamps[k] = _drawn[k].LastPosed; }
        }
        PosedOnWorkersLastFrame = onWorkers;
        int posed;
        long started = Stopwatch.GetTimestamp();
        if (onWorkers) posed = PoseThroughWorkers(seconds, poseBudgetMs, n, ref calls, ref pose, ref upload);
        else posed = PoseHere(seconds, poseBudgetMs, n, all, started, ref calls, ref pose, ref upload);
        // every drawn avatar goes where it is now, whether or not it was posed this frame
        for (int k = 0; k < n; k++)
        {
            var a = _drawn[k];
            if (!a.Moved) continue;
            long t1 = Stopwatch.GetTimestamp();
            var p = a.Position;
            a.World = System.Numerics.Matrix4x4.CreateRotationZ(a.Yaw) * System.Numerics.Matrix4x4.CreateTranslation(p.X, p.Y, p.Z + hip + a.Lift);
            using (FrameProbe.Begin(FrameProbe.AvInstanceCall))
                RenderingServer.InstanceSetTransform(a.Instance, new Transform3D(new Basis(Vector3.Up, a.Yaw), new Vector3(p.X, p.Z + hip + a.Lift, -p.Y)));
            calls++;
            a.Moved = false;
            upload += Stopwatch.GetTimestamp() - t1;
        }
        long flush0 = Stopwatch.GetTimestamp();
        _skinTextures?.Flush();
        double flushMs = (Stopwatch.GetTimestamp() - flush0) * 1000.0 / Stopwatch.Frequency;
        _flushMsAverage += (flushMs - _flushMsAverage) * 0.2;
        PosedLastFrame = posed;
        SkippedLastFrame = n - posed;
        for (int k = 0; k < n; k++) _drawn[k] = null!;   // nothing here keeps an avatar that is removed later
        CallsLastFrame = calls;
        PoseMsLastFrame = pose * 1000.0 / Stopwatch.Frequency;
        UploadMsLastFrame = upload * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>The poses worked out on the main thread, the avatar posed longest ago first, as many as the time allows.</summary>
    private int PoseHere(double seconds, double poseBudgetMs, int n, bool all, long started, ref int calls, ref long pose, ref long upload)
    {
        int posed = 0;
        var plan = all ? default : _sweep.Plan(_stamps.AsSpan(0, n));
        for (int k = 0; k < n; k++)
        {
            var a = all ? _drawn[k] : _drawn[plan[k]];
            if (!all   // (an avatar never posed waits like any other once the first is posed: its body is not drawn before its first pose, and the one longest waiting is first)
                && !PoseSweep.MayContinue(posed, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency, poseBudgetMs, MinPosedPerFrame, ForcedPoseCapMs)) break;
            long t0 = Stopwatch.GetTimestamp();
            var overrides = a.Wear.Overrides.MeshCount > 0 ? a.Wear.Overrides : null;   // the default body follows the worn meshes' joint overrides too
            var evalProbe = FrameProbe.Begin(FrameProbe.AvPoseEval);
            float lift = PoseComputer.LiftOf(_neutral, _neutralPelvisToFoot, a.Shape, overrides, a.Wear.PelvisFixup);   // the worn meshes' pelvis offset, a taller or shorter leg, the hover height
            float clipTime = a.Clip?.TimeAt(seconds + a.Phase + a.Skew) ?? 0f;
            a.Constraints = ConstraintState.For(a.Constraints, a.Clip, Rig.NodeCount);
            ConstraintPose? solved = null;
            try
            {
                // the clip's constraints (hands and feet held to the ground, the pelvis on it for a ground sit)
                if (a.Constraints is { } state) solved = _constraints.Step(state, a.Tracks, a.Shape, overrides, clipTime, _body.Skeleton.Joints[0].Position.Z - _body.FootHeight + lift + a.GroundGap);
                if (solved is not null) (a.ConstraintsShown ??= new ConstraintPose(Rig.NodeCount)).CopyFrom(solved);
                a.Joints.PelvisDrop = 0f;
                a.Joints.Constraint = solved is null ? null : a.ConstraintsShown;
                a.Pose.Evaluate(seconds + a.Phase + a.Skew, _skin, a.Shape, overrides, 0f, solved);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // one avatar's error does not stop the frame: it keeps its last pose and is posed again next frame without the clip that failed
                evalProbe.End();
                OnPoseFailed(a, e, a.Clip);
                a.LastPosed = _frameNumber;
                continue;
            }
            evalProbe.End();
            long t1 = Stopwatch.GetTimestamp();
            pose += t1 - t0;
            var jointsProbe = FrameProbe.Begin(FrameProbe.AvJoints);
            a.Joints.Begin(a.Tracks, a.Clip?.TimeAt(seconds + a.Phase + a.Skew) ?? 0f);
            if (a.Row >= 0)
            {
                a.Joints.Fill(_worlds);
                SkinTables.PackBones(_worlds, _skinTextures!.Row(a.Row));
                _skinTextures.Touch();
            }
            jointsProbe.End();
            var boneProbe = FrameProbe.Begin(FrameProbe.AvBoneCalls);
            for (int i = 0; i < _skin.Length; i++)
                RenderingServer.SkeletonBoneSetTransform(a.Skeleton, i, ToEngine(_skin[i]));
            boneProbe.End();
            calls += _skin.Length;
            if (lift != a.Lift) { a.Lift = lift; a.Moved = true; }
            a.LastPosed = _frameNumber;
            a.PoseShown = seconds + a.Phase + a.Skew;
            MakeReady(a);
            posed++;
            upload += Stopwatch.GetTimestamp() - t1;
        }
        return posed;
    }

    /// <summary>The poses worked out on workers: the finished ones are handed to the engine (the one finished longest ago first, within the budget and never
    /// fewer than <see cref="MinPosedPerFrame"/> when that many are ready), then every drawn avatar with no pose in hand is given to the workers, the one
    /// posed longest ago first.</summary>
    private int PoseThroughWorkers(double seconds, double poseBudgetMs, int n, ref int calls, ref long pose, ref long upload)
    {
        _workers ??= new PoseWorkers(_body, Rig, WorkerThreadCount());
        int handed = 0;
        long started = Stopwatch.GetTimestamp();
        while (PoseSweep.MayContinue(handed, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency, poseBudgetMs, MinPosedPerFrame, ForcedPoseCapMs) && _workers.TryTake(out var job))
        {
            long t0 = Stopwatch.GetTimestamp();
            if (HandOverPose(job, ref calls)) handed++;
            upload += Stopwatch.GetTimestamp() - t0;
        }
        long t1 = Stopwatch.GetTimestamp();
        using (FrameProbe.Begin(FrameProbe.AvPoseSubmit))
        {
            for (int k = 0; k < n; k++) { _busy[k] = _drawn[k].InFlight; _stamps[k] = _drawn[k].LastPosed; }   // (an avatar whose pose was just handed over is free again)
            int count = _sweep.PlanSubmissions(_stamps.AsSpan(0, n), _busy.AsSpan(0, n), _plan, Math.Max(MaxPosesPerFrame, MinPosedPerFrame));
            for (int k = 0; k < count; k++)
            {
                var a = _drawn[_plan[k]];
                var job = _workers.Rent(a.Row >= 0);
                job.Tag = a;
                job.Generation = a.Generation;
                job.Playing = seconds + a.Phase + a.Skew;
                job.Clip = _failures.IsDropped(a.Clip) ? null : a.Clip;
                a.Constraints = ConstraintState.For(a.Constraints, job.Clip, Rig.NodeCount);
                job.Constraints = a.Constraints;
                job.Shape = a.Shape;
                job.GroundGap = a.GroundGap;
                job.UseOverrides = a.Wear.Overrides.MeshCount > 0;
                job.Overrides = a.OverridesSnap ??= a.Wear.Overrides.Clone();
                a.InFlight = true;
                a.LastPosed = _frameNumber;
                _workers.Submit(job);
            }
        }
        pose += Stopwatch.GetTimestamp() - t1;
        return handed;
    }

    /// <summary>Hands the engine a pose a worker finished. False (and nothing handed over) when the avatar is gone or changed since the job was made:
    /// it is posed again from its new state.</summary>
    private bool HandOverPose(PoseJob job, ref int calls)
    {
        var a = (Avatar)job.Tag!;
        a.InFlight = false;
        try
        {
            if (job.Error is { } failed)
            {
                // this avatar keeps its last pose and is posed again next frame without the clip that failed; the others are not affected
                OnPoseFailed(a, failed, job.Clip);
                return false;
            }
            if (a.Removed || job.Generation != a.Generation || (a.Row >= 0) != job.WantRow) return false;
            var boneProbe = FrameProbe.Begin(FrameProbe.AvBoneCalls);
            for (int i = 0; i < job.Skin.Length; i++)
                RenderingServer.SkeletonBoneSetTransform(a.Skeleton, i, ToEngine(job.Skin[i]));
            boneProbe.End();
            calls += job.Skin.Length;
            using (FrameProbe.Begin(FrameProbe.AvJoints))
            {
                a.Joints.PelvisDrop = 0f;
                if (job.Solved is { } solved) (a.ConstraintsShown ??= new ConstraintPose(Rig.NodeCount)).CopyFrom(solved);
                a.Joints.Constraint = job.Solved is null ? null : a.ConstraintsShown;
                a.Joints.Begin(a.Tracks, job.ClipTime);   // the worn objects that follow its joints read this pose
                if (a.Row >= 0)
                {
                    job.Row.AsSpan(0, _skinTextures!.RowEntries * SkinTables.FloatsPerMatrix).CopyTo(_skinTextures.Row(a.Row));
                    _skinTextures.Touch();
                }
            }
            if (job.Lift != a.Lift) { a.Lift = job.Lift; a.Moved = true; }
            a.PoseShown = job.Playing;
            MakeReady(a);
            return true;
        }
        finally { _workers!.Return(job); }
    }

    /// <summary>A pose could not be worked out for <paramref name="a"/>: logs it once for that avatar and cause, stops using the clip, and goes on.</summary>
    private void OnPoseFailed(Avatar a, Exception error, AnimationClip? clip)
    {
        _failures.Report(a, "#" + a.Key, error, clip);
        if (clip is not null && ReferenceEquals(a.Clip, clip))
        {
            a.Pose = PoseFor(null);
            a.Clip = null;
            a.Tracks = TracksFor(null);
            a.Generation++;
        }
    }

    /// <summary>Waits for every pose in hand to finish and hands them over, whatever the time (the poses go back to the main thread).</summary>
    private long DrainWorkers(ref int calls)
    {
        long t0 = Stopwatch.GetTimestamp();
        _workers!.WaitUntilFinished(TimeSpan.FromSeconds(30));
        while (_workers.TryTake(out var job)) HandOverPose(job, ref calls);
        return Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>A skinning transform from the core's frame (SL axes, row vectors) to the engine's (x, z, -y; columns).</summary>
    public static Transform3D ToEngine(in System.Numerics.Matrix4x4 m) => new(
        new Basis(new Vector3(m.M11, m.M13, -m.M12), new Vector3(m.M31, m.M33, -m.M32), new Vector3(-m.M21, -m.M23, m.M22)),
        new Vector3(m.M41, m.M43, -m.M42));

    private static ShaderMaterial PartMaterial(BodyPart part, Texture2D tex, Color tint)
    {
        var m = new ShaderMaterial { Shader = KeptShaders.Body(part.Type == "eyelashMesh") };
        VariantDisposal.SetShaderParameter(m, EngineNames.Tex, tex);
        m.SetShaderParameter(EngineNames.Tint, new Vector4(tint.R, tint.G, tint.B, tint.A));   // raw sRGB (a Color would be converted to linear)
        return m;
    }

    /// <summary>A skinned surface handed to the engine as every surface is (<see cref="PrimLayer.HandOver(Rid, PreparedSurface)"/>:
    /// the kept dictionary pinned, the byte arrays held through the call), with its skin data.</summary>
    internal static unsafe void HandOver(Rid mesh, PreparedSkinnedSurface skinned)
    {
        var s = skinned.Surface;
        var a = s.Aabb;
        using var held = new HeldValues();
        var k = KeptCollections.Handover;
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
                d[EngineNames.SkinData] = held.Add(skinned.SkinData);
                if (s.IndexCount > 0) { d[EngineNames.IndexData] = held.Add(s.IndexData); d[EngineNames.IndexCount] = s.IndexCount; }
                RenderingServer.MeshAddSurface(mesh, d);
            }
            finally { k.Clear(); }
        }
        GC.KeepAlive(held);
    }

    public void Dispose()
    {
        _workers?.Dispose();
        _workers = null;
        foreach (var a in _avatars.Values) { a.Removed = true; Free(a); }
        _avatars.Clear();
        Rids.Free(_mesh);
        foreach (var m in _materials) m.Dispose();
        _white.Dispose();
    }
}
