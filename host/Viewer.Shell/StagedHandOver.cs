// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// Hands a list of sized pieces (a mesh's surfaces) to the engine a few at a time: each piece is a unit of the frame's shared budget
/// (<see cref="FrameBudget.TryBeginUnit"/>), so a mesh whose pieces do not fit one frame finishes over several, and each piece's time is
/// measured for the cost model so the next estimate follows what the engine really takes.
/// </summary>
public static class StagedHandOver
{
    /// <summary>Hands over pieces from <paramref name="next"/> on while the budget allows (advancing <paramref name="next"/>); returns how many it
    /// handed over. A piece the budget refuses is left for the next frame, whose first unit goes ahead, so the list always finishes.</summary>
    public static int Run(IReadOnlyList<double> sizes, ref int next, FrameBudget budget, UnitCostModel costs, Action<int> handOver)
    {
        int handed = 0;
        while (next < sizes.Count && budget.TryBeginUnit(costs.EstimateMs(sizes[next])))
        {
            double t0 = budget.SpentMs;
            handOver(next);
            costs.Record(sizes[next], budget.SpentMs - t0);
            next++;
            handed++;
        }
        return handed;
    }
}
