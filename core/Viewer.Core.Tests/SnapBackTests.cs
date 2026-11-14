// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Avatars;
using Xunit;
using Xunit.Abstractions;
using Key = Viewer.Core.Tests.MovementRig.Key;

namespace Viewer.Core.Tests;

/// <summary>
/// The drawn own avatar never steps back against its direction of travel while keys hold it moving: scripted walks and flights
/// through the key input, the own-avatar driver and the drawn motion against a model region (<see cref="MovementRig"/>), counting
/// the frames in which the drawn position moves against the travel by more than <see cref="Threshold"/> metres. After a key comes
/// up the avatar coasts: it is carried on until the region's stop arrives and then settles on the region's position, so it may
/// come back by up to one network trip at its speed (<see cref="ComeBack"/>). Every test builds
/// its own rig: no process-wide state, so the class runs in parallel with the others.
/// </summary>
public class SnapBackTests(ITestOutputHelper output)
{
    /// <summary>A step back of this many metres in one drawn frame counts (a walk covers about 0.04 m a frame).</summary>
    public const float Threshold = 0.02f;

    private static Key Down(double at, MoveCommand c) => new(at, true, c);
    private static Key Up(double at, MoveCommand c) => new(at, false, c);

    /// <summary>The frames between <paramref name="from"/> and <paramref name="to"/> seconds in which the drawn soles move against
    /// <paramref name="travel"/> (a unit vector) by more than the threshold.</summary>
    public static List<(double At, float Back)> SnapBacks(MovementRig.Result run, Vector3 travel, double from, double to)
    {
        var found = new List<(double, float)>();
        for (int i = 1; i < run.Frames.Count; i++)
        {
            double at = run.Frames[i].At;
            if (at < from || at > to) continue;
            float along = Vector3.Dot(run.Frames[i].Soles - run.Frames[i - 1].Soles, travel);
            if (along < -Threshold) found.Add((at, along));
        }
        return found;
    }

    /// <summary>How far the drawn soles come back against <paramref name="travel"/> in all, between <paramref name="from"/> and <paramref name="to"/> seconds.</summary>
    public static float ComeBack(MovementRig.Result run, Vector3 travel, double from, double to)
    {
        float back = 0;
        for (int i = 1; i < run.Frames.Count; i++)
        {
            double at = run.Frames[i].At;
            if (at < from || at > to) continue;
            back += MathF.Max(0, -Vector3.Dot(run.Frames[i].Soles - run.Frames[i - 1].Soles, travel));
        }
        return back;
    }

    /// <summary>The most a flying avatar may come back after its key is released: its speed (16.38 m/s) over the rig's longest
    /// network trip (0.04 s).</summary>
    private const float MostComeBack = 16.38f * 0.04f;

    private void Report(string name, List<(double At, float Back)> backs) =>
        output.WriteLine($"{name}\tsnap_backs={backs.Count}\t{string.Join(" ", backs.Take(8).Select(b => $"{b.At:F2}s:{b.Back:F3}m"))}");

    public static IEnumerable<object[]> Backlogs() { yield return [0]; yield return [100]; yield return [400]; }

    [Theory]
    [MemberData(nameof(Backlogs))]
    public void A_walk_forward_never_steps_back(int backlog)
    {
        var run = MovementRig.Run([Down(0.5, MoveCommand.Forward), Up(4.0, MoveCommand.Forward)], 5.0, backlog: backlog);
        var backs = SnapBacks(run, Vector3.UnitX, 0.8, 4.0);
        Report($"walk backlog={backlog}", backs);
        Assert.True(run.Frames.Last().Soles.X - run.Frames.First().Soles.X > 5, "the avatar walked");
        Assert.Empty(backs);
    }

    [Theory]
    [MemberData(nameof(Backlogs))]
    public void A_flight_forward_never_steps_back(int backlog)
    {
        Key[] keys = [Down(0.2, MoveCommand.ToggleFly), Up(0.25, MoveCommand.ToggleFly), Down(0.6, MoveCommand.Forward), Up(5.0, MoveCommand.Forward)];
        var run = MovementRig.Run(keys, 6.0, backlog: backlog);
        var backs = SnapBacks(run, Vector3.UnitX, 1.0, 5.0);   // while the key is held
        float after = ComeBack(run, Vector3.UnitX, 5.0, 6.0);    // the coast to the region's stop after the key comes up
        Report($"fly backlog={backlog}", backs);
        output.WriteLine($"fly backlog={backlog}\tcome_back_after_release={after:F3}m");
        Assert.True(run.Frames.Last().Soles.X - run.Frames.First().Soles.X > 40, "the avatar flew");
        Assert.Empty(backs);
        Assert.True(after <= MostComeBack, $"the avatar came back {after:F3} m after the release (limit {MostComeBack:F2} m)");
    }

    [Theory]
    [MemberData(nameof(Backlogs))]
    public void A_climb_and_a_descent_in_flight_never_step_back(int backlog)
    {
        Key[] keys = [Down(0.2, MoveCommand.ToggleFly), Up(0.25, MoveCommand.ToggleFly), Down(0.6, MoveCommand.Jump), Up(3.0, MoveCommand.Jump), Down(3.5, MoveCommand.Down), Up(5.0, MoveCommand.Down)];
        var run = MovementRig.Run(keys, 6.0, backlog: backlog);
        var up = SnapBacks(run, Vector3.UnitZ, 1.0, 3.0);     // while the key is held
        var down = SnapBacks(run, -Vector3.UnitZ, 4.0, 5.0);
        float afterUp = ComeBack(run, Vector3.UnitZ, 3.0, 3.5), afterDown = ComeBack(run, -Vector3.UnitZ, 5.0, 6.0);   // the coast to the region's stop
        Report($"climb backlog={backlog}", up);
        Report($"descend backlog={backlog}", down);
        output.WriteLine($"climb backlog={backlog}\tcome_back_after_release={afterUp:F3}m\tdescend come_back_after_release={afterDown:F3}m");
        Assert.True(run.Frames.Max(f => f.Soles.Z) - run.Frames.First().Soles.Z > 5, "the avatar climbed");
        Assert.Empty(up);
        Assert.Empty(down);
        Assert.True(afterUp <= MostComeBack, $"the avatar came back {afterUp:F3} m after the climb's release (limit {MostComeBack:F2} m)");
        Assert.True(afterDown <= MostComeBack, $"the avatar came back {afterDown:F3} m after the descent's release (limit {MostComeBack:F2} m)");
    }
}
