// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Addons;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Core.Session;
using Viewer.Shell.Addons;
using Xunit;
using ChatKind = Viewer.Core.Session.ChatKind;

namespace Viewer.Core.Tests;

/// <summary>The viewer's enforcement of RLV restrictions: locks, sit and teleport, chat and IM, each with its exceptions, and
/// forced actions passing over the issuing object's own restrictions.</summary>
public class RlvGateTests
{
    private static readonly Guid Collar = Guid.Parse("4b2e1d3f-8c5e-4f9b-8d7a-2e3f40516273");
    private static readonly Guid Cuffs = Guid.Parse("4b2e1d3f-8c5e-4f9b-8d7a-2e3f40516274");
    private static readonly Guid CollarItem = Guid.Parse("4b2e1d3f-8c5e-4f9b-8d7a-2e3f40516275");
    private static readonly Guid CuffsItem = Guid.Parse("4b2e1d3f-8c5e-4f9b-8d7a-2e3f40516276");
    private static readonly Guid Chair = Guid.Parse("4b2e1d3f-8c5e-4f9b-8d7a-2e3f40516277");
    private static readonly Guid Friend = Guid.Parse("4b2e1d3f-8c5e-4f9b-8d7a-2e3f40516278");
    private static readonly Guid Stranger = Guid.Parse("4b2e1d3f-8c5e-4f9b-8d7a-2e3f40516279");
    private static readonly Guid Group = Guid.Parse("4b2e1d3f-8c5e-4f9b-8d7a-2e3f4051627a");
    private static readonly Guid Landmark = Guid.Parse("4b2e1d3f-8c5e-4f9b-8d7a-2e3f4051627b");

    private sealed class World : IRlvWorld
    {
        public bool Seated { get; set; }
        public Vector3? AgentPosition { get; set; } = new Vector3(100, 100, 25);
        public readonly Dictionary<Guid, Vector3> Positions = new() { [Chair] = new Vector3(101, 100, 25) };
        public Vector3? ObjectPosition(Guid objectId) => Positions.TryGetValue(objectId, out var p) ? p : null;
        public IReadOnlyList<RlvAttachment> Attachments { get; set; } = [new(Collar, 11, CollarItem, 39), new(Cuffs, 12, CuffsItem, 6)];
        public readonly HashSet<Guid> Sessions = [];
        public bool HasImSession(Guid id) => Sessions.Contains(id);
    }

    private static (RlvGate Gate, World World, List<(int Channel, string Text)> Said) NewGate(params (Guid Obj, string Behaviour, string Option)[] holds)
    {
        var world = new World();
        var said = new List<(int, string)>();
        var gate = new RlvGate(world, (channel, text) => { said.Add((channel, text)); return true; });
        gate.Set(holds.Select((h, i) => new AddonRestriction(h.Obj, h.Obj == Collar ? "Collar" : "Cuffs", h.Behaviour, h.Option, true, i)).ToArray());
        return (gate, world, said);
    }

    private static bool Allowed(RlvGate gate, ActionIntent intent) => gate.Check(intent, ActionPhase.Submit).Allowed;

    private static ActionIntent? Rewritten(RlvGate gate, ActionIntent intent) => gate.Rewrite(intent, out _);

    private static OutfitChangeIntent DetachOf(params Guid[] items) => new(new OutfitChange(Guid.NewGuid(), [], [], [], items));

    // ---------------- locks ----------------

    [Fact]
    public void A_locked_object_cannot_be_taken_off_and_an_unlocked_one_can()
    {
        var (gate, _, _) = NewGate((Collar, "detach", ""));
        Assert.Equal("Collar", Assert.Single(gate.Holds).ObjectName);
        Assert.False(gate.CanDetach(CollarItem));
        Assert.True(gate.CanDetach(CuffsItem));
        var refused = gate.Check(DetachOf(CollarItem), ActionPhase.Submit);
        Assert.Equal((false, "rlv.detach"), (refused.Allowed, refused.Code));
        Assert.True(Allowed(gate, DetachOf(CuffsItem)));
        Assert.False(Allowed(gate, new DropAttachmentIntent(11)));
        Assert.True(Allowed(gate, new DropAttachmentIntent(12)));
    }

    [Fact]
    public void A_locked_attachment_point_holds_whatever_is_worn_there_and_refuses_a_replacing_attach()
    {
        var (gate, _, _) = NewGate((Collar, "detach", "right hand"));   // the cuffs are worn on the right hand (point 6)
        Assert.True(gate.CanDetach(CollarItem));
        Assert.False(gate.CanDetach(CuffsItem));
        var other = Guid.NewGuid();
        Assert.False(Allowed(gate, new OutfitChangeIntent(new OutfitChange(Guid.NewGuid(), [], [], [new AttachRequest(other, 6, false)], []))));
        Assert.True(Allowed(gate, new OutfitChangeIntent(new OutfitChange(Guid.NewGuid(), [], [], [new AttachRequest(other, 6, true)], []))));
    }

    // ---------------- sit and stand ----------------

    [Fact]
    public void Sit_refuses_sitting_on_objects_and_the_ground()
    {
        var (gate, _, _) = NewGate((Collar, "sit", ""));
        Assert.False(Allowed(gate, new SitIntent(Chair)));
        Assert.False(Allowed(gate, new SitGroundIntent()));
        Assert.True(Allowed(gate, new StandIntent()));
    }

    [Fact]
    public void Unsit_refuses_standing_only_while_seated_and_then_also_teleports()
    {
        var (gate, world, _) = NewGate((Collar, "unsit", ""));
        Assert.True(Allowed(gate, new StandIntent()));
        world.Seated = true;
        Assert.False(Allowed(gate, new StandIntent()));
        Assert.False(Allowed(gate, new TeleportLandmarkIntent(Landmark)));
        Assert.False(Allowed(gate, new SitIntent(Chair)));   // sitting elsewhere would stand first
    }

    [Fact]
    public void Sittp_refuses_sitting_on_an_object_further_than_a_metre_and_a_half()
    {
        var (gate, world, _) = NewGate((Collar, "sittp", ""));
        Assert.True(Allowed(gate, new SitIntent(Chair)));      // one metre away
        world.Positions[Chair] = new Vector3(110, 100, 25);
        Assert.False(Allowed(gate, new SitIntent(Chair)));
        Assert.True(Allowed(gate, new SitGroundIntent()));
    }

    [Fact]
    public void Standtp_takes_the_user_back_to_where_they_sat_from()
    {
        var (gate, world, _) = NewGate((Collar, "standtp", ""));
        gate.SatDown(new Vector3(90, 95, 25));
        Assert.Equal(new Vector3(90, 95, 25), gate.StandTeleportTarget());
        var (free, _, _) = NewGate();
        free.SatDown(new Vector3(90, 95, 25));
        Assert.Null(free.StandTeleportTarget());
    }

    // ---------------- teleports ----------------

    [Fact]
    public void Tplm_refuses_landmarks_tploc_refuses_locations_and_home_needs_both()
    {
        var (lm, _, _) = NewGate((Collar, "tplm", ""));
        Assert.False(Allowed(lm, new TeleportLandmarkIntent(Landmark)));
        Assert.True(Allowed(lm, new TeleportRegionIntent("Somewhere", new Vector3(128, 128, 30))));
        Assert.True(Allowed(lm, new TeleportHomeIntent()));
        var (loc, _, _) = NewGate((Collar, "tploc", ""));
        Assert.False(Allowed(loc, new TeleportRegionIntent("Somewhere", new Vector3(128, 128, 30))));
        Assert.False(Allowed(loc, new TeleportHandleIntent(1, new Vector3(1, 1, 1))));
        Assert.True(Allowed(loc, new TeleportHomeIntent()));
        var (both, _, _) = NewGate((Collar, "tplm", ""), (Cuffs, "tploc", ""));
        var home = both.Check(new TeleportHomeIntent(), ActionPhase.Submit);
        Assert.Equal((false, "rlv.teleport"), (home.Allowed, home.Code));
    }

    [Fact]
    public void Tplure_refuses_accepting_offers_except_from_its_exceptions_and_declining_is_always_allowed()
    {
        var (gate, _, _) = NewGate((Collar, "tplure", ""), (Collar, "tplure", Friend.ToString()));
        Assert.False(Allowed(gate, new TeleportLureAnswerIntent(Stranger, Guid.NewGuid(), true)));
        Assert.True(Allowed(gate, new TeleportLureAnswerIntent(Friend, Guid.NewGuid(), true)));
        Assert.True(Allowed(gate, new TeleportLureAnswerIntent(Stranger, Guid.NewGuid(), false)));
    }

    [Fact]
    public void Accepttp_accepts_offers_from_its_avatar_or_from_anyone_without_an_option()
    {
        var (one, _, _) = NewGate((Collar, "accepttp", Friend.ToString()));
        Assert.True(one.AutoAcceptLure(Friend));
        Assert.False(one.AutoAcceptLure(Stranger));
        var (all, _, _) = NewGate((Collar, "accepttp", ""));
        Assert.True(all.AutoAcceptLure(Stranger));
        var (none, _, _) = NewGate();
        Assert.False(none.AutoAcceptLure(Friend));
    }

    // ---------------- forced actions ----------------

    [Fact]
    public void A_forced_action_passes_over_the_issuing_objects_restrictions_but_not_anothers()
    {
        var (gate, world, _) = NewGate((Collar, "sit", ""), (Collar, "unsit", ""));
        world.Seated = true;
        using (gate.Forcing(Collar))
            Assert.True(Allowed(gate, new StandIntent()));
        using (gate.Forcing(Cuffs))
            Assert.False(Allowed(gate, new StandIntent()));
        Assert.False(Allowed(gate, new StandIntent()));
    }

    // ---------------- outgoing chat ----------------

    [Fact]
    public void Sendchat_turns_public_chat_into_an_ellipsis_but_lets_short_emotes_and_ooc_through()
    {
        var (gate, _, _) = NewGate((Collar, "sendchat", ""));
        Assert.Equal("...", ((SayIntent)Rewritten(gate, new SayIntent("hello everyone"))!).Text);
        Assert.Equal("((brb))", ((SayIntent)Rewritten(gate, new SayIntent("((brb))"))!).Text);
        Assert.Equal("/me smiles.", ((SayIntent)Rewritten(gate, new SayIntent("/me smiles. And says hi"))!).Text);
        Assert.Equal("...", ((SayIntent)Rewritten(gate, new SayIntent("/me says \"hi\""))!).Text);
        Assert.Equal("hello", ((SayIntent)Rewritten(gate, new SayIntent("hello", 5))!).Text);   // other channels are not filtered
    }

    [Fact]
    public void Emote_lifts_the_truncation_of_emotes()
    {
        var (gate, _, _) = NewGate((Collar, "sendchat", ""), (Collar, "emote", ""));
        Assert.Equal("/me smiles. And says hi", ((SayIntent)Rewritten(gate, new SayIntent("/me smiles. And says hi"))!).Text);
    }

    [Fact]
    public void Chatshout_chatwhisper_and_chatnormal_change_how_loud_chat_goes()
    {
        var (shout, _, _) = NewGate((Collar, "chatshout", ""));
        Assert.Equal(ChatKind.Say, ((SayIntent)Rewritten(shout, new SayIntent("hi", 0, ChatKind.Shout))!).Chat);
        var (whisper, _, _) = NewGate((Collar, "chatwhisper", ""));
        Assert.Equal(ChatKind.Say, ((SayIntent)Rewritten(whisper, new SayIntent("hi", 0, ChatKind.Whisper))!).Chat);
        var (normal, _, _) = NewGate((Collar, "chatnormal", ""));
        Assert.Equal(ChatKind.Whisper, ((SayIntent)Rewritten(normal, new SayIntent("hi", 0, ChatKind.Say))!).Chat);
        Assert.Equal(ChatKind.Whisper, ((SayIntent)Rewritten(normal, new SayIntent("hi", 0, ChatKind.Shout))!).Chat);
    }

    [Fact]
    public void Redirchat_sends_public_chat_to_its_channels_instead()
    {
        var (gate, _, said) = NewGate((Collar, "redirchat", "1234"), (Cuffs, "redirchat", "-7"));
        Assert.Null(gate.Rewrite(new SayIntent("hello there"), out var code));
        Assert.Equal("rlv.redirected", code);
        Assert.Equal([(1234, "hello there"), (-7, "hello there")], said);
        Assert.NotNull(Rewritten(gate, new SayIntent("/me waves")));   // emotes are not redirected by @redirchat
    }

    [Fact]
    public void Sendchannel_refuses_other_channels_except_its_exceptions()
    {
        var (gate, _, _) = NewGate((Collar, "sendchannel", ""), (Collar, "sendchannel", "77"));
        Assert.Null(gate.Rewrite(new SayIntent("x", 5), out var code));
        Assert.Equal("rlv.sendchannel", code);
        Assert.NotNull(Rewritten(gate, new SayIntent("x", 77)));
        Assert.NotNull(Rewritten(gate, new SayIntent("x", 0)));
    }

    [Fact]
    public void A_reply_passes_every_chat_restriction()
    {
        var (gate, _, _) = NewGate((Collar, "sendchannel", ""), (Collar, "sendchat", ""));
        Assert.Null(Rewritten(gate, new SayIntent("RestrainedLove", 5)));   // the user's own line on that channel is refused
        using (gate.Forcing(Guid.Empty, reply: true))   // as the host sends a reply: no issuing object
            Assert.Equal("RestrainedLove", Assert.IsType<SayIntent>(Rewritten(gate, new SayIntent("RestrainedLove", 5))).Text);
    }

    // ---------------- outgoing IM ----------------

    [Fact]
    public void Sendim_replaces_the_message_with_the_blocked_notice_except_to_its_exceptions()
    {
        var (gate, _, _) = NewGate((Collar, "sendim", ""), (Collar, "sendim", Friend.ToString()));
        Assert.Equal("*** IM blocked by sender's viewer", ((InstantMessageIntent)Rewritten(gate, new InstantMessageIntent(Stranger, "hi"))!).Text);
        Assert.Equal("hi", ((InstantMessageIntent)Rewritten(gate, new InstantMessageIntent(Friend, "hi"))!).Text);
        Assert.Equal("*** IM blocked by sender's viewer", ((GroupMessageIntent)Rewritten(gate, new GroupMessageIntent(Group, "hi"))!).Text);
    }

    [Fact]
    public void Startim_refuses_a_new_conversation_but_not_one_already_open_or_an_exception()
    {
        var (gate, world, _) = NewGate((Collar, "startim", ""), (Collar, "startim", Friend.ToString()));
        var refused = gate.Check(new InstantMessageIntent(Stranger, "hi"), ActionPhase.Submit);
        Assert.Equal((false, "rlv.startim"), (refused.Allowed, refused.Code));
        Assert.True(Allowed(gate, new InstantMessageIntent(Friend, "hi")));
        world.Sessions.Add(Stranger);
        Assert.True(Allowed(gate, new InstantMessageIntent(Stranger, "hi")));
    }

    // ---------------- incoming ----------------

    [Fact]
    public void Recvchat_shows_others_chat_as_an_ellipsis_except_emotes_and_its_exceptions()
    {
        var (gate, _, _) = NewGate((Collar, "recvchat", ""), (Collar, "recvchat", Friend.ToString()));
        Assert.Equal("...", gate.Incoming(Stranger, true, "secret plans"));
        Assert.Equal("/me waves", gate.Incoming(Stranger, true, "/me waves"));
        Assert.Equal("((ooc))", gate.Incoming(Stranger, true, "((ooc))"));
        Assert.Equal("secret plans", gate.Incoming(Friend, true, "secret plans"));
        var (free, _, _) = NewGate();
        Assert.Equal("secret plans", free.Incoming(Stranger, true, "secret plans"));
    }

    [Fact]
    public void Recvchat_sec_counts_only_exceptions_set_by_the_same_object()
    {
        var (gate, _, _) = NewGate((Collar, "recvchat_sec", ""), (Cuffs, "recvchat", Friend.ToString()));
        Assert.Equal("...", gate.Incoming(Friend, true, "secret plans"));
        var (same, _, _) = NewGate((Collar, "recvchat_sec", ""), (Collar, "recvchat", Friend.ToString()));
        Assert.Equal("secret plans", same.Incoming(Friend, true, "secret plans"));
    }

    [Fact]
    public void Recvim_shows_the_blocked_notice_and_answers_the_sender_except_for_its_exceptions()
    {
        var (gate, _, _) = NewGate((Collar, "recvim", ""), (Collar, "recvim", Friend.ToString()));
        var blocked = gate.IncomingIm(Stranger, "hello");
        Assert.Equal("*** IM blocked by your viewer", blocked.Text);
        Assert.Equal("The Resident you messaged is currently prevented from reading your instant messages at the moment, please try again later.", blocked.AutoReply);
        Assert.Equal(new RlvIncomingIm("hello", null), gate.IncomingIm(Friend, "hello"));
        Assert.False(gate.CanReceiveGroup(Group));
    }

    [Fact]
    public void A_restriction_listed_as_not_enforced_changes_nothing()
    {
        var world = new World { Seated = true };
        var gate = new RlvGate(world, (_, _) => true);
        gate.Set([new AddonRestriction(Collar, "Collar", "sit", "", false, 0)]);
        Assert.True(Allowed(gate, new SitIntent(Chair)));
        Assert.False(gate.Has("sit"));
    }

    [Fact]
    public void A_refusal_is_announced_once_with_its_code_for_the_notice()
    {
        var (gate, _, _) = NewGate((Collar, "tploc", ""));
        var heard = new List<string>();
        gate.Refused += heard.Add;
        Assert.False(Allowed(gate, new TeleportRegionIntent("Elsewhere", new Vector3(1, 1, 1))));
        Assert.True(Allowed(gate, new StandIntent()));
        Assert.Equal(["rlv.teleport"], heard);
        Assert.Equal("Unable to initiate teleport due to RLV restrictions", RlvGate.NoticeFor("rlv.teleport"));
        Assert.Equal("Unable to perform action due to RLV restrictions", RlvGate.NoticeFor("rlv.sit"));
    }

    [Fact]
    public void Clearing_lets_everything_through_again()
    {
        var (gate, world, _) = NewGate((Collar, "detach", ""), (Collar, "unsit", ""), (Collar, "sendchat", ""));
        world.Seated = true;
        int changes = 0;
        gate.Changed += () => changes++;
        gate.Clear();
        Assert.True(gate.CanDetach(CollarItem));
        Assert.True(Allowed(gate, new StandIntent()));
        Assert.Equal("hello", ((SayIntent)Rewritten(gate, new SayIntent("hello"))!).Text);
        Assert.Equal(1, changes);
    }
}
