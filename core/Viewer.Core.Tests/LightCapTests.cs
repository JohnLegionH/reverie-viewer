// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>How many prim lights are drawn: the official viewer draws at most its RenderLocalLightCount setting (default 256) of the lights
/// nearest the camera, so a region full of lights costs a bounded amount (Docs/PROJECTOR-SPEC.md).</summary>
public class LightCapTests
{
    [Fact]
    public void The_cap_is_the_official_viewers_default_local_light_count()
    {
        Assert.Equal(256, LightRules.MaxLocalLights);
    }

    [Fact]
    public void Fewer_lights_than_the_cap_are_all_drawn()
    {
        var lights = Enumerable.Range(1, 5).Select(i => ((uint)i, new Vector3(i, 0, 0))).ToList();
        Assert.Equal(5, LightRules.Nearest(lights, Vector3.Zero, 256).Count);
    }

    [Fact]
    public void Over_the_cap_the_nearest_lights_to_the_eye_are_the_ones_drawn()
    {
        var lights = Enumerable.Range(1, 10).Select(i => ((uint)i, new Vector3(i * 10, 0, 0))).ToList();
        var drawn = LightRules.Nearest(lights, Vector3.Zero, 3);
        Assert.Equal(new uint[] { 1, 2, 3 }, drawn.OrderBy(x => x));
        var fromFar = LightRules.Nearest(lights, new Vector3(100, 0, 0), 3);
        Assert.Equal(new uint[] { 8, 9, 10 }, fromFar.OrderBy(x => x));
    }

    [Fact]
    public void A_cap_of_zero_draws_none()
    {
        Assert.Empty(LightRules.Nearest([(1u, Vector3.One)], Vector3.Zero, 0));
    }
}
