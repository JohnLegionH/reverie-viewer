// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The adapter's side of neighbour regions: the library keeps child circuits only when the session asks for them.</summary>
public class NeighbourTransportTests
{
    [Fact]
    public void A_new_client_keeps_one_region_and_the_login_request_switches_child_circuits_on_or_off()
    {
        using var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        var settings = t.Client.Settings;
        Assert.False(settings.Agent.MultipleSims);

        LmvTransport.ApplyNeighbours(t.Client, true);
        Assert.True(settings.Agent.MultipleSims);

        LmvTransport.ApplyNeighbours(t.Client, false);
        Assert.False(settings.Agent.MultipleSims);
    }
}
