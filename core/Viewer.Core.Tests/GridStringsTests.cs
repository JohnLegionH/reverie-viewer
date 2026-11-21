// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The words the texts take from the grid: the currency symbol where a text names money, and the grid's name where a text says
/// which grid the person is on. Pure values; the class runs in parallel.</summary>
public class GridStringsTests
{
    [Fact]
    public void Money_texts_carry_the_set_symbol_and_start_as_second_lifes()
    {
        var s = StringTable.English();
        Assert.Equal("For sale: L$ 5", s.Get("edit.for_sale.price", 5));
        s.Context.Currency = "I'z";
        Assert.Equal("For sale: I'z 5", s.Get("edit.for_sale.price", 5));
        Assert.Equal("Fee: I'z 7", s.Get("model.fee.amount", 7));
        Assert.Equal("Cost: I'z 9", s.Get("upload.cost.amount", 9));
        Assert.Equal("Upload (I'z 9)", s.Get("upload.button.cost", 9));
        Assert.Equal("Take money (I'z) from you", s.Get("script.perm.take_money"));
        Assert.Equal("I'z", s.Get("edit.sale_price"));
    }

    [Fact]
    public void A_grid_with_no_money_leaves_the_symbol_out_without_a_stray_space()
    {
        var s = StringTable.English();
        s.Context.Currency = "";
        Assert.Equal("For sale: 5", s.Get("edit.for_sale.price", 5));
        Assert.Equal("Upload (9)", s.Get("upload.button.cost", 9));
        Assert.Equal("", s.Get("edit.sale_price"));
    }

    [Fact]
    public void No_text_in_the_table_hard_codes_second_lifes_symbol()
    {
        var s = StringTable.English();
        s.Context.Currency = "¤";   // the symbol is put in by the context: a literal L$ would show against it
        foreach (string key in s.Keys)
        {
            string raw = s.Get(key);
            Assert.False(raw.Contains("L$", StringComparison.Ordinal), key + " names L$ instead of the grid's symbol: " + raw);
        }
    }

    [Fact]
    public void The_place_message_names_the_grid_the_person_is_on()
    {
        var s = StringTable.English();
        Assert.Equal("Example Region is a place in Second Life, not on this grid.", s.Get("link.sl_place", "Example Region"));
        s.Context.Grid = "Example Grid";
        Assert.Equal("Example Region is a place in Second Life, not on Example Grid.", s.Get("link.sl_place", "Example Region"));
    }

    [Fact]
    public void The_pseudo_table_keeps_the_tokens_whole()
    {
        var s = StringTable.Pseudo();
        s.Context.Currency = "X$";
        Assert.Contains("X$", s.Get("edit.for_sale.price", 5));
        Assert.DoesNotContain("{currency}", s.Get("edit.for_sale.price", 5));
    }
}
