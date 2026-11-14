// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llpolyskeletaldistortion.cpp
 * @brief Implementation of LLPolySkeletalDistortion classes
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
 * @file lldriverparam.cpp
 * @brief A visual parameter that drives (controls) other visual parameters.
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
 * @File llavatarappearance.cpp
 * @brief Implementation of LLAvatarAppearance class
 *
 * $LicenseInfo:firstyear=2012&license=viewerlgpl$
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
 * @file llquantize.h
 * @brief useful routines for quantizing floats to various length ints
 * and back out again
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
 * @file xform.cpp
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


// Adapted 2026 by Legion Builds. The driver parameters' trapezoid (DrivenWeight), the skeletal parameters' sum of weighted scale and
// offset deltas with the collision volumes inheriting their bone's scale change (From), the quantised weight (WeightOf) and the
// body size and pelvis-to-foot height (Measure) follow the files named above, rewritten in C# for our own skeleton types; the way a joint's scale
// moves its children's offsets (Viewer.Core.Avatars.AvatarJoints) follows xform.cpp. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

using System.Numerics;
using Viewer.Core.World;

namespace Viewer.Core.Avatars;

/// <summary>The body figures the official viewer works out from the shaped skeleton.</summary>
/// <param name="Height">The body size's vertical figure in metres: the figure the shape editor shows (in feet or metres), and what the
/// viewer tells the region as the avatar's height. The hover height is not part of it.</param>
/// <param name="PelvisToFoot">From the pelvis down to the soles, in metres.</param>
public readonly record struct BodyMeasure(float Height, float PelvisToFoot)
{
    public const float MetresPerFoot = 0.3048f;

    /// <summary>The fixed amount Firestorm's shape editor adds to the body size before it shows the height (a measured fudge factor there, not part of the body size).</summary>
    public const float FirestormAddition = 0.195f;

    /// <summary>The height as the official viewer's shape editor shows it: the body size, in feet.</summary>
    public float OfficialFeet => Height / MetresPerFoot;

    /// <summary>The height as Firestorm's shape editor shows it, in metres: the body size plus <see cref="FirestormAddition"/>.</summary>
    public float FirestormHeight => Height + FirestormAddition;

    /// <summary>The same in feet.</summary>
    public float FirestormFeet => FirestormHeight / MetresPerFoot;
}

/// <summary>
/// An avatar's shaped skeleton: the scale and position of every node of its <see cref="AvatarRig"/> once its visual parameters
/// are applied, before any worn mesh's joint overrides (<see cref="JointOverrides"/>, which win over the shape's positions and can
/// pin a joint's scale; <see cref="PositionOf"/>, <see cref="ScaleOf"/>). Pure data; immutable after <see cref="From"/>.
/// <para>The order, as the official viewer has it: the transmitted weights are set in parameter-id order and each driver parameter
/// passes its weight on to the parameters it drives; the avatar is male when the "male" weight is above one half; every skeletal
/// parameter then adds its weight (its default, when it is for the other sex) times its scale and offset deltas to the skeleton's own
/// values; and a worn mesh's position override replaces the joint's position afterwards, whatever the shape asked.</para>
/// </summary>
public sealed class AvatarShape
{
    /// <summary>The official viewer's approximate correction from the head joint to the top of the head.</summary>
    public const float HeadCorrection = 1.41421356f;

    private readonly Dictionary<int, float> _weights;
    private readonly float? _hover;

    private AvatarShape(AvatarRig rig, Vector3[] position, Vector3[] scale, bool male, Dictionary<int, float> weights, float? hover = null)
    {
        Rig = rig;
        Position = position;
        Scale = scale;
        Male = male;
        _weights = weights;
        _hover = hover;
    }

    public AvatarRig Rig { get; }

    /// <summary>Each node's position relative to its parent, shape applied (attachment points keep their offsets).</summary>
    public Vector3[] Position { get; }

    /// <summary>Each node's own scale, shape applied: one for a bone with no shape, a collision volume's box size changed with its bone.</summary>
    public Vector3[] Scale { get; }

    public bool Male { get; }

    /// <summary>The weight a parameter ended with (drivers applied); false for a parameter the definition lacks.</summary>
    public bool TryGetWeight(int paramId, out float weight) => _weights.TryGetValue(paramId, out weight);

    /// <summary>Every parameter's weight (drivers applied), by id.</summary>
    public IReadOnlyDictionary<int, float> Weights => _weights;

    /// <summary>The hover height in metres: the one given to <see cref="From"/> (see <see cref="HoverOverride"/>), else the Hover parameter's weight (0 when the avatar carries none).</summary>
    public float Hover => _hover ?? (_weights.TryGetValue(ShapeDefinition.HoverId, out var h) ? h : 0f);

    /// <summary>
    /// The hover to draw instead of the Hover parameter's weight, as the official viewer chooses it (llvoavatar.cpp:9912-9923): another
    /// avatar's hover is the AppearanceHover block's height, zero when its message had none (the parameter is not consulted); the own
    /// avatar ignores the message and uses its preference, which is the Hover parameter's weight (null here: no override).
    /// </summary>
    public static float? HoverOverride(bool isSelf, float? messageHover) => isSelf ? null : messageHover ?? 0f;

    /// <summary>The skeleton with no shape applied at all.</summary>
    public static AvatarShape Neutral(AvatarRig rig) => new(rig, (Vector3[])rig.DefaultPosition.Clone(), (Vector3[])rig.DefaultScale.Clone(), false, []);

    /// <summary>The quantised wire value as a weight in the parameter's range (a value within one step of zero is zero).</summary>
    public static float WeightOf(byte value, float lo, float hi)
    {
        float v = value * (1f / 255f);
        float delta = hi - lo;
        v = v * delta + lo;
        float maxError = delta * (1f / 255f);
        return MathF.Abs(v) < maxError ? 0f : v;
    }

    /// <summary>What a driver passes to one driven parameter at <paramref name="input"/>.</summary>
    public static float DrivenWeight(ShapeParam driver, DrivenEntry e, ShapeParam driven, float input)
    {
        float dmin = driven.Min, dmax = driven.Max;
        if (input <= e.Min1) return e.Min1 == e.Max1 && e.Min1 <= driver.Min ? dmax : dmin;
        if (input <= e.Max1) return dmin + (input - e.Min1) / (e.Max1 - e.Min1) * (dmax - dmin);
        if (input <= e.Max2) return dmax;
        if (input <= e.Min2) return dmax + (input - e.Max2) / (e.Min2 - e.Max2) * (dmin - dmax);
        return e.Max2 >= driver.Max ? dmax : dmin;
    }

    /// <summary>Every parameter's weight for an avatar: defaults, then the transmitted values in wire order, each driver passing its
    /// weight down (a driven parameter that is itself a driver passes it on).</summary>
    public static Dictionary<int, float> ResolveWeights(ShapeDefinition def, VisualParamSet vp)
    {
        var w = new Dictionary<int, float>();
        foreach (var p in def.Params.Values) w[p.Id] = p.Default;
        foreach (var p in def.InIdOrder) if (p.Driven.Count > 0) Set(def, w, p.Id, p.Default, 0);
        for (int i = 0; i < vp.Raw.Length && i < vp.WireOrder.Length; i++)
            if (def.TryGet(vp.WireOrder[i], out var p)) Set(def, w, p.Id, WeightOf(vp.Raw[i], p.Min, p.Max), 0);
        return w;
    }

    private static void Set(ShapeDefinition def, Dictionary<int, float> w, int id, float weight, int depth)
    {
        if (depth > 8 || !def.TryGet(id, out var p)) return;   // a cycle in the file is cut
        weight = Math.Clamp(weight, p.Min, p.Max);
        w[id] = weight;
        foreach (var d in p.Driven)
            if (def.TryGet(d.Id, out var driven)) Set(def, w, d.Id, DrivenWeight(p, d, driven, weight), depth + 1);
    }

    /// <summary>The shaped skeleton of an avatar with these transmitted parameters.</summary>
    public static AvatarShape From(AvatarRig rig, ShapeDefinition def, VisualParamSet vp, float? hover = null)
    {
        var w = ResolveWeights(def, vp);
        bool male = w.TryGetValue(ShapeDefinition.MaleId, out float m) && m > 0.5f;
        var position = (Vector3[])rig.DefaultPosition.Clone();
        var scale = (Vector3[])rig.DefaultScale.Clone();
        var sk = rig.Skeleton;
        foreach (var p in def.InIdOrder)
        {
            if (p.Bones.Count == 0) continue;
            float eff = ((int)p.Sex & (male ? (int)ShapeSex.Male : (int)ShapeSex.Female)) != 0 ? w[p.Id] : p.Default;
            if (eff == 0f) continue;
            foreach (var b in p.Bones)
            {
                int j = sk.IndexOf(b.Bone);
                if (j < 0) continue;
                scale[j] += eff * b.Scale;
                foreach (int c in sk.Children[j])
                    if (sk.Joints[c].Kind == JointKind.CollisionVolume) scale[c] += eff * (rig.DefaultScale[c] * b.Scale);   // a collision volume follows its bone
                if (b.Offset is { } o) position[j] += eff * o;
            }
        }
        // the volume morphs of the body sliders (belly, muscles, fat...) change collision volumes, which fitted mesh follows
        foreach (var (volume, effect) in BodyMorph.VolumeEffects(def.Morphs, w, male))
        {
            int v = sk.IndexOf(volume);
            if (v < 0 || sk.Joints[v].Kind != JointKind.CollisionVolume) continue;
            scale[v] += effect.Scale;
            position[v] += effect.Pos;
        }
        return new AvatarShape(rig, position, scale, male, w, hover);
    }

    /// <summary>The node's position: a worn mesh's winning override, else the shape's.</summary>
    public Vector3 PositionOf(int node, JointOverrides? overrides) =>
        overrides is not null && overrides.TryGet(node, out var o) ? o : Position[node];

    /// <summary>The node's scale: pinned to the skeleton's own when a worn mesh locks it, else the shape's.</summary>
    public Vector3 ScaleOf(int node, JointOverrides? overrides) =>
        overrides is not null && overrides.ScaleLocked(node) ? Rig.DefaultScale[node] : Scale[node];

    /// <summary>The body size and pelvis-to-foot height from the joints that matter, with <paramref name="overrides"/> applied (a mesh
    /// body's own joint positions change the height); null when the skeleton lacks one of them.</summary>
    public BodyMeasure? Measure(JointOverrides? overrides)
    {
        var sk = Rig.Skeleton;
        string[] names = ["mPelvis", "mSkull", "mNeck", "mChest", "mHead", "mTorso", "mHipLeft", "mKneeLeft", "mAnkleLeft", "mFootLeft"];
        var n = new int[names.Length];
        for (int i = 0; i < names.Length; i++) if ((n[i] = sk.IndexOf(names[i])) < 0) return null;
        float PZ(int i) => PositionOf(n[i], overrides).Z;
        float SZ(int i) => ScaleOf(n[i], overrides).Z;
        const int Pelvis = 0, Skull = 1, Neck = 2, Chest = 3, Head = 4, Torso = 5, Hip = 6, Knee = 7, Ankle = 8, Foot = 9;
        float pelvisToFoot = PZ(Hip) * SZ(Pelvis) - PZ(Knee) * SZ(Hip) - PZ(Ankle) * SZ(Knee) - PZ(Foot) * SZ(Ankle);
        float height = pelvisToFoot + HeadCorrection * (PZ(Skull) * SZ(Head)) + PZ(Head) * SZ(Neck) + PZ(Neck) * SZ(Chest)
            + PZ(Chest) * SZ(Torso) + PZ(Torso) * SZ(Pelvis);
        return new BodyMeasure(height, pelvisToFoot);
    }
}
