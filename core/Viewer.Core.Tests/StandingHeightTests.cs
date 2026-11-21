// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A standing avatar's feet are drawn on the ground: the region rests an avatar by its centre, the soles are half its height lower
/// (<see cref="Viewer.Shell.AvatarScene.Place"/>, pinned with the land in LandingTests), and the body hangs from the pelvis placed its
/// pelvis-to-foot height above the soles, so the foot joints of the rest pose touch the soles whatever the shape (llvoavatar.cpp
/// updateCharacter places the pelvis that way). Plain .NET, nothing shared, so the class runs in parallel.
/// </summary>
public class StandingHeightTests
{
    private static AvatarRig RealRig()
    {
        string dir = CharacterFactAttribute.Folder!;
        return new AvatarRig(CharacterSkeleton.Parse(File.ReadAllBytes(Path.Combine(dir, "avatar_skeleton.xml")), File.ReadAllBytes(Path.Combine(dir, "avatar_lad.xml"))));
    }

    private static AvatarShape Shaped(AvatarRig rig, byte size, bool male)
    {
        var def = ShapeDefinition.Parse(File.ReadAllBytes(Path.Combine(CharacterFactAttribute.Folder!, "avatar_lad.xml")));
        return AvatarShape.From(rig, def, new VisualParamSet([male ? (byte)255 : (byte)0, size, size], [80, 33, 692]));
    }

    /// <summary>Where the host draws the foot joints of a standing avatar relative to the soles it places (AvatarLayer puts each joint at
    /// <see cref="FootPlacement.NodeHeight"/> with the neutral pelvis-to-foot height plus the shape's lift): measured on main as 8.2 cm under
    /// the soles for every shape, the placement under which a standing avatar's feet are seen on the ground.</summary>
    private const float FootJointUnderSoles = -0.082f;

    [CharacterFact]
    public void Short_normal_and_tall_avatars_standing_have_their_feet_at_the_same_height_over_the_ground_as_now()
    {
        var rig = RealRig();
        var neutral = AvatarShape.Neutral(rig);
        float neutralPelvisToFoot = neutral.Measure(null)!.Value.PelvisToFoot;
        int left = rig.Find("mFootLeft"), right = rig.Find("mFootRight");
        const float soles = 25.0f;
        var heights = new List<float>();
        foreach (var (size, male) in new[] { ((byte)60, false), ((byte)128, false), ((byte)255, true) })
        {
            var shape = Shaped(rig, size, male);
            var joints = new AvatarJoints(rig) { Shape = shape };
            joints.Begin(null, 0f);   // the default pose: standing
            float lift = PoseComputer.LiftOf(neutral, neutralPelvisToFoot, shape, null, 0f);
            float l = FootPlacement.NodeHeight(soles, joints, left, neutralPelvisToFoot + lift);
            float r = FootPlacement.NodeHeight(soles, joints, right, neutralPelvisToFoot + lift);
            float height = shape.Measure(null)!.Value.Height;
            heights.Add(height);
            Assert.True(MathF.Abs(l - soles - FootJointUnderSoles) < 0.002f && MathF.Abs(r - soles - FootJointUnderSoles) < 0.002f,
                $"a {height:F2} m avatar standing: feet at {l - soles:+0.000;-0.000} and {r - soles:+0.000;-0.000} m from the soles, {FootJointUnderSoles:+0.000;-0.000} expected");
        }
        Assert.True(heights[0] < 1.5f && heights[2] > 2.1f, $"the shapes span short to tall: {string.Join(", ", heights)}");
    }
}
