// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Nearby chat without the engine or a grid: the input's channel and range rules, typing sent, lines received (hearing,
/// typing marks, emotes, ranges, styles, the bounded history) and the rich text they become. Sends go through a stand-in for
/// the pipeline's Submit; times come from a test clock. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class NearbyChatTests
{
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Other = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Box = Guid.Parse("b0b0b0b0-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset T0 = new(2026, 3, 4, 13, 5, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public DateTimeOffset Now = T0;
        public readonly List<ActionIntent> Submitted = new();
        public Func<ActionIntent, ActionResult> Answer = _ => new ActionResult(ActionStatus.Sent, 1);
        public int Limit = 500;
        public bool SendTyping = true;
        public readonly NearbyChat Chat;

        public Rig() => Chat = new NearbyChat(Self, i => { Submitted.Add(i); return Answer(i); }, () => Limit, () => SendTyping, () => Now);

        public IEnumerable<SayIntent> Says => Submitted.OfType<SayIntent>();
        public IEnumerable<bool> TypingSent => Submitted.OfType<ChatTypingIntent>().Select(t => t.Started);
    }

    private static ChatReceived Line(Guid from, string name, string text, ChatLineType type = ChatLineType.Say, ChatSource source = ChatSource.Agent,
        ChatAudible audible = ChatAudible.Fully) =>
        new(null, from, from, name, source, type, audible, Vector3.Zero, new MessageBody(text, MessageChannel.LocalChat));

    // ---------------- the input's channel and range rules ----------------

    [Theory]
    [InlineData("hello", "hello", 0, ChatKind.Say)]
    [InlineData("  hello  ", "hello", 0, ChatKind.Say)]
    [InlineData("/5 open", "open", 5, ChatKind.Say)]
    [InlineData("/5open", "open", 5, ChatKind.Say)]
    [InlineData("/42   spaced", "spaced", 42, ChatKind.Say)]
    [InlineData("/-7 negative", "negative", -7, ChatKind.Say)]
    [InlineData("/me waves", "/me waves", 0, ChatKind.Say)]
    [InlineData("/whisper quietly", "quietly", 0, ChatKind.Whisper)]
    [InlineData("/SHOUT loudly", "loudly", 0, ChatKind.Shout)]
    [InlineData("/shoutnospace", "nospace", 0, ChatKind.Shout)]
    [InlineData("/- not a channel", "/- not a channel", 0, ChatKind.Say)]
    [InlineData("/x", "/x", 0, ChatKind.Say)]
    public void A_line_is_read_for_its_channel_and_range(string raw, string text, int channel, ChatKind kind)
    {
        var p = new ChatInputParser();
        Assert.Equal(new ParsedChat(text, channel, kind), p.Parse(raw, ChatKind.Say));
    }

    [Fact]
    public void A_double_slash_repeats_the_last_channel_given_with_a_prefix()
    {
        var p = new ChatInputParser();
        Assert.Equal(new ParsedChat("first", 0, ChatKind.Say), p.Parse("//first", ChatKind.Say));
        p.Parse("/9 set", ChatKind.Say);
        Assert.Equal(9, p.LastChannel);
        Assert.Equal(new ParsedChat("again", 9, ChatKind.Say), p.Parse("//again", ChatKind.Say));
        p.Parse("plain", ChatKind.Say);
        Assert.Equal(new ParsedChat("still nine", 9, ChatKind.Say), p.Parse("//still nine", ChatKind.Say));
    }

    [Fact]
    public void A_range_trigger_only_changes_a_say_line()
    {
        var p = new ChatInputParser();
        Assert.Equal(new ParsedChat("hi", 0, ChatKind.Whisper), p.Parse("/shout hi", ChatKind.Whisper));
    }

    // ---------------- sending ----------------

    [Fact]
    public void Enter_says_and_the_keys_whisper_and_shout_through_the_pipeline()
    {
        var r = new Rig();
        Assert.True(r.Chat.Send("one"));
        Assert.True(r.Chat.Send("two", ChatKind.Whisper));
        Assert.True(r.Chat.Send("three", ChatKind.Shout));
        Assert.Equal([new SayIntent("one", 0, ChatKind.Say), new SayIntent("two", 0, ChatKind.Whisper), new SayIntent("three", 0, ChatKind.Shout)], r.Says);
    }

    [Fact]
    public void The_chosen_range_is_used_when_no_key_chooses_one()
    {
        var r = new Rig();
        r.Chat.Chosen = ChatKind.Shout;
        r.Chat.Send("loud");
        Assert.Equal(new SayIntent("loud", 0, ChatKind.Shout), Assert.Single(r.Says));
    }

    [Fact]
    public void A_channel_line_goes_on_its_channel()
    {
        var r = new Rig();
        r.Chat.Send("/7 door");
        Assert.Equal(new SayIntent("door", 7, ChatKind.Say), Assert.Single(r.Says));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_line_sends_nothing_and_clears_the_input(string text)
    {
        var r = new Rig();
        Assert.True(r.Chat.Send(text));
        Assert.Empty(r.Says);
    }

    [Fact]
    public void A_refused_line_keeps_the_input_and_says_why_and_a_later_send_clears_the_reason()
    {
        var r = new Rig { Answer = _ => new ActionResult(ActionStatus.Invalid, 1, null, [new("say.too_long", "2000 bytes")]) };
        Assert.False(r.Chat.Send("long"));
        Assert.Equal(("status.say.too_long", (string?)"2000 bytes"), r.Chat.Status);
        r.Answer = _ => new ActionResult(ActionStatus.Sent, 2);
        Assert.True(r.Chat.Send("short"));
        Assert.Null(r.Chat.Status);
    }

    [Theory]
    [InlineData(ActionStatus.Vetoed, "status.vetoed")]
    [InlineData(ActionStatus.Unavailable, "status.unavailable")]
    [InlineData(ActionStatus.Failed, "status.failed")]
    public void Each_kind_of_refusal_has_its_own_status(ActionStatus status, string key)
    {
        var r = new Rig { Answer = _ => new ActionResult(status, 1, "code") };
        Assert.False(r.Chat.Send("x"));
        Assert.Equal(key, r.Chat.Status?.Key);
    }

    [Fact]
    public void An_unknown_outcome_clears_the_input_so_the_line_is_not_sent_twice_by_accident()
    {
        // the pipeline never retries an unknown outcome; keeping the text would invite a second send of the same line
        var r = new Rig { Answer = _ => new ActionResult(ActionStatus.OutcomeUnknown, 1) };
        Assert.True(r.Chat.Send("x"));
        Assert.Equal("status.unknown", r.Chat.Status?.Key);
    }

    // ---------------- typing sent ----------------

    [Fact]
    public void Typing_starts_once_on_the_first_keystroke_and_stops_when_the_line_is_sent()
    {
        var r = new Rig();
        r.Chat.InputChanged("h");
        r.Chat.InputChanged("he");
        r.Chat.InputChanged("hel");
        Assert.Equal([true], r.TypingSent);
        Assert.True(r.Chat.OwnTyping);
        r.Chat.Send("hel");
        Assert.Equal([true, false], r.TypingSent);
        Assert.False(r.Chat.OwnTyping);
    }

    [Fact]
    public void A_slash_line_or_an_emptied_input_is_not_typing()
    {
        var r = new Rig();
        r.Chat.InputChanged("/");
        Assert.Empty(r.TypingSent);
        r.Chat.InputChanged("a");
        r.Chat.InputChanged("");
        r.Chat.InputChanged("  /5 x");
        Assert.Equal([true, false], r.TypingSent);
    }

    [Fact]
    public void Typing_stops_after_five_quiet_seconds_and_each_keystroke_rearms_it()
    {
        var r = new Rig();
        r.Chat.InputChanged("a");
        r.Now += TimeSpan.FromSeconds(4);
        r.Chat.InputChanged("ab");
        r.Now += TimeSpan.FromSeconds(4);
        r.Chat.Tick();
        Assert.Equal([true], r.TypingSent);
        r.Now += TimeSpan.FromSeconds(1.5);
        r.Chat.Tick();
        Assert.Equal([true, false], r.TypingSent);
        r.Chat.InputChanged("abc");
        Assert.Equal([true, false, true], r.TypingSent);
    }

    [Fact]
    public void Typing_stops_when_the_input_loses_the_keyboard()
    {
        var r = new Rig();
        r.Chat.InputChanged("a");
        r.Chat.InputLostFocus();
        Assert.Equal([true, false], r.TypingSent);
        r.Chat.InputLostFocus();
        Assert.Equal([true, false], r.TypingSent);
    }

    [Fact]
    public void With_typing_turned_off_in_settings_nothing_is_sent()
    {
        var r = new Rig { SendTyping = false };
        r.Chat.InputChanged("a");
        r.Chat.Send("a");
        Assert.Empty(r.TypingSent);
    }

    [Fact]
    public void A_refused_typing_start_is_not_followed_by_a_stop()
    {
        var r = new Rig { Answer = i => i is ChatTypingIntent ? new ActionResult(ActionStatus.Unavailable) : new ActionResult(ActionStatus.Sent, 1) };
        r.Chat.InputChanged("a");
        Assert.False(r.Chat.OwnTyping);
        r.Chat.Send("a");
        Assert.Equal([true], r.TypingSent);   // the one refused start; no stop for a start that never went
    }

    // ---------------- receiving ----------------

    [Fact]
    public void A_heard_line_is_added_with_its_speaker_style_and_time()
    {
        var r = new Rig();
        r.Chat.Receive(Line(Other, "Example Resident", "hello there"));
        var line = Assert.Single(r.Chat.Lines);
        Assert.Equal(new ChatLine(T0, ChatLineStyle.Agent, Other, "Example Resident", "hello there"), line);
    }

    [Theory]
    [InlineData(ChatAudible.Barely)]
    [InlineData(ChatAudible.Not)]
    public void A_line_not_fully_heard_is_not_shown(ChatAudible audible)
    {
        var r = new Rig();
        r.Chat.Receive(Line(Other, "Example Resident", "faint", audible: audible));
        Assert.Empty(r.Chat.Lines);
    }

    [Fact]
    public void Own_object_and_system_lines_are_styled_apart()
    {
        var r = new Rig();
        r.Chat.Receive(Line(Self, "Test User", "mine"));
        r.Chat.Receive(Line(Box, "A Box", "beep", source: ChatSource.Object));
        r.Chat.Receive(Line(Guid.Empty, "", "region notice", source: ChatSource.System));
        r.Chat.AddSystem("grid notice");
        Assert.Equal([ChatLineStyle.Own, ChatLineStyle.Object, ChatLineStyle.System, ChatLineStyle.System], r.Chat.Lines.Select(l => l.Style));
    }

    [Fact]
    public void Whispers_and_shouts_keep_their_range()
    {
        var r = new Rig();
        r.Chat.Receive(Line(Other, "Example Resident", "psst", ChatLineType.Whisper));
        r.Chat.Receive(Line(Other, "Example Resident", "HEY", ChatLineType.Shout));
        Assert.Equal([ChatKind.Whisper, ChatKind.Shout], r.Chat.Lines.Select(l => l.Range));
    }

    [Theory]
    [InlineData("/me waves", " waves")]
    [InlineData("/me's hat", "'s hat")]
    public void An_emote_is_the_name_followed_by_the_rest(string text, string rest)
    {
        var r = new Rig();
        r.Chat.Receive(Line(Other, "Example Resident", text));
        var line = Assert.Single(r.Chat.Lines);
        Assert.True(line.Emote);
        Assert.Equal(rest, line.Text);
    }

    [Fact]
    public void A_line_merely_starting_with_slash_me_is_not_an_emote()
    {
        var r = new Rig();
        r.Chat.Receive(Line(Other, "Example Resident", "/meet me here"));
        Assert.False(Assert.Single(r.Chat.Lines).Emote);
    }

    [Fact]
    public void Typing_marks_come_and_go_with_start_stop_and_a_real_line_and_show_no_line()
    {
        var r = new Rig();
        var third = Guid.NewGuid();
        r.Chat.Receive(Line(Other, "Example Resident", "", ChatLineType.StartTyping));
        r.Chat.Receive(Line(third, "Third Resident", "", ChatLineType.StartTyping));
        Assert.Equal([(Other, "Example Resident"), (third, "Third Resident")], r.Chat.Typing);
        r.Chat.Receive(Line(Other, "Example Resident", "done typing"));
        r.Chat.Receive(Line(third, "Third Resident", "", ChatLineType.StopTyping));
        Assert.Empty(r.Chat.Typing);
        Assert.Single(r.Chat.Lines);
    }

    [Fact]
    public void A_typing_mark_with_no_news_clears_after_nine_seconds()
    {
        var r = new Rig();
        r.Chat.Receive(Line(Other, "Example Resident", "", ChatLineType.StartTyping));
        r.Now += TimeSpan.FromSeconds(8.5);
        r.Chat.Tick();
        Assert.Single(r.Chat.Typing);
        r.Now += TimeSpan.FromSeconds(1);
        r.Chat.Tick();
        Assert.Empty(r.Chat.Typing);
    }

    [Fact]
    public void This_users_own_typing_is_not_shown_and_a_typing_mark_not_heard_is_ignored()
    {
        var r = new Rig();
        r.Chat.Receive(Line(Self, "Test User", "", ChatLineType.StartTyping));
        r.Chat.Receive(Line(Other, "Example Resident", "", ChatLineType.StartTyping, audible: ChatAudible.Barely));
        Assert.Empty(r.Chat.Typing);
    }

    [Fact]
    public void The_history_keeps_only_the_newest_lines_up_to_its_limit()
    {
        var r = new Rig { Limit = 3 };
        for (int i = 0; i < 5; i++)
            r.Chat.Receive(Line(Other, "Example Resident", "line " + i));
        Assert.Equal(["line 2", "line 3", "line 4"], r.Chat.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Every_shown_change_is_announced()
    {
        var r = new Rig();
        int changes = 0;
        r.Chat.Changed += () => changes++;
        r.Chat.Receive(Line(Other, "Example Resident", "", ChatLineType.StartTyping));
        r.Chat.Receive(Line(Other, "Example Resident", "a line"));
        r.Chat.Receive(Line(Other, "Example Resident", "faint", audible: ChatAudible.Not));
        Assert.Equal(2, changes);
    }

    // ---------------- rich text ----------------

    private static readonly StringTable En = StringTable.English();

    [Fact]
    public void Text_from_the_grid_cannot_add_formatting_or_links()
    {
        var line = new ChatLine(T0, ChatLineStyle.Agent, Other, "Evil [b]Name[/b]", "[url=https://example.org]click[/url] [color=red]x");
        string bb = ChatText.Format(line, ThemeSpec.Default, En, showTime: false);
        Assert.DoesNotContain("[b]", bb);
        Assert.DoesNotContain("[url=https", bb);
        Assert.DoesNotContain("[color=red]", bb);
        Assert.Contains("[lb]url=https://example.org]click[lb]/url]", bb);
        Assert.Equal("[lb]b]", ChatText.Escape("[b]"));
    }

    [Fact]
    public void A_residents_name_links_to_a_conversation_with_them_and_an_objects_does_not()
    {
        string agent = ChatText.Format(new ChatLine(T0, ChatLineStyle.Agent, Other, "Example Resident", "hi"), ThemeSpec.Default, En, false);
        string obj = ChatText.Format(new ChatLine(T0, ChatLineStyle.Object, Box, "A Box", "beep"), ThemeSpec.Default, En, false);
        Assert.Contains("[url=agent:" + Other.ToString("D") + "]", agent);
        Assert.DoesNotContain("[url", obj);
        Assert.Equal(Other, ChatText.AgentOf("agent:" + Other.ToString("D")));
        Assert.Null(ChatText.AgentOf("https://example.org"));
        Assert.Null(ChatText.AgentOf("agent:not-an-id"));
    }

    [Fact]
    public void Lines_read_as_the_string_table_says_with_their_colours_and_an_optional_time()
    {
        var t = ThemeSpec.Default;
        string say = ChatText.Format(new ChatLine(T0, ChatLineStyle.Object, Box, "A Box", "beep"), t, En, showTime: true);
        Assert.StartsWith("[color=" + t.MutedText.Hex + "][lb]13:05] [/color]", say);
        Assert.Contains("[color=" + t.ChatObject.Hex + "]", say);
        Assert.EndsWith("A Box: beep[/color]", say);
        string whisper = ChatText.Format(new ChatLine(T0, ChatLineStyle.Own, Self, "Test User", "psst", ChatKind.Whisper), t, En, false);
        Assert.Contains("[color=" + t.ChatOwn.Hex + "]", whisper);
        Assert.Contains(" whispers: psst", whisper);
        string emote = ChatText.Format(new ChatLine(T0, ChatLineStyle.Agent, Other, "Example Resident", " waves", Emote: true), t, En, false);
        Assert.EndsWith("Example Resident[/url] waves[/color]", emote);
        string system = ChatText.Format(new ChatLine(T0, ChatLineStyle.System, Guid.Empty, "", "Region restarting"), t, En, false);
        Assert.Equal("[color=" + t.ChatSystem.Hex + "]Region restarting[/color]", system);
    }

    [Fact]
    public void A_saved_message_says_when_it_was_sent()
    {
        var sent = new DateTimeOffset(2026, 2, 1, 9, 30, 0, TimeSpan.Zero);
        string bb = ChatText.Format(new ChatLine(T0, ChatLineStyle.Agent, Other, "Example Resident", "while you were away", Saved: true, SavedAt: sent), ThemeSpec.Default, En, false);
        Assert.Contains("(saved message, sent 2026-02-01 09:30) ", bb);
        string unknown = ChatText.Format(new ChatLine(T0, ChatLineStyle.Agent, Other, "Example Resident", "x", Saved: true), ThemeSpec.Default, En, false);
        Assert.Contains("(saved message) ", unknown);
    }

    [Fact]
    public void A_status_reads_from_its_string_with_its_detail()
    {
        Assert.Equal("That line is too long to send (2000 bytes). Please shorten it.", ChatText.Status(("status.say.too_long", "2000 bytes"), En));
        Assert.Equal("Sending that is not allowed right now.", ChatText.Status(("status.vetoed", null), En));
        Assert.Equal("That could not be sent.", ChatText.Status(("status.nonexistent.code", null), En));
    }

    [Fact]
    public void Every_issue_code_the_chat_and_message_intents_can_give_has_a_status_string()
    {
        foreach (var code in new[] { "say.empty", "say.too_long", "say.control_chars", "im.empty", "im.too_long", "im.control_chars", "im.no_target" })
            Assert.True(En.Has("status." + code), code);
    }
}
