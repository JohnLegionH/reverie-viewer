// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Notifications;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Toasts, the list that keeps what waits for the user, the history, the unique-notice rule and the modal queue, driven by a test
/// clock. Also the button layout of a script dialog and the permission table. Every member builds its own objects, so the class
/// runs in parallel.
/// </summary>
public class NotificationModelTests
{
    private sealed class Rig
    {
        public double Now = 100;
        public readonly NotificationModel Model;
        public int Changes;

        public Rig()
        {
            Model = new NotificationModel(() => Now);
            Model.Changed += () => Changes++;
        }

        public long Post(NotificationKind kind, string text = "x", string? unique = null, Action<NotificationAnswer>? responder = null, Guid source = default) =>
            Model.Post(new Notification { Kind = kind, Body = NoticeText.Raw(text), UniqueKey = unique, Responder = responder, Source = source });
    }

    // ---------------- toasts by kind ----------------

    [Fact]
    public void A_tip_is_a_toast_for_ten_seconds_then_goes_to_history_and_is_never_kept()
    {
        var r = new Rig();
        long id = r.Post(NotificationKind.Tip);
        Assert.Equal(id, Assert.Single(r.Model.Toasts).Id);
        Assert.Empty(r.Model.List);
        r.Now += 9.9; r.Model.Tick();
        Assert.Single(r.Model.Toasts);
        r.Now += 0.2; r.Model.Tick();
        Assert.Empty(r.Model.Toasts);
        Assert.Empty(r.Model.List);
        Assert.Equal(id, Assert.Single(r.Model.History).Id);
        Assert.Equal(0, r.Model.UnreadCount);
    }

    [Fact]
    public void A_notify_is_a_toast_for_five_seconds_and_then_waits_in_the_list_unread()
    {
        var r = new Rig();
        long id = r.Post(NotificationKind.Notify);
        Assert.Single(r.Model.Toasts);
        Assert.Equal(id, Assert.Single(r.Model.List).Id);
        Assert.Equal(1, r.Model.UnreadCount);
        r.Now += 5.1; r.Model.Tick();
        Assert.Empty(r.Model.Toasts);
        Assert.Equal(id, Assert.Single(r.Model.List).Id);
        Assert.Equal(1, r.Model.UnreadCount);
        Assert.Empty(r.Model.History);
    }

    [Fact]
    public void An_offer_toast_does_not_time_out_and_the_offer_waits_until_answered()
    {
        var r = new Rig();
        long id = r.Post(NotificationKind.Offer);
        r.Now += 3600; r.Model.Tick();
        Assert.Equal(id, Assert.Single(r.Model.Toasts).Id);
        Assert.Equal(id, Assert.Single(r.Model.List).Id);
    }

    [Fact]
    public void A_closed_toast_leaves_the_screen_but_the_notification_stays_in_the_list()
    {
        var r = new Rig();
        long id = r.Post(NotificationKind.Offer);
        Assert.True(r.Model.CloseToast(id));
        Assert.Empty(r.Model.Toasts);
        Assert.Equal(id, Assert.Single(r.Model.List).Id);
        Assert.False(r.Model.CloseToast(12345));
    }

    // ---------------- answering ----------------

    [Fact]
    public void An_answer_runs_the_responder_once_with_the_button_and_text_and_moves_it_to_history()
    {
        var r = new Rig();
        var got = new List<NotificationAnswer>();
        long id = r.Post(NotificationKind.Offer, responder: got.Add);
        Assert.True(r.Model.Answer(id, 2, "typed"));
        Assert.Equal(new NotificationAnswer(2, "typed"), Assert.Single(got));
        Assert.Empty(r.Model.Toasts);
        Assert.Empty(r.Model.List);
        Assert.Equal(id, Assert.Single(r.Model.History).Id);
        Assert.False(r.Model.Answer(id, 0));   // a second answer does nothing
        Assert.Single(got);
    }

    [Fact]
    public void Closing_without_a_reply_is_the_answer_minus_one()
    {
        var r = new Rig();
        var got = new List<NotificationAnswer>();
        long id = r.Post(NotificationKind.Offer, responder: got.Add);
        Assert.True(r.Model.Answer(id, -1));
        Assert.Equal(-1, Assert.Single(got).Button);
        Assert.Empty(r.Model.List);
    }

    [Fact]
    public void A_removed_notification_is_gone_without_a_reply_and_is_not_kept_in_history()
    {
        var r = new Rig();
        int calls = 0;
        long id = r.Post(NotificationKind.Offer, responder: _ => calls++);
        Assert.True(r.Model.Remove(id));
        Assert.Equal(0, calls);
        Assert.Empty(r.Model.Toasts);
        Assert.Empty(r.Model.List);
        Assert.Empty(r.Model.History);
        Assert.False(r.Model.Remove(id));
    }

    // ---------------- unique notices ----------------

    [Fact]
    public void A_notice_with_the_same_unique_key_replaces_the_older_one_without_a_reply()
    {
        var r = new Rig();
        int calls = 0;
        r.Post(NotificationKind.Tip, "online", unique: "friend:1", responder: _ => calls++);
        long second = r.Post(NotificationKind.Tip, "offline", unique: "friend:1");
        r.Post(NotificationKind.Tip, "other friend", unique: "friend:2");
        Assert.Equal(2, r.Model.Toasts.Count);
        Assert.Contains(r.Model.Toasts, n => n.Id == second);
        Assert.DoesNotContain(r.Model.Toasts, n => n.Body.Literal == "online");
        Assert.Equal(0, calls);
    }

    // ---------------- the unread count ----------------

    [Fact]
    public void Reading_marks_one_or_all_and_the_count_follows()
    {
        var r = new Rig();
        long a = r.Post(NotificationKind.Offer);
        long b = r.Post(NotificationKind.Offer);
        r.Post(NotificationKind.Notify);
        Assert.Equal(3, r.Model.UnreadCount);
        r.Model.MarkRead(a);
        Assert.Equal(2, r.Model.UnreadCount);
        r.Model.MarkRead(a);
        Assert.Equal(2, r.Model.UnreadCount);
        r.Model.MarkAllRead();
        Assert.Equal(0, r.Model.UnreadCount);
        Assert.Equal(3, r.Model.List.Count);   // reading keeps them: only an answer removes them
        Assert.False(r.Model.List.Single(n => n.Id == b).Unread);
    }

    // ---------------- limits ----------------

    [Fact]
    public void Only_the_newest_five_toasts_show_and_a_pushed_off_notify_stays_in_the_list()
    {
        var r = new Rig();
        long first = r.Post(NotificationKind.Notify, "first");
        for (int i = 0; i < 6; i++)
            r.Post(NotificationKind.Tip, "tip " + i);
        Assert.Equal(NotificationModel.MaxToasts, r.Model.Toasts.Count);
        Assert.DoesNotContain(r.Model.Toasts, n => n.Id == first);
        Assert.Equal(first, Assert.Single(r.Model.List).Id);
        Assert.Equal("tip 5", r.Model.Toasts[^1].Body.Literal);   // newest last
    }

    [Fact]
    public void The_history_keeps_the_last_hundred()
    {
        var r = new Rig();
        for (int i = 0; i < 130; i++)
        {
            long id = r.Post(NotificationKind.Offer, "n" + i);
            r.Model.Answer(id, 0);
        }
        Assert.Equal(NotificationModel.HistoryLimit, r.Model.History.Count);
        Assert.Equal("n129", r.Model.History[^1].Body.Literal);
        Assert.Equal("n30", r.Model.History[0].Body.Literal);
    }

    // ---------------- modal ----------------

    [Fact]
    public void An_alert_is_modal_not_a_toast_and_the_next_one_waits_behind_it()
    {
        var r = new Rig();
        long a = r.Post(NotificationKind.Alert, "first");
        long b = r.Post(NotificationKind.Alert, "second");
        Assert.NotNull(r.Model.Modal);
        Assert.Equal(a, r.Model.Modal!.Id);
        Assert.Empty(r.Model.Toasts);
        r.Model.Answer(a, 0);
        Assert.NotNull(r.Model.Modal);
        Assert.Equal(b, r.Model.Modal!.Id);
        r.Model.Answer(b, 0);
        Assert.Null(r.Model.Modal);
    }

    // ---------------- change events ----------------

    [Fact]
    public void Changed_is_raised_by_a_post_an_answer_and_an_expiry_but_not_by_a_tick_that_changes_nothing()
    {
        var r = new Rig();
        long id = r.Post(NotificationKind.Tip);
        Assert.Equal(1, r.Changes);
        r.Model.Tick();
        Assert.Equal(1, r.Changes);
        r.Now += 11; r.Model.Tick();
        Assert.Equal(2, r.Changes);
        long o = r.Post(NotificationKind.Offer);
        r.Model.Answer(o, 0);
        Assert.Equal(4, r.Changes);
        Assert.NotEqual(id, o);
    }

    [Fact]
    public void Ids_are_unique_and_rising()
    {
        var r = new Rig();
        long a = r.Post(NotificationKind.Tip);
        long b = r.Post(NotificationKind.Tip);
        Assert.True(a > 0 && b > a);
    }
}

/// <summary>The script dialog's button layout and the permission table, as data.</summary>
public class DialogLayoutTests
{
    private static string Rows(DialogLayout l) => string.Join(" / ", l.Rows.Select(row => string.Join(" ", row)));

    [Fact]
    public void Twelve_buttons_read_from_the_top_ten_eleven_twelve_down_to_one_two_three_with_the_first_at_the_bottom_left()
    {
        // zero-based indices of the script's list: the first button (0) is bottom-left, rows are added upward
        Assert.Equal("9 10 11 / 6 7 8 / 3 4 5 / 0 1 2", Rows(DialogLayout.For(12)));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "0")]
    [InlineData(2, "0 1")]
    [InlineData(3, "0 1 2")]
    [InlineData(4, "3 / 0 1 2")]
    [InlineData(7, "6 / 3 4 5 / 0 1 2")]
    [InlineData(10, "9 / 6 7 8 / 3 4 5 / 0 1 2")]
    public void A_short_dialog_leaves_the_top_row_partial_and_left_aligned(int count, string expected) =>
        Assert.Equal(expected, Rows(DialogLayout.For(count)));

    [Fact]
    public void More_than_twelve_buttons_are_laid_out_as_twelve()
    {
        Assert.Equal("9 10 11 / 6 7 8 / 3 4 5 / 0 1 2", Rows(DialogLayout.For(13)));
        Assert.Equal("9 10 11 / 6 7 8 / 3 4 5 / 0 1 2", Rows(DialogLayout.For(200)));
    }

    [Fact]
    public void Every_button_appears_exactly_once_in_the_layout()
    {
        for (int n = 1; n <= 20; n++)
        {
            var all = DialogLayout.For(n).Rows.SelectMany(r => r).OrderBy(i => i).ToArray();
            Assert.Equal(Enumerable.Range(0, Math.Min(n, DialogLayout.MaxButtons)).ToArray(), all);
        }
    }

    [Fact]
    public void The_dialog_shows_the_first_twelve_labels_in_order()
    {
        var labels = Enumerable.Range(0, 15).Select(i => "b" + i).ToArray();
        Assert.Equal(labels.Take(12), DialogLayout.Clip(labels));
        Assert.Equal(new[] { "a", "b" }, DialogLayout.Clip(new[] { "a", "b" }));
    }
}

public class ScriptPermissionsTests
{
    [Fact]
    public void The_table_has_the_eighteen_known_permissions_by_bit_in_the_official_order()
    {
        var bits = ScriptPermissions.Table.Select(e => e.Bit).ToArray();
        Assert.Equal(Enumerable.Range(1, 18).Select(shift => 1 << shift).ToArray(), bits);
        Assert.Equal(18, ScriptPermissions.Table.Select(e => e.Key).Distinct().Count());
    }

    [Fact]
    public void Only_taking_money_is_a_caution()
    {
        Assert.Equal(new[] { 1 << 1 }, ScriptPermissions.Table.Where(e => e.Caution).Select(e => e.Bit).ToArray());
        Assert.True(ScriptPermissions.HasCaution((1 << 1) | (1 << 4)));
        Assert.False(ScriptPermissions.HasCaution(1 << 4));
    }

    [Fact]
    public void Unknown_bits_are_dropped_and_the_known_ones_come_back_in_table_order()
    {
        int mask = (1 << 4) | (1 << 2) | (1 << 25) | 1;   // animate, controls, two unknown bits
        Assert.Equal((1 << 4) | (1 << 2), ScriptPermissions.Known(mask));
        Assert.Equal(new[] { 1 << 2, 1 << 4 }, ScriptPermissions.Asked(mask).Select(e => e.Bit).ToArray());
        Assert.Equal(0, ScriptPermissions.Known((1 << 25) | 1));
        Assert.Empty(ScriptPermissions.Asked(0));
    }

    [Fact]
    public void Every_permission_has_words_in_the_string_table()
    {
        var strings = StringTable.English();
        Assert.Equal(18, ScriptPermissions.Table.Count);
        foreach (var e in ScriptPermissions.Table)
        {
            Assert.True(strings.Has(e.Key), e.Key);
            Assert.False(string.IsNullOrWhiteSpace(strings.Get(e.Key)), e.Key);
        }
    }
}
