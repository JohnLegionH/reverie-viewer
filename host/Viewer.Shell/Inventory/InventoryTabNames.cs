// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Inventory;

/// <summary>The names the development automation command uses for the inventory window's tabs.</summary>
public static class InventoryTabNames
{
    /// <summary>"all", "recent" and "worn", in any case and with surrounding spaces; anything else (or nothing) names no tab.</summary>
    public static bool TryParse(string? text, out InventoryTab tab)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "all": tab = InventoryTab.All; return true;
            case "recent": tab = InventoryTab.Recent; return true;
            case "worn": tab = InventoryTab.Worn; return true;
            default: tab = InventoryTab.All; return false;
        }
    }
}
