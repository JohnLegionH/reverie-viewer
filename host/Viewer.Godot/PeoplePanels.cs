// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Ui;
using Viewer.Shell.Chat;
using Viewer.Shell.Social;

namespace Viewer.Godot;

/// <summary>
/// Friends, groups and group notices as one floater with three tabs, built by hand in code. It only draws the models in
/// <see cref="CommsHub"/> (tested without the engine) and hands clicks back to them; every text comes from the string table and
/// every control is named for assistive technology. Behaviour follows the official viewer's people panel (online marks,
/// rights, offers, groups, notices); no layout file of any viewer is used. A removal and the first grant of the
/// edit-objects right ask for a confirmation in a bar inside the panel.
/// </summary>
public static class PeopleFeature
{
    public const string PeopleId = "people";

    /// <summary>Adds the floater, a menu entry, a toolbar button and their command to a shell that already has the chat feature.</summary>
    public static PeoplePanel Install(ShellRoot shell, SettingsService settings, ConversationsPanel conversations)
    {
        var s = shell.Shell;
        var panel = new PeoplePanel(s.Strings);
        panel.MessageRequested += agent =>
        {
            if (conversations.OpenWith(agent))
                s.Floaters.Open(ChatFeature.ConversationsId);
        };
        panel.ConferenceRequested += agents =>
        {
            if (conversations.OpenConference(agents))
                s.Floaters.Open(ChatFeature.ConversationsId);
        };
        panel.GroupChatRequested += (group, name) =>
        {
            if (conversations.OpenGroup(group, name))
                s.Floaters.Open(ChatFeature.ConversationsId);
        };
        shell.AddFloater(new FloaterSpec(PeopleId, "people.title", new UiSize(500, 440), new UiSize(360, 320)), panel);
        s.Commands.Register("floater.people", () => s.Floaters.Toggle(PeopleId));
        s.Menus.AddEntry("communicate", new MenuEntry("communicate.people", "menu.communicate.people", "floater.people"));
        s.Toolbar.Add(new ToolbarButton("toolbar_people", "toolbar.people", "floater.people", UiIcons.People, PeopleId));
        return panel;
    }

    /// <summary>The words for a friend notice (nearby chat and open conversations show them as system lines).</summary>
    public static string? NoticeText(FriendNotice notice, StringTable strings, Viewer.Core.Social.NameCache names)
    {
        string name = names.Get(notice.Friend) ?? Viewer.Core.Social.NameCache.Placeholder(notice.Friend);
        string key = notice.Kind switch
        {
            FriendNoticeKind.Online => "friend.notice.online",
            FriendNoticeKind.Offline => "friend.notice.offline",
            FriendNoticeKind.Accepted => "friend.notice.accepted",
            FriendNoticeKind.Declined => "friend.notice.declined",
            FriendNoticeKind.Ended => "friend.notice.ended",
            FriendNoticeKind.ModifyGranted => "friend.notice.modify_granted",
            FriendNoticeKind.ModifyRevoked => "friend.notice.modify_revoked",
            _ => "",
        };
        return key.Length == 0 ? null : strings.Get(key, name);
    }
}

/// <summary>The people floater's content.</summary>
public partial class PeoplePanel : VBoxContainer
{
    private readonly StringTable _strings;
    private static ThemeSpec _theme => UiTheme.Spec;
    private CommsHub? _hub;
    private bool _dirty = true, _syncing;
    private Guid _selectedFriend, _selectedGroup, _selectedNotice;
    private readonly List<Guid> _pickedFriends = new();   // every friend picked in the list (Ctrl and Shift pick several); _selectedFriend is the one when exactly one is
    private (string Text, Action Confirm)? _confirm;

    public TabContainer Tabs { get; }

    /// <summary>The Nearby tab.</summary>
    public NearbyTab Nearby { get; }

    // friends
    public VBoxContainer OffersBox { get; }
    public Label OffersTitle { get; }
    public ItemList FriendList { get; }
    public Label FriendsNote { get; }
    public Button MessageButton { get; }
    public Button RemoveButton { get; }
    public Button BlockButton { get; }
    public BlockedTab Blocked { get; }
    private BlockedModel? _blockedModel;
    public CheckBox RightOnline { get; }
    public CheckBox RightMap { get; }
    public CheckBox RightModify { get; }
    public Label ReceivedLabel { get; }
    public HBoxContainer ConfirmBar { get; }
    public Label ConfirmLabel { get; }
    public Button ConfirmYes { get; }
    public Button ConfirmNo { get; }
    public Label FriendsStatus { get; }

    // groups
    public ItemList GroupList { get; }
    public Label ActiveLabel { get; }
    public Button GroupChatButton { get; }
    public Button ActivateButton { get; }
    public Button NoActiveButton { get; }
    public OptionButton TitleChoice { get; }
    public Label GroupsNote { get; }
    public Label GroupsStatus { get; }

    // notices
    public ItemList NoticeList { get; }
    public Label NoticeHeader { get; }
    public Label NoticeBody { get; }

    /// <summary>The links the notice's body holds, clickable (the body itself is shown as typed).</summary>
    public LinkStrip NoticeLinks { get; } = new("notice_links");
    public Label AttachmentLabel { get; }
    public Button AttachmentAccept { get; }
    public Button AttachmentDecline { get; }
    public Label NoticesStatus { get; }
    public Label NoticesNote { get; }

    /// <summary>"Message" was pressed for a friend.</summary>
    public event Action<Guid>? MessageRequested;

    /// <summary>"Start conference" was pressed with several friends picked: their ids.</summary>
    public event Action<IReadOnlyList<Guid>>? ConferenceRequested;

    /// <summary>"Open group chat" was pressed for a group: its id and name.</summary>
    public event Action<Guid, string>? GroupChatRequested;

    /// <summary>A question that must be answered in the shared dialog (giving a friend the right to edit objects). Without a listener the panel asks in its own bar.</summary>
    public event Action<Viewer.Shell.Notifications.Notification>? ConfirmationRequested;

    public PeoplePanel(StringTable strings)
    {
        _strings = strings;
        Name = "people_panel";
        Tabs = new TabContainer { Name = "tabs", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Tabs.AccessibilityName = strings.Get("people.tabs");
        AddChild(Tabs);

        // ---- nearby (the first tab, as in the official viewer) ----
        Nearby = new NearbyTab(strings);
        Nearby.MessageRequested += agent => MessageRequested?.Invoke(agent);
        Tabs.AddChild(Nearby);
        Tabs.SetTabTitle(0, strings.Get("people.tab.nearby"));

        // ---- friends ----
        var friends = new VBoxContainer { Name = "friends_tab" };
        OffersTitle = Note("offers_title", strings.Get("friends.offers.title"));
        OffersTitle.Text = strings.Get("friends.offers.title");
        friends.AddChild(OffersTitle);
        OffersBox = new VBoxContainer { Name = "offers" };
        friends.AddChild(OffersBox);
        FriendList = MakeList("friend_list", strings.Get("friends.list"), multi: true);
        FriendList.ItemSelected += _ => OnFriendsPicked();
        FriendList.MultiSelected += (_, _) => OnFriendsPicked();
        FriendList.ItemActivated += OnFriendActivated;   // a double-click: the default action, a message to that friend
        friends.AddChild(FriendList);
        FriendsNote = Note("friends_note", strings.Get("friends.none"));
        FriendsNote.Text = strings.Get("friends.none");
        friends.AddChild(FriendsNote);
        var friendButtons = new HBoxContainer { Name = "friend_buttons" };
        MessageButton = MakeButton("message", strings.Get("friends.message"), OnMessage);
        RemoveButton = MakeButton("remove", strings.Get("friends.remove"), OnRemove);
        BlockButton = MakeButton("block", strings.Get("blocked.block_resident"), OnBlockFriend);
        friendButtons.AddChild(MessageButton);
        friendButtons.AddChild(RemoveButton);
        friendButtons.AddChild(BlockButton);
        friends.AddChild(friendButtons);
        RightOnline = MakeCheck("right_online", strings.Get("friends.rights.online"));
        RightMap = MakeCheck("right_map", strings.Get("friends.rights.map"));
        RightModify = MakeCheck("right_modify", strings.Get("friends.rights.modify"));
        RightOnline.Toggled += _ => OnRightsChanged();
        RightMap.Toggled += _ => OnRightsChanged();
        RightModify.Toggled += _ => OnRightsChanged();
        friends.AddChild(RightOnline);
        friends.AddChild(RightMap);
        friends.AddChild(RightModify);
        ReceivedLabel = Note("received", strings.Get("friends.received.label"));
        friends.AddChild(ReceivedLabel);
        ConfirmBar = new HBoxContainer { Name = "confirm_bar", Visible = false };
        ConfirmLabel = Note("confirm_label", strings.Get("friends.confirm"));
        ConfirmLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ConfirmYes = MakeButton("confirm_yes", strings.Get("friends.confirm.yes"), () => { var c = _confirm; _confirm = null; _dirty = true; c?.Confirm(); });
        ConfirmNo = MakeButton("confirm_no", strings.Get("friends.confirm.no"), () => { _confirm = null; _dirty = true; });
        ConfirmBar.AddChild(ConfirmLabel);
        ConfirmBar.AddChild(ConfirmYes);
        ConfirmBar.AddChild(ConfirmNo);
        friends.AddChild(ConfirmBar);
        FriendsStatus = Note("friends_status", strings.Get("friends.status"));
        FriendsStatus.ThemeTypeVariation = UiTheme.ErrorLabel;
        friends.AddChild(FriendsStatus);
        Tabs.AddChild(friends);
        Tabs.SetTabTitle(1, strings.Get("people.tab.friends"));

        // ---- groups ----
        var groups = new VBoxContainer { Name = "groups_tab" };
        ActiveLabel = Note("active", strings.Get("groups.active.label"));
        groups.AddChild(ActiveLabel);
        GroupList = MakeList("group_list", strings.Get("groups.list"));
        GroupList.ItemSelected += i => { if (!_syncing) { _selectedGroup = GroupIdAt((int)i); _dirty = true; _hub?.Groups.LoadTitles(_selectedGroup); } };
        GroupList.ItemActivated += i => { if (GroupIdAt((int)i) != Guid.Empty) { _selectedGroup = GroupIdAt((int)i); OnGroupChat(); } };   // a double-click opens the group's chat
        groups.AddChild(GroupList);
        GroupsNote = Note("groups_note", strings.Get("groups.none"));
        GroupsNote.Text = strings.Get("groups.none");
        groups.AddChild(GroupsNote);
        var groupButtons = new HBoxContainer { Name = "group_buttons" };
        GroupChatButton = MakeButton("group_chat", strings.Get("groups.chat"), OnGroupChat);
        ActivateButton = MakeButton("activate", strings.Get("groups.activate"), () => { if (_hub is not null && _selectedGroup != Guid.Empty) _hub.Groups.Activate(_selectedGroup); _dirty = true; });
        NoActiveButton = MakeButton("no_active", strings.Get("groups.deactivate"), () => { _hub?.Groups.Activate(Guid.Empty); _dirty = true; });
        groupButtons.AddChild(GroupChatButton);
        groupButtons.AddChild(ActivateButton);
        groupButtons.AddChild(NoActiveButton);
        groups.AddChild(groupButtons);
        TitleChoice = new OptionButton { Name = "title_choice" };
        TitleChoice.AccessibilityName = strings.Get("groups.title");
        TitleChoice.ItemSelected += i =>
        {
            if (_syncing || _hub is null || _selectedGroup == Guid.Empty)
                return;
            var role = RoleAt((int)i);
            if (role != Guid.Empty)
                _hub.Groups.SetTitle(_selectedGroup, role);
            _dirty = true;
        };
        groups.AddChild(TitleChoice);
        GroupsStatus = Note("groups_status", strings.Get("groups.status"));
        GroupsStatus.ThemeTypeVariation = UiTheme.ErrorLabel;
        groups.AddChild(GroupsStatus);
        Tabs.AddChild(groups);
        Tabs.SetTabTitle(2, strings.Get("people.tab.groups"));

        // ---- notices ----
        var notices = new VBoxContainer { Name = "notices_tab" };
        NoticeList = MakeList("notice_list", strings.Get("notices.list"));
        NoticeList.ItemSelected += i =>
        {
            if (_syncing)
                return;
            _selectedNotice = NoticeIdAt((int)i);
            _hub?.Groups.MarkRead(_selectedNotice);
            _dirty = true;
        };
        notices.AddChild(NoticeList);
        NoticesNote = Note("notices_note", strings.Get("notices.none"));
        NoticesNote.Text = strings.Get("notices.none");
        notices.AddChild(NoticesNote);
        NoticeHeader = Note("notice_header", strings.Get("notices.header"));
        notices.AddChild(NoticeHeader);
        NoticeBody = Note("notice_body", strings.Get("notices.body"));
        NoticeBody.SizeFlagsVertical = SizeFlags.ExpandFill;
        notices.AddChild(NoticeBody);
        notices.AddChild(NoticeLinks);
        var attachment = new HBoxContainer { Name = "attachment" };
        AttachmentLabel = Note("attachment_label", strings.Get("notices.attachment.label"));
        AttachmentLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AttachmentAccept = MakeButton("attachment_accept", strings.Get("notices.accept"), () => Answer(true));
        AttachmentDecline = MakeButton("attachment_decline", strings.Get("notices.decline"), () => Answer(false));
        attachment.AddChild(AttachmentLabel);
        attachment.AddChild(AttachmentAccept);
        attachment.AddChild(AttachmentDecline);
        notices.AddChild(attachment);
        NoticesStatus = Note("notices_status", strings.Get("notices.status"));
        NoticesStatus.ThemeTypeVariation = UiTheme.ErrorLabel;
        notices.AddChild(NoticesStatus);
        Tabs.AddChild(notices);
        Tabs.SetTabTitle(3, strings.Get("people.tab.notices"));
        Blocked = new BlockedTab(strings);
        Tabs.AddChild(Blocked);
        Tabs.SetTabTitle(4, strings.Get("people.tab.blocked"));
        Bind(null);
    }

    private Label Note(string name, string accessibleName) => ChatFeature.Note(name, accessibleName, _theme);

    private static ItemList MakeList(string name, string accessibleName, bool multi = false)
    {
        var l = new ItemList { Name = name, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(200, 80), SelectMode = multi ? ItemList.SelectModeEnum.Multi : ItemList.SelectModeEnum.Single };
        l.AccessibilityName = accessibleName;
        return l;
    }

    private static Button MakeButton(string name, string text, Action pressed)
    {
        var b = new Button { Name = name, Text = text };
        b.AccessibilityName = text;
        b.Pressed += pressed;
        return b;
    }

    private static CheckBox MakeCheck(string name, string text)
    {
        var c = new CheckBox { Name = name, Text = text };
        c.AccessibilityName = text;
        return c;
    }

    // the rows currently listed, in the order they are shown (the lists hold only text)
    private List<FriendRow> _friendRows = new();
    private List<GroupRow> _groupRows = new();
    private List<GroupNoticeItem> _noticeRows = new();
    private List<GroupTitleRow> _titleRows = new();

    private Guid FriendIdAt(int i) => i >= 0 && i < _friendRows.Count ? _friendRows[i].Id : Guid.Empty;
    private Guid GroupIdAt(int i) => i >= 0 && i < _groupRows.Count ? _groupRows[i].Id : Guid.Empty;
    private Guid NoticeIdAt(int i) => i >= 0 && i < _noticeRows.Count ? _noticeRows[i].Id : Guid.Empty;
    private Guid RoleAt(int i) => i >= 0 && i < _titleRows.Count ? _titleRows[i].Role : Guid.Empty;

    /// <summary>Shows a session's block list in the Blocked tab (null: none).</summary>
    public void BindBlocked(BlockedModel? model)
    {
        _blockedModel = model;
        Blocked.Bind(model);
    }

    private void OnBlockFriend()
    {
        if (_blockedModel is not null && FriendRow(_selectedFriend) is { } row)
            _blockedModel.BlockResident(row.Id, row.Name);
        _dirty = true;
    }

    /// <summary>Shows a session's people (null: no session; everything is off).</summary>
    public void Bind(CommsHub? hub)
    {
        if (_hub is not null)
        {
            _hub.Friends.Changed -= MarkDirty;
            _hub.Groups.Changed -= MarkDirty;
        }
        _hub = hub;
        _selectedFriend = _selectedGroup = _selectedNotice = Guid.Empty;
        _pickedFriends.Clear();
        _confirm = null;
        if (hub is not null)
        {
            hub.Friends.Changed += MarkDirty;
            hub.Groups.Changed += MarkDirty;
        }
        _dirty = true;
    }

    private void MarkDirty() => _dirty = true;

    /// <summary>The friends picked in the list changed (a click, Ctrl-click or Shift-click): the single friend the other buttons act on is
    /// the one picked when exactly one is.</summary>
    private void OnFriendsPicked()
    {
        if (_syncing)
            return;
        _pickedFriends.Clear();
        foreach (int i in FriendList.GetSelectedItems())
            if (FriendIdAt(i) is var id && id != Guid.Empty)
                _pickedFriends.Add(id);
        _selectedFriend = _pickedFriends.Count == 1 ? _pickedFriends[0] : Guid.Empty;
        _dirty = true;
    }

    /// <summary>A double-click on a friend: with several picked, the conference; otherwise a message to that friend.</summary>
    private void OnFriendActivated(long index)
    {
        var id = FriendIdAt((int)index);
        if (id == Guid.Empty)
            return;
        if (_pickedFriends.Count > 1 && _pickedFriends.Contains(id))
            ConferenceRequested?.Invoke(_pickedFriends.ToList());
        else
            MessageRequested?.Invoke(id);
    }

    private void OnMessage()
    {
        if (_pickedFriends.Count > 1)
        {
            ConferenceRequested?.Invoke(_pickedFriends.ToList());
            return;
        }
        if (_selectedFriend != Guid.Empty)
            MessageRequested?.Invoke(_selectedFriend);
    }

    private void OnRemove()
    {
        if (_hub is null || FriendRow(_selectedFriend) is not { } f)
            return;
        _confirm = (_strings.Get("friends.remove.confirm", f.Name), () => { _hub.Friends.End(f.Id, confirmed: true); _selectedFriend = Guid.Empty; _dirty = true; });
        _dirty = true;
    }

    private FriendRow? FriendRow(Guid id) => _friendRows.Find(f => f.Id == id);

    private void OnRightsChanged()
    {
        if (_syncing || _hub is null || FriendRow(_selectedFriend) is not { } f)
            return;
        var rights = (RightOnline.ButtonPressed ? FriendRightsFlags.SeeOnline : 0)
            | (RightMap.ButtonPressed ? FriendRightsFlags.SeeOnMap : 0)
            | (RightModify.ButtonPressed ? FriendRightsFlags.ModifyObjects : 0);
        if (rights.HasFlag(FriendRightsFlags.ModifyObjects) && !f.Granted.HasFlag(FriendRightsFlags.ModifyObjects))
        {
            // giving the right to edit one's objects is asked about first; until the answer the box shows what the grid has
            Guid friendId = f.Id;
            void Grant() { _hub?.Friends.SetRights(friendId, rights, confirmedModify: true); _dirty = true; }
            if (ConfirmationRequested is { } ask)
            {
                RightModify.SetPressedNoSignal(false);   // the box shows what the grid has until the answer is Yes
                ask(Viewer.Shell.Notifications.Confirmations.GrantModifyRights(f.Name, Grant));
                _dirty = true;
                return;
            }
            _confirm = (_strings.Get("friends.rights.modify.confirm", f.Name), Grant);
            _dirty = true;
            return;
        }
        _hub.Friends.SetRights(f.Id, rights, confirmedModify: false);
        _dirty = true;
    }

    private void OnGroupChat()
    {
        if (_selectedGroup != Guid.Empty && _groupRows.Find(g => g.Id == _selectedGroup) is { } g)
            GroupChatRequested?.Invoke(g.Id, g.Name);
    }

    private void Answer(bool accept)
    {
        if (_hub is not null && _selectedNotice != Guid.Empty)
            _hub.Groups.AnswerAttachment(_selectedNotice, accept);
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        if (Tabs.CurrentTab == 0 && IsVisibleInTree())
            Nearby.Tick(delta);   // read once a second while it is on screen; nothing otherwise
        if (!_dirty)
            return;
        _dirty = false;
        _syncing = true;
        try { Refresh(); }
        finally { _syncing = false; }
    }

    private static string Rights(StringTable s, FriendRightsFlags r)
    {
        var parts = new List<string>();
        if (r.HasFlag(FriendRightsFlags.SeeOnline)) parts.Add(s.Get("friends.received.online"));
        if (r.HasFlag(FriendRightsFlags.SeeOnMap)) parts.Add(s.Get("friends.received.map"));
        if (r.HasFlag(FriendRightsFlags.ModifyObjects)) parts.Add(s.Get("friends.received.modify"));
        return parts.Count == 0 ? s.Get("friends.received.none") : string.Join(", ", parts);
    }

    private void Refresh()
    {
        var hub = _hub;
        // ---- friends ----
        _friendRows = hub?.Friends.Friends.ToList() ?? new();
        FriendList.Clear();
        foreach (var f in _friendRows)
            FriendList.AddItem(f.Online ? _strings.Get("friends.row.online", f.Name) : _strings.Get("friends.row", f.Name));
        _pickedFriends.RemoveAll(id => _friendRows.FindIndex(f => f.Id == id) < 0);   // a friend who left the list is no longer picked
        foreach (var id in _pickedFriends)
            FriendList.Select(_friendRows.FindIndex(f => f.Id == id), single: false);
        _selectedFriend = _pickedFriends.Count == 1 ? _pickedFriends[0] : Guid.Empty;
        FriendsNote.Visible = _friendRows.Count == 0;
        var offers = hub?.Friends.Offers ?? [];
        OffersTitle.Visible = offers.Count > 0;
        OffersBox.Visible = offers.Count > 0;
        foreach (var child in OffersBox.GetChildren())
        {
            OffersBox.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var o in offers)
        {
            var row = new HBoxContainer { Name = "offer_" + o.From.ToString("N") };
            var text = new Label { Text = _strings.Get("friends.offer.row", o.Name, o.Message), AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(160, 0) };
            text.AccessibilityName = text.Text;
            var from = o.From;
            row.AddChild(text);
            row.AddChild(MakeButton("accept", _strings.Get("friends.accept"), () => { hub?.Friends.Accept(from); _dirty = true; }));
            row.AddChild(MakeButton("decline", _strings.Get("friends.decline"), () => { hub?.Friends.Decline(from); _dirty = true; }));
            ((Button)row.GetChild(1)).AccessibilityName = _strings.Get("friends.accept.for", o.Name);
            ((Button)row.GetChild(2)).AccessibilityName = _strings.Get("friends.decline.for", o.Name);
            OffersBox.AddChild(row);
        }
        var current = FriendRow(_selectedFriend);
        MessageButton.Disabled = _pickedFriends.Count == 0;
        MessageButton.Text = _pickedFriends.Count > 1 ? _strings.Get("friends.conference") : _strings.Get("friends.message");
        RemoveButton.Disabled = current is null;
        BlockButton.Disabled = !ButtonAvailability.BlockResidentEnabled(current is null ? 0 : 1);
        foreach (var c in new CheckBox[] { RightOnline, RightMap, RightModify })
            c.Disabled = current is null;
        RightOnline.ButtonPressed = current?.Granted.HasFlag(FriendRightsFlags.SeeOnline) ?? false;
        RightMap.ButtonPressed = current?.Granted.HasFlag(FriendRightsFlags.SeeOnMap) ?? false;
        RightModify.ButtonPressed = current?.Granted.HasFlag(FriendRightsFlags.ModifyObjects) ?? false;
        RightMap.Disabled = current is null || !RightOnline.ButtonPressed;   // the map right goes only with the online right
        ReceivedLabel.Text = current is null ? "" : _strings.Get("friends.received", current.Name, Rights(_strings, current.Received));
        ConfirmBar.Visible = _confirm is not null;
        ConfirmLabel.Text = _confirm?.Text ?? "";
        FriendsStatus.Text = hub?.Friends.Status is { } fs ? ChatText.Status(fs, _strings) : "";

        // ---- groups ----
        _groupRows = hub?.Groups.Groups.ToList() ?? new();
        var active = hub?.Groups.Active ?? default;
        GroupList.Clear();
        foreach (var g in _groupRows)
            GroupList.AddItem(g.Id == active.Id ? _strings.Get("groups.row.active", g.Name) : _strings.Get("groups.row", g.Name));
        int gsel = _groupRows.FindIndex(g => g.Id == _selectedGroup);
        if (gsel >= 0)
            GroupList.Select(gsel);
        else
            _selectedGroup = Guid.Empty;
        GroupsNote.Visible = _groupRows.Count == 0;
        ActiveLabel.Text = active.Id == Guid.Empty ? _strings.Get("groups.active.none") : _strings.Get("groups.active", active.Name, active.Title);
        bool haveGroup = _selectedGroup != Guid.Empty;
        GroupChatButton.Disabled = !haveGroup;
        ActivateButton.Disabled = !haveGroup || _selectedGroup == active.Id;
        NoActiveButton.Disabled = hub is null || active.Id == Guid.Empty;
        _titleRows = haveGroup ? hub!.Groups.TitlesOf(_selectedGroup).ToList() : new();
        TitleChoice.Clear();
        foreach (var t in _titleRows)
            TitleChoice.AddItem(t.Title);
        int chosen = _titleRows.FindIndex(t => t.Selected);
        if (chosen >= 0)
            TitleChoice.Select(chosen);
        TitleChoice.Disabled = _titleRows.Count == 0;
        GroupsStatus.Text = hub?.Groups.Status is { } gs ? ChatText.Status(gs, _strings) : "";

        // ---- notices ----
        _noticeRows = hub?.Groups.Notices.ToList() ?? new();
        NoticeList.Clear();
        foreach (var n in _noticeRows)
            NoticeList.AddItem(_strings.Get(n.Unread ? "notices.row.unread" : "notices.row", GroupName(n.Group), n.Subject));
        int nsel = _noticeRows.FindIndex(n => n.Id == _selectedNotice);
        if (nsel >= 0)
            NoticeList.Select(nsel);
        else
            _selectedNotice = Guid.Empty;
        NoticesNote.Visible = _noticeRows.Count == 0;
        var notice = nsel >= 0 ? _noticeRows[nsel] : null;
        NoticeHeader.Text = notice is null ? "" : _strings.Get("notices.from", notice.FromName.Length > 0 ? notice.FromName : Viewer.Core.Social.NameCache.Placeholder(notice.Sender),
            notice.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        NoticeLinks.ShowLinksOf(notice?.Text);
        NoticeBody.Text = notice?.Text ?? "";   // a label: the text is shown as typed, never as markup
        bool offered = notice is { HasAttachment: true };
        AttachmentLabel.Visible = AttachmentAccept.Visible = AttachmentDecline.Visible = offered;
        if (notice is { HasAttachment: true } at)
        {
            AttachmentLabel.Text = at.Attachment switch
            {
                AttachmentState.Accepted => _strings.Get("notices.attachment.accepted", at.AttachmentName),
                AttachmentState.Declined => _strings.Get("notices.attachment.declined", at.AttachmentName),
                _ => _strings.Get("notices.attachment", at.AttachmentName),
            };
            AttachmentAccept.Disabled = AttachmentDecline.Disabled = at.Attachment != AttachmentState.Offered;
        }
        NoticesStatus.Text = hub?.Groups.Status is { } ns && notice is not null ? ChatText.Status(ns, _strings) : "";
        int unread = hub?.Groups.UnreadNotices ?? 0;
        Tabs.SetTabTitle(3, unread > 0 ? _strings.Get("people.tab.notices.unread", unread) : _strings.Get("people.tab.notices"));
    }

    private string GroupName(Guid group) => _groupRows.Find(g => g.Id == group)?.Name ?? Viewer.Core.Social.NameCache.Placeholder(group);
}
