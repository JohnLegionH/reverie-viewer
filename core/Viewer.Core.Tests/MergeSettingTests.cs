// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Platform;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Merging of still objects: on when nothing is set; a start-up option and a settings-file key turn it off; the option wins over the file.</summary>
public class MergeSettingTests
{
    private static readonly string[] None = [];

    [Fact]
    public void On_when_nothing_is_set() => Assert.True(MergeSetting.Resolve(None, null));

    [Fact]
    public void On_when_the_file_does_not_mention_it() => Assert.True(MergeSetting.Resolve(None, "{ \"other\": 1 }"));

    [Fact]
    public void Off_by_the_startup_option() => Assert.False(MergeSetting.Resolve(["--merge=off"], null));

    [Fact]
    public void Off_by_the_settings_file() => Assert.False(MergeSetting.Resolve(None, "{ \"merge_still_objects\": false }"));

    [Fact]
    public void The_option_wins_over_the_file()
    {
        Assert.False(MergeSetting.Resolve(["--merge=off"], "{ \"merge_still_objects\": true }"));
        Assert.True(MergeSetting.Resolve(["--merge=on"], "{ \"merge_still_objects\": false }"));
    }

    [Fact]
    public void A_value_not_understood_is_ignored()
    {
        Assert.True(MergeSetting.Resolve(["--merge=maybe"], null));
        Assert.True(MergeSetting.Resolve(None, "{ \"merge_still_objects\": 7 }"));
        Assert.False(MergeSetting.Resolve(["--merge=maybe"], "{ \"merge_still_objects\": false }"));
    }

    [Fact]
    public void A_file_that_does_not_parse_is_ignored()
    {
        Assert.True(MergeSetting.Resolve(None, "{ not json"));
        Assert.True(MergeSetting.Resolve(None, ""));
    }

    [Fact]
    public void The_option_is_read_case_insensitively_and_other_arguments_are_ignored()
    {
        Assert.False(MergeSetting.Resolve(["--other", "--merge=OFF"], null));
        Assert.True(MergeSetting.Resolve(["--merge", "off"], null));
    }

    [Fact]
    public void A_missing_settings_file_reads_as_null()
    {
        var dirs = new DataDirectories("no-such-folder/settings", "no-such-folder/cache", "no-such-folder/logs");
        Assert.Null(MergeSetting.ReadFile(dirs));
    }
}
