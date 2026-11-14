// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Meshing;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A whole sphere is one face at every level of detail, as the official viewer builds it (llvolume.cpp, LLProfile::generate, the half-circle profile:
/// the profile is open, and so gets the two edge faces, only when it covers less than the whole range). A sphere with a profile cut keeps its edge faces.
/// Pure, runs in parallel.
/// </summary>
public class PrimSphereFacesTests
{
    private static readonly float[] Detail = [1f, 1.5f, 2.5f, 4f];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_whole_sphere_is_one_face_at_every_level(int level)
    {
        var sphere = new PrimShape { ProfileCurve = 5, PathCurve = 32 };
        Assert.Single(PrimGeometry.Build(sphere, Detail[level]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_sphere_with_a_profile_cut_keeps_its_two_edge_faces_at_every_level(int level)
    {
        var cut = new PrimShape { ProfileCurve = 5, PathCurve = 32, ProfileBegin = 0.25f, ProfileEnd = 0.75f };
        Assert.Equal(3, PrimGeometry.Build(cut, Detail[level]).Count);
    }
}
