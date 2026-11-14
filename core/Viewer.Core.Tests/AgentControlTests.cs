// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;
using F = Viewer.Motion.AgentControlFlags;

namespace Viewer.Core.Tests;

/// <summary>The user's own avatar moved from the keyboard, engine-free: the control bits each key gives, the turn, fly
/// and landing, the focus rule, the update rate, the bindings, the drawn heading and the camera that follows it, and a
/// walking avatar's region updates drawn smoothly. Every class member is independent (no process-wide state), so the
/// class runs in parallel with the others.</summary>
public class AgentControlTests
{
    private const double Frame60 = 1.0 / 60;

    /// <summary>Holds <paramref name="cmds"/> from 0 for <paramref name="seconds"/> at 60 frames a second; the last frame's bits.</summary>
    private static uint Hold(AgentControlInput input, double seconds, params MoveCommand[] cmds)
    {
        foreach (var c in cmds) input.Press(c, 0);
        uint bits = 0;
        for (double t = Frame60; t <= seconds + 1e-9; t += Frame60) bits = input.Frame(t, Frame60, false, 0);
        return bits;
    }

    // ---------------- the bits each key gives ----------------

    [Fact]
    public void The_control_bits_have_the_values_the_protocol_defines()
    {
        // the official viewer's indra_constants.h, AGENT_CONTROL_*
        Assert.Equal(0x00000001u, F.AtPos);
        Assert.Equal(0x00000002u, F.AtNeg);
        Assert.Equal(0x00000004u, F.LeftPos);
        Assert.Equal(0x00000008u, F.LeftNeg);
        Assert.Equal(0x00000010u, F.UpPos);
        Assert.Equal(0x00000020u, F.UpNeg);
        Assert.Equal(0x00000100u, F.YawPos);
        Assert.Equal(0x00000200u, F.YawNeg);
        Assert.Equal(0x00000400u, F.FastAt);
        Assert.Equal(0x00000800u, F.FastLeft);
        Assert.Equal(0x00001000u, F.FastUp);
        Assert.Equal(0x00002000u, F.Fly);
        Assert.Equal(0x00004000u, F.Stop);
        Assert.Equal(0x00080000u, F.NudgeAtPos);
        Assert.Equal(0x00100000u, F.NudgeAtNeg);
        Assert.Equal(0x00200000u, F.NudgeLeftPos);
        Assert.Equal(0x00400000u, F.NudgeLeftNeg);
    }

    [Fact]
    public void The_protocol_library_sends_exactly_the_bits_it_is_given()
    {
        using var client = new LibreMetaverse.GridClient();
        var mv = client.Self.Movement;
        foreach (uint bit in F.All)
        {
            Viewer.Core.Protocol.LmvTransport.ApplyControls(mv, bit);
            Assert.Equal(bit, mv.AgentControls);
        }
        uint walkFly = F.AtPos | F.FastAt | F.Fly | F.YawPos;
        Viewer.Core.Protocol.LmvTransport.ApplyControls(mv, walkFly);
        Assert.Equal(walkFly, mv.AgentControls);
        Viewer.Core.Protocol.LmvTransport.ApplyControls(mv, 0);
        Assert.Equal(0u, mv.AgentControls);
    }

    [Theory]
    [InlineData("W", false, F.AtPos | F.FastAt)]
    [InlineData("UP", false, F.AtPos | F.FastAt)]
    [InlineData("S", false, F.AtNeg | F.FastAt)]
    [InlineData("DOWN", false, F.AtNeg | F.FastAt)]
    [InlineData("A", false, F.YawPos)]
    [InlineData("LEFT", false, F.YawPos)]
    [InlineData("D", false, F.YawNeg)]
    [InlineData("RIGHT", false, F.YawNeg)]
    [InlineData("A", true, F.LeftPos | F.FastLeft)]
    [InlineData("LEFT", true, F.LeftPos | F.FastLeft)]
    [InlineData("D", true, F.LeftNeg | F.FastLeft)]
    [InlineData("RIGHT", true, F.LeftNeg | F.FastLeft)]
    [InlineData("E", false, F.UpPos | F.FastUp)]
    [InlineData("PGUP", false, F.UpPos | F.FastUp)]
    [InlineData("C", false, F.UpNeg | F.FastUp)]
    [InlineData("PGDN", false, F.UpNeg | F.FastUp)]
    [InlineData("SPACE", false, F.Stop)]
    [InlineData("F", false, F.Fly)]
    [InlineData("HOME", false, F.Fly)]
    public void Each_default_key_held_past_the_nudge_gives_the_official_bits(string key, bool shift, uint expected)
    {
        Assert.True(KeyBindings.Default.TryCommand(key, shift, out var cmd));
        var input = new AgentControlInput();
        input.AdoptHeading(0);
        // held 0.4 s: past the nudge, short of the jump's fly time
        Assert.Equal(expected, Hold(input, 0.4, cmd));
    }

    [Fact]
    public void Forward_back_and_the_slides_nudge_first_then_move()
    {
        foreach (var (cmd, nudge, move) in new[]
        {
            (MoveCommand.Forward, F.NudgeAtPos, F.AtPos | F.FastAt), (MoveCommand.Backward, F.NudgeAtNeg, F.AtNeg | F.FastAt),
            (MoveCommand.SlideLeft, F.NudgeLeftPos, F.LeftPos | F.FastLeft), (MoveCommand.SlideRight, F.NudgeLeftNeg, F.LeftNeg | F.FastLeft),
        })
        {
            var input = new AgentControlInput();
            input.Press(cmd, 0);
            Assert.Equal(nudge, input.Frame(0.1, 0.1, false, 0));    // inside the 0.25 s nudge
            Assert.Equal(nudge, input.Frame(0.24, 0.14, false, 0));
            Assert.Equal(move, input.Frame(0.26, 0.02, false, 0));   // past it (and past its two frames)
            input.Release(cmd);
            Assert.Equal(0u, input.Frame(0.3, 0.04, false, 0));    // the release: the bits clear and nothing else goes out
            Assert.Equal(0u, input.Frame(0.41, 0.11, false, 0));
        }

        // a slow frame rate: the time has passed but not the frames
        var slow = new AgentControlInput();
        slow.Press(MoveCommand.Forward, 0);
        Assert.Equal(F.NudgeAtPos, slow.Frame(0.5, 0.5, false, 0));
        Assert.Equal(F.NudgeAtPos, slow.Frame(1.0, 0.5, false, 0));
        Assert.Equal(F.AtPos | F.FastAt, slow.Frame(1.5, 0.5, false, 0));
    }

    [Fact]
    public void Keys_combine_and_a_held_key_pressed_again_keeps_its_start()
    {
        var input = new AgentControlInput();
        input.AdoptHeading(0);
        input.Press(MoveCommand.Forward, 0);
        input.Press(MoveCommand.TurnLeft, 0);
        input.Press(MoveCommand.ToggleFly, 0);
        uint bits = 0;
        for (double t = Frame60; t <= 0.5; t += Frame60)
        {
            input.Press(MoveCommand.Forward, t);   // the keyboard's repeat
            bits = input.Frame(t, Frame60, true, 0);
        }
        Assert.Equal(F.AtPos | F.FastAt | F.YawPos | F.Fly, bits);
    }

    // ---------------- turning ----------------

    [Fact]
    public void Turning_ramps_to_ninety_degrees_a_second_and_turns_the_heading_here()
    {
        var input = new AgentControlInput();
        input.AdoptHeading(0);
        Hold(input, 1.0, MoveCommand.TurnLeft);
        // the first 0.25 s ramp from 5 % to all of the rate (0.25 x 0.525 = 0.13125), then 0.75 s at the full rate
        float expected = (0.13125f + 0.75f) * MathF.PI / 2;
        Assert.InRange(input.Heading!.Value, expected - 0.02f, expected + 0.02f);

        var right = new AgentControlInput();
        right.AdoptHeading(0);
        Hold(right, 1.0, MoveCommand.TurnRight);
        Assert.InRange(right.Heading!.Value, -expected - 0.02f, -expected + 0.02f);

        Assert.Equal(AgentControlInput.TurnNudgeRate, AgentControlInput.YawRate(0));
        Assert.Equal(1f, AgentControlInput.YawRate(0.25));
    }

    [Fact]
    public void Both_turn_keys_cancel_the_turn_but_both_bits_are_sent()
    {
        var input = new AgentControlInput();
        input.AdoptHeading(1f);
        Assert.Equal(F.YawPos | F.YawNeg, Hold(input, 0.5, MoveCommand.TurnLeft, MoveCommand.TurnRight));
        Assert.Equal(1f, input.Heading);
    }

    [Fact]
    public void The_heading_wraps_and_the_body_rotation_faces_it()
    {
        var input = new AgentControlInput();
        input.AdoptHeading(MathF.PI - 0.01f);
        Hold(input, 0.5, MoveCommand.TurnLeft);
        Assert.InRange(input.Heading!.Value, -MathF.PI, MathF.PI);
        Assert.True(input.Heading < 0);   // past +180 degrees, it reads as negative

        var q = AgentControlInput.BodyRotation(MathF.PI / 2);
        var forward = Vector3.Transform(Vector3.UnitX, q);
        Assert.InRange(forward.Y, 0.999f, 1.001f);   // facing north
        Assert.Equal(MathF.PI / 2, AvatarMotion.Yaw(q), 3);
    }

    // ---------------- jumping, flying and landing ----------------

    [Fact]
    public void A_short_jump_stays_on_the_ground_and_a_long_one_starts_flying()
    {
        var tap = new AgentControlInput();
        Assert.Equal(F.UpPos | F.FastUp, Hold(tap, 0.3, MoveCommand.Jump));
        tap.Release(MoveCommand.Jump);
        Assert.Equal(0u, tap.Frame(0.4, 0.1, false, 0));
        Assert.False(tap.Flying);

        var hold = new AgentControlInput();
        Assert.Equal(F.UpPos | F.FastUp | F.Fly, Hold(hold, 0.6, MoveCommand.Jump));
        hold.Release(MoveCommand.Jump);
        Assert.Equal(F.Fly, hold.Frame(0.7, 0.1, true, 0));   // flying stays after the key; no stop bit for the release
    }

    [Fact]
    public void Fly_toggles_on_each_press_and_down_on_the_ground_lands()
    {
        var input = new AgentControlInput();
        input.Press(MoveCommand.ToggleFly, 0);
        Assert.Equal(F.Fly, input.Frame(0.1, 0.1, true, 0));
        input.Press(MoveCommand.ToggleFly, 0.2);
        Assert.Equal(0u, input.Frame(0.3, 0.1, false, 0));
        input.Press(MoveCommand.ToggleFly, 0.4);

        // down while still in the air keeps flying (hovering down)
        input.Press(MoveCommand.Down, 0.5);
        Assert.Equal(F.Fly | F.UpNeg | F.FastUp, input.Frame(0.6, 0.1, true, 0));
        // on the ground but moving fast across it: still flying
        Assert.Equal(F.Fly | F.UpNeg | F.FastUp, input.Frame(0.7, 0.1, false, 5));
        // on the ground and slow: lands
        Assert.Equal(F.UpNeg | F.FastUp, input.Frame(0.8, 0.1, false, 1));
        Assert.False(input.Flying);
    }

    [Fact]
    public void The_regions_word_on_flying_is_taken_at_the_start_and_the_air_is_read_from_its_animations()
    {
        var hover = Guid.Parse("4ae8016b-31b9-03bb-c401-b1ea941db41d");
        var land = Guid.Parse("7a17b059-12b2-41b1-570a-186368b6aa6f");
        var jump = Guid.Parse("2305bd75-1ca9-b03b-1faa-b176b8a8c49e");
        Assert.True(AgentAnimations.IsFlying([new(hover, 1)]));
        Assert.True(AgentAnimations.InAir([new(hover, 1)]));
        Assert.True(AgentAnimations.InAir([new(jump, 1)]));
        Assert.False(AgentAnimations.IsFlying([new(jump, 1)]));
        Assert.False(AgentAnimations.InAir([new(land, 1), new(AnimationChoice.Stand, 2)]));
        Assert.Equal(["land", "walk"], AgentAnimations.NamesOf([new(land, 1), new(AnimationChoice.Walk, 2)]));

        var input = new AgentControlInput();
        input.AdoptFlying(true);
        Assert.Equal(F.Fly, input.Frame(0.1, 0.1, true, 0));
    }

    // ---------------- focus ----------------

    [Fact]
    public void Keys_count_only_with_focus_and_losing_it_releases_every_held_key()
    {
        var input = new AgentControlInput();
        input.AdoptHeading(0);
        input.Press(MoveCommand.ToggleFly, 0);
        input.Press(MoveCommand.Forward, 0);
        input.Press(MoveCommand.TurnLeft, 0);
        uint bits = 0;
        for (double t = 0.1; t <= 0.6 + 1e-9; t += 0.1) bits = input.Frame(t, 0.1, true, 0);
        Assert.Equal(F.Fly | F.AtPos | F.FastAt | F.YawPos, bits);

        input.SetFocused(false);
        float heading = input.Heading!.Value;
        Assert.Equal(F.Fly, input.Frame(0.7, 0.1, true, 0));   // the held keys are gone, with no stop bit; flying (persistent) stays
        Assert.Equal(heading, input.Heading);
        input.Press(MoveCommand.Forward, 0.8);   // a key event without focus is ignored
        input.Press(MoveCommand.ToggleFly, 0.8);
        Assert.Equal(F.Fly, input.Frame(0.9, 0.1, true, 0));

        // the automation API presses as a focused window would
        input.Press(MoveCommand.Forward, 1.0, synthetic: true);
        Assert.Equal(F.Fly | F.NudgeAtPos, input.Frame(1.1, 0.1, true, 0));

        input.SetFocused(true);
        input.Press(MoveCommand.Backward, 1.2);
        Assert.True(input.IsHeld(MoveCommand.Backward));
    }

    // ---------------- when updates are sent ----------------

    private static AgentUpdate U(uint bits, float heading = 0, Vector3? eye = null, Vector3? target = null) =>
        new(bits, AgentControlInput.BodyRotation(heading), eye ?? new(100, 100, 22), target ?? new(104, 100, 22));

    [Fact]
    public void A_change_of_controls_is_sent_at_once_and_followed_up_once()
    {
        var rule = new AgentUpdateRule();
        Assert.True(rule.ShouldSend(U(0), 0));
        Assert.False(rule.ShouldSend(U(0), 0.001));
        Assert.True(rule.ShouldSend(U(F.AtPos), 0.002));    // a change goes at once, even inside the 8 ms
        Assert.True(rule.ShouldSend(U(F.AtPos), 0.011));    // the follow-up after it
        Assert.False(rule.ShouldSend(U(F.AtPos), 0.02));    // then nothing while nothing changes
        Assert.False(rule.ShouldSend(U(F.AtPos), 0.9));
        Assert.True(rule.ShouldSend(U(F.AtPos), 1.03));     // the keep-alive, more than a second after the last
        Assert.Equal(4, rule.Sent);
    }

    [Fact]
    public void Camera_and_body_changes_are_sent_no_more_than_125_times_a_second()
    {
        var rule = new AgentUpdateRule();
        Assert.True(rule.ShouldSend(U(F.AtPos), 0));
        Assert.True(rule.ShouldSend(U(F.AtPos), 0.0081));   // the follow-up
        int sent = 0;
        // the camera moving 5 cm every 1 ms for a second: at most one update per 8 ms
        for (int i = 1; i <= 1000; i++)
        {
            double t = 0.0081 + i * 0.001;
            if (rule.ShouldSend(U(F.AtPos, eye: new Vector3(100 + i * 0.05f, 100, 22), target: new Vector3(104 + i * 0.05f, 100, 22)), t)) sent++;
        }
        Assert.InRange(sent, 100, 125);

        // below the thresholds: a 5 mm camera move and a 1 degree body turn send nothing
        var still = new AgentUpdateRule();
        Assert.True(still.ShouldSend(U(0), 0));
        Assert.False(still.ShouldSend(U(0, eye: new Vector3(100.005f, 100, 22), target: new Vector3(104.005f, 100, 22)), 0.1));
        Assert.False(still.ShouldSend(U(0, heading: MathF.PI / 180), 0.2));
        // a 3 degree body turn does
        Assert.True(still.ShouldSend(U(0, heading: 3 * MathF.PI / 180), 0.3));
    }

    // ---------------- the bindings file ----------------

    [Fact]
    public void A_bindings_file_replaces_the_defaults_and_an_unusable_one_is_refused_with_its_reason()
    {
        var read = KeyBindings.Parse("""
            { "bindings": [
              { "key": "i", "mask": "NONE", "command": "push_forward" },
              { "key": "J", "mask": "SHIFT", "command": "slide_left" },
              { "key": "K", "mask": "ALT", "command": "push_backward" },
              { "key": "L", "mask": "NONE", "command": "no_such_command" }
            ] }
            """, out var why);
        Assert.NotNull(read);
        Assert.Null(why);
        Assert.Equal(2, read.Count);
        Assert.True(read.TryCommand("I", false, out var c) && c == MoveCommand.Forward);
        Assert.True(read.TryCommand("j", true, out c) && c == MoveCommand.SlideLeft);
        Assert.False(read.TryCommand("J", false, out _));
        Assert.False(read.TryCommand("W", false, out _));   // the file replaces the defaults

        Assert.Null(KeyBindings.Parse("not json", out why));
        Assert.Equal("not JSON", why);
        Assert.Null(KeyBindings.Parse("""{ "keys": [] }""", out why));
        Assert.Equal("no bindings array", why);
        Assert.Null(KeyBindings.Parse("""{ "bindings": [ { "key": "Q", "command": "dance" } ] }""", out why));
        Assert.Equal("no usable binding", why);
    }

    [Fact]
    public void A_missing_or_unusable_bindings_file_leaves_the_defaults_and_says_so()
    {
        var sink = new ListSink();
        var log = new CoreLog(sink, "test");
        Assert.Same(KeyBindings.Default, KeyBindings.Load(null, log));
        string missing = Path.Combine(TestTemp.NewFolderPath("keys"), "keys.json");   // a unique folder never created
        Assert.Same(KeyBindings.Default, KeyBindings.Load(missing, log));
        Assert.Contains("keys.file_unreadable", sink.Text);
        Assert.Equal(19, KeyBindings.Default.Count);
    }

    // ---------------- the own avatar: heading, camera, smoothing ----------------

    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private sealed class Clock { public double Now; }

    private static AvatarData Self(Vector3 pos, Vector3 vel, float heading, uint seat = 0) =>
        new(800, ServerAppearanceTests.Me, "Own Avatar", pos, AgentControlInput.BodyRotation(heading), new Vector3(0.45f, 0.6f, 1.9f), vel, seat);

    private static (FakeTransport T, AvatarScene Scene, OwnAvatarDriver Driver, Clock Clock, List<WorldChange> Changes, List<AgentUpdate> Sent) Own()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var clock = new Clock();
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => clock.Now);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        var sent = new List<AgentUpdate>();
        var driver = new OwnAvatarDriver(scene, s.World, u => { sent.Add(u); return true; });
        return (t, scene, driver, clock, changes, sent);
    }

    private static void Pump(AvatarScene scene, List<WorldChange> changes)
    {
        foreach (var c in changes.ToList()) scene.Apply(c);
        changes.Clear();
    }

    [Fact]
    public void Nothing_is_sent_before_the_own_avatar_is_placed_and_its_heading_starts_as_the_regions()
    {
        var (t, scene, driver, clock, changes, sent) = Own();
        driver.Input.Press(MoveCommand.Forward, 0);
        driver.Frame(0, 0, Vector3.Zero, Vector3.UnitX);
        Assert.Empty(sent);
        Assert.Null(scene.SelfHeading);

        t.Raise(new AvatarUpdated(Here, Self(new(100, 100, 21.95f), Vector3.Zero, 1.2f)));
        Pump(scene, changes);
        driver.Frame(0.016, 0.016, new(97, 100, 23), new(101, 100, 23));
        Assert.Equal(1.2f, driver.Input.Heading!.Value, 4);
        var u = Assert.Single(sent);
        Assert.Equal(F.NudgeAtPos, u.Controls);
        Assert.Equal(1.2f, AvatarMotion.Yaw(u.BodyRotation), 4);
        Assert.Equal(new Vector3(97, 100, 23), u.CameraEye);
    }

    [Fact]
    public void The_turn_is_drawn_at_once_and_the_camera_follows_it_keeping_its_orbit_and_zoom()
    {
        var (t, scene, driver, clock, changes, sent) = Own();
        t.Raise(new AvatarUpdated(Here, Self(new(100, 100, 21.95f), Vector3.Zero, 0)));
        Pump(scene, changes);
        var camera = new FollowCamera();
        camera.SetOrbit(0.3f, 0);
        camera.SetZoom(2f);

        driver.Input.Press(MoveCommand.TurnLeft, 0);
        Vector3 eye = default, target = default;
        for (int i = 1; i <= 60; i++)
        {
            clock.Now = i * Frame60;
            driver.Frame(clock.Now, Frame60, eye, target);
            Assert.True(scene.Place(scene.Self!, out var soles, out float yaw));
            Assert.Equal(driver.Input.Heading!.Value, yaw);   // drawn as turned here, before the region says so
            (eye, target) = camera.Pose(soles + new Vector3(0, 0, 0.95f), yaw, Frame60);
        }
        float heading = driver.Input.Heading!.Value;
        Assert.InRange(heading, 1.3f, 1.45f);   // about 79 degrees after a second
        // the camera's smoothing trails a turning avatar by a frame or so; a moment later it is where the turn puts it
        Assert.True(scene.Place(scene.Self!, out var restSoles, out float restYaw));
        (eye, target) = camera.Pose(restSoles + new Vector3(0, 0, 0.95f), restYaw, 1.0);
        // the camera sits behind the turned heading plus its orbit, at its zoomed distance
        var back = Vector3.Normalize(new Vector3(eye.X - 100, eye.Y - 100, 0));
        float cameraAngle = MathF.Atan2(-back.Y, -back.X);
        Assert.Equal(heading + 0.3f, cameraAngle, 2);
        Assert.Equal(0.3f, camera.OrbitYaw, 4);
        Assert.Equal(2f, camera.Zoom);
        // and the region was told: the last update sent carries the turned body
        Assert.Equal(heading, AvatarMotion.Yaw(sent[^1].BodyRotation), 1);
        Assert.Equal(F.YawPos, sent[^1].Controls);
    }

    [Fact]
    public void A_region_change_or_a_sit_takes_the_regions_heading_again_and_a_seated_avatar_is_drawn_as_the_region_says()
    {
        var (t, scene, driver, clock, changes, sent) = Own();
        t.Raise(new AvatarUpdated(Here, Self(new(100, 100, 21.95f), Vector3.Zero, 0)));
        Pump(scene, changes);
        driver.Input.Press(MoveCommand.TurnRight, 0);
        for (int i = 1; i <= 30; i++) driver.Frame(i * Frame60, Frame60, default, default);
        driver.Input.Release(MoveCommand.TurnRight);
        Assert.True(driver.Input.Heading < -0.1f);

        // the region seats the avatar: its heading is the region's again
        t.Raise(new AvatarUpdated(Here, Self(new(0, 0, 0.5f), Vector3.Zero, 2f, seat: 0)));
        Pump(scene, changes);
        t.Raise(new AvatarUpdated(Here, Self(new(0, 0, 0.5f), Vector3.Zero, 2f, seat: 5000)));
        Pump(scene, changes);
        driver.Frame(1, Frame60, default, default);
        Assert.Equal(2f, driver.Input.Heading!.Value, 4);
    }

    [Fact]
    public void A_walking_own_avatars_region_updates_are_drawn_smoothly()
    {
        // the region's updates for an avatar walking at 3.2 m/s and turning: about ten a second, arriving with up to
        // 40 ms of jitter, each a little off the straight line (the region's own physics steps)
        var (t, scene, driver, clock, changes, sent) = Own();
        var rng = new Random(11);
        const float speed = 3.2f;
        var regionPos = new Vector3(100, 100, 21.95f);
        float regionHeading = 0;
        double nextUpdate = 0, lastUpdate = 0;
        Vector3? lastDrawn = null;
        float worstStep = 0;
        int updates = 0;
        driver.Input.Press(MoveCommand.Forward, 0);
        for (int frame = 0; frame < 600; frame++)   // ten seconds at 60 frames a second
        {
            double now = frame * Frame60;
            clock.Now = now;
            if (frame == 180) driver.Input.Press(MoveCommand.TurnLeft, now);
            if (frame == 240) driver.Input.Release(MoveCommand.TurnLeft);
            if (now >= nextUpdate)
            {
                // the region turns a round trip (about 50 ms) behind the heading sent to it
                regionHeading = driver.Last is { } last ? AvatarMotion.Yaw(last.BodyRotation) : 0;
                var dir = new Vector3(MathF.Cos(regionHeading), MathF.Sin(regionHeading), 0);
                regionPos += dir * speed * (float)(now - lastUpdate) + new Vector3((float)(rng.NextDouble() - 0.5) * 0.04f, (float)(rng.NextDouble() - 0.5) * 0.04f, 0);
                if (updates == 0) t.Raise(new AvatarUpdated(Here, Self(regionPos, dir * speed, regionHeading)));
                else t.Raise(new ObjectMoved(Here, 800, true, regionPos, AgentControlInput.BodyRotation(regionHeading), dir * speed, Vector3.Zero, Vector3.Zero));
                updates++;
                lastUpdate = now;
                nextUpdate = now + 0.1 + (rng.NextDouble() - 0.5) * 0.08;
            }
            Pump(scene, changes);
            driver.Frame(now, Frame60, default, default);
            if (scene.Self is null || !scene.Place(scene.Self, out var soles, out _)) continue;
            if (lastDrawn is { } prev) worstStep = MathF.Max(worstStep, (soles - prev).Length());
            lastDrawn = soles;
        }
        Assert.InRange(updates, 85, 115);
        // a frame's step at 3.2 m/s is 5.3 cm; corrections spread over 0.2 s keep every step under twice that
        Assert.True(worstStep < 2 * speed * Frame60, $"worst step {worstStep:F3} m");
        Assert.Equal(0, scene.Self!.Motion.Snaps - 1);   // only the first placement was taken at once
    }
}
