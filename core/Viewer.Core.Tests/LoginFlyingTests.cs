// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Motion;
using Viewer.Shell;
using Xunit;
using F = Viewer.Motion.AgentControlFlags;

namespace Viewer.Core.Tests;

/// <summary>
/// Whether the user's own avatar flies when it arrives. The official viewer decides it from its own memory of how the last
/// session ended (llagent.cpp, LLAgent::init: the saved FlyingAtExit setting, written at quit in llappviewer.cpp), never from
/// the animations the region first plays: a region that briefly plays a hover while it settles an avatar placed on a prim
/// must not leave the viewer sending the fly bit. Sitting down turns flying off (llviewermessage.cpp, process_avatar_sit_response).
/// </summary>
public sealed class LoginFlyingTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Hover = Guid.Parse("4ae8016b-31b9-03bb-c401-b1ea941db41d");

    private sealed class Rig
    {
        public required OwnAvatarDriver Driver;
        public required FakeTransport T;
        public required AvatarScene Scene;
        public required List<WorldChange> Changes;
        public required List<AgentUpdate> Sent;
        public required double[] Clock;
        public double Now { get => Clock[0]; set => Clock[0] = value; }

        public void Avatar(uint seat = 0)
        {
            T.Raise(new AvatarUpdated(Here, new AvatarData(800, ServerAppearanceTests.Me, "Own Avatar", new Vector3(100, 100, 30.5f),
                AgentControlInput.BodyRotation(0), new Vector3(0.45f, 0.6f, 1.9f), Vector3.Zero, seat)));
            Pump();
        }

        public void Plays(params Guid[] animations)
        {
            T.Raise(new AvatarAnimationsReceived(Here, ServerAppearanceTests.Me, animations.Select((a, i) => new PlayingAnimation(a, i + 1)).ToList()));
            Pump();
        }

        private void Pump()
        {
            foreach (var c in Changes.ToList()) Scene.Apply(c);
            Changes.Clear();
        }

        public uint Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                Now += 1 / 60.0;
                Driver.Frame(Now, 1 / 60.0, new Vector3(97, 100, 32), new Vector3(101, 100, 32));
            }
            return Sent[^1].Controls;
        }
    }

    private static Rig Placed()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var clock = new double[1];
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => clock[0]);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        var sent = new List<AgentUpdate>();
        var driver = new OwnAvatarDriver(scene, s.World, u => { sent.Add(u); return true; });
        var rig = new Rig { Driver = driver, T = t, Scene = scene, Changes = changes, Sent = sent, Clock = clock };
        return rig;
    }

    [Fact]
    public void A_login_on_a_prim_where_the_region_first_plays_a_hover_sends_no_fly_bit()
    {
        var r = Placed();
        r.Avatar();
        r.Plays(Hover);   // the region settles the avatar on the prim and plays a hover for a moment
        uint bits = r.Frames(30);
        Assert.Equal(0u, bits & F.Fly);
        Assert.False(r.Driver.Input.Flying);
    }

    [Fact]
    public void A_session_that_ended_flying_starts_flying_whatever_the_region_first_plays()
    {
        var r = Placed();
        r.Driver.Input.AdoptFlying(true);   // the saved fly-at-exit state, set before the first frame
        r.Avatar();
        r.Plays(AnimationChoice.Stand);
        uint bits = r.Frames(30);
        Assert.Equal(F.Fly, bits & F.Fly);
        Assert.True(r.Driver.Input.Flying);
    }

    [Fact]
    public void Sitting_down_turns_flying_off()
    {
        var r = Placed();
        r.Avatar();
        r.Plays(AnimationChoice.Stand);
        r.Driver.Input.AdoptFlying(true);
        Assert.Equal(F.Fly, r.Frames(10) & F.Fly);
        r.Avatar(seat: 900);   // the region seats the avatar on a prim
        uint bits = r.Frames(30);
        Assert.Equal(0u, bits & F.Fly);
        Assert.False(r.Driver.Input.Flying);
    }
}
