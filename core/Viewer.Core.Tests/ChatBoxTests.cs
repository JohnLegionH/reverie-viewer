// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The chat box without the engine: how many lines it shows, what its text may hold, recalling earlier lines, completing a
/// gesture's trigger, and which arrow keys stay with the box. Every member builds its own objects, so the class runs in parallel.</summary>
public sealed class ChatBoxTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(40, 5)]
    public void The_box_grows_with_the_wrapped_text_up_to_five_lines(int wrapped, int shown) =>
        Assert.Equal(shown, ChatBoxRules.VisibleLines(wrapped));

    [Fact]
    public void Line_breaks_and_other_control_characters_are_left_out_as_a_one_line_field_does()
    {
        Assert.Equal(("onetwothree", 11), ChatBoxRules.Clean("", "one\ntwo\r\n\tthree", 15));
        Assert.Equal(("ab", 1), ChatBoxRules.Clean("ab", "a\nb", 2));
    }

    [Fact]
    public void Ordinary_text_is_left_as_it_is_with_its_caret()
    {
        Assert.Equal(("hello there", 5), ChatBoxRules.Clean("hell there", "hello there", 5));
    }

    [Fact]
    public void Text_past_the_limit_is_cut_from_what_was_put_in_not_from_the_end()
    {
        string full = new('a', 1021);
        string before = full + "ZZ";                                      // 1023: no room
        Assert.Equal((before, 3), ChatBoxRules.Clean(before, before.Insert(3, "x"), 4));
        string roomy = full;                                              // room for two
        Assert.Equal((roomy.Insert(3, "12"), 5), ChatBoxRules.Clean(roomy, roomy.Insert(3, "12345"), 8));
    }

    [Fact]
    public void The_limit_counts_characters_and_never_splits_one()
    {
        string before = new('a', 1022);
        var (text, _) = ChatBoxRules.Clean(before, before + "\U0001F600b", 1024);
        Assert.Equal(before + "\U0001F600", text);
    }

    [Fact]
    public void Ctrl_up_walks_back_through_the_lines_sent_and_ctrl_down_comes_forward_to_the_unsent_text()
    {
        var h = new ChatLineHistory();
        h.Remember("one");
        h.Remember("two");
        h.Remember("three");
        Assert.Equal("three", h.Previous("draft"));
        Assert.Equal("two", h.Previous("three"));
        Assert.Equal("one", h.Previous("two"));
        Assert.Null(h.Previous("one"));                 // the oldest stays
        Assert.Equal("two", h.Next("one"));
        Assert.Equal("three", h.Next("two"));
        Assert.Equal("draft", h.Next("three"));
        Assert.Null(h.Next("draft"));
    }

    [Fact]
    public void Empty_lines_and_a_repeat_of_the_last_line_are_not_kept_and_a_send_starts_again_from_the_newest()
    {
        var h = new ChatLineHistory();
        h.Remember("one");
        h.Remember("two");
        h.Remember("two");
        h.Remember("");
        Assert.Equal("two", h.Previous(""));
        Assert.Equal("one", h.Previous("two"));
        h.Remember("three");
        Assert.Equal("three", h.Previous(""));
    }

    [Fact]
    public void With_nothing_sent_there_is_nothing_to_recall_and_clearing_forgets()
    {
        var h = new ChatLineHistory();
        Assert.Null(h.Previous("x"));
        Assert.Null(h.Next("x"));
        h.Remember("one");
        h.Clear();
        Assert.Null(h.Previous("x"));
    }

    private static readonly string[] Triggers = ["/dance", "/dance2", "/Bow", "wave", "/bow", ""];

    [Theory]
    [InlineData("/da", "/dance|/dance2")]
    [InlineData("/BO", "/Bow")]
    [InlineData("/dance", "/dance2")]
    [InlineData("  /da", "/dance|/dance2")]
    [InlineData("/dance2", "")]
    [InlineData("/", "")]
    [InlineData("wa", "")]
    [InlineData("hello /da", "")]
    [InlineData("/zz", "")]
    [InlineData("", "")]
    public void A_line_starting_with_a_slash_offers_the_triggers_it_begins(string typed, string offered) =>
        Assert.Equal(offered, string.Join('|', GestureCompletion.Matches(typed, Triggers)));

    [Fact]
    public void No_more_than_eight_triggers_are_offered() =>
        Assert.Equal(8, GestureCompletion.Matches("/g", Enumerable.Range(10, 20).Select(i => "/g" + i)).Count);

    [Fact]
    public void Completing_keeps_what_was_typed_as_typed_and_adds_the_rest()
    {
        Assert.Equal("/DAnce", GestureCompletion.Complete("/DA", "/dance"));
        Assert.Equal("  /dance", GestureCompletion.Complete("  /da", "/dance"));
    }

    [Fact]
    public void The_active_gestures_give_their_triggers()
    {
        var set = new ActiveGestures(_ => { }, _ => 0);
        set.Set(Guid.NewGuid(), new GestureDocument { Trigger = "/dance" });
        set.Set(Guid.NewGuid(), new GestureDocument());
        Assert.Equal(["/dance"], set.Triggers());
    }

    [Fact]
    public void Ctrl_up_and_down_stay_with_a_box_that_recalls_lines_even_when_arrows_move_the_avatar()
    {
        Assert.False(ArrowKeyRouting.TakenByAvatar(ArrowFocusKind.TextField, false, true, true, true, recallKey: true));
        Assert.True(ArrowKeyRouting.TakenByAvatar(ArrowFocusKind.TextField, false, true, true, true, recallKey: false));
    }

    [Fact]
    public void The_chat_box_hint_is_short_and_the_keys_are_told_in_its_tooltip()
    {
        var strings = StringTable.English();
        Assert.Equal("Say something…", strings.Get("chat.input.hint"));
        string tip = strings.Get("chat.input.tip");
        Assert.Contains("Shift+Enter", tip);
        Assert.Contains("Ctrl+Enter", tip);
        Assert.Contains("Ctrl+Up", tip);
        Assert.Contains("/5", tip);
    }
}
