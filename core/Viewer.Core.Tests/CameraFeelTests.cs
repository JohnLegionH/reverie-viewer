// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;
using Xunit.Abstractions;
using Key = Viewer.Core.Tests.MovementRig.Key;

namespace Viewer.Core.Tests;

/// <summary>
/// The third-person camera as the official viewer drives it (llagentcamera.cpp's camera position update): the region's
/// constraint plane shortening the distance, the offset from the avatar smoothed with a 0.02 s half-life, the flight lag, the
/// avatar's position followed as the damped drawable it is (lldrawable.cpp's 0.06 s), the orbit returning behind the avatar
/// when a walk key goes down, and the camera's roughness against a region that states uneven ground. Each camera is its own
/// instance and no test shares state, so the class runs in parallel.
/// </summary>
public class CameraFeelTests(ITestOutputHelper output)
{
    private const double Frame = 1.0 / 60;
    private static readonly Vector3 Centre = new(100, 100, 30);

    /// <summary>A camera with the smoothing and the anchor damping off: the camera as it was before.</summary>
    private static FollowCamera Plain() => new() { PositionHalfLife = 0 };

    private static (Vector3 Eye, Vector3 Target) Settle(FollowCamera cam, Vector3 centre, float heading, double seconds, Vector3 velocity = default, float inAirFrom = float.NaN)
    {
        (Vector3, Vector3) pose = default;
        for (double t = 0; t < seconds; t += Frame)
            pose = cam.Pose(centre, heading, Frame, velocity, float.IsNaN(inAirFrom) ? 0f : (float)Math.Max(0, t - inAirFrom));
        return pose;
    }

    // ---- the region's constraint plane ----

    [Fact]
    public void A_wall_behind_the_avatar_brings_the_camera_in_front_of_it_by_the_epsilon()
    {
        // the plane x = 98.5 with its normal towards the avatar; the camera's offset is 3.0 m west (and 0.75 up)
        var cam = Plain();
        cam.CollidePlane = new Vector4(1, 0, 0, 98.5f);
        var (eye, _) = Settle(cam, Centre, 0f, 3.0);
        // the official rule: the offset is shortened by (pos - w - 0.1) / (offset . normal), so the eye stops at w + 0.1
        Assert.Equal(98.6f, eye.X, 2);
        Assert.Equal(100f, eye.Y, 3);
    }

    [Fact]
    public void A_plane_the_camera_is_already_on_the_right_side_of_changes_nothing_and_neither_does_no_plane()
    {
        var far = Plain();
        far.CollidePlane = new Vector4(1, 0, 0, 90f);
        var none = Plain();
        var (a, _) = Settle(far, Centre, 0f, 2.0);
        var (b, _) = Settle(none, Centre, 0f, 2.0);
        Assert.True((a - b).Length() < 1e-4f, $"{a} against {b}");
        Assert.Equal(Centre.X - 3.0f, b.X, 3);
    }

    [Fact]
    public void A_plane_through_the_avatar_leaves_the_camera_the_least_distance_not_inside_the_avatar()
    {
        var cam = Plain();
        cam.CollidePlane = new Vector4(1, 0, 0, 99.99f);
        var (eye, _) = Settle(cam, Centre, 0f, 3.0);
        Assert.Equal(FollowCamera.MinDistance, (eye - (Centre + FollowCamera.DefaultHeadOffset)).Length(), 2);
    }

    [Fact]
    public void The_distance_shortened_by_a_plane_is_eased_in_with_the_zoom_half_life()
    {
        var cam = Plain();
        cam.CollidePlane = new Vector4(1, 0, 0, 98.5f);
        var (first, _) = cam.Pose(Centre, 0f, Frame);   // the first pose is at the target distance at once
        Assert.Equal(98.6f, first.X, 2);
        cam.CollidePlane = default;   // the wall goes: the distance eases back out with a 0.07 s half-life
        float full = Centre.X - 3.0f * (float)Math.Cos(0);
        (Vector3 Eye, Vector3 Target) at = default;
        for (double t = 0; t < 0.07; t += Frame) at = cam.Pose(Centre, 0f, Frame);
        // after about one half-life the eye is about half the way from 98.6 to 97.0 (a frame's rounding either side)
        Assert.InRange(at.Eye.X, 97.6f, 98.1f);
        for (double t = 0; t < 2; t += Frame) at = cam.Pose(Centre, 0f, Frame);
        Assert.InRange(at.Eye.X, full - 0.01f, full + 0.01f);
    }

    // ---- smoothing of the offset from the avatar ----

    [Fact]
    public void The_first_pose_is_exact_and_an_orbit_change_is_followed_with_a_two_hundredths_of_a_second_half_life()
    {
        var cam = new FollowCamera();
        var (eye0, _) = cam.Pose(Centre, 0f, Frame);
        Assert.True((eye0 - (Centre + new Vector3(-3f, 0, 1.75f))).Length() < 0.01f);   // the third-person head offset (0, 0, 1) is part of the base
        cam.SetOrbit(MathF.PI / 2, 0);
        var (eye1, _) = cam.Pose(Centre, 0f, Frame);   // one frame: the fraction 1 - 2^(-dt / 0.02) of the way
        float f = 1f - MathF.Pow(2f, -(float)Frame / FollowCamera.PositionSmoothingHalfLife);
        var target = Plain();
        target.SetOrbit(MathF.PI / 2, 0);
        var (want, _) = target.Pose(Centre, 0f, Frame);
        var expect = eye0 + (want - eye0) * f;
        Assert.True((eye1 - expect).Length() < 0.02f, $"{eye1} against {expect}");
        var (late, _) = Settle(cam, Centre, 0f, 0.5);
        Assert.True((late - want).Length() < 0.01f);
    }

    [Fact]
    public void A_jump_of_the_avatar_is_followed_at_once()
    {
        var cam = new FollowCamera();
        cam.Pose(Centre, 0f, Frame);
        var moved = Centre + new Vector3(200, 0, 0);
        var (eye, _) = cam.Pose(moved, 0f, Frame);
        Assert.True((eye - (moved + new Vector3(-3f, 0, 1.75f))).Length() < 0.01f, $"{eye}");
    }

    // ---- the avatar's position followed where it is drawn ----

    /// <summary>The camera follows the agent's position, which is the avatar's render position (llvoavatar.cpp, updateCharacter:
    /// gAgent.setPositionAgent(getRenderPosition())); the drawable has already damped it, and the camera adds no damping of its own
    /// (llagentcamera.cpp, updateCamera, smooths only the camera's offset from the agent).</summary>
    [Fact]
    public void The_avatars_position_is_followed_where_it_is_drawn_with_no_damping_of_the_camera_s_own()
    {
        var cam = new FollowCamera { PositionHalfLife = 0 };
        cam.Pose(Centre, 0f, Frame);
        var moved = Centre + new Vector3(1, 0, 0);
        Vector3 eye = default;
        for (double t = 0; t < 0.06 - 1e-9; t += Frame / 4) eye = cam.Pose(moved, 0f, Frame / 4).Eye;
        // the whole way at once
        float travelled = eye.X - (Centre.X - 3f);
        Assert.InRange(travelled, 0.999f, 1.001f);
    }

    // ---- the lag in the air ----

    [Fact]
    public void Flying_the_camera_trails_the_velocity_by_its_strength_over_thirty_after_the_transition_and_not_before()
    {
        var cam = Plain();
        var velocity = new Vector3(10, 0, 0);
        var (ground, _) = Settle(cam, Centre, 0f, 2.0);   // on the ground: no lag
        var (early, _) = Settle(cam, Centre, 0f, 0.4, velocity, inAirFrom: 0f);   // less than 0.5 s in the air: none yet
        Assert.True((early - ground).Length() < 0.01f, "no lag before half a second in the air");
        var air = Plain();
        var (steady, _) = Settle(air, Centre, 0f, 9.0, velocity, inAirFrom: 0f);
        // the lag settles at velocity * 2 / 30 = 0.667 m, behind the avatar along the velocity
        Assert.Equal(ground.X - 10f * 2f / 30f, steady.X, 1);
        Assert.Equal(ground.Y, steady.Y, 2);
    }

    [Fact]
    public void The_lag_never_passes_five_metres_and_a_backward_flight_keeps_the_avatar_in_front()
    {
        var fast = Plain();
        var (ground, _) = Settle(Plain(), Centre, 0f, 2.0);
        var (eye, _) = Settle(fast, Centre, 0f, 20.0, new Vector3(300, 0, 0), inAirFrom: 0f);
        Assert.Equal(5f, ground.X - eye.X, 1);
        // flying backward (against the facing): the lag along the facing is held so the avatar stays in front of the camera
        var back = Plain();
        var (b, _) = Settle(back, Centre, 0f, 9.0, new Vector3(-10, 0, 0), inAirFrom: 0f);
        Assert.True(b.X >= ground.X - 0.01f, $"the camera went in front: {b.X} against {ground.X}");
    }

    [Fact]
    public void Back_on_the_ground_the_lag_eases_away_with_a_fifteen_hundredths_half_life()
    {
        var cam = Plain();
        var (ground, _) = Settle(Plain(), Centre, 0f, 2.0);
        Settle(cam, Centre, 0f, 9.0, new Vector3(100, 0, 0), inAirFrom: 0f);
        var (lagged, _) = cam.Pose(Centre, 0f, Frame, default, 0f);   // lands
        float lag0 = ground.X - lagged.X;
        Assert.True(lag0 > 3f);
        (Vector3 Eye, Vector3 Target) at = default;
        for (double t = 0; t < 0.15 - 1e-9; t += Frame) at = cam.Pose(Centre, 0f, Frame, default, 0f);
        float lag1 = ground.X - at.Eye.X;
        Assert.InRange(lag1 / lag0, 0.4f, 0.62f);
    }

    // ---- the orbit returning behind the avatar ----

    [Fact]
    public void Resetting_the_orbit_clears_the_turn_and_tilt_and_keeps_the_zoom()
    {
        var cam = new FollowCamera();
        cam.SetOrbit(1.2f, 0.4f);
        cam.SetZoom(3f);
        cam.ResetOrbit();
        Assert.Equal(0f, cam.OrbitYaw);
        Assert.Equal(0f, cam.OrbitPitch);
        Assert.Equal(3f, cam.Zoom);
    }

    [Fact]
    public void A_key_that_moves_the_avatar_going_down_is_announced_and_other_keys_and_repeats_are_not()
    {
        var input = new AgentControlInput();
        int n = 0;
        input.WalkKeyPressed += () => n++;
        input.Press(MoveCommand.Forward, 0);
        Assert.Equal(1, n);
        input.Press(MoveCommand.Forward, 0.01);   // a repeat of a held key
        Assert.Equal(1, n);
        input.Press(MoveCommand.TurnLeft, 0.02);
        input.Press(MoveCommand.Jump, 0.03);
        input.Press(MoveCommand.ToggleFly, 0.04);
        Assert.Equal(4, n);                       // the turn, jump and fly keys reset the view as the official ones do
        input.Press(MoveCommand.SlideLeft, 0.05);
        input.Press(MoveCommand.RunBackward, 0.06);
        Assert.Equal(6, n);
        input.Press(MoveCommand.Stop, 0.07);
        input.Press(MoveCommand.ToggleRun, 0.08);
        Assert.Equal(6, n);                       // stopping and the run toggle do not
    }

    // ---- the region's plane reaches the session ----

    [Fact]
    public void The_plane_the_region_sends_is_kept_and_a_zero_plane_clears_it()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        var here = new SimRef(256000UL << 32 | 256000UL, "Here");
        ServerAppearanceTests.Arrive(t, here, serverBaked: true);
        Assert.Equal(default, s.CameraCollidePlane);
        t.Raise(new CameraConstraintReceived(here, new Vector4(0, 1, 0, 12.5f)));
        Assert.Equal(new Vector4(0, 1, 0, 12.5f), s.CameraCollidePlane);
        t.Raise(new CameraConstraintReceived(here, Vector4.Zero));
        Assert.Equal(Vector4.Zero, s.CameraCollidePlane);
    }

    [Fact]
    public void The_message_becomes_a_plane_and_the_plane_survives_a_capture_file()
    {
        var here = new SimRef(256000UL << 32 | 256000UL, "Here");
        var packet = new LibreMetaverse.Packets.CameraConstraintPacket { CameraCollidePlane = new() { Plane = new LibreMetaverse.Vector4(0.6f, -0.8f, 0f, 41.5f) } };
        var ev = Viewer.Core.Protocol.LmvTransport.ToCameraConstraint(here, packet);
        Assert.Equal(new Vector4(0.6f, -0.8f, 0f, 41.5f), ev.Plane);
        var dir = Path.Combine(TestPaths.RepoRoot, "tools", "test-results", "capture-replay");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "camera-plane.jsonl.gz");
        var aliases = new Viewer.Core.Replay.CaptureFile.Aliases(Guid.Empty);
        using (var w = new Viewer.Core.Replay.CaptureFile.Writer(path, new Viewer.Core.Replay.CaptureHeader(Viewer.Core.Replay.CaptureFile.FormatVersion, "plane", Guid.Empty, 0, 0, "test")))
            w.Write(new Viewer.Core.Replay.CaptureRecord(1, null, 0, Viewer.Core.Replay.CaptureFile.Scrub(ev, aliases)));
        var back = Assert.IsType<CameraConstraintReceived>(Viewer.Core.Replay.CaptureFile.Read(path).Records[0].Event);
        Assert.Equal(ev.Plane, back.Plane);
    }

    // ---- the camera against uneven ground ----

    private static readonly Key[] Walk = [new(0.5, true, MoveCommand.Forward), new(6.0, false, MoveCommand.Forward)];

    [Fact]
    public void The_camera_is_rougher_over_uneven_ground_and_the_smoothed_camera_is_as_rough_as_the_drawn_avatar_it_follows()
    {
        // measured while the key is held (to 5.9 s): the ground is the subject here, not the coast to a stop after the release. The drawable's
        // damping is what smooths the avatar over uneven ground; the official camera follows the drawn avatar and smooths only its offset from it
        // (llagentcamera.cpp, updateCamera), so it is as rough as the drawn avatar, no rougher and no smoother.
        var plainFlat = JerkMeter.Of(MovementRig.Run(Walk, 7.0, camera: Plain()), 0.4, 5.9).Camera.RmsJerk;
        var plainRough = JerkMeter.Of(MovementRig.Run(Walk, 7.0, wobble: 0.03, camera: Plain()), 0.4, 5.9).Camera.RmsJerk;
        var dampedFlat = JerkMeter.Of(MovementRig.Run(Walk, 7.0, camera: new FollowCamera()), 0.4, 5.9).Camera.RmsJerk;
        var rough = JerkMeter.Of(MovementRig.Run(Walk, 7.0, wobble: 0.03, camera: new FollowCamera()), 0.4, 5.9);
        var dampedRough = rough.Camera.RmsJerk;
        output.WriteLine($"camera rms jerk: plain flat {plainFlat:F1}, plain uneven {plainRough:F1}, smoothed flat {dampedFlat:F1}, smoothed uneven {dampedRough:F1}, drawn avatar uneven {rough.Avatar.RmsJerk:F1}");
        Assert.True(plainRough > plainFlat * 1.5, "the uneven ground made the plain camera rougher");
        Assert.True(Math.Abs(dampedRough - rough.Avatar.RmsJerk) <= 0.01 * rough.Avatar.RmsJerk, $"uneven ground: camera {dampedRough:F1} against the drawn avatar {rough.Avatar.RmsJerk:F1}");
    }

    [Theory]
    [InlineData("walk-forward", 0.5, -1.0, 4.0)]
    [InlineData("walk-and-stop", 0.5, 3.0, 5.0)]
    public void No_scripted_case_is_rougher_for_the_camera_than_before(string name, double down, double up, double seconds)
    {
        Key[] keys = up < 0 ? [new(down, true, MoveCommand.Forward)] : [new(down, true, MoveCommand.Forward), new(up, false, MoveCommand.Forward)];
        double before = JerkMeter.Of(MovementRig.Run(keys, seconds, camera: Plain()), 0.4, seconds).Camera.RmsJerk;
        double after = JerkMeter.Of(MovementRig.Run(keys, seconds, camera: new FollowCamera()), 0.4, seconds).Camera.RmsJerk;
        output.WriteLine($"{name}: camera rms jerk before {before:F1}, after {after:F1}");
        Assert.True(after <= before + 1e-6, $"{name}: {after:F1} against {before:F1}");
    }

    [Fact]
    public void A_walk_and_a_turn_is_no_rougher_for_the_camera_than_before()
    {
        Key[] keys = [new(0.5, true, MoveCommand.Forward), new(1.5, true, MoveCommand.TurnRight), new(2.5, false, MoveCommand.TurnRight), new(4.0, false, MoveCommand.Forward)];
        double before = JerkMeter.Of(MovementRig.Run(keys, 5.5, camera: Plain()), 0.4, 5.5).Camera.RmsJerk;
        double after = JerkMeter.Of(MovementRig.Run(keys, 5.5, camera: new FollowCamera()), 0.4, 5.5).Camera.RmsJerk;
        output.WriteLine($"walk-and-turn: camera rms jerk before {before:F1}, after {after:F1}");
        Assert.True(after <= before + 1e-6, $"{after:F1} against {before:F1}");
    }
}
