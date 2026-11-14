// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// An avatar that changes region is one drawable that never jumps (llviewerobject.cpp setRegion keeps the object's motion; llviewerobject.cpp
/// interpolateLinearMotion carries it on every frame). The region left keeps drawing the avatar from the entered region's state until the entered
/// region draws it, so the two must put it in the same place in the frame the drawing is handed over.
/// </summary>
public class HandoffMotionTests
{
    private static readonly Guid Avatar = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly SimRef SimFrom = new(256000UL << 32 | 256000UL, "From");
    private static readonly SimRef SimTo = new(256256UL << 32 | 256000UL, "To");
    private const double Frame = 1.0 / 60;

    /// <summary>The avatar flies east at <paramref name="speed"/> m/s, crosses at t = 0.3 s; the entered region states it once and sends no more for a while
    /// (a region sends a moving avatar's position again only when its velocity changes or it has gone 5 m). Returns the position the region left draws
    /// (it follows the entered region's state) and the position the region entered draws, <paramref name="after"/> seconds after the crossing.</summary>
    private static (Vector3 Held, Vector3 Entered) DrawnAfterCrossing(bool own, float speed, double after)
    {
        double clock = 0;
        var hub = new IdentityHub { SelfAgent = own ? Avatar : Guid.NewGuid() };
        var from = new WorldState(new CoreLog(new ListSink(), "test"));
        var to = new WorldState(new CoreLog(new ListSink(), "test"));
        from.AttachIdentity(hub);
        to.AttachIdentity(hub);
        from.BeginRegion(SimFrom, new RegionInfo("from", 1000, 1000, 256, 256, true));
        to.BeginRegion(SimTo, new RegionInfo("to", 1001, 1000, 256, 256, true));
        hub.Current = from;
        var left = new AvatarScene(from, hub.SelfAgent, () => clock) { HoldHandoffs = true };
        var changes = new List<WorldChange>();
        from.Changed += changes.Add;
        var velocity = new Vector3(speed, 0, 0);
        var size = new Vector3(0.45f, 0.6f, 1.9f);
        from.ApplyAvatar(SimFrom, new AvatarData(900, Avatar, "Test User", new(240, 128, 21), Quaternion.Identity, size, velocity, 0));
        foreach (var c in changes) left.Apply(c);
        changes.Clear();
        for (; clock < 0.3; clock += Frame) left.Place(left.Entries.First(), out _, out _);

        hub.Current = to;
        var entered = new AvatarScene(to, hub.SelfAgent, () => clock);
        var enteredChanges = new List<WorldChange>();
        to.Changed += enteredChanges.Add;
        to.ApplyAvatar(SimTo, new AvatarData(1900, Avatar, "Test User", new(2, 128, 21), Quaternion.Identity, size, velocity, 0));
        foreach (var c in changes) left.Apply(c);
        foreach (var c in enteredChanges) entered.Apply(c);
        var held = Assert.Single(left.Held);
        var arrived = entered.Entries.Single();
        double crossedAt = clock;
        Vector3 heldAt = default, enteredAt = default;
        for (; clock <= crossedAt + after; clock += Frame)
        {
            left.Follow(held, hub.CurrentAvatar(Avatar)!);   // what the presenter does for a held avatar in every frame
            left.Place(held, out heldAt, out _);
            entered.Place(arrived, out enteredAt, out _);
        }
        return (heldAt, enteredAt);
    }

    /// <summary>
    /// The own avatar flies east at <paramref name="speed"/> m/s; the region left states it each time it has gone 5 m, as a region does. It
    /// crosses at x = 256 and the region entered states it there (where it crossed) only <paramref name="late"/> seconds later, as a region does once
    /// the viewer has told it the agent arrived. With <paramref name="enteredFirst"/> the region entered takes the avatar in a frame before the region left
    /// lets go of it, otherwise a frame after. The two scenes have clocks that started at different times, as each region's presenter had its own.
    /// The drawn body follows one entry at a time, as the shared body does: the region left's while it holds the avatar live, then the region entered's.
    /// Returns the largest distance, from 0.5 s to 2 s (the crossing is between 1 and 1.5 s), between the drawn body and where the official viewer draws the
    /// one object on the same statements taken in the same frames (<see cref="OfficialOwnAvatarReplay"/>; the region entered lies east of the region left).
    /// </summary>
    private static (double WorstStepError, int Frames, string Where) DrawnStepsAcrossLateCrossing(float speed, double late, bool enteredFirst, bool share)
    {
        double clock = 0;
        var hub = new IdentityHub { SelfAgent = Avatar };
        var from = new WorldState(new CoreLog(new ListSink(), "test"));
        var to = new WorldState(new CoreLog(new ListSink(), "test"));
        from.AttachIdentity(hub);
        to.AttachIdentity(hub);
        from.BeginRegion(SimFrom, new RegionInfo("from", 1000, 1000, 256, 256, true));
        to.BeginRegion(SimTo, new RegionInfo("to", 1001, 1000, 256, 256, true));
        hub.Current = from;
        var own = share ? new OwnMotionShare(() => clock) : null;
        var east = new Vector3(256, 0, 0);
        to.SetOriginOffset(east);
        var left = new AvatarScene(from, Avatar, () => clock + 40.0) { HoldHandoffs = true, OwnMotion = own, HasRegionAt = p => p.X >= 256 && p.X < 512 && p.Y >= 0 && p.Y < 256 };
        var entered = new AvatarScene(to, Avatar, () => clock - 3.0) { HoldHandoffs = true, OwnMotion = own, HasRegionAt = p => p.X >= -256 && p.X < 0 && p.Y >= 0 && p.Y < 256 };
        var official = new OfficialOwnAvatarReplay();
        static bool Known(Vector3 p) => p.X >= 0 && p.X < 512 && p.Y >= 0 && p.Y < 256;
        var officialFrom = new List<(Vector3, Vector3)>();
        var officialTo = new List<(Vector3, Vector3)>();
        double last = double.NaN;
        var leftChanges = new List<WorldChange>();
        var enteredChanges = new List<WorldChange>();
        from.Changed += leftChanges.Add;
        to.Changed += enteredChanges.Add;
        var velocity = new Vector3(speed, 0, 0);
        var size = new Vector3(0.45f, 0.6f, 1.9f);
        const float start = 236f;
        float lastStated = start;
        from.ApplyAvatar(SimFrom, new AvatarData(900, Avatar, "Test User", new(start, 128, 30), Quaternion.Identity, size, velocity, 0));
        foreach (var c in leftChanges) left.Apply(c);
        leftChanges.Clear();
        official.Statement(new(start, 128, 30), velocity, Vector3.Zero, 256, clock, Frame, 1f);
        double crossedAt = (256f - start) / speed, statedAt = crossedAt + late;
        int stage = 0;   // 0 the region left draws; 1 the region entered's statement has come; 2 both regions have taken it in
        float? previous = null;
        double worst = 0;
        int frames = 0;
        string where = "";
        for (; clock < 2.0; clock += Frame)
        {
            float trueX = start + speed * (float)clock;
            if (stage == 0 && trueX < 256f && trueX - lastStated >= 5f)
            {
                lastStated = trueX;
                from.ApplyAvatar(SimFrom, new AvatarData(900, Avatar, "Test User", new(trueX, 128, 30), Quaternion.Identity, size, velocity, 0));
                foreach (var c in leftChanges) left.Apply(c);
                leftChanges.Clear();
                official.Statement(new(trueX, 128, 30), velocity, Vector3.Zero, 256, clock, Frame, 1f);
            }
            if (stage == 0 && clock >= statedAt)
            {
                hub.Current = to;
                to.ApplyAvatar(SimTo, new AvatarData(1900, Avatar, "Test User", new(0f, 128, 30), Quaternion.Identity, size, velocity, 0));   // where it crossed
                if (enteredFirst)
                {
                    foreach (var c in enteredChanges) entered.Apply(c);
                    enteredChanges.Clear();
                    official.Statement(new(256f, 128, 30), velocity, east, 256, clock, Frame, 1f);   // the official viewer takes it in this frame too
                }
                else { foreach (var c in leftChanges) left.Apply(c); leftChanges.Clear(); }
                stage = 1;
            }
            else if (stage == 1)
            {
                foreach (var c in leftChanges) left.Apply(c);
                bool firstStatement = enteredChanges.Count > 0 && !enteredFirst;
                foreach (var c in enteredChanges) entered.Apply(c);
                leftChanges.Clear();
                enteredChanges.Clear();
                if (firstStatement) official.Statement(new(256f, 128, 30), velocity, east, 256, clock, Frame, 1f);   // taken a frame later here, and so there
                stage = 2;
            }
            foreach (var held in left.Held) left.Follow(held, hub.CurrentAvatar(Avatar)!);   // what the presenter does for a held avatar in every frame
            var leftEntry = left.Entries.FirstOrDefault();
            var enteredEntry = entered.Entries.FirstOrDefault();
            Vector3 drawn;
            if (leftEntry is not null && !left.IsHeld(leftEntry)) left.Place(leftEntry, out drawn, out _);   // the entry that drives the shared body
            else if (enteredEntry is not null) entered.Place(enteredEntry, out drawn, out _);
            else left.Place(leftEntry!, out drawn, out _);
            var drawnOfficial = official.Frame(clock, double.IsNaN(last) ? Frame : clock - last, 1f, Known);
            last = clock;
            // from when the first statement's damping has settled
            if (clock >= 0.5 && Math.Abs(drawn.X - drawnOfficial.X) > worst) { worst = Math.Abs(drawn.X - drawnOfficial.X); where = $"at {clock:0.000} s (crossed {crossedAt:0.000} s, stated {statedAt:0.000} s, stage {stage}), drawn {drawn.X:0.00}, official {drawnOfficial.X:0.00}"; }
            previous = drawn.X;
            frames++;
        }
        return (worst, frames, where);
    }

    [Fact]
    public void The_share_hands_the_same_motion_on_while_an_entry_uses_it_and_a_new_one_after_every_entry_has_gone_or_a_reset()
    {
        var share = new OwnMotionShare(() => 0);
        var first = share.Join(Avatar);
        Assert.Same(first, share.Join(Avatar));   // a crossing: the region entered's entry while the region left's still has it
        Assert.Equal(2, share.Users);
        share.Leave(first);
        share.Leave(first);
        Assert.Equal(0, share.Users);
        Assert.NotSame(first, share.Join(Avatar));   // every entry gone: a new object
        var held = share.Join(Avatar);
        share.Reset();   // a new presentation
        Assert.NotSame(held, share.Join(Avatar));
        Assert.NotSame(share.Join(Avatar), share.Join(Guid.NewGuid()));   // another agent never takes it
    }

    [Theory]
    [InlineData(18f, 0.2, true)]
    [InlineData(18f, 0.2, false)]
    [InlineData(15f, 0.15, true)]
    [InlineData(20f, 0.18, false)]
    public void The_own_avatar_is_one_object_across_a_crossing_whose_first_statement_comes_late(float speed, double late, bool enteredFirst)
    {
        // the official viewer takes the late statement where it says, so it steps back too: the body must be drawn where it draws it
        var (worst, frames, where) = DrawnStepsAcrossLateCrossing(speed, late, enteredFirst, share: true);
        Assert.True(frames > 80, $"{frames} frames");
        Assert.True(worst <= 0.1, $"the drawn body is {worst:0.000} m from the official viewer's across the hand-over, {where}");
    }

    /// <summary>
    /// The crossing as the presentation frames it. Each frame the clock moves on once, then one region's scene takes the changes waiting for it, the shared
    /// body is placed (by the region left's entry while it holds the avatar live, then by the region entered's), then the other region's scene takes its
    /// changes: so one region's updates are taken before the body is placed in the frame and the other's after, as the region framed first drives the
    /// shared body for every region. A region's statement is made in one frame and taken in the next. The agent flies east at 18 m/s; the region left
    /// states it every 5 m and stops at the border; the crossing message comes at the border, and the region entered states the avatar where it crossed
    /// <paramref name="late"/> seconds later, then every 5 m. Frames are 50 ms. Returns the largest distance, from the third statement of the run-up on,
    /// between the drawn body and where the official viewer draws the one object (<see cref="OfficialOwnAvatarReplay"/>), which takes every statement of a
    /// frame before it moves anything.
    /// </summary>
    private static (double WorstStepError, string Where) DrawnStepsAsPresented(bool enteredFirst, double late)
    {
        const float speed = 18f, crossX = 128f, startX = 110f;
        const double frame = 0.05;
        double clock = 0;
        var hub = new IdentityHub { SelfAgent = Avatar };
        var from = new WorldState(new CoreLog(new ListSink(), "test"));
        var to = new WorldState(new CoreLog(new ListSink(), "test"));
        from.AttachIdentity(hub);
        to.AttachIdentity(hub);
        from.BeginRegion(SimFrom, new RegionInfo("from", 1000, 1000, 256, 256, true));
        to.BeginRegion(SimTo, new RegionInfo("to", 1000, 1000, 256, 256, true));
        hub.Current = from;
        var own = new OwnMotionShare(() => clock);
        var left = new AvatarScene(from, Avatar, () => clock) { HoldHandoffs = true, OwnMotion = own };
        var entered = new AvatarScene(to, Avatar, () => clock) { HoldHandoffs = true, OwnMotion = own };
        var leftChanges = new List<WorldChange>();
        var enteredChanges = new List<WorldChange>();
        from.Changed += leftChanges.Add;
        to.Changed += enteredChanges.Add;
        var size = new Vector3(0.45f, 0.6f, 1.9f);
        var velocity = new Vector3(speed, 0, 0);
        AvatarData At(uint local, float x, Vector3 v) => new(local, Avatar, "Test User", new(x, 128, 40), Quaternion.Identity, size, v, 0);
        void TakeLeft() { foreach (var c in leftChanges) left.Apply(c); leftChanges.Clear(); }
        void TakeEntered() { foreach (var c in enteredChanges) entered.Apply(c); enteredChanges.Clear(); }
        float drawn = 0;
        var official = new OfficialOwnAvatarReplay();
        var statedThisFrame = new List<(float X, Vector3 V)>();
        void Place()
        {
            foreach (var held in left.Held) if (hub.CurrentAvatar(Avatar) is { } state) left.Follow(held, state);   // what the presenter does for a held avatar
            var leftEntry = left.Entries.FirstOrDefault();
            var enteredEntry = entered.Entries.FirstOrDefault();
            Vector3 soles;
            if (leftEntry is not null && !left.IsHeld(leftEntry)) left.Place(leftEntry, out soles, out _);
            else if (enteredEntry is not null) entered.Place(enteredEntry, out soles, out _);
            else if (leftEntry is not null) left.Place(leftEntry, out soles, out _);
            else return;   // no scene has the avatar yet (the first frame, when the region left is framed second)
            drawn = soles.X;
        }
        void Frame()
        {
            clock += frame;
            foreach (var (x, v) in statedThisFrame) official.Statement(new(x, 128, 40), v, Vector3.Zero, 4096, clock, frame, 1f);
            statedThisFrame.Clear();
            // the regions framed in either order, every region's changes taken before the body is placed (as the presentation does it)
            SharedBodyFrame.Run<int>(enteredFirst ? [1, 0] : [0, 1], r => { if (r == 1) TakeEntered(); else TakeLeft(); }, _ => { });
            Place();
        }
        from.ApplyAvatar(SimFrom, At(900, startX, Vector3.Zero));
        statedThisFrame.Add((startX, Vector3.Zero));
        for (int i = 0; i < 10; i++) { Frame(); official.Frame(clock, frame, 1f, _ => true); }
        double t0 = clock, crossedAt = t0 + (crossX - startX) / speed, statedAt = crossedAt + late;
        float lastStated = float.NegativeInfinity;
        bool crossed = false, inside = false;
        float? previous = null;
        double worst = 0;
        string where = "";
        for (int f = 0; f < 70; f++)
        {
            float x = startX + speed * (float)(clock - t0);   // where the agent is
            if (!crossed && x < crossX && x - lastStated >= 5f) { lastStated = x; from.ApplyAvatar(SimFrom, At(900, x, velocity)); statedThisFrame.Add((x, velocity)); }
            if (!crossed && clock >= crossedAt) { crossed = true; hub.Current = to; }
            if (crossed && !inside && clock >= statedAt) { inside = true; lastStated = crossX; to.ApplyAvatar(SimTo, At(1900, crossX, velocity)); statedThisFrame.Add((crossX, velocity)); }   // where it crossed
            else if (inside && x - lastStated >= 5f) { lastStated = x; to.ApplyAvatar(SimTo, At(1900, x, velocity)); statedThisFrame.Add((x, velocity)); }
            Frame();
            float drawnOfficial = official.Frame(clock, frame, 1f, _ => true).X;
            if (f >= 6 && Math.Abs(drawn - drawnOfficial) > worst)
            {
                worst = Math.Abs(drawn - drawnOfficial);
                where = $"frame {f} ({clock - crossedAt:0.00} s from the crossing): drawn at x {drawn:0.00}, official {drawnOfficial:0.00}";
            }
            previous = drawn;
        }
        return (worst, where);
    }

    [Theory]
    [InlineData(false, 0.2)]
    [InlineData(true, 0.2)]
    [InlineData(false, 0.35)]
    [InlineData(true, 0.35)]
    public void The_own_avatar_moves_on_by_its_speed_across_a_crossing_whichever_region_takes_its_updates_before_the_body_is_placed(bool enteredFirst, double late)
    {
        // the official viewer takes the late statement where it says (a step back the size of the lateness): the body must be drawn where it draws it
        var (worst, where) = DrawnStepsAsPresented(enteredFirst, late);
        Assert.True(worst <= 0.1, $"the drawn body is {worst:0.000} m from the official viewer's, {where}");
    }

    [Theory]
    [InlineData(true, 8f, 0.4)]
    [InlineData(true, 8f, 0.9)]
    [InlineData(true, 20f, 0.9)]
    [InlineData(false, 8f, 0.9)]
    public void The_region_left_draws_the_avatar_where_the_region_entered_will_when_no_new_state_comes(bool own, float speed, double after)
    {
        var (held, entered) = DrawnAfterCrossing(own, speed, after);
        float apart = Vector3.Distance(held, entered);
        Assert.True(apart < 0.35f, $"the region left draws the avatar at {held}, the region entered at {entered}: {apart:0.00} m apart in the frame the drawing is handed over");
    }
}
