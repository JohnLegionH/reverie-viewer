// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Building;

/// <summary>
/// The land impact the Build window shows for a selection (llselectmgr.cpp getSelectedLinksetCost, llfloatertools.cpp updateStatusMessage /
/// the "status_selectcount" string; read as behaviour): the region's linkset cost of each distinct root that is not worn, added up, and shown
/// as a whole number by dropping the fraction. The region gives each cost through its GetObjectCost capability ("linked_set_resource_cost").
/// </summary>
public static class LandImpact
{
    /// <summary>The roots' linkset costs added up; a root whose cost the region has not answered yet counts as 0, as the official viewer's does.</summary>
    public static float Total(IEnumerable<(uint Root, bool Attachment)> selected, Func<uint, float?> linksetCost)
    {
        float total = 0;
        var seen = new HashSet<uint>();
        foreach (var (root, attachment) in selected)
            if (!attachment && seen.Add(root))
                total += linksetCost(root) ?? 0f;
        return total;
    }

    /// <summary>The number shown: the cost with its fraction dropped ((S32) link_cost).</summary>
    public static int Shown(float total) => (int)total;
}
