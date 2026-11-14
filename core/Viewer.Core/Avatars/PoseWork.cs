// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.ExceptionServices;

namespace Viewer.Core.Avatars;

/// <summary>
/// One avatar's pose to work out: what the main thread hands a worker (a snapshot that is not changed while the worker reads it) and what the
/// worker hands back (the skinning transforms of the body's palette, the packed row of joint matrices for worn rigged meshes, the clip time,
/// the height the avatar is raised by). Jobs are reused (<see cref="PoseWorkers.Rent"/>, <see cref="PoseWorkers.Return"/>), so a steady crowd
/// allocates nothing per frame.
/// </summary>
public sealed class PoseJob
{
    // ---- in: set by the main thread before the job is submitted, read only by the worker ----

    /// <summary>The caller's own reference to the avatar this job is for (not read by the worker).</summary>
    public object? Tag;

    /// <summary>The avatar's generation when the job was made: the caller drops a result whose avatar has changed since.</summary>
    public long Generation;

    /// <summary>Seconds into the clip (the frame's time plus the avatar's phase).</summary>
    public double Playing;
    public AnimationClip? Clip;
    public AvatarShape? Shape;

    /// <summary>The joint overrides of the meshes the avatar wears, a copy (<see cref="JointOverrides.Clone"/>) that nobody changes while jobs read it.</summary>
    public JointOverrides? Overrides;

    /// <summary>False: the avatar wears no rigged mesh with overrides, so the body is posed as an unshaped one when it has no shape either.</summary>
    public bool UseOverrides;

    /// <summary>True when the avatar has a row of joint matrices for worn rigged meshes to fill.</summary>
    public bool WantRow;

    // ---- out: written by the worker ----

    /// <summary>The skinning transform of each palette entry.</summary>
    public Matrix4x4[] Skin = [];

    /// <summary>The avatar's row of joint matrices as packed floats (<see cref="SkinTables.PackBones"/>); filled when <see cref="WantRow"/>.</summary>
    public float[] Row = [];
    public float ClipTime;
    public float Lift;

    /// <summary>How far the avatar's soles are above the terrain under them (the ground a clip's constraints hold the body to is the terrain, not the soles; see <see cref="ConstraintSolver"/>).</summary>
    public float GroundGap;

    /// <summary>In: the avatar's constraint state for the clip (<see cref="ConstraintState.For"/>), null for a clip with no constraint. At most one job per
    /// avatar is in flight, so the worker may change it.</summary>
    public ConstraintState? Constraints;

    /// <summary>Out: what the constraints changed in the pose (a copy this job owns, for the worn objects' joints to read); null when <see cref="Constraints"/> is.</summary>
    public ConstraintPose? Solved;

    /// <summary>What went wrong working the pose out (the caller throws it where the pose is handed over, as an inline pose would).</summary>
    public Exception? Error;
}

/// <summary>
/// Works out poses. Engine-free arithmetic: <see cref="AvatarPose"/> for the body's palette and <see cref="AvatarJoints"/> for the row of joint
/// matrices worn rigged meshes skin by, packed as the skinning shader reads them. One instance per thread (it keeps its scratch space and the
/// poses of the clips it has seen).
/// </summary>
public sealed class PoseComputer
{
    private readonly AvatarBody _body;
    private readonly AvatarRig _rig;
    private readonly Dictionary<AnimationClip, (AvatarPose Pose, JointTrack?[] Tracks)> _clips = new(ReferenceEqualityComparer.Instance);
    private (AvatarPose Pose, JointTrack?[] Tracks)? _still;
    private readonly AvatarJoints _joints;
    private readonly ConstraintSolver _solver;
    private readonly Matrix4x4[] _worlds;

    public PoseComputer(AvatarBody body, AvatarRig rig)
    {
        _body = body;
        _rig = rig;
        _joints = new AvatarJoints(rig);
        _solver = new ConstraintSolver(rig);
        _worlds = new Matrix4x4[rig.NodeCount + 1];
        Neutral = AvatarShape.Neutral(rig);
        NeutralPelvisToFoot = Neutral.Measure(null)?.PelvisToFoot ?? 0f;
    }

    /// <summary>The skeleton with no shape applied, and its pelvis-to-foot height: what a raised or lowered body is measured against.</summary>
    public AvatarShape Neutral { get; }
    public float NeutralPelvisToFoot { get; }

    /// <summary>The metres an avatar is raised by: the worn meshes' pelvis offset, a taller or shorter leg and the hover height.
    /// <paramref name="overrides"/> null: the avatar wears no mesh with overrides.</summary>
    public static float LiftOf(AvatarShape neutral, float neutralPelvisToFoot, AvatarShape? shape, JointOverrides? overrides, float pelvisFixup)
    {
        float lift = pelvisFixup;
        if ((shape ?? (overrides is null ? null : neutral)) is { } shaped)
            lift += (shaped.Measure(overrides)?.PelvisToFoot ?? neutralPelvisToFoot) - neutralPelvisToFoot + shaped.Hover;
        return lift;
    }

    /// <summary>The pose of the job's snapshot: fills <see cref="PoseJob.Skin"/>, <see cref="PoseJob.Row"/> (with <see cref="PoseJob.WantRow"/>),
    /// <see cref="PoseJob.ClipTime"/> and <see cref="PoseJob.Lift"/>.</summary>
    public void Compute(PoseJob job)
    {
        var (pose, tracks) = For(job.Clip);
        var overrides = job.UseOverrides ? job.Overrides : null;   // the body is posed with the overrides only while a worn mesh asks for some
        job.Lift = LiftOf(Neutral, NeutralPelvisToFoot, job.Shape, overrides, job.Overrides?.PelvisFixup ?? 0f);
        job.ClipTime = job.Clip?.TimeAt(job.Playing) ?? 0f;
        ConstraintPose? solved = null;
        if (job.Constraints is { } state)
        {
            solved = _solver.Step(state, tracks, job.Shape, overrides, job.ClipTime, HipHeight + job.Lift + job.GroundGap);
            (job.Solved ??= new ConstraintPose(_rig.NodeCount)).CopyFrom(solved);
        }
        else job.Solved = null;
        pose.Evaluate(job.Playing, job.Skin, job.Shape, overrides, 0f, solved);
        if (job.WantRow)
        {
            // the joints the worn meshes skin by are always given the overrides in hand (an empty set overrides nothing)
            _joints.Overrides = job.Overrides;
            _joints.Shape = job.Shape;
            _joints.PelvisDrop = 0f;
            _joints.Constraint = solved;
            _joints.Begin(tracks, job.ClipTime);
            _joints.Fill(_worlds);
            SkinTables.PackBones(_worlds, job.Row);
        }
    }

    /// <summary>The pelvis's rest height above the soles: the root hangs the body from here.</summary>
    public float HipHeight => _body.Skeleton.Joints[0].Position.Z - _body.FootHeight;

    private (AvatarPose Pose, JointTrack?[] Tracks) For(AnimationClip? clip)
    {
        if (clip is null) return _still ??= (new AvatarPose(_body, null), AvatarJoints.TracksFor(_rig, null));
        if (!_clips.TryGetValue(clip, out var have)) _clips[clip] = have = (new AvatarPose(_body, clip), AvatarJoints.TracksFor(_rig, clip));
        return have;
    }
}

/// <summary>
/// Worker threads that work poses out from snapshots while the main thread does other things. The main thread rents a job, fills its input,
/// submits it, and takes finished jobs back later; a job is for one avatar and the caller keeps at most one in flight per avatar. Jobs
/// come out in the order they went in per thread, and finish in any order across threads. Disposing ends the threads.
/// </summary>
public sealed class PoseWorkers : IDisposable
{
    private readonly AvatarBody _body;
    private readonly AvatarRig _rig;
    private readonly ConcurrentQueue<PoseJob> _todo = new();
    private readonly ConcurrentQueue<PoseJob> _done = new();
    private readonly ConcurrentBag<PoseJob> _pool = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly Thread[] _threads;
    private readonly object _idleLock = new();
    private int _inFlight;
    private volatile bool _stop;

    public PoseWorkers(AvatarBody body, AvatarRig rig, int threads)
    {
        _body = body;
        _rig = rig;
        _threads = new Thread[Math.Max(1, threads)];
        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Run) { IsBackground = true, Name = "pose-worker-" + i };
            _threads[i].Start();
        }
    }

    public int Threads => _threads.Length;

    /// <summary>Jobs submitted and not yet taken back.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>A job to fill: a returned one, else a new one with room for the body's palette and, when asked, the row.</summary>
    public PoseJob Rent(bool wantRow)
    {
        if (!_pool.TryTake(out var job)) job = new PoseJob { Skin = new Matrix4x4[_body.Palette.Length] };
        job.Error = null;
        job.WantRow = wantRow;
        if (wantRow && job.Row.Length == 0) job.Row = new float[(_rig.NodeCount + 1) * SkinTables.FloatsPerMatrix];
        return job;
    }

    /// <summary>Gives a job back for reuse once its result has been used.</summary>
    public void Return(PoseJob job)
    {
        job.Tag = null;
        job.Clip = null;
        job.Shape = null;
        job.Overrides = null;
        _pool.Add(job);
    }

    /// <summary>Queues the job for a worker.</summary>
    public void Submit(PoseJob job)
    {
        Interlocked.Increment(ref _inFlight);
        _todo.Enqueue(job);
        _work.Release();
    }

    /// <summary>The oldest finished job, or false when none has finished.</summary>
    public bool TryTake(out PoseJob job)
    {
        if (!_done.TryDequeue(out job!)) return false;
        lock (_idleLock)
            if (Interlocked.Decrement(ref _inFlight) == 0) Monitor.PulseAll(_idleLock);
        return true;
    }

    /// <summary>Waits until every submitted job has finished (they stay to be taken); false at the timeout.</summary>
    public bool WaitUntilFinished(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (_todo.Count > 0 || _done.Count < Volatile.Read(ref _inFlight))
        {
            if (DateTime.UtcNow > until) return false;
            Thread.Yield();
        }
        return true;
    }

    private void Run()
    {
        var computer = new PoseComputer(_body, _rig);
        while (true)
        {
            _work.Wait();
            if (_stop) return;
            if (!_todo.TryDequeue(out var job)) continue;
            try { computer.Compute(job); }
            catch (Exception e) { job.Error = e; }
            _done.Enqueue(job);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _work.Release(_threads.Length);
        foreach (var t in _threads) t.Join(2000);
    }

    /// <summary>Throws the error a job carries, with its original stack.</summary>
    public static void ThrowIfFailed(PoseJob job)
    {
        if (job.Error is { } e) ExceptionDispatchInfo.Capture(e).Throw();
    }
}
