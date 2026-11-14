// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Addon.Rlv;
using Viewer.Addons;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell.Addons;
using Xunit;
using ChatKind = Viewer.Addons.ChatKind;

namespace Viewer.Core.Tests;

/// <summary>RLV in the add-on host: off by default, switched at the next login, the restraints only for built-in add-ons,
/// failing open, owner-say command lines kept out of chat while it runs, and the action pipeline's rewrite step. Also the
/// headless replay: owner-say lines from a fake attachment go through the real add-on and host into the gate.</summary>
public class RlvHostTests
{
    private static readonly Guid Collar = Guid.Parse("5c3f2e40-9d6f-4a0c-8e8b-3f4051627384");
    private static readonly Guid CollarItem = Guid.Parse("5c3f2e40-9d6f-4a0c-8e8b-3f4051627385");
    private static readonly Guid Chair = Guid.Parse("5c3f2e40-9d6f-4a0c-8e8b-3f4051627386");

    private sealed class Services : IAddonServices
    {
        public double Clock;
        public readonly List<(int Channel, string Text)> Said = [];
        public readonly List<string> Forced = [];
        public readonly HashSet<Guid> Present = [Collar];
        public double Now => Clock;
        public void SendAnimations(IReadOnlyList<(Guid Animation, bool Start)> requests) { }
        public bool Say(string text) { Said.Add((0, text)); return true; }
        public IAddonInventory? Inventory => null;
        public bool SayOnChannel(int channel, string text) { Said.Add((channel, text)); return true; }
        public bool ForceSit(Guid objectId) { Forced.Add("sit " + objectId); return true; }
        public bool ForceStand() { Forced.Add("stand"); return true; }
        public bool ForceTeleport(double x, double y, double z) { Forced.Add($"tp {x}/{y}/{z}"); return true; }
        public bool ObjectPresent(Guid objectId) => Present.Contains(objectId);
        public string ViewerName => "Example Viewer";
    }

    private sealed class World : IRlvWorld
    {
        public bool Seated { get; set; }
        public Vector3? AgentPosition => new Vector3(10, 10, 20);
        public Vector3? ObjectPosition(Guid objectId) => new Vector3(10, 11, 20);
        public IReadOnlyList<RlvAttachment> Attachments => [new(Collar, 7, CollarItem, 39)];
        public bool HasImSession(Guid id) => false;
    }

    private static AddonManifest RlvManifest(bool builtIn = true) => AddonManifest.Parse("""
        { "id": "rlv", "name": "RLV", "version": "1.0.0", "author": "a", "licence": "l", "interface": "0.4", "job": "rlv",
          "permissions": ["session", "chat.read", "window", "restraints"], "default_on": false, "switch_at": "next-login",
          "library": "Viewer.Addon.Rlv.dll" }
        """, out _)!;

    private static (AddonHost Host, Services Services, RlvGate Gate, World World) NewHost(string? stateFile = null, Func<IAddon>? factory = null, bool builtIn = true)
    {
        var services = new Services();
        var host = new AddonHost(services, new CoreLog(new ListSink(), "test"), stateFile);
        var world = new World();
        var gate = new RlvGate(world, services.SayOnChannel);
        host.Restraints = gate;
        host.Register(RlvManifest(), factory ?? (() => new RlvAddon()), builtIn);
        return (host, services, gate, world);
    }

    private static ChatLine OwnerSay(string text) => new(ChatKind.OwnerSay, Collar, "Collar", text);

    [Fact]
    public void The_manifest_says_off_by_default_and_switched_at_the_next_login()
    {
        var m = RlvManifest();
        Assert.Equal((false, true), (m.DefaultOn, m.SwitchAtNextLogin));
        Assert.Contains(AddonPermissions.Restraints, m.Permissions);
    }

    [Fact]
    public void RLV_is_off_until_switched_on_and_owner_say_commands_then_show_as_chat()
    {
        var (host, _, gate, _) = NewHost();
        host.StartEnabled();
        var rlv = host.Addons.Single();
        Assert.False(rlv.Enabled);
        Assert.False(rlv.Running);
        host.LoggedIn();
        host.Chat(OwnerSay("@sit=n"));
        Assert.False(host.Swallows(OwnerSay("@sit=n")));
        Assert.Empty(gate.Holds);
    }

    [Fact]
    public void Switching_takes_effect_at_the_next_login_in_both_directions()
    {
        string folder = TestTemp.NewFolder("rlv-state");
        try
        {
            string state = Path.Combine(folder, "addons-state.json");
            var (host, _, gate, _) = NewHost(state);
            host.StartEnabled();
            host.LoggedIn();
            Assert.True(host.SetEnabled("rlv", true));
            var rlv = host.Addons.Single();
            Assert.True(rlv.Enabled);          // the switch shows the choice
            Assert.False(rlv.Running);         // but nothing runs before the next login
            Assert.True(rlv.WaitsForLogin);
            Assert.Equal("Takes effect at next login.", rlv.Note);
            host.LoggedOut();
            host.LoggedIn();
            Assert.True(rlv.Running);
            Assert.False(rlv.WaitsForLogin);
            host.Chat(OwnerSay("@sit=n"));
            Assert.True(gate.Has("sit"));

            Assert.True(host.SetEnabled("rlv", false));
            Assert.True(rlv.Running);          // still on until the next login
            Assert.True(gate.Has("sit"));
            host.LoggedOut();
            host.LoggedIn();
            Assert.False(rlv.Running);
            Assert.Empty(gate.Holds);

            // a new start of the viewer reads the choice back
            var (again, _, _, _) = NewHost(state);
            again.StartEnabled();
            Assert.False(again.Addons.Single().Enabled);
        }
        finally { TestTemp.Delete(folder); }
    }

    [Fact]
    public void While_RLV_runs_command_lines_are_swallowed_and_other_owner_say_still_shows()
    {
        var (host, _, _, _) = NewHost();
        host.SetEnabled("rlv", true);
        host.StartEnabled();
        host.LoggedIn();
        Assert.True(host.Addons.Single().Running);
        Assert.True(host.Swallows(OwnerSay("@sit=n")));
        Assert.False(host.Swallows(OwnerSay("hello")));
        Assert.False(host.Swallows(new ChatLine(ChatKind.Local, Collar, "Someone", "@sit=n")));
    }

    [Fact]
    public void The_restraints_are_for_add_ons_that_come_with_the_viewer_only()
    {
        IAddonRestraints? seen = null;
        Exception? thrown = null;
        var probe = new Probe(c => { try { seen = c.Restraints; } catch (AddonPermissionException ex) { thrown = ex; } });
        var (host, _, _, _) = NewHost(factory: () => probe, builtIn: false);
        host.LoggedIn();
        Assert.True(host.SetEnabled("rlv", true, grantPermissions: true));
        host.LoggedOut();
        host.LoggedIn();
        Assert.Null(seen);
        Assert.IsType<AddonPermissionException>(thrown);

        var builtInProbe = new Probe(c => seen = c.Restraints);
        var (host2, _, _, _) = NewHost(factory: () => builtInProbe);
        host2.SetEnabled("rlv", true);
        host2.StartEnabled();
        host2.LoggedIn();
        Assert.NotNull(seen);
        Assert.Equal("Example Viewer", seen!.ViewerName);
    }

    [Fact]
    public void An_add_on_error_switches_it_off_and_every_restriction_goes_with_it()
    {
        var probe = new Probe(c =>
        {
            c.Restraints!.Publish([new AddonRestriction(Collar, "Collar", "unsit", "", true, 0)]);
            c.ChatReceived += _ => throw new InvalidOperationException("broken");
        });
        var (host, _, gate, world) = NewHost(factory: () => probe);
        host.SetEnabled("rlv", true);
        host.StartEnabled();
        host.LoggedIn();
        world.Seated = true;
        Assert.False(gate.Check(new StandIntent(), ActionPhase.Submit).Allowed);
        host.Chat(OwnerSay("@anything=n"));
        Assert.False(host.Addons.Single().Running);
        Assert.Empty(gate.Holds);
        Assert.True(gate.Check(new StandIntent(), ActionPhase.Submit).Allowed);   // fails open
    }

    [Fact]
    public void Headless_replay_lock_refusal_redirect_force_and_clear()
    {
        var (host, services, gate, world) = NewHost();
        host.SetEnabled("rlv", true);
        host.StartEnabled();
        host.LoggedIn();
        services.Clock = 1;
        host.Chat(OwnerSay("@detach=n,tploc=n,redirchat:4444=add,versionnew=5555"));
        Assert.False(gate.CanDetach(CollarItem));                                           // greyed and refused
        Assert.False(gate.Check(new TeleportRegionIntent("Elsewhere", new Vector3(1, 1, 1)), ActionPhase.Submit).Allowed);
        Assert.Null(gate.Rewrite(new SayIntent("hello"), out _));                            // redirected
        Assert.Contains((4444, "hello"), services.Said);
        Assert.Contains((5555, "RestrainedLove viewer v3.4.3 (Example Viewer RLV 1.0.0)"), services.Said);
        host.Chat(OwnerSay($"@sit:{Chair}=force"));
        host.Chat(OwnerSay("@unsit=force"));
        Assert.Equal(["sit " + Chair, "stand"], services.Forced);
        var listed = host.Diagnose("rlv", "restrictions", null);
        Assert.NotNull(listed);
        host.Chat(OwnerSay("@clear"));
        Assert.Empty(gate.Holds);
        Assert.True(gate.CanDetach(CollarItem));
        Assert.Equal("hello", ((SayIntent)gate.Rewrite(new SayIntent("hello"), out _)!).Text);
    }

    [Fact]
    public void The_pipeline_sends_a_rewritten_intent_and_refuses_a_dropped_one()
    {
        var transport = new FakeTransport();
        var pipeline = new ActionPipeline(() => transport, () => true, new CoreLog(new ListSink(), "test"));
        pipeline.AddRewriter(new Rewriter(i => i is SayIntent { Text: "drop" } ? null : i is SayIntent s ? s with { Text = "..." } : i));
        Assert.Equal(ActionStatus.Sent, pipeline.Submit(new SayIntent("hello")).Status);
        var sent = Assert.IsType<SayCommand>(Assert.Single(transport.Commands));
        Assert.Equal("...", sent.Text);
        var dropped = pipeline.Submit(new SayIntent("drop"));
        Assert.Equal((ActionStatus.Vetoed, "rewrite.code"), (dropped.Status, dropped.Code));
        Assert.Single(transport.Commands);
        pipeline.AddRewriter(new Rewriter(_ => throw new InvalidOperationException()));
        Assert.Equal(ActionStatus.Sent, pipeline.Submit(new SayIntent("x")).Status);   // a failing rewriter changes nothing
    }

    // ---------------- incoming chat and IMs ----------------

    private static readonly Guid Self = Guid.Parse("5c3f2e40-9d6f-4a0c-8e8b-3f4051627390");
    private static readonly Guid Other = Guid.Parse("5c3f2e40-9d6f-4a0c-8e8b-3f4051627391");
    private static readonly Guid SomeGroup = Guid.Parse("5c3f2e40-9d6f-4a0c-8e8b-3f4051627392");

    private static ChatReceived Heard(Guid from, ChatLineType type, string text, ChatSource source = ChatSource.Agent) =>
        new(null, from, from, "Someone", source, type, ChatAudible.Fully, default, new MessageBody(text, MessageChannel.LocalChat));

    private static InstantMessageReceived Im(Guid from, string text, ImKind kind = ImKind.Message, Guid? session = null) =>
        new(from, "Someone", Self, session ?? Guid.NewGuid(), kind, 0, false, null, kind == ImKind.GroupMessage, new MessageBody(text, MessageChannel.InstantMessage));

    private static string? TextOf(RlvIncomingResult r) => r.Shown switch
    {
        ChatReceived c => c.Text.Reveal(),
        InstantMessageReceived m => m.Text.Reveal(),
        _ => null,
    };

    [Fact]
    public void Incoming_owner_say_command_lines_are_kept_out_while_RLV_runs_and_shown_while_it_is_off()
    {
        var (host, _, gate, _) = NewHost();
        var line = Heard(Collar, ChatLineType.OwnerSay, "@sit=n", ChatSource.Object);
        Assert.Equal("@sit=n", TextOf(RlvIncoming.Apply(line, host, gate, Self)));     // off: shown as normal owner-say
        host.SetEnabled("rlv", true);
        host.StartEnabled();
        host.LoggedIn();
        Assert.Null(RlvIncoming.Apply(line, host, gate, Self).Shown);
        Assert.Equal("hello", TextOf(RlvIncoming.Apply(Heard(Collar, ChatLineType.OwnerSay, "hello", ChatSource.Object), host, gate, Self)));
    }

    [Fact]
    public void Incoming_chat_is_filtered_under_recvchat_but_never_the_users_own_or_owner_say()
    {
        var (host, _, gate, _) = NewHost();
        gate.Set([new AddonRestriction(Collar, "Collar", "recvchat", "", true, 0)]);
        Assert.Equal("...", TextOf(RlvIncoming.Apply(Heard(Other, ChatLineType.Say, "secret plans"), host, gate, Self)));
        Assert.Equal("...", TextOf(RlvIncoming.Apply(Heard(Chair, ChatLineType.Shout, "an object talks", ChatSource.Object), host, gate, Self)));
        Assert.Equal("my own words", TextOf(RlvIncoming.Apply(Heard(Self, ChatLineType.Say, "my own words"), host, gate, Self)));
        Assert.Equal("to you only", TextOf(RlvIncoming.Apply(Heard(Chair, ChatLineType.OwnerSay, "to you only", ChatSource.Object), host, gate, Self)));
    }

    [Fact]
    public void Incoming_IMs_show_as_blocked_and_answer_the_sender_and_group_messages_are_dropped_under_recvim()
    {
        var (host, _, gate, _) = NewHost();
        gate.Set([new AddonRestriction(Collar, "Collar", "recvim", "", true, 0)]);
        var r = RlvIncoming.Apply(Im(Other, "hello"), host, gate, Self);
        Assert.Equal("*** IM blocked by your viewer", TextOf(r));
        Assert.Equal((Other, RlvGate.BlockedRecvImRemote), (r.AutoReplyTo, r.AutoReply));
        Assert.Null(RlvIncoming.Apply(Im(Other, "group news", ImKind.GroupMessage, SomeGroup), host, gate, Self).Shown);
        var typing = Im(Other, "", ImKind.StartTyping);
        Assert.Same(typing, RlvIncoming.Apply(typing, host, gate, Self).Shown);
    }

    private sealed class Rewriter(Func<ActionIntent, ActionIntent?> rewrite) : IActionRewriter
    {
        public ActionIntent? Rewrite(ActionIntent intent, out string? code)
        {
            code = "rewrite.code";
            return rewrite(intent);
        }
    }

    private sealed class Probe(Action<IAddonContext> onStart) : IAddon
    {
        public void Start(IAddonContext context) => onStart(context);
        public void Stop() { }
    }
}
