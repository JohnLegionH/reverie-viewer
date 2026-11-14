// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using System.Globalization;
using Viewer.Core.Actions;
using Viewer.Core.Session;
using Viewer.Core.Social;

namespace Viewer.Shell.Chat;

/// <summary>
/// Chat and message lines as rich text for the engine's rich-text label. Everything that came from the grid (names and
/// text) is escaped, so a message cannot add formatting or a link of its own; the only link is the speaker's name for a
/// resident ("agent:" and the id), which opens a conversation.
/// </summary>
public static class ChatText
{
    public const string AgentLinkPrefix = "agent:";

    /// <summary>Text with every opening bracket made literal (the rich-text label's own escape).</summary>
    public static string Escape(string text) => (text ?? "").Replace("[", "[lb]", StringComparison.Ordinal);

    /// <summary>One line as rich text.</summary>
    /// <param name="links">When given, links in the text are made clickable through the one link renderer; otherwise the text is plain.</param>
    public static string Format(ChatLine line, ThemeSpec theme, StringTable strings, bool showTime, float opacity = 1f, Viewer.Shell.Links.LinkRouter? links = null)
    {
string Hex(UiColour c) => opacity >= 1f ? c.Hex : c.Hex + ((int)Math.Round(Math.Clamp(opacity, 0f, 1f) * 255f)).ToString("x2", CultureInfo.InvariantCulture);
        var colour = line.Mark == ChatMark.ScriptError ? theme.Error : line.Style switch
        {
            ChatLineStyle.Own => theme.ChatOwn,
            ChatLineStyle.Object => theme.ChatObject,
            ChatLineStyle.System => theme.ChatSystem,
            _ => theme.ChatAgent,
        };
        string time = showTime
            ? $"[color={Hex(theme.MutedText)}]{Escape(strings.Get("chat.time", line.At.ToString("HH:mm", CultureInfo.InvariantCulture)))}[/color]"
            : "";
        string saved = !line.Saved ? ""
            : Escape(line.SavedAt is { } at
                ? strings.Get("chat.saved.at", at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                : strings.Get("chat.saved"));
        string body;
        if (line.Style == ChatLineStyle.System)
            body = Fill(strings, "chat.line.system", Escape(line.Text));
        else
        {
            string shownName = line.Name.Length > 0 ? line.Name : line.Style == ChatLineStyle.Own ? strings.Get("chat.you") : NameCache.Placeholder(line.SpeakerId);
            string name = Escape(shownName);
            if (line.Style == ChatLineStyle.Agent && line.SpeakerId != Guid.Empty)
                name = $"[url={AgentLinkPrefix}{line.SpeakerId:D}]{name}[/url]";
            else if (line.ObjectId != Guid.Empty)
            {
                // an object's name opens its details (the official viewer's remote object inspector); the mark says who hears the line
                name = $"[url={ObjectLinkPrefix}{line.ObjectId:D}]{name}[/url]";
                string? mark = line.Mark switch
                {
                    ChatMark.OwnerOnly => "chat.mark.owner_only",
                    ChatMark.Direct => "chat.mark.direct",
                    ChatMark.ScriptError => "chat.mark.script_error",
                    ChatMark.ObjectMessage => "chat.mark.object_message",
                    _ => null,
                };
                if (mark is not null)
                    name += $" [color={Hex(theme.MutedText)}]{Escape(strings.Get(mark))}[/color]";
            }
            string key = line.Emote ? "chat.line.emote"
                : line.Range == ChatKind.Whisper ? "chat.line.whisper"
                : line.Range == ChatKind.Shout ? "chat.line.shout"
                : "chat.line";
            body = Fill(strings, key, name, links is null ? Escape(line.Text) : Viewer.Shell.Links.LinkText.Render(line.Text, Escape, links));
        }
        return $"{time}[color={Hex(colour)}]{saved}{body}[/color]";
    }

    /// <summary>A string-table pattern with its own text escaped (a translation, or the pseudo-locale's brackets, must not
    /// become formatting), then filled with parts that are already rich text.</summary>
    private static string Fill(StringTable strings, string key, params object[] parts) =>
        string.Format(CultureInfo.InvariantCulture, Escape(strings.Get(key)), parts);

    public const string ObjectLinkPrefix = "object:";

    /// <summary>The object a link names, or null when it is not an object link.</summary>
    public static Guid? ObjectOf(string link) =>
        link is not null && link.StartsWith(ObjectLinkPrefix, StringComparison.Ordinal) && Guid.TryParse(link.AsSpan(ObjectLinkPrefix.Length), out var id) && id != Guid.Empty
            ? id : null;

    /// <summary>The agent a link names, or null when it is not an agent link.</summary>
    public static Guid? AgentOf(string link) =>
        link is not null && link.StartsWith(AgentLinkPrefix, StringComparison.Ordinal) && Guid.TryParse(link.AsSpan(AgentLinkPrefix.Length), out var id) && id != Guid.Empty
            ? id : null;

    /// <summary>A status (refusal) as text: the code's own string when the table has one, else a general one for its kind.</summary>
    public static string Status((string Key, string? Arg) status, StringTable strings)
    {
        string key = strings.Has(status.Key) ? status.Key : "status.invalid";
        return status.Arg is null ? strings.Get(key) : strings.Get(key, status.Arg);
    }

    /// <summary>The status key for a pipeline result that did not send (null when it was sent).</summary>
    public static (string Key, string? Arg)? StatusOf(ActionResult r) => r.Status switch
    {
        ActionStatus.Sent => null,
        ActionStatus.Invalid when r.Issues is { Count: > 0 } issues => ("status." + issues[0].Code, issues[0].Detail.Length > 0 ? issues[0].Detail : null),
        ActionStatus.Invalid => ("status.invalid", null),
        ActionStatus.Vetoed => ("status.vetoed", null),
        ActionStatus.Unavailable => ("status.unavailable", null),
        ActionStatus.OutcomeUnknown => ("status.unknown", null),
        _ => ("status.failed", null),
    };
}

/// <summary>
/// The session's communication as the interface sees it: the session raises events on network threads, the hub queues them,
/// and the engine's frame drains the queue into nearby chat and the conversations (<see cref="Pump"/>), so the models are
/// only ever touched on the main thread. One hub per session.
/// </summary>
public sealed class CommsHub
{
    private readonly ConcurrentQueue<TransportEvent> _queue = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<int>? _requestNames;
    private readonly Func<Viewer.Shell.Social.FriendNotice, string?>? _noticeText;
    private readonly List<Viewer.Shell.Social.FriendNotice> _recentNotices = new();
    private DateTimeOffset _lastNameRequest = DateTimeOffset.MinValue;

    /// <summary>Whether a friend's online or offline notice is also written into nearby chat. On unless the interface shows the notice itself.</summary>
    public bool FriendNoticesInNearbyChat { get; set; } = true;

    public CommsHub(Guid self, NameCache names, Func<ActionIntent, ActionResult> submit, SettingsService settings, Func<DateTimeOffset> clock,
        Func<int>? requestNames = null, Func<Viewer.Shell.Social.FriendNotice, string?>? noticeText = null, Action<Guid>? requestTitles = null)
    {
        Friends = new Viewer.Shell.Social.FriendsModel(self, names, submit, () => settings.Get(Settings.FriendOnlineNotices));
        Groups = new Viewer.Shell.Social.GroupsModel(submit, clock, requestTitles);
        _noticeText = noticeText;
        _clock = clock;
        _requestNames = requestNames;
        Nearby = new NearbyChat(self, submit, () => settings.Get(Settings.ChatHistoryLines), () => settings.Get(Settings.ChatSendTyping), clock);
        Ims = new Conversations(self, names, submit, () => settings.Get(Settings.ChatHistoryLines), () => settings.Get(Settings.ChatSendTyping), clock, Nearby.AddSystem);
        Friends.Notice += OnFriendNotice;
    }

    public NearbyChat Nearby { get; }
    public Conversations Ims { get; }
    public Viewer.Shell.Social.FriendsModel Friends { get; }
    public Viewer.Shell.Social.GroupsModel Groups { get; }

    /// <summary>The friend notices raised so far (the last hundred), as kinds and ids: what the development build's automation reports.</summary>
    public IReadOnlyList<Viewer.Shell.Social.FriendNotice> RecentFriendNotices => _recentNotices.ToArray();

    /// <summary>A friend notice becomes a system line in nearby chat, and in the conversation with that friend when one is open.</summary>
    private void OnFriendNotice(Viewer.Shell.Social.FriendNotice notice)
    {
        _recentNotices.Add(notice);
        if (_recentNotices.Count > 100)
            _recentNotices.RemoveAt(0);
        string? text = _noticeText?.Invoke(notice);
        if (string.IsNullOrEmpty(text))
            return;
        if (FriendNoticesInNearbyChat)
            Nearby.AddSystem(text);
        if (notice.Kind is Viewer.Shell.Social.FriendNoticeKind.Online or Viewer.Shell.Social.FriendNoticeKind.Offline)
            Ims.AddSystemLine(notice.Friend, text);
    }

    /// <summary>Main thread: sees each queued event before the models (restraints filter chat and IMs); it answers the event to
    /// hand on, changed or not, or null to drop it. Null: everything is handed on.</summary>
    public Func<TransportEvent, TransportEvent?>? Incoming { get; set; }

    /// <summary>Any thread: queues a communication event (the session's <c>Communication</c> handler).</summary>
    public void Enqueue(TransportEvent e) => _queue.Enqueue(e);

    /// <summary>Main thread, every frame: hands queued events to the models, runs time-outs, and asks for unknown names at most once a second.
    /// Returns how many events were handled.</summary>
    public int Pump()
    {
        int handled = 0;
        while (_queue.TryDequeue(out var queued))
        {
            handled++;
            if ((Incoming is { } filter ? filter(queued) : queued) is not { } e)
                continue;
            switch (e)
            {
                case ChatReceived c:
                    Nearby.Receive(c);
                    break;
                case InstantMessageReceived m:
                    Ims.Receive(m);
                    break;
                case ObjectInstantMessage om:
                    Nearby.ReceiveObjectMessage(om);   // an object's message opens no conversation
                    break;
                case AgentNamesReceived n:
                    foreach (var (id, name) in n.Names)
                        Ims.Rename(id, name);
                    Friends.Refresh();
                    break;
                case FriendsListed or FriendPresenceChanged or FriendRightsChanged or FriendshipOffered or FriendshipAnswered or FriendshipEnded:
                    Friends.Receive(e);
                    break;
                case GroupsListed or ActiveGroupChanged or GroupTitlesListed or GroupNoticeReceived:
                    Groups.Receive(e);
                    break;
                case GroupChatJoined joined:
                    Ims.ReceiveJoined(joined);
                    break;
            }
        }
        Nearby.Tick();
        Ims.Tick();
        var now = _clock();
        if (_requestNames is not null && now - _lastNameRequest >= TimeSpan.FromSeconds(1))
        {
            _lastNameRequest = now;
            _requestNames();
        }
        return handled;
    }
}
