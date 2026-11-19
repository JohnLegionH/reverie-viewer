// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Ctrl-click and Shift-click on the rows of a list, as pure functions, so the class runs in parallel.</summary>
public class RowSelectionTests
{
    private static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
    private static readonly Guid[] Shown = [G(1), G(2), G(3), G(4), G(5)];

    [Fact]
    public void Shift_chooses_every_row_from_the_last_chosen_to_the_clicked_one_whichever_comes_first()
    {
        Assert.Equal([G(2), G(3), G(4)], RowSelection.Range(Shown, G(2), G(4)));
        Assert.Equal([G(2), G(3), G(4)], RowSelection.Range(Shown, G(4), G(2)));
        Assert.Equal([G(3)], RowSelection.Range(Shown, G(3), G(3)));
    }

    [Fact]
    public void Shift_with_no_anchor_in_view_chooses_just_the_clicked_row_and_a_row_out_of_view_nothing()
    {
        Assert.Equal([G(4)], RowSelection.Range(Shown, Guid.Empty, G(4)));
        Assert.Equal([G(4)], RowSelection.Range(Shown, G(99), G(4)));   // the anchor's folder was folded away
        Assert.Empty(RowSelection.Range(Shown, G(1), G(99)));
    }

    [Fact]
    public void Ctrl_adds_a_row_or_takes_it_out_but_never_empties_the_choice()
    {
        Assert.Equal([G(1), G(3)], RowSelection.Toggle([G(1)], G(3)));
        Assert.Equal([G(1)], RowSelection.Toggle([G(1), G(3)], G(3)));
        Assert.Equal([G(1)], RowSelection.Toggle([G(1)], G(1)));
        Assert.Equal([G(1)], RowSelection.Toggle([G(1)], Guid.Empty));
    }
}
