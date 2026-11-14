// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using NvTools;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Which runs go through crash-watch (tools\TestWindowPlacement.cs, the file tools\godot.ps1 compiles): runs of the test harness
/// have it on unless it is switched off, other programs only when asked.
/// </summary>
public class CrashWatchPolicyTests
{
    private const string Render = "--rendering-driver vulkan -- --test-render --tr-out=/work/out --tr-tag=golden-vulkan";
    private const string Smoke = "--rendering-driver d3d12 -- --s1-out=/work/out --s1-tag=d3d12";
    private const string Editor = "--headless --editor --path /work/project --build-solutions --quit";
    private const string PlainStart = "-- --character=/work/character";

    [Theory]
    [InlineData(Render)]
    [InlineData(Smoke)]
    public void A_harness_run_has_crash_watch_on_by_default(string argLine)
    {
        Assert.True(CrashWatchPolicy.Enabled(argLine, forcedOn: false, forcedOff: false, measuring: false));
    }

    [Theory]
    [InlineData(Editor)]
    [InlineData(PlainStart)]
    [InlineData("")]
    public void The_editor_the_export_and_a_plain_start_are_left_alone(string argLine)
    {
        Assert.False(CrashWatchPolicy.Enabled(argLine, forcedOn: false, forcedOff: false, measuring: false));
    }

    [Theory]
    [InlineData(Render)]
    [InlineData(Smoke)]
    [InlineData(Editor)]
    public void The_switch_turns_it_off_for_any_run_and_wins_over_asking_for_it(string argLine)
    {
        Assert.False(CrashWatchPolicy.Enabled(argLine, forcedOn: false, forcedOff: true, measuring: false));
        Assert.False(CrashWatchPolicy.Enabled(argLine, forcedOn: true, forcedOff: true, measuring: false));
    }

    [Theory]
    [InlineData(Editor)]
    [InlineData(PlainStart)]
    public void Asking_for_it_turns_it_on_for_any_program(string argLine)
    {
        Assert.True(CrashWatchPolicy.Enabled(argLine, forcedOn: true, forcedOff: false, measuring: false));
    }

    [Fact]
    public void A_measuring_run_follows_the_measured_decision_unless_asked_for_it()
    {
        Assert.Equal(!CrashWatchPolicy.MeasuringRunsKeepItOff, CrashWatchPolicy.Enabled(Render, forcedOn: false, forcedOff: false, measuring: true));
        Assert.True(CrashWatchPolicy.Enabled(Render, forcedOn: true, forcedOff: false, measuring: true));
        Assert.False(CrashWatchPolicy.Enabled(Render, forcedOn: false, forcedOff: true, measuring: true));
    }
}
