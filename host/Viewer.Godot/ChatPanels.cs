// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Chat;

namespace Viewer.Godot;

/// <summary>
/// Nearby chat and the conversations as floaters, built by hand in code. They only draw the models in
/// <see cref="CommsHub"/> (tested without the engine) and hand keys and clicks back to them. Layout: history on top, who is
/// typing under it, then the input row and a status line; every text from the string table, every control named for
/// assistive technology. Behaviour follows the official viewer's nearby chat and conversations (keys, ranges, typing, unread
/// counts); no layout file of any viewer is used.
/// </summary>
public static class ChatFeature
{
    public const string NearbyId = "nearby_chat";
    public const string ConversationsId = "conversations";

    /// <summary>Adds the two floaters, the Communicate menu, two toolbar buttons and their commands to a shell that is
    /// already drawn (the menus and toolbar draw themselves again). Returns the panels, to be bound to a session's hub.</summary>
    public static (NearbyChatPanel Nearby, ConversationsPanel Conversations) Install(ShellRoot shell, SettingsService settings)
    {
        var s = shell.Shell;
        var nearby = new NearbyChatPanel(s.Strings, settings, s.Focus);
        var conversations = new ConversationsPanel(s.Strings, settings, s.Floaters, ConversationsId);
        nearby.AgentClicked += agent =>
        {
            if (conversations.OpenWith(agent))
                s.Floaters.Open(ConversationsId);
        };
        shell.AddFloater(new FloaterSpec(NearbyId, "chat.title", new UiSize(460, 300), new UiSize(320, 200)), nearby);
        shell.AddFloater(new FloaterSpec(ConversationsId, "im.title", new UiSize(460, 340), new UiSize(320, 220)), conversations);
        s.Commands.Register("floater.chat", () => s.Floaters.Toggle(NearbyId));
        s.Commands.Register("floater.conversations", () => s.Floaters.Toggle(ConversationsId));
        s.Menus.AddMenu("communicate", "menu.communicate");
        s.Menus.AddEntry("communicate", new MenuEntry("communicate.chat", "menu.communicate.chat", "floater.chat"));
        s.Menus.AddEntry("communicate", new MenuEntry("communicate.conversations", "menu.communicate.conversations", "floater.conversations"));
        s.Toolbar.Add(new ToolbarButton("toolbar_chat", "toolbar.chat", "floater.chat", UiIcons.Chat, NearbyId));
        s.Toolbar.Add(new ToolbarButton("toolbar_conversations", "toolbar.conversations", "floater.conversations", UiIcons.Conversations, ConversationsId));
        return (nearby, conversations);
    }

    internal static RichTextLabel History(string accessibleName, SettingsService settings)
    {
        var h = new RichTextLabel
        {
            Name = "history", BbcodeEnabled = true, ScrollFollowing = true, SelectionEnabled = true, ContextMenuEnabled = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.Click,
        };
        h.AccessibilityName = accessibleName;
        void ApplySize()
        {
            int size = settings.Get(Settings.ChatFontSize);
            foreach (var font in new[] { "normal_font_size", "bold_font_size", "italics_font_size" })
                h.AddThemeFontSizeOverride(font, size);
        }
        ApplySize();
        // the text size follows the setting while the window is up (Preferences changes it as a preview, and Cancel puts it back)
        Action<string> changed = key => { if (key == Settings.ChatFontSize.Key) ApplySize(); };
        settings.Changed += changed;
        h.TreeExiting += () => settings.Changed -= changed;
        return h;
    }

    internal static Label Note(string name, string accessibleName, ThemeSpec theme) =>
        new() { Name = name, AccessibilityName = accessibleName, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(200, 0),
            ThemeTypeVariation = UiTheme.MutedLabel };
}

/// <summary>The nearby chat floater's content.</summary>
public partial class NearbyChatPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private readonly SettingsService _settings;
    private static ThemeSpec _theme => UiTheme.Spec;
    private CommsHub? _hub;
    private bool _dirty = true;

    public RichTextLabel HistoryView { get; }
    public Label TypingLabel { get; }
    public LineEdit Input { get; }
    public OptionButton Kind { get; }
    public Button SendButton { get; }
    public Label StatusLabel { get; }

    /// <summary>A resident's name in the history was clicked.</summary>
    public event Action<Guid>? AgentClicked;

    /// <summary>Clicks a link target as the mouse does (the development automation's way in).</summary>
    internal void ClickTarget(string meta) => HistoryView.EmitSignal(RichTextLabel.SignalName.MetaClicked, meta);

    /// <summary>An object's name in the history was clicked.</summary>
    public event Action<Guid>? ObjectClicked;

    public NearbyChatPanel(StringTable strings, SettingsService settings, InputFocus focus)
    {
        _strings = strings;
        _settings = settings;
        Name = "nearby_chat_panel";
        HistoryView = ChatFeature.History(strings.Get("chat.history"), settings);
        HistoryView.MetaClicked += meta =>
        {
            if (ChatText.AgentOf(meta.AsString()) is { } agent)
                AgentClicked?.Invoke(agent);
            else if (ChatText.ObjectOf(meta.AsString()) is { } obj)
                ObjectClicked?.Invoke(obj);
            else if (Viewer.Shell.Links.LinkText.LinkOf(meta.AsString()) is { } link)
                StatusLabel.Text = HostLinks.Click(link) ?? "";
        };
        AddChild(HistoryView);
        TypingLabel = ChatFeature.Note("typing", strings.Get("chat.typing.label"), _theme);
        AddChild(TypingLabel);

        var row = new HBoxContainer { Name = "input_row" };
        Input = new LineEdit { Name = "input", PlaceholderText = strings.Get("chat.input.hint"), SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = 1023 };
        Input.AccessibilityName = strings.Get("chat.input");
        Input.TextChanged += t => _hub?.Nearby.InputChanged(t);
        Input.FocusExited += () => _hub?.Nearby.InputLostFocus();
        // the input's own key handling comes after this signal: Enter is taken here, with Shift (whisper) or Ctrl (shout) choosing the range
        Input.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Echo: false } k && k.Keycode is Key.Enter or Key.KpEnter)
            {
                Submit(k.CtrlPressed ? ChatKind.Shout : k.ShiftPressed ? ChatKind.Whisper : null);
                Input.AcceptEvent();
            }
        };
        row.AddChild(Input);
        Kind = new OptionButton { Name = "kind" };
        Kind.AccessibilityName = strings.Get("chat.kind");
        Kind.AddItem(strings.Get("chat.kind.whisper"), (int)ChatKind.Whisper);
        Kind.AddItem(strings.Get("chat.kind.say"), (int)ChatKind.Say);
        Kind.AddItem(strings.Get("chat.kind.shout"), (int)ChatKind.Shout);
        Kind.Select(Kind.GetItemIndex((int)ChatKind.Say));
        Kind.ItemSelected += i => { if (_hub is not null) _hub.Nearby.Chosen = (ChatKind)Kind.GetItemId((int)i); };
        row.AddChild(Kind);
        SendButton = new Button { Name = "send", Text = strings.Get("chat.send") };
        SendButton.AccessibilityName = strings.Get("chat.send");
        SendButton.Pressed += () => Submit(null);
        row.AddChild(SendButton);
        AddChild(row);
        StatusLabel = ChatFeature.Note("status", strings.Get("chat.status"), _theme);
        StatusLabel.ThemeTypeVariation = UiTheme.ErrorLabel;
        AddChild(StatusLabel);
        _settings.Changed += _ => _dirty = true;
        Bind(null);
    }

    /// <summary>Shows a session's nearby chat (null: no session; the input is off).</summary>
    public void Bind(CommsHub? hub)
    {
        if (_hub is not null)
            _hub.Nearby.Changed -= MarkDirty;
        _hub = hub;
        if (hub is not null)
        {
            hub.Nearby.Changed += MarkDirty;
            hub.Nearby.Chosen = (ChatKind)Kind.GetSelectedId();
        }
        Input.Editable = hub is not null;
        SendButton.Disabled = hub is null;
        _dirty = true;
    }

    private void MarkDirty() => _dirty = true;

    /// <summary>Sends the input's line with a range a key chose (or the chosen one); clears the input unless it was refused.</summary>
    public void Submit(ChatKind? keyKind)
    {
        if (_hub is null)
            return;
        if (_hub.Nearby.Send(Input.Text, keyKind))
            Input.Clear();
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        if (!_dirty)
            return;
        _dirty = false;
        var nearby = _hub?.Nearby;
        bool showTime = _settings.Get(Settings.ChatTimestamps);
        HistoryView.Text = nearby is null ? "" : string.Join("\n", nearby.Lines.Select(l => ChatText.Format(l, _theme, _strings, showTime, links: HostLinks.Router)));
        var typing = nearby?.Typing ?? [];
        TypingLabel.Text = typing.Count switch
        {
            0 => "",
            1 => _strings.Get("chat.typing.one", typing[0].Name),
            2 => _strings.Get("chat.typing.two", typing[0].Name, typing[1].Name),
            _ => _strings.Get("chat.typing.many", typing[0].Name, typing.Count - 1),
        };
        StatusLabel.Text = nearby?.Status is { } st ? ChatText.Status(st, _strings) : "";
    }
}

/// <summary>The conversations floater's content: a tab per conversation, the shown one's history, the other's typing, the input.</summary>
public partial class ConversationsPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private readonly SettingsService _settings;
    private readonly FloaterManager _floaters;
    private readonly string _floaterId;
    private static ThemeSpec _theme => UiTheme.Spec;
    private readonly Dictionary<Guid, string> _drafts = new();
    private CommsHub? _hub;
    private Guid? _shown;
    private bool _dirty = true, _syncingTabs;

    public TabBar Tabs { get; }
    public Label EmptyNote { get; }
    public RichTextLabel HistoryView { get; }
    public Label TypingLabel { get; }
    public LineEdit Input { get; }
    public Button SendButton { get; }
    public Button CloseButton { get; }
    public Button OfferButton { get; }
    public Label StatusLabel { get; }

    // the answer to the last friendship offer from this panel (shown when the conversation has no status of its own)
    private string _offerNote = "";
    private readonly PanelContainer _tabsTrack;

    public ConversationsPanel(StringTable strings, SettingsService settings, FloaterManager floaters, string floaterId)
    {
        _strings = strings;
        _settings = settings;
        _floaters = floaters;
        _floaterId = floaterId;
        Name = "conversations_panel";
        // switching conversations is the segmented control on its track, as Inventory's tabs are; the track spans the window so many
        // conversations clip instead of widening it
        Tabs = new TabBar { Name = "tabs", ClipTabs = true, ThemeTypeVariation = ReverieStyle.TabBar };
        Tabs.AccessibilityName = strings.Get("im.tabs");
        Tabs.TabChanged += i => { if (!_syncingTabs && _hub is not null && i >= 0 && i < _hub.Ims.Open.Count) _hub.Ims.Activate(_hub.Ims.Open[(int)i].SessionId); };
        _tabsTrack = new PanelContainer { Name = "tabs_track", ThemeTypeVariation = ReverieStyle.Track, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _tabsTrack.AddChild(Tabs);
        AddChild(_tabsTrack);
        EmptyNote = ChatFeature.Note("empty", strings.Get("im.none"), _theme);
        EmptyNote.Text = strings.Get("im.none");
        AddChild(EmptyNote);
        HistoryView = ChatFeature.History(strings.Get("im.history", ""), settings);
        // a link in a message (an IM, group chat or an object's message) does what it does in nearby chat
        HistoryView.MetaClicked += meta =>
        {
            if (Viewer.Shell.Links.LinkText.LinkOf(meta.AsString()) is { } link)
                StatusLabel!.Text = HostLinks.Click(link) ?? "";
        };
        AddChild(HistoryView);
        TypingLabel = ChatFeature.Note("typing", strings.Get("chat.typing.label"), _theme);
        AddChild(TypingLabel);
        var row = new HBoxContainer { Name = "input_row" };
        Input = new LineEdit { Name = "input", SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = 1023 };
        Input.AccessibilityName = strings.Get("im.input", "");
        Input.TextChanged += t => { if (_hub is not null && _shown is { } id) _hub.Ims.InputChanged(id, t); };
        Input.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Echo: false } k && k.Keycode is Key.Enter or Key.KpEnter)
            {
                Submit();
                Input.AcceptEvent();
            }
        };
        row.AddChild(Input);
        SendButton = new Button { Name = "send", Text = strings.Get("im.send") };
        SendButton.AccessibilityName = strings.Get("im.send");
        SendButton.Pressed += Submit;
        row.AddChild(SendButton);
        CloseButton = new RvIconButton(LucideIcons.Close, strings.Get("im.close")) { Name = "close_conversation" };
        CloseButton.Pressed += () => { if (_hub is not null && _shown is { } id) { _drafts.Remove(id); _hub.Ims.Close(id); } };
        row.AddChild(CloseButton);
        OfferButton = new Button { Name = "offer_friendship", Text = strings.Get("friends.offer") };
        OfferButton.AccessibilityName = strings.Get("friends.offer");
        OfferButton.Pressed += OfferFriendship;
        row.AddChild(OfferButton);
        AddChild(row);
        StatusLabel = ChatFeature.Note("status", strings.Get("im.status"), _theme);
        StatusLabel.ThemeTypeVariation = UiTheme.ErrorLabel;
        AddChild(StatusLabel);
        _settings.Changed += _ => _dirty = true;
        _floaters.Changed += () => { if (_hub is not null) _hub.Ims.Visible = _floaters.IsOpen(_floaterId); };
        Bind(null);
    }

    public void Bind(CommsHub? hub)
    {
        if (_hub is not null)
        {
            _hub.Ims.Changed -= MarkDirty;
            _hub.Ims.Arrived -= OnArrived;
        }
        _hub = hub;
        _drafts.Clear();
        _shown = null;
        if (hub is not null)
        {
            hub.Ims.Changed += MarkDirty;
            hub.Ims.Arrived += OnArrived;
            hub.Ims.Visible = _floaters.IsOpen(_floaterId);
        }
        _dirty = true;
    }

    /// <summary>An incoming message opens the panel (there is no notification area yet), without taking the keyboard.</summary>
    private void OnArrived(Conversation c)
    {
        if (!_floaters.IsOpen(_floaterId))
            _floaters.Open(_floaterId);
    }

    /// <summary>Opens the conversation with an agent (from a name clicked elsewhere). False when there is no session or no such conversation can exist.</summary>
    public bool OpenWith(Guid agent) => _hub?.Ims.OpenWith(agent) is not null;

    /// <summary>Opens a group's chat (joining it) and shows it. False when there is no session.</summary>
    public bool OpenGroup(Guid group, string name) => _hub?.Ims.OpenGroup(group, name) is not null;

    /// <summary>Starts (or opens, for one friend) a conversation with several agents at once.</summary>
    public bool OpenConference(IReadOnlyList<Guid> agents) => _hub?.Ims.OpenConference(agents) is not null;

    /// <summary>Offers friendship to the other person of the shown conversation, with the default message.</summary>
    private void OfferFriendship()
    {
        if (_hub is null || _shown is not { } id || _hub.Ims.Find(id) is not { IsGroup: false } c)
            return;
        bool sent = _hub.Friends.Offer(c.Other, _strings.Get("friends.offer.default"));
        _offerNote = sent ? _strings.Get("friends.offer.sent")
            : _hub.Friends.Status is { } st ? ChatText.Status(st, _strings) : "";
        _dirty = true;
    }

    private void MarkDirty() => _dirty = true;

    private void Submit()
    {
        if (_hub is null || _shown is not { } id)
            return;
        if (_hub.Ims.Send(id, Input.Text))
        {
            Input.Clear();
            _drafts.Remove(id);
        }
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        if (!_dirty)
            return;
        _dirty = false;
        var ims = _hub?.Ims;
        var open = ims?.Open ?? [];
        _syncingTabs = true;
        while (Tabs.TabCount > open.Count)
            Tabs.RemoveTab(Tabs.TabCount - 1);
        for (int i = 0; i < open.Count; i++)
        {
            string title = open[i].Unread > 0 ? _strings.Get("im.tab.unread", open[i].Name, open[i].Unread) : _strings.Get("im.tab", open[i].Name);
            if (i >= Tabs.TabCount)
                Tabs.AddTab(title);
            else
                Tabs.SetTabTitle(i, title);
        }
        var active = ims?.Active is { } a ? ims.Find(a) : null;
        if (active is not null)
            Tabs.CurrentTab = open.ToList().IndexOf(active);
        _syncingTabs = false;

        // the input belongs to the shown conversation: keep what was typed in each one
        if (_shown != active?.SessionId)
        {
            if (_shown is { } old)
                _drafts[old] = Input.Text;
            _shown = active?.SessionId;
            Input.Text = _shown is { } now && _drafts.TryGetValue(now, out var draft) ? draft : "";
        }
        bool any = active is not null;
        EmptyNote.Visible = !any;
        Tabs.Visible = any;
        _tabsTrack.Visible = any;
        Input.Editable = any;
        SendButton.Disabled = !any;
        CloseButton.Disabled = !any;
        // offering friendship fits a one-to-one conversation with someone who is not yet a friend
        OfferButton.Visible = active is { IsGroup: false } && !(_hub?.Friends.Friends.Any(f => f.Id == active.Other) ?? true);
        bool showTime = _settings.Get(Settings.ChatTimestamps);
        HistoryView.Text = active is null ? "" : string.Join("\n", active.Lines.Select(l => ChatText.Format(l, _theme, _strings, showTime, links: HostLinks.Router)));
        if (active is not null)
        {
            HistoryView.AccessibilityName = _strings.Get("im.history", active.Name);
            Input.AccessibilityName = _strings.Get("im.input", active.Name);
        }
        TypingLabel.Text = active is { OtherTyping: true } ? _strings.Get("im.typing", active.Name)
            : active is { Group: GroupChatState.Joining } ? _strings.Get("im.group.joining") : "";
        StatusLabel.Text = active?.Status is { } st ? ChatText.Status(st, _strings) : active is { IsGroup: false } ? _offerNote : "";
        if (ims is not null && active is not null && ims.Visible && active.Unread > 0)
            ims.Activate(active.SessionId);   // the shown conversation, on screen, is read
    }
}
