// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Inventory;

/// <summary>The names the development automation command uses for the inventory window's two tabs.</summary>
public static class InventoryTabNames
{
    /// <summary>"all" and "recent", in any case and with surrounding spaces; anything else (or nothing) names no tab.</summary>
    public static bool TryParse(string? text, out InventoryTab tab)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "all": tab = InventoryTab.All; return true;
            case "recent": tab = InventoryTab.Recent; return true;
            default: tab = InventoryTab.All; return false;
        }
    }
}
