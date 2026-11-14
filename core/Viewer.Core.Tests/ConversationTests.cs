// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// One-to-one instant message conversations without the engine or a grid: opening by a message or by the user, tabs and
/// unread counts, stored messages delivered at login, both sides' typing, sends through a stand-in for the pipeline, and the
/// hub that moves the session's events onto the interface's thread. Times come from a test clock. Every member builds its
/// own objects, so the class runs in parallel.
/// </summary>
public class ConversationTests
{
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Friend = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Second = Guid.Parse("5e5e5e5e-0000-4000-8000-000000000002");
    private static readonly DateTimeOffset T0 = new(2026, 3, 4, 13, 5, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public DateTimeOffset Now = T0;
        public readonly List<ActionIntent> Submitted = new();
        public Func<ActionIntent, ActionResult> Answer = _ => new ActionResult(ActionStatus.Sent, 1);
        public readonly List<string> SystemLines = new();
        public readonly NameCache Names = new();
        public readonly Conversations Ims;
        public bool SendTyping = true;

        public Rig() => Ims = new Conversations(Self, Names, i => { Submitted.Add(i); return Answer(i); }, () => 500, () => SendTyping, () => Now, SystemLines.Add);

        public IEnumerable<ImTypingIntent> TypingSent => Submitted.OfType<ImTypingIntent>();
    }

    private static T At<T>(IReadOnlyList<T> list, int i)
    {
        Assert.True(list.Count > i, $"expected at least {i + 1} conversations, found {list.Count}");
        return list[i];
    }

    private static T LastOf<T>(IEnumerable<T> items)
    {
        var all = items.ToList();
        Assert.NotEmpty(all);
        return all[^1];
    }

    private static InstantMessageReceived Im(Guid from, string name, string text, ImKind kind = ImKind.Message, bool offline = false,
        DateTimeOffset? sentAt = null, bool group = false, int dialog = 0) =>
        new(from, name, Self, ImSessionId.For(Self, from), kind, dialog, offline, sentAt, group, new MessageBody(text, MessageChannel.InstantMessage));

    // ---------------- receiving ----------------

    [Fact]
    public void A_message_opens_its_conversation_with_the_line_and_counts_it_unread()
    {
        var r = new Rig();
        Conversation? arrived = null;
        r.Ims.Arrived += c => arrived = c;
        r.Ims.Receive(Im(Friend, "Example Resident", "hello"));
        var c = Assert.Single(r.Ims.Open);
        Assert.Same(c, arrived);
        Assert.Equal(ImSessionId.For(Self, Friend), c.SessionId);
        Assert.Equal(Friend, c.Other);
        Assert.Equal("Example Resident", c.Name);
        Assert.Equal(new ChatLine(T0, ChatLineStyle.Agent, Friend, "Example Resident", "hello"), Assert.Single(c.Lines));
        Assert.Equal(1, c.Unread);
        Assert.Equal(c.SessionId, r.Ims.Active);
    }

    [Fact]
    public void Later_messages_join_the_same_conversation_and_a_second_sender_gets_a_second_tab_in_order()
    {
        var r = new Rig();
        int arrivals = 0;
        r.Ims.Arrived += _ => arrivals++;
        r.Ims.Receive(Im(Friend, "Example Resident", "one"));
        r.Ims.Receive(Im(Friend, "Example Resident", "two"));
        r.Ims.Receive(Im(Second, "Second Resident", "three"));
        Assert.Equal([Friend, Second], r.Ims.Open.Select(c => c.Other));
        Assert.Equal(2, At(r.Ims.Open, 0).Lines.Count);
        Assert.Equal(2, arrivals);
        Assert.Equal(At(r.Ims.Open, 0).SessionId, r.Ims.Active);   // a new arrival does not take the shown tab
    }

    [Fact]
    public void A_line_into_the_shown_conversation_while_the_panel_is_visible_is_read_at_once()
    {
        var r = new Rig();
        r.Ims.Receive(Im(Friend, "Example Resident", "one"));
        r.Ims.Visible = true;
        r.Ims.Activate(At(r.Ims.Open, 0).SessionId);
        Assert.Equal(0, At(r.Ims.Open, 0).Unread);
        r.Ims.Receive(Im(Friend, "Example Resident", "two"));
        Assert.Equal(0, At(r.Ims.Open, 0).Unread);
        r.Ims.Receive(Im(Second, "Second Resident", "elsewhere"));
        Assert.Equal(1, At(r.Ims.Open, 1).Unread);
    }

    [Fact]
    public void A_stored_message_is_marked_saved_with_its_sent_time()
    {
        var r = new Rig();
        var sent = new DateTimeOffset(2026, 2, 1, 9, 30, 0, TimeSpan.Zero);
        r.Ims.Receive(Im(Friend, "Example Resident", "while you were away", offline: true, sentAt: sent));
        var line = Assert.Single(Assert.Single(r.Ims.Open).Lines);
        Assert.True(line.Saved);
        Assert.Equal(sent, line.SavedAt);
        Assert.Equal(1, At(r.Ims.Open, 0).Unread);
    }

    [Fact]
    public void Messages_from_the_grid_itself_and_message_boxes_become_system_lines_not_conversations()
    {
        var r = new Rig();
        r.Ims.Receive(Im(Guid.Empty, "", "grid notice"));
        r.Ims.Receive(Im(Friend, "Example Resident", "a box", ImKind.MessageBox, dialog: 1));
        Assert.Empty(r.Ims.Open);
        Assert.Equal(["grid notice", "a box"], r.SystemLines);
    }

    [Fact]
    public void Group_and_other_dialogs_are_counted_and_not_shown()
    {
        var r = new Rig();
        r.Ims.Receive(Im(Friend, "Example Resident", "group line", group: true, dialog: 17));
        r.Ims.Receive(Im(Friend, "Example Resident", "an offer", ImKind.Other, dialog: 38));
        Assert.Empty(r.Ims.Open);
        Assert.Empty(r.SystemLines);
        Assert.Equal(new Dictionary<int, int> { [17] = 1, [38] = 1 }, r.Ims.Ignored);
    }

    [Fact]
    public void An_emote_message_reads_as_an_emote()
    {
        var r = new Rig();
        r.Ims.Receive(Im(Friend, "Example Resident", "/me waves"));
        var line = Assert.Single(At(r.Ims.Open, 0).Lines);
        Assert.True(line.Emote);
        Assert.Equal(" waves", line.Text);
    }

    [Fact]
    public void A_sender_with_no_name_is_shown_by_the_name_cache_or_the_ids_short_form_and_renamed_when_the_name_arrives()
    {
        var r = new Rig();
        r.Ims.Receive(Im(Friend, "", "who am I"));
        Assert.Equal(NameCache.Placeholder(Friend), At(r.Ims.Open, 0).Name);
        r.Ims.Rename(Friend, "Example Resident");
        Assert.Equal("Example Resident", At(r.Ims.Open, 0).Name);
        r.Names.Learn(Second, "Second Resident");
        r.Ims.Receive(Im(Second, "", "named by the cache"));
        Assert.Equal("Second Resident", At(r.Ims.Open, 1).Name);
    }

    // ---------------- the other side's typing ----------------

    [Fact]
    public void The_others_typing_shows_until_they_stop_send_a_line_or_go_quiet_for_nine_seconds()
    {
        var r = new Rig();
        r.Ims.Receive(Im(Friend, "Example Resident", "hi"));
        var c = At(r.Ims.Open, 0);
        r.Ims.Receive(Im(Friend, "Example Resident", "typing", ImKind.StartTyping));
        Assert.True(c.OtherTyping);
        r.Ims.Receive(Im(Friend, "Example Resident", "typing", ImKind.StopTyping));
        Assert.False(c.OtherTyping);
        r.Ims.Receive(Im(Friend, "Example Resident", "typing", ImKind.StartTyping));
        r.Ims.Receive(Im(Friend, "Example Resident", "a line"));
        Assert.False(c.OtherTyping);
        r.Ims.Receive(Im(Friend, "Example Resident", "typing", ImKind.StartTyping));
        r.Now += TimeSpan.FromSeconds(8.5);
        r.Ims.Tick();
        Assert.True(c.OtherTyping);
        r.Now += TimeSpan.FromSeconds(1);
        r.Ims.Tick();
        Assert.False(c.OtherTyping);
    }

    [Fact]
    public void Typing_from_someone_with_no_open_conversation_opens_nothing()
    {
        var r = new Rig();
        r.Ims.Receive(Im(Friend, "Example Resident", "typing", ImKind.StartTyping));
        Assert.Empty(r.Ims.Open);
    }

    // ---------------- opening, sending and typing sent ----------------

    [Fact]
    public void The_user_opens_a_conversation_and_it_becomes_the_shown_one_without_a_second_tab()
    {
        var r = new Rig();
        r.Names.Learn(Friend, "Example Resident");
        var c = r.Ims.OpenWith(Friend)!;
        Assert.Equal("Example Resident", c.Name);
        Assert.Same(c, r.Ims.OpenWith(Friend));
        Assert.Single(r.Ims.Open);
        Assert.Equal(c.SessionId, r.Ims.Active);
        Assert.Null(r.Ims.OpenWith(Guid.Empty));
        Assert.Null(r.Ims.OpenWith(Self));   // no conversation with oneself
    }

    [Fact]
    public void A_sent_message_goes_through_the_pipeline_and_appears_as_ones_own_line()
    {
        var r = new Rig();
        var c = r.Ims.OpenWith(Friend)!;
        Assert.True(r.Ims.Send(c.SessionId, "  hello  "));
        Assert.Equal(new InstantMessageIntent(Friend, "hello"), Assert.Single(r.Submitted.OfType<InstantMessageIntent>()));
        var line = Assert.Single(c.Lines);
        Assert.Equal(ChatLineStyle.Own, line.Style);
        Assert.Equal(Self, line.SpeakerId);
        Assert.Equal("hello", line.Text);
        Assert.Equal(0, c.Unread);
    }

    [Fact]
    public void A_refused_message_keeps_the_input_adds_no_line_and_says_why()
    {
        var r = new Rig { Answer = _ => new ActionResult(ActionStatus.Unavailable) };
        var c = r.Ims.OpenWith(Friend)!;
        Assert.False(r.Ims.Send(c.SessionId, "hello"));
        Assert.Empty(c.Lines);
        Assert.Equal("status.unavailable", c.Status?.Key);
    }

    [Fact]
    public void An_empty_message_sends_nothing()
    {
        var r = new Rig();
        var c = r.Ims.OpenWith(Friend)!;
        Assert.True(r.Ims.Send(c.SessionId, "   "));
        Assert.Empty(r.Submitted);
    }

    [Fact]
    public void Typing_is_sent_on_the_change_again_every_four_seconds_and_stopped_by_an_empty_input_or_a_send()
    {
        var r = new Rig();
        var c = r.Ims.OpenWith(Friend)!;
        r.Ims.InputChanged(c.SessionId, "h");
        r.Ims.InputChanged(c.SessionId, "he");
        Assert.Equal([new ImTypingIntent(Friend, true)], r.TypingSent);
        r.Now += TimeSpan.FromSeconds(3.5);
        r.Ims.Tick();
        Assert.Single(r.TypingSent);
        r.Now += TimeSpan.FromSeconds(1);
        r.Ims.Tick();
        Assert.Equal(2, r.TypingSent.Count());
        r.Now += TimeSpan.FromSeconds(30);   // no idle stop for a conversation: only an empty input or a send stops it
        r.Ims.Tick();
        Assert.True(LastOf(r.TypingSent).Started);
        r.Ims.InputChanged(c.SessionId, "");
        Assert.False(LastOf(r.TypingSent).Started);
        r.Ims.InputChanged(c.SessionId, "x");
        r.Ims.Send(c.SessionId, "x");
        Assert.Equal([true, true, true, false, true, false], r.TypingSent.Select(t => t.Started));
    }

    [Fact]
    public void Closing_a_conversation_stops_its_typing_and_forgets_it_until_the_next_message()
    {
        var r = new Rig();
        var c = r.Ims.OpenWith(Friend)!;
        r.Ims.InputChanged(c.SessionId, "x");
        r.Ims.Close(c.SessionId);
        Assert.False(LastOf(r.TypingSent).Started);
        Assert.Empty(r.Ims.Open);
        Assert.Null(r.Ims.Active);
        r.Ims.Receive(Im(Friend, "Example Resident", "back"));
        Assert.Single(Assert.Single(r.Ims.Open).Lines);
    }

    [Fact]
    public void Closing_the_shown_conversation_shows_the_one_before_it()
    {
        var r = new Rig();
        var a = r.Ims.OpenWith(Friend)!;
        var b = r.Ims.OpenWith(Second)!;
        r.Ims.Close(b.SessionId);
        Assert.Equal(a.SessionId, r.Ims.Active);
    }

    [Fact]
    public void With_typing_turned_off_no_typing_is_sent_for_a_conversation()
    {
        var r = new Rig { SendTyping = false };
        var c = r.Ims.OpenWith(Friend)!;
        r.Ims.InputChanged(c.SessionId, "x");
        r.Ims.Send(c.SessionId, "x");
        Assert.Empty(r.TypingSent);
    }

    // ---------------- the hub ----------------

    private static ChatReceived Chat(Guid from, string text) =>
        new(null, from, from, "Example Resident", ChatSource.Agent, ChatLineType.Say, ChatAudible.Fully, Vector3.Zero, new MessageBody(text, MessageChannel.LocalChat));

    [Fact]
    public void The_hub_holds_events_from_any_thread_until_the_frame_pumps_them_into_the_models()
    {
        var names = new NameCache();
        var hub = new CommsHub(Self, names, _ => new ActionResult(ActionStatus.Sent, 1), new SettingsService(null), () => T0);
        var threads = Enumerable.Range(0, 4).Select(i => new Thread(() =>
        {
            for (int k = 0; k < 25; k++)
                hub.Enqueue(Chat(Friend, $"t{i} line {k}"));
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        hub.Enqueue(Im(Friend, "Example Resident", "an im"));
        Assert.Empty(hub.Nearby.Lines);   // nothing reaches a model before the pump
        Assert.Equal(101, hub.Pump());
        Assert.Equal(100, hub.Nearby.Lines.Count);
        Assert.Single(hub.Ims.Open);
        Assert.Equal(0, hub.Pump());
    }

    [Fact]
    public void The_hubs_incoming_filter_sees_each_event_before_the_models_and_may_change_or_drop_it()
    {
        var hub = new CommsHub(Self, new NameCache(), _ => new ActionResult(ActionStatus.Sent, 1), new SettingsService(null), () => T0)
        {
            Incoming = e => e is ChatReceived c ? c.Text.Reveal() switch
            {
                "drop me" => null,
                "change me" => c with { Text = new MessageBody("...", MessageChannel.LocalChat) },
                _ => e,
            } : e,
        };
        hub.Enqueue(Chat(Friend, "drop me"));
        hub.Enqueue(Chat(Friend, "change me"));
        hub.Enqueue(Chat(Friend, "keep me"));
        Assert.Equal(3, hub.Pump());
        Assert.Equal(["...", "keep me"], hub.Nearby.Lines.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void The_hub_routes_system_messages_into_nearby_chat_and_renames_conversations_when_names_arrive()
    {
        var names = new NameCache();
        var hub = new CommsHub(Self, names, _ => new ActionResult(ActionStatus.Sent, 1), new SettingsService(null), () => T0);
        hub.Enqueue(Im(Guid.Empty, "", "grid notice"));
        hub.Enqueue(Im(Friend, "", "nameless"));
        hub.Pump();
        Assert.Equal("grid notice", Assert.Single(hub.Nearby.Lines).Text);
        names.Learn(Friend, "Example Resident");
        hub.Enqueue(new AgentNamesReceived(new Dictionary<Guid, string> { [Friend] = "Example Resident" }));
        hub.Pump();
        Assert.Equal("Example Resident", At(hub.Ims.Open, 0).Name);
    }

    [Fact]
    public void The_hub_asks_for_queued_names_at_most_once_a_second()
    {
        var now = T0;
        int asks = 0;
        var hub = new CommsHub(Self, new NameCache(), _ => new ActionResult(ActionStatus.Sent, 1), new SettingsService(null), () => now, () => { asks++; return 0; });
        hub.Pump();
        hub.Pump();
        Assert.Equal(1, asks);
        now += TimeSpan.FromSeconds(1.1);
        hub.Pump();
        Assert.Equal(2, asks);
    }

    [Fact]
    public void The_hub_reads_the_history_size_and_typing_settings_as_they_are_now()
    {
        var settings = new SettingsService(null);
        var submitted = new List<ActionIntent>();
        var hub = new CommsHub(Self, new NameCache(), i => { submitted.Add(i); return new ActionResult(ActionStatus.Sent, 1); }, settings, () => T0);
        settings.Set(Settings.ChatHistoryLines, 50);
        for (int i = 0; i < 60; i++)
            hub.Enqueue(Chat(Friend, "line " + i));
        hub.Pump();
        Assert.Equal(50, hub.Nearby.Lines.Count);
        settings.Set(Settings.ChatSendTyping, false);
        hub.Nearby.InputChanged("abc");
        Assert.Empty(submitted.OfType<ChatTypingIntent>());
    }
}
