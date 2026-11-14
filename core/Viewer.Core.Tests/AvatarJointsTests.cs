// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The animated world matrix of any node of the avatar rig (a joint or an attachment point), in the frame the stock body is
/// drawn in (the pelvis at the origin, as <see cref="AvatarPose"/> hangs it). The body's own pose is the oracle: for every
/// palette entry, the skinning matrix is the inverse of the joint's default world times its animated world, so the joint's
/// animated world is the default world times the skinning matrix. Expected values elsewhere are worked by hand. Pure, parallel.
/// </summary>
public class AvatarJointsTests
{
    private const string SkeletonXml = """
        <linden_skeleton version="2.0">
          <bone name="mPelvis" pos="0 0 1" rot="0 0 0" scale="1 1 1" support="base" aliases="hip">
            <bone name="mTorso" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mChest" pos="0 0 0.2" rot="0 0 0" scale="1 1 1" support="base">
                <bone name="mNeck" pos="0 0 0.25" rot="0 0 0" scale="1 1 1" support="base"/>
                <bone name="mCollarLeft" pos="0 0.1 0.2" rot="0 0 0" scale="1 1 1" support="base"/>
              </bone>
            </bone>
          </bone>
        </linden_skeleton>
        """;

    private const string DefinitionXml = """
        <linden_avatar>
          <attachment_point id="1" group="6" name="Chest" joint="mChest" position="0.15 0 -0.1" rotation="0 90 90"/>
          <attachment_point id="2" group="2" name="Skull" joint="mNeck" position="0 0 0.1" rotation="0 0 0"/>
          <attachment_point id="40" group="0" name="Avatar Center" joint="mRoot" position="0 0 0.5" rotation="0 0 0"/>
          <attachment_point id="35" group="0" name="Center" joint="mScreen" position="0 0 0" rotation="0 0 0"/>
        </linden_avatar>
        """;

    private static CharacterSkeleton Skeleton() => CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml), Encoding.UTF8.GetBytes(DefinitionXml));

    private static Quaternion Turn(Vector3 axis, float radians) => Quaternion.CreateFromAxisAngle(axis, radians);

    private static AnimationClip Clip(params JointTrack[] tracks) => new()
    {
        Duration = 1f, Loop = true, LoopIn = 0, LoopOut = 1, Priority = 1, EaseIn = 0, EaseOut = 0, Emote = "", Tracks = tracks,
    };

    private static JointTrack Rotate(string joint, Quaternion q) => new(joint, 1, [0f, 1f], [q, q], [], []);

    private static JointTrack Move(string joint, Vector3 from, Vector3 to) => new(joint, 1, [], [], [0f, 1f], [from, to]);

    private static Matrix4x4 PoseWorld(AvatarRig rig, AnimationClip? clip, double t, int node)
    {
        var joints = new AvatarJoints(rig);
        joints.Begin(AvatarJoints.TracksFor(rig, clip), clip?.TimeAt(t) ?? 0f);
        return joints.World(node);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tol = 1e-4f) =>
        Assert.True((expected - actual).Length() <= tol, $"expected {expected}, got {actual}");

    private static void Near(Matrix4x4 expected, Matrix4x4 actual, float tol = 1e-4f)
    {
        foreach (var p in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Near(Vector3.Transform(p, expected), Vector3.Transform(p, actual), tol);
    }

    [Fact]
    public void At_rest_every_node_is_its_default_world_with_the_pelvis_at_the_origin()
    {
        var rig = new AvatarRig(Skeleton());
        var rest = new Matrix4x4[rig.NodeCount];
        JointWorlds.Rest(rig, rest);
        var toPelvisFrame = Matrix4x4.CreateTranslation(-rig.DefaultPosition[rig.Pelvis]);
        var joints = new AvatarJoints(rig);
        joints.Begin(null, 0f);
        for (int n = 0; n < rig.NodeCount; n++)
            Near(rest[n] * toPelvisFrame, joints.World(n));
        Near(new Vector3(0, 0, 0), Vector3.Transform(Vector3.Zero, joints.World(rig.Pelvis)));
    }

    [Fact]
    public void A_point_on_the_root_sits_a_pelvis_height_below_the_pelvis_frames_origin()
    {
        var rig = new AvatarRig(Skeleton());
        var joints = new AvatarJoints(rig);
        joints.Begin(null, 0f);
        // "Avatar Center" is 0.5 m above mRoot; the root is 1 m (the pelvis height) below the pelvis
        Near(new Vector3(0, 0, -0.5f), Vector3.Transform(Vector3.Zero, joints.World(rig.PointNode(40))));
    }

    [Fact]
    public void Every_joint_the_body_uses_agrees_with_the_bodys_own_pose()
    {
        var skeleton = AvatarTests.Skeleton();
        var rig = new AvatarRig(skeleton);
        var body = AvatarTests.SyntheticBody();
        var clip = Clip(
            Rotate("mChest", Turn(Vector3.UnitZ, MathF.PI / 2)),
            Rotate("mTorso", Turn(Vector3.UnitY, 0.3f)),
            Move("mPelvis", new Vector3(0, 0, 0.9f), new Vector3(0.2f, 0, 0.9f)));
        var pose = new AvatarPose(body, clip);
        var skin = new Matrix4x4[pose.PaletteSize];
        foreach (double t in new[] { 0.0, 0.25, 0.8 })
        {
            pose.Evaluate(t, skin);
            var joints = new AvatarJoints(rig);
            joints.Begin(AvatarJoints.TracksFor(rig, clip), clip.TimeAt(t));
            for (int i = 0; i < body.Palette.Length; i++)
            {
                if (body.Palette[i] == SkinBinding.Root) continue;
                int joint = body.Palette[i];
                Near(skeleton.DefaultWorld[joint] * skin[i], joints.World(joint), 2e-4f);
            }
        }
    }

    [Fact]
    public void A_point_follows_the_joint_it_hangs_from()
    {
        var rig = new AvatarRig(Skeleton());
        // the chest turned 90 degrees about up: everything above it stays on the axis (torso 0.1, chest 0.2, neck 0.25, skull point 0.1)
        var upright = Clip(Rotate("mChest", Turn(Vector3.UnitZ, MathF.PI / 2)));
        Near(new Vector3(0, 0, 0.1f + 0.25f + 0.2f + 0.1f), Vector3.Transform(Vector3.Zero, PoseWorld(rig, upright, 0.5, rig.PointNode(2))), 1e-4f);

        // the chest leaning 90 degrees about y (forward): the neck's 0.25 m of "up" points along +x (right-handed: z turns to x)
        var lean = Clip(Rotate("mChest", Turn(Vector3.UnitY, MathF.PI / 2)));
        var neck = Vector3.Transform(Vector3.Zero, PoseWorld(rig, lean, 0.5, rig.Skeleton.IndexOf("mNeck")));
        Near(new Vector3(0.25f, 0, 0.1f + 0.2f), neck, 1e-4f);   // pelvis frame: torso 0.1 + chest 0.2 up, then 0.25 forward
    }

    [Fact]
    public void The_pelvis_moving_in_the_clip_carries_every_point()
    {
        var rig = new AvatarRig(Skeleton());
        var bob = Clip(Move("mPelvis", new Vector3(0, 0, 0.5f), new Vector3(0, 0, 0.5f)));
        // the pelvis is where the clip puts it (as the body's pose does), the neck 0.1 + 0.2 + 0.25 above it, the skull point 0.1 more
        Near(new Vector3(0, 0, 0.5f + 0.1f + 0.2f + 0.25f + 0.1f), Vector3.Transform(Vector3.Zero, PoseWorld(rig, bob, 0.3, rig.PointNode(2))));
    }

    [Fact]
    public void Before_any_pose_is_started_a_node_is_in_the_default_pose()
    {
        var rig = new AvatarRig(Skeleton());
        var joints = new AvatarJoints(rig);
        Near(new Vector3(0, 0, 0.65f), Vector3.Transform(Vector3.Zero, joints.World(rig.PointNode(2))));
    }

    [Fact]
    public void A_new_pose_replaces_the_last()
    {
        var rig = new AvatarRig(Skeleton());
        var clip = Clip(Move("mPelvis", new Vector3(0, 0, 0), new Vector3(0, 0, 1)));
        var joints = new AvatarJoints(rig);
        var tracks = AvatarJoints.TracksFor(rig, clip);
        joints.Begin(tracks, 0f);
        Near(Vector3.Zero, Vector3.Transform(Vector3.Zero, joints.World(rig.Pelvis)));
        joints.Begin(tracks, 1f);
        Near(new Vector3(0, 0, 1), Vector3.Transform(Vector3.Zero, joints.World(rig.Pelvis)));
        Near(new Vector3(0, 0, 1), Vector3.Transform(Vector3.Zero, joints.World(rig.Pelvis)));   // asked again in the same pose: the same
    }

    [Fact]
    public void A_clip_naming_no_joint_of_the_rig_changes_nothing()
    {
        var rig = new AvatarRig(Skeleton());
        var stranger = Clip(Rotate("mNoSuchJoint", Turn(Vector3.UnitZ, 1f)));
        var tracks = AvatarJoints.TracksFor(rig, stranger);
        Assert.All(tracks, t => Assert.Null(t));
        // the skull point at rest: torso 0.1, chest 0.2, neck 0.25 and the point's 0.1 above the pelvis
        Near(new Vector3(0, 0, 0.65f), Vector3.Transform(Vector3.Zero, PoseWorld(rig, stranger, 0.5, rig.PointNode(2))));
    }

    [Fact]
    public void Posing_a_node_allocates_nothing()
    {
        var rig = new AvatarRig(Skeleton());
        var clip = Clip(Rotate("mChest", Turn(Vector3.UnitZ, 1f)), Move("mPelvis", Vector3.Zero, Vector3.UnitX));
        var tracks = AvatarJoints.TracksFor(rig, clip);
        var joints = new AvatarJoints(rig);
        int[] nodes = [rig.PointNode(1), rig.PointNode(2), rig.PointNode(40)];
        void Run()
        {
            for (int i = 0; i < 1000; i++)
            {
                joints.Begin(tracks, clip.TimeAt(i * 0.01));
                foreach (int n in nodes) joints.World(n);
            }
        }
        Run();   // the steady state: every branch taken once
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"{allocated} bytes allocated over 1,000 poses");
    }
}
