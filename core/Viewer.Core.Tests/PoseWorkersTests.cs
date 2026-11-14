// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Poses and joint rows worked out on worker threads: they equal what the main thread works out, from a snapshot nothing changes while a worker
/// reads it; the order avatars are chosen in and the bound on how old a shown pose can be hold. Engine-free; every wait polls for the result
/// with a generous cap.
/// </summary>
public class PoseWorkersTests
{
    private static readonly AvatarRig Rig = ShapeSkeletonTests.Rig();

    private static AvatarBody Body()
    {
        var pos = new[] { new Vector3(0, 0, 1), new Vector3(0, 0, 1.1f), new Vector3(0, 0.1f, 1.5f) };
        var mesh = LlmMesh.Parse(AvatarTests.Llm(pos, [0, 1, 2], [1f, 2f, 4f], ["mPelvis", "mTorso", "mChest", "mNeck"], detail: false));
        return AvatarBody.Build(Rig.Skeleton, [("upperBodyMesh", "upper", null, mesh)]);
    }

    private static AnimationClip Clip(float turn) => new()
    {
        Duration = 2f, Loop = true, LoopIn = 0, LoopOut = 2, Priority = 1, EaseIn = 0, EaseOut = 0, Emote = "",
        Tracks =
        [
            new JointTrack("mTorso", 1, [0f, 1f, 2f], [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn), Quaternion.Identity], [], []),
            new JointTrack("mChest", 1, [], [], [0f, 2f], [new Vector3(0, 0, 0.2f), new Vector3(0.05f, 0, 0.22f)]),
        ],
    };

    private static MeshSkinBlock Skin(float pelvisOffset) =>
        new(["mTorso", "mNeck"], [Matrix4x4.Identity, Matrix4x4.Identity], Matrix4x4.Identity,
            [Matrix4x4.CreateTranslation(0, 0, 0.17f), Matrix4x4.CreateTranslation(0, 0.02f, 0.3f)], pelvisOffset, true, false);

    private static JointOverrides Overrides(float pelvisOffset)
    {
        var o = new JointOverrides(Rig);
        Assert.True(o.Add(new Guid("00000000-0000-0000-0000-0000000000a1"), Skin(pelvisOffset)));
        return o;
    }

    /// <summary>The pose as the main thread works it out: the layer's own steps, one after the other.</summary>
    private static (Matrix4x4[] Skin, float[] Row, float ClipTime, float Lift) OnMainThread(AvatarBody body, AnimationClip? clip, double playing, AvatarShape? shape, JointOverrides? overrides, bool row)
    {
        var skin = new Matrix4x4[body.Palette.Length];
        new AvatarPose(body, clip).Evaluate(playing, skin, shape, overrides?.MeshCount > 0 ? overrides : null);
        float clipTime = clip?.TimeAt(playing) ?? 0f;
        float[] packed = [];
        if (row)
        {
            var joints = new AvatarJoints(Rig) { Overrides = overrides, Shape = shape };
            joints.Begin(AvatarJoints.TracksFor(Rig, clip), clipTime);
            var worlds = new Matrix4x4[Rig.NodeCount + 1];
            joints.Fill(worlds);
            packed = new float[worlds.Length * SkinTables.FloatsPerMatrix];
            SkinTables.PackBones(worlds, packed);
        }
        var neutral = AvatarShape.Neutral(Rig);
        float lift = PoseComputer.LiftOf(neutral, neutral.Measure(null)?.PelvisToFoot ?? 0f, shape, overrides?.MeshCount > 0 ? overrides : null, overrides?.PelvisFixup ?? 0f);
        return (skin, packed, clipTime, lift);
    }

    private sealed record Case(AnimationClip? Clip, double Playing, AvatarShape? Shape, JointOverrides? Overrides, bool Row);

    private static List<Case> Cases()
    {
        var shaped = AvatarShape.From(Rig, ShapeSkeletonTests.Def(), ShapeSkeletonTests.Wire((33, 255), (35, 0), (80, 255)));
        var clipA = Clip(0.7f);
        var clipB = Clip(-0.4f);
        var list = new List<Case>();
        for (int i = 0; i < 48; i++)
            list.Add(new Case(i % 3 == 0 ? null : i % 3 == 1 ? clipA : clipB, 0.13 * i, i % 2 == 0 ? shaped : null, i % 4 < 2 ? Overrides(0.04f * (i % 3)) : null, i % 5 != 0));
        return list;
    }

    private static void Same(Matrix4x4[] expected, Matrix4x4[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.True(expected[i] == actual[i], $"skin {i}: expected {expected[i]}, got {actual[i]}");
    }

    [Fact]
    public void Poses_and_joint_rows_worked_out_on_workers_equal_those_worked_out_on_the_main_thread_exactly()
    {
        var body = Body();
        using var workers = new PoseWorkers(body, Rig, 3);
        var cases = Cases();
        var sent = new List<PoseJob>();
        foreach (var c in cases)
        {
            var job = workers.Rent(c.Row);
            job.Tag = c;
            job.Playing = c.Playing;
            job.Clip = c.Clip;
            job.Shape = c.Shape;
            job.UseOverrides = c.Overrides?.MeshCount > 0;
            job.Overrides = c.Overrides?.Clone();
            workers.Submit(job);
            sent.Add(job);
        }
        Assert.True(workers.WaitUntilFinished(TimeSpan.FromSeconds(30)));
        int taken = 0;
        while (workers.TryTake(out var job))
        {
            taken++;
            PoseWorkers.ThrowIfFailed(job);
            var c = (Case)job.Tag!;
            var expected = OnMainThread(body, c.Clip, c.Playing, c.Shape, c.Overrides, c.Row);
            Same(expected.Skin, job.Skin);
            Assert.Equal(expected.ClipTime, job.ClipTime);
            Assert.Equal(expected.Lift, job.Lift);
            if (c.Row) Assert.Equal(expected.Row, job.Row.AsSpan(0, expected.Row.Length).ToArray());
            workers.Return(job);
        }
        Assert.Equal(cases.Count, taken);
        Assert.Equal(0, workers.InFlight);
    }

    [Fact]
    public void A_reused_job_carries_nothing_over_from_the_pose_it_had_before()
    {
        var body = Body();
        using var workers = new PoseWorkers(body, Rig, 1);
        var shaped = AvatarShape.From(Rig, ShapeSkeletonTests.Def(), ShapeSkeletonTests.Wire((33, 255)));
        var first = workers.Rent(true);
        first.Playing = 0.5; first.Clip = Clip(0.7f); first.Shape = shaped; first.UseOverrides = true; first.Overrides = Overrides(0.1f).Clone();
        workers.Submit(first);
        Assert.True(workers.WaitUntilFinished(TimeSpan.FromSeconds(30)));
        Assert.True(workers.TryTake(out var back));
        workers.Return(back);
        var second = workers.Rent(false);
        Assert.Same(first, second);   // the pool handed the same job out again
        second.Playing = 1.25; second.Clip = null; second.Shape = null; second.UseOverrides = false; second.Overrides = null;
        workers.Submit(second);
        Assert.True(workers.WaitUntilFinished(TimeSpan.FromSeconds(30)));
        Assert.True(workers.TryTake(out var result));
        var expected = OnMainThread(body, null, 1.25, null, null, false);
        Same(expected.Skin, result.Skin);
        Assert.Equal(expected.Lift, result.Lift);
        Assert.Equal(0f, result.ClipTime);
    }

    [Fact]
    public void A_copy_of_the_overrides_does_not_change_when_the_avatars_worn_meshes_do()
    {
        var live = Overrides(0.1f);
        var copy = live.Clone();
        Assert.NotSame(live, copy);
        Assert.True(copy.TryGet(Rig.Find("mTorso"), out var torso));
        float pelvis = copy.PelvisFixup;
        Assert.Equal(1, copy.MeshCount);
        // the live one goes on: another mesh wins the torso, the first is taken off
        Assert.True(live.Add(new Guid("00000000-0000-0000-0000-0000000000a2"), new MeshSkinBlock(["mTorso"], [Matrix4x4.Identity], Matrix4x4.Identity, [Matrix4x4.CreateTranslation(0, 0, 0.4f)], 0.3f, false, false)));
        live.Remove(new Guid("00000000-0000-0000-0000-0000000000a1"));
        Assert.True(copy.TryGet(Rig.Find("mTorso"), out var after));
        Assert.Equal(torso, after);
        Assert.Equal(pelvis, copy.PelvisFixup);
        Assert.Equal(1, copy.MeshCount);
        Assert.True(copy.ScaleLocked(Rig.Find("mTorso")));
        Assert.False(live.ScaleLocked(Rig.Find("mTorso")));
        // and changing the copy leaves the live one alone
        copy.Clear();
        Assert.Equal(1, live.MeshCount);
    }

    [Fact]
    public void A_worker_reads_the_snapshot_it_was_given_not_the_overrides_the_main_thread_changes_afterwards()
    {
        var body = Body();
        using var workers = new PoseWorkers(body, Rig, 2);
        var live = Overrides(0.1f);
        var shape = AvatarShape.From(Rig, ShapeSkeletonTests.Def(), ShapeSkeletonTests.Wire((33, 255)));
        var clip = Clip(0.7f);
        var expectedAt = new List<(Matrix4x4[] Skin, float[] Row, float ClipTime, float Lift)>();
        for (int i = 0; i < 16; i++)
        {
            expectedAt.Add(OnMainThread(body, clip, 0.9, shape, live, true));   // what the overrides are when the job is made
            var job = workers.Rent(true);
            job.Tag = i;
            job.Playing = 0.9; job.Clip = clip; job.Shape = shape; job.UseOverrides = true; job.Overrides = live.Clone();
            workers.Submit(job);
            // the main thread goes on changing the live overrides while the workers read their copies
            live.Add(new Guid("00000000-0000-0000-0000-0000000001" + i.ToString("x2")), new MeshSkinBlock(["mTorso"], [Matrix4x4.Identity], Matrix4x4.Identity, [Matrix4x4.CreateTranslation(0, 0, 0.3f + i)], 0.2f * i, true, false));
        }
        Assert.True(workers.WaitUntilFinished(TimeSpan.FromSeconds(30)));
        int n = 0;
        while (workers.TryTake(out var job))
        {
            n++;
            PoseWorkers.ThrowIfFailed(job);
            var expected = expectedAt[(int)job.Tag!];
            Same(expected.Skin, job.Skin);
            Assert.Equal(expected.Row, job.Row.AsSpan(0, expected.Row.Length).ToArray());
            Assert.Equal(expected.Lift, job.Lift);
        }
        Assert.Equal(16, n);
    }

    [Fact]
    public void An_error_in_a_worker_comes_back_with_the_job_and_the_worker_goes_on()
    {
        var body = Body();
        using var workers = new PoseWorkers(body, Rig, 1);
        var bad = workers.Rent(false);
        bad.Playing = 0; bad.Skin = new Matrix4x4[1];   // shorter than the palette: the pose refuses it
        workers.Submit(bad);
        var good = workers.Rent(false);
        good.Playing = 0.25;
        workers.Submit(good);
        Assert.True(workers.WaitUntilFinished(TimeSpan.FromSeconds(30)));
        Assert.True(workers.TryTake(out var first));
        Assert.True(workers.TryTake(out var second));
        var failed = first.Error is not null ? first : second;
        var fine = ReferenceEquals(failed, first) ? second : first;
        Assert.NotNull(failed.Error);
        Assert.Throws<ArgumentException>(() => PoseWorkers.ThrowIfFailed(failed));
        Assert.Null(fine.Error);
        Same(OnMainThread(body, null, 0.25, null, null, false).Skin, fine.Skin);
    }

    // ---- which avatars are posed, and how old a pose shown can be ----

    [Fact]
    public void The_avatars_handed_to_the_workers_are_those_with_nothing_in_flight_the_one_posed_longest_ago_first()
    {
        var sweep = new PoseSweep();
        long[] stamps = [7, PoseSweep.Never, 3, 5, 3, PoseSweep.Never];
        bool[] busy = [false, false, false, true, false, true];
        var into = new int[stamps.Length];
        int n = sweep.PlanSubmissions(stamps, busy, into);
        Assert.Equal([1, 2, 4, 0], into.AsSpan(0, n).ToArray());   // the never posed first, then oldest, ties by index; 3 and 5 are in flight
    }

    [Fact]
    public void A_limit_keeps_to_the_avatars_posed_longest_ago()
    {
        var sweep = new PoseSweep();
        long[] stamps = [7, PoseSweep.Never, 3, 5, 3, PoseSweep.Never];
        var into = new int[stamps.Length];
        int n = sweep.PlanSubmissions(stamps, new bool[6], into, 3);
        Assert.Equal([1, 5, 2], into.AsSpan(0, n).ToArray());
        Assert.Equal(0, sweep.PlanSubmissions(stamps, new bool[6], into, 0));
    }

    [Fact]
    public void With_every_avatar_in_flight_nothing_is_handed_out()
    {
        var sweep = new PoseSweep();
        var into = new int[3];
        Assert.Equal(0, sweep.PlanSubmissions([1, 2, 3], [true, true, true], into));
        Assert.Equal(0, sweep.PlanSubmissions([], [], into));
    }

    [Fact]
    public void A_crowd_of_a_hundred_is_posed_with_no_pose_shown_older_than_the_frames_the_hand_over_needs()
    {
        // a simulated crowd run with the layer's own steps: each frame the finished poses are handed over (at most `handPerFrame`, the frame's
        // share of main-thread time), then every avatar with nothing in flight is submitted in the sweep's order
        const int avatars = 100, handPerFrame = 25, frames = 80;
        var body = Body();
        using var workers = new PoseWorkers(body, Rig, 3);
        var sweep = new PoseSweep();
        var stamps = new long[avatars];
        Array.Fill(stamps, PoseSweep.Never);
        var busy = new bool[avatars];
        var shownAt = new long[avatars];
        Array.Fill(shownAt, 0L);
        var into = new int[avatars];
        long worst = 0;
        long firstPose = 0;
        var shown = new bool[avatars];
        for (long frame = 1; frame <= frames; frame++)
        {
            int handed = 0;
            while (handed < handPerFrame && workers.TryTake(out var job))
            {
                int who = (int)(long)job.Tag!;
                PoseWorkers.ThrowIfFailed(job);
                busy[who] = false;
                if (!shown[who]) { shown[who] = true; firstPose = Math.Max(firstPose, frame); }
                shownAt[who] = frame;
                workers.Return(job);
                handed++;
            }
            int count = sweep.PlanSubmissions(stamps, busy, into, handPerFrame);
            for (int k = 0; k < count; k++)
            {
                int who = into[k];
                var job = workers.Rent(false);
                job.Tag = (long)who;
                job.Playing = frame * 0.016;
                busy[who] = true;
                stamps[who] = frame;
                workers.Submit(job);
            }
            // let the workers finish what they were given before the next frame, as a frame's time does for them
            Assert.True(workers.WaitUntilFinished(TimeSpan.FromSeconds(30)));
            if (frame > avatars / handPerFrame + 3)
                for (int a = 0; a < avatars; a++) worst = Math.Max(worst, frame - shownAt[a]);
        }
        Assert.True(shown.All(s => s), "every avatar was posed");
        Assert.True(firstPose <= avatars / handPerFrame + 2, $"the last avatar first shown in frame {firstPose}");
        // a pose is replaced about every avatars / handPerFrame frames; allow the one frame the workers hold it
        Assert.True(worst <= avatars / handPerFrame + 2, $"the oldest pose shown was {worst} frames old");
    }

    [Fact]
    public void Handing_a_job_over_and_back_allocates_next_to_nothing_once_the_crowd_has_been_posed()
    {
        var body = Body();
        using var workers = new PoseWorkers(body, Rig, 2);
        var shape = AvatarShape.Neutral(Rig);
        var clip = Clip(0.7f);
        void Round(int n)
        {
            for (int i = 0; i < n; i++)
            {
                var job = workers.Rent(true);
                job.Playing = i * 0.01; job.Clip = clip; job.Shape = shape; job.UseOverrides = false; job.Overrides = null;
                workers.Submit(job);
            }
            Assert.True(workers.WaitUntilFinished(TimeSpan.FromSeconds(30)));
            while (workers.TryTake(out var job)) workers.Return(job);
        }
        Round(64);   // the jobs and the workers' scratch space are made
        Round(64);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Round(64);
        long perJob = (GC.GetAllocatedBytesForCurrentThread() - before) / 64;
        Assert.True(perJob < 256, $"{perJob} bytes allocated on the calling thread per job");
    }
}
