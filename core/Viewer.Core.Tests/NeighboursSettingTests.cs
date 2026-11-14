// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Platform;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Neighbour regions: on when nothing is set; a start-up option and a settings-file key turn them off (or on); the option wins over the file.</summary>
public class NeighboursSettingTests
{
    private static readonly string[] None = [];

    [Fact]
    public void On_when_nothing_is_set() => Assert.True(NeighboursSetting.Resolve(None, null));

    [Fact]
    public void On_when_the_file_does_not_mention_it() => Assert.True(NeighboursSetting.Resolve(None, "{ \"other\": 1 }"));

    [Fact]
    public void On_by_the_startup_option() => Assert.True(NeighboursSetting.Resolve(["--neighbours=on"], null));

    [Fact]
    public void On_by_the_settings_file() => Assert.True(NeighboursSetting.Resolve(None, "{ \"neighbours\": true }"));

    [Fact]
    public void Off_by_the_startup_option() => Assert.False(NeighboursSetting.Resolve(["--neighbours=off"], null));

    [Fact]
    public void Off_by_the_settings_file() => Assert.False(NeighboursSetting.Resolve(None, "{ \"neighbours\": false }"));

    [Fact]
    public void The_option_wins_over_the_file()
    {
        Assert.False(NeighboursSetting.Resolve(["--neighbours=off"], "{ \"neighbours\": true }"));
        Assert.True(NeighboursSetting.Resolve(["--neighbours=on"], "{ \"neighbours\": false }"));
    }

    [Fact]
    public void A_value_not_understood_is_ignored()
    {
        Assert.True(NeighboursSetting.Resolve(["--neighbours=maybe"], null));
        Assert.True(NeighboursSetting.Resolve(None, "{ \"neighbours\": 7 }"));
        Assert.True(NeighboursSetting.Resolve(["--neighbours=maybe"], "{ \"neighbours\": true }"));
        Assert.False(NeighboursSetting.Resolve(["--neighbours=maybe"], "{ \"neighbours\": false }"));
    }

    [Fact]
    public void A_file_that_does_not_parse_is_ignored()
    {
        Assert.True(NeighboursSetting.Resolve(None, "{ not json"));
        Assert.True(NeighboursSetting.Resolve(None, ""));
    }

    [Fact]
    public void The_option_is_read_case_insensitively_and_other_arguments_are_ignored()
    {
        Assert.True(NeighboursSetting.Resolve(["--other", "--neighbours=ON"], null));
    }
}
