// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// Decides which avatars are posed in a frame when posing them all does not fit the frame's time: the one posed longest ago first
/// (an avatar never posed first of all), as many as the time allows, and always at least a few. An avatar not chosen keeps the pose it
/// had; every avatar is reached within a bounded number of frames whatever the budget. Not thread safe; made once and reused each
/// frame (nothing here allocates once the arrays have grown to the number of avatars).
/// </summary>
public sealed class PoseSweep
{
    /// <summary>The stamp of an avatar that has never been posed.</summary>
    public const long Never = -1;

    private const int IndexBits = 16;
    private long[] _keys = [];
    private int[] _order = [];

    /// <summary>The avatars' indices, the one posed longest ago first (ties: the lower index first). <paramref name="lastPosed"/> is the frame
    /// number each was last posed in, <see cref="Never"/> for one not yet posed. At most 65,535 avatars.</summary>
    public ReadOnlySpan<int> Plan(ReadOnlySpan<long> lastPosed)
    {
        int n = lastPosed.Length;
        if (n >= 1 << IndexBits) throw new ArgumentException("too many avatars to order");
        if (_keys.Length < n) { _keys = new long[n]; _order = new int[n]; }
        // one sortable key each: the stamp above, the index below, so the order is total and ties go to the lower index
        for (int i = 0; i < n; i++) _keys[i] = (lastPosed[i] << IndexBits) | (long)i;
        Array.Sort(_keys, 0, n);
        for (int i = 0; i < n; i++) _order[i] = (int)(_keys[i] & ((1 << IndexBits) - 1));
        return _order.AsSpan(0, n);
    }

    /// <summary>The avatars to hand to the pose workers this frame: the indices of those with no pose in flight, in the order <see cref="Plan"/> gives (the one
    /// posed longest ago first). Writes at most <paramref name="limit"/> of them to <paramref name="into"/> and returns how many: a limit keeps the
    /// engine's skinning updates to a share of the avatars each frame, as the main-thread budget did.</summary>
    public int PlanSubmissions(ReadOnlySpan<long> lastPosed, ReadOnlySpan<bool> inFlight, Span<int> into, int limit = int.MaxValue)
    {
        var order = Plan(lastPosed);
        int n = 0;
        for (int k = 0; k < order.Length && n < limit; k++)
            if (!inFlight[order[k]]) into[n++] = order[k];
        return n;
    }

    /// <summary>True while another avatar may be posed: fewer than <paramref name="atLeast"/> have been, or time is left.</summary>
    public static bool MayContinue(int posedSoFar, double spentMs, double budgetMs, int atLeast) => posedSoFar < atLeast || spentMs < budgetMs;

    /// <summary>As above, but the least number is only forced while <paramref name="forcedCapMs"/> is not yet spent: one pose is always made, the
    /// rest of the floor only while that much time has not gone, so a crowd the budget has no room for does not cost a frame its floor in full.</summary>
    public static bool MayContinue(int posedSoFar, double spentMs, double budgetMs, int atLeast, double forcedCapMs) =>
        spentMs < budgetMs || posedSoFar == 0 || (posedSoFar < atLeast && spentMs < forcedCapMs);
}
