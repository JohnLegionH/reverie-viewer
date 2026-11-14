// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The camera's far plane that goes with a draw distance: as in the official viewer it is the draw distance itself. Plain .NET; every member builds its own objects, so the class runs in parallel.</summary>
public class DrawDistanceViewTests
{
    private const float EngineDefault = 4000f;

    [Theory]
    [InlineData(64, 64f)]
    [InlineData(128, 128f)]
    [InlineData(300, 300f)]
    [InlineData(1023, 1023f)]
    [InlineData(1024, 1024f)]
    [InlineData(10, 64f)]
    public void The_far_plane_is_the_distance_and_never_less_than_the_smallest(int metres, float expected)
    {
        Assert.Equal(expected, DrawDistanceView.FarPlane(metres, EngineDefault));
    }
}
