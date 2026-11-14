// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>
/// The animated world matrix of any node of an avatar's <see cref="AvatarRig"/> (a joint, a collision volume or an
/// attachment point), worked out only for the nodes asked for and the chain above them, once per pose. This is what an
/// attached object follows: its attachment point's node. The frame is the one the stock body is drawn in: the pelvis at the
/// origin (<see cref="AvatarPose"/> hangs the body from the pelvis), so a joint's matrix here is its default world matrix
/// times the body's skinning matrix for it, and a point on the avatar root sits a pelvis height below the origin.
///
/// A joint is posed as <see cref="AvatarPose"/> poses it, so attachments and the body agree: its rotation is the clip's key
/// (the joint's own default without one); its position is the clip's key (the pelvis is at the origin and every other joint
/// at its default offset without one). A node with no joint of its own in the clip, such as an attachment point, keeps its
/// default offset and rotation under its animated parent. Joint scale of bones is not applied (the skeleton gives every bone
/// a scale of one). Not thread safe; one instance per avatar; allocation-free per pose.
/// </summary>
public sealed class AvatarJoints
{
    private readonly Matrix4x4[] _world;
    private readonly int[] _stamp;
    private readonly Vector3[] _wpos, _scale;
    private readonly Matrix4x4[] _wrot;
    private readonly Matrix4x4 _rootToPelvisFrame;
    private JointTrack?[]? _tracks;
    private float _time;
    private int _pose = 1;   // never equal to a node's first stamp (0): a node asked for before any Begin is worked out in the default pose

    public AvatarJoints(AvatarRig rig)
    {
        Rig = rig;
        _world = new Matrix4x4[rig.NodeCount];
        _stamp = new int[rig.NodeCount];
        _wpos = new Vector3[rig.NodeCount];
        _wrot = new Matrix4x4[rig.NodeCount];
        _scale = new Vector3[rig.NodeCount];
        _rootToPelvisFrame = Matrix4x4.CreateTranslation(-rig.DefaultPosition[rig.Pelvis]);
    }

    public AvatarRig Rig { get; }

    /// <summary>The joint position overrides the avatar's worn rigged meshes ask for (<see cref="RiggedWear.Overrides"/>); null: none.
    /// A joint with an override is at that position whatever the clip says (the official viewer's LLJoint::setPosition replaces the
    /// requested position with the active override); the pelvis override moves the avatar root below it. Call <see cref="Invalidate"/>
    /// when the overrides change, or <see cref="Begin"/>.</summary>
    public JointOverrides? Overrides { get; set; }

    /// <summary>The avatar's shaped skeleton (<see cref="AvatarShape"/>); null: the skeleton's own scales and positions. The order is the
    /// official viewer's: the shape sets each joint's position and scale, then a worn mesh's position override replaces the position and
    /// its scale lock pins the scale to the skeleton's own. Call <see cref="Invalidate"/> when it changes.</summary>
    public AvatarShape? Shape { get; set; }

    /// <summary>The metres the pelvis is moved down in the pose, on top of the clip's keys; 0 for none (the clip's constraints work through <see cref="Constraint"/>).
    /// Set before <see cref="Begin"/>.</summary>
    public float PelvisDrop { get; set; }

    /// <summary>What the clip's constraints change in the pose (<see cref="ConstraintSolver"/>): the pelvis position and joint rotations that replace the clip's keys. Null: none.
    /// Set before <see cref="Begin"/>, or call <see cref="Invalidate"/> after changing it.</summary>
    public ConstraintPose? Constraint { get; set; }

    /// <summary>The node's world position in this frame (<see cref="World"/> worked out).</summary>
    public Vector3 WorldPosition(int node)
    {
        if (node < 0) return RootWorld().Translation;   // the avatar root, above the top joint
        World(node);
        return _wpos[node];
    }

    /// <summary>The node's world rotation alone (no scale or position) as a matrix, row-vector convention; the avatar root's (no node) is the identity.</summary>
    public Matrix4x4 WorldRotation(int node)
    {
        if (node < 0) return Matrix4x4.Identity;
        World(node);
        return _wrot[node];
    }

    /// <summary>Forgets the matrices worked out for the pose started by the last <see cref="Begin"/>, so they are worked out again.</summary>
    public void Invalidate() => _pose++;

    /// <summary>The avatar root's matrix in this frame: a pelvis height below the origin (the pelvis's own position, overridden or not).</summary>
    public Matrix4x4 RootWorld() =>
        Overrides is { } o && o.TryGet(Rig.Pelvis, out var pelvis) ? Matrix4x4.CreateTranslation(-pelvis) : _rootToPelvisFrame;

    /// <summary>Writes every node's world matrix (index = node) and, after them, the avatar root's, in the pose started by the last
    /// <see cref="Begin"/>: <see cref="AvatarRig.NodeCount"/> + 1 entries. This is an avatar's row of joint matrices for the skinning shader.</summary>
    public void Fill(Span<Matrix4x4> worlds)
    {
        int n = Rig.NodeCount;
        if (worlds.Length < n + 1) throw new ArgumentException("span shorter than the node count plus the root");
        for (int i = 0; i < n; i++) worlds[i] = World(i);
        worlds[n] = RootWorld();
    }

    /// <summary>The clip's track for each of the rig's nodes (null where the clip has none, and for every attachment point).
    /// Made once per clip and rig, then shared by every avatar playing the clip.</summary>
    public static JointTrack?[] TracksFor(AvatarRig rig, AnimationClip? clip)
    {
        var tracks = new JointTrack?[rig.NodeCount];
        if (clip is null) return tracks;
        foreach (var t in clip.Tracks)
        {
            int joint = rig.Skeleton.IndexOf(t.Joint);
            if (joint >= 0 && tracks[joint] is null) tracks[joint] = t;
        }
        return tracks;
    }

    /// <summary>Starts a pose: the clip's tracks (<see cref="TracksFor"/>; null: the default pose) at clip time
    /// <paramref name="clipTime"/> seconds. Nothing is worked out until a node is asked for.</summary>
    public void Begin(JointTrack?[]? tracks, float clipTime)
    {
        _tracks = tracks;
        _time = clipTime;
        _pose++;
    }

    /// <summary>The node's matrix in the pose started by the last <see cref="Begin"/> (its own frame to the pelvis frame;
    /// row-vector convention, as <see cref="JointWorlds"/>). A screen point (HUD) is not on the body: it is worked out as if
    /// it hung from the root, and callers do not place anything on it.</summary>
    public Matrix4x4 World(int node)
    {
        if (_stamp[node] == _pose) return _world[node];
        var rig = Rig;
        var track = _tracks is null ? null : _tracks[node];
        var rotation = Constraint?.Rotations[node] is { } solved ? solved
            : track is { RotationTimes.Length: > 0 } ? AnimationClip.RotationAt(track, _time) : rig.DefaultRotation[node];
        // position: a worn mesh's override wins, then the clip's key, then the shape's (the skeleton's own without a shape)
        Vector3 position;
        if (node != rig.Pelvis && Overrides is { } o && o.TryGet(node, out var overridden)) position = overridden;
        else if (track is { PositionTimes.Length: > 0 }) position = AnimationClip.PositionAt(track, _time);
        else position = node == rig.Pelvis ? Vector3.Zero : Shape is { } sp ? sp.Position[node] : rig.DefaultPosition[node];
        // scale: the shape's, unless a worn mesh pins the joint's scale to the skeleton's own
        if (node == rig.Pelvis)
        {
            if (Constraint?.PelvisPosition is { } held) position = held;   // a constraint with no chain moved the pelvis
            position.Z += PelvisDrop;
        }
        var scale = Shape is { } ss ? ss.ScaleOf(node, Overrides) : rig.DefaultScale[node];
        var rotM = Matrix4x4.CreateFromQuaternion(rotation);
        int parent = rig.Parent(node);
        // as the official viewer composes joints (xform.cpp): a joint's matrix carries its OWN scale only, and its offset from the
        // parent is scaled by the parent's scale, then turned by the parent's world rotation and added to the parent's world position
        Vector3 wpos;
        Matrix4x4 wrot;
        if (parent >= 0)
        {
            World(parent);
            wpos = Vector3.Transform(position * _scale[parent], _wrot[parent]) + _wpos[parent];
            wrot = rotM * _wrot[parent];
        }
        else if (node == rig.Pelvis) { wpos = position; wrot = rotM; }
        else { wpos = position + RootWorld().Translation; wrot = rotM; }   // hung from the avatar root, which is a pelvis height below the pelvis
        _wpos[node] = wpos;
        _wrot[node] = wrot;
        _scale[node] = scale;
        var world = Matrix4x4.CreateScale(scale) * wrot;
        world.Translation = wpos;
        _world[node] = world;
        _stamp[node] = _pose;
        return world;
    }
}
