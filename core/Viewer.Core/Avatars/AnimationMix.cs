// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llpose.cpp
 * @brief Implementation of LLPose class.
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

// Adapted 2026 by Legion Builds. The way motions are ordered by priority and blended by weight, and the way an additive motion is added on top
// (LLJointStateBlender::addJointState and blendJointStates) follow the file named above, rewritten in C# for our own types. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>One motion an avatar plays: its animation, and the region's sequence number for it (a higher one started later).</summary>
public readonly record struct PlayingMotion(AnimationClip Clip, int Sequence);

/// <summary>A motion that takes part in a blend: its clip, its timing (so its weight at a time) and its order (a higher one started later).</summary>
public readonly record struct BlendInput(AnimationClip Clip, MotionTiming Timing, int Order);

/// <summary>A built-in motion that takes part in a blend, with its timing and its order as <see cref="BlendInput"/> has them.</summary>
public readonly record struct ProceduralInput(IProceduralMotion Motion, MotionTiming Timing, int Order);

/// <summary>
/// One joint animated by several motions, blended as the official viewer blends them (llcharacter/llpose.cpp LLJointStateBlender::addJointState,
/// 196-230, and blendJointStates, 238 on): the motions are taken from the highest priority down, a newer motion first among equals, and each one's
/// value is blended in by its weight until the weights add up to one; a motion with no weight is left out. A joint's own priority is used; -1
/// (LLJoint::USE_MOTION_PRIORITY, llkeyframemotion.cpp:1519-1534) stands for the motion's priority. Each motion is read at its own time, the
/// time since it started. Rotation and position are blended apart, each over the motions that have keys for it. When no motion has any weight yet
/// the first one's value is used, so a joint is never dropped into the bind pose for a frame.
/// An additive motion (a built-in one such as the body's sway) does not take part in that blend: its value, nlerped from the identity by the share
/// of weight still free, is added on top of what the others blend to, as <c>added * blended</c> in the official product (a position is summed). A
/// joint no other motion turns starts from the skeleton's own rotation.
/// </summary>
public sealed class BlendedJoint
{
    internal readonly record struct Layer(JointTrack? Track, AnimationClip? Clip, MotionTiming Timing, IProceduralMotion? Motion, string Joint, bool Additive)
    {
        public Quaternion Rotation(double t) => Motion is null
            ? AnimationClip.RotationAt(Track!, Clip!.TimeAt(t - Timing.Activation))
            : Motion.Rotation(Joint, t - Timing.Activation);

        public Vector3 Position(double t) => Motion is null
            ? AnimationClip.PositionAt(Track!, Clip!.TimeAt(t - Timing.Activation))
            : Motion.Position(Joint, t - Timing.Activation);
    }

    private readonly Layer[] _rotation, _position;
    private readonly Quaternion _default;

    internal BlendedJoint(List<(int Priority, int Order, Layer Layer, bool HasRotation, bool HasPosition)> layers, Quaternion defaultRotation)
    {
        var sorted = layers.OrderByDescending(l => l.Priority).ThenByDescending(l => l.Order).ToList();
        _rotation = sorted.Where(l => l.HasRotation).Select(l => l.Layer).ToArray();
        _position = sorted.Where(l => l.HasPosition).Select(l => l.Layer).ToArray();
        _default = defaultRotation;
    }

    public bool HasRotation => _rotation.Length > 0;
    public bool HasPosition => _position.Length > 0;

    /// <summary>The blended rotation at the caller's time <paramref name="t"/>.</summary>
    public Quaternion Rotation(float t)
    {
        if (_rotation.Length == 0) return Quaternion.Identity;
        bool any = false, addedAny = false;
        double sum = 0;
        Quaternion blended = default, added = Quaternion.Identity;
        foreach (var l in _rotation)
        {
            double w = l.Timing.Weight(t);
            if (w <= 0) continue;
            var value = l.Rotation(t);
            if (l.Additive)
            {
                double free = Math.Min(1.0, w + sum) - sum;
                added = OfficialMath.Then(OfficialMath.Nlerp((float)free, added, value), added);
                addedAny = true;
                continue;
            }
            if (!any) { blended = value; sum = w; any = true; }
            else
            {
                double next = Math.Min(1.0, w + sum);
                blended = OfficialMath.Nlerp((float)(sum / next), value, blended);
                sum = next;
            }
            if (sum >= 1.0) break;
        }
        if (!any)
        {
            // no weighted motion yet: the first one that is not additive shows as it is (never the bind pose for a frame); with only additive
            // motions the joint starts from its own rotation
            int first = Array.FindIndex(_rotation, l => !l.Additive);
            blended = first >= 0 ? _rotation[first].Rotation(t) : _default;
        }
        return addedAny ? OfficialMath.Then(added, blended) : blended;
    }

    /// <summary>The blended position offset at the caller's time <paramref name="t"/>.</summary>
    public Vector3 Position(float t)
    {
        if (_position.Length == 0) return Vector3.Zero;
        bool any = false;
        double sum = 0;
        Vector3 blended = default, added = Vector3.Zero;
        foreach (var l in _position)
        {
            double w = l.Timing.Weight(t);
            if (w <= 0) continue;
            var value = l.Position(t);
            if (l.Additive)
            {
                double free = Math.Min(1.0, w + sum) - sum;
                added += value * (float)free;
                continue;
            }
            if (!any) { blended = value; sum = w; any = true; }
            else
            {
                double next = Math.Min(1.0, w + sum);
                blended = Vector3.Lerp(value, blended, (float)(sum / next));
                sum = next;
            }
            if (sum >= 1.0) break;
        }
        if (!any)
        {
            int first = Array.FindIndex(_position, l => !l.Additive);
            blended = first >= 0 ? _position[first].Position(t) : Vector3.Zero;
        }
        return blended + added;
    }
}

/// <summary>
/// The animations an avatar plays at once, put together into one clip. Each joint is a <see cref="BlendedJoint"/> over the motions that animate it.
/// The clip made has no end: its time is the caller's clock, and each motion is read from the time it started.
/// </summary>
public static class AnimationMix
{
    /// <summary>The length of the made clip's timeline, one that never ends in practice.</summary>
    private const float Forever = 1_000_000f;

    /// <summary>The clip that poses an avatar by <paramref name="motions"/>, each at full weight and started at time 0; null when there are none.</summary>
    public static AnimationClip? Build(IReadOnlyList<PlayingMotion> motions) =>
        Blend(motions.Select(m => new BlendInput(m.Clip, new MotionTiming(0, 0, 0, null, 0), m.Sequence)).ToList());

    /// <summary>The clip that poses an avatar by <paramref name="inputs"/> and the built-in <paramref name="procedurals"/>; <paramref name="defaultRotation"/> gives a joint's own rotation, which an additive motion is added to when no other motion turns the joint.</summary>
    public static AnimationClip? Blend(IReadOnlyList<BlendInput> inputs, IReadOnlyList<ProceduralInput> procedurals, Func<string, Quaternion?>? defaultRotation)
    {
        if (inputs.Count == 0 && procedurals.Count == 0) return null;
        var byJoint = new Dictionary<string, List<(int, int, BlendedJoint.Layer, bool, bool)>>(StringComparer.Ordinal);
        foreach (var m in inputs)
            foreach (var track in m.Clip.Tracks)
            {
                int priority = track.Priority < 0 ? m.Clip.Priority : track.Priority;
                if (!byJoint.TryGetValue(track.Joint, out var list)) byJoint[track.Joint] = list = new();
                list.Add((priority, m.Order, new BlendedJoint.Layer(track, m.Clip, m.Timing, null, track.Joint, false), track.RotationTimes.Length > 0, track.PositionTimes.Length > 0));
            }
        foreach (var p in procedurals)
            foreach (var joint in p.Motion.RotationJoints.Union(p.Motion.PositionJoints))
            {
                if (!byJoint.TryGetValue(joint, out var list)) byJoint[joint] = list = new();
                list.Add((p.Motion.Priority, p.Order, new BlendedJoint.Layer(null, null, p.Timing, p.Motion, joint, p.Motion.Additive),
                    p.Motion.RotationJoints.Contains(joint), p.Motion.PositionJoints.Contains(joint)));
            }
        var tracks = new List<JointTrack>(byJoint.Count);
        foreach (var (joint, layers) in byJoint)
        {
            var blend = new BlendedJoint(layers, defaultRotation?.Invoke(joint) ?? Quaternion.Identity);
            // the track carries one key as a marker that the joint has rotation or position; the values come from the blend
            tracks.Add(new JointTrack(joint, 0,
                blend.HasRotation ? [0f] : [], blend.HasRotation ? [Quaternion.Identity] : [],
                blend.HasPosition ? [0f] : [], blend.HasPosition ? [Vector3.Zero] : []) { Blend = blend });
        }
        int priorityOfMix = Math.Max(inputs.Count == 0 ? 0 : inputs.Max(m => m.Clip.Priority), procedurals.Count == 0 ? 0 : procedurals.Max(p => p.Motion.Priority));
        return new AnimationClip
        {
            Duration = Forever, Loop = true, LoopIn = 0, LoopOut = Forever,
            Priority = priorityOfMix, EaseIn = 0, EaseOut = 0, Emote = "", Tracks = tracks, Transient = true,
            Constraints = inputs.SelectMany(m => m.Clip.Constraints.Select(c => c.InMix(m.Clip, m.Timing, MaskOf(m, inputs, procedurals)))).ToArray(),
        };
    }

    /// <summary>The joint mask of a constraint of <paramref name="own"/>: the blend's motions that are not additive, newest first (an animation before a built-in
    /// motion that started at the same time), each with the priority it turns its joints at.</summary>
    private static JointMask MaskOf(BlendInput own, IReadOnlyList<BlendInput> inputs, IReadOnlyList<ProceduralInput> procedurals)
    {
        var motions = new List<(int Order, int Kind, MaskMotion Motion, object Source)>();
        foreach (var m in inputs)
        {
            var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var track in m.Clip.Tracks)
                if (track.RotationTimes.Length > 0) priorities[track.Joint] = track.Priority < 0 ? m.Clip.Priority : track.Priority;
            motions.Add((m.Order, 0, new MaskMotion(m.Timing, priorities), m));
        }
        foreach (var p in procedurals)
        {
            if (p.Motion.Additive) continue;   // an additive motion is run in its own pass
            motions.Add((p.Order, 1, new MaskMotion(p.Timing, p.Motion.RotationJoints.ToDictionary(j => j, _ => p.Motion.Priority, StringComparer.Ordinal)), p));
        }
        var ordered = motions.OrderByDescending(x => x.Order).ThenBy(x => x.Kind).ToList();
        int at = ordered.FindIndex(x => x.Source is BlendInput b && b.Equals(own));
        return new JointMask(ordered.Select(x => x.Motion).ToArray(), at, own.Clip.Priority);
    }
    /// <summary>The clip that poses an avatar by <paramref name="inputs"/>, each with its own weight over time; null when there are none.</summary>
    public static AnimationClip? Blend(IReadOnlyList<BlendInput> inputs) => Blend(inputs, [], null);
}
