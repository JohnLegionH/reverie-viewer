// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Crossing into a region the session already holds a circuit to. The library's own crossing opens a second circuit to the destination
/// and waits for a region handshake that never comes (the region already has the agent as a child), then recurses in its seed-capability
/// retry until the process dies; with neighbours on, the adapter takes the crossing over for a held destination and leaves every other
/// crossing to the library.
/// </summary>
public class CrossingHookTests
{
    [Fact]
    public void The_library_handler_the_takeover_replaces_exists_in_the_pinned_library()
    {
        var handler = LmvTransport.LibraryCrossingHandler();
        Assert.NotNull(handler);
        var parameters = handler!.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal(typeof(LibreMetaverse.Simulator), parameters[2].ParameterType);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void A_held_destination_is_taken_over_only_with_neighbours_on(bool neighbours, bool held, bool promoted) =>
        Assert.Equal(promoted, LmvTransport.PlanCrossing(neighbours, held) == LmvTransport.CrossingPlan.Promote);
}
