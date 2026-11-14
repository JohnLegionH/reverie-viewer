// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Vector3 = System.Numerics.Vector3;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell.Notifications;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What scripts ask of the viewer as the interface sees it: dialogs and text boxes, permission requests, web addresses and map
/// destinations become notifications, and the user's answers go out as intents. A recording submit stands in for the pipeline; time
/// is a test clock. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class ScriptPromptsTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid ObjA = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a1");
    private static readonly Guid ObjB = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a2");
    private static readonly Guid Owner = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000b2");
    private static readonly Guid Item = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000c3");

    private sealed class Rig
    {
        public double Now = 1000;
        public readonly NotificationModel Model;
        public readonly ScriptPrompts Prompts;
        public readonly List<ActionIntent> Sent = new();
        public readonly List<MapDestinationRequested> Maps = new();
        public string Self = "Test User";

        public Rig()
        {
            Model = new NotificationModel(() => Now);
            Prompts = new ScriptPrompts(Model, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, () => Now, () => Self);
            Prompts.MapDestination += Maps.Add;
        }

        public ScriptDialogReceived Dialog(Guid obj, int channel, params string[] buttons) =>
            new(Here, obj, Owner, "Vendor", "Some Owner", false, new MessageBody("Pick one", MessageChannel.LocalChat), channel, buttons);

        public Notification Only() => Assert.Single(Model.List);
    }

    // ---------------- dialogs ----------------

    [Fact]
    public void A_dialog_waits_for_the_user_with_the_object_the_owner_the_message_and_the_buttons_in_order()
    {
        var r = new Rig();
        r.Prompts.Receive(r.Dialog(ObjA, -5, "Yes", "No", "Maybe"));
        var n = r.Only();
        Assert.Equal(NotificationKind.Offer, n.Kind);   // no timeout: it waits for an answer
        Assert.Equal("Vendor", n.Title!.Literal);
        Assert.Equal("Some Owner", n.Subtitle!.Literal);
        Assert.Equal("Pick one", n.Body.Literal);
        Assert.Equal(new[] { "Yes", "No", "Maybe" }, n.Buttons.Select(b => b.Label.Literal).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, n.Buttons.Select(b => b.Index).ToArray());
        Assert.False(n.TextEntry);
        Assert.Empty(r.Sent);   // nothing is sent until the user chooses
    }

    [Fact]
    public void At_most_twelve_buttons_are_kept_and_the_clicked_one_replies_with_its_index_label_and_channel()
    {
        var r = new Rig();
        r.Prompts.Receive(r.Dialog(ObjA, -77, Enumerable.Range(0, 15).Select(i => "b" + i).ToArray()));
        var n = r.Only();
        Assert.Equal(12, n.Buttons.Count);
        r.Model.Answer(n.Id, 11);
        Assert.Equal(new ScriptDialogReplyIntent(Here, ObjA, -77, 11, "b11"), Assert.Single(r.Sent));
    }

    [Fact]
    public void Dialogs_and_text_boxes_are_marked_as_script_dialogs_and_other_prompts_are_not()
    {
        var r = new Rig();
        r.Prompts.Receive(r.Dialog(ObjA, 1, "OK"));
        r.Prompts.Receive(r.Dialog(ObjB, 1, ScriptPrompts.TextBoxMarker));
        r.Prompts.Receive(Ask(1 << 4, task: Guid.NewGuid()));
        r.Prompts.Receive(Url("https://example.org/"));
        Assert.Equal(new[] { true, true, false, false }, r.Model.List.Select(n => n.ScriptDialog).ToArray());
    }

    [Fact]
    public void Ignore_closes_the_dialog_and_sends_nothing()
    {
        var r = new Rig();
        r.Prompts.Receive(r.Dialog(ObjA, 1, "OK"));
        r.Model.Answer(r.Only().Id, -1);
        Assert.Empty(r.Sent);
        Assert.Empty(r.Model.List);
    }

    [Fact]
    public void A_new_dialog_from_the_same_object_replaces_the_old_one_which_closes_with_no_reply()
    {
        var r = new Rig();
        r.Prompts.Receive(r.Dialog(ObjA, 1, "old"));
        r.Prompts.Receive(r.Dialog(ObjA, 2, "new"));
        r.Prompts.Receive(r.Dialog(ObjB, 1, "other object"));
        Assert.Equal(2, r.Model.List.Count);
        Assert.DoesNotContain(r.Model.List, n => n.Buttons.Any(b => b.Label.Literal == "old"));
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void In_the_per_channel_mode_only_a_dialog_on_the_same_channel_replaces_the_old_one()
    {
        var r = new Rig();
        r.Prompts.Limit = DialogLimit.PerChannel;
        r.Prompts.Receive(r.Dialog(ObjA, 1, "ch1"));
        r.Prompts.Receive(r.Dialog(ObjA, 2, "ch2"));
        Assert.Equal(2, r.Model.List.Count);
        r.Prompts.Receive(r.Dialog(ObjA, 1, "ch1 again"));
        Assert.Equal(2, r.Model.List.Count);
        Assert.DoesNotContain(r.Model.List, n => n.Buttons.Any(b => b.Label.Literal == "ch1"));
        Assert.Contains(r.Model.List, n => n.Buttons.Any(b => b.Label.Literal == "ch2"));
    }

    [Fact]
    public void A_dialog_from_a_muted_object_or_owner_is_dropped_and_counted()
    {
        var r = new Rig();
        r.Prompts.IsMuted = (obj, owner) => obj == ObjA;
        r.Prompts.Receive(r.Dialog(ObjA, 1, "OK"));
        Assert.Empty(r.Model.List);
        Assert.Equal(1, r.Prompts.Muted);
        r.Prompts.Receive(r.Dialog(ObjB, 1, "OK"));
        Assert.Single(r.Model.List);
    }

    [Fact]
    public void A_dialog_from_a_group_owned_object_names_the_group()
    {
        var r = new Rig();
        r.Prompts.Receive(new ScriptDialogReceived(Here, ObjA, Owner, "Door", "Example Group", true, new MessageBody("m", MessageChannel.LocalChat), 1, ["OK"]));
        Assert.Equal("Example Group", r.Only().Subtitle!.Literal);
    }

    // ---------------- text boxes ----------------

    [Fact]
    public void A_dialog_with_the_text_box_marker_is_a_text_entry_whatever_its_other_buttons_are()
    {
        var r = new Rig();
        r.Prompts.Receive(r.Dialog(ObjA, 9, "Other", ScriptPrompts.TextBoxMarker));
        var n = r.Only();
        Assert.True(n.TextEntry);
        Assert.Empty(n.Buttons);
        r.Model.Answer(n.Id, 0, "hello there");
        Assert.Equal(new ScriptDialogReplyIntent(Here, ObjA, 9, 0, "hello there"), Assert.Single(r.Sent));
    }

    [Fact]
    public void An_empty_text_box_reply_is_sent_empty_and_a_long_one_is_cut_at_the_limit_on_a_character_boundary()
    {
        var r = new Rig();
        r.Prompts.Receive(r.Dialog(ObjA, 9, ScriptPrompts.TextBoxMarker));
        r.Model.Answer(r.Only().Id, 0, "");
        Assert.Equal("", ((ScriptDialogReplyIntent)r.Sent[0]).Label);
        r.Prompts.Receive(r.Dialog(ObjA, 9, ScriptPrompts.TextBoxMarker));
        r.Model.Answer(r.Only().Id, 0, new string('a', 300));
        Assert.Equal(ScriptPrompts.MaxLabelBytes, ((ScriptDialogReplyIntent)r.Sent[1]).Label.Length);
        r.Prompts.Receive(r.Dialog(ObjA, 9, ScriptPrompts.TextBoxMarker));
        r.Model.Answer(r.Only().Id, 0, new string('é', 200));   // two bytes each: 127 fit
        var cut = ((ScriptDialogReplyIntent)r.Sent[2]).Label;
        Assert.Equal(127, cut.Length);
        Assert.All(cut, c => Assert.Equal('é', c));
    }

    [Fact]
    public void Ignore_on_a_text_box_sends_nothing()
    {
        var r = new Rig();
        r.Prompts.Receive(r.Dialog(ObjA, 9, ScriptPrompts.TextBoxMarker));
        r.Model.Answer(r.Only().Id, -1, "typed but ignored");
        Assert.Empty(r.Sent);
    }

    // ---------------- permissions ----------------

    private static ScriptPermissionRequested Ask(int mask, string owner = "Some Owner", Guid? task = null) =>
        new(Here, task ?? ObjA, Item, "Gadget", owner, mask);

    [Fact]
    public void A_permission_request_lists_each_known_permission_and_yes_grants_exactly_those()
    {
        var r = new Rig();
        r.Prompts.Receive(Ask((1 << 4) | (1 << 2) | (1 << 25)));
        var n = r.Only();
        Assert.Equal(NotificationKind.Offer, n.Kind);
        Assert.Equal("Gadget", n.Title!.Literal);
        Assert.Equal("Some Owner", n.Subtitle!.Literal);
        Assert.Equal(ScriptPermissions.Asked((1 << 4) | (1 << 2)).Select(e => e.Key).ToArray(), n.Details.Select(d => d.Key!).ToArray());
        Assert.Equal(2, n.Buttons.Count);
        Assert.Empty(r.Sent);
        r.Model.Answer(n.Id, 0);
        Assert.Equal(new ScriptPermissionAnswerIntent(Here, ObjA, Item, (1 << 4) | (1 << 2)), Assert.Single(r.Sent));
    }

    [Fact]
    public void No_grants_nothing_and_closing_sends_no_answer_at_all()
    {
        var r = new Rig();
        r.Prompts.Receive(Ask(1 << 4));
        r.Model.Answer(r.Only().Id, 1);
        Assert.Equal(new ScriptPermissionAnswerIntent(Here, ObjA, Item, 0), Assert.Single(r.Sent));
        r.Prompts.Receive(Ask(1 << 4, task: ObjB));
        r.Model.Answer(r.Only().Id, -1);
        Assert.Single(r.Sent);
    }

    [Fact]
    public void A_request_with_only_unknown_permissions_shows_nothing_and_is_not_answered()
    {
        var r = new Rig();
        r.Prompts.Receive(Ask((1 << 25) | 1));
        Assert.Empty(r.Model.List);
        Assert.Empty(r.Sent);
        Assert.Equal(1, r.Prompts.Unknown);
    }

    [Fact]
    public void Taking_money_is_always_listed_and_marks_the_request_as_a_caution_even_beside_other_permissions()
    {
        var r = new Rig();
        r.Prompts.Receive(Ask((1 << 1) | (1 << 4)));
        var n = r.Only();
        Assert.True(n.Caution);
        Assert.Contains(n.Details, d => d.Key == ScriptPermissions.Table[0].Key);
        Assert.Equal(2, n.Details.Count);
        r.Prompts.Receive(Ask(1 << 4, task: ObjB));
        Assert.False(r.Model.List.Single(x => x.Source == ObjB).Caution);
    }

    [Fact]
    public void More_than_five_requests_in_ten_seconds_from_one_owner_are_dropped_until_the_window_passes()
    {
        var r = new Rig();
        for (int i = 0; i < 6; i++)
            r.Prompts.Receive(Ask(1 << 4, task: Guid.NewGuid()));
        Assert.Equal(ScriptPrompts.PermissionThrottleCount, r.Model.List.Count);
        Assert.Equal(1, r.Prompts.Throttled);
        r.Prompts.Receive(Ask(1 << 4, owner: "Another Owner", task: Guid.NewGuid()));   // another owner has a window of its own
        Assert.Equal(ScriptPrompts.PermissionThrottleCount + 1, r.Model.List.Count);
        r.Now += ScriptPrompts.PermissionThrottleSeconds + 0.5;
        r.Prompts.Receive(Ask(1 << 4, task: Guid.NewGuid()));
        Assert.Equal(ScriptPrompts.PermissionThrottleCount + 2, r.Model.List.Count);
    }

    [Fact]
    public void The_users_own_objects_are_throttled_one_object_at_a_time_not_all_together()
    {
        var r = new Rig();
        for (int i = 0; i < 8; i++)
            r.Prompts.Receive(Ask(1 << 4, owner: "Test User", task: Guid.NewGuid()));   // eight different own objects
        Assert.Equal(8, r.Model.List.Count);
        var same = Guid.NewGuid();
        for (int i = 0; i < 6; i++)
            r.Prompts.Receive(Ask(1 << 4, owner: "Test User", task: same));
        Assert.Equal(8 + ScriptPrompts.PermissionThrottleCount, r.Model.List.Count + 0);
    }

    // ---------------- web addresses ----------------

    private static LoadUrlRequested Url(string url) =>
        new(Here, ObjA, Owner, false, "Sign", new MessageBody("Visit our shop", MessageChannel.LocalChat), url);

    [Fact]
    public void A_web_address_opens_only_when_the_user_chooses_go_to_page()
    {
        var r = new Rig();
        r.Prompts.Receive(Url("https://example.org/shop"));
        var n = r.Only();
        Assert.Equal("Sign", n.Title!.Literal);
        Assert.Equal("Visit our shop", n.Body.Literal);
        Assert.Contains(n.Details, d => d.Literal == "https://example.org/shop");
        Assert.Equal(2, n.Buttons.Count);
        Assert.Empty(r.Sent);   // receiving it opens nothing
        r.Model.Answer(n.Id, 0);
        Assert.Equal(new OpenUrlIntent("https://example.org/shop"), Assert.Single(r.Sent));
    }

    [Fact]
    public void Cancel_and_closing_open_nothing()
    {
        var r = new Rig();
        r.Prompts.Receive(Url("https://example.org/a"));
        r.Model.Answer(r.Only().Id, 1);
        r.Prompts.Receive(Url("https://example.org/b"));
        r.Model.Answer(r.Only().Id, -1);
        Assert.Empty(r.Sent);
    }

    [Theory]
    [InlineData("ftp://example.org/x")]
    [InlineData("file:///etc/hosts")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public void An_address_that_is_not_http_or_https_is_shown_but_has_no_go_button(string url)
    {
        var r = new Rig();
        r.Prompts.Receive(Url(url));
        var n = r.Only();
        Assert.Contains(n.Details, d => d.Literal == url);
        Assert.Single(n.Buttons);
        Assert.Equal(1, n.Buttons[0].Index);   // the Cancel button keeps its index
        r.Model.Answer(n.Id, 0);   // even a stray go-to-page answer opens nothing
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void A_web_address_from_a_muted_object_is_dropped()
    {
        var r = new Rig();
        r.Prompts.IsMuted = (obj, owner) => owner == Owner;
        r.Prompts.Receive(Url("https://example.org/"));
        Assert.Empty(r.Model.List);
        Assert.Equal(1, r.Prompts.Muted);
    }

    // ---------------- map destinations ----------------

    [Fact]
    public void A_map_destination_is_passed_on_once_and_only_while_scripts_may_show_windows()
    {
        var r = new Rig();
        var m = new MapDestinationRequested("Beacon", "Far Region", new Vector3(1, 2, 3), Vector3.Zero, 3);
        r.Prompts.Receive(m);
        Assert.Equal(m, Assert.Single(r.Maps));
        r.Prompts.ScriptsCanShowUi = false;
        r.Prompts.Receive(m);
        Assert.Single(r.Maps);
        Assert.Empty(r.Model.List);   // it is not a prompt
    }
}

/// <summary>The notification hub: events queued from any thread are handled on the main thread's pump. Every member builds its own objects.</summary>
public class NotificationHubTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid Obj = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a1");

    [Fact]
    public void Events_queued_from_another_thread_are_held_until_the_pump_and_then_become_prompts()
    {
        double now = 10;
        var hub = new NotificationHub(i => new ActionResult(ActionStatus.Sent, 1), () => now, () => "Test User");
        var e = new ScriptDialogReceived(Here, Obj, Guid.NewGuid(), "Vendor", "Some Owner", false, new MessageBody("pick", MessageChannel.LocalChat), 1, ["OK"]);
        Task.Run(() => hub.Enqueue(e)).Wait();
        Assert.Empty(hub.Model.List);   // nothing is touched off the main thread
        Assert.Equal(1, hub.Pump());
        Assert.Single(hub.Model.List);
        Assert.Equal(0, hub.Pump());
    }

    [Fact]
    public void Events_that_are_not_about_scripts_are_ignored_and_the_pump_takes_down_old_toasts()
    {
        double now = 10;
        var hub = new NotificationHub(i => new ActionResult(ActionStatus.Sent, 1), () => now, () => "Test User");
        hub.Enqueue(new ServerAlert("x", "", false));
        hub.Enqueue(new MapBlockSize(1, 1, 256, 256, "R", null, 13));
        Assert.Equal(0, hub.Pump());
        hub.Model.Post(new Notification { Kind = NotificationKind.Tip, Body = NoticeText.Raw("t") });
        now += 11;
        hub.Pump();
        Assert.Empty(hub.Model.Toasts);
    }
}
