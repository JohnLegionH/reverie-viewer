// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What the viewer tells the person about the rendering backend: which one is in use and, when the one wanted could not run, that and
/// why (a one-time notice and a line on the Graphics page of Preferences). A backend started by an explicit engine flag, or the one the
/// person chose, is never reported as a failure.
/// </summary>
public class BackendStatusTests
{
    private static readonly DeviceReport VulkanTooOld = new("Example Adapter", "1.2.175", "12_0");
    private static readonly DeviceReport BothFine = new("Example Adapter", "1.3.250", "12_0");

    [Fact]
    public void The_decision_says_why_the_wanted_backend_was_set_aside()
    {
        var d = BackendChoice.Decide("vulkan", VulkanTooOld);
        Assert.True(d.FellBack);
        Assert.Equal("d3d12", d.Driver);
        Assert.Contains("Vulkan 1.2.175", d.WhyNot);
        Assert.Null(BackendChoice.Decide("vulkan", BothFine).WhyNot);
    }

    [Fact]
    public void A_backend_that_runs_as_chosen_is_in_use_and_nothing_failed()
    {
        var s = BackendStatus.From("auto", "vulkan", [], BackendChoice.Decide("auto", BothFine));
        Assert.Equal("vulkan", s.InUse);
        Assert.False(s.FellBack);
        Assert.Null(s.Wanted);
    }

    [Fact]
    public void A_backend_the_person_chose_that_started_after_a_restart_is_not_a_failure()
    {
        var s = BackendStatus.From("d3d12", "d3d12", ["d3d12"], BackendChoice.Decide("d3d12", BothFine));
        Assert.False(s.FellBack);
    }

    [Fact]
    public void A_wanted_backend_below_the_floor_is_reported_with_the_reason_from_the_probe()
    {
        var s = BackendStatus.From("auto", "d3d12", ["d3d12"], BackendChoice.Decide("auto", VulkanTooOld));
        Assert.True(s.FellBack);
        Assert.Equal(("vulkan", "d3d12"), (s.Wanted, s.InUse));
        Assert.Contains("Vulkan 1.2.175", s.Why);
    }

    [Fact]
    public void A_backend_the_engine_refused_after_the_probe_passed_it_is_reported_too()
    {
        var s = BackendStatus.From("vulkan", "d3d12", ["d3d12"], BackendChoice.Decide("vulkan", BothFine));
        Assert.True(s.FellBack);
        Assert.Equal(("vulkan", "d3d12"), (s.Wanted, s.InUse));
        Assert.False(string.IsNullOrWhiteSpace(s.Why));
    }

    [Fact]
    public void A_backend_forced_by_an_engine_flag_with_no_restart_is_not_reported_as_a_failure()
    {
        var s = BackendStatus.From("auto", "d3d12", [], BackendChoice.Decide("auto", BothFine));
        Assert.False(s.FellBack);
        Assert.Equal("d3d12", s.InUse);
    }

    [Fact]
    public void Without_a_device_report_nothing_is_claimed_beyond_the_backend_in_use()
    {
        var s = BackendStatus.From("vulkan", "vulkan", [], null);
        Assert.Equal("vulkan", s.InUse);
        Assert.False(s.FellBack);
    }

    [Fact]
    public void The_texts_name_the_backends_and_the_reason()
    {
        var strings = StringTable.English();
        var s = BackendStatus.From("auto", "d3d12", ["d3d12"], BackendChoice.Decide("auto", VulkanTooOld));
        Assert.Equal("Rendering backend in use: Direct3D 12", s.InUseText(strings));
        string notice = s.NoticeText(strings)!;
        Assert.Contains("Vulkan", notice);
        Assert.Contains("Direct3D 12", notice);
        Assert.Contains("1.2.175", notice);
        Assert.Null(BackendStatus.From("auto", "vulkan", [], null).NoticeText(strings));
    }
}
