// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A link the viewer has just removed from the current outfit folder is remembered for a minute, so an answer from the grid that
/// still lists it, or a late notice, does not put it back in the library's store where it would stay as worn. Offline, with a
/// test clock; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class RemovedLinkLedgerTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-4000-8000-0000000000a1");
    private static readonly Guid B = Guid.Parse("00000000-0000-4000-8000-0000000000a2");

    [Fact]
    public void A_removed_link_is_remembered_and_another_is_not()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var ledger = new RemovedLinkLedger(() => now);
        Assert.False(ledger.IsRemoved(A));
        ledger.Removed(A);
        Assert.True(ledger.IsRemoved(A));
        Assert.False(ledger.IsRemoved(B));
        Assert.Equal(1, ledger.Count);
    }

    [Fact]
    public void A_removal_is_remembered_for_a_minute_and_then_forgotten()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var ledger = new RemovedLinkLedger(() => now);
        ledger.Removed(A);
        now += RemovedLinkLedger.Keep - TimeSpan.FromSeconds(1);
        Assert.True(ledger.IsRemoved(A));
        now += TimeSpan.FromSeconds(2);
        Assert.False(ledger.IsRemoved(A));
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void A_link_removed_again_is_remembered_from_the_later_removal()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var ledger = new RemovedLinkLedger(() => now);
        ledger.Removed(A);
        now += TimeSpan.FromSeconds(50);
        ledger.Removed(A);
        now += TimeSpan.FromSeconds(50);
        Assert.True(ledger.IsRemoved(A));
        Assert.Equal(1, ledger.Count);
    }

    [Fact]
    public void Old_removals_do_not_pile_up()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var ledger = new RemovedLinkLedger(() => now);
        for (int i = 0; i < 200; i++)
        {
            ledger.Removed(Guid.NewGuid());
            now += TimeSpan.FromSeconds(10);
        }
        Assert.InRange(ledger.Count, 1, 7);   // the last minute's removals are held, the older ones are not
    }
}
