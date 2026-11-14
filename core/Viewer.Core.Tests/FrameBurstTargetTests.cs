// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The development frame burst writes only to a folder it was explicitly given.</summary>
public class FrameBurstTargetTests
{
    // full paths built from the current drive's root, so no drive letter or folder of any machine is written here
    private static readonly string Root = Path.GetPathRoot(Environment.CurrentDirectory)!;
    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);
    private static readonly string Data = P("example", "appdata", "ReverieViewer");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_no_folder_given_nothing_is_written_and_the_reason_says_so(string? given)
    {
        Assert.Null(FrameBurstTarget.Resolve(given, Data, out var why));
        Assert.Contains("NV_LOG_DIR", why);
    }

    [Fact]
    public void A_relative_folder_is_refused()
    {
        Assert.Null(FrameBurstTarget.Resolve(Path.Combine("logs", "burst"), Data, out var why));
        Assert.Contains("full path", why);
    }

    [Fact]
    public void The_viewers_own_per_user_folder_is_never_used()
    {
        foreach (var given in new[] { Data, Path.Combine(Data, "Logs"), Path.Combine(Data, "logs", "x").ToUpperInvariant(), Data + Path.DirectorySeparatorChar })
        {
            Assert.Null(FrameBurstTarget.Resolve(given, Data, out var why));
            Assert.Contains("per-user", why);
        }
    }

    [Fact]
    public void A_full_path_that_was_given_is_used_as_given()
    {
        string ok = P("example", "run-1");
        Assert.Equal(ok, FrameBurstTarget.Resolve(ok, Data, out var why));
        Assert.Equal("", why);
        string sibling = Data + "Other";   // only shares a prefix with the per-user folder
        Assert.Equal(sibling, FrameBurstTarget.Resolve(sibling, Data, out _));
    }
}
