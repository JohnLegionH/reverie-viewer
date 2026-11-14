// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Vector3 = System.Numerics.Vector3;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What scripts ask of the viewer, below the interface: the three new intents' checks and commands, the pipeline's local path for
/// a command the viewer runs itself, the adapter's translations of the library's messages, and the rule that none of it reaches a
/// capture. No grid: a fake transport and synthetic messages. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class ScriptDialogCoreTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid Obj = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a1");
    private static readonly Guid Owner = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000b2");
    private static readonly Guid Item = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000c3");

    private sealed class Rig
    {
        public readonly FakeTransport Transport = new();
        public readonly ActionPipeline Pipeline;
        public readonly List<TransportCommand> Local = new();

        public Rig()
        {
            Pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
            Pipeline.LocalExecutor = c => { Local.Add(c); return CommandDispatch.Handed; };
        }
    }

    // ---------------- intents ----------------

    [Fact]
    public void A_dialog_reply_becomes_its_command_with_the_region_channel_index_and_label()
    {
        var r = new Rig();
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new ScriptDialogReplyIntent(Here, Obj, -4242, 3, "Yes")).Status);
        Assert.Equal(new ScriptDialogReplyCommand(Here, Obj, -4242, 3, "Yes"), Assert.Single(r.Transport.Commands));
    }

    [Fact]
    public void A_text_box_reply_may_be_empty_and_may_hold_a_line_break_but_not_a_control_character()
    {
        var r = new Rig();
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new ScriptDialogReplyIntent(Here, Obj, 7, 0, "")).Status);
        Assert.Equal(ActionStatus.Invalid, r.Pipeline.Submit(new ScriptDialogReplyIntent(Here, Obj, 7, 0, "a\u0007b")).Status);
    }

    public static TheoryData<ActionIntent, string> BadIntents => new()
    {
        { new ScriptDialogReplyIntent(Here, Guid.Empty, 0, 0, "x"), "dialog.no_object" },
        { new ScriptDialogReplyIntent(Here, Obj, 0, -1, "x"), "dialog.bad_button" },
        { new ScriptDialogReplyIntent(Here, Obj, 0, 12, "x"), "dialog.bad_button" },
        { new ScriptDialogReplyIntent(Here, Obj, 0, 0, new string('a', 255)), "dialog.label_too_long" },
        { new ScriptDialogReplyIntent(Here, Obj, 0, 0, "a\u0000b"), "dialog.label_control_chars" },
        { new ScriptPermissionAnswerIntent(Here, Guid.Empty, Item, 0), "permission.no_task" },
        { new ScriptPermissionAnswerIntent(Here, Obj, Guid.Empty, 0), "permission.no_item" },
        { new ScriptPermissionAnswerIntent(Here, Obj, Item, -1), "permission.bad_mask" },
        { new OpenUrlIntent(""), "url.empty" },
        { new OpenUrlIntent("ftp://example.org/x"), "url.not_http" },
        { new OpenUrlIntent("javascript:alert(1)"), "url.not_http" },
        { new OpenUrlIntent("http://exa mple.org/"), "url.not_http" },
        { new OpenUrlIntent("https://example.org/" + new string('a', 250)), "url.too_long" },
    };

    [Theory]
    [MemberData(nameof(BadIntents))]
    public void A_bad_intent_is_refused_with_its_code_and_nothing_is_sent(ActionIntent intent, string code)
    {
        var r = new Rig();
        var result = r.Pipeline.Submit(intent);
        Assert.Equal(ActionStatus.Invalid, result.Status);
        Assert.Contains(result.Issues!, i => i.Code == code);
        Assert.Empty(r.Transport.Commands);
        Assert.Empty(r.Local);
    }

    [Fact]
    public void A_permission_answer_carries_the_mask_given_and_none_is_zero()
    {
        var r = new Rig();
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new ScriptPermissionAnswerIntent(Here, Obj, Item, 0x14)).Status);
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new ScriptPermissionAnswerIntent(Here, Obj, Item, 0)).Status);
        Assert.Equal(new ScriptPermissionAnswerCommand(Here, Obj, Item, 0x14), r.Transport.Commands[0]);
        Assert.Equal(new ScriptPermissionAnswerCommand(Here, Obj, Item, 0), r.Transport.Commands[1]);
    }

    // ---------------- the local path ----------------

    [Fact]
    public void An_open_url_goes_to_the_local_executor_and_never_to_the_grid()
    {
        var r = new Rig();
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new OpenUrlIntent("https://example.org/page?x=1")).Status);
        Assert.Equal(new OpenUrlCommand("https://example.org/page?x=1"), Assert.Single(r.Local));
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void An_open_url_with_no_executor_is_unavailable_and_a_veto_still_refuses_it()
    {
        var bare = new ActionPipeline(() => new FakeTransport(), () => true, new CoreLog(new ListSink(), "test"));
        Assert.Equal(ActionStatus.Unavailable, bare.Submit(new OpenUrlIntent("https://example.org/")).Status);
        var r = new Rig();
        r.Pipeline.AddVeto(new RefuseAll());
        Assert.Equal(ActionStatus.Vetoed, r.Pipeline.Submit(new OpenUrlIntent("https://example.org/")).Status);
        Assert.Empty(r.Local);
    }

    [Fact]
    public void The_local_executor_opens_an_address_through_the_systems_callback_and_refuses_anything_else()
    {
        var opened = new List<string>();
        var run = LocalCommands.Executor(u => { opened.Add(u); return true; });
        Assert.True(run(new OpenUrlCommand("https://example.org/a")).Sent);
        Assert.Equal("https://example.org/a", Assert.Single(opened));
        var refused = run(new SayCommand("hi", 0, ChatKind.Say));
        Assert.False(refused.Sent);
        Assert.Equal("unknown_local_command", refused.Reason);
        Assert.Single(opened);
        var failing = LocalCommands.Executor(_ => false)(new OpenUrlCommand("https://example.org/b"));
        Assert.False(failing.Sent);
        Assert.Equal("open_failed", failing.Reason);
    }

    private sealed class RefuseAll : IActionVeto
    {
        public VetoDecision Check(ActionIntent intent, ActionPhase phase) => VetoDecision.Refuse("test");
    }

    // ---------------- the adapter's translations ----------------

    [Fact]
    public void A_script_dialog_becomes_the_record_with_the_buttons_in_the_scripts_order()
    {
        var args = new ScriptDialogEventArgs("Pick one", "Vendor", UUID.Zero, new UUID(Obj), "Some", "Owner", -77, ["A", "B", "C"], new UUID(Owner));
        var d = LmvTransport.ToScriptDialog(Here, args);
        Assert.NotNull(d);
        Assert.Equal(Obj, d!.ObjectId);
        Assert.Equal(Owner, d.OwnerId);
        Assert.Equal("Vendor", d.ObjectName);
        Assert.Equal("Some Owner", d.OwnerName);
        Assert.False(d.GroupOwned);
        Assert.Equal(-77, d.Channel);
        Assert.Equal("Pick one", d.Message.Reveal());
        Assert.Equal(new[] { "A", "B", "C" }, d.Buttons);
        Assert.Same(Here, d.Sim);
    }

    [Fact]
    public void A_dialog_with_no_first_name_is_owned_by_the_group_named_in_the_last()
    {
        var args = new ScriptDialogEventArgs("m", "Thing", UUID.Zero, new UUID(Obj), "", "Example Group", 1, ["OK"], UUID.Zero);
        var d = LmvTransport.ToScriptDialog(Here, args);
        Assert.NotNull(d);
        Assert.True(d!.GroupOwned);
        Assert.Equal("Example Group", d.OwnerName);
    }

    [Fact]
    public void A_permission_request_becomes_the_record_with_the_mask_as_sent()
    {
        var args = new ScriptQuestionEventArgs(null!, new UUID(Obj), new UUID(Item), "Gadget", "Some Owner", (ScriptPermission)(0x10 | 0x4 | 0x20000));
        var q = LmvTransport.ToScriptPermission(Here, args);
        Assert.NotNull(q);
        Assert.Equal(Obj, q!.TaskId);
        Assert.Equal(Item, q.ItemId);
        Assert.Equal("Gadget", q.ObjectName);
        Assert.Equal("Some Owner", q.OwnerName);
        Assert.Equal(0x10 | 0x4 | 0x20000, q.Questions);
    }

    [Fact]
    public void A_load_url_request_becomes_the_record_and_opens_nothing()
    {
        var args = new LoadUrlEventArgs("Sign", new UUID(Obj), new UUID(Owner), false, "Visit us", "https://example.org/shop");
        var u = LmvTransport.ToLoadUrl(Here, args);
        Assert.NotNull(u);
        Assert.Equal("https://example.org/shop", u!.Url);
        Assert.Equal("Visit us", u.Message.Reveal());
        Assert.Equal("Sign", u.ObjectName);
        Assert.False(u.OwnerIsGroup);
    }

    [Fact]
    public void A_map_destination_becomes_the_record_with_region_position_and_flags()
    {
        var p = new ScriptTeleportRequestPacket
        {
            Data = new ScriptTeleportRequestPacket.DataBlock
            {
                ObjectName = Utils.StringToBytes("Beacon"), SimName = Utils.StringToBytes("Far Region"),
                SimPosition = new LibreMetaverse.Vector3(10, 20, 30), LookAt = new LibreMetaverse.Vector3(1, 0, 0),
            },
            Options = [new ScriptTeleportRequestPacket.OptionsBlock { Flags = 3 }],
        };
        var m = LmvTransport.ToMapDestination(p);
        Assert.NotNull(m);
        Assert.Equal("Beacon", m!.ObjectName);
        Assert.Equal("Far Region", m.RegionName);
        Assert.Equal(new Vector3(10, 20, 30), m.Position);
        Assert.Equal(3u, m.Flags);
    }

    private static InstantMessage Im(InstantMessageDialog dialog, Guid from, string text, Guid session, byte[]? bucket = null) => new()
    {
        FromAgentID = new UUID(from), FromAgentName = "Thing", ToAgentID = new UUID(Owner), ParentEstateID = 0, RegionID = UUID.Zero,
        Position = new LibreMetaverse.Vector3(1, 2, 3), IMSessionID = new UUID(session), Dialog = dialog, Offline = InstantMessageOnline.Online,
        Message = text, BinaryBucket = bucket ?? [],
    };

    [Fact]
    public void An_object_instant_message_carries_the_object_the_text_and_the_place_from_its_bucket()
    {
        var bucket = System.Text.Encoding.UTF8.GetBytes("Far Region/12/34/56\0");
        var m = LmvTransport.ToObjectInstantMessage(Im(InstantMessageDialog.MessageFromObject, Owner, "hello owner", Obj, bucket));
        Assert.NotNull(m);
        Assert.Equal(Obj, m!.ObjectId);
        Assert.Equal(Owner, m.OwnerId);
        Assert.Equal("Thing", m.ObjectName);
        Assert.Equal("Far Region/12/34/56", m.Location);
        Assert.False(m.FromSystem);
        Assert.Equal("hello owner", m.Text.Reveal());
    }

    [Fact]
    public void Only_the_object_dialog_is_an_object_message_and_a_missing_bucket_gives_no_place()
    {
        Assert.Null(LmvTransport.ToObjectInstantMessage(Im(InstantMessageDialog.MessageFromAgent, Owner, "hi", Obj)));
        Assert.Null(LmvTransport.ToObjectInstantMessage(Im(InstantMessageDialog.RequestLure, Owner, "hi", Obj)));
        var m = LmvTransport.ToObjectInstantMessage(Im(InstantMessageDialog.MessageFromObject, Owner, "hi", Obj));
        Assert.NotNull(m);
        Assert.Equal("", m!.Location);
    }

    // ---------------- never in a capture, and through the session ----------------

    private static TransportEvent[] NewEvents() =>
    [
        new ScriptDialogReceived(Here, Obj, Owner, "Vendor", "Some Owner", false, new MessageBody("m", MessageChannel.LocalChat), 1, ["OK"]),
        new ScriptPermissionRequested(Here, Obj, Item, "Gadget", "Some Owner", 0x10),
        new LoadUrlRequested(Here, Obj, Owner, false, "Sign", new MessageBody("go", MessageChannel.LocalChat), "https://example.org/"),
        new MapDestinationRequested("Beacon", "Far Region", new Vector3(1, 2, 3), Vector3.Zero, 3),
        new ObjectInstantMessage(Obj, Owner, "Thing", false, "Far Region/1/2/3", false, new MessageBody("hi", MessageChannel.InstantMessage)),
    ];

    [Fact]
    public void The_new_records_are_not_written_to_a_capture()
    {
        foreach (var e in NewEvents())
            Assert.False(CaptureFile.IsRecorded(e), e.GetType().Name);
    }

    [Fact]
    public void The_new_records_reach_the_communication_channel()
    {
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        var seen = new List<TransportEvent>();
        session.Communication += seen.Add;
        var events = NewEvents();
        foreach (var e in events)
            fake.Raise(e);
        Assert.Equal(events, seen.Where(e => e is ScriptDialogReceived or ScriptPermissionRequested or LoadUrlRequested or MapDestinationRequested or ObjectInstantMessage).ToArray());
    }

    [Fact]
    public void The_adapter_accepts_the_new_commands_as_known_ones()
    {
        // the unknown-command guard in the adapter lists every command it can run; an unlisted one would be refused as unknown
        Assert.True(LmvTransport.IsKnownCommand(new ScriptDialogReplyCommand(Here, Obj, 1, 0, "x")));
        Assert.True(LmvTransport.IsKnownCommand(new ScriptPermissionAnswerCommand(Here, Obj, Item, 0)));
        Assert.False(LmvTransport.IsKnownCommand(new OpenUrlCommand("https://example.org/")));   // local: never reaches the grid adapter
    }
}
