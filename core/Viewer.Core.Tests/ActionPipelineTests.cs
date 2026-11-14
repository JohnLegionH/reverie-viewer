// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;
using F = Viewer.Motion.AgentControlFlags;

namespace Viewer.Core.Tests;

/// <summary>
/// The action pipeline (ADR-015): every intent is checked, offered to the veto points and only then handed to the command
/// side of the transport, once. All offline, through the fake transport. Nothing here touches process-wide state, so the
/// class runs in parallel with the others.
/// </summary>
public class ActionPipelineTests
{
    private static readonly Guid Someone = Guid.Parse("0f3f2d4e-7a41-4c1d-9a55-5b0f2f6a1e11");
    private static readonly Guid Thing = Guid.Parse("a1b2c3d4-0000-4000-8000-0000000000aa");
    private static readonly Guid Folder = Guid.Parse("a1b2c3d4-0000-4000-8000-0000000000bb");
    private static readonly Guid Trash = Guid.Parse("a1b2c3d4-0000-4000-8000-0000000000cc");

    private sealed class Rig
    {
        public FakeTransport Transport = new();
        public ListSink Sink = new();
        public bool InWorld = true;
        public ActionPipeline Pipeline;
        public Rig(bool withTransport = true) =>
            Pipeline = new ActionPipeline(() => withTransport ? Transport : null, () => InWorld, new CoreLog(Sink, "test"));
    }

    private sealed class Veto(Func<ActionIntent, ActionPhase, VetoDecision> check) : IActionVeto
    {
        public int Calls;
        public VetoDecision Check(ActionIntent intent, ActionPhase phase) { Calls++; return check(intent, phase); }
    }

    private sealed class RecordingSink : IHeldSink
    {
        public readonly List<string> Calls = new();
        public Action? OnContinue;
        public void Begin(HeldIntent intent) => Calls.Add("begin");
        public void Continue(HeldIntent intent) { Calls.Add("continue"); OnContinue?.Invoke(); }
        public void End(HeldIntent intent, HoldEnd reason) => Calls.Add("end:" + reason);
    }

    // ---------------- a good intent is sent, once, as the command it means ----------------

    [Fact]
    public void A_valid_say_is_handed_over_once_with_its_text_trimmed()
    {
        var r = new Rig();
        var result = r.Pipeline.Submit(new SayIntent("  hello there  ", 0, ChatKind.Shout));
        Assert.Equal(ActionStatus.Sent, result.Status);
        Assert.NotEqual(0, result.Id);
        var sent = Assert.Single(r.Transport.Commands);
        Assert.Equal(new SayCommand("hello there", 0, ChatKind.Shout), sent);
        Assert.Equal(1, r.Transport.ExecuteCalls);
    }

    [Fact]
    public void Each_kind_of_intent_becomes_its_command()
    {
        var r = new Rig();
        var cases = new (ActionIntent Intent, TransportCommand Command)[]
        {
            (new InstantMessageIntent(Someone, " hi \n there "), new InstantMessageCommand(Someone, "hi \n there")),
            (new TouchIntent(4711), new TouchCommand(4711)),
            (new SitIntent(Thing, new Vector3(0.5f, 0, 0.25f)), new SitCommand(Thing, new Vector3(0.5f, 0, 0.25f))),
            (new StandIntent(), new StandCommand()),
            (new TeleportRegionIntent("  Example Region ", new Vector3(128, 64, 30)), new TeleportRegionCommand("Example Region", new Vector3(128, 64, 30))),
            (new TeleportLandmarkIntent(Thing), new TeleportLandmarkCommand(Thing)),
            (new TeleportHomeIntent(), new TeleportHomeCommand()),
            (new InventoryMoveIntent(Thing, Folder, " Renamed "), new MoveInventoryItemCommand(Thing, Folder, "Renamed")),
            (new InventoryMoveIntent(Thing, Folder), new MoveInventoryItemCommand(Thing, Folder, null)),
            (new InventoryTrashIntent(Thing, Trash, Confirmed: true), new MoveInventoryItemCommand(Thing, Trash, null)),
        };
        foreach (var (intent, command) in cases)
        {
            var before = r.Transport.Commands.Count;
            Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(intent).Status);
            Assert.Equal(before + 1, r.Transport.Commands.Count);
            Assert.Equal(command, r.Transport.Commands[^1]);
        }
    }

    [Fact]
    public void Submission_ids_are_distinct_and_one_submit_is_one_dispatch()
    {
        var r = new Rig();
        var a = r.Pipeline.Submit(new TouchIntent(1));
        var b = r.Pipeline.Submit(new TouchIntent(1));
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, r.Transport.ExecuteCalls);
    }

    // ---------------- checked before anything is sent ----------------

    public static TheoryData<ActionIntent, string> BadIntents => new()
    {
        { new SayIntent(""), "say.empty" },
        { new SayIntent("   \t "), "say.empty" },
        { new SayIntent(new string('a', 1024)), "say.too_long" },
        { new SayIntent(new string('é', 600)), "say.too_long" },   // 1,200 bytes: the limit is bytes, not characters
        { new SayIntent("a\u0000b"), "say.control_chars" },
        { new SayIntent("two\nlines"), "say.control_chars" },
        { new SayIntent("ok", 0, (ChatKind)9), "say.bad_kind" },
        { new InstantMessageIntent(Guid.Empty, "hi"), "im.no_target" },
        { new InstantMessageIntent(Someone, " "), "im.empty" },
        { new InstantMessageIntent(Someone, new string('a', 1024)), "im.too_long" },
        { new InstantMessageIntent(Someone, "a\u0007b"), "im.control_chars" },
        { new TouchIntent(0), "touch.no_object" },
        { new SitIntent(Guid.Empty), "sit.no_object" },
        { new SitIntent(Thing, new Vector3(float.NaN, 0, 0)), "sit.bad_offset" },
        { new TeleportRegionIntent("", new Vector3(1, 1, 1)), "teleport.no_region" },
        { new TeleportRegionIntent(new string('r', 129), new Vector3(1, 1, 1)), "teleport.region_too_long" },
        { new TeleportRegionIntent("R", new Vector3(-1, 1, 1)), "teleport.bad_position" },
        { new TeleportRegionIntent("R", new Vector3(1, 5000, 1)), "teleport.bad_position" },
        { new TeleportRegionIntent("R", new Vector3(1, 1, float.PositiveInfinity)), "teleport.bad_position" },
        { new TeleportLandmarkIntent(Guid.Empty), "teleport.no_landmark" },
        { new InventoryMoveIntent(Guid.Empty, Folder), "inventory.no_item" },
        { new InventoryMoveIntent(Thing, Guid.Empty), "inventory.no_folder" },
        { new InventoryMoveIntent(Thing, Thing), "inventory.same_id" },
        { new InventoryMoveIntent(Thing, Folder, "   "), "inventory.bad_name" },
        { new InventoryMoveIntent(Thing, Folder, new string('n', 64)), "inventory.bad_name" },
        { new InventoryTrashIntent(Guid.Empty, Trash, true), "inventory.no_item" },
        { new InventoryTrashIntent(Thing, Guid.Empty, true), "inventory.no_trash" },
        { new InventoryTrashIntent(Thing, Trash, false), "inventory.not_confirmed" },
    };

    [Theory]
    [MemberData(nameof(BadIntents))]
    public void A_bad_intent_is_refused_with_its_reason_and_nothing_is_sent(ActionIntent intent, string code)
    {
        Assert.Contains(intent.Validate(), i => i.Code == code);

        var r = new Rig();
        var result = r.Pipeline.Submit(intent);
        Assert.Equal(ActionStatus.Invalid, result.Status);
        Assert.Contains(result.Issues!, i => i.Code == code);
        Assert.Empty(r.Transport.Commands);
        Assert.Equal(0, r.Transport.ExecuteCalls);
    }

    [Fact]
    public void The_limits_themselves_are_allowed()
    {
        var r = new Rig();
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new SayIntent(new string('a', 1023))).Status);
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new SayIntent("x", int.MaxValue)).Status);
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new InventoryMoveIntent(Thing, Folder, new string('n', 63))).Status);
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new TeleportRegionIntent(new string('r', 128), new Vector3(4096, 4096, 4096))).Status);
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new InstantMessageIntent(Someone, "line one\nline two\tend")).Status);   // an IM may hold a newline and a tab
    }

    [Fact]
    public void A_held_intent_cannot_be_submitted_as_a_one_off()
    {
        var r = new Rig();
        var result = r.Pipeline.Submit(new HoldMovementIntent(F.AtPos));
        Assert.Equal(ActionStatus.Invalid, result.Status);
        Assert.Contains(result.Issues!, i => i.Code == "action.held_intent");
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void A_null_intent_is_invalid_and_does_not_throw()
    {
        var r = new Rig();
        var result = r.Pipeline.Submit(null!);
        Assert.Equal(ActionStatus.Invalid, result.Status);
        Assert.Empty(r.Transport.Commands);
    }

    // ---------------- veto points ----------------

    [Fact]
    public void A_veto_refuses_before_anything_is_sent_and_later_vetoes_are_not_asked()
    {
        var r = new Rig();
        var allow = new Veto((_, _) => VetoDecision.Allow);
        var refuse = new Veto((i, p) => i is TouchIntent ? VetoDecision.Refuse("test.no_touch") : VetoDecision.Allow);
        var after = new Veto((_, _) => VetoDecision.Allow);
        r.Pipeline.AddVeto(allow);
        r.Pipeline.AddVeto(refuse);
        r.Pipeline.AddVeto(after);

        var result = r.Pipeline.Submit(new TouchIntent(5));
        Assert.Equal(ActionStatus.Vetoed, result.Status);
        Assert.Equal("test.no_touch", result.Code);
        Assert.Empty(r.Transport.Commands);
        Assert.Equal(1, allow.Calls);
        Assert.Equal(1, refuse.Calls);
        Assert.Equal(0, after.Calls);

        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new StandIntent()).Status);   // the same veto lets another kind through
        Assert.Equal(1, after.Calls);
    }

    [Fact]
    public void A_veto_sees_the_intent_and_the_phase_and_an_invalid_intent_never_reaches_it()
    {
        var r = new Rig();
        ActionIntent? seen = null;
        ActionPhase? phase = null;
        var veto = new Veto((i, p) => { seen = i; phase = p; return VetoDecision.Allow; });
        r.Pipeline.AddVeto(veto);
        var say = new SayIntent("hi");
        r.Pipeline.Submit(say);
        Assert.Same(say, seen);
        Assert.Equal(ActionPhase.Submit, phase);

        r.Pipeline.Submit(new SayIntent(""));
        Assert.Equal(1, veto.Calls);
    }

    [Fact]
    public void A_veto_that_throws_refuses_and_nothing_is_sent()
    {
        var r = new Rig();
        r.Pipeline.AddVeto(new Veto((_, _) => throw new InvalidOperationException("policy broke")));
        var result = r.Pipeline.Submit(new SayIntent("hi"));
        Assert.Equal(ActionStatus.Vetoed, result.Status);
        Assert.Equal("veto.error", result.Code);
        Assert.Empty(r.Transport.Commands);
    }

    [Fact]
    public void A_restriction_set_refuses_the_kinds_it_holds_and_lifting_it_lets_them_through()
    {
        var r = new Rig();
        var restrictions = new RestrictionSet();
        r.Pipeline.AddVeto(restrictions);
        restrictions.Restrict(ActionKind.Say);
        Assert.True(restrictions.IsRestricted(ActionKind.Say));
        Assert.False(restrictions.IsRestricted(ActionKind.Touch));

        var refused = r.Pipeline.Submit(new SayIntent("hi"));
        Assert.Equal(ActionStatus.Vetoed, refused.Status);
        Assert.Equal("restricted.say", refused.Code);
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new TouchIntent(1)).Status);

        restrictions.Lift(ActionKind.Say);
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new SayIntent("hi")).Status);

        restrictions.RestrictAll();
        Assert.Equal(ActionStatus.Vetoed, r.Pipeline.Submit(new TouchIntent(1)).Status);
        restrictions.LiftAll();
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new TouchIntent(1)).Status);
    }

    // ---------------- not now ----------------

    [Fact]
    public void Nothing_is_sent_outside_the_world_and_a_bad_intent_is_still_told_its_fault()
    {
        var r = new Rig { InWorld = false };
        var result = r.Pipeline.Submit(new SayIntent("hi"));
        Assert.Equal(ActionStatus.Unavailable, result.Status);
        Assert.Equal("not_in_world", result.Code);
        Assert.Empty(r.Transport.Commands);
        Assert.Equal(ActionStatus.Invalid, r.Pipeline.Submit(new SayIntent("")).Status);
    }

    [Fact]
    public void A_transport_without_a_command_side_is_unavailable()
    {
        var r = new Rig(withTransport: false);
        var result = r.Pipeline.Submit(new SayIntent("hi"));
        Assert.Equal(ActionStatus.Unavailable, result.Status);
        Assert.Equal("no_command_transport", result.Code);
    }

    [Fact]
    public void A_veto_is_not_asked_about_an_intent_that_could_not_go_anyway()
    {
        var r = new Rig { InWorld = false };
        var veto = new Veto((_, _) => VetoDecision.Allow);
        r.Pipeline.AddVeto(veto);
        r.Pipeline.Submit(new SayIntent("hi"));
        Assert.Equal(0, veto.Calls);
    }

    // ---------------- what the protocol layer says ----------------

    [Fact]
    public void A_command_the_protocol_layer_did_not_send_is_a_failure_and_nothing_is_unknown()
    {
        var r = new Rig();
        r.Transport.OnCommand = _ => CommandDispatch.NotSent("not_connected");
        var result = r.Pipeline.Submit(new SayIntent("hi"));
        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.Equal("not_connected", result.Code);
        Assert.Empty(r.Pipeline.Unreconciled);
    }

    [Fact]
    public void A_throw_during_the_send_is_an_unknown_outcome_never_retried_until_reconciled()
    {
        var r = new Rig();
        r.Transport.OnCommand = _ => throw new IOException("socket closed");
        var result = r.Pipeline.Submit(new InventoryMoveIntent(Thing, Folder));
        Assert.Equal(ActionStatus.OutcomeUnknown, result.Status);
        Assert.Equal(1, r.Transport.ExecuteCalls);   // tried once and not again
        Assert.Equal([result.Id], r.Pipeline.Unreconciled);

        // a later, different submission does not re-send the uncertain one
        r.Transport.OnCommand = null;
        Assert.Equal(ActionStatus.Sent, r.Pipeline.Submit(new TouchIntent(2)).Status);
        Assert.Equal(2, r.Transport.ExecuteCalls);
        Assert.Equal([result.Id], r.Pipeline.Unreconciled);

        Assert.True(r.Pipeline.Reconciled(result.Id));
        Assert.Empty(r.Pipeline.Unreconciled);
        Assert.False(r.Pipeline.Reconciled(result.Id));   // a second time, and an id never issued, are not found
        Assert.False(r.Pipeline.Reconciled(987654));
    }

    // ---------------- messages stay out of the log ----------------

    [Fact]
    public void The_log_names_what_was_done_and_never_the_message()
    {
        var r = new Rig();
        r.Transport.OnCommand = c => c is InstantMessageCommand ? throw new IOException("boom") : CommandDispatch.Handed;
        r.Pipeline.Submit(new SayIntent("a secret chat line"));
        r.Pipeline.Submit(new InstantMessageIntent(Someone, "a private message body"));
        r.Pipeline.Submit(new SayIntent(""));
        r.Pipeline.Submit(new InventoryMoveIntent(Thing, Folder, "Secret Item Name"));
        string text = r.Sink.Text;
        Assert.DoesNotContain("secret chat", text);
        Assert.DoesNotContain("private message", text);
        Assert.DoesNotContain("Secret Item Name", text);
        Assert.DoesNotContain(Someone.ToString(), text);
        Assert.Contains("action.submit", text);
        Assert.Contains("Say", text);
        Assert.Contains("OutcomeUnknown", text);
    }

    // ---------------- held intents ----------------

    [Fact]
    public void A_hold_begins_continues_and_ends_through_its_sink_and_ends_once()
    {
        var r = new Rig();
        var sink = new RecordingSink();
        var result = r.Pipeline.BeginHold(new HoldMovementIntent(F.AtPos), sink, out var hold);
        Assert.Equal(ActionStatus.Sent, result.Status);
        Assert.NotNull(hold);
        Assert.True(hold!.IsActive);
        Assert.Equal(1, r.Pipeline.ActiveHolds);
        Assert.Empty(r.Transport.Commands);   // a hold is not a command: its repeated traffic is the sink's, the adapter's

        Assert.True(r.Pipeline.Continue(hold));
        Assert.True(r.Pipeline.Continue(hold));
        r.Pipeline.End(hold);
        r.Pipeline.End(hold);
        Assert.False(r.Pipeline.Continue(hold));
        Assert.Equal(["begin", "continue", "continue", "end:Released"], sink.Calls);
        Assert.False(hold.IsActive);
        Assert.Equal(HoldEnd.Released, hold.EndedBy);
        Assert.Equal(0, r.Pipeline.ActiveHolds);
    }

    [Fact]
    public void A_hold_is_checked_before_it_begins()
    {
        var r = new Rig();
        var sink = new RecordingSink();
        Assert.Equal(ActionStatus.Invalid, r.Pipeline.BeginHold(new HoldMovementIntent(0), sink, out var none).Status);
        Assert.Null(none);
        // Every bit of the 32 is assigned in the protocol's agent control list, so there is no bit the protocol does not know.
        // The check refuses the ones this viewer has no name for (it sends only the named bits): the lowest of them.
        uint unnamed = Enumerable.Range(0, 32).Select(i => 1u << i).First(b => !AgentControlFlags.All.Contains(b));
        Assert.Equal(ActionStatus.Invalid, r.Pipeline.BeginHold(new HoldMovementIntent(unnamed), sink, out _).Status);

        r.Pipeline.AddVeto(new Veto((_, p) => p == ActionPhase.Begin ? VetoDecision.Refuse("test.no_hold") : VetoDecision.Allow));
        var vetoed = r.Pipeline.BeginHold(new HoldMovementIntent(F.AtPos), sink, out var hold);
        Assert.Equal(ActionStatus.Vetoed, vetoed.Status);
        Assert.Null(hold);

        r.InWorld = false;
        Assert.Equal(ActionStatus.Unavailable, r.Pipeline.BeginHold(new HoldMovementIntent(F.AtPos), sink, out _).Status);
        Assert.Empty(sink.Calls);
        Assert.Equal(0, r.Pipeline.ActiveHolds);
    }

    [Fact]
    public void A_restriction_added_between_ticks_ends_the_hold_at_once_and_the_next_tick_sends_nothing()
    {
        var r = new Rig();
        var restrictions = new RestrictionSet();
        r.Pipeline.AddVeto(restrictions);
        var sink = new RecordingSink();
        r.Pipeline.BeginHold(new HoldMovementIntent(F.AtPos | F.FastAt), sink, out var hold);
        Assert.True(r.Pipeline.Continue(hold!));

        restrictions.Restrict(ActionKind.HoldMovement);   // no tick has happened since
        Assert.False(hold!.IsActive);
        Assert.Equal(HoldEnd.Revoked, hold.EndedBy);
        Assert.Equal(["begin", "continue", "end:Revoked"], sink.Calls);

        Assert.False(r.Pipeline.Continue(hold));
        Assert.Equal(["begin", "continue", "end:Revoked"], sink.Calls);   // nothing more reached the sink
        Assert.Equal(0, r.Pipeline.ActiveHolds);
    }

    [Fact]
    public void A_veto_that_changes_its_mind_without_telling_is_caught_on_the_next_tick()
    {
        var r = new Rig();
        bool allowed = true;
        r.Pipeline.AddVeto(new Veto((_, p) => allowed ? VetoDecision.Allow : VetoDecision.Refuse("test.revoked")));
        var sink = new RecordingSink();
        r.Pipeline.BeginHold(new HoldMovementIntent(F.AtPos), sink, out var hold);
        Assert.True(r.Pipeline.Continue(hold!));
        allowed = false;
        Assert.False(r.Pipeline.Continue(hold!));
        Assert.Equal(["begin", "continue", "end:Revoked"], sink.Calls);
        Assert.Equal(HoldEnd.Revoked, hold!.EndedBy);
    }

    [Fact]
    public void Only_the_holds_a_restriction_covers_end()
    {
        var r = new Rig();
        var restrictions = new RestrictionSet();
        r.Pipeline.AddVeto(restrictions);
        var a = new RecordingSink();
        r.Pipeline.BeginHold(new HoldMovementIntent(F.AtPos), a, out var hold);
        restrictions.Restrict(ActionKind.Say);   // not movement
        Assert.True(hold!.IsActive);
        Assert.Equal(["begin"], a.Calls);
    }

    [Fact]
    public void A_hold_may_change_its_controls_on_a_tick_and_the_change_is_checked_too()
    {
        var r = new Rig();
        var sink = new RecordingSink();
        r.Pipeline.BeginHold(new HoldMovementIntent(F.AtPos), sink, out var hold);
        Assert.True(r.Pipeline.Continue(hold!, new HoldMovementIntent(F.AtPos | F.YawPos)));
        Assert.Equal(new HoldMovementIntent(F.AtPos | F.YawPos), hold!.Intent);
        Assert.False(r.Pipeline.Continue(hold, new HoldMovementIntent(0)));   // an unusable change ends the hold
        Assert.Equal(HoldEnd.Failed, hold.EndedBy);
        Assert.Equal("end:Failed", sink.Calls[^1]);
    }

    [Fact]
    public void A_sink_that_throws_ends_the_hold_and_does_not_leak_the_exception()
    {
        var r = new Rig();
        var sink = new RecordingSink { OnContinue = () => throw new InvalidOperationException("sink broke") };
        r.Pipeline.BeginHold(new HoldMovementIntent(F.AtPos), sink, out var hold);
        Assert.False(r.Pipeline.Continue(hold!));
        Assert.Equal(HoldEnd.Failed, hold!.EndedBy);
        Assert.Equal(["begin", "continue", "end:Failed"], sink.Calls);
    }

    [Fact]
    public void Leaving_the_world_ends_a_hold_on_its_next_tick_and_ending_all_holds_ends_every_one()
    {
        var r = new Rig();
        var one = new RecordingSink();
        var two = new RecordingSink();
        r.Pipeline.BeginHold(new HoldMovementIntent(F.AtPos), one, out var h1);
        r.Pipeline.BeginHold(new HoldMovementIntent(F.LeftPos), two, out var h2);
        Assert.Equal(2, r.Pipeline.ActiveHolds);

        r.InWorld = false;
        Assert.False(r.Pipeline.Continue(h1!));
        Assert.Equal(HoldEnd.SessionEnded, h1!.EndedBy);

        r.Pipeline.EndAllHolds(HoldEnd.SessionEnded);
        Assert.Equal(["begin", "end:SessionEnded"], two.Calls);
        Assert.Equal(0, r.Pipeline.ActiveHolds);
        Assert.False(h2!.IsActive);
    }

    [Fact]
    public void Disposing_the_pipeline_ends_the_holds_and_refuses_new_work()
    {
        var r = new Rig();
        var sink = new RecordingSink();
        r.Pipeline.BeginHold(new HoldMovementIntent(F.AtPos), sink, out _);
        r.Pipeline.Dispose();
        Assert.Equal(["begin", "end:SessionEnded"], sink.Calls);
        Assert.Equal(ActionStatus.Unavailable, r.Pipeline.Submit(new TouchIntent(1)).Status);
        Assert.Empty(r.Transport.Commands);
    }

    // ---------------- the session owns the pipeline ----------------

    private static (ViewerSession Session, FakeTransport Transport) InWorld()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, new SimRef(256000UL << 32 | 256000UL, "Here"), serverBaked: true);
        return (s, t);
    }

    [Fact]
    public void A_session_in_world_sends_through_its_pipeline_and_before_that_it_does_not()
    {
        var t = new FakeTransport();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid:8002/" }, t, new CoreLog(new ListSink(), "test"));
        Assert.Equal(ActionStatus.Unavailable, s.Actions.Submit(new SayIntent("hi")).Status);   // not logged in
        s.Dispose();

        var (live, transport) = InWorld();
        Assert.Equal(ActionStatus.Sent, live.Actions.Submit(new SayIntent("hi")).Status);
        Assert.Equal(new SayCommand("hi", 0, ChatKind.Say), Assert.Single(transport.Commands));
        live.Dispose();
    }

    [Fact]
    public void A_session_that_ends_ends_its_holds_and_sends_no_more()
    {
        var (s, t) = InWorld();
        var sink = new RecordingSink();
        s.Actions.BeginHold(new HoldMovementIntent(F.AtPos), sink, out var hold);
        t.Raise(new Disconnected("test"));
        Assert.False(hold!.IsActive);
        Assert.Equal(HoldEnd.SessionEnded, hold.EndedBy);
        Assert.Equal(ActionStatus.Unavailable, s.Actions.Submit(new TouchIntent(1)).Status);
        Assert.Empty(t.Commands);
        s.Dispose();
    }

    [Fact]
    public void Disposing_the_session_ends_a_hold()
    {
        var (s, _) = InWorld();
        var sink = new RecordingSink();
        s.Actions.BeginHold(new HoldMovementIntent(F.AtPos), sink, out var hold);
        s.Dispose();
        Assert.Equal(HoldEnd.SessionEnded, hold!.EndedBy);
        Assert.Equal(["begin", "end:SessionEnded"], sink.Calls);
    }

    // ---------------- transport maintenance does not wait on policy ----------------

    [Fact]
    public async Task Transport_maintenance_runs_with_every_intent_refused()
    {
        var (s, t) = InWorld();
        var restrictions = new RestrictionSet();
        s.Actions.AddVeto(restrictions);
        restrictions.RestrictAll();
        Assert.Equal(ActionStatus.Vetoed, s.Actions.Submit(new SayIntent("hi")).Status);   // the policy is in force

        // the draw distance the simulator culls by is circuit upkeep, not an intent
        s.SetDrawDistance(300);
        Assert.Equal([300f], t.DrawDistances);

        // grid events still apply (the session keeps its world current)
        var changes = new List<Viewer.Core.World.WorldChange>();
        s.World.Changed += changes.Add;
        t.Raise(new AvatarUpdated(t.CurrentSim,
            new Viewer.Core.World.AvatarData(7, Someone, "Test User", new Vector3(1, 2, 3), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), Vector3.Zero, 0)));
        Assert.NotEmpty(changes);

        // and a logout is not an intent: it goes through
        await s.LogoutAsync(CancellationToken.None);
        Assert.Equal(1, t.Logouts);
        s.Dispose();
    }

    private sealed class ControlsTransport : ISessionTransport, IAgentControlTransport
    {
        public event Action<TransportEvent>? Event;
        public readonly List<AgentUpdate> Updates = new();
        public SimRef? CurrentSim { get; set; }
        public int ConnectedSimulatorCount { get; set; }
        public Task<TransportLoginResult> LoginAsync(TransportLoginRequest request, CancellationToken ct) => Task.FromResult(FakeTransport.Ok(Guid.NewGuid()));
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
        public void SetDrawDistance(float metres) { }
        public void SendAgentUpdate(AgentUpdate update) => Updates.Add(update);
        public void SendAlwaysRun(bool run) { }
        public void Dispose() { }
        public void Raise(TransportEvent e) => Event?.Invoke(e);
    }

    [Fact]
    public void The_own_avatars_update_path_is_the_adapters_and_is_not_held_up_by_the_pipeline()
    {
        // ViewerSession.SendAgentUpdate goes to the transport's agent-control side directly; the movement work that turns
        // it into a held intent plugs into BeginHold/Continue without this path changing.
        var t = new ControlsTransport();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid:8002/" }, t, new CoreLog(new ListSink(), "test"));
        Assert.True(s.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None).GetAwaiter().GetResult().Ok);
        var sim = new SimRef(256000UL << 32 | 256000UL, "Here");
        t.CurrentSim = sim;
        t.ConnectedSimulatorCount = 1;
        t.Raise(new SimConnected(sim, 256, 256));
        t.Raise(new EventQueueRunning(sim));
        t.Raise(new MovementComplete(sim));
        var restrictions = new RestrictionSet();
        s.Actions.AddVeto(restrictions);
        restrictions.RestrictAll();

        var update = new AgentUpdate(F.AtPos, Quaternion.Identity, Vector3.Zero, Vector3.UnitX);
        Assert.True(s.SendAgentUpdate(update));
        Assert.Equal([update], t.Updates);
        s.Dispose();
    }
}
