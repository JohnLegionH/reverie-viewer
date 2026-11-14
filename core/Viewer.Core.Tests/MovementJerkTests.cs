// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Avatars;
using Xunit;
using Xunit.Abstractions;
using F = Viewer.Motion.AgentControlFlags;
using Key = Viewer.Core.Tests.MovementRig.Key;

namespace Viewer.Core.Tests;

/// <summary>
/// How rough the user's own avatar and the camera behind it are drawn while keys move it, measured with no login
/// (<see cref="MovementRig"/>, <see cref="JerkMeter"/>). The figures of each scripted case are written to the test output
/// (and, when NV_JERK_OUT names a file, appended to it as tab-separated lines) so a change can be compared with the code
/// before it. Every test builds its own rig: no process-wide state, so the class runs in parallel with the others.
/// </summary>
public class MovementJerkTests(ITestOutputHelper output)
{
    private static Key Down(double at, MoveCommand c) => new(at, true, c);
    private static Key Up(double at, MoveCommand c) => new(at, false, c);

    /// <summary>The scripted cases: name, keys, length of the run in seconds.</summary>
    public static IEnumerable<object[]> Cases()
    {
        yield return ["walk-forward", new[] { Down(0.5, MoveCommand.Forward) }, 4.0];
        yield return ["walk-and-stop", new[] { Down(0.5, MoveCommand.Forward), Up(3.0, MoveCommand.Forward) }, 5.0];
        yield return ["walk-and-turn", new[] { Down(0.5, MoveCommand.Forward), Down(1.5, MoveCommand.TurnRight), Up(2.5, MoveCommand.TurnRight), Up(4.0, MoveCommand.Forward) }, 5.5];
        yield return ["double-tap-run", new[] { Down(0.5, MoveCommand.Forward), Up(0.58, MoveCommand.Forward), Down(0.68, MoveCommand.Forward), Up(3.0, MoveCommand.Forward) }, 4.5];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_drawn_path_of_each_case_has_a_jerk_figure(string name, Key[] keys, double seconds)
    {
        var run = MovementRig.Run(keys, seconds);
        var (avatar, camera) = JerkMeter.Of(run, 0.4, seconds);
        // the lowest drawn speed from the first moment the avatar moves faster than 1 m/s to the last key release (or the end)
        double began = run.Frames.First(f => f.At > 0.5 && JerkMeter.SpeedAt(run, f.At) > 1.0f).At;
        double ended = keys.Where(k => !k.Down).Select(k => k.At).DefaultIfEmpty(seconds).Max();
        float lowest = JerkMeter.LowestSpeed(run, began, ended);
        string line = $"{name}\tlowest_speed_while_held={lowest:F2}\tavatar_rms_jerk={avatar.RmsJerk:F2}\tavatar_peak_jerk={avatar.PeakJerk:F1}\tavatar_peak_accel={avatar.PeakAcceleration:F2}"
            + $"\tcamera_rms_jerk={camera.RmsJerk:F2}\tcamera_peak_jerk={camera.PeakJerk:F1}\tstop_bits={run.Sent.Count(s => (s.Controls & F.Stop) != 0)}\thalts={run.Halts}";
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("NV_JERK_OUT") is { Length: > 0 } path) File.AppendAllText(path, line + "\n");
        Assert.True(run.Frames.Count > 100, "the own avatar was drawn");
        Assert.True(avatar.RmsJerk > 0);
    }

    // ---------------- a tap followed by a press: the double-tap window ----------------

    private static readonly Key[] DoubleTap =
        [Down(0.5, MoveCommand.Forward), Up(0.58, MoveCommand.Forward), Down(0.68, MoveCommand.Forward), Up(3.0, MoveCommand.Forward)];

    [Fact]
    public void A_key_released_and_pressed_again_inside_the_double_tap_window_sends_no_stop_bit_and_no_brake()
    {
        var run = MovementRig.Run(DoubleTap, 2.5);   // the second press is still held at the end
        // the key is released at 0.58 s and pressed again at 0.68 s, inside the 0.25 s after the first press: the region
        // is not told to stop, and the drawn avatar is not braked, between the two presses
        Assert.Empty(run.Sent.Where(s => s.At < 2.0 && (s.Controls & F.Stop) != 0));
        Assert.Equal(0, run.Halts);
    }

    [Fact]
    public void The_drawn_avatar_does_not_slow_between_two_presses_of_a_key()
    {
        var run = MovementRig.Run(DoubleTap, 2.5);
        var moving = run.Frames.First(f => f.At > 0.5 && JerkMeter.SpeedAt(run, f.At) > 1.0f).At;
        float lowest = JerkMeter.LowestSpeed(run, moving, 2.5);
        Assert.True(lowest > 1.0f, $"the drawn avatar slowed to {lowest:F2} m/s after it was moving ({moving:F2} s)");
    }

    [Fact]
    public void A_tap_with_no_second_press_sends_no_stop_bit_and_the_avatar_comes_to_rest_as_the_region_stops_it()
    {
        var run = MovementRig.Run([Down(0.5, MoveCommand.Forward), Up(0.58, MoveCommand.Forward)], 2.0);
        // a key-up sends nothing of its own: no stop bit when the double-tap window ends or at any other time
        Assert.Empty(run.Sent.Where(s => (s.Controls & F.Stop) != 0));
        Assert.Equal(0, run.Halts);
        Assert.True(JerkMeter.SpeedAt(run, 1.9) < 0.1f, $"the drawn avatar still moves at {JerkMeter.SpeedAt(run, 1.9):F2} m/s 1.3 s after the tap");
    }

    [Fact]
    public void A_key_held_past_the_double_tap_window_sends_no_stop_bit_when_released_and_the_avatar_coasts_to_rest()
    {
        var run = MovementRig.Run([Down(0.5, MoveCommand.Forward), Up(1.5, MoveCommand.Forward)], 3.0);
        Assert.Empty(run.Sent.Where(s => (s.Controls & F.Stop) != 0));
        Assert.Equal(0, run.Halts);
        // still walking a tenth of a second after the key came up (the region moves it for a quarter of a second more), at rest later
        Assert.True(JerkMeter.SpeedAt(run, 1.6) > 1.0f, $"the drawn avatar slowed to {JerkMeter.SpeedAt(run, 1.6):F2} m/s 0.1 s after the release");
        Assert.True(JerkMeter.SpeedAt(run, 2.9) < 0.1f, $"the drawn avatar still moves at {JerkMeter.SpeedAt(run, 2.9):F2} m/s 1.4 s after the release");
    }
}
