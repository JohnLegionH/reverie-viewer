// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Motion;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The camera's ground, draw-distance limit and seat, against the official viewer's agent camera (llagentcamera.cpp,
/// LLAgentCamera::calcCameraPositionTargetGlobal). The land under the camera is the land of whichever region holds the camera's place
/// (llworld.cpp, LLWorld::resolveLandHeightGlobal), the draw-distance limit is skipped for a god and in a region whose estate the agent
/// can manage, and a seated avatar's camera turns with the seat and is not held by the region's constraint plane.
/// </summary>
public class CameraGroundTests
{
    private static readonly SimRef Home = new(1000UL * 256 << 32 | 1000UL * 256, "Home");
    private static readonly SimRef Next = new(1000UL * 256 << 32 | 1001UL * 256, "Next");

    /// <summary>A region lying <paramref name="offset"/> metres from the anchor, its land flat at <paramref name="flat"/>, or rising to
    /// <paramref name="farEdge"/> at its far (north) edge.</summary>
    private static WorldState Region(SimRef sim, Vector3 offset, float flat, float? farEdge = null, bool estateManager = false)
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.SetOriginOffset(offset);
        w.BeginRegion(sim, new RegionInfo(sim.Name, sim.GridX, sim.GridY, 256, 256, true) { IsEstateManager = estateManager });
        for (int py = 0; py < 16; py++)
        {
            var h = new float[16 * 16];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    h[y * 16 + x] = farEdge is { } far ? flat + (far - flat) * (py * 16 + y) / 255f : flat;
            for (int px = 0; px < 16; px++) w.ApplyPatch(sim, px, py, 16, h);
        }
        return w;
    }

    // ---------------- the ground of the region that holds the point ----------------

    [Fact]
    public void The_ground_under_a_point_is_that_of_the_region_holding_it_in_either_order_and_zero_where_none_does()
    {
        var home = Region(Home, Vector3.Zero, 20f, farEdge: 46f);
        var next = Region(Next, new Vector3(0, 256, 0), 22f);
        foreach (var worlds in new[] { new[] { home, next }, new[] { next, home } })
        {
            Assert.Equal(20f + 26f * 100f / 255f, RegionGround.Height(worlds, 100, 100), 2);   // the first region's own ground there
            Assert.Equal(22f, RegionGround.Height(worlds, 100, 300), 3);                      // the second's, not the first's far edge
            Assert.Equal(22f, RegionGround.Height(worlds, 255.5f, 511.5f), 3);
            Assert.Equal(0f, RegionGround.Height(worlds, 100, 600), 3);                       // no region there: 0, as the official viewer answers
            Assert.Equal(0f, RegionGround.Height(worlds, -5, 100), 3);
        }
    }

    [Fact]
    public void A_low_flight_out_into_the_neighbour_and_back_keeps_the_camera_at_the_ground_under_it_in_both_directions()
    {
        // the first region's land is 20 m, rising to 26 m at its far edge; the neighbour's is flat at 29 m, above the flight's natural camera
        // height (28.75 m): there the camera is set by the neighbour's land (29.5 m), and in the first region it is not touched. With the
        // neighbour's lookups taken in the first region's frame they land on its far edge (26 m) and the neighbour's land is ignored
        var home = Region(Home, Vector3.Zero, 20f, farEdge: 26f);
        var next = Region(Next, new Vector3(0, 256, 0), 29f);
        var worlds = new[] { home, next };
        var camera = new AgentCamera { LandHeight = (x, y) => RegionGround.Height(worlds, x, y), DrawDistance = 256f };
        for (int leg = 0; leg < 2; leg++)
        {
            for (int i = 0; i <= 120; i++)
            {
                float y = leg == 0 ? 200 + i : 320 - i;   // 200 to 320 and back: across y = 256
                var (eye, _) = camera.Pose(new Vector3(100, y, 27f), leg == 0 ? MathF.PI / 2 : -MathF.PI / 2, 1.0 / 60);
                float expected = eye.Y > 256 ? 29.5f : 28.75f;   // the land under the camera plus half a metre, or the natural height
                if (MathF.Abs(eye.Y - 256) < 6) continue;   // the camera's offset smoothing eases the change over a few frames around the border
                Assert.True(MathF.Abs(eye.Z - expected) < 0.35f, $"leg {leg}, avatar at y={y:0}, eye y={eye.Y:0.0}: eye z {eye.Z:0.00}, wanted about {expected:0.00}");
            }
        }
    }

    [Fact]
    public void A_constraint_plane_the_region_states_in_its_own_frame_is_the_same_wall_in_the_anchor_frame()
    {
        // the wall n . p = w in the region's frame, for a region 256 m north of the anchor: n . p_anchor = w + n . offset
        var plane = RegionGround.PlaneInAnchorFrame(new Vector4(0, 1, 0, 250f), new Vector3(0, 256, 0));
        Assert.Equal(new Vector4(0, 1, 0, 506f), plane);
        var tilted = RegionGround.PlaneInAnchorFrame(new Vector4(0.6f, -0.8f, 0, 10f), new Vector3(256, 512, 0));
        Assert.Equal(10f + 0.6f * 256 - 0.8f * 512, tilted.W, 3);
        Assert.Equal(Vector4.Zero, RegionGround.PlaneInAnchorFrame(Vector4.Zero, new Vector3(0, 256, 0)));   // no plane stays no plane
    }

    [Fact]
    public void A_drawn_avatar_in_a_region_that_is_not_the_anchor_is_kept_above_that_regions_land()
    {
        // the avatar is carried below the land it was stated on (velocity down, no update for a while); the floor is that region's land at
        // its place in the anchor frame, as in the anchor region itself
        var next = Region(Next, new Vector3(0, 256, 0), 24f);
        var clock = new double[1];
        var scene = new AvatarScene(next, ServerAppearanceTests.Me, () => clock[0]);
        next.Changed += scene.Apply;
        clock[0] = 1;
        next.ApplyAvatar(Next, new AvatarData(800, ServerAppearanceTests.Me, "Own Avatar", new Vector3(100, 100, 24.95f), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), new Vector3(0, 0, -10f), 0));
        clock[0] = 1.3;
        Assert.True(scene.Place(scene.Self!, out var soles, out _));
        Assert.True(soles.Y > 256, "the avatar is drawn in the anchor frame");
        Assert.True(soles.Z >= 24f - 0.001f, $"drawn {24f - soles.Z:0.00} m into the land");
    }

    // ---------------- the draw-distance limit ----------------

    private static float DistanceFrom(AgentCamera camera, Vector3 centre)
    {
        var (eye, _) = camera.Pose(centre, 0f, 0.0);
        return Vector3.Distance(eye, centre);
    }

    [Fact]
    public void The_draw_distance_holds_the_camera_for_an_ordinary_agent_and_not_for_a_god_or_in_a_region_whose_estate_can_be_managed()
    {
        // zoomed far out (8 times the rear offset: about 24 m) with a draw distance of 10 m
        AgentCamera Make() { var c = new AgentCamera { DrawDistance = 10f }; c.SetZoom(8f); return c; }
        var centre = new Vector3(100, 100, 50);

        Assert.True(DistanceFrom(Make(), centre) <= 10.01f);

        var god = Make();
        god.Godlike = true;
        Assert.True(DistanceFrom(god, centre) > 20f, "a god's camera is not held to the draw distance");

        var manager = Make();
        manager.CanManageEstateAt = (x, y) => true;
        Assert.True(DistanceFrom(manager, centre) > 20f, "nor where the agent manages the estate");

        var elsewhere = Make();
        elsewhere.CanManageEstateAt = (x, y) => y > 1000;   // the region holding the camera is not one it manages
        Assert.True(DistanceFrom(elsewhere, centre) <= 10.01f);
    }

    [Fact]
    public void A_regions_handshake_says_whether_the_agent_manages_its_estate_and_a_god_level_makes_the_agent_a_god()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        t.Raise(new SimConnected(new SimRef(256000UL << 32 | 256000UL, "Here"), 256, 256, true, null, null, IsEstateManager: true));
        Assert.True(s.Region!.IsEstateManager);
        Assert.False(s.AgentGodlike);
        t.Raise(new GodLevelReceived(200));
        Assert.True(s.AgentGodlike);
        t.Raise(new GodLevelReceived(0));
        Assert.False(s.AgentGodlike);
    }

    // ---------------- the seat ----------------

    [Fact]
    public void A_seated_avatars_camera_is_not_held_by_the_regions_constraint_plane()
    {
        // a wall 1 m behind the avatar: standing, the camera is brought in to it; sitting, the official camera skips the plane
        var wall = new Vector4(-1, 0, 0, 1f);   // -x . p = 1: the half-plane x >= -1, the avatar at x = 0
        var standing = new AgentCamera { CollidePlane = wall };
        var seated = new AgentCamera { CollidePlane = wall, SeatRotation = Quaternion.Identity };
        float stand = standing.Pose(new Vector3(0, 0, 10), 0f, 0.0).Eye.X;
        float sit = seated.Pose(new Vector3(0, 0, 10), 0f, 0.0).Eye.X;
        Assert.True(stand > -1.01f, $"standing: the camera stops at the wall ({stand:0.00})");
        Assert.True(sit < -2.9f, $"sitting: the camera is at its rear offset ({sit:0.00})");
    }

    [Fact]
    public void A_seated_avatars_camera_turns_with_the_seat_so_a_tilted_seat_tilts_the_view()
    {
        // the seat rolled 30 degrees about the heading's axis: the rear offset (-3, 0, 0.75) is turned by the seat, and so is the focus
        var roll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6);
        var camera = new AgentCamera { SeatRotation = roll };
        var centre = new Vector3(0, 0, 10);
        var (eye, target) = camera.Pose(centre, 0f, 0.0);
        var head = centre + new Vector3(0, 0, 1);
        var wantEye = head + Vector3.Transform(AgentCamera.CameraOffset, roll);
        var wantTarget = centre + Vector3.Transform(AgentCamera.FocusOffset, roll);
        Assert.True(Vector3.Distance(eye, wantEye) < 0.01f, $"eye {eye} wanted {wantEye}");
        Assert.True(Vector3.Distance(target, wantTarget) < 0.01f, $"target {target} wanted {wantTarget}");
    }

    [Fact]
    public void A_level_seat_turned_about_up_gives_the_standing_camera_for_the_same_heading()
    {
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f);
        var seated = new AgentCamera { SeatRotation = turned };
        var standing = new AgentCamera();
        var a = seated.Pose(new Vector3(5, 6, 10), 1.1f, 0.0);
        var b = standing.Pose(new Vector3(5, 6, 10), 1.1f, 0.0);
        Assert.True(Vector3.Distance(a.Eye, b.Eye) < 0.01f);
        Assert.True(Vector3.Distance(a.Target, b.Target) < 0.01f);
    }
}
