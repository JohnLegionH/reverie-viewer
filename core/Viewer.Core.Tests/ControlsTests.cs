// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using System.Text.RegularExpressions;
using Viewer.Motion;
using Viewer.Shell;
using Xunit;
using F = Viewer.Motion.AgentControlFlags;

namespace Viewer.Core.Tests;

/// <summary>A test against the official key table file (local input): skipped with its reason unless NV_CHARACTER_DIR names the official
/// viewer's character folder, whose sibling app_settings folder holds key_bindings.xml.</summary>
public sealed class OfficialKeyTableFactAttribute : FactAttribute
{
    public OfficialKeyTableFactAttribute()
    {
        if (Path is null)
            Skip = $"local-only official key table: {CharacterFactAttribute.Variable} is not set (the official viewer's source is not in the repo)";
    }

    public static string? Path
    {
        get
        {
            var folder = CharacterFactAttribute.Folder;
            if (folder is null) return null;
            var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, "..", "app_settings", "key_bindings.xml"));
            return File.Exists(file) ? file : null;
        }
    }
}

/// <summary>The default key bindings in every mode, the way each command is routed, left-drag steering and the yaw it gives. All
/// members are independent (no process-wide state), so the class runs in parallel with the others.</summary>
public class ControlsTests
{
    // ---------------- the table ----------------

    [Theory]
    [InlineData(KeyMode.FirstPerson, 19)]
    [InlineData(KeyMode.ThirdPerson, 47)]
    [InlineData(KeyMode.Sitting, 56)]
    [InlineData(KeyMode.EditAvatar, 16)]
    public void Each_mode_has_the_official_number_of_bindings(KeyMode mode, int count) =>
        Assert.Equal(count, KeyBindingTable.For(mode).Count());

    [OfficialKeyTableFact]
    public void The_table_is_the_official_file_line_for_line()
    {
        var text = File.ReadAllText(OfficialKeyTableFact_Path());
        var modes = new Dictionary<string, KeyMode> { ["first_person"] = KeyMode.FirstPerson, ["third_person"] = KeyMode.ThirdPerson, ["sitting"] = KeyMode.Sitting, ["edit_avatar"] = KeyMode.EditAvatar };
        foreach (var (tag, mode) in modes)
        {
            var section = Regex.Match(text, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline).Groups[1].Value;
            var official = Regex.Matches(section, "<binding\\s+key=\"([^\"]*)\"\\s+mask=\"([^\"]*)\"(?:\\s+mouse=\"([^\"]*)\")?\\s+command=\"([^\"]*)\"")
                .Select(m => (Key: m.Groups[1].Value, Mask: m.Groups[2].Value, Mouse: m.Groups[3].Value, Command: m.Groups[4].Value)).ToList();
            var ours = KeyBindingTable.For(mode).Select(e => (e.Key, Mask: MaskName(e.Mask), e.Mouse, e.Command)).ToList();
            Assert.NotEmpty(official);
            Assert.Equal(official, ours);
        }
    }

    private static string OfficialKeyTableFact_Path() => OfficialKeyTableFactAttribute.Path!;

    private static string MaskName(CameraMask m) => m switch
    {
        CameraMask.None => "NONE",
        CameraMask.Shift => "SHIFT",
        CameraMask.Alt => "ALT",
        CameraMask.Control | CameraMask.Alt => "CTL_ALT",
        CameraMask.Control | CameraMask.Alt | CameraMask.Shift => "CTL_ALT_SHIFT",
        _ => "?",
    };

    [Fact]
    public void Every_binding_in_every_mode_has_a_known_command()
    {
        Assert.NotEmpty(KeyBindingTable.All);
        foreach (var e in KeyBindingTable.All)
        {
            var route = KeyBindingTable.Route(e.Command);
            Assert.True(route.Kind != KeyRouteKind.None, $"{e.Mode} {e.Key}{e.Mouse} {e.Mask}: {e.Command} is routed nowhere");
            if (e.Mouse != "")
                Assert.True(route.Kind is KeyRouteKind.ToggleVoice or KeyRouteKind.ScriptLeftButton, $"{e.Command} is a mouse command");
        }
    }

    [Theory]
    // third person: the arrow keys do what W, A, S and D do
    [InlineData(KeyMode.ThirdPerson, "UP", CameraMask.None, "push_forward", MoveCommand.Forward)]
    [InlineData(KeyMode.ThirdPerson, "DOWN", CameraMask.None, "push_backward", MoveCommand.Backward)]
    [InlineData(KeyMode.ThirdPerson, "LEFT", CameraMask.None, "turn_left", MoveCommand.TurnLeft)]
    [InlineData(KeyMode.ThirdPerson, "RIGHT", CameraMask.None, "turn_right", MoveCommand.TurnRight)]
    [InlineData(KeyMode.ThirdPerson, "LEFT", CameraMask.Shift, "slide_left", MoveCommand.SlideLeft)]
    [InlineData(KeyMode.ThirdPerson, "RIGHT", CameraMask.Shift, "slide_right", MoveCommand.SlideRight)]
    [InlineData(KeyMode.ThirdPerson, "W", CameraMask.None, "push_forward", MoveCommand.Forward)]
    [InlineData(KeyMode.ThirdPerson, "S", CameraMask.None, "push_backward", MoveCommand.Backward)]
    [InlineData(KeyMode.ThirdPerson, "A", CameraMask.None, "turn_left", MoveCommand.TurnLeft)]
    [InlineData(KeyMode.ThirdPerson, "D", CameraMask.None, "turn_right", MoveCommand.TurnRight)]
    [InlineData(KeyMode.ThirdPerson, "A", CameraMask.Shift, "slide_left", MoveCommand.SlideLeft)]
    [InlineData(KeyMode.ThirdPerson, "D", CameraMask.Shift, "slide_right", MoveCommand.SlideRight)]
    [InlineData(KeyMode.ThirdPerson, "E", CameraMask.None, "jump", MoveCommand.Jump)]
    [InlineData(KeyMode.ThirdPerson, "C", CameraMask.None, "push_down", MoveCommand.Down)]
    [InlineData(KeyMode.ThirdPerson, "F", CameraMask.None, "toggle_fly", MoveCommand.ToggleFly)]
    [InlineData(KeyMode.ThirdPerson, "PGUP", CameraMask.None, "jump", MoveCommand.Jump)]
    [InlineData(KeyMode.ThirdPerson, "PGDN", CameraMask.None, "push_down", MoveCommand.Down)]
    [InlineData(KeyMode.ThirdPerson, "HOME", CameraMask.None, "toggle_fly", MoveCommand.ToggleFly)]
    [InlineData(KeyMode.ThirdPerson, "SPACE", CameraMask.None, "stop_moving", MoveCommand.Stop)]
    // first person: the turn keys slide
    [InlineData(KeyMode.FirstPerson, "LEFT", CameraMask.None, "slide_left", MoveCommand.SlideLeft)]
    [InlineData(KeyMode.FirstPerson, "RIGHT", CameraMask.None, "slide_right", MoveCommand.SlideRight)]
    [InlineData(KeyMode.FirstPerson, "A", CameraMask.None, "slide_left", MoveCommand.SlideLeft)]
    [InlineData(KeyMode.FirstPerson, "D", CameraMask.None, "slide_right", MoveCommand.SlideRight)]
    [InlineData(KeyMode.FirstPerson, "UP", CameraMask.None, "push_forward", MoveCommand.Forward)]
    [InlineData(KeyMode.FirstPerson, "HOME", CameraMask.None, "toggle_fly", MoveCommand.ToggleFly)]
    // sitting: Shift passes the control on to a vehicle
    [InlineData(KeyMode.Sitting, "A", CameraMask.Shift, "slide_left", MoveCommand.SlideLeft)]
    [InlineData(KeyMode.Sitting, "RIGHT", CameraMask.Shift, "slide_right", MoveCommand.SlideRight)]
    public void A_key_gives_the_official_movement(KeyMode mode, string key, CameraMask mask, string command, MoveCommand move)
    {
        Assert.True(KeyBindingTable.TryFind(mode, key, mask, out var found));
        Assert.Equal(command, found);
        var route = KeyBindingTable.Route(found);
        Assert.Equal(KeyRouteKind.Move, route.Kind);
        Assert.Equal(move, route.Move);
    }

    [Theory]
    [InlineData(KeyMode.ThirdPerson, "LEFT", CameraMask.Alt, "spin_around_cw", CameraKey.SpinAroundCw)]
    [InlineData(KeyMode.ThirdPerson, "RIGHT", CameraMask.Alt, "spin_around_ccw", CameraKey.SpinAroundCcw)]
    [InlineData(KeyMode.ThirdPerson, "UP", CameraMask.Alt, "move_forward", CameraKey.MoveIn)]
    [InlineData(KeyMode.ThirdPerson, "S", CameraMask.Alt, "move_backward", CameraKey.MoveOut)]
    [InlineData(KeyMode.ThirdPerson, "E", CameraMask.Alt, "spin_over", CameraKey.SpinOver)]
    [InlineData(KeyMode.ThirdPerson, "PGDN", CameraMask.Alt, "spin_under", CameraKey.SpinUnder)]
    [InlineData(KeyMode.ThirdPerson, "W", CameraMask.Control | CameraMask.Alt, "spin_over", CameraKey.SpinOver)]
    [InlineData(KeyMode.ThirdPerson, "DOWN", CameraMask.Control | CameraMask.Alt, "spin_under", CameraKey.SpinUnder)]
    [InlineData(KeyMode.ThirdPerson, "A", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_left", CameraKey.PanLeft)]
    [InlineData(KeyMode.ThirdPerson, "UP", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_up", CameraKey.PanUp)]
    [InlineData(KeyMode.EditAvatar, "A", CameraMask.None, "edit_avatar_spin_cw", CameraKey.SpinAroundCw)]
    [InlineData(KeyMode.EditAvatar, "RIGHT", CameraMask.None, "edit_avatar_spin_ccw", CameraKey.SpinAroundCcw)]
    [InlineData(KeyMode.EditAvatar, "E", CameraMask.None, "edit_avatar_spin_over", CameraKey.SpinOver)]
    [InlineData(KeyMode.EditAvatar, "PGDN", CameraMask.None, "edit_avatar_spin_under", CameraKey.SpinUnder)]
    [InlineData(KeyMode.EditAvatar, "W", CameraMask.None, "edit_avatar_move_forward", CameraKey.MoveIn)]
    [InlineData(KeyMode.EditAvatar, "DOWN", CameraMask.None, "edit_avatar_move_backward", CameraKey.MoveOut)]
    [InlineData(KeyMode.Sitting, "W", CameraMask.Alt, "move_forward", CameraKey.MoveIn)]
    [InlineData(KeyMode.Sitting, "LEFT", CameraMask.Control | CameraMask.Alt | CameraMask.Shift, "pan_left", CameraKey.PanLeft)]
    public void A_key_gives_the_official_camera_command(KeyMode mode, string key, CameraMask mask, string command, CameraKey camera)
    {
        Assert.True(KeyBindingTable.TryFind(mode, key, mask, out var found));
        Assert.Equal(command, found);
        var route = KeyBindingTable.Route(found);
        Assert.Equal(KeyRouteKind.Camera, route.Kind);
        Assert.Equal(camera, route.Camera);
        Assert.Null(route.Fallback);
    }

    [Theory]
    [InlineData("A", "spin_around_cw_sitting", CameraKey.SpinAroundCw, MoveCommand.TurnLeft)]
    [InlineData("D", "spin_around_ccw_sitting", CameraKey.SpinAroundCcw, MoveCommand.TurnRight)]
    [InlineData("W", "move_forward_sitting", CameraKey.MoveIn, MoveCommand.Forward)]
    [InlineData("S", "move_backward_sitting", CameraKey.MoveOut, MoveCommand.Backward)]
    [InlineData("E", "spin_over_sitting", CameraKey.SpinOver, MoveCommand.Jump)]
    [InlineData("C", "spin_under_sitting", CameraKey.SpinUnder, MoveCommand.Down)]
    [InlineData("LEFT", "spin_around_cw_sitting", CameraKey.SpinAroundCw, MoveCommand.TurnLeft)]
    [InlineData("PGUP", "spin_over_sitting", CameraKey.SpinOver, MoveCommand.Jump)]
    public void Sitting_keys_move_the_camera_and_name_the_key_a_script_or_a_seat_camera_takes_over(string key, string command, CameraKey camera, MoveCommand fallback)
    {
        Assert.True(KeyBindingTable.TryFind(KeyMode.Sitting, key, CameraMask.None, out var found));
        Assert.Equal(command, found);
        var route = KeyBindingTable.Route(found);
        Assert.Equal(KeyRouteKind.Camera, route.Kind);
        Assert.Equal(camera, route.Camera);
        Assert.Equal(fallback, route.Fallback);
    }

    [Theory]
    [InlineData("start_chat", KeyRouteKind.StartChat)]
    [InlineData("start_gesture", KeyRouteKind.StartGesture)]
    [InlineData("toggle_voice", KeyRouteKind.ToggleVoice)]
    [InlineData("script_trigger_lbutton", KeyRouteKind.ScriptLeftButton)]
    [InlineData("no_such_command", KeyRouteKind.None)]
    public void Other_commands_are_routed_to_the_host(string command, KeyRouteKind kind) =>
        Assert.Equal(kind, KeyBindingTable.Route(command).Kind);

    [Theory]
    [InlineData(KeyMode.ThirdPerson, "UP", CameraMask.Control)]
    [InlineData(KeyMode.ThirdPerson, "W", CameraMask.Shift)]
    [InlineData(KeyMode.ThirdPerson, "W", CameraMask.Alt | CameraMask.Shift)]
    [InlineData(KeyMode.FirstPerson, "A", CameraMask.Shift)]
    [InlineData(KeyMode.ThirdPerson, "Q", CameraMask.None)]
    public void A_mask_must_match_exactly(KeyMode mode, string key, CameraMask mask) =>
        Assert.False(KeyBindingTable.TryFind(mode, key, mask, out _));

    [Fact]
    public void The_camera_keys_of_the_controls_are_the_table_camera_commands()
    {
        int camera = 0;
        foreach (var e in KeyBindingTable.For(KeyMode.ThirdPerson).Where(e => e.Mouse == "" && KeyBindingTable.Route(e.Command).Kind == KeyRouteKind.Camera))
        {
            camera++;
            Assert.True(CameraControls.TryKey(e.Key, e.Mask, out var key), $"{e.Key} {e.Mask}");
            Assert.Equal(KeyBindingTable.Route(e.Command).Camera, key);
        }
        Assert.Equal(24, camera);
    }

    [Fact]
    public void The_default_avatar_keys_are_the_table_ones()
    {
        int moves = 0;
        foreach (var e in KeyBindingTable.For(KeyMode.ThirdPerson).Where(e => e.Mouse == "" && e.Mask is CameraMask.None or CameraMask.Shift))
        {
            var route = KeyBindingTable.Route(e.Command);
            if (route.Kind != KeyRouteKind.Move) continue;
            moves++;
            Assert.True(KeyBindings.Default.TryCommand(e.Key, e.Mask == CameraMask.Shift, out var cmd), $"{e.Key} {e.Mask}");
            Assert.Equal(route.Move, cmd);
        }
        foreach (var e in KeyBindingTable.For(KeyMode.FirstPerson).Where(e => e.Mouse == "" && KeyBindingTable.Route(e.Command).Kind == KeyRouteKind.Move))
        {
            moves++;
            Assert.True(KeyBindings.Default.TryCommand(e.Key, false, out var cmd, mouseLook: true), e.Key);
            Assert.Equal(KeyBindingTable.Route(e.Command).Move, cmd);
        }
        Assert.Equal(19 + 15, moves);
    }

    [Fact]
    public void The_left_click_is_bound_in_third_person_so_a_drag_steers()
    {
        Assert.True(KeyBindingTable.IsMouseBindUsed(KeyMode.ThirdPerson, "LMB", CameraMask.None));   // isMouseBindUsed(CLICK_LEFT, MASK_NONE, MODE_THIRD_PERSON)
        Assert.True(KeyBindingTable.IsMouseBindUsed(KeyMode.ThirdPerson, "MMB", CameraMask.None));
        Assert.False(KeyBindingTable.IsMouseBindUsed(KeyMode.ThirdPerson, "RMB", CameraMask.None));  // the right button only opens the menu
        Assert.False(KeyBindingTable.IsMouseBindUsed(KeyMode.ThirdPerson, "LMB", CameraMask.Alt));
    }

    // ---------------- sitting: when the avatar's own key is sent ----------------

    [Fact]
    public void A_sitting_key_sends_the_avatar_key_when_a_script_took_the_control_or_the_seat_has_a_camera()
    {
        var up = KeyBindingTable.Route("spin_over_sitting");
        var forward = KeyBindingTable.Route("move_forward_sitting");
        var around = KeyBindingTable.Route("spin_around_cw_sitting");
        var plain = KeyBindingTable.Route("spin_over");

        Assert.False(KeyBindingTable.SendsAvatarKey(up, null, false, false, false));
        Assert.True(KeyBindingTable.SendsAvatarKey(up, null, true, false, false));          // the seat's own camera
        var script = new ScriptControls();
        script.Take(F.UpPos, passOn: false);
        Assert.True(KeyBindingTable.SendsAvatarKey(up, script, false, false, false));       // llagent upGrabbed
        Assert.False(KeyBindingTable.SendsAvatarKey(forward, script, false, false, false)); // only up was taken
        script.Take(F.AtPos, passOn: false);
        Assert.True(KeyBindingTable.SendsAvatarKey(forward, script, false, false, false));
        script.Take(F.YawPos, passOn: false);
        Assert.True(KeyBindingTable.SendsAvatarKey(around, script, false, false, false));
        Assert.False(KeyBindingTable.SendsAvatarKey(plain, script, true, true, false));     // a camera key that is not a sitting one
    }

    [Fact]
    public void A_run_makes_the_sitting_keys_walk_unless_always_run_is_on_for_the_push_keys()
    {
        var forward = KeyBindingTable.Route("move_forward_sitting");
        var backward = KeyBindingTable.Route("move_backward_sitting");
        var around = KeyBindingTable.Route("spin_around_ccw_sitting");
        Assert.True(KeyBindingTable.SendsAvatarKey(forward, null, false, running: true, alwaysRun: false));
        Assert.False(KeyBindingTable.SendsAvatarKey(forward, null, false, running: true, alwaysRun: true));
        Assert.True(KeyBindingTable.SendsAvatarKey(backward, null, false, running: true, alwaysRun: false));
        Assert.False(KeyBindingTable.SendsAvatarKey(backward, null, false, running: false, alwaysRun: false));
        Assert.True(KeyBindingTable.SendsAvatarKey(around, null, false, running: true, alwaysRun: true));   // the turn keys: any run
    }

    // ---------------- the heading the mouse turns ----------------

    [Fact]
    public void Yaw_turns_the_heading_left_for_a_positive_angle()
    {
        var input = new AgentControlInput();
        input.AdoptHeading(0.2f);
        input.Yaw(0.5f);
        Assert.Equal(0.7f, input.Heading!.Value, 5);
        input.Yaw(-1.0f);
        Assert.Equal(-0.3f, input.Heading!.Value, 5);
    }

    [Fact]
    public void Yaw_wraps_and_does_nothing_without_a_heading_or_when_a_script_took_the_turn()
    {
        var none = new AgentControlInput();
        none.Yaw(1f);
        Assert.Null(none.Heading);

        var input = new AgentControlInput();
        input.AdoptHeading(3.0f);
        input.Yaw(0.5f);
        Assert.InRange(input.Heading!.Value, -MathF.PI - 1e-4f, MathF.PI + 1e-4f);
        Assert.Equal(3.5f - 2 * MathF.PI, input.Heading!.Value, 4);

        var script = new ScriptControls();
        script.Take(F.YawNeg, passOn: false);
        var held = new AgentControlInput { Script = script };
        held.AdoptHeading(1f);
        held.Yaw(0.5f);
        Assert.Equal(1f, held.Heading!.Value, 5);   // llagent yaw: not while rotateGrabbed
    }

    // ---------------- left-drag steering ----------------

    private const float Width = 1920, Height = 1080, Focal = 800;
    private static readonly Vector3 Agent = new(0, 0, 1);
    private static readonly Vector3 Camera = new(-3, 0, 2.75f);
    private static readonly Vector3 CameraAt = Vector3.Normalize(new Vector3(3, 0, -1));

    /// <summary>The ray through a pixel of a pinhole camera at <see cref="Camera"/> looking along <see cref="CameraAt"/>.</summary>
    private static Vector3 RayAt(float x, float y)
    {
        var right = Vector3.Normalize(Vector3.Cross(CameraAt, Vector3.UnitZ));
        var up = Vector3.Cross(right, CameraAt);
        return Vector3.Normalize(CameraAt + right * ((x - Width / 2) / Focal) - up * ((y - Height / 2) / Focal));
    }

    private static MouseSteering Started(Vector3 pick, bool pickIsSelf = false, bool valid = true, Vector3 far = default, bool focusOnAvatar = true, float x = 960, float y = 540)
    {
        var s = new MouseSteering();
        s.MouseDown(x, y);
        s.Begin(focusOnAvatar, valid, pickIsSelf, pick, far, Agent, Camera, CameraAt);
        return s;
    }

    /// <summary>The yaw (radians, left positive) for a drag of <paramref name="dx"/> pixels, computed on a horizontal circle without
    /// the steering code: the far point where the mouse ray meets the circle round the avatar of radius <paramref name="radius"/>.</summary>
    private static float Expected(float dx, float radius, bool pickBeyondAvatar)
    {
        float Angle(float px)
        {
            var d = RayAt(px, 540);
            var h = Vector2.Normalize(new Vector2(d.X, d.Y));
            var c = new Vector2(Camera.X, Camera.Y);
            float b = Vector2.Dot(c, h);
            float t = -b + MathF.Sqrt(b * b - c.LengthSquared() + radius * radius);
            var hit = c + h * t;
            return MathF.Atan2(hit.Y, hit.X);
        }
        float delta = Angle(960 + dx) - Angle(960);
        return pickBeyondAvatar ? -delta : delta;
    }

    [Fact]
    public void Dragging_right_on_the_ground_beyond_the_avatar_turns_the_avatar_left_as_the_official_code_does()
    {
        var s = Started(new Vector3(5, 0, 0));
        Assert.True(s.Steering);
        float yaw = s.Steer(1060, 540, RayAt, Camera, 0);
        Assert.True(yaw > 0.05f, $"yaw {yaw}");
        Assert.Equal(Expected(100, 5, pickBeyondAvatar: true), yaw, 3);
    }

    [Fact]
    public void Dragging_left_turns_it_the_other_way()
    {
        var s = Started(new Vector3(5, 0, 0));
        float yaw = s.Steer(860, 540, RayAt, Camera, 0);
        Assert.True(yaw < -0.05f, $"yaw {yaw}");
        Assert.Equal(Expected(-100, 5, pickBeyondAvatar: true), yaw, 3);
    }

    [Fact]
    public void A_ground_point_between_the_camera_and_the_avatar_turns_it_the_opposite_way()
    {
        var s = Started(new Vector3(-1.5f, 0, 0));
        float yaw = s.Steer(1060, 540, RayAt, Camera, 0);
        Assert.True(yaw < -0.05f, $"yaw {yaw}");
        Assert.Equal(Expected(100, 1.5f, pickBeyondAvatar: false), yaw, 3);
    }

    [Fact]
    public void The_steps_of_one_drag_add_up_to_the_same_turn_as_one_big_step()
    {
        var whole = Started(new Vector3(5, 2, 0)).Steer(1160, 540, RayAt, Camera, 0);
        var s = Started(new Vector3(5, 2, 0));
        float sum = 0;
        for (float x = 970; x <= 1160; x += 10)
            sum += s.Steer(x, 540, RayAt, Camera, 0);
        Assert.NotEqual(0f, whole);
        Assert.Equal(whole, sum, 4);
    }

    [Fact]
    public void No_movement_is_no_turn()
    {
        var s = Started(new Vector3(5, 0, 0));
        Assert.Equal(0f, s.Steer(960, 540, RayAt, Camera, 0));
    }

    [Fact]
    public void The_turn_is_measured_from_the_avatars_heading()
    {
        // the same drag with the avatar facing another way: the sphere is round the avatar, the angle is taken against its at and left axes,
        // so the difference of two angles does not depend on the heading
        var a = Started(new Vector3(5, 0, 0)).Steer(1060, 540, RayAt, Camera, 0);
        var b = Started(new Vector3(5, 0, 0)).Steer(1060, 540, RayAt, Camera, 1.2f);
        Assert.Equal(a, b, 4);
    }

    [Fact]
    public void A_click_on_the_own_avatar_steers_about_a_point_three_metres_along_the_camera_axis()
    {
        var onSelf = Started(new Vector3(0, 0, 1), pickIsSelf: true).Steer(1060, 540, RayAt, Camera, 0);
        var ahead = Started(Agent + CameraAt * 3).Steer(1060, 540, RayAt, Camera, 0);
        Assert.NotEqual(0f, onSelf);
        Assert.Equal(ahead, onSelf, 5);
    }

    [Fact]
    public void A_click_on_the_sky_steers_about_the_point_the_host_gives()
    {
        var far = Camera + RayAt(960, 540) * 100;
        var sky = Started(default, valid: false, far: far).Steer(1060, 540, RayAt, Camera, 0);
        var point = Started(far).Steer(1060, 540, RayAt, Camera, 0);
        Assert.NotEqual(0f, sky);
        Assert.Equal(point, sky, 5);
    }

    [Fact]
    public void Steering_needs_the_camera_on_the_avatar()
    {
        var s = Started(new Vector3(5, 0, 0), focusOnAvatar: false);
        Assert.False(s.Steering);
        Assert.Equal(0f, s.Steer(1060, 540, RayAt, Camera, 0));
    }

    [Fact]
    public void The_drag_starts_past_three_pixels_and_only_when_the_left_click_is_bound()
    {
        var s = new MouseSteering();
        Assert.False(s.ExceedsThreshold(500, 500, true));                // no button down
        s.MouseDown(100, 100);
        Assert.True(s.Pressed);
        Assert.False(s.ExceedsThreshold(103, 100, true));                // 3 pixels is the threshold itself
        Assert.False(s.ExceedsThreshold(102, 102, true));                // sqrt 8
        Assert.True(s.ExceedsThreshold(103, 101, true));                 // sqrt 10
        Assert.True(s.ExceedsThreshold(100, 104, true));
        Assert.False(s.ExceedsThreshold(100, 140, false));               // left click not bound in third person: no steering
        s.Begin(true, true, false, new Vector3(5, 0, 0), default, Agent, Camera, CameraAt);
        Assert.True(s.Steering);
        Assert.False(s.ExceedsThreshold(100, 140, true));                // already under way
        s.MouseUp();
        Assert.False(s.Pressed);
        Assert.False(s.Steering);
        Assert.Equal(0f, s.Steer(200, 100, RayAt, Camera, 0));
        s.MouseDown(100, 100);
        Assert.True(s.ExceedsThreshold(100, 140, true));                 // a new press starts afresh
    }

    [Fact]
    public void A_drag_that_never_started_leaves_nothing_behind()
    {
        var s = new MouseSteering();
        s.MouseDown(100, 100);
        s.MouseUp();
        Assert.False(s.Steering);
        Assert.False(s.ExceedsThreshold(300, 300, true));
    }
}
