// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Avatars;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The height a shape editor shows. The official viewer shows the body size's vertical figure (<see cref="BodyMeasure.Height"/>) in feet;
/// Firestorm adds a fixed 0.195 m to it first (a fudge factor its source says was measured against prims in world), so the same avatar
/// reads about 0.64 ft taller there. Both figures are reported so ours can be compared with what either viewer shows. Pure, parallel.
/// </summary>
public class HeightDisplayTests
{
    [Fact]
    public void The_official_figure_is_the_body_size_in_feet_and_Firestorms_adds_a_fixed_195_millimetres_first()
    {
        var m = new BodyMeasure(2.0847f, 1.0f);
        Assert.Equal(0.195f, BodyMeasure.FirestormAddition);
        Assert.Equal(6.84f, MathF.Round(m.OfficialFeet, 2));
        Assert.Equal(2.2797f, m.FirestormHeight, 4);
        Assert.Equal(7.48f, MathF.Round(m.FirestormFeet, 2));
        Assert.Equal(0.64f, MathF.Round(m.FirestormFeet - m.OfficialFeet, 2));
    }

    [Fact]
    public void A_shaped_default_avatars_two_figures_differ_by_exactly_the_addition()
    {
        var lad = Encoding.UTF8.GetBytes("""
            <linden_avatar><param id="33" group="0" name="Height" value_min="-2" value_max="2"><param_skeleton><bone name="mTorso" scale="0 0 0.5"/></param_skeleton></param></linden_avatar>
            """);
        var sk = Encoding.UTF8.GetBytes("""
            <linden_skeleton version="2.0"><bone name="mPelvis" pos="0 0 1" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mTorso" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base"><bone name="mChest" pos="0 0 0.2" rot="0 0 0" scale="1 1 1" support="base">
                <bone name="mNeck" pos="0 0 0.25" rot="0 0 0" scale="1 1 1" support="base"><bone name="mHead" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base"><bone name="mSkull" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base"/></bone></bone></bone></bone>
              <bone name="mHipLeft" pos="0 0.1 -0.05" rot="0 0 0" scale="1 1 1" support="base"><bone name="mKneeLeft" pos="0 0 -0.5" rot="0 0 0" scale="1 1 1" support="base">
                <bone name="mAnkleLeft" pos="0 0 -0.5" rot="0 0 0" scale="1 1 1" support="base"><bone name="mFootLeft" pos="0 0 -0.1" rot="0 0 0" scale="1 1 1" support="base"/></bone></bone></bone>
            </bone></linden_skeleton>
            """);
        var rig = new AvatarRig(CharacterSkeleton.Parse(sk, lad));
        var shape = AvatarShape.From(rig, ShapeDefinition.Parse(lad), new VisualParamSet([255], [33]));
        var m = shape.Measure(null)!.Value;
        Assert.Equal(m.Height + 0.195f, m.FirestormHeight, 5);
        Assert.Equal((m.Height + 0.195f) / 0.3048f, m.FirestormFeet, 4);
        Assert.True(m.FirestormFeet > m.OfficialFeet + 0.6f);
    }
}
