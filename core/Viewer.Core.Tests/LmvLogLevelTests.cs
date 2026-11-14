// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Microsoft.Extensions.Logging;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Xunit;
using LogLevel = Viewer.Core.Diagnostics.LogLevel;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Viewer.Core.Tests;

/// <summary>
/// Two library messages that repeat all the time in a healthy session (an event-queue poll that came back empty, and one line per
/// parcel packet) are debug-level in the core's log, so the default level does not show them; other library warnings still do.
/// </summary>
public class LmvLogLevelTests
{
    private const string EmptyPoll = "Skipping LLSD parsing; server returned non-LLSD response from http://sim.example/cap: \"\"";
    private const string Overlay = "Parcel overlay with sequence ID of 3 received from sim.example";
    private const string Other = "Could not parse response (1) from sim.example event queue";

    private static ListSink Feed(LogLevel minimum, params string[] messages)
    {
        var sink = new ListSink();
        ILogger route = new LmvLogRoute(new CoreLog(new LevelFilterSink(sink, minimum), "test"));
        foreach (var m in messages)
            route.Log(MsLogLevel.Warning, new EventId(0), m, null, (s, _) => s);
        return sink;
    }

    [Fact]
    public void The_default_level_is_info()
    {
        Assert.Equal(LogLevel.Info, LevelFilterSink.DefaultMinimum);
        Assert.Equal(LogLevel.Info, LevelFilterSink.Parse(null));
        Assert.Equal(LogLevel.Info, LevelFilterSink.Parse("not a level"));
        Assert.Equal(LogLevel.Debug, LevelFilterSink.Parse("debug"));
        Assert.Equal(LogLevel.Warn, LevelFilterSink.Parse("WARN"));
    }

    [Fact]
    public void The_empty_poll_and_the_parcel_overlay_lines_do_not_appear_at_the_default_level()
    {
        var sink = Feed(LevelFilterSink.DefaultMinimum, EmptyPoll, Overlay);

        Assert.Empty(sink.Entries);
    }

    [Fact]
    public void They_appear_at_debug_level_and_as_debug_entries()
    {
        var sink = Feed(LogLevel.Debug, EmptyPoll, Overlay);

        Assert.Equal(2, sink.Entries.Count);
        Assert.All(sink.Entries, e => Assert.Equal(LogLevel.Debug, e.Level));
    }

    [Fact]
    public void Another_library_warning_still_appears_at_the_default_level_as_a_warning()
    {
        var sink = Feed(LevelFilterSink.DefaultMinimum, EmptyPoll, Other, Overlay);

        var e = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Warn, e.Level);
        Assert.Equal("lmv.warning", e.Event);
    }
}
