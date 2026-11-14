// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Shell;
using Xunit;
using Xunit.Abstractions;
using Key = Viewer.Core.Tests.MovementRig.Key;

namespace Viewer.Core.Tests;

/// <summary>
/// How evenly the view moves while the avatar walks: the camera's step from one drawn frame to the next, against the avatar's step. The
/// region's updates arrive 20 ms late with 20 ms of jitter and, in the uneven cases, the height it states wobbles by 3 cm
/// (<see cref="MovementRig"/>). The figures are printed for a before and after comparison; the assertions hold the camera to the
/// official smoothing (an uneven region step is not passed straight into the view).
/// </summary>
public class CameraStepMeasure(ITestOutputHelper output)
{
    private static readonly Key[] Walk = [new(0.5, true, MoveCommand.Forward), new(6.0, false, MoveCommand.Forward)];

    private static (double Mean, double P95, double Max, double Spread, double Ratio) Steps(MovementRig.Result run, double from, double to)
    {
        var frames = run.Frames.Where(f => f.At >= from && f.At <= to).ToList();
        var eye = new List<double>();
        var soles = new List<double>();
        for (int i = 1; i < frames.Count; i++)
        {
            eye.Add((frames[i].Eye - frames[i - 1].Eye).Length());
            soles.Add((frames[i].Soles - frames[i - 1].Soles).Length());
        }
        var sorted = eye.OrderBy(x => x).ToList();
        double mean = eye.Average();
        double spread = Math.Sqrt(eye.Average(x => (x - mean) * (x - mean)));
        return (mean, sorted[(int)(sorted.Count * 0.95)], sorted[^1], spread, spread / Math.Max(1e-9, soles.Average() > 0 ? soles.Average() : 1));
    }

    [Theory]
    [InlineData("walk, even ground", 0.0)]
    [InlineData("walk, uneven ground (3 cm)", 0.03)]
    public void The_cameras_step_per_frame_while_walking(string name, double wobble)
    {
        var run = MovementRig.Run(Walk, 7.0, wobble: wobble, camera: new FollowCamera());
        var (mean, p95, max, spread, ratio) = Steps(run, 1.5, 5.5);
        output.WriteLine($"{name}: camera step per frame, mean {mean * 1000:F2} mm, p95 {p95 * 1000:F2} mm, max {max * 1000:F2} mm, spread (sd) {spread * 1000:F2} mm, spread over the avatar's mean step {ratio:F3}");
        Assert.True(max < 0.2, $"{name}: the camera moved {max:F3} m in one frame");
    }
}
