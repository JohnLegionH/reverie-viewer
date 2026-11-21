// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Viewer.Shell.Notifications;
using Viewer.Shell.Social;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The one shared confirmation dialog and what goes with it: the official viewer's questions and buttons, Enter and Esc on a modal alert,
/// the editing keys a text field keeps, the menu a name in chat opens, and the friend notice that opens an instant message. Every member
/// builds its own objects, so the class runs in parallel (no static state, no real time: the model's clock is a test input).
/// </summary>
public class ConfirmationDialogTests
{
    private static readonly StringTable Strings = StringTable.English();
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Ann = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");

    private static string Body(Notification n) => Strings.Get(n.Body.Key!, n.Body.Arg ?? "");
    private static string Title(Notification n) => Strings.Get(n.Title!.Key!);
    private static string[] ButtonTexts(Notification n) => n.Buttons.Select(b => Strings.Get(b.Label.Key!)).ToArray();

    // ---------------- the questions: the official viewer's text, buttons and defaults ----------------

    [Fact]
    public void Return_asks_the_official_question_with_its_warning_on_its_own_line_and_OK_is_the_default()
    {
        int confirmed = 0;
        var n = Confirmations.Return(() => confirmed++);
        Assert.Equal(NotificationKind.Alert, n.Kind);
        Assert.True(n.Confirmation);
        Assert.Equal("Are you sure you want to return the selected objects to their owners? Transferable deeded objects will be returned to their previous owners.\n\n*WARNING* No-transfer deeded objects will be deleted!", Body(n));
        Assert.Equal(["OK", "Cancel"], ButtonTexts(n));
        Assert.Equal(0, n.DefaultButton);
        Assert.Equal(1, n.CancelButton);
        Assert.False(string.IsNullOrWhiteSpace(Title(n)));
        n.Responder!(new NotificationAnswer(1, ""));
        Assert.Equal(0, confirmed);
        n.Responder!(new NotificationAnswer(-1, ""));   // closed with no answer
        Assert.Equal(0, confirmed);
        n.Responder!(new NotificationAnswer(0, ""));
        Assert.Equal(1, confirmed);
    }

    [Theory]
    [InlineData("ConfirmObjectDeleteLock", "At least one of the items you have selected is locked.\n\nAre you sure you want to delete these items?")]
    [InlineData("ConfirmObjectDeleteNoCopy", "At least one of the items you have selected is not copyable.\n\nAre you sure you want to delete these items?")]
    [InlineData("ConfirmObjectDeleteNoOwn", "You do not own at least one of the items you have selected.\n\nAre you sure you want to delete these items?")]
    [InlineData("ConfirmObjectDeleteLockNoCopy", "At least one object is locked.\nAt least one object is not copyable.\n\nAre you sure you want to delete these items?")]
    [InlineData("ConfirmObjectDeleteLockNoOwn", "At least one object is locked.\nYou do not own at least one object.\n\nAre you sure you want to delete these items?")]
    [InlineData("ConfirmObjectDeleteNoCopyNoOwn", "At least one object is not copyable.\nYou do not own at least one object.\n\nAre you sure you want to delete these items?")]
    [InlineData("ConfirmObjectDeleteLockNoCopyNoOwn", "At least one object is locked.\nAt least one object is not copyable.\nYou do not own at least one object.\n\nAre you sure you want to delete these items?")]
    public void Delete_asks_the_official_question_for_each_reason_with_OK_and_Cancel(string variant, string text)
    {
        int confirmed = 0;
        var n = Confirmations.Delete(variant, () => confirmed++);
        Assert.Equal(text, Body(n));
        Assert.Equal(["OK", "Cancel"], ButtonTexts(n));
        Assert.Equal((0, 1), (n.DefaultButton, n.CancelButton));
        n.Responder!(new NotificationAnswer(0, ""));
        Assert.Equal(1, confirmed);
    }

    [Fact]
    public void Drop_asks_the_official_question_with_Yes_as_the_default_and_No_as_the_way_out()
    {
        int confirmed = 0;
        var n = Confirmations.Drop(() => confirmed++);
        Assert.Equal("You are about to drop your attachment.\nAre you sure you want to continue?", Body(n));
        Assert.Equal(["Yes", "No"], ButtonTexts(n));
        Assert.Equal((0, 1), (n.DefaultButton, n.CancelButton));
        n.Responder!(new NotificationAnswer(1, ""));
        Assert.Equal(0, confirmed);
        n.Responder!(new NotificationAnswer(0, ""));
        Assert.Equal(1, confirmed);
    }

    [Fact]
    public void Granting_modify_rights_names_the_friend_and_warns_that_they_can_take_any_object()
    {
        int confirmed = 0;
        var n = Confirmations.GrantModifyRights("Ann Example", () => confirmed++);
        string body = Body(n);
        Assert.Equal("Granting modify rights to another Resident allows them to change, delete or take ANY objects you may have in-world. Be VERY careful when handing out this permission.\nDo you want to grant modify rights for Ann Example?", body);
        Assert.Equal(["Yes", "No"], ButtonTexts(n));
        n.Responder!(new NotificationAnswer(0, ""));
        Assert.Equal(1, confirmed);
    }

    // ---------------- Enter and Esc on a modal alert ----------------

    [Fact]
    public void Enter_presses_the_default_button_once_and_does_nothing_with_no_alert_up()
    {
        double now = 10;
        var model = new NotificationModel(() => now);
        Assert.False(model.PressEnter());
        var answers = new List<int>();
        model.Post(new Notification { Kind = NotificationKind.Alert, Body = NoticeText.Raw("q"), Buttons = [new(NoticeText.Raw("OK"), 0), new(NoticeText.Raw("Cancel"), 1)],
            DefaultButton = 0, CancelButton = 1, Responder = a => answers.Add(a.Button) });
        Assert.True(model.PressEnter());
        Assert.Equal([0], answers);
        Assert.Null(model.Modal);
        Assert.False(model.PressEnter());
    }

    [Fact]
    public void Esc_is_ignored_in_the_alerts_first_second_then_answers_as_the_cancel_button()
    {
        double now = 10;
        var model = new NotificationModel(() => now);
        var answers = new List<int>();
        model.Post(new Notification { Kind = NotificationKind.Alert, Body = NoticeText.Raw("q"), Buttons = [new(NoticeText.Raw("OK"), 0), new(NoticeText.Raw("Cancel"), 1)],
            DefaultButton = 0, CancelButton = 1, Responder = a => answers.Add(a.Button) });
        now = 10.5;
        Assert.True(model.PressEscape());   // taken (nothing else may act on it) but the alert stays: a key held down must not answer it
        Assert.Empty(answers);
        Assert.NotNull(model.Modal);
        now = 11.1;
        Assert.True(model.PressEscape());
        Assert.Equal([1], answers);
        Assert.Null(model.Modal);
        Assert.False(model.PressEscape());   // no alert: Esc is not ours
    }

    [Fact]
    public void An_error_alert_has_OK_as_both_its_default_and_its_escape()
    {
        double now = 1;
        var rig = new Rig { Now = now };
        rig.Notices.Receive(new ServerAlert("Something went wrong.", "", true));
        var alert = Assert.IsType<Notification>(rig.Model.Modal);
        Assert.Equal((0, 0), (alert.DefaultButton, alert.CancelButton));
    }

    [Fact]
    public void Esc_with_an_alert_up_answers_the_alert_and_never_closes_the_window_behind_it()
    {
        var floaters = new FloaterManager(null);
        floaters.Register(new FloaterSpec("alpha", "alpha.title", new UiSize(300, 200), new UiSize(100, 100)));
        floaters.SetBounds(new UiRect(0, 26, 1000, 600));
        floaters.Open("alpha");
        var focus = new InputFocus(floaters);
        focus.SetWindowFocused(true);
        bool alertUp = true;
        focus.ModalEscape = () => alertUp;
        Assert.Equal(EscapeResult.AnsweredModal, focus.OnEscape());
        Assert.True(floaters.IsOpen("alpha"));
        alertUp = false;
        Assert.Equal(EscapeResult.ClosedFloater, focus.OnEscape());
    }

    // ---------------- a text field keeps its editing keys ----------------

    [Theory]
    [InlineData("A")] [InlineData("C")] [InlineData("V")] [InlineData("X")] [InlineData("Z")] [InlineData("Y")] [InlineData("D")]
    public void A_text_field_takes_its_editing_chords_before_any_menu(string key)
    {
        Assert.True(MenuShortcuts.TextFieldFirst(new KeyChord(key, Control: true)));
        Assert.True(MenuShortcuts.TextFieldFirst(new KeyChord(key, Control: true, Shift: true)) || key != "Z");   // redo is Ctrl+Shift+Z
    }

    [Theory]
    [InlineData("I")] [InlineData("B")] [InlineData("L")] [InlineData("H")]
    public void A_chord_a_field_does_not_use_still_reaches_the_menus_while_typing(string key) =>
        Assert.False(MenuShortcuts.TextFieldFirst(new KeyChord(key, Control: true)));

    [Fact]
    public void Ctrl_D_duplicates_the_selection_from_the_world_but_not_while_typing_in_a_field()
    {
        var shell = new ShellController(StringTable.English(), () => { });
        BuildMenu.InstallObject(shell.Menus, () => true, () => null);   // the real Duplicate entry on Ctrl+D (disabled here: no selection to read)
        Assert.Contains(MenuShortcuts.All(TopBarMenuMap.Build(shell.Menus, shell.Commands)), s => s.Entry.Id == BuildMenu.DuplicateId && s.Chord == new KeyChord("D", Control: true));
        // an enabled entry on the same chord in a menu of its own, so the routing rule itself is what is checked
        var plain = new ShellController(StringTable.English(), () => { });
        plain.Menus.AddMenu("t", "menu.build");
        plain.Menus.AddEntry("t", new MenuEntry("t.dup", "menu.build", "t.dup", () => true, Shortcut: "control|D"));
        plain.Commands.Register("t.dup", () => { });
        var view = TopBarMenuMap.Build(plain.Menus, plain.Commands);
        var ctrlD = new KeyChord("D", Control: true);
        Assert.Equal("t.dup", MenuShortcuts.Route(view, ctrlD, textEntry: false)?.Id);   // from the world it runs
        Assert.Null(MenuShortcuts.Route(view, ctrlD, textEntry: true));                  // typing in a field: the field keeps it
    }

    // ---------------- the menu a name in chat opens ----------------

    [Fact]
    public void A_name_in_chat_opens_the_official_avatar_menu_and_greys_what_is_not_built_with_a_reason()
    {
        var entries = ContextMenus.ForName(new AvatarMenuContext(IsSelf: false, IsFriend: false));
        var ids = entries.Select(e => e.Id).ToArray();
        foreach (var wanted in new[] { "profile", "im", "add_friend", "offer_teleport", "request_teleport", "pay", "block", "copy_name" })
            Assert.Contains(wanted, ids);
        Assert.DoesNotContain("remove_friend", ids);
        Assert.True(entries.Single(e => e.Id == "im").Enabled);
        Assert.True(entries.Single(e => e.Id == "offer_teleport").Enabled);
        Assert.True(entries.Single(e => e.Id == "copy_name").Enabled);
        // an entry that does nothing yet is greyed and says why
        foreach (var grey in entries.Where(e => !e.Enabled))
        {
            Assert.False(string.IsNullOrWhiteSpace(grey.ReasonKey));
            Assert.True(Strings.Has(grey.ReasonKey!));
        }
        Assert.Contains(entries, e => !e.Enabled && e.Id == "profile");
    }

    [Fact]
    public void A_friends_name_offers_no_add_friend_and_a_blocked_name_offers_unblock()
    {
        var friend = ContextMenus.ForName(new AvatarMenuContext(false, IsFriend: true)).Select(e => e.Id).ToArray();
        Assert.DoesNotContain("add_friend", friend);
        var blocked = ContextMenus.ForName(new AvatarMenuContext(false, false, IsBlocked: true)).Select(e => e.Id).ToArray();
        Assert.Contains("unblock", blocked);
        Assert.DoesNotContain("block", blocked);
        Assert.Empty(ContextMenus.ForName(new AvatarMenuContext(IsSelf: true, IsFriend: false)).Where(e => e.Id is "im" or "offer_teleport"));
    }

    // ---------------- friend notices ----------------

    private sealed class Rig
    {
        public double Now = 50;
        public readonly FakeTransport Transport = new();
        public readonly NameCache Names = new();
        public readonly NotificationModel Model;
        public readonly FriendsModel Friends;
        public readonly NoticeModel Notices;
        public readonly NoticeFeeder Feeder;
        public readonly List<Guid> Opened = new();

        public Rig()
        {
            var pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
            Model = new NotificationModel(() => Now);
            Friends = new FriendsModel(Self, Names, pipeline.Submit, () => true);
            var teleport = new TeleportModel(Self, Names, pipeline.Submit, () => new DateTimeOffset(2026, 3, 4, 13, 5, 0, TimeSpan.Zero));
            Notices = new NoticeModel(() => Now);
            Feeder = new NoticeFeeder(Model, Friends, teleport, Notices, id => "Ann Example", Opened.Add);
            Friends.Receive(new FriendsListed([new FriendEntry(Ann, "Ann Example", false, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline)]));
        }
    }

    [Fact]
    public void Pressing_a_friends_online_toast_opens_the_conversation_with_that_friend_and_takes_the_toast_down()
    {
        var rig = new Rig();
        rig.Friends.Receive(new FriendPresenceChanged(Ann, true));
        var toast = Assert.Single(rig.Model.Toasts);
        Assert.NotNull(toast.Clicked);
        Assert.True(rig.Model.ClickToast(toast.Id));
        Assert.Equal([Ann], rig.Opened);
        Assert.Empty(rig.Model.Toasts);
    }

    [Fact]
    public void Pressing_a_toast_that_has_no_click_action_does_nothing()
    {
        var rig = new Rig();
        long id = rig.Model.Post(new Notification { Kind = NotificationKind.Tip, Body = NoticeText.Raw("hello") });
        Assert.False(rig.Model.ClickToast(id));
        Assert.Single(rig.Model.Toasts);
    }

    [Fact]
    public void A_friend_notice_line_in_nearby_chat_carries_the_friends_name_as_a_link_and_follows_the_switch()
    {
        foreach (bool on in new[] { true, false })
        {
            var settings = new SettingsService(null);
            settings.Set(Settings.FriendOnlineInChat, on);
            var hub = new CommsHub(Self, new NameCache(), _ => new ActionResult(ActionStatus.Sent, 1), settings, () => new DateTimeOffset(2026, 3, 4, 13, 5, 0, TimeSpan.Zero),
                noticeText: n => "Ann Example is online.");
            hub.Friends.Receive(new FriendsListed([new FriendEntry(Ann, "Ann Example", false, FriendRightsFlags.SeeOnline, FriendRightsFlags.SeeOnline)]));
            hub.Friends.Receive(new FriendPresenceChanged(Ann, true));
            var lines = hub.Nearby.Lines.Where(l => l.Text.Contains(" is online")).ToArray();
            Assert.Equal(on ? 1 : 0, lines.Length);
            if (!on)
                continue;
            Assert.Equal(Ann, lines[0].SpeakerId);
            string rich = ChatText.Format(lines[0], ThemeSpec.Default, Strings, showTime: false);
            Assert.Contains($"[url={ChatText.AgentLinkPrefix}{Ann:D}]Ann Example[/url]", rich);
            Assert.Contains(" is online.", rich);
        }
    }

    [Fact]
    public void The_chat_line_switch_is_on_by_default_and_the_friend_notice_switch_is_unchanged()
    {
        Assert.True(Settings.FriendOnlineInChat.Default);
        Assert.True(Settings.FriendOnlineNotices.Default);
    }
}
