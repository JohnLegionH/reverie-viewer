// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The automatic backend choice, from a made-up device report so no graphics card is needed: each backend is held to the hardware floor,
/// the choice falls back to the other one without a question, a plain message appears only when neither passes, and --backend= overrides
/// the saved choice for one run. Plain .NET; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class BackendChoiceTests
{
    private static DeviceReport Report(string? vulkan, string? d3d12) => new("Example GPU", vulkan, d3d12);

    [Fact]
    public void Vulkan_1_2_with_Direct3D_12_at_12_0_picks_Direct3D_12()
    {
        var d = BackendChoice.Decide("auto", Report("1.2.175", "12_0"));
        Assert.Equal("d3d12", d.Driver);
        Assert.True(d.FellBack);
        Assert.Null(d.MessageKey);
        Assert.Contains("d3d12", d.LogLine);
        Assert.Contains("1.2.175", d.LogLine);
    }

    [Fact]
    public void Vulkan_1_4_with_Direct3D_12_below_the_floor_picks_Vulkan()
    {
        var d = BackendChoice.Decide("auto", Report("1.4.325", "11_1"));
        Assert.Equal("vulkan", d.Driver);
        Assert.False(d.FellBack);
        Assert.Null(d.MessageKey);
    }

    [Fact]
    public void Both_passing_keeps_the_default_Vulkan_for_auto()
    {
        var d = BackendChoice.Decide("auto", Report("1.3.0", "12_1"));
        Assert.Equal("vulkan", d.Driver);
        Assert.False(d.FellBack);
    }

    [Fact]
    public void Neither_passing_gives_no_backend_and_the_plain_message()
    {
        var d = BackendChoice.Decide("auto", Report("1.2.175", "11_1"));
        Assert.Null(d.Driver);
        Assert.Equal("startup.refused.neither", d.MessageKey);
        var text = StringTable.English().Get(d.MessageKey!, d.MessageArgs);
        Assert.Contains("Example GPU", text);
        Assert.Contains("1.2.175", text);
        Assert.Contains("11_1", text);
        Assert.DoesNotContain("{", text);
    }

    [Fact]
    public void A_backend_that_is_not_there_does_not_pass()
    {
        Assert.Equal("d3d12", BackendChoice.Decide("auto", Report(null, "12_0")).Driver);
        Assert.Equal("vulkan", BackendChoice.Decide("auto", Report("1.3.0", null)).Driver);
        Assert.Null(BackendChoice.Decide("auto", Report(null, null)).Driver);
        Assert.Null(BackendChoice.Decide("vulkan", Report("garbage", "")).Driver);
    }

    [Fact]
    public void A_forced_choice_that_passes_is_used()
    {
        var d = BackendChoice.Decide("d3d12", Report("1.4.325", "12_0"));
        Assert.Equal("d3d12", d.Driver);
        Assert.False(d.FellBack);
        Assert.Equal("d3d12", d.Requested);
    }

    [Fact]
    public void A_forced_choice_that_fails_the_floor_falls_back_and_says_so_in_one_line()
    {
        var d = BackendChoice.Decide("vulkan", Report("1.2.175", "12_0"));
        Assert.Equal("d3d12", d.Driver);
        Assert.True(d.FellBack);
        Assert.Equal("vulkan", d.Requested);
        Assert.DoesNotContain('\n', d.LogLine);
        Assert.Contains("vulkan", d.LogLine);
        Assert.Contains("d3d12", d.LogLine);

        var reverse = BackendChoice.Decide("d3d12", Report("1.4.325", "11_0"));
        Assert.Equal("vulkan", reverse.Driver);
        Assert.True(reverse.FellBack);
    }

    [Fact]
    public void A_forced_choice_with_no_passing_backend_shows_the_message()
    {
        var d = BackendChoice.Decide("d3d12", Report("1.2.175", "11_0"));
        Assert.Null(d.Driver);
        Assert.Equal("startup.refused.neither", d.MessageKey);
    }

    [Theory]
    [InlineData("vulkan", "--backend=d3d12", "d3d12")]
    [InlineData("d3d12", "--backend=vulkan", "vulkan")]
    [InlineData("vulkan", "--backend=auto", "auto")]
    [InlineData("auto", "--backend=vulkan", "vulkan")]
    [InlineData("d3d12", "--backend=opengl3", "d3d12")]
    [InlineData("d3d12", "--backend=", "d3d12")]
    public void The_command_line_overrides_the_saved_choice_for_the_run(string saved, string arg, string expected)
    {
        Assert.Equal(expected, BackendChoice.Requested(saved, ["--grid=https://grid.example.org", arg]));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("vulkan")]
    [InlineData("d3d12")]
    public void The_saved_choice_is_used_without_an_argument(string saved)
    {
        Assert.Equal(saved, BackendChoice.Requested(saved, []));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("opengl3")]
    [InlineData("VULKAN ")]
    public void A_missing_or_unknown_saved_choice_is_auto(string? saved)
    {
        Assert.Equal("auto", BackendChoice.Requested(saved, []));
    }

    [Fact]
    public void The_other_backend_is_the_one_that_is_not_named()
    {
        Assert.Equal("d3d12", BackendChoice.Other("vulkan"));
        Assert.Equal("vulkan", BackendChoice.Other("d3d12"));
    }

    [Fact]
    public void A_made_up_report_is_read_from_text()
    {
        var r = DeviceReport.Parse("vulkan=1.2.175;d3d12=12_0;adapter=Test Card");
        Assert.NotNull(r);
        Assert.Equal("1.2.175", r!.VulkanApi);
        Assert.Equal("12_0", r.D3D12FeatureLevel);
        Assert.Equal("Test Card", r.Adapter);
        var partial = DeviceReport.Parse("d3d12=12_1");
        Assert.NotNull(partial);
        Assert.Null(partial!.VulkanApi);
        Assert.Equal("12_1", partial.D3D12FeatureLevel);
        Assert.Null(DeviceReport.Parse(null));
        Assert.Null(DeviceReport.Parse(""));
        Assert.Null(DeviceReport.Parse("nonsense"));
    }

    // ---------------- the second start ----------------

    private static readonly string[] Engine = ["--path", "viewer"];

    [Fact]
    public void A_backend_argument_no_longer_blocks_the_second_start_because_the_choice_is_made_before_it()
    {
        Assert.NotNull(BackendRelaunch.Plan("d3d12", "vulkan", Engine, ["--backend=auto"]));
        Assert.NotNull(BackendRelaunch.Plan("d3d12", "vulkan", Engine, ["--backend=vulkan"]));
    }

    [Fact]
    public void A_placed_window_argument_does_not_block_the_second_start_and_goes_with_it()
    {
        var plan = BackendRelaunch.Plan("d3d12", "vulkan", Engine, ["--nv-test-window=corner"]);
        Assert.NotNull(plan);
        Assert.Contains("--nv-test-window=corner", plan!);
    }

    [Fact]
    public void The_second_start_names_the_drivers_tried_so_no_choice_is_tried_twice()
    {
        var plan = BackendRelaunch.Plan("d3d12", "vulkan", Engine, ["--grid=https://grid.example.org"]);
        Assert.Equal(["--path", "viewer", "--rendering-driver", "d3d12", "--", "--grid=https://grid.example.org", "--backend-applied=d3d12"], plan);

        // a start that found its own driver below the floor goes to the other one once, and carries what was tried
        var again = BackendRelaunch.Plan("vulkan", "d3d12", Engine, ["--backend-applied=d3d12"]);
        Assert.NotNull(again);
        Assert.Equal("--backend-applied=d3d12,vulkan", again![^1]);
        Assert.Single(again, a => a.StartsWith("--backend-applied=", StringComparison.Ordinal));

        // and never to one already tried
        Assert.Null(BackendRelaunch.Plan("vulkan", "d3d12", Engine, ["--backend-applied=d3d12,vulkan"]));
        Assert.Null(BackendRelaunch.Plan("d3d12", "vulkan", Engine, ["--backend-applied=d3d12"]));
    }
}
