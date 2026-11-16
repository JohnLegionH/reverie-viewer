// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Session;
using Viewer.Core.Social;

namespace Viewer.Shell.Chat;

// Behaviour follows the official viewer's nearby chat and instant message sessions, written from a behaviour spec in our
// own words (no code, text or layout copied); each rule names the official file it was read from where it matters.

/// <summary>How a chat or message line is drawn.</summary>
public enum ChatLineStyle { Own, Agent, Object, System }

/// <summary>One line of nearby chat or of a conversation, as text for the interface (never logged).</summary>
/// <param name="Name">The speaker's name as shown (empty for a system line).</param>
/// <param name="Range">Whisper or Shout adds the range word; Say adds none.</param>
/// <param name="Emote">A line written as "/me ...": shown as the name followed directly by <paramref name="Text"/>.</param>
/// <param name="Saved">A stored message delivered at login; <paramref name="SavedAt"/> is when it was sent, when known.</param>
/// <param name="ObjectId">The speaking object, for a line from an object (empty otherwise).</param>
/// <param name="OwnerId">The object's owner, when the region said.</param>
/// <param name="Mark">What kind of object line this is (owner-only, direct to this user, a script error, an object's instant message).</param>
/// <param name="Place">The place text an object's instant message carried.</param>
/// <param name="Far">The speaker was beyond the grid's say range when the line arrived (shown dimmed).</param>
public sealed record ChatLine(DateTimeOffset At, ChatLineStyle Style, Guid SpeakerId, string Name, string Text, ChatKind Range = ChatKind.Say,
    bool Emote = false, bool Saved = false, DateTimeOffset? SavedAt = null, Guid ObjectId = default, Guid OwnerId = default,
    ChatMark Mark = ChatMark.None, string? Place = null, bool Far = false);

/// <summary>How an object's line is marked: none, heard only by its owner (owner say), sent to this user alone (region say-to), a script error, or an object's instant message.</summary>
public enum ChatMark { None, OwnerOnly, Direct, ScriptError, ObjectMessage }

/// <summary>What the chat input's text means: the line to send, its channel and its range.</summary>
public readonly record struct ParsedChat(string Text, int Channel, ChatKind Kind);

/// <summary>
/// Reads a nearby chat line the way the official viewer does (llfloaterimnearbychat.cpp, stripChannelNumber and the type
/// triggers): "//text" repeats the last channel given with a prefix, "/N text" or "/Ntext" (N may start with '-') speaks on
/// channel N and remembers it, "/whisper" and "/shout" at the start change a Say line's range, anything else is channel 0
/// unchanged. The text is trimmed. One parser per chat input (it remembers the last channel).
/// </summary>
public sealed class ChatInputParser
{
    private static readonly (string Trigger, ChatKind Kind)[] Triggers = [("/whisper", ChatKind.Whisper), ("/shout", ChatKind.Shout)];

    public int LastChannel { get; private set; }

    public ParsedChat Parse(string raw, ChatKind chosen)
    {
        string text = (raw ?? "").Trim();
        int channel = 0;
        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            channel = LastChannel;
            text = text[2..];
        }
        else if (text.Length > 1 && text[0] == '/' && (char.IsAsciiDigit(text[1]) || (text[1] == '-' && text.Length > 2 && char.IsAsciiDigit(text[2]))))
        {
            int end = 2;
            while (end < text.Length && end < 64 && char.IsAsciiDigit(text[end]))
                end++;
            // a number too large for a channel is held at the largest one, as the C library's conversion does
            channel = long.TryParse(text.AsSpan(1, end - 1), out long n)
                ? (int)Math.Clamp(n, int.MinValue, int.MaxValue)
                : (text[1] == '-' ? int.MinValue : int.MaxValue);
            LastChannel = channel;
            text = text[end..];
        }
        text = text.Trim();
        var kind = chosen;
        foreach (var (trigger, triggerKind) in Triggers)
        {
            if (!text.StartsWith(trigger, StringComparison.OrdinalIgnoreCase))
                continue;
            // the trigger (and one space after it) is taken off either way; it changes only a Say line's range
            text = text[trigger.Length..];
            if (text.StartsWith(' '))
                text = text[1..];
            if (chosen == ChatKind.Say)
                kind = triggerKind;
            break;
        }
        return new ParsedChat(text.Trim(), channel, kind);
    }
}

/// <summary>
/// Whether others are told this user is typing, as a small state machine over a send function (which returns true when the
/// message went). Starting sends once on the change; stopping sends once if a start went. <paramref name="idleStop"/>: stop
/// after that long with no input (nearby chat: 5 s, llagent.cpp). <paramref name="resendEvery"/>: send the start again that
/// often while typing goes on (conversations: 4 s, llfloaterimsession.cpp). <paramref name="enabled"/> false: starts are not sent.
/// </summary>
public sealed class TypingSender(Func<bool, bool> send, Func<bool> enabled, TimeSpan? idleStop, TimeSpan? resendEvery)
{
    private DateTimeOffset _lastInput, _lastSent;

    public bool Typing { get; private set; }

    /// <summary>Raised when typing starts (the start was sent), not again until it has stopped.</summary>
    public event Action? Started;

    public void Input(bool typingNow, DateTimeOffset now)
    {
        if (!typingNow)
        {
            Stop();
            return;
        }
        _lastInput = now;
        if (Typing || !enabled())
            return;
        if (send(true))
        {
            Typing = true;
            _lastSent = now;
            Started?.Invoke();
        }
    }

    public void Tick(DateTimeOffset now)
    {
        if (!Typing)
            return;
        if (idleStop is { } idle && now - _lastInput > idle)
            Stop();
        else if (resendEvery is { } every && now - _lastSent > every && enabled())
        {
            send(true);
            _lastSent = now;
        }
    }

    public void Stop()
    {
        if (!Typing)
            return;
        Typing = false;
        send(false);
    }
}

/// <summary>The time rules of chat and conversations.</summary>
public static class ChatTiming
{
    /// <summary>Nearby chat: typing stops after this long with no keystroke (llagent.cpp, TYPING_TIMEOUT_SECS).</summary>
    public static readonly TimeSpan LocalTypingIdle = TimeSpan.FromSeconds(5);

    /// <summary>Conversations: the typing start is sent again this often while typing goes on (llfloaterimsession.cpp, ME_TYPING_TIMEOUT).</summary>
    public static readonly TimeSpan ImTypingResend = TimeSpan.FromSeconds(4);

    /// <summary>Another's typing mark clears after this long with no news (llfloaterimsession.cpp, OTHER_TYPING_TIMEOUT; used
    /// for nearby chat too, so a lost stop does not leave a mark for ever).</summary>
    public static readonly TimeSpan OtherTypingTimeout = TimeSpan.FromSeconds(9);

    /// <summary>"/me waves" and "/me's hat" are emotes (llviewermessage.cpp): the rest after "/me" follows the name directly.</summary>
    internal static bool IsEmote(string text, out string rest)
    {
        if (text.StartsWith("/me ", StringComparison.Ordinal) || text.StartsWith("/me'", StringComparison.Ordinal))
        {
            rest = text[3..];
            return true;
        }
        rest = text;
        return false;
    }
}

/// <summary>
/// Nearby chat as data: the history (bounded), who is typing, and the input (send through the pipeline, typing sent). Main
/// thread only; the hub feeds it the session's chat events.
/// </summary>
public sealed class NearbyChat
{
    private readonly Guid _self;
    private readonly Func<ActionIntent, ActionResult> _submit;
    private readonly Func<int> _historyLimit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ChatInputParser _parser = new();
    private readonly TypingSender _typing;
    private readonly List<ChatLine> _lines = new();
    private readonly List<(Guid Id, string Name, DateTimeOffset At)> _others = new();

    public NearbyChat(Guid self, Func<ActionIntent, ActionResult> submit, Func<int> historyLimit, Func<bool> sendTyping, Func<DateTimeOffset> clock)
    {
        _self = self;
        _submit = submit;
        _historyLimit = historyLimit;
        _clock = clock;
        _typing = new TypingSender(started => submit(new ChatTypingIntent(started)).Sent, sendTyping, ChatTiming.LocalTypingIdle, null);
        _typing.Started += () => OwnTypingStarted?.Invoke();
    }

    public IReadOnlyList<ChatLine> Lines => _lines;

    /// <summary>The objects that spoke, so a link on a name can open their details.</summary>
    public Viewer.Shell.Notifications.ObjectDirectory Objects { get; } = new();

    /// <summary>An instant message from an object: a line in nearby chat, never a conversation.</summary>
    public void ReceiveObjectMessage(ObjectInstantMessage m)
    {
        string text = m.Text.Reveal();
        if (m.FromSystem)
        {
            AddSystem(text);   // the grid's own sender is not an object
            return;
        }
        Objects.Remember(new Viewer.Shell.Notifications.ObjectInfo(m.ObjectId, m.OwnerId, m.ObjectName, m.GroupOwned, m.Location));
        bool emote = ChatTiming.IsEmote(text, out string rest);
        Add(new ChatLine(_clock(), ChatLineStyle.Object, m.ObjectId, m.ObjectName, rest, ChatKind.Say, emote,
            ObjectId: m.ObjectId, OwnerId: m.OwnerId, Mark: ChatMark.ObjectMessage, Place: m.Location));
    }

    /// <summary>Whether a speaker at a region position is beyond the grid's say range (null: every line is near).</summary>
    public Func<System.Numerics.Vector3, bool>? IsBeyondSayRange { get; set; }

    /// <summary>Who is typing nearby, in the order they started (never this user).</summary>
    public IReadOnlyList<(Guid Id, string Name)> Typing => _others.Select(o => (o.Id, o.Name)).ToArray();

    /// <summary>The range the input sends with when no key chooses another.</summary>
    public ChatKind Chosen { get; set; } = ChatKind.Say;

    /// <summary>A string-table key (and its argument) saying why the last line was not sent; null after a send.</summary>
    public (string Key, string? Arg)? Status { get; private set; }

    /// <summary>Raised when anything shown changed (a line, a typing mark, the status).</summary>
    public event Action? Changed;

    public bool OwnTyping => _typing.Typing;

    /// <summary>Raised when this user starts typing in the chat bar (the moment the avatar's typing starts), once per start.</summary>
    public event Action? OwnTypingStarted;

    public void Receive(ChatReceived chat)
    {
        if (chat.Audible != ChatAudible.Fully)
            return;
        switch (chat.Type)
        {
            case ChatLineType.StartTyping:
                if (chat.SourceId == _self || chat.SourceId == Guid.Empty)
                    return;
                int at = _others.FindIndex(o => o.Id == chat.SourceId);
                if (at >= 0)
                    _others[at] = (chat.SourceId, chat.FromName, _clock());
                else
                    _others.Add((chat.SourceId, chat.FromName, _clock()));
                Changed?.Invoke();
                return;
            case ChatLineType.StopTyping:
                if (_others.RemoveAll(o => o.Id == chat.SourceId) > 0)
                    Changed?.Invoke();
                return;
        }
        _others.RemoveAll(o => o.Id == chat.SourceId);   // a real line from them ends their typing mark
        string text = chat.Text.Reveal();
        bool emote = ChatTiming.IsEmote(text, out string rest);
        var style = chat.Source switch
        {
            ChatSource.System => ChatLineStyle.System,
            ChatSource.Object => ChatLineStyle.Object,
            _ => chat.SourceId == _self ? ChatLineStyle.Own : ChatLineStyle.Agent,
        };
        var range = chat.Type switch { ChatLineType.Whisper => ChatKind.Whisper, ChatLineType.Shout => ChatKind.Shout, _ => ChatKind.Say };
        // a line with a place, from someone else, beyond the grid's say range is shown dimmed (llviewerchat.cpp)
        bool far = chat.Position != System.Numerics.Vector3.Zero && chat.SourceId != _self && IsBeyondSayRange?.Invoke(chat.Position) == true;
        if (chat.Source != ChatSource.Object)
        {
            Add(new ChatLine(_clock(), style, chat.SourceId, chat.FromName, rest, range, emote, Far: far));
            return;
        }
        // a direct line starting with an at sign is for scripts, not for people (llfloaterimnearbychathandler.cpp)
        if (chat.Type == ChatLineType.RegionSayTo && text.StartsWith('@'))
            return;
        var mark = chat.Type switch
        {
            ChatLineType.OwnerSay => ChatMark.OwnerOnly,
            ChatLineType.RegionSayTo => ChatMark.Direct,
            ChatLineType.Debug => ChatMark.ScriptError,
            _ => ChatMark.None,
        };
        Objects.Remember(new Viewer.Shell.Notifications.ObjectInfo(chat.SourceId, chat.OwnerId, chat.FromName, false, "",
            chat.Position == System.Numerics.Vector3.Zero ? null : chat.Position));
        Add(new ChatLine(_clock(), style, chat.SourceId, chat.FromName, rest, range, emote, ObjectId: chat.SourceId, OwnerId: chat.OwnerId, Mark: mark, Far: far));
    }

    /// <summary>A line from the grid itself (a system message or a message box), shown in nearby chat.</summary>
    public void AddSystem(string text) => Add(new ChatLine(_clock(), ChatLineStyle.System, Guid.Empty, "", text));

    private void Add(ChatLine line)
    {
        _lines.Add(line);
        int limit = Math.Max(1, _historyLimit());
        if (_lines.Count > limit)
            _lines.RemoveRange(0, _lines.Count - limit);
        Changed?.Invoke();
    }

    /// <summary>The input's text changed (each keystroke).</summary>
    public void InputChanged(string text)
    {
        string t = (text ?? "").TrimStart();
        _typing.Input(t.Length > 0 && t[0] != '/', _clock());
    }

    /// <summary>The input lost the keyboard.</summary>
    public void InputLostFocus() => _typing.Stop();

    /// <summary>Sends a line: <paramref name="keyKind"/> is the range a key chose (Shift+Enter, Ctrl+Enter), else <see cref="Chosen"/>.
    /// True when the input should be cleared (sent, nothing to send, or an unknown outcome, which must not invite a second
    /// send); false keeps the text (refused, with <see cref="Status"/>).</summary>
    public bool Send(string text, ChatKind? keyKind = null)
    {
        var parsed = _parser.Parse(text, keyKind ?? Chosen);
        _typing.Stop();
        if (parsed.Text.Length == 0)
            return SetStatus(null, true);
        var r = _submit(new SayIntent(parsed.Text, parsed.Channel, parsed.Kind));
        return SetStatus(ChatText.StatusOf(r), r.Sent || r.Status == ActionStatus.OutcomeUnknown);
    }

    private bool SetStatus((string Key, string? Arg)? status, bool clear)
    {
        if (Status != status)
        {
            Status = status;
            Changed?.Invoke();
        }
        return clear;
    }

    /// <summary>Called every frame: typing time-outs.</summary>
    public void Tick()
    {
        var now = _clock();
        _typing.Tick(now);
        if (_others.RemoveAll(o => now - o.At > ChatTiming.OtherTypingTimeout) > 0)
            Changed?.Invoke();
    }
}

/// <summary>Where a group chat session stands.</summary>
public enum GroupChatState { NotGroup, Joining, Joined, Failed }

/// <summary>One conversation: one-to-one, or a group's chat (its session id is the group's id).</summary>
public sealed class Conversation
{
    internal Conversation(Guid sessionId, Guid other, string name) { SessionId = sessionId; Other = other; Name = name; }

    public Guid SessionId { get; }
    public Guid Other { get; }
    public string Name { get; internal set; }
    public List<ChatLine> Lines { get; } = new();
    public int Unread { get; internal set; }
    public bool OtherTyping { get; internal set; }
    public bool IsGroup => Group != GroupChatState.NotGroup;
    public GroupChatState Group { get; internal set; }
    internal DateTimeOffset OtherTypingAt;
    public (string Key, string? Arg)? Status { get; internal set; }
    internal TypingSender? Typing;
}

/// <summary>
/// One-to-one instant message conversations as data: opened by a message or by the user, one tab each in the order opened,
/// unread counts, both sides' typing, stored messages marked, sends through the pipeline. Main thread only.
/// </summary>
public sealed class Conversations
{
    private readonly Guid _self;
    private readonly NameCache _names;
    private readonly Func<ActionIntent, ActionResult> _submit;
    private readonly Func<int> _historyLimit;
    private readonly Func<bool> _sendTyping;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string> _systemLine;
    private readonly List<Conversation> _open = new();
    private readonly Dictionary<int, int> _ignored = new();

    public Conversations(Guid self, NameCache names, Func<ActionIntent, ActionResult> submit, Func<int> historyLimit, Func<bool> sendTyping,
        Func<DateTimeOffset> clock, Action<string> systemLine)
    {
        _self = self;
        _names = names;
        _submit = submit;
        _historyLimit = historyLimit;
        _sendTyping = sendTyping;
        _clock = clock;
        _systemLine = systemLine;
    }

    public IReadOnlyList<Conversation> Open => _open;

    /// <summary>The conversation whose tab is shown (null: none).</summary>
    public Guid? Active { get; private set; }

    /// <summary>Whether the conversations panel is on screen (an incoming line in the shown tab is then read at once).</summary>
    public bool Visible { get; set; }

    /// <summary>Raised when anything shown changed.</summary>
    public event Action? Changed;

    /// <summary>Raised when the user starts a conversation (with a person or a group), each time, as the official viewer's start of an IM.</summary>
    public event Action<Conversation>? Started;

    /// <summary>Raised when a conversation opens because a message arrived.</summary>
    public event Action<Conversation>? Arrived;

    /// <summary>How many messages of kinds this viewer does not handle (group, conference, offers...) arrived, by dialog number.</summary>
    public IReadOnlyDictionary<int, int> Ignored => _ignored;

    public Conversation? Find(Guid sessionId) => _open.Find(c => c.SessionId == sessionId);

    /// <summary>Opens (or shows) a group's chat and asks to join it once. A group's conversation id is the group's id, and it is
    /// listed beside the one-to-one conversations. Null when the id is empty.</summary>
    public Conversation? OpenGroup(Guid group, string name)
    {
        if (group == Guid.Empty)
            return null;
        if (Find(group) is { } existing)
        {
            Activate(group);
            Started?.Invoke(existing);
            return existing;
        }
        var c = new Conversation(group, group, string.IsNullOrWhiteSpace(name) ? NameCache.Placeholder(group) : name) { Group = GroupChatState.Joining };
        _open.Add(c);
        Active = group;
        c.Unread = 0;
        var r = _submit(new GroupChatJoinIntent(group));
        if (!r.Sent)
        {
            c.Group = GroupChatState.Failed;
            c.Status = ChatText.StatusOf(r);
        }
        Changed?.Invoke();
        Started?.Invoke(c);
        return c;
    }

    /// <summary>The grid's answer to a join request.</summary>
    public void ReceiveJoined(GroupChatJoined joined)
    {
        if (Find(joined.GroupId) is not { IsGroup: true } c)
            return;
        c.Group = joined.Success ? GroupChatState.Joined : GroupChatState.Failed;
        c.Status = joined.Success ? null : ("status.group.join_failed", null);
        Changed?.Invoke();
    }

    /// <summary>A line from the viewer itself in the conversation with <paramref name="agent"/>, when one is open (an online notice).</summary>
    public void AddSystemLine(Guid agent, string text)
    {
        if (FindByAgent(agent) is not { IsGroup: false } c)
            return;
        AddLine(c, new ChatLine(_clock(), ChatLineStyle.System, Guid.Empty, "", text));
        Changed?.Invoke();
    }

    private void ReceiveGroup(InstantMessageReceived m)
    {
        if (Find(m.SessionId) is not { IsGroup: true } c)
        {
            // only a chat session this agent joined shows messages
            _ignored[m.Dialog] = _ignored.GetValueOrDefault(m.Dialog) + 1;
            return;
        }
        if (m.FromAgent == _self)
            return;   // our own line was added when it was sent
        bool emote = ChatTiming.IsEmote(m.Text.Reveal(), out string rest);
        AddLine(c, new ChatLine(_clock(), ChatLineStyle.Agent, m.FromAgent, NameFor(m.FromAgent, m.FromName), rest, ChatKind.Say, emote,
            m.Offline, m.Offline ? m.SentAt : null));
        if (!(Visible && Active == c.SessionId))
            c.Unread++;
        Changed?.Invoke();
    }

    private Conversation? FindByAgent(Guid agent) => _open.Find(c => c.Other == agent);

    private string NameFor(Guid agent, string? given) =>
        !string.IsNullOrWhiteSpace(given) ? given : _names.Get(agent) ?? NameCache.Placeholder(agent);

    private Conversation Create(Guid agent, string? name)
    {
        var c = new Conversation(ImSessionId.For(_self, agent), agent, NameFor(agent, name));
        c.Typing = new TypingSender(started => _submit(new ImTypingIntent(agent, started)).Sent, _sendTyping, null, ChatTiming.ImTypingResend);
        _open.Add(c);
        Active ??= c.SessionId;
        return c;
    }

    /// <summary>Opens (or shows) the conversation with <paramref name="agent"/> and makes it the shown one.</summary>
    public Conversation? OpenWith(Guid agent)
    {
        if (agent == Guid.Empty || agent == _self)
            return null;
        var c = FindByAgent(agent) ?? Create(agent, null);
        Activate(c.SessionId);
        Started?.Invoke(c);
        return c;
    }

    public void Activate(Guid sessionId)
    {
        var c = Find(sessionId);
        if (c is null)
            return;
        Active = sessionId;
        c.Unread = 0;
        Changed?.Invoke();
    }

    public void Close(Guid sessionId)
    {
        int at = _open.FindIndex(c => c.SessionId == sessionId);
        if (at < 0)
            return;
        var closing = _open[at];
        closing.Typing?.Stop();
        if (closing.IsGroup)
            _submit(new GroupChatLeaveIntent(closing.SessionId));
        _open.RemoveAt(at);
        if (Active == sessionId)
            Active = _open.Count == 0 ? null : _open[Math.Max(0, at - 1)].SessionId;
        Changed?.Invoke();
    }

    public void Receive(InstantMessageReceived m)
    {
        if (m.Kind == ImKind.GroupMessage)
        {
            ReceiveGroup(m);
            return;
        }
        if (m.Group || m.Kind == ImKind.Other)
        {
            _ignored[m.Dialog] = _ignored.GetValueOrDefault(m.Dialog) + 1;
            return;
        }
        if (m.Kind == ImKind.MessageBox || (m.Kind == ImKind.Message && m.FromAgent == Guid.Empty))
        {
            _systemLine(m.Text.Reveal());
            return;
        }
        var existing = FindByAgent(m.FromAgent);
        switch (m.Kind)
        {
            case ImKind.StartTyping or ImKind.StopTyping:
                if (existing is null)
                    return;   // typing alone opens nothing (llimview.cpp: only an open session shows it)
                existing.OtherTyping = m.Kind == ImKind.StartTyping;
                existing.OtherTypingAt = _clock();
                Changed?.Invoke();
                return;
            case ImKind.Message:
                bool arrived = existing is null;
                var c = existing ?? Create(m.FromAgent, m.FromName);
                string text = m.Text.Reveal();
                bool emote = ChatTiming.IsEmote(text, out string rest);
                AddLine(c, new ChatLine(_clock(), ChatLineStyle.Agent, m.FromAgent, NameFor(m.FromAgent, m.FromName), rest, ChatKind.Say, emote,
                    m.Offline, m.Offline ? m.SentAt : null));
                c.OtherTyping = false;
                if (!(Visible && Active == c.SessionId))
                    c.Unread++;
                if (arrived)
                    Arrived?.Invoke(c);
                Changed?.Invoke();
                return;
        }
    }

    private void AddLine(Conversation c, ChatLine line)
    {
        c.Lines.Add(line);
        int limit = Math.Max(1, _historyLimit());
        if (c.Lines.Count > limit)
            c.Lines.RemoveRange(0, c.Lines.Count - limit);
    }

    public void Rename(Guid agent, string name)
    {
        if (FindByAgent(agent) is { IsGroup: false } c && !string.IsNullOrWhiteSpace(name) && c.Name != name)
        {
            c.Name = name;
            Changed?.Invoke();
        }
    }

    public void InputChanged(Guid sessionId, string text) => Find(sessionId)?.Typing?.Input(!string.IsNullOrEmpty(text), _clock());

    /// <summary>Sends a message in a conversation. True when the input should be cleared (as <see cref="NearbyChat.Send"/>).</summary>
    public bool Send(Guid sessionId, string text)
    {
        var c = Find(sessionId);
        if (c is null)
            return false;
        c.Typing?.Stop();
        string t = (text ?? "").Trim();
        if (t.Length == 0)
            return true;
        if (c.IsGroup && c.Group != GroupChatState.Joined)
        {
            c.Status = ("status.group.not_joined", null);   // nothing goes to a group before the grid says the session is joined
            Changed?.Invoke();
            return false;
        }
        var r = _submit(c.IsGroup ? new GroupMessageIntent(c.SessionId, t) : new InstantMessageIntent(c.Other, t));
        c.Status = ChatText.StatusOf(r);
        if (r.Sent)
        {
            // the grid does not echo an instant message back, so the sender's own line is added here
            bool emote = ChatTiming.IsEmote(t, out string rest);
            AddLine(c, new ChatLine(_clock(), ChatLineStyle.Own, _self, _names.Get(_self) ?? "", rest, ChatKind.Say, emote));
        }
        Changed?.Invoke();
        return r.Sent || r.Status == ActionStatus.OutcomeUnknown;
    }

    public void Tick()
    {
        var now = _clock();
        bool changed = false;
        foreach (var c in _open)
        {
            c.Typing?.Tick(now);
            if (c.OtherTyping && now - c.OtherTypingAt > ChatTiming.OtherTypingTimeout)
            {
                c.OtherTyping = false;
                changed = true;
            }
        }
        if (changed)
            Changed?.Invoke();
    }
}
