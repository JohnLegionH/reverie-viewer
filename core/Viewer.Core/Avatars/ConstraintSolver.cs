// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llkeyframemotion.cpp
 * @brief Implementation of LLKeyframeMotion class.
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
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

/**
 * @file llcriticaldamp.cpp
 * @brief Implementation of the critical damping functionality.
 *
 * $LicenseInfo:firstyear=2002&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
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

/**
 * @file llquaternion.cpp
 * @brief LLQuaternion class implementation.
 *
 * $LicenseInfo:firstyear=2000&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
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

// Adapted 2026 by Legion Builds: translated to C# from LLKeyframeMotion::initializeConstraint, activateConstraint, deactivateConstraint and
// applyConstraint (indra/llcharacter/llkeyframemotion.cpp, viewer 26.4), with LLSmoothInterpolation::calcInterpolant
// (indra/llcommon/llcriticaldamp.cpp) and LLQuaternion::shortestArc (indra/llmath/llquaternion.cpp), all under the same licence and
// copyright (2010, Linden Research, Inc.). What changed: the skeleton is this viewer's AvatarJoints, joints are addressed by node index,
// the ground is the avatar's own ground height in the pelvis frame, the solver's state lives in a ConstraintState per avatar, and the
// number of solver iterations is a parameter.

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>What the constraint solver changes in a pose: the pelvis position (null: the clip's key), and the local rotation of each joint
/// the solver set (null: the clip's key). Indexed by <see cref="AvatarRig"/> node.</summary>
public sealed class ConstraintPose
{
    public ConstraintPose(int nodes) { Rotations = new Quaternion?[nodes]; }
    public Vector3? PelvisPosition;
    public Quaternion?[] Rotations;
    public void Clear() { PelvisPosition = null; Array.Clear(Rotations); }
    public void CopyFrom(ConstraintPose other) { PelvisPosition = other.PelvisPosition; Array.Copy(other.Rotations, Rotations, Rotations.Length); }
}

/// <summary>
/// What the solver keeps from one frame to the next for one avatar playing one clip: per constraint whether it is active, the ground point it
/// holds (fixed while active), the smoothed weight, the simulated positions of the chain's middle joints in the pelvis frame and their last
/// fixup distance; and the pelvis position of a constraint with no chain (which moves from where it was, not from its key).
/// </summary>
public sealed class ConstraintState
{
    internal sealed class Slot
    {
        public bool Active;
        public Vector3 GroundPos, GroundNorm = Vector3.UnitZ;
        public float Weight = 1f;
        public readonly Vector3[] Positions = new Vector3[ConstraintSolver.MaxChain];
        public float FixupRms;
        public readonly float[] Lengths = new float[ConstraintSolver.MaxChain], Fractions = new float[ConstraintSolver.MaxChain];
        public float Total;
        public int[]? Chain;        // node of the joints from the source volume's parent (0) up to the chain's root (ChainLength)
        public int Source = -1, Target = -1;
        public bool Ready;
    }

    internal ConstraintState(AnimationClip clip, int nodes)
    {
        Clip = clip;
        Pose = new ConstraintPose(nodes);
        Prev = new ConstraintPose(nodes);
        Work = new ConstraintPose(nodes);
        Slots = new Slot[clip.Constraints.Count];
        for (int i = 0; i < Slots.Length; i++) Slots[i] = new Slot();
    }

    public AnimationClip Clip { get; }
    public ConstraintPose Pose { get; }
    /// <summary>The pose the constraints left on the body at the end of the frame before, and the working copy the constraints of a frame read.</summary>
    internal ConstraintPose Prev { get; }
    internal ConstraintPose Work { get; }
    internal Slot[] Slots { get; }
    internal AvatarShape? Shape;
    internal bool HasLast;
    internal float LastTime;

    /// <summary>The state for <paramref name="clip"/>: <paramref name="old"/> while the avatar plays the same clip; for another clip a new one that
    /// takes over each constraint of the old state that is the same constraint (a mix changes as motions start and stop, and a constraint that goes on
    /// must not start again).</summary>
    public static ConstraintState? For(ConstraintState? old, AnimationClip? clip, int nodes)
    {
        if (clip is null || clip.Constraints.Count == 0) return null;
        if (old is not null && ReferenceEquals(old.Clip, clip)) return old;
        var s = new ConstraintState(clip, nodes);
        if (old is not null)
        {
            s.HasLast = old.HasLast; s.LastTime = old.LastTime; s.Prev.CopyFrom(old.Prev);
            for (int i = 0; i < s.Slots.Length; i++)
            {
                var c = clip.Constraints[i];
                for (int j = 0; j < old.Slots.Length; j++)
                {
                    var o = old.Clip.Constraints[j];
                    if (o.ChainLength != c.ChainLength || o.Plane != c.Plane || o.SourceVolume != c.SourceVolume || o.SourceOffset != c.SourceOffset
                        || o.Target != c.Target || o.TargetOffset != c.TargetOffset) continue;
                    var from = old.Slots[j]; var to = s.Slots[i];
                    to.Active = from.Active; to.GroundPos = from.GroundPos; to.GroundNorm = from.GroundNorm; to.Weight = from.Weight;
                    Array.Copy(from.Positions, to.Positions, from.Positions.Length); to.FixupRms = from.FixupRms;
                    break;
                }
            }
        }
        return s;
    }
}

/// <summary>
/// The clip constraints of the official viewer's keyframe motion: a point on a collision volume (a hand, a foot, a lower leg) is held to the ground or to
/// another volume by turning the joint chain above it (one or two joints), with the chain's middle joint simulated as a spring between its neighbours; a
/// constraint with no chain moves the pelvis. The solver keeps state between frames (<see cref="ConstraintState"/>), as the official one does. One instance
/// per thread (it owns the scratch skeleton); the state belongs to the avatar.
/// A constraint is skipped while a motion run before its own already turns one of its chain's joints at an equal or higher priority (<see cref="JointMask"/>).
/// Not ported: the iteration count from the avatar's size on screen; here the count is a parameter, and the ground is the avatar's own ground height, level.
/// </summary>
public sealed class ConstraintSolver
{
    public const int MaxChain = 4;
    public const float JointLengthK = 0.7f;              // JOINT_LENGTH_K
    public const int MaxIterations = 20, MinIterationCount = 2;   // MAX_ITERATIONS, MIN_ITERATION_COUNT
    private const float MinAccelerationSquared = 0.0005f * 0.0005f;

    private readonly AvatarRig _rig;
    private readonly AvatarJoints _j;

    public ConstraintSolver(AvatarRig rig)
    {
        _rig = rig;
        _j = new AvatarJoints(rig);
    }

    /// <summary>Works out the constraints of <paramref name="state"/>'s clip at time <paramref name="t"/> (the time the pose is read at) and returns
    /// what they change. <paramref name="groundBelowPelvis"/> is how far the ground lies below the pelvis's rest height.</summary>
    public ConstraintPose Step(ConstraintState state, JointTrack?[] tracks, AvatarShape? shape, JointOverrides? overrides, float t, float groundBelowPelvis,
        int maxIterations = MaxIterations)
    {
        var clip = state.Clip;
        var pose = state.Pose;
        pose.Clear();
        // the body as the frame before left it: what the constraints read, as the official viewer's joints hold the last pose until the new one is put on them
        var work = state.Work;
        work.CopyFrom(state.Prev);
        _stale = state.HasLast ? state.LastTime : t;
        _j.Shape = shape; _j.Overrides = overrides; _j.PelvisDrop = 0f; _j.Constraint = work;
        _j.Begin(tracks, _stale);
        if (!ReferenceEquals(state.Shape, shape))
        {
            state.Shape = shape;
            foreach (var s in state.Slots) s.Ready = false;
        }
        float deltaT = state.HasLast ? MathF.Abs(t - state.LastTime) : 0.02f;
        float frameDt = Math.Clamp(deltaT, 0.001f, 0.1f);
        for (int i = 0; i < state.Slots.Length; i++)
        {
            var slot = state.Slots[i];
            var c = clip.Constraints[i];
            if (!slot.Ready && !Initialize(slot, c, tracks, t)) continue;
            Apply(state, slot, c, tracks, t, groundBelowPelvis, deltaT, frameDt, maxIterations);
        }
        state.Prev.CopyFrom(pose);
        state.HasLast = true;
        state.LastTime = t;
        _j.Constraint = null;
        return pose;
    }

    // ---- initializeConstraint ----
    private bool Initialize(ConstraintState.Slot s, ClipConstraint c, JointTrack?[] tracks, float time)
    {
        int chainLength = c.ChainLength;
        if (chainLength < 0 || chainLength >= MaxChain) return false;
        int vol = _rig.Skeleton.IndexOf(c.SourceVolume);
        if (vol < 0 || _rig.Skeleton.Joints[vol].Kind != JointKind.CollisionVolume) return false;
        s.Source = vol;
        s.Target = c.TargetsGround ? -1 : _rig.Skeleton.IndexOf(c.Target);
        if (!c.TargetsGround && s.Target < 0) return false;
        var chain = new int[chainLength + 1];
        int node = vol;
        for (int k = 0; k <= chainLength; k++)
        {
            node = _rig.Parent(node);
            if (node < 0 || tracks[node] is null) return false;   // the official viewer refuses such an animation; here the constraint is left out
            chain[k] = node;
        }
        s.Chain = chain;
        // lengths in the pose the skeleton has with no animation
        var saved = _j.Constraint;
        _j.Constraint = null; _j.PelvisDrop = 0f;
        _j.Begin(null, 0f);
        var source = VolumePos(vol, c.SourceOffset);
        float sourceOffset = Vector3.Distance(source, _j.WorldPosition(chain[0]));
        s.Total = s.Lengths[0] = Vector3.Distance(_j.WorldPosition(_rig.Parent(chain[0])), source);
        for (int k = 1; k < chainLength; k++)
        {
            s.Lengths[k] = Vector3.Distance(_j.WorldPosition(chain[k]), _j.WorldPosition(_rig.Parent(chain[k])));
            s.Total += s.Lengths[k];
        }
        for (int k = 1; k < chainLength; k++) s.Fractions[k] = s.Lengths[k] / s.Total;
        s.Total += sourceOffset;
        _j.Constraint = saved;
        _j.Begin(tracks, _stale);
        s.Ready = true;
        return true;
    }

    private Vector3 VolumePos(int volume, Vector3 offset) => Vector3.Transform(offset, _j.World(volume));

    // ---- activateConstraint ----
    private void Activate(ConstraintState.Slot s, ClipConstraint c, float groundBelowPelvis)
    {
        s.Active = true;
        if (c.TargetsGround)
        {
            var source = VolumePos(s.Source, c.SourceOffset);
            s.GroundNorm = Vector3.UnitZ;
            s.GroundPos = new Vector3(source.X, source.Y, -groundBelowPelvis) + c.TargetOffset;
        }
        var chain = s.Chain!;
        if (chain.Length > 2)
        {
            var pw = _j.World(_rig.Pelvis);
            var pp = _j.WorldPosition(_rig.Pelvis);
            var inv = Matrix4x4.Transpose(_j.WorldRotation(_rig.Pelvis));
            for (int k = 1; k < c.ChainLength; k++) s.Positions[k] = Vector3.Transform(_j.WorldPosition(chain[k]) - pp, inv);
        }
        s.Weight = 1f;
    }

    // ---- deactivateConstraint ----
    private static void Deactivate(ConstraintState.Slot s, ClipConstraint c)
    {
        s.Active = false;
    }

    // ---- applyConstraint ----
    private void Apply(ConstraintState state, ConstraintState.Slot s, ClipConstraint c, JointTrack?[] tracks, float time, float groundBelowPelvis,
        float deltaT, float frameDt, int maxIterations)
    {
        float t = c.LocalTime(time);
        if (t < c.EaseInStart) return;
        if (t > c.EaseOutStop) { if (s.Active) Deactivate(s, c); return; }
        var pose = state.Pose;
        var work = state.Work;
        var chain = s.Chain!;
        int chainLength = c.ChainLength;
        // the joints are as the frame before left them (the official viewer reads the joints before the new pose is put on them)
        if (!s.Active || t < c.EaseInStop) Activate(s, c, groundBelowPelvis);

        // a joint of the chain that a motion run before this one already turns at an equal or higher priority: the constraint is skipped
        // (llkeyframemotion.cpp applyConstraint, the joint mask check)
        if (c.Mask is { } mask)
        {
            var names = new string[chainLength + 1];
            for (int k = 0; k <= chainLength; k++) names[k] = _rig.NodeName(chain[k]);
            if (mask.Covers(names, time)) return;
        }

        var rootPos = _j.WorldPosition(chain[chainLength]);
        // the chain's joints are then set to the motion's keys; the rest of the body stays as it was. A constraint with no chain moves the pelvis
        // from where it was
        for (int k = 0; k <= chainLength; k++) work.Rotations[chain[k]] = StateRotation(pose, chain[k], tracks, time);
        _j.Invalidate();
        var keyframeSource = VolumePos(s.Source, c.SourceOffset);
        Vector3 target = c.TargetsGround ? s.GroundPos : VolumePos(s.Target, c.TargetOffset);

        if (c.Plane)
        {
            Vector3 norm;
            if (c.TargetsGround) norm = s.GroundNorm;
            else
            {
                norm = target - _j.WorldPosition(s.Target);
                if (norm == Vector3.Zero)
                {
                    norm = -c.SourceOffset;
                    norm = Vector3.Transform(norm, _j.WorldRotation(s.Source));
                }
                if (norm.LengthSquared() > 0) norm = Vector3.Normalize(norm);
            }
            target = keyframeSource + norm * Vector3.Dot(target - keyframeSource, norm);
        }

        // the weight follows 1 (towards 0 when the target is out of the chain's reach), at the frame's rate
        if (chainLength != 0 && Vector3.DistanceSquared(rootPos, target) * 0.95f > s.Total * s.Total)
            s.Weight = Lerp(s.Weight, 0f, Interpolant(0.1f, frameDt));
        else
            s.Weight = Lerp(s.Weight, 1f, Interpolant(0.3f, frameDt));
        float weight = s.Weight * c.MotionWeight(time) * (c.EaseOutStop == 0f ? 1f
            : MathF.Min(ClampRescale(t, c.EaseInStart, c.EaseInStop, 0f, 1f), ClampRescale(t, c.EaseOutStart, c.EaseOutStop, 1f, 0f)));
        var sourceToTarget = target - keyframeSource;

        if (chainLength > 0)
        {
            var endRot = _j.WorldRotation(chain[0]);
            var positions = new Vector3[MaxChain];
            var velocities = new Vector3[MaxChain];
            positions[0] = Vector3.Lerp(keyframeSource, target, weight);
            positions[chainLength] = rootPos;
            var pelvisPos = _j.WorldPosition(_rig.Pelvis);
            var pelvisRot = _j.WorldRotation(_rig.Pelvis);
            for (int k = 1; k < chainLength; k++)
            {
                var kinematic = _j.WorldPosition(chain[k]) + sourceToTarget * s.Fractions[k];
                positions[k] = Vector3.Transform(s.Positions[k], pelvisRot) + pelvisPos;
                float timeConstant = 1f / ClampRescale(s.FixupRms, 0f, 0.5f, 0.2f, 8f);
                positions[k] = Vector3.Lerp(positions[k], kinematic, Interpolant(timeConstant, frameDt));
            }
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                int finished = 0;
                for (int k = 1; k < chainLength; k++)
                {
                    var acceleration = (positions[k - 1] - positions[k]) * ((Vector3.Distance(positions[k], positions[k - 1]) - s.Lengths[k - 1]) * JointLengthK);
                    acceleration += (positions[k + 1] - positions[k]) * ((Vector3.Distance(positions[k + 1], positions[k]) - s.Lengths[k]) * JointLengthK);
                    if (acceleration.LengthSquared() < MinAccelerationSquared) finished++;
                    velocities[k] *= 0.7f;
                    positions[k] += velocities[k] + acceleration * 0.5f;
                    velocities[k] += acceleration;
                }
                if (iteration >= MinIterationCount && finished == chainLength - 1) break;
            }
            for (int k = chainLength; k > 0; k--)
            {
                int cur = chain[k];
                var parentRot = _j.WorldRotation(_rig.Parent(cur));
                var curRot = _j.WorldRotation(cur);
                var targetAt = positions[k - 1] - positions[k];
                // at the bottom of the chain the point on the collision volume stands for the child joint
                var currentAt = k == 1
                    ? VolumePos(s.Source, c.SourceOffset) - _j.WorldPosition(cur)
                    : Vector3.Transform(LocalPosition(chain[k - 1], tracks, time), curRot);
                var fixup = Matrix4x4.CreateFromQuaternion(ShortestArc(currentAt, targetAt));
                var targetWorld = curRot * fixup;
                var local = Quaternion.CreateFromRotationMatrix(targetWorld * Matrix4x4.Transpose(parentRot));
                if (weight != 1f) local = Nlerp(weight, StateRotation(pose, cur, tracks, time), local);
                pose.Rotations[cur] = work.Rotations[cur] = local;
                _j.Invalidate();
            }
            var endLocal = Quaternion.CreateFromRotationMatrix(endRot * Matrix4x4.Transpose(_j.WorldRotation(_rig.Parent(chain[0]))));
            pose.Rotations[chain[0]] = weight == 1f ? endLocal : Nlerp(weight, StateRotation(pose, chain[0], tracks, time), endLocal);

            // the simulated positions in the pelvis frame, and how far they moved this frame
            var pelvisPos2 = _j.WorldPosition(_rig.Pelvis);
            var invPelvis = Matrix4x4.Transpose(_j.WorldRotation(_rig.Pelvis));
            s.FixupRms = 0f;
            float deltaTime = MathF.Max(0.02f, deltaT);
            for (int k = 1; k < chainLength; k++)
            {
                var np = Vector3.Transform(positions[k] - pelvisPos2, invPelvis);
                s.FixupRms += Vector3.DistanceSquared(np, s.Positions[k]) / deltaTime;
                s.Positions[k] = np;
            }
            s.FixupRms *= 1f / (s.Total * (chainLength - 1));
            s.FixupRms = MathF.Sqrt(s.FixupRms);
        }
        else if (chain[0] == _rig.Pelvis && tracks[chain[0]] is { PositionTimes.Length: > 0 })
        {
            // the simple positional constraint: the pelvis moves by what is left between the source point and the target
            var delta = sourceToTarget * weight;
            var from = work.PelvisPosition ?? KeyPosition(chain[0], tracks, _stale);
            pose.PelvisPosition = from + delta;
        }
        // the joints go back to the frame before's pose, for the constraints that follow
        for (int k = 0; k <= chainLength; k++) work.Rotations[chain[k]] = state.Prev.Rotations[chain[k]];
        _j.Invalidate();
    }

    private float _stale;

    private Vector3 KeyPosition(int node, JointTrack?[] tracks, float t) =>
        tracks[node] is { PositionTimes.Length: > 0 } tr ? AnimationClip.PositionAt(tr, t) : Vector3.Zero;

    private Vector3 LocalPosition(int node, JointTrack?[] tracks, float t)
    {
        if (tracks[node] is { PositionTimes.Length: > 0 } tr) return AnimationClip.PositionAt(tr, t);
        return _j.Shape is { } sp ? sp.Position[node] : _rig.DefaultPosition[node];
    }

    /// <summary>The joint state's rotation: what an earlier constraint of the frame left there, else the motion's key.</summary>
    private Quaternion StateRotation(ConstraintPose pose, int node, JointTrack?[] tracks, float t) => pose.Rotations[node] ?? KeyRotation(node, tracks, t);

    private Quaternion KeyRotation(int node, JointTrack?[] tracks, float t) =>
        tracks[node] is { RotationTimes.Length: > 0 } tr ? AnimationClip.RotationAt(tr, t) : _rig.DefaultRotation[node];

    // ---- helpers from the official sources ----

    /// <summary>LLSmoothInterpolation::calcInterpolant: 1 - 2^(-dt / timeConstant), kept in 0..1.</summary>
    public static float Interpolant(float timeConstant, float dt) =>
        timeConstant == 0f ? 1f : Math.Clamp(1f - MathF.Pow(2f, -dt / timeConstant), 0f, 1f);

    private static float Lerp(float a, float b, float interpolant) => a * (1f - interpolant) + b * interpolant;

    /// <summary>clamp_rescale (llmath.h).</summary>
    public static float ClampRescale(float x, float x1, float x2, float y1, float y2)
    {
        float r = y1 + (x - x1) * (y2 - y1) / (x2 - x1);
        return y1 < y2 ? Math.Clamp(r, y1, y2) : Math.Clamp(r, y2, y1);
    }

    /// <summary>LLQuaternion::shortestArc: the rotation that takes the direction a to the direction b.</summary>
    public static Quaternion ShortestArc(Vector3 a, Vector3 b)
    {
        float ab = Vector3.Dot(a, b);
        var cross = Vector3.Cross(a, b);
        float cc = cross.LengthSquared();
        if (ab * ab + cc == 0f) return Quaternion.Identity;
        if (cc > 0f)
        {
            float s = MathF.Sqrt(ab * ab + cc) + ab;
            float m = 1f / MathF.Sqrt(cc + s * s);
            return new Quaternion(cross.X * m, cross.Y * m, cross.Z * m, s * m);
        }
        if (ab < 0f)
        {
            var c = a - b;
            float m = MathF.Sqrt(c.X * c.X + c.Y * c.Y);
            if (m > 1e-6f) return new Quaternion(-c.Y / m, c.X / m, 0f, 0f);
            return new Quaternion(1f, 0f, 0f, 0f);
        }
        return Quaternion.Identity;
    }

    /// <summary>nlerp (llquaternion.cpp): the normalised blend, taking the shorter way round.</summary>
    public static Quaternion Nlerp(float t, Quaternion a, Quaternion b)
    {
        float inv = 1f - t;
        var q = Quaternion.Dot(a, b) < 0f ? new Quaternion(a.X * inv - b.X * t, a.Y * inv - b.Y * t, a.Z * inv - b.Z * t, a.W * inv - b.W * t)
                                          : new Quaternion(a.X * inv + b.X * t, a.Y * inv + b.Y * t, a.Z * inv + b.Z * t, a.W * inv + b.W * t);
        return Quaternion.Normalize(q);
    }
}
