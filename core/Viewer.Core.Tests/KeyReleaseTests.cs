// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Motion;
using Xunit;
using F = Viewer.Motion.AgentControlFlags;

namespace Viewer.Core.Tests;

/// <summary>
/// What goes to the region when a movement key comes up, as the official viewer does it (llviewerinput.cpp: a key-up
/// handler returns without touching the agent; llagent.cpp: the control bits are cleared after each update and set again
/// only by keys still held). The stop bit is set by the stop-moving key and by the viewer's own stops (going away, the
/// end of autopilot, the camera turning to a chatter), never by releasing a walk, run, fly or turn key.
/// </summary>
public class KeyReleaseTests
{
    private const double Frame = 1.0 / 60;

    /// <summary>Holds <paramref name="cmd"/> for <paramref name="hold"/> seconds, releases it, and returns every update's bits for a second after.</summary>
    private static List<uint> AfterRelease(AgentControlInput input, MoveCommand cmd, double hold, bool inAir)
    {
        double now = 10;
        input.Press(cmd, now);
        for (double end = now + hold; now < end; now += Frame) input.Frame(now, Frame, inAir, 0);
        input.Release(cmd);
        var after = new List<uint>();
        for (double end = now + 1.0; now < end; now += Frame) after.Add(input.Frame(now, Frame, inAir, 0));
        return after;
    }

    [Fact]
    public void Releasing_a_walk_run_slide_or_turn_key_clears_its_bits_and_sends_no_stop_bit()
    {
        foreach (var cmd in new[]
        {
            MoveCommand.Forward, MoveCommand.Backward, MoveCommand.SlideLeft, MoveCommand.SlideRight,
            MoveCommand.RunForward, MoveCommand.RunBackward, MoveCommand.RunLeft, MoveCommand.RunRight,
            MoveCommand.TurnLeft, MoveCommand.TurnRight,
        })
        foreach (double hold in new[] { 0.1, 1.0 })   // a tap (inside the nudge) and a walk
        {
            var input = new AgentControlInput();
            input.AdoptHeading(0);
            var after = AfterRelease(input, cmd, hold, inAir: false);
            Assert.True(after.All(c => (c & F.Stop) == 0), $"{cmd} held {hold} s: the stop bit went out after the release");
            Assert.True(after.All(c => c == 0), $"{cmd} held {hold} s: bits {after.FirstOrDefault(c => c != 0):X} after the release");
        }
    }

    [Fact]
    public void Releasing_a_key_while_flying_leaves_only_the_fly_bit_and_sends_no_stop_bit()
    {
        foreach (var cmd in new[] { MoveCommand.Jump, MoveCommand.Down, MoveCommand.Forward, MoveCommand.SlideLeft, MoveCommand.TurnRight })
        {
            var input = new AgentControlInput();
            input.AdoptHeading(0);
            input.AdoptFlying(true);
            var after = AfterRelease(input, cmd, 1.0, inAir: true);
            Assert.True(after.All(c => (c & F.Stop) == 0), $"{cmd}: the stop bit went out after the release in flight");
            Assert.True(after.All(c => c == F.Fly), $"{cmd}: bits {after.FirstOrDefault(c => c != F.Fly):X} after the release in flight");
        }
    }

    [Fact]
    public void Losing_focus_while_walking_clears_the_keys_and_sends_no_stop_bit()
    {
        var input = new AgentControlInput();
        input.AdoptHeading(0);
        input.Press(MoveCommand.Forward, 0);
        double now = 0;
        for (; now < 1.0; now += Frame) input.Frame(now, Frame, false, 0);
        input.SetFocused(false);
        for (double end = now + 1.0; now < end; now += Frame)
            Assert.Equal(0u, input.Frame(now, Frame, false, 0));
    }

    [Fact]
    public void A_stop_the_viewer_sets_itself_goes_out_with_the_next_update_only()
    {
        // going away, the end of autopilot and the camera turning to a chatter set the stop bit on the agent; the bits are
        // cleared again after the update that carried them
        var input = new AgentControlInput();
        input.AdoptHeading(0);
        input.SetControlFlags(F.Stop);
        Assert.Equal(F.Stop, input.Frame(0.0, Frame, false, 0));
        Assert.Equal(0u, input.Frame(Frame, Frame, false, 0));

        // beside a held key, whose bits stay
        input.Press(MoveCommand.Forward, 1.0);
        input.Frame(1.0, Frame, false, 0);
        input.SetControlFlags(F.Stop);
        Assert.Equal(F.Stop | F.NudgeAtPos, input.Frame(1.0 + Frame, Frame, false, 0));
        Assert.Equal(F.NudgeAtPos, input.Frame(1.0 + 2 * Frame, Frame, false, 0));
    }
}
