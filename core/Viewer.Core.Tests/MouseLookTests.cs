// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;
using Xunit.Abstractions;
using F = Viewer.Motion.AgentControlFlags;
using Look = Viewer.Core.Tests.MovementRig.Look;
using LookKind = Viewer.Core.Tests.MovementRig.LookKind;

namespace Viewer.Core.Tests;

/// <summary>
/// Mouse-look, with no login and no engine: the mouse sensitivity and smoothing, the turn and tilt, the bits sent
/// (the mouse-look bit, the left button for scripts that take it), what scripts' taken controls change, the keys,
/// what enters and leaves it, the camera at the eye, and the own avatar left out of the picture. The behaviour is the
/// official viewer's (lltoolgun.cpp, llagent.cpp, llagentcamera.cpp, llviewerinput.cpp); the expected numbers below are
/// written out from the rules there, not from our code. Every member builds its own objects: no process-wide state, so
/// the class runs in parallel with the others.
/// </summary>
public class MouseLookTests(ITestOutputHelper output)
{
    private const double Frame60 = 1.0 / 60;

    private static AgentControlInput Ready(float heading = 0)
    {
        var input = new AgentControlInput();
        input.AdoptHeading(heading);
        return input;
    }

    private static uint Step(AgentControlInput input, ref double now)
    {
        now += Frame60;
        return input.Frame(now, Frame60, false, 0);
    }

    // ---------------- the sensitivity and the smoothing ----------------

    [Theory]
    [InlineData(0f, 0.5f * 0.0025f)]
    [InlineData(3f, 0.002375f)]          // the default: 0.5 + 3/15 * 2.25 = 0.95 of the nominal 0.0025
    [InlineData(15f, 2.75f * 0.0025f)]
    [InlineData(7.5f, 1.625f * 0.0025f)]
    [InlineData(40f, 2.75f * 0.0025f)]   // beyond the range it stays at the ends
    [InlineData(-5f, 0.5f * 0.0025f)]
    public void The_sensitivity_setting_is_rescaled_as_the_official_viewer_does(float setting, float expected) =>
        Assert.Equal(expected, MouseLookRules.RadiansPerPixel(setting), 6);

    [Fact]
    public void The_defaults_are_the_official_viewers()
    {
        var input = new AgentControlInput();
        Assert.Equal(3f, input.MouseSensitivity);   // settings.xml: MouseSensitivity 3.0
        Assert.False(input.InvertMouse);            // InvertMouse 0
        Assert.False(input.SmoothMouse);            // MouseSmooth 0
        Assert.False(input.MouseLook);
    }

    [Fact]
    public void The_smoothing_eases_each_movement_into_a_running_value_rounded_to_pixels()
    {
        // llviewerwindow.cpp: f += (d - f) * min(frame time * 16, 1); the movement used is f rounded
        var smoother = new MouseSmoother();
        double fx = 0, fy = 0;
        float[] dxs = [100, 0, 0, 0, 0, 40, 0];
        foreach (float dx in dxs)
        {
            double k = Math.Min(Frame60 * 16, 1);
            fx += (dx - fx) * k;
            fy += (-dx / 2 - fy) * k;
            var got = smoother.Step(dx, -dx / 2, Frame60);
            Assert.Equal((int)Math.Floor(fx + 0.5), got.X);
            Assert.Equal((int)Math.Floor(fy + 0.5), got.Y);
        }
        // a long frame takes the whole movement at once (the factor is limited to 1)
        var coarse = new MouseSmoother();
        Assert.Equal((50, -20), coarse.Step(50, -20, 0.5));
    }

    [Fact]
    public void With_smoothing_off_the_mouse_moves_the_view_by_the_raw_movement_and_with_it_on_by_the_eased_one()
    {
        var raw = Ready();
        raw.EnterMouseLook();
        raw.LookBy(100, 0, Frame60);
        var eased = Ready();
        eased.SmoothMouse = true;
        eased.EnterMouseLook();
        eased.LookBy(100, 0, Frame60);   // the first frame of the easing takes 100 * 16/60 = 26.67, rounded to 27 pixels
        float per = MouseLookRules.RadiansPerPixel(3f);
        Assert.Equal(-100 * per, raw.Heading!.Value, 5);
        Assert.Equal(-27 * per, eased.Heading!.Value, 5);
    }

    // ---------------- turning and tilting ----------------

    [Fact]
    public void Moving_the_mouse_right_turns_the_avatar_to_the_right_and_left_to_the_left()
    {
        var input = Ready(0.5f);
        input.EnterMouseLook();
        float per = MouseLookRules.RadiansPerPixel(3f);
        input.LookBy(100, 0, Frame60);
        Assert.Equal(0.5f - 100 * per, input.Heading!.Value, 5);   // clockwise seen from above
        input.LookBy(-300, 0, Frame60);
        Assert.Equal(0.5f + 200 * per, input.Heading!.Value, 5);
    }

    [Fact]
    public void Moving_the_mouse_up_looks_up_and_down_looks_down_and_the_inverted_mouse_does_the_reverse()
    {
        float per = MouseLookRules.RadiansPerPixel(3f);
        var input = Ready();
        input.EnterMouseLook();
        input.LookBy(0, -100, Frame60);   // up the screen is a negative Y movement
        Assert.Equal(100 * per, input.Pitch, 5);
        input.LookBy(0, 150, Frame60);
        Assert.Equal(-50 * per, input.Pitch, 5);
        var inverted = Ready();
        inverted.InvertMouse = true;
        inverted.EnterMouseLook();
        inverted.LookBy(0, -100, Frame60);
        Assert.Equal(-100 * per, inverted.Pitch, 5);
    }

    [Fact]
    public void A_higher_sensitivity_turns_further_for_the_same_movement()
    {
        var slow = Ready();
        slow.MouseSensitivity = 0;
        slow.EnterMouseLook();
        slow.LookBy(100, 0, Frame60);
        var fast = Ready();
        fast.MouseSensitivity = 15;
        fast.EnterMouseLook();
        fast.LookBy(100, 0, Frame60);
        Assert.Equal(-100 * 0.5f * 0.0025f, slow.Heading!.Value, 5);
        Assert.Equal(-100 * 2.75f * 0.0025f, fast.Heading!.Value, 5);
    }

    [Fact]
    public void The_view_cannot_point_closer_than_5_degrees_to_straight_up_or_1_degree_to_straight_down()
    {
        var input = Ready();
        input.EnterMouseLook();
        input.LookBy(0, -100000, Frame60);
        Assert.Equal(85f * MathF.PI / 180, input.Pitch, 4);    // 5 degrees from the sky
        input.LookBy(0, 100000, Frame60);
        Assert.Equal(-89f * MathF.PI / 180, input.Pitch, 4);   // 179 degrees from the sky
    }

    [Fact]
    public void The_mouse_does_nothing_outside_mouse_look()
    {
        var input = Ready(0.25f);
        input.LookBy(200, -200, Frame60);
        Assert.Equal(0.25f, input.Heading!.Value);
        Assert.Equal(0f, input.Pitch);
    }

    [Fact]
    public void The_mouse_steers_the_avatar_even_when_a_script_took_the_turn_controls()
    {
        // the official mouse-look turns the frame with rotate(), which the taken turn controls do not stop (only yaw() is stopped)
        var script = new ScriptControls();
        script.Take(F.YawPos | F.YawNeg, passOn: false);
        var input = Ready();
        input.Script = script;
        input.EnterMouseLook();
        input.LookBy(100, 0, Frame60);
        Assert.NotEqual(0f, input.Heading!.Value);
    }

    // ---------------- the body rotation sent ----------------

    [Fact]
    public void The_body_rotation_carries_the_tilt_and_its_heading_is_still_the_headings()
    {
        float heading = 0.8f, pitch = 0.4f;
        var q = AgentControlInput.BodyRotation(heading, pitch);
        var forward = Vector3.Transform(Vector3.UnitX, q);
        Assert.Equal(MathF.Cos(pitch) * MathF.Cos(heading), forward.X, 4);
        Assert.Equal(MathF.Cos(pitch) * MathF.Sin(heading), forward.Y, 4);
        Assert.Equal(MathF.Sin(pitch), forward.Z, 4);   // up is positive
        Assert.Equal(heading, Viewer.Core.Avatars.AvatarMotion.Yaw(q), 4);
        Assert.Equal(AgentControlInput.BodyRotation(heading), AgentControlInput.BodyRotation(heading, 0f));
    }

    [Fact]
    public void The_updates_in_mouse_look_carry_the_tilt_and_the_ones_outside_it_are_level()
    {
        var run = MovementRig.Run([], 3.0, looks:
        [
            new Look(0.5, LookKind.Enter), new Look(1.0, LookKind.Move, 0, -120), new Look(2.0, LookKind.Leave),
        ]);
        var inside = run.SentBody.Where(s => s.At is > 1.0 and < 2.0).ToList();
        Assert.NotEmpty(inside);
        Assert.All(inside, s => Assert.True(Vector3.Transform(Vector3.UnitX, s.Body).Z > 0.2f, "looking up"));
        var after = run.SentBody.Where(s => s.At > 2.05).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, s => Assert.Equal(0f, Vector3.Transform(Vector3.UnitX, s.Body).Z, 4));
    }

    // ---------------- the bits ----------------

    [Fact]
    public void The_mouse_look_bit_goes_out_with_every_frame_while_it_lasts_and_not_after()
    {
        var input = Ready();
        double now = 0;
        Assert.Equal(0u, Step(input, ref now) & F.Mouselook);
        input.EnterMouseLook();
        for (int i = 0; i < 5; i++) Assert.Equal(F.Mouselook, Step(input, ref now) & F.Mouselook);
        input.Press(MoveCommand.Forward, now);
        Assert.Equal(F.Mouselook, Step(input, ref now) & F.Mouselook);
        input.LeaveMouseLook();
        Assert.Equal(0u, Step(input, ref now) & F.Mouselook);
    }

    [Fact]
    public void The_mouse_look_and_button_bits_have_the_values_the_protocol_defines()
    {
        Assert.Equal(0x00040000u, F.Mouselook);
        Assert.Equal(0x10000000u, F.LButtonDown);
        Assert.Equal(0x20000000u, F.LButtonUp);
        Assert.Equal(0x40000000u, F.MlLButtonDown);
        Assert.Equal(0x80000000u, F.MlLButtonUp);
    }

    [Fact]
    public void Entering_lets_go_of_the_turn_keys_and_they_do_nothing_in_mouse_look()
    {
        var input = Ready();
        double now = 0;
        input.Press(MoveCommand.TurnLeft, now);
        for (int i = 0; i < 10; i++) Step(input, ref now);
        Assert.NotEqual(0f, input.Heading!.Value);
        input.EnterMouseLook();
        Assert.False(input.IsHeld(MoveCommand.TurnLeft));
        float turned = input.Heading!.Value;
        input.Press(MoveCommand.TurnRight, now);   // not bound in mouse-look, but even asked for it does not turn
        uint bits = 0;
        for (int i = 0; i < 10; i++) bits = Step(input, ref now);
        Assert.Equal(turned, input.Heading!.Value);
        Assert.Equal(0u, bits & (F.YawPos | F.YawNeg));
    }

    [Fact]
    public void The_slide_keys_give_the_slide_bits_in_mouse_look()
    {
        var input = Ready();
        input.EnterMouseLook();
        input.Press(MoveCommand.SlideLeft, 0);
        double now = 0;
        for (int i = 0; i < 20; i++) Step(input, ref now);
        uint bits = Step(input, ref now);
        Assert.Equal(F.LeftPos | F.FastLeft, bits & (F.LeftPos | F.FastLeft | F.LeftNeg));
    }

    // ---------------- the left button and the controls scripts take ----------------

    [Fact]
    public void A_script_that_took_the_mouse_look_button_gets_one_down_and_one_up_bit()
    {
        var script = new ScriptControls();
        script.Take(F.MlLButtonDown, passOn: false);
        var input = Ready();
        input.Script = script;
        input.EnterMouseLook();
        double now = 0;
        Step(input, ref now);
        input.LeftButton(true);
        Assert.Equal(F.MlLButtonDown, Step(input, ref now) & (F.MlLButtonDown | F.MlLButtonUp | F.LButtonDown | F.LButtonUp));
        Assert.Equal(0u, Step(input, ref now) & (F.MlLButtonDown | F.MlLButtonUp));   // one update only
        input.LeftButton(false);
        Assert.Equal(F.MlLButtonUp, Step(input, ref now) & (F.MlLButtonDown | F.MlLButtonUp | F.LButtonDown | F.LButtonUp));
        Assert.Equal(0u, Step(input, ref now) & (F.MlLButtonDown | F.MlLButtonUp));
    }

    [Fact]
    public void A_button_nobody_wants_sends_no_button_bits()
    {
        var input = Ready();
        input.Script = new ScriptControls();
        input.EnterMouseLook();
        double now = 0;
        input.LeftButton(true);
        Assert.Equal(0u, Step(input, ref now) & (F.MlLButtonDown | F.MlLButtonUp | F.LButtonDown | F.LButtonUp));
        input.LeftButton(false);
        Assert.Equal(0u, Step(input, ref now) & (F.MlLButtonDown | F.MlLButtonUp | F.LButtonDown | F.LButtonUp));
    }

    [Fact]
    public void The_button_bits_follow_the_view_the_script_asked_for()
    {
        var mlOnly = new ScriptControls();
        mlOnly.Take(F.MlLButtonDown, passOn: true);   // passing the control on counts too (leftButtonGrabbed)
        var ordinary = Ready();
        ordinary.Script = mlOnly;
        double now = 0;
        ordinary.LeftButton(true);
        Assert.Equal(0u, Step(ordinary, ref now) & (F.MlLButtonDown | F.LButtonDown));   // outside mouse-look the mouse-look take is not for it
        var plain = new ScriptControls();
        plain.Take(F.LButtonDown, passOn: false);
        var third = Ready();
        third.Script = plain;
        third.LeftButton(true);
        now = 0;
        Assert.Equal(F.LButtonDown, Step(third, ref now) & (F.MlLButtonDown | F.LButtonDown));
        third.LeftButton(false);
        Assert.Equal(F.LButtonUp, Step(third, ref now) & (F.MlLButtonUp | F.LButtonUp));
        var inMouseLook = Ready();
        inMouseLook.Script = plain;
        inMouseLook.EnterMouseLook();
        inMouseLook.LeftButton(true);
        now = 0;
        Assert.Equal(0u, Step(inMouseLook, ref now) & (F.MlLButtonDown | F.LButtonDown));   // in mouse-look only the mouse-look take counts
    }

    [Fact]
    public void The_script_controls_are_counted_per_bit_and_by_whether_they_pass_on()
    {
        var s = new ScriptControls();
        Assert.False(s.LeftButtonGrabbed(true) || s.LeftButtonGrabbed(false) || s.RotateGrabbed || s.UpGrabbed);
        s.Take(F.MlLButtonDown, false);
        s.Take(F.MlLButtonDown, false);
        Assert.True(s.LeftButtonGrabbed(true));
        Assert.False(s.LeftButtonGrabbed(false));
        s.Release(F.MlLButtonDown, false);
        Assert.True(s.LeftButtonGrabbed(true));    // one take is left
        s.Release(F.MlLButtonDown, false);
        Assert.False(s.LeftButtonGrabbed(true));
        s.Release(F.MlLButtonDown, false);          // more releases than takes never go below none
        s.Take(F.MlLButtonDown, false);
        Assert.True(s.LeftButtonGrabbed(true));

        s.Take(F.YawNeg, passOn: true);
        Assert.False(s.RotateGrabbed);              // a take that passes the control on does not stop the avatar turning
        s.Take(F.YawNeg, passOn: false);
        Assert.True(s.RotateGrabbed);
        s.Release(F.YawNeg, passOn: false);
        Assert.False(s.RotateGrabbed);

        s.Take(F.UpPos, passOn: false);
        Assert.True(s.UpGrabbed);
        s.Take(F.LButtonDown, passOn: true);
        Assert.True(s.LeftButtonGrabbed(false));    // passed-on takes of the button count
    }

    [Fact]
    public void A_script_that_took_the_turn_controls_stops_the_turn_keys_turning_but_the_bits_still_go()
    {
        // llviewerinput.cpp sends the yaw bit; llagent.cpp yaw() does nothing while rotateGrabbed
        var script = new ScriptControls();
        var blocked = Ready();
        blocked.Script = script;
        script.Take(F.YawPos, passOn: false);
        blocked.Press(MoveCommand.TurnLeft, 0);
        double now = 0;
        uint bits = 0;
        for (int i = 0; i < 30; i++) bits = Step(blocked, ref now);
        Assert.Equal(0f, blocked.Heading!.Value);
        Assert.Equal(F.YawPos, bits & F.YawPos);

        var passed = Ready();
        var passing = new ScriptControls();
        passing.Take(F.YawPos, passOn: true);
        passed.Script = passing;
        passed.Press(MoveCommand.TurnLeft, 0);
        now = 0;
        for (int i = 0; i < 30; i++) Step(passed, ref now);
        Assert.NotEqual(0f, passed.Heading!.Value);
    }

    [Fact]
    public void A_script_that_took_the_up_control_stops_a_held_jump_starting_to_fly()
    {
        var free = Ready();
        free.Press(MoveCommand.Jump, 0);
        var taken = Ready();
        var script = new ScriptControls();
        script.Take(F.UpPos, passOn: false);
        taken.Script = script;
        taken.Press(MoveCommand.Jump, 0);
        double now = 0;
        uint takenBits = 0;
        for (int i = 0; i < 90; i++) { takenBits = taken.Frame(now += Frame60, Frame60, false, 0); }
        now = 0;
        for (int i = 0; i < 90; i++) free.Frame(now += Frame60, Frame60, false, 0);
        Assert.True(free.Flying);
        Assert.False(taken.Flying);
        Assert.Equal(F.UpPos, takenBits & F.UpPos);   // the bit still goes to the script
    }

    // ---------------- the transport and the session ----------------

    [Fact]
    public void The_protocol_library_sends_the_mouse_look_and_button_bits_it_is_given()
    {
        using var client = new LibreMetaverse.GridClient();
        var mv = client.Self.Movement;
        foreach (uint bit in new[] { F.Mouselook, F.LButtonDown, F.LButtonUp, F.MlLButtonDown, F.MlLButtonUp })
        {
            LmvTransport.ApplyControls(mv, bit);
            Assert.Equal(bit, mv.AgentControls);
        }
        LmvTransport.ApplyControls(mv, F.Mouselook | F.AtPos | F.FastAt | F.MlLButtonDown);
        Assert.Equal(F.Mouselook | F.AtPos | F.FastAt | F.MlLButtonDown, mv.AgentControls);
        LmvTransport.ApplyControls(mv, 0);
        Assert.Equal(0u, mv.AgentControls);
    }

    [Fact]
    public void A_script_control_message_becomes_one_entry_per_block()
    {
        var packet = new LibreMetaverse.Packets.ScriptControlChangePacket
        {
            Data =
            [
                new() { TakeControls = true, Controls = F.MlLButtonDown | F.AtPos, PassToAgent = false },
                new() { TakeControls = false, Controls = F.LButtonDown, PassToAgent = true },
            ],
        };
        var changed = LmvTransport.ToScriptControlChange(null, packet);
        Assert.Equal(2, changed.Entries.Count);
        Assert.Equal(new ScriptControlEntry(true, F.MlLButtonDown | F.AtPos, false), changed.Entries[0]);
        Assert.Equal(new ScriptControlEntry(false, F.LButtonDown, true), changed.Entries[1]);
    }

    [Fact]
    public void The_session_keeps_the_controls_the_region_says_scripts_took()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        var sim = new SimRef(256000UL << 32 | 256000UL, "Here");
        ServerAppearanceTests.Arrive(t, sim, serverBaked: true);
        Assert.False(s.ScriptControls.LeftButtonGrabbed(true));
        t.Raise(new ScriptControlChanged(sim, [new ScriptControlEntry(true, F.MlLButtonDown, false)]));
        Assert.True(s.ScriptControls.LeftButtonGrabbed(true));
        t.Raise(new ScriptControlChanged(sim, [new ScriptControlEntry(false, F.MlLButtonDown, false)]));
        Assert.False(s.ScriptControls.LeftButtonGrabbed(true));
    }

    // ---------------- the keys ----------------

    [Theory]
    [InlineData("A", MoveCommand.SlideLeft)]
    [InlineData("D", MoveCommand.SlideRight)]
    [InlineData("LEFT", MoveCommand.SlideLeft)]
    [InlineData("RIGHT", MoveCommand.SlideRight)]
    [InlineData("W", MoveCommand.Forward)]
    [InlineData("UP", MoveCommand.Forward)]
    [InlineData("S", MoveCommand.Backward)]
    [InlineData("DOWN", MoveCommand.Backward)]
    [InlineData("E", MoveCommand.Jump)]
    [InlineData("PGUP", MoveCommand.Jump)]
    [InlineData("C", MoveCommand.Down)]
    [InlineData("PGDN", MoveCommand.Down)]
    [InlineData("F", MoveCommand.ToggleFly)]
    [InlineData("HOME", MoveCommand.ToggleFly)]
    [InlineData("SPACE", MoveCommand.Stop)]
    public void In_mouse_look_the_keys_are_the_official_first_person_ones(string key, MoveCommand expected)
    {
        // app_settings/key_bindings.xml, <first_person>: A and D (and the arrows) slide instead of turning
        Assert.True(KeyBindings.Default.TryCommand(key, false, out var cmd, mouseLook: true));
        Assert.Equal(expected, cmd);
    }

    [Fact]
    public void In_mouse_look_no_key_turns_and_shift_changes_nothing()
    {
        foreach (var key in new[] { "A", "D", "LEFT", "RIGHT" })
            foreach (bool shift in new[] { false, true })
                if (KeyBindings.Default.TryCommand(key, shift, out var cmd, mouseLook: true))
                    Assert.DoesNotContain(cmd, new[] { MoveCommand.TurnLeft, MoveCommand.TurnRight });
        Assert.True(KeyBindings.Default.TryCommand("A", true, out var slide, mouseLook: true));
        Assert.Equal(MoveCommand.SlideLeft, slide);
        Assert.True(KeyBindings.Default.TryCommand("A", false, out var third));   // outside mouse-look A still turns
        Assert.Equal(MoveCommand.TurnLeft, third);
    }

    // ---------------- what enters and leaves ----------------

    [Fact]
    public void The_M_key_toggles_mouse_look()
    {
        var mode = new MouseLookMode();
        var changes = new List<bool>();
        mode.Changed += changes.Add;
        mode.Toggle();
        Assert.True(mode.Active);
        mode.Toggle();
        Assert.False(mode.Active);
        Assert.Equal([true, false], changes);
    }

    [Fact]
    public void Scrolling_in_past_the_closest_zoom_enters_and_scrolling_out_leaves()
    {
        var mode = new MouseLookMode();
        mode.WheelIn(pastClosest: false);   // an ordinary zoom in
        Assert.False(mode.Active);
        mode.WheelIn(pastClosest: true);
        Assert.True(mode.Active);
        mode.WheelOut();
        Assert.False(mode.Active);
        mode.WheelOut();                    // out of mouse-look it is an ordinary zoom out
        Assert.False(mode.Active);
    }

    [Fact]
    public void Escape_focus_gain_and_minimising_leave_and_do_nothing_otherwise()
    {
        var mode = new MouseLookMode();
        var changes = new List<bool>();
        mode.Changed += changes.Add;
        mode.Escape();
        mode.FocusGained();
        mode.Minimized();
        Assert.Empty(changes);
        foreach (Action leave in new Action[] { mode.Escape, mode.FocusGained, mode.Minimized, mode.Leave })
        {
            mode.Toggle();
            Assert.True(mode.Active);
            leave();
            Assert.False(mode.Active);
        }
        Assert.Equal(8, changes.Count);
    }

    [Fact]
    public void When_mouse_look_is_not_allowed_nothing_enters_it()
    {
        var mode = new MouseLookMode { Allowed = false };
        mode.Toggle();
        mode.WheelIn(pastClosest: true);
        Assert.False(mode.Active);
    }

    // ---------------- the camera ----------------

    [Fact]
    public void A_zoom_in_that_would_pass_the_closest_zoom_is_the_one_that_enters()
    {
        var camera = new FollowCamera();
        Assert.False(camera.ZoomWouldPassClosest(1f / 1.15f));   // from 1.0 to 0.87
        camera.SetZoom(FollowCamera.MinZoom);
        Assert.True(camera.ZoomWouldPassClosest(1f / 1.15f));    // already at the closest
        Assert.False(camera.ZoomWouldPassClosest(1.15f));        // zooming out never does
        camera.SetZoom(0.27f);
        Assert.True(camera.ZoomWouldPassClosest(0.8f));          // 0.27 * 0.8 = 0.216, under 0.25
        Assert.False(camera.ZoomWouldPassClosest(0.95f));        // 0.2565, not
    }

    [Fact]
    public void In_mouse_look_the_eye_is_at_the_avatars_eye_height_and_looks_along_the_heading_and_tilt()
    {
        var camera = new FollowCamera();
        camera.EnterMouselook();
        var centre = new Vector3(100, 100, 25.95f);   // an avatar 1.9 m tall standing at z 25
        var (eye, target) = camera.MouselookPose(centre, 1.9f, 0.6f, 0.3f, 1.0 / 60);
        Assert.Equal(100f, eye.X, 3);
        Assert.Equal(100f, eye.Y, 3);
        Assert.Equal(25f + FollowCamera.EyeHeightFraction * 1.9f, eye.Z, 3);
        var look = Vector3.Normalize(target - eye);
        Assert.Equal(MathF.Cos(0.3f) * MathF.Cos(0.6f), look.X, 4);
        Assert.Equal(MathF.Cos(0.3f) * MathF.Sin(0.6f), look.Y, 4);
        Assert.Equal(MathF.Sin(0.3f), look.Z, 4);
    }

    [Fact]
    public void In_mouse_look_the_view_follows_the_mouse_at_once_with_no_smoothing()
    {
        var camera = new FollowCamera();
        camera.EnterMouselook();
        var centre = new Vector3(100, 100, 25.95f);
        camera.MouselookPose(centre, 1.9f, 0f, 0f, 1.0 / 60);
        var (eye, target) = camera.MouselookPose(centre, 1.9f, 1.2f, -0.5f, 1.0 / 60);   // turned and tilted in one frame
        var look = Vector3.Normalize(target - eye);
        Assert.Equal(MathF.Cos(-0.5f) * MathF.Cos(1.2f), look.X, 4);
        Assert.Equal(MathF.Cos(-0.5f) * MathF.Sin(1.2f), look.Y, 4);
        Assert.Equal(MathF.Sin(-0.5f), look.Z, 4);
    }

    [Fact]
    public void Leaving_puts_the_zoom_back_to_the_default_and_the_camera_starts_close_and_eases_out()
    {
        var camera = new FollowCamera();
        camera.SetZoom(FollowCamera.MinZoom);
        camera.EnterMouselook();
        camera.LeaveMouselook();
        Assert.Equal(1f, camera.Zoom);   // llagentcamera.cpp changeCameraToThirdPerson: the zoom fraction is the initial one
        var centre = new Vector3(100, 100, 25.95f);
        camera.Pose(centre, 0f, 1.0 / 60);
        float first = camera.Distance;
        for (int i = 0; i < 120; i++) camera.Pose(centre, 0f, 1.0 / 60);
        Assert.True(first < 0.8f, $"the camera starts at {first:F2} m");
        Assert.Equal(camera.TargetDistance, camera.Distance, 1);
    }

    // ---------------- the own avatar left out ----------------

    [Fact]
    public void In_mouse_look_the_own_avatar_is_not_drawn_and_everyone_else_still_is()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        var here = new SimRef(256000UL << 32 | 256000UL, "Here");
        ServerAppearanceTests.Arrive(t, here, serverBaked: true);
        double clock = 0;
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => clock);
        var changes = new List<Viewer.Core.World.WorldChange>();
        s.World.Changed += changes.Add;
        t.Raise(new AvatarUpdated(here, new Viewer.Core.World.AvatarData(800, ServerAppearanceTests.Me, "Test User", new Vector3(100, 100, 25), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), Vector3.Zero, 0)));
        t.Raise(new AvatarUpdated(here, new Viewer.Core.World.AvatarData(801, Guid.Parse("0c4f0c4f-1111-4222-8333-944444444444"), "Other User", new Vector3(104, 100, 25), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), Vector3.Zero, 0)));
        foreach (var c in changes.ToList()) scene.Apply(c);
        var self = scene.Self!;
        Assert.Equal(2, scene.Count);
        var other = scene.ByLocalId(801)!;
        Assert.True(scene.Draws(self));
        scene.HideSelf = true;
        Assert.False(scene.Draws(self));
        Assert.True(scene.Draws(other));
        scene.HideSelf = false;
        Assert.True(scene.Draws(self));
    }

    // ---------------- the jerk meter ----------------

    private static readonly MovementRig.Key[] WalkInMouseLook =
    [
        new(1.0, true, MoveCommand.Forward), new(4.0, false, MoveCommand.Forward),
    ];

    private static Look[] SweepingTheMouse()
    {
        var looks = new List<Look> { new(0.5, LookKind.Enter) };
        for (int i = 0; i < 90; i++) looks.Add(new Look(1.5 + (i + 0.5) * Frame60, LookKind.Move, 4, i % 2 == 0 ? -2 : 2));   // 1.5 s of steady turning with a little tilt
        looks.Add(new Look(4.5, LookKind.Leave));
        return [.. looks];
    }

    [Fact]
    public void Mouse_look_case_of_the_jerk_meter()
    {
        var run = MovementRig.Run(WalkInMouseLook, 5.5, looks: SweepingTheMouse());
        var (avatar, camera) = JerkMeter.Of(run, 1.0, 4.4);
        // how smooth the picture is: the direction the camera looks along, turned at a steady rate, moves in even steps
        var aim = run.Frames.Where(f => f.At is > 1.6 and < 3.0).Select(f => Vector3.Normalize(f.Target - f.Eye)).ToList();
        double worstStep = 0, bestStep = double.MaxValue;
        for (int i = 1; i < aim.Count; i++)
        {
            double step = Math.Acos(Math.Clamp(Vector3.Dot(aim[i - 1], aim[i]), -1f, 1f));
            worstStep = Math.Max(worstStep, step);
            bestStep = Math.Min(bestStep, step);
        }
        // the same walk seen from behind the avatar, for comparison
        var third = MovementRig.Run(WalkInMouseLook, 5.5);
        var (_, thirdCamera) = JerkMeter.Of(third, 1.0, 4.4);
        string line = $"mouse-look-walk	avatar_rms_jerk={avatar.RmsJerk:F2}	avatar_peak_jerk={avatar.PeakJerk:F1}	camera_rms_jerk={camera.RmsJerk:F2}	camera_peak_jerk={camera.PeakJerk:F1}"
            + $"	third_person_camera_rms_jerk={thirdCamera.RmsJerk:F2}	aim_step_min_deg={bestStep * 180 / Math.PI:F3}	aim_step_max_deg={worstStep * 180 / Math.PI:F3}";
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("NV_JERK_OUT") is { Length: > 0 } path) File.AppendAllText(path, line + "\n");
        Assert.True(run.Frames.Count > 100);
        Assert.True(camera.RmsJerk > 0, "the camera moved");
        // 4 pixels a frame at the default sensitivity is 0.54 degrees a frame; the view turns by exactly that every frame
        Assert.InRange(bestStep * 180 / Math.PI, 0.4, 0.7);
        Assert.InRange(worstStep * 180 / Math.PI, 0.4, 0.7);
        // the eye (the damped avatar position plus the eye height) is no rougher than the third-person camera on the same walk
        Assert.True(camera.RmsJerk <= thirdCamera.RmsJerk * 1.25, $"mouse-look camera jerk {camera.RmsJerk:F1} against {thirdCamera.RmsJerk:F1} in third person");
    }

    [Fact]
    public void Walking_in_mouse_look_moves_the_avatar_along_its_heading_and_leaving_ends_it()
    {
        var run = MovementRig.Run(WalkInMouseLook, 5.5, looks: [new Look(0.5, LookKind.Enter), new Look(4.5, LookKind.Leave)]);
        Assert.All(run.Sent.Where(s => s.At is > 0.6 and < 4.4), s => Assert.Equal(F.Mouselook, s.Controls & F.Mouselook));
        Assert.All(run.Sent.Where(s => s.At > 4.7), s => Assert.Equal(0u, s.Controls & F.Mouselook));
        var moved = run.Frames.Last().Soles - run.Frames.First().Soles;
        Assert.True(moved.Length() > 8f, $"moved {moved.Length():F1} m");
    }
}
