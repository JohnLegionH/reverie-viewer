// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.NameTags;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A name tag over a flying avatar. A region sends a steadily moving avatar's position each time it has gone 5 m, so the stated
/// position steps while the body is drawn moving evenly (AvatarMotion). The tag is placed from the drawn pose: over a few seconds of
/// flight it moves no faster than the body, stays planned (visible) every frame and stays over the body. Every member builds its own
/// objects, so the class runs in parallel.
/// </summary>
public class NameTagFlightTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Other = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000ee");
    private const double Frame = 1.0 / 60;
    private const uint Local = 900;
    private const float Speed = 16.38f;

    private sealed class Clock { public double Now; }

    private sealed record Sample(Vector3 Anchor, Vector3 DrawnCentre, Vector3 Stated, Vector3 TagPosition, bool Planned);

    private static readonly NameTagSettings Always = new(NameTagMode.Always, ShowSelf: true, GroupTitles: true, DisplayNames: true, UserNames: true, FriendColour: false);

    /// <summary>An avatar flying east at full speed for four seconds, the region stating its position each time it has gone 5 m
    /// (a twentieth of a second late), with a camera that trails the drawn body by six metres.</summary>
    private static List<Sample> Fly(bool self)
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var clock = new Clock();
        Guid agent = self ? ServerAppearanceTests.Me : Other;
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => clock.Now);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        var model = new NameTagModel(() => Always, () => clock.Now, k => k);
        var size = new Vector3(0.45f, 0.6f, 1.9f);
        var start = new Vector3(100, 100, 60);
        var velocity = new Vector3(Speed, 0, 0);
        var sentAt = new Vector3(float.NaN, 0, 0);
        var wire = new Queue<(double Arrives, Vector3 Position, bool First)>();
        bool first = true;
        var samples = new List<Sample>();
        for (int frame = 0; frame < 4 * 60; frame++)
        {
            double now = frame * Frame;
            clock.Now = now;
            var truth = start + velocity * (float)now;
            if (first || Vector3.Distance(truth, sentAt) > 5f)
            {
                wire.Enqueue((now + 0.05, truth, first));
                sentAt = truth;
                first = false;
            }
            while (wire.TryPeek(out var p) && p.Arrives <= now)
            {
                wire.Dequeue();
                if (p.First) t.Raise(new AvatarUpdated(Here, new AvatarData(Local, agent, "Test User", p.Position, Quaternion.Identity, size, velocity, 0, "Builders")));
                else t.Raise(new ObjectMoved(Here, Local, true, p.Position, Quaternion.Identity, velocity, Vector3.Zero, Vector3.Zero));
            }
            foreach (var c in changes.ToList()) scene.Apply(c);
            changes.Clear();
            var entry = scene.Entries.FirstOrDefault(e => e.AgentId == agent);
            if (entry is null || s.World.Avatar(entry.Id) is not { } state || !scene.Place(entry, out var soles, out _)) continue;
            var drawnCentre = soles + new Vector3(0, 0, size.Z / 2);
            var (position, rotation) = NameTagPose.Of(scene, state);
            var input = new NameTagInput(agent, new AvatarNames("Test User"), "Builders", position, rotation, state.Scale, self, false, false, []);
            var camera = new NameTagCamera(drawnCentre - Vector3.UnitX * 6f, Vector3.UnitX, Vector3.UnitZ);
            var plans = model.Update([input], camera, false);
            samples.Add(new Sample(plans.Count == 1 ? plans[0].Anchor : default, drawnCentre, state.Position, position, plans.Count == 1));
        }
        return samples;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_tag_over_a_flying_avatar_moves_no_faster_than_the_body_and_never_drops_out(bool self)
    {
        var samples = Fly(self);
        Assert.True(samples.Count > 200, $"{samples.Count} frames sampled");
        Assert.All(samples, x => Assert.True(x.Planned, "the tag was not planned for a frame of the flight"));
        // the region's own stated position steps by metres between its updates: that is what the tag must not follow
        float steppedMost = 0;
        for (int i = 1; i < samples.Count; i++)
            steppedMost = MathF.Max(steppedMost, Vector3.Distance(samples[i].Stated, samples[i - 1].Stated));
        Assert.True(steppedMost > 4f, $"the region's stated position stepped only {steppedMost:F2} m: the test does not exercise the step");
        float most = 0;
        for (int i = 10; i < samples.Count; i++)
            most = MathF.Max(most, Vector3.Distance(samples[i].Anchor, samples[i - 1].Anchor));
        Assert.True(most <= 0.45f, $"the tag's anchor moved {most:F2} m in one 60th of a second (the body flies {Speed * Frame:F2} m)");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_tag_is_placed_from_the_pose_the_body_is_drawn_at(bool self)
    {
        var samples = Fly(self);
        Assert.NotEmpty(samples);
        foreach (var x in samples.Skip(10))
            Assert.True(Vector3.Distance(x.TagPosition, x.DrawnCentre) < 0.01f, $"the tag is placed from {x.TagPosition}, the body is drawn at {x.DrawnCentre}");
    }

    [Fact]
    public void An_avatar_the_scene_does_not_hold_keeps_the_pose_the_region_states()
    {
        var state = new AvatarState(new ObjectId(1, Local), Other, "Test User", new Vector3(10, 20, 30), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f),
            new Vector3(0.45f, 0.6f, 1.9f), Vector3.Zero, Vector3.Zero);
        var (position, rotation) = NameTagPose.Of(null, state);
        Assert.Equal(state.Position, position);
        Assert.Equal(state.Rotation, rotation);
    }
}
