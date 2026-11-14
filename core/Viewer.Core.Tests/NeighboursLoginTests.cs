// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The login screen's model hands the neighbour-regions switch to the session it makes; off unless set.</summary>
public class NeighboursLoginTests
{
    private const string Uri = "http://grid.example.org:8002/";

    [Fact]
    public void The_switch_reaches_the_session_options_and_is_off_by_default()
    {
        foreach (bool on in new[] { false, true })
        {
            SessionOptions? seen = null;
            var m = new LoginModel(StringTable.English(), opts => { seen = opts; return new ViewerSession(opts, new FakeTransport(), new CoreLog(new ListSink(), "test")); })
            {
                LoginUri = Uri, FirstName = "Test", LastName = "User", Neighbours = on,
            };
            m.SetPassword("pw");
            Assert.True(m.LoginAsync().GetAwaiter().GetResult());
            Assert.Equal(on, seen!.Neighbours);
            Assert.Equal(on, m.Session!.NeighboursEnabled);
        }
        Assert.False(new LoginModel(StringTable.English(), _ => throw new InvalidOperationException()).Neighbours);
    }
}
