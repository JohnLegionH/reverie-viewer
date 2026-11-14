// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The user's own avatar between the region's updates, against the official viewer's rules (llviewerobject.cpp interpolateLinearMotion and
/// processUpdateMessage, lldrawable.cpp updateXform; see Viewer.Motion's NOTICE). Each test builds its own motion: no process-wide
/// state, so the class runs in parallel with the others.
/// </summary>
public class OfficialMotionTests
{
    private static Vector3 Drawn(ObjectMotion m, double at) => m.At(at).Position;

    private static ObjectMotion Walking(float speed = 2.46f)
    {
        var m = new ObjectMotion();
        m.Update(new Vector3(10, 10, 20), new Vector3(speed, 0, 0), Quaternion.Identity, 0);
        return m;
    }

    [Fact]
    public void Between_updates_the_object_is_carried_along_its_velocity_every_frame_with_no_taper_while_the_circuit_is_alive()
    {
        var m = Walking(3f);
        double t = 0;
        for (int i = 0; i < 600; i++) { t += 1.0 / 60; m.At(t); }   // ten seconds without an update
        // the drawn position trails the object by the damping lag: speed * 0.06 / ln 2 = 0.26 m at 3 m/s
        float travelled = Drawn(m, t).X - 10;
        Assert.InRange(travelled, 3f * 10 - 0.5f, 3f * 10);
    }

    [Fact]
    public void A_quiet_circuit_phases_the_carried_motion_out_by_three_seconds()
    {
        var m = Walking(3f);
        m.CircuitQuietSeconds = 10;
        double t = 0;
        for (int i = 0; i < 60 * 6; i++) { t += 1.0 / 60; m.At(t); }
        float at6 = Drawn(m, t).X;
        for (int i = 0; i < 60 * 4; i++) { t += 1.0 / 60; m.At(t); }
        float at10 = Drawn(m, t).X;
        Assert.True(at10 - at6 < 0.05f, $"still moving after the phase-out: {at10 - at6:F3} m");
        Assert.InRange(at6 - 10, 6.0f, 6.9f);   // 2 s at full speed and the taper to 3 s: 2 * 3 + 0.5 * 3 = 7.5 m of object travel, less the damping lag
    }

    [Fact]
    public void An_update_sets_the_object_at_once_and_the_drawn_position_follows_with_a_0_06_second_time_constant()
    {
        var m = new ObjectMotion();
        m.Update(new Vector3(10, 10, 20), Vector3.Zero, Quaternion.Identity, 0);
        m.At(0.485);
        // a 1 m correction, taken in the next frame's network step before the frame moves anything (llappviewer.cpp idle)
        m.Update(new Vector3(11, 10, 20), Vector3.Zero, Quaternion.Identity, 0.5);
        double t = 0.485;
        for (int i = 0; i < 4; i++) { t += 0.015; m.At(t); }                       // four short frames from the last one drawn: 0.06 s in all
        float left = 11 - Drawn(m, t).X;
        // half the way after one time constant (the official interpolant is 1 - 2^(-dt / 0.06))
        Assert.InRange(left, 0.48f, 0.52f);
    }

    [Fact]
    public void A_correction_of_more_than_ten_metres_is_taken_at_once()
    {
        var m = new ObjectMotion();
        m.Update(new Vector3(10, 10, 20), Vector3.Zero, Quaternion.Identity, 0);
        m.At(0.4);
        // taken in the next frame before the frame moves anything (llappviewer.cpp idle): one frame's damping still leaves it more than 10 m off
        m.Update(new Vector3(30, 10, 20), Vector3.Zero, Quaternion.Identity, 0.416);
        Assert.Equal(30f, Drawn(m, 0.416).X, 3);
    }

    [Fact]
    public void An_update_in_the_same_frame_still_moves_the_object_on_over_the_time_since_the_last_frame()
    {
        var m = Walking(3f);
        m.At(0.5);
        m.Update(new Vector3(11.5f, 10, 20), new Vector3(3, 0, 0), Quaternion.Identity, 0.6);
        float before = Drawn(m, 0.5).X;
        float after = Drawn(m, 0.6).X;
        Assert.True(after > before, "a frame that carries an update stood still");
    }

    /// <summary>
    /// The official viewer takes an update in its network step, after the frame's time is set and before the frame moves the objects
    /// (llappviewer.cpp idle: updateFrameTime, then idleNetwork, then gObjectList.update). processUpdateMessage sets the object's place and
    /// makes the update's time the time of its last move (llviewerobject.cpp 2454-2455), and interpolateLinearMotion does nothing while the
    /// update is not older than the frame (2541-2545): the place an update states is never carried on by the time before it came. Here the
    /// frame is one time, so the same must hold whether a region's update is taken before or after the frame moves the object (two regions
    /// framed in either order): the drawn place must be the same in every frame after.
    /// An update that changes the speed is the same: taken after the frame has moved what is drawn, it is drawn as if it had been taken before,
    /// which is the only order the official viewer has.
    /// </summary>
    [Theory]
    [InlineData(true, 18f)]
    [InlineData(false, 30f)]
    [InlineData(false, 6f)]
    public void An_update_places_the_object_the_same_whether_it_is_taken_before_or_after_the_frame_moves_it(bool steady, float speedAfter)
    {
        const double frame = 0.05;
        var taken = new[] { new ObjectMotion(), new ObjectMotion() };
        foreach (var m in taken) m.Update(new Vector3(10, 10, 20), new Vector3(18, 0, 0), Quaternion.Identity, 0);
        double t = 0;
        for (int i = 0; i < 10; i++) { t += frame; foreach (var m in taken) m.At(t); }
        t += frame;
        // the update: where the avatar is by its velocity (a steady mover), or with a new velocity (a change of speed)
        var stated = steady ? new Vector3(10 + 18f * (float)t, 10, 20) : new Vector3(10 + 18f * (float)t - 3f, 10, 20);
        var velocity = new Vector3(speedAfter, 0, 0);
        taken[0].Update(stated, velocity, Quaternion.Identity, t);   // taken before the frame moves it
        taken[0].At(t);
        taken[1].At(t);
        taken[1].Update(stated, velocity, Quaternion.Identity, t);   // taken after
        double worst = 0;
        string where = "";
        for (int i = 0; i < 20; i++)
        {
            t += frame;
            float a = Drawn(taken[0], t).X, b = Drawn(taken[1], t).X;
            if (Math.Abs(a - b) > worst) { worst = Math.Abs(a - b); where = $"{t:0.00} s: {a:0.000} against {b:0.000}"; }
        }
        Assert.True(worst < 1e-3, $"the update is placed differently by when it is taken in the frame: {worst:0.000} m apart ({where})");
    }

    [Fact]
    public void The_same_state_applied_twice_would_rewind_the_object_so_the_coordinator_applies_each_change_once()
    {
        // what the official viewer never sees and the presenter must not do: an old state applied again after the object was carried on
        var m = Walking(3f);
        for (double t = 1.0 / 60; t <= 1.0; t += 1.0 / 60) m.At(t);
        float carried = Drawn(m, 1.0).X;
        m.Update(new Vector3(10, 10, 20), new Vector3(3, 0, 0), Quaternion.Identity, 1.0);   // the first state again, a second late
        for (double t = 1.0 + 1.0 / 60; t <= 1.3; t += 1.0 / 60) m.At(t);
        Assert.True(Drawn(m, 1.3).X < carried + 3f * 0.3f - 2.0f, "the second application did not rewind the object, so this test no longer shows why each change is applied once");
    }

    [Fact]
    public void A_halted_avatar_stands_until_the_region_says_it_stopped_and_ignores_updates_that_still_say_it_moves()
    {
        var m = Walking(3f);
        for (double t = 1.0 / 60; t <= 1.0; t += 1.0 / 60) m.At(t);
        m.Halt(1.0);
        m.Update(new Vector3(12, 10, 20), new Vector3(3, 0, 0), Quaternion.Identity, 1.1);
        for (double t = 1.0 + 1.0 / 60; t <= 1.5; t += 1.0 / 60) m.At(t);
        float stopped = Drawn(m, 1.5).X;   // the drawn position has closed up on the object, which no longer moves
        for (double t = 1.5 + 1.0 / 60; t <= 1.6; t += 1.0 / 60) m.At(t);
        Assert.Equal(stopped, Drawn(m, 1.6).X, 2);
        Assert.InRange(stopped, 12.5f, 13.1f);   // not the 12 m of the update that still said it moved
        m.Update(new Vector3(11.7f, 10, 20), Vector3.Zero, Quaternion.Identity, 1.61);
        for (double t = 1.62; t <= 2.2; t += 1.0 / 60) m.At(t);
        Assert.Equal(11.7f, Drawn(m, 2.2).X, 2);
    }

    /// <summary>
    /// The official viewer takes an update where it says, at the time it arrives (processUpdateMessage sets the place at once; nothing in 26.4
    /// moves a late update on). Updates 5 m apart at 10 m/s are due every half second; the second one arrives 0.3 s late (a stalled frame, a slow
    /// packet), so the avatar is put back by its speed times its lateness and carried on from there.
    /// </summary>
    [Fact]
    public void A_steady_movers_update_that_arrives_late_is_taken_where_it_says()
    {
        var m = new ObjectMotion();
        var v = new Vector3(10, 0, 0);
        m.Update(new Vector3(10, 10, 20), v, Quaternion.Identity, 0);
        double t = 0;
        for (int i = 0; i < 48; i++) { t += 1.0 / 60; m.At(t); }   // 0.8 s
        m.Update(new Vector3(15, 10, 20), v, Quaternion.Identity, 0.8);                 // due at 0.5 s
        for (int i = 0; i < 12; i++) { t += 1.0 / 60; m.At(t); }
        // the object is where the update said, carried 0.2 s on; what is drawn has closed up on it but for the damping lag (speed x 0.06 / ln 2)
        float after = m.At(t).Position.X;
        Assert.InRange(after, 15f + 10f * 0.2f - 10f * 0.06f / MathF.Log(2f) - 0.05f, 15f + 10f * 0.2f);
    }
}
