// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>One avatar's pose error must not stop the frame: the other avatars are posed, the cause is logged once, nothing is thrown.</summary>
public class PoseFailuresTests
{
    private static readonly AvatarRig Rig = ShapeSkeletonTests.Rig();

    private static AvatarBody Body()
    {
        var pos = new[] { new Vector3(0, 0, 1), new Vector3(0, 0, 1.1f), new Vector3(0, 0.1f, 1.5f) };
        var mesh = LlmMesh.Parse(AvatarTests.Llm(pos, [0, 1, 2], [1f, 2f, 4f], ["mPelvis", "mTorso", "mChest", "mNeck"], detail: false));
        return AvatarBody.Build(Rig.Skeleton, [("upperBodyMesh", "upper", null, mesh)]);
    }

    private static AnimationClip Clip() => new()
    {
        Duration = 2f, Loop = true, LoopIn = 0, LoopOut = 2, Priority = 1, EaseIn = 0, EaseOut = 0, Emote = "",
        Tracks = [new JointTrack("mTorso", 1, [0f, 1f, 2f], [Quaternion.Identity, Quaternion.Identity, Quaternion.Identity], [], [])],
    };

    [Fact]
    public void A_broken_pose_on_one_of_two_avatars_leaves_the_other_posed_and_logs_one_line_and_throws_nothing()
    {
        var body = Body();
        var lines = new List<string>();
        var failures = new PoseFailures(lines.Add);
        using var workers = new PoseWorkers(body, Rig, 1);
        object brokenAvatar = new(), goodAvatar = new();
        var goodClip = Clip();
        int goodPosed = 0, brokenSeen = 0;
        for (int frame = 0; frame < 20; frame++)
        {
            var bad = workers.Rent(false);
            bad.Tag = brokenAvatar; bad.Skin = new Matrix4x4[1]; bad.Playing = frame;   // shorter than the palette: the pose refuses it
            workers.Submit(bad);
            var good = workers.Rent(false);
            good.Tag = goodAvatar; good.Playing = frame; good.Clip = goodClip;
            good.Skin = new Matrix4x4[body.Palette.Length];   // (a returned job keeps the short one it was last given)
            workers.Submit(good);
            Assert.True(workers.WaitUntilFinished(TimeSpan.FromSeconds(30)));
            while (workers.TryTake(out var job))
            {
                var ex = Record.Exception(() =>
                {
                    if (job.Error is { } error)
                    {
                        brokenSeen++;
                        failures.Report(job.Tag!, "avatar-1", error, job.Clip);
                    }
                    else goodPosed++;
                });
                Assert.Null(ex);
                workers.Return(job);
            }
        }
        Assert.Equal(20, goodPosed);      // the other avatar is posed in every frame
        Assert.Equal(20, brokenSeen);     // the broken one failed every frame...
        Assert.Single(lines);             // ...and is logged once
        Assert.Contains("avatar-1", lines[0]);
    }

    [Fact]
    public void The_same_cause_on_the_same_avatar_is_logged_once_a_different_cause_or_avatar_is_logged_again()
    {
        var lines = new List<string>();
        var f = new PoseFailures(lines.Add);
        object a = new(), b = new();
        Assert.True(f.Report(a, "a", new ArgumentException("palette too short"), null));
        Assert.False(f.Report(a, "a", new ArgumentException("palette too short"), null));
        Assert.True(f.Report(a, "a", new InvalidOperationException("no such joint"), null));
        Assert.True(f.Report(b, "b", new ArgumentException("palette too short"), null));
        Assert.Equal(3, lines.Count);
        f.Forget(a);
        Assert.True(f.Report(a, "a", new ArgumentException("palette too short"), null));   // a new avatar of that name starts afresh
    }

    [Fact]
    public void A_clip_that_failed_is_dropped_and_other_clips_are_kept()
    {
        var f = new PoseFailures(_ => { });
        var broken = Clip();
        var fine = Clip();
        Assert.False(f.IsDropped(broken));
        f.Report(new object(), "x", new ArgumentException("bad"), broken);
        Assert.True(f.IsDropped(broken));
        Assert.False(f.IsDropped(fine));
        Assert.False(f.IsDropped(null));
        Assert.Equal(1, f.DroppedClips);
    }
}
