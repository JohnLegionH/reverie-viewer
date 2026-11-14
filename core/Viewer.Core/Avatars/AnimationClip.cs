// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Viewer.Core.Avatars;

/// <summary>
/// One constraint of a clip: the point <paramref name="SourceOffset"/> of the collision volume <paramref name="SourceVolume"/> is held to
/// <paramref name="Target"/> ("GROUND" or another volume, plus <paramref name="TargetOffset"/>), by moving the joint chain above it
/// (<paramref name="ChainLength"/> joints; none: the pelvis itself moves). The weight rises from zero at <paramref name="EaseInStart"/>
/// to one at <paramref name="EaseInStop"/> and falls again from <paramref name="EaseOutStart"/> to zero at <paramref name="EaseOutStop"/>, in clip seconds.
/// </summary>
public sealed record ClipConstraint(int ChainLength, bool Plane, string SourceVolume, Vector3 SourceOffset, string Target, Vector3 TargetOffset, Vector3 TargetDirection,
    float EaseInStart, float EaseInStop, float EaseOutStart, float EaseOutStop)
{
    public bool TargetsGround => Target == "GROUND";

    /// <summary>For a constraint carried into an <see cref="AnimationMix"/>: the clip it came from and the timing of the motion playing it, so
    /// its times are read in that clip's own time and it fades with the motion.</summary>
    public AnimationClip? Source { get; init; }
    public MotionTiming? Timing { get; init; }

    /// <summary>This constraint as part of a mix, where <paramref name="source"/> plays with <paramref name="timing"/>.</summary>
    public ClipConstraint InMix(AnimationClip source, MotionTiming timing, JointMask? mask = null) => this with { Source = source, Timing = timing, Mask = mask };

    /// <summary>For a constraint in a mix: which of its joints other motions already drive (<see cref="JointMask"/>); the constraint is skipped while one does. Null: never.</summary>
    public JointMask? Mask { get; init; }

    /// <summary>The constraint's weight at time t (llkeyframemotion.cpp applyConstraint): zero before it eases in or after it ends. t is the
    /// clip's own time, or, for a constraint in a mix, the mix's time.</summary>
    /// <summary>The clip's own time for time t (the same for a constraint of a single clip).</summary>
    public float LocalTime(float t) => Timing is { } timing ? Source!.TimeAt(t - (float)timing.Activation) : t;

    /// <summary>How much of the pose the motion that carries this constraint has at time t (1 for a single clip).</summary>
    public float MotionWeight(float t) => Timing is { } timing ? (float)Math.Max(0.0, timing.Weight(t)) : 1f;

    public float WeightAt(float t)
    {
        if (Timing is { } timing)
        {
            double motion = timing.Weight(t);
            if (motion <= 0) return 0f;
            return (float)(motion * Own(Source!.TimeAt(t - (float)timing.Activation)));
        }
        return Own(t);
    }

    private float Own(float t)
    {
        if (t < EaseInStart || t > EaseOutStop) return 0f;
        if (EaseOutStop == 0f) return 1f;
        return MathF.Min(Rescale(t, EaseInStart, EaseInStop, 0f, 1f), Rescale(t, EaseOutStart, EaseOutStop, 1f, 0f));
    }

    private static float Rescale(float x, float x1, float x2, float y1, float y2) =>
        x <= x1 ? y1 : x >= x2 ? y2 : y1 + (y2 - y1) * (x - x1) / (x2 - x1);
}

/// <summary>One joint's keys in a clip: rotations (local to the parent) and position offsets, each at a time in seconds.</summary>
public sealed record JointTrack(string Joint, int Priority, float[] RotationTimes, Quaternion[] Rotations, float[] PositionTimes, Vector3[] Positions)
{
    /// <summary>For a track of an <see cref="AnimationMix"/>: the blend of the motions that animate the joint, read instead of the keys. Null for a track read at its clip's time.</summary>
    public BlendedJoint? Blend { get; init; }
}

/// <summary>
/// A keyframe animation in the grid's binary animation asset layout (little-endian): version and sub-version (16-bit,
/// 1 and 0), base priority (32-bit), duration (float), an emote name (zero-terminated), loop in and loop out points
/// (floats), a loop flag (32-bit), ease in and ease out (floats), a hand pose (32-bit) and a joint count (32-bit). Per
/// joint: its name (zero-terminated), priority (32-bit), a rotation key count (32-bit) and per key a time and x, y, z
/// (16-bit each), then a position key count and per key a time and x, y, z. A time maps 0..65535 to 0..duration; a
/// rotation component maps 0..65535 to -1..1 (w is the positive root that makes a unit quaternion); a position
/// component maps 0..65535 to -5..5 metres. Values within one step of zero are zero. The constraints follow (a count, then per
/// constraint: chain length and type bytes, 16-byte source and target volume names, three vectors, four ease times, as
/// llkeyframemotion.cpp deserialize reads them); a clip that ends before them, or whose constraints are malformed, has none.
/// </summary>
public sealed class AnimationClip
{
    public const float MaxPositionOffset = 5f;
    /// <summary>Upper bounds on what an animation asset may declare: past them it is refused.</summary>
    public const int MaxJoints = 512, MaxKeys = 1 << 16, MaxBytes = 4 << 20, MaxName = 256;

    public required float Duration { get; init; }
    public required bool Loop { get; init; }
    public required float LoopIn { get; init; }
    public required float LoopOut { get; init; }
    public required int Priority { get; init; }
    public required float EaseIn { get; init; }
    public required float EaseOut { get; init; }
    public required string Emote { get; init; }
    public required IReadOnlyList<JointTrack> Tracks { get; init; }
    /// <summary>The clip's constraints (llkeyframemotion.h JointConstraintSharedData): points of the body held to the ground or to another part of the body.</summary>
    public IReadOnlyList<ClipConstraint> Constraints { get; init; } = [];
    /// <summary>A clip made for one avatar by <see cref="AnimationMix"/>: it changes as motions start and stop, and is not kept for others to share.</summary>
    public bool Transient { get; init; }
    /// <summary>The hand pose the animation asks for (llhandmotion.h eHandPose).</summary>
    public int HandPose { get; init; }

    public static AnimationClip Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length > MaxBytes) throw new InvalidDataException($"animation over {MaxBytes} bytes");
        var r = new Reader(b);
        int version = r.U16(), sub = r.U16();
        if (version != 1 || sub != 0) throw new InvalidDataException($"animation version {version}.{sub}, expected 1.0");
        int priority = r.I32();
        float duration = r.F32();
        if (!(duration >= 0 && duration < 3600)) throw new InvalidDataException($"animation duration {duration}");
        string emote = r.Str();
        float loopIn = r.F32(), loopOut = r.F32();
        bool loop = r.I32() != 0;
        float easeIn = r.F32(), easeOut = r.F32();
        int handPose = r.I32();
        int joints = r.I32();
        if (joints < 0 || joints > MaxJoints) throw new InvalidDataException($"animation joint count {joints}");
        var tracks = new List<JointTrack>(joints);
        for (int j = 0; j < joints; j++)
        {
            string name = r.Str();
            int p = r.I32();
            int nr = r.Count();
            var rt = new float[nr];
            var rv = new Quaternion[nr];
            for (int k = 0; k < nr; k++)
            {
                rt[k] = Unit(r.U16()) * duration;
                float x = Signed(r.U16(), 1f), y = Signed(r.U16(), 1f), z = Signed(r.U16(), 1f);
                float w2 = 1f - (x * x + y * y + z * z);
                rv[k] = Quaternion.Normalize(new Quaternion(x, y, z, w2 > 0 ? MathF.Sqrt(w2) : 0f));
            }
            int np = r.Count();
            var pt = new float[np];
            var pv = new Vector3[np];
            for (int k = 0; k < np; k++)
            {
                pt[k] = Unit(r.U16()) * duration;
                pv[k] = new(Signed(r.U16(), MaxPositionOffset), Signed(r.U16(), MaxPositionOffset), Signed(r.U16(), MaxPositionOffset));
            }
            tracks.Add(new JointTrack(name, p, rt, rv, pt, pv));
        }
        return new AnimationClip { Duration = duration, Loop = loop, LoopIn = loopIn, LoopOut = loopOut, Priority = priority, EaseIn = easeIn, EaseOut = easeOut, Emote = emote, Tracks = tracks, HandPose = handPose, Constraints = ReadConstraints(ref r, joints) };
    }

    /// <summary>Most constraints a clip may declare (llkeyframemotion.h MAX_CONSTRAINTS).</summary>
    public const int MaxConstraints = 10;

    private static ClipConstraint[] ReadConstraints(ref Reader r, int jointCount)
    {
        try
        {
            if (r.Remaining < 4) return [];
            int n = r.I32();
            if (n < 0 || n > MaxConstraints) return [];
            var list = new ClipConstraint[n];
            for (int i = 0; i < n; i++)
            {
                int chain = r.U8(), type = r.U8();
                if (chain > jointCount || type > 1) return [];
                string source = r.Fixed16();
                var sourceOffset = r.Vec3();
                string target = r.Fixed16();
                var targetOffset = r.Vec3();
                var targetDir = r.Vec3();
                float inStart = r.F32(), inStop = r.F32(), outStart = r.F32(), outStop = r.F32();
                if (!float.IsFinite(inStart + inStop + outStart + outStop) || !float.IsFinite(sourceOffset.X + sourceOffset.Y + sourceOffset.Z + targetOffset.X + targetOffset.Y + targetOffset.Z)) return [];
                list[i] = new ClipConstraint(chain, type == 1, source, sourceOffset, target, targetOffset, targetDir, inStart, inStop, outStart, outStop);
            }
            return list;
        }
        catch (InvalidDataException) { return []; }
    }

    private static float Unit(int v) => v / 65535f;

    /// <summary>0..65535 onto -range..range, with anything within one step of zero made exactly zero.</summary>
    private static float Signed(int v, float range)
    {
        float f = v / 65535f * 2f * range - range;
        return MathF.Abs(f) < 2f * range / 65535f ? 0f : f;
    }

    /// <summary>The clip's time for a playing time: a looping clip plays from 0 to the loop-out point once, then repeats
    /// the loop-in to loop-out span; a clip that does not loop holds its last pose.</summary>
    public float TimeAt(double playing)
    {
        if (Duration <= 0) return 0;
        // a cycle played in reverse (a walk cycle played backward) wraps from its start to its end, as the official viewer's walk motion does
        if (playing < 0 && Loop) playing = Duration + playing % Duration;
        if (!Loop) return (float)Math.Min(playing, Duration);
        float loopIn = Math.Clamp(LoopIn, 0, Duration), loopOut = Math.Clamp(LoopOut, loopIn, Duration);
        if (loopOut - loopIn < 1e-4f) return (float)Math.Min(playing, Duration);
        if (playing <= loopOut) return (float)Math.Max(0, playing);
        return loopIn + (float)((playing - loopOut) % (loopOut - loopIn));
    }

    /// <summary>A track's rotation at clip time t: spherical interpolation between the keys either side (the first or
    /// last key outside them); identity for a track with no rotation keys.</summary>
    public static Quaternion RotationAt(JointTrack track, float t)
    {
        if (track.Blend is { } blend) return blend.Rotation(t);
        var times = track.RotationTimes;
        if (times.Length == 0) return Quaternion.Identity;
        int k = Upper(times, t);
        if (k == 0) return track.Rotations[0];
        if (k == times.Length) return track.Rotations[^1];
        float span = times[k] - times[k - 1];
        return Quaternion.Slerp(track.Rotations[k - 1], track.Rotations[k], span > 0 ? (t - times[k - 1]) / span : 1f);
    }

    /// <summary>A track's position offset at clip time t: linear between the keys either side; zero with no keys.</summary>
    public static Vector3 PositionAt(JointTrack track, float t)
    {
        if (track.Blend is { } blend) return blend.Position(t);
        var times = track.PositionTimes;
        if (times.Length == 0) return Vector3.Zero;
        int k = Upper(times, t);
        if (k == 0) return track.Positions[0];
        if (k == times.Length) return track.Positions[^1];
        float span = times[k] - times[k - 1];
        return Vector3.Lerp(track.Positions[k - 1], track.Positions[k], span > 0 ? (t - times[k - 1]) / span : 1f);
    }

    /// <summary>The first key index whose time is after t (the key count when none is).</summary>
    private static int Upper(float[] times, float t)
    {
        int lo = 0, hi = times.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (times[mid] <= t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private ref struct Reader(ReadOnlySpan<byte> b)
    {
        private readonly ReadOnlySpan<byte> _b = b;
        private int _o;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (_o + n > _b.Length) throw new InvalidDataException($"animation truncated at byte {_o}");
            var s = _b.Slice(_o, n);
            _o += n;
            return s;
        }

        public int Remaining => _b.Length - _o;
        public int U8() => Take(1)[0];
        public Vector3 Vec3() => new(F32(), F32(), F32());

        /// <summary>A 16-byte name field, zero-terminated within it.</summary>
        public string Fixed16()
        {
            var f = Take(16);
            int z = f.IndexOf((byte)0);
            return Encoding.UTF8.GetString(z < 0 ? f : f[..z]);
        }

        public int U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public int Count()
        {
            int n = I32();
            if (n < 0 || n > MaxKeys) throw new InvalidDataException($"animation key count {n}");
            return n;
        }

        public string Str()
        {
            var rest = _b[_o..];
            int z = rest.IndexOf((byte)0);
            if (z < 0 || z > MaxName) throw new InvalidDataException($"animation string at byte {_o} is not terminated within {MaxName} bytes");
            _o += z + 1;
            return Encoding.UTF8.GetString(rest[..z]);
        }
    }
}
