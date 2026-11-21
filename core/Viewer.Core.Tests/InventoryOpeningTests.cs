// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

public sealed class InventoryOpeningTests
{
    [Theory]
    [InlineData(ItemKind.Notecard)]
    [InlineData(ItemKind.Texture)]
    [InlineData(ItemKind.Script)]
    [InlineData(ItemKind.Gesture)]
    [InlineData(ItemKind.Landmark)]
    public void Items_with_an_editor_open_on_double_click(ItemKind kind) => Assert.True(InventoryOpening.Opens(kind));

    [Theory]
    [InlineData(ItemKind.Sound)]
    [InlineData(ItemKind.Object)]
    [InlineData(ItemKind.Clothing)]
    [InlineData(ItemKind.Animation)]
    public void Other_items_do_not(ItemKind kind) => Assert.False(InventoryOpening.Opens(kind));
}
