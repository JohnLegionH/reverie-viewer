// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A seat that asks for mouse-look: the view enters real mouse-look when the avatar sits there and, as the official viewer does, stays in it on stand until the usual exits;
/// a mouse-look the person chose is theirs. Engine-free; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class SeatMouseLookTests
{
    private static (MouseLookMode Mode, SeatMouseLook Seat, List<bool> Changes) Make(bool allowed = true)
    {
        var mode = new MouseLookMode { Allowed = allowed };
        var changes = new List<bool>();
        mode.Changed += changes.Add;
        return (mode, new SeatMouseLook(mode), changes);
    }

    [Fact]
    public void Sitting_on_a_seat_that_forces_mouse_look_enters_it_and_standing_leaves_it_in_place()
    {
        // The official viewer's stand clears only the seat's camera (llvoavatar.cpp, getOffObject); the camera mode stays mouse-look.
        var (mode, seat, changes) = Make();
        seat.Update(sitting: true, seatForcesMouseLook: true);
        Assert.True(mode.Active);
        Assert.True(mode.Forced);
        seat.Update(sitting: false, seatForcesMouseLook: false);
        Assert.True(mode.Active);
        Assert.False(mode.Forced);
        Assert.Equal([true], changes);
    }

    [Fact]
    public void After_standing_the_usual_exits_still_end_the_mouse_look_the_seat_started()
    {
        foreach (var exit in new Action<MouseLookMode>[] { m => m.Escape(), m => m.WheelOut(), m => m.FocusGained(), m => m.Minimized(), m => m.Leave(), m => m.Toggle() })
        {
            var (mode, seat, changes) = Make();
            seat.Update(true, true);
            seat.Update(false, false);
            Assert.True(mode.Active);
            exit(mode);
            Assert.False(mode.Active);
            Assert.Equal([true, false], changes);
        }
    }

    [Fact]
    public void Standing_then_sitting_on_a_seat_that_does_not_force_it_leaves_the_mouse_look_alone()
    {
        var (mode, seat, changes) = Make();
        seat.Update(true, true);
        seat.Update(false, false);
        seat.Update(true, false);
        Assert.True(mode.Active);
        Assert.Equal([true], changes);
    }

    [Fact]
    public void Entering_happens_once_not_every_frame()
    {
        var (mode, seat, changes) = Make();
        for (int i = 0; i < 5; i++)
            seat.Update(true, true);
        Assert.Equal([true], changes);
        Assert.True(mode.Active);
    }

    [Fact]
    public void A_seat_that_does_not_force_it_changes_nothing()
    {
        var (mode, seat, changes) = Make();
        seat.Update(sitting: true, seatForcesMouseLook: false);
        seat.Update(sitting: false, seatForcesMouseLook: false);
        Assert.False(mode.Active);
        Assert.Empty(changes);
    }

    [Fact]
    public void A_person_who_leaves_mouse_look_while_seated_stays_out_until_the_next_sit()
    {
        var (mode, seat, _) = Make();
        seat.Update(true, true);
        mode.Escape();
        Assert.False(mode.Active);
        Assert.False(mode.Forced);
        seat.Update(true, true);   // still seated on the same seat
        Assert.False(mode.Active);
        seat.Update(false, false);
        seat.Update(true, true);   // a new sit asks again
        Assert.True(mode.Active);
    }

    [Fact]
    public void A_mouse_look_the_person_chose_before_sitting_is_not_taken_away_on_stand()
    {
        var (mode, seat, _) = Make();
        mode.Toggle();
        Assert.True(mode.Active);
        seat.Update(true, true);
        Assert.False(mode.Forced);
        seat.Update(false, false);
        Assert.True(mode.Active);
    }

    [Fact]
    public void Nothing_enters_mouse_look_when_the_setting_is_off()
    {
        var (mode, seat, changes) = Make(allowed: false);
        seat.Update(true, true);
        Assert.False(mode.Active);
        Assert.False(mode.Forced);
        Assert.Empty(changes);
    }

    [Fact]
    public void The_usual_exits_all_end_a_forced_mouse_look_and_clear_the_mark()
    {
        foreach (var exit in new Action<MouseLookMode>[] { m => m.Escape(), m => m.WheelOut(), m => m.FocusGained(), m => m.Minimized(), m => m.Leave(), m => m.Toggle() })
        {
            var (mode, seat, _) = Make();
            seat.Update(true, true);
            Assert.True(mode.Forced);
            exit(mode);
            Assert.False(mode.Active);
            Assert.False(mode.Forced);
        }
    }
}
