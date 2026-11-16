// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;

namespace Viewer.Shell.Build;

/// <summary>The rows of the Build window's texture and material lists.</summary>
public sealed class InventoryPickList
{
    public InventoryPickList(InventoryModel model, ItemKind kind)
    {
    }

    /// <summary>Asks for the folders the list still needs.</summary>
    public void Fetch()
    {
    }

    /// <summary>True when every folder the list reads has been loaded.</summary>
    public bool AllLoaded => false;

    /// <summary>The items listed for <paramref name="search"/>, by name.</summary>
    public IReadOnlyList<InventoryItemData> Rows(string search) => [];

    /// <summary>The string key of the text shown when nothing is listed, or null.</summary>
    public string? EmptyKey => null;
}
