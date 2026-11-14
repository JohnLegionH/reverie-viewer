// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>
/// Poses a body with one clip and writes the skinning transforms of its palette. Each joint's local transform is its
/// rotation (the clip's, else its default) then its position: the top joint (the pelvis) sits at the avatar's root, offset
/// by the clip's position keys when it has any (the official viewer places the pelvis at the root and the body meshes
/// skin relative to the default skeleton, so the default body hangs from the root at hip height); other joints keep their
/// default position unless the clip moves them. Only the joints the palette needs (and their ancestors) are evaluated.
/// Allocation-free per call; one instance per thread.
/// </summary>
public sealed class AvatarPose
{
    private readonly AvatarBody _body;
    private readonly int[] _joints;          // needed joints, parents first
    private readonly int[] _parentSlot;      // per needed joint, its parent's slot in _joints (-1: the root)
    private readonly JointTrack?[] _tracks;  // per needed joint, the clip's track
    private readonly int[] _paletteSlot;     // per palette entry, its slot in _joints (-1: the root)
    private readonly Matrix4x4[] _world;
    private readonly Vector3[] _wpos, _scale;   // the shaped path's per joint: world position and own scale
    private readonly Matrix4x4[] _wrot;         // and world rotation
    private readonly AnimationClip? _clip;
    private readonly Matrix4x4 _rootAtHip;   // the root entry hangs with the body: the default pelvis height taken off

    public AvatarPose(AvatarBody body, AnimationClip? clip)
    {
        _body = body;
        _clip = clip;
        var sk = body.Skeleton;
        var needed = new SortedSet<int>();
        foreach (int p in body.Palette)
            for (int j = p; j >= 0; j = sk.Joints[j].Parent) needed.Add(j);
        _joints = needed.ToArray();   // file order: every parent before its children
        var slot = new Dictionary<int, int>();
        for (int i = 0; i < _joints.Length; i++) slot[_joints[i]] = i;
        _parentSlot = _joints.Select(j => sk.Joints[j].Parent is int p && p >= 0 ? slot[p] : -1).ToArray();
        var byName = clip?.Tracks.GroupBy(t => sk.IndexOf(t.Joint)).Where(g => g.Key >= 0).ToDictionary(g => g.Key, g => g.First());
        _tracks = _joints.Select(j => byName is not null && byName.TryGetValue(j, out var t) ? t : null).ToArray();
        _paletteSlot = body.Palette.Select(p => p == SkinBinding.Root ? -1 : slot[p]).ToArray();
        _world = new Matrix4x4[_joints.Length];
        _wpos = new Vector3[_joints.Length];
        _wrot = new Matrix4x4[_joints.Length];
        _scale = new Vector3[_joints.Length];
        AnimatedJoints = _tracks.Count(t => t is not null);
        _rootAtHip = Matrix4x4.CreateTranslation(-sk.Joints.First(j => j.Parent < 0).Position);
    }

    /// <summary>How many of the evaluated joints the clip drives.</summary>
    public int AnimatedJoints { get; }
    public int JointsEvaluated => _joints.Length;
    public int PaletteSize => _paletteSlot.Length;

    /// <summary>The skinning transforms (row-vector convention, root frame) at <paramref name="playing"/> seconds into the
    /// clip (the default pose without a clip): a default-pose vertex v moves to v * skin[i] blended over its two entries.</summary>
    public void Evaluate(double playing, Span<Matrix4x4> skin) => Evaluate(playing, skin, null, null);

    /// <summary>As <see cref="Evaluate(double, Span{Matrix4x4})"/> for an avatar with a shaped skeleton and the joint overrides of the
    /// rigged meshes it wears: each joint is at the override's position if a mesh asks, else the clip's key, else the shape's; its scale is
    /// the shape's unless a mesh pins it; joints are composed as the official viewer does (<see cref="AvatarJoints"/>). With neither it is the unshaped pose.</summary>
    public void Evaluate(double playing, Span<Matrix4x4> skin, AvatarShape? shape, JointOverrides? overrides, float pelvisDrop = 0f, ConstraintPose? constraints = null)
    {
        if (shape is null && overrides is null && constraints is null) { EvaluateUnshaped(playing, skin, pelvisDrop); return; }
        if (skin.Length < _paletteSlot.Length) throw new ArgumentException("skin span shorter than the palette");
        var sk = _body.Skeleton;
        float t = _clip?.TimeAt(playing) ?? 0f;
        for (int i = 0; i < _joints.Length; i++)
        {
            int ji = _joints[i];
            var j = sk.Joints[ji];
            var track = _tracks[i];
            var rot = constraints?.Rotations[ji] is { } solved ? solved
                : track is { RotationTimes.Length: > 0 } ? AnimationClip.RotationAt(track, t) : j.Rotation;
            Vector3 pos;
            if (j.Parent >= 0 && overrides is not null && overrides.TryGet(ji, out var overridden)) pos = overridden;
            else if (track is { PositionTimes.Length: > 0 }) pos = AnimationClip.PositionAt(track, t);
            else pos = j.Parent < 0 ? Vector3.Zero : shape is not null ? shape.Position[ji] : j.Position;
            if (j.Parent < 0)
            {
                if (constraints?.PelvisPosition is { } held) pos = held;   // a constraint with no chain moved the pelvis
                pos.Z += pelvisDrop;
            }
            var scale = shape is not null ? shape.ScaleOf(ji, overrides) : Vector3.One;
            var rotM = Matrix4x4.CreateFromQuaternion(rot);
            int ps = _parentSlot[i];
            Vector3 wpos;
            Matrix4x4 wrot;
            if (ps < 0) { wpos = pos; wrot = rotM; }
            else
            {
                wpos = Vector3.Transform(pos * _scale[ps], _wrot[ps]) + _wpos[ps];
                wrot = rotM * _wrot[ps];
            }
            _wpos[i] = wpos;
            _wrot[i] = wrot;
            _scale[i] = scale;
            var w = Matrix4x4.CreateScale(scale) * wrot;
            w.Translation = wpos;
            _world[i] = w;
        }
        var inv = _body.InverseBind;
        for (int i = 0; i < _paletteSlot.Length; i++)
        {
            int s = _paletteSlot[i];
            skin[i] = s < 0 ? inv[i] * _rootAtHip : inv[i] * _world[s];
        }
    }

    private void EvaluateUnshaped(double playing, Span<Matrix4x4> skin, float pelvisDrop)
    {
        if (skin.Length < _paletteSlot.Length) throw new ArgumentException("skin span shorter than the palette");
        var sk = _body.Skeleton;
        float t = _clip?.TimeAt(playing) ?? 0f;
        for (int i = 0; i < _joints.Length; i++)
        {
            var j = sk.Joints[_joints[i]];
            var track = _tracks[i];
            var rot = track is { RotationTimes.Length: > 0 } ? AnimationClip.RotationAt(track, t) : j.Rotation;
            Vector3 pos;
            if (track is { PositionTimes.Length: > 0 }) pos = AnimationClip.PositionAt(track, t);
            else pos = j.Parent < 0 ? Vector3.Zero : j.Position;
            if (j.Parent < 0) pos.Z += pelvisDrop;
            var local = Matrix4x4.CreateFromQuaternion(rot);
            local.Translation = pos;
            int ps = _parentSlot[i];
            _world[i] = ps < 0 ? local : local * _world[ps];
        }
        var inv = _body.InverseBind;
        for (int i = 0; i < _paletteSlot.Length; i++)
        {
            int s = _paletteSlot[i];
            skin[i] = s < 0 ? inv[i] * _rootAtHip : inv[i] * _world[s];
        }
    }
}
