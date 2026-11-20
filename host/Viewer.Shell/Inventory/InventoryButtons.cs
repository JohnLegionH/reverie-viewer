// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;

namespace Viewer.Shell.Inventory;

/// <summary>When the inventory window's buttons are shown. Pure.</summary>
public static class InventoryButtons
{
    /// <summary>Whether Try again is shown: only for a selected folder whose load failed (it is hidden while the folder loads, once it is
    /// complete, and again after a retry that worked).</summary>
    public static bool ShowsRetry(bool bound, bool hasSelection, FolderLoad? state) => bound && hasSelection && state == FolderLoad.Failed;
}
