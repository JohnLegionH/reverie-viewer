// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse.Packets;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Motion;
using Viewer.Shell;
using Xunit;
using F = Viewer.Motion.AgentControlFlags;

namespace Viewer.Core.Tests;

/// <summary>
/// The AgentUpdate the viewer sends, against the official viewer's rule (llviewermessage.cpp send_agent_update, llagent.cpp getHeadRotation and
/// getRenderState): when it is sent (changes, keep-alive, never during a teleport), the head rotation, the state and flag bytes, and that the
/// socket is never written to while the movement lock is held. Every test builds its own objects, so the class runs in parallel with the others.
/// </summary>
public class AgentUpdateOfficialTests
{
    private static AgentUpdate U(uint controls = 0, Quaternion? head = null, byte state = 0, byte flags = 0) =>
        new(controls, Quaternion.Identity, new Vector3(100, 100, 22), new Vector3(104, 100, 22), head, state, flags);

    // ---------------- the send rule ----------------

    [Fact]
    public void A_change_of_state_or_flags_is_sent_once_the_throttle_allows_it_and_no_follow_up_comes_after()
    {
        var rule = new AgentUpdateRule();
        Assert.True(rule.ShouldSend(U(), 0));
        Assert.False(rule.ShouldSend(U(state: AgentRenderStates.Typing), 0.004));      // inside the 8 ms: throttled, only a control change goes through it
        Assert.True(rule.ShouldSend(U(state: AgentRenderStates.Typing), 0.009));       // allowed now, and sent because the state differs from the last sent
        Assert.False(rule.ShouldSend(U(state: AgentRenderStates.Typing), 0.02));       // unchanged: nothing, and no follow-up as there is for control bits
        Assert.True(rule.ShouldSend(U(state: AgentRenderStates.Typing, flags: AgentRenderStates.HideTitle), 0.03));
        Assert.False(rule.ShouldSend(U(state: AgentRenderStates.Typing, flags: AgentRenderStates.HideTitle), 0.04));
        Assert.True(rule.ShouldSend(U(), 0.05));                                       // both back to none
    }

    [Fact]
    public void A_head_turn_alone_is_sent_by_the_official_thresholds()
    {
        var rule = new AgentUpdateRule();
        Assert.True(rule.ShouldSend(U(), 0));
        Quaternion Head(double degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(degrees * Math.PI / 180));
        Assert.True(rule.ShouldSend(U(head: Head(5)), 0.02));     // 5 degrees: way off (dot below 0.9997), at once
        Assert.False(rule.ShouldSend(U(head: Head(5.3)), 0.05));  // 0.3 degrees from the last sent: close enough
        // 1 degree from the last sent (dot 0.99996): between the two limits, sent when the rising threshold passes it, 0.448 s after the last send
        Assert.False(rule.ShouldSend(U(head: Head(6)), 0.40));
        Assert.True(rule.ShouldSend(U(head: Head(6)), 0.50));
    }

    [Fact]
    public void An_update_the_session_refused_is_not_counted_as_sent()
    {
        var rule = new AgentUpdateRule();
        Assert.True(rule.ShouldSend(U(F.AtPos), 0));
        Assert.Equal(1, rule.Sent);
        rule.Unsent();
        Assert.Equal(0, rule.Sent);
        Assert.True(rule.ShouldSend(U(F.AtPos), 0.01));   // as if the first had never been decided: still the first send
        Assert.Equal(1, rule.Sent);
    }

    // ---------------- the head rotation ----------------

    [Fact]
    public void Outside_mouse_look_the_head_follows_the_body_and_in_mouse_look_it_looks_where_the_camera_looks()
    {
        Assert.Null(AgentHead.Rotation(false, Quaternion.Identity, Vector3.Normalize(new Vector3(1, 0, 1))));
        float pitch = MathF.PI / 6;
        var at = new Vector3(MathF.Cos(pitch), 0, MathF.Sin(pitch));
        var head = AgentHead.Rotation(true, Quaternion.Identity, at);
        Assert.NotNull(head);
        Near(at, Vector3.Transform(Vector3.UnitX, head!.Value));
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitY, head.Value));                                         // left stays level
        Near(new Vector3(-MathF.Sin(pitch), 0, MathF.Cos(pitch)), Vector3.Transform(Vector3.UnitZ, head.Value));  // up tilts with the view
        // a body turned to face north, the camera looking north and down
        var body = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var down = Vector3.Normalize(new Vector3(0, 1, -0.5f));
        var headNorth = AgentHead.Rotation(true, body, down)!.Value;
        Near(down, Vector3.Transform(Vector3.UnitX, headNorth));
    }

    private static void Near(Vector3 expected, Vector3 actual) => Assert.True((expected - actual).Length() < 1e-4f, $"expected {expected}, got {actual}");

    // ---------------- the driver: states, head, refused sends ----------------

    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");

    private static (OwnAvatarDriver Driver, List<AgentUpdate> Sent, Func<bool, bool> SetAccept) Placed()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        double now = 0;
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => now);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        var sent = new List<AgentUpdate>();
        bool accept = true;
        var driver = new OwnAvatarDriver(scene, s.World, u => { if (accept) sent.Add(u); return accept; });
        t.Raise(new AvatarUpdated(Here, new AvatarData(800, ServerAppearanceTests.Me, "Own Avatar", new Vector3(100, 100, 21.95f), AgentControlInput.BodyRotation(0), new Vector3(0.45f, 0.6f, 1.9f), Vector3.Zero, 0)));
        foreach (var c in changes.ToList()) scene.Apply(c);
        return (driver, sent, a => accept = a);
    }

    [Fact]
    public void Typing_in_local_chat_sets_the_state_byte_for_as_long_as_it_lasts_and_each_change_is_sent()
    {
        var (driver, sent, _) = Placed();
        bool typing = false;
        driver.StateSource = () => (AgentRenderStates.State(typing, false), AgentRenderStates.Flags(false, false));
        for (int i = 1; i <= 120; i++)
        {
            typing = i >= 2 && i < 90;
            driver.Frame(i / 60.0, 1 / 60.0, new Vector3(97, 100, 23), new Vector3(101, 100, 23));
        }
        // the first update, the change to typing, the keep-alive a second later, the change back; nothing else in two seconds
        Assert.Equal([(byte)0, AgentRenderStates.Typing, AgentRenderStates.Typing, (byte)0], sent.Select(u => u.State).ToArray());
        Assert.All(sent, u => Assert.Equal(0, u.Flags));
    }

    [Fact]
    public void The_hide_title_and_autopilot_flags_are_carried_in_the_flag_byte()
    {
        var (driver, sent, _) = Placed();
        driver.StateSource = () => (0, AgentRenderStates.Flags(true, true));
        driver.Frame(0.016, 1 / 60.0, new Vector3(97, 100, 23), new Vector3(101, 100, 23));
        Assert.Equal(AgentRenderStates.HideTitle | AgentRenderStates.Autopilot, Assert.Single(sent).Flags);
    }

    [Fact]
    public void The_head_rotation_is_the_cameras_in_mouse_look_and_left_to_the_body_otherwise()
    {
        var (driver, sent, _) = Placed();
        driver.Frame(0.016, 1 / 60.0, new Vector3(97, 100, 23), new Vector3(101, 100, 23));
        Assert.Null(Assert.Single(sent).HeadRotation);   // orbiting the camera does not turn the head: the official head rotation outside mouse-look is the avatar's
        driver.Frame(0.1, 1 / 60.0, new Vector3(97, 100, 23), new Vector3(101, 105, 23));
        Assert.All(sent, u => Assert.Null(u.HeadRotation));

        driver.Input.EnterMouseLook();
        driver.Frame(0.2, 1 / 60.0, new Vector3(100, 100, 23), new Vector3(100, 100 + 4 * MathF.Cos(0.5f), 23 + 4 * MathF.Sin(0.5f)));
        var last = sent[^1];
        Assert.NotNull(last.HeadRotation);
        var forward = Vector3.Transform(Vector3.UnitX, last.HeadRotation!.Value);
        Near(Vector3.Normalize(new Vector3(0, MathF.Cos(0.5f), MathF.Sin(0.5f))), forward);
    }

    [Fact]
    public void An_update_the_session_refuses_is_tried_again_on_the_next_frame()
    {
        var (driver, sent, accept) = Placed();
        accept(false);
        driver.Frame(0.016, 1 / 60.0, new Vector3(97, 100, 23), new Vector3(101, 100, 23));
        Assert.Empty(sent);
        accept(true);
        driver.Frame(0.033, 1 / 60.0, new Vector3(97, 100, 23), new Vector3(101, 100, 23));
        Assert.Single(sent);   // not held back for a second as if the refused one had gone
    }

    // ---------------- the session: never during a teleport ----------------

    private sealed class CountingTransport : ISessionTransport, IAgentControlTransport
    {
        public event Action<TransportEvent>? Event;
        public readonly List<AgentUpdate> Updates = new();
        public int FarSends;
        public SimRef? CurrentSim { get; set; }
        public int ConnectedSimulatorCount { get; set; }
        public Task<TransportLoginResult> LoginAsync(TransportLoginRequest request, CancellationToken ct) => Task.FromResult(FakeTransport.Ok(Guid.NewGuid()));
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
        public void SetDrawDistance(float metres) => FarSends++;
        public void SendAgentUpdate(AgentUpdate update) => Updates.Add(update);
        public void SendAlwaysRun(bool run) { }
        public void Dispose() { }
        public void Raise(TransportEvent e) => Event?.Invoke(e);
    }

    private static (ViewerSession S, CountingTransport T) InWorld()
    {
        var t = new CountingTransport();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid:8002/" }, t, new CoreLog(new ListSink(), "test"));
        Assert.True(s.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None).GetAwaiter().GetResult().Ok);
        var sim = new SimRef(256000UL << 32 | 256000UL, "Here");
        t.CurrentSim = sim;
        t.ConnectedSimulatorCount = 1;
        t.Raise(new SimConnected(sim, 256, 256));
        t.Raise(new EventQueueRunning(sim));
        t.Raise(new MovementComplete(sim));
        return (s, t);
    }

    [Theory]
    [InlineData(TeleportPhase.Finished)]
    [InlineData(TeleportPhase.Local)]
    [InlineData(TeleportPhase.Failed)]
    [InlineData(TeleportPhase.Cancelled)]
    public void No_agent_update_goes_while_a_teleport_is_in_progress_and_they_resume_when_it_ends(TeleportPhase end)
    {
        var (s, t) = InWorld();
        var update = U(F.AtPos);
        Assert.True(s.SendAgentUpdate(update));
        t.Raise(new TeleportProgressed(TeleportPhase.Started, "", true));
        Assert.False(s.SendAgentUpdate(update));
        t.Raise(new TeleportProgressed(TeleportPhase.Progress, "moving", true));
        Assert.False(s.SendAgentUpdate(update));
        Assert.Single(t.Updates);   // only the one from before it started
        t.Raise(new TeleportProgressed(end, "", false));
        Assert.True(s.SendAgentUpdate(update));
        Assert.Equal(2, t.Updates.Count);
        s.Dispose();
    }

    [Fact]
    public void The_far_distance_is_not_sent_while_a_teleport_is_in_progress_and_goes_after_it()
    {
        var (s, t) = InWorld();
        s.AdvanceFar();
        int before = t.FarSends;
        t.Raise(new TeleportProgressed(TeleportPhase.Started, "", true));
        s.SetDrawDistance(100);
        s.AdvanceFar();
        Assert.Equal(before, t.FarSends);
        t.Raise(new TeleportProgressed(TeleportPhase.Finished, "", false));
        s.AdvanceFar();
        Assert.True(t.FarSends > before, "the far distance goes once the teleport has ended");
        s.Dispose();
    }

    // ---------------- the transport: one sender, a copy under the lock, the message as built ----------------

    private static LmvTransport NewTransport() => new(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "agent-update"));

    [Fact]
    public void The_transport_has_no_timer_of_its_own_that_resends_the_update()
    {
        var type = typeof(LmvTransport);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        Assert.Null(type.GetMethod("SendPeriodicUpdate", flags));
        Assert.DoesNotContain(type.GetFields(flags), f => f.FieldType == typeof(System.Threading.Timer) && f.Name.Contains("eriodic", StringComparison.Ordinal));
    }

    [Fact]
    public void The_socket_is_never_written_to_while_the_movement_lock_is_held()
    {
        using var transport = NewTransport();
        bool held = false;
        int probes = 0;
        transport.SendProbe = _ => { probes++; held |= Monitor.IsEntered(transport.MovementLockForTests); };
        transport.SendAgentUpdate(new AgentUpdate(F.AtPos, Quaternion.Identity, Vector3.Zero, Vector3.UnitX));
        transport.SetDrawDistance(128);
        Assert.Equal(2, probes);
        Assert.False(held, "an update was sent with the movement lock held");
    }

    [Fact]
    public void The_agent_update_trace_is_off_until_switched_on_and_then_writes_one_line_per_update()
    {
        var sink = new ListSink();
        using var transport = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(sink, "agent-update"));
        var packets = new List<AgentUpdatePacket>();
        transport.PacketProbe = packets.Add;
        var update = new AgentUpdate(F.AtPos, Quaternion.Identity, Vector3.Zero, Vector3.UnitX, null, AgentRenderStates.Typing, 0);
        transport.SendAgentUpdate(update);
        Assert.DoesNotContain("agent.update.sent", sink.Text);
        ((INeighbourControl)transport).SetAgentUpdateTrace(true);
        transport.SendAgentUpdate(update);
        transport.SendAgentUpdate(update);
        // (an unconnected transport writes nothing to a socket, so the trace is made where the message is built too)
        Assert.Equal(2, sink.Text.Split("agent.update.sent").Length - 1);
        Assert.Contains("state=4", sink.Text);
        Assert.Contains("head_body_dot=1", sink.Text);
        ((INeighbourControl)transport).SetAgentUpdateTrace(false);
        transport.SendAgentUpdate(update);
        Assert.Equal(2, sink.Text.Split("agent.update.sent").Length - 1);
    }

    [Fact]
    public void The_log_says_when_agent_updates_are_held_for_a_teleport_and_when_they_go_again()
    {
        var sink = new ListSink();
        var t = new CountingTransport();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid:8002/" }, t, new CoreLog(sink, "test"));
        Assert.True(s.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None).GetAwaiter().GetResult().Ok);
        t.Raise(new TeleportProgressed(TeleportPhase.Started, "", true));
        t.Raise(new TeleportProgressed(TeleportPhase.Progress, "", true));
        Assert.Equal(1, sink.Text.ToLowerInvariant().Split("agent.updates sending=false").Length - 1);
        t.Raise(new TeleportProgressed(TeleportPhase.Finished, "", false));
        Assert.Equal(1, sink.Text.ToLowerInvariant().Split("agent.updates sending=true").Length - 1);
        s.Dispose();
    }

    [Fact]
    public void The_message_carries_the_head_rotation_the_state_and_the_flags_it_was_given()
    {
        using var transport = NewTransport();
        var packets = new List<AgentUpdatePacket>();
        transport.PacketProbe = packets.Add;
        var body = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f);
        var head = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.4f) * body;
        transport.SendAgentUpdate(new AgentUpdate(F.AtPos, body, Vector3.Zero, Vector3.UnitX, head, AgentRenderStates.Typing, AgentRenderStates.HideTitle));
        transport.SendAgentUpdate(new AgentUpdate(0, body, Vector3.Zero, Vector3.UnitX));   // no head given: the head follows the body; no state: none
        Assert.Equal(2, packets.Count);
        Assert.Equal(AgentRenderStates.Typing, packets[0].AgentData.State);
        Assert.Equal(AgentRenderStates.HideTitle, packets[0].AgentData.Flags);
        Assert.Equal(F.AtPos, packets[0].AgentData.ControlFlags);
        Assert.True(Math.Abs(Quaternion.Dot(head, new Quaternion(packets[0].AgentData.HeadRotation.X, packets[0].AgentData.HeadRotation.Y, packets[0].AgentData.HeadRotation.Z, packets[0].AgentData.HeadRotation.W))) > 0.9999f);
        Assert.True(Math.Abs(Quaternion.Dot(body, new Quaternion(packets[1].AgentData.HeadRotation.X, packets[1].AgentData.HeadRotation.Y, packets[1].AgentData.HeadRotation.Z, packets[1].AgentData.HeadRotation.W))) > 0.9999f);
        Assert.Equal(0, packets[1].AgentData.State);
        Assert.Equal(0, packets[1].AgentData.Flags);
    }
}
