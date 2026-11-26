// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The tab names of the development automation command's inventory search. Pure functions: the tests share nothing and run in parallel.</summary>
public class InventoryTabNamesTests
{
    [Theory]
    [InlineData("recent")]
    [InlineData("Recent")]
    [InlineData("  RECENT ")]
    public void Recent_is_named_in_any_case(string text)
    {
        Assert.True(InventoryTabNames.TryParse(text, out var tab));
        Assert.Equal(InventoryTab.Recent, tab);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("All")]
    [InlineData(" all ")]
    public void All_is_named_in_any_case(string text)
    {
        Assert.True(InventoryTabNames.TryParse(text, out var tab));
        Assert.Equal(InventoryTab.All, tab);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("recents")]
    [InlineData("favorites")]
    public void Anything_else_names_no_tab(string? text)
    {
        Assert.False(InventoryTabNames.TryParse(text, out _));
    }
}
