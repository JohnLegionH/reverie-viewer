// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Numerics;
using Viewer.Core.Avatars;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// The constraint solver against a plain reproduction of the official one (indra/llcharacter/llkeyframemotion.cpp, viewer 26.4: applyConstraint,
/// activateConstraint, initializeConstraint) written here as the C++ has it: joints are mutable objects that keep the pose of the frame before, the
/// keyframes set the motion's joint states, the constraints read the joints and rewrite the states, and the states are then put on the joints. It
/// plays the official ground-sit animation frame by frame (sixty a second) for the default skeleton and for two stored avatar shapes, on land and on a
/// floor, and compares the world position of every joint a constraint turns and of each constraint's source point, at every frame. Local input: skips
/// with its reason without the character files, the animation and the stored shapes.
/// </summary>
public class ConstraintReproductionTests
{
    private readonly ITestOutputHelper _out;
    public ConstraintReproductionTests(ITestOutputHelper output) => _out = output;

    // ---- a rotation as the official math library has it: a * b turns by a, then by b ----
    private readonly record struct Q(float X, float Y, float Z, float W)
    {
        public static readonly Q Id = new(0, 0, 0, 1);
        public static Q Hamilton(Q p, Q q) => new(p.W * q.X + p.X * q.W + p.Y * q.Z - p.Z * q.Y, p.W * q.Y - p.X * q.Z + p.Y * q.W + p.Z * q.X, p.W * q.Z + p.X * q.Y - p.Y * q.X + p.Z * q.W, p.W * q.W - p.X * q.X - p.Y * q.Y - p.Z * q.Z);
        public static Q Then(Q a, Q b) => Hamilton(b, a);
        public Q Inverse => new(-X, -Y, -Z, W);
        public Vector3 Rotate(Vector3 v)
        {
            var u = new Vector3(X, Y, Z);
            var t = 2f * Vector3.Cross(u, v);
            return v + W * t + Vector3.Cross(u, t);
        }
        public static Q From(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
        public Q Normalized() { float m = MathF.Sqrt(X * X + Y * Y + Z * Z + W * W); return new(X / m, Y / m, Z / m, W / m); }
        public static Q Nlerp(float t, Q a, Q b)
        {
            float inv = 1f - t;
            float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            float s = dot < 0f ? -t : t;
            return new Q(a.X * inv + b.X * s, a.Y * inv + b.Y * s, a.Z * inv + b.Z * s, a.W * inv + b.W * s).Normalized();
        }
        public static Q ShortestArc(Vector3 a, Vector3 b)
        {
            float ab = Vector3.Dot(a, b);
            var c = Vector3.Cross(a, b);
            float cc = Vector3.Dot(c, c);
            if (ab * ab + cc != 0f)
            {
                if (cc > 0f)
                {
                    float s = MathF.Sqrt(ab * ab + cc) + ab;
                    float m = 1f / MathF.Sqrt(cc + s * s);
                    return new Q(c.X * m, c.Y * m, c.Z * m, s * m);
                }
                if (ab < 0f)
                {
                    c = a - b;
                    float m = MathF.Sqrt(c.X * c.X + c.Y * c.Y);
                    if (m > 1e-6f) return new Q(-c.Y / m, c.X / m, 0f, 0f);
                    return new Q(1f, 0f, 0f, 0f);
                }
            }
            return Id;
        }
    }

    private sealed class Joint
    {
        public string Name = "";
        public Joint? Parent;
        public Vector3 Pos, Scale = Vector3.One;
        public Q Rot = Q.Id;
        public Q WorldRot => Parent is null ? Rot : Q.Then(Rot, Parent.WorldRot);
        public Vector3 WorldPos => Parent is null ? Pos : Parent.WorldPos + Parent.WorldRot.Rotate(Pos * Parent.Scale);
    }

    private sealed class Constraint
    {
        public ClipConstraint Data = null!;
        public bool Active;
        public Vector3 GroundPos;
        public float Weight = 1f;
        public Vector3[] Positions = new Vector3[4];
        public float[] Lengths = new float[4], Fractions = new float[4];
        public float Total, FixupRms;
        public Joint[] Chain = [];      // the joints from the source volume's parent (0) to the chain's root
        public Joint Source = null!, Target = null!;
    }

    /// <summary>One motion on one character, as LLKeyframeMotion holds it.</summary>
    private sealed class Reference
    {
        private readonly AvatarRig _rig;
        private readonly AnimationClip _clip;
        public readonly Joint[] Joints;
        private readonly Q[] _stateRot;
        private readonly Vector3?[] _statePos;
        private readonly JointTrack?[] _tracks;
        public readonly List<Constraint> Constraints = [];
        private readonly float _groundBelowPelvis;
        private float _lastUpdate;
        private readonly Joint _pelvis;

        public Reference(AvatarRig rig, AvatarShape? shape, AnimationClip clip, float groundBelowPelvis)
        {
            _rig = rig; _clip = clip; _groundBelowPelvis = groundBelowPelvis;
            var sk = rig.Skeleton;
            Joints = new Joint[sk.Joints.Count];
            for (int i = 0; i < Joints.Length; i++)
                Joints[i] = new Joint
                {
                    Name = sk.Joints[i].Name,
                    Pos = sk.Joints[i].Parent < 0 ? Vector3.Zero : shape is not null ? shape.Position[i] : rig.DefaultPosition[i],
                    Scale = shape is not null ? shape.ScaleOf(i, null) : rig.DefaultScale[i],
                    Rot = Q.From(rig.DefaultRotation[i]),
                };
            for (int i = 0; i < Joints.Length; i++) if (sk.Joints[i].Parent >= 0) Joints[i].Parent = Joints[sk.Joints[i].Parent];
            _pelvis = Joints[rig.Pelvis];
            _tracks = AvatarJoints.TracksFor(rig, clip).Take(Joints.Length).ToArray();
            _stateRot = new Q[Joints.Length];
            _statePos = new Vector3?[Joints.Length];
            foreach (var c in clip.Constraints)
            {
                var k = new Constraint { Data = c };
                int vol = sk.IndexOf(c.SourceVolume);
                k.Source = Joints[vol];
                if (!c.TargetsGround) k.Target = Joints[sk.IndexOf(c.Target)];
                var chain = new List<Joint>();
                var j = Joints[vol];
                for (int i = 0; i <= c.ChainLength; i++) { j = j.Parent!; chain.Add(j); }
                k.Chain = chain.ToArray();
                Constraints.Add(k);
                Initialize(k);   // LLKeyframeMotion::setupPose
            }
        }

        private int Index(Joint j) => Array.IndexOf(Joints, j);
        private static Vector3 VolumePos(Joint volume, Vector3 offset) => volume.WorldRot.Rotate(offset * volume.Scale) + volume.WorldPos;

        private void Initialize(Constraint c)
        {
            var d = c.Data;
            var sourcePos = VolumePos(c.Source, d.SourceOffset);
            var cur = c.Chain[0];
            float sourcePosOffset = Vector3.Distance(sourcePos, cur.WorldPos);
            c.Total = c.Lengths[0] = Vector3.Distance(cur.Parent?.WorldPos ?? Vector3.Zero, sourcePos);
            for (int n = 1; n < d.ChainLength; n++)
            {
                cur = c.Chain[n];
                c.Lengths[n] = Vector3.Distance(cur.WorldPos, cur.Parent!.WorldPos);
                c.Total += c.Lengths[n];
            }
            for (int n = 1; n < d.ChainLength; n++) c.Fractions[n] = c.Lengths[n] / c.Total;
            c.Total += sourcePosOffset;
        }

        /// <summary>LLKeyframeMotion::onUpdate for one frame of the motion at <paramref name="time"/>, then the pose put on the joints.</summary>
        public void Update(float time, float dt)
        {
            // applyKeyframes
            for (int i = 0; i < Joints.Length; i++)
            {
                if (_tracks[i] is not { } tr) continue;
                _stateRot[i] = tr.RotationTimes.Length > 0 ? Q.From(AnimationClip.RotationAt(tr, time)) : Q.From(_rig.DefaultRotation[i]);
                _statePos[i] = tr.PositionTimes.Length > 0 ? AnimationClip.PositionAt(tr, time) : null;
            }
            foreach (var c in Constraints) Apply(c, time, dt);
            _lastUpdate = time;
            // the blended pose: this motion alone
            for (int i = 0; i < Joints.Length; i++)
            {
                if (_tracks[i] is null) continue;
                Joints[i].Rot = _stateRot[i];
                if (_statePos[i] is { } p) Joints[i].Pos = p;
            }
        }

        private void Activate(Constraint c)
        {
            var d = c.Data;
            c.Active = true;
            if (d.TargetsGround)
            {
                var source = VolumePos(c.Source, d.SourceOffset);
                c.GroundPos = new Vector3(source.X, source.Y, -_groundBelowPelvis) + d.TargetOffset;
            }
            for (int n = 1; n < d.ChainLength; n++)
                c.Positions[n] = _pelvis.WorldRot.Inverse.Rotate(c.Chain[n].WorldPos - _pelvis.WorldPos);
            c.Weight = 1f;
        }

        private void Apply(Constraint c, float time, float dt)
        {
            var d = c.Data;
            if (time < d.EaseInStart) return;
            if (time > d.EaseOutStop) { c.Active = false; return; }
            if (!c.Active || time < d.EaseInStop) Activate(c);
            var root = c.Chain[d.ChainLength];
            var rootPos = root.WorldPos;
            var oldRots = new Q[d.ChainLength + 1];
            for (int n = 0; n <= d.ChainLength; n++)
            {
                oldRots[n] = c.Chain[n].Rot;
                c.Chain[n].Rot = _stateRot[Index(c.Chain[n])];
            }
            var keyframeSource = VolumePos(c.Source, d.SourceOffset);
            var targetPos = d.TargetsGround ? c.GroundPos : VolumePos(c.Target, d.TargetOffset);
            if (d.Plane)
            {
                Vector3 norm;
                if (d.TargetsGround) norm = Vector3.UnitZ;
                else
                {
                    norm = targetPos - c.Target.WorldPos;
                    if (norm == Vector3.Zero) norm = c.Source.WorldRot.Rotate(-1f * d.SourceOffset);
                    norm = Vector3.Normalize(norm);
                }
                targetPos = keyframeSource + norm * Vector3.Dot(targetPos - keyframeSource, norm);
            }
            if (d.ChainLength != 0 && Vector3.DistanceSquared(rootPos, targetPos) * 0.95f > c.Total * c.Total) c.Weight = Lerp(c.Weight, 0f, Interp(0.1f, dt));
            else c.Weight = Lerp(c.Weight, 1f, Interp(0.3f, dt));
            float weight = c.Weight * (d.EaseOutStop == 0f ? 1f : MathF.Min(ConstraintSolver.ClampRescale(time, d.EaseInStart, d.EaseInStop, 0f, 1f), ConstraintSolver.ClampRescale(time, d.EaseOutStart, d.EaseOutStop, 1f, 0f)));
            var sourceToTarget = targetPos - keyframeSource;
            if (d.ChainLength > 0)
            {
                var endJoint = c.Chain[0];
                var endRot = endJoint.WorldRot;
                var positions = new Vector3[4];
                var velocities = new Vector3[3];
                positions[0] = Vector3.Lerp(keyframeSource, targetPos, weight);
                positions[d.ChainLength] = rootPos;
                for (int n = 1; n < d.ChainLength; n++)
                {
                    var kinematic = c.Chain[n].WorldPos + sourceToTarget * c.Fractions[n];
                    positions[n] = _pelvis.WorldRot.Rotate(c.Positions[n]) + _pelvis.WorldPos;
                    float tc = 1f / ConstraintSolver.ClampRescale(c.FixupRms, 0f, 0.5f, 0.2f, 8f);
                    positions[n] = Vector3.Lerp(positions[n], kinematic, Interp(tc, dt));
                }
                for (int it = 0; it < 20; it++)
                {
                    int finished = 0;
                    for (int n = 1; n < d.ChainLength; n++)
                    {
                        var acc = (positions[n - 1] - positions[n]) * ((Vector3.Distance(positions[n], positions[n - 1]) - c.Lengths[n - 1]) * 0.7f);
                        acc += (positions[n + 1] - positions[n]) * ((Vector3.Distance(positions[n + 1], positions[n]) - c.Lengths[n]) * 0.7f);
                        if (acc.LengthSquared() < 0.0005f * 0.0005f) finished++;
                        velocities[n - 1] *= 0.7f;
                        positions[n] += velocities[n - 1] + acc * 0.5f;
                        velocities[n - 1] += acc;
                    }
                    if (it >= 2 && finished == d.ChainLength - 1) break;
                }
                for (int n = d.ChainLength; n > 0; n--)
                {
                    var cur = c.Chain[n];
                    var child = c.Chain[n - 1];
                    var parentRot = cur.Parent!.WorldRot;
                    var curRot = cur.WorldRot;
                    var targetAt = positions[n - 1] - positions[n];
                    var currentAt = n == 1 ? VolumePos(c.Source, d.SourceOffset) - cur.WorldPos : curRot.Rotate(child.Pos);
                    var fixup = Q.ShortestArc(currentAt, targetAt);
                    var targetRot = Q.Then(Q.Then(curRot, fixup), parentRot.Inverse);
                    if (weight != 1f) targetRot = Q.Nlerp(weight, _stateRot[Index(cur)], targetRot);
                    _stateRot[Index(cur)] = targetRot;
                    cur.Rot = targetRot;
                }
                var endLocal = Q.Then(endRot, endJoint.Parent!.WorldRot.Inverse);
                _stateRot[Index(endJoint)] = weight == 1f ? endLocal : Q.Nlerp(weight, _stateRot[Index(endJoint)], endLocal);
                c.FixupRms = 0f;
                float deltaTime = MathF.Max(0.02f, MathF.Abs(time - _lastUpdate));
                for (int n = 1; n < d.ChainLength; n++)
                {
                    var np = _pelvis.WorldRot.Inverse.Rotate(positions[n] - _pelvis.WorldPos);
                    c.FixupRms += Vector3.DistanceSquared(np, c.Positions[n]) / deltaTime;
                    c.Positions[n] = np;
                }
                c.FixupRms *= 1f / (c.Total * (d.ChainLength - 1));
                c.FixupRms = MathF.Sqrt(c.FixupRms);
                for (int n = 0; n <= d.ChainLength; n++) c.Chain[n].Rot = oldRots[n];
            }
            else if (_statePos[Index(c.Chain[0])] is not null)
            {
                var delta = sourceToTarget * weight;
                var parentRot = c.Chain[0].Parent?.WorldRot ?? Q.Id;
                delta = parentRot.Inverse.Rotate(delta);
                _statePos[Index(c.Chain[0])] = c.Chain[0].Pos + delta;
            }
        }

        private static float Interp(float timeConstant, float dt) => Math.Clamp(1f - MathF.Pow(2f, -dt / timeConstant), 0f, 1f);
        private static float Lerp(float a, float b, float t) => a * (1f - t) + b * t;
    }

    private static (AvatarRig Rig, AvatarShape? Shape, float Hip, float Lift) Avatar(string? paramsVariable)
    {
        var body = AvatarBody.Load(CharacterFactAttribute.Folder!);
        var rig = new AvatarRig(body.Skeleton);
        float hip = body.Skeleton.Joints[0].Position.Z - body.FootHeight;
        if (paramsVariable is null) return (rig, null, hip, 0f);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(LocalReferenceFactAttribute.Path(paramsVariable)!));
        var order = doc.RootElement.GetProperty("order").EnumerateArray().Select(e => e.GetInt32()).ToImmutableArray();
        var raw = doc.RootElement.GetProperty("bytes").EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
        var shape = AvatarShape.From(rig, body.Shape!, new Viewer.Core.World.VisualParamSet(raw, order));
        var neutral = AvatarShape.Neutral(rig);
        return (rig, shape, hip, PoseComputer.LiftOf(neutral, neutral.Measure(null)?.PelvisToFoot ?? 0f, shape, null, 0f));
    }

    private sealed class ReproductionFactAttribute : FactAttribute
    {
        public ReproductionFactAttribute()
        {
            if (CharacterFactAttribute.Folder is null) Skip = "local-only character data: NV_CHARACTER_DIR is not set";
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GroundConstraintTests.GroundSitFactAttribute.Variable))) Skip = "local-only animation: NV_GROUND_SIT_ANIM is not set";
        }
    }

    [ReproductionFact]
    public void The_ground_sit_matches_the_reproduction_of_the_official_solver_at_every_frame_on_land_and_on_a_floor()
    {
        var clip = AnimationClip.Parse(File.ReadAllBytes(Environment.GetEnvironmentVariable(GroundConstraintTests.GroundSitFactAttribute.Variable)!));
        bool pelvisOnly = Environment.GetEnvironmentVariable("NV_CONSTRAINT_BEFORE") == "1";   // the viewer as it was before the limb constraints: only the pelvis one
        var ours = pelvisOnly ? new AnimationClip
        {
            Duration = clip.Duration, Loop = clip.Loop, LoopIn = clip.LoopIn, LoopOut = clip.LoopOut, Priority = clip.Priority, EaseIn = clip.EaseIn, EaseOut = clip.EaseOut,
            Emote = clip.Emote, Tracks = clip.Tracks, HandPose = clip.HandPose, Constraints = clip.Constraints.Where(c => c.ChainLength == 0).ToArray(),
        } : clip;
        var cases = new List<(string Name, string? Variable)> { ("default skeleton", null) };
        if (LocalReferenceFactAttribute.Path("NV_FACE_LOOK_PARAMS") is not null) cases.Add(("stored shape A", "NV_FACE_LOOK_PARAMS"));
        if (LocalReferenceFactAttribute.Path("NV_FACE_LOOK_PARAMS_B") is not null) cases.Add(("stored shape B", "NV_FACE_LOOK_PARAMS_B"));
        var failures = new List<string>();
        foreach (var (name, variable) in cases)
        {
            var (rig, shape, hip, lift) = Avatar(variable);
            foreach (var (ground, floorGap) in new[] { ("land", 0f), ("floor", 0.05f) })
            {
                float below = hip + lift + floorGap;
                var reference = new Reference(rig, shape, clip, below);
                var solver = new ConstraintSolver(rig);
                var state = ConstraintState.For(null, ours, rig.NodeCount)!;
                var tracks = AvatarJoints.TracksFor(rig, ours);
                var joints = new AvatarJoints(rig) { Shape = shape };
                var watch = clip.Constraints.Where(c => c.ChainLength > 0).ToList();
                float worst = 0f; int worstFrame = 0; string worstWhat = "";
                var perConstraint = new float[watch.Count];
                int frames = (int)(3.5f * 60);
                for (int f = 0; f < frames; f++)
                {
                    float t = clip.TimeAt(f / 60.0);
                    reference.Update(t, 1f / 60f);
                    var pose = solver.Step(state, tracks, shape, null, t, below);
                    joints.Constraint = pose;
                    joints.Begin(tracks, t);
                    for (int w = 0; w < watch.Count; w++)
                    {
                        var c = watch[w];
                        int vol = rig.Skeleton.IndexOf(c.SourceVolume);
                        var refVol = reference.Joints[vol];
                        var refSource = refVol.WorldRot.Rotate(c.SourceOffset * refVol.Scale) + refVol.WorldPos;
                        var ourSource = Vector3.Transform(c.SourceOffset, joints.World(vol));
                        float e = Vector3.Distance(refSource, ourSource);
                        for (int n = vol, k = 0; k <= c.ChainLength; k++)
                        {
                            n = rig.Parent(n);
                            e = MathF.Max(e, Vector3.Distance(reference.Joints[n].WorldPos, joints.WorldPosition(n)));
                        }
                        if (Environment.GetEnvironmentVariable("NV_CONSTRAINT_TRACE") == "1" && name == "default skeleton" && ground == "land" && c.SourceVolume == "R_FOOT" && f % 6 == 0)
                            _out.WriteLine($"trace f{f} t{t:F2} R_FOOT err {e * 100:F1} cm  ref {refSource.X:F3},{refSource.Y:F3},{refSource.Z:F3} ours {ourSource.X:F3},{ourSource.Y:F3},{ourSource.Z:F3}");
                        perConstraint[w] = MathF.Max(perConstraint[w], e);
                        if (e > worst) { worst = e; worstFrame = f; worstWhat = $"{c.SourceVolume} chain {c.ChainLength}"; }
                    }
                }
                _out.WriteLine($"{name}, {ground}: worst {worst * 100:F2} cm at frame {worstFrame} ({worstWhat}); per constraint: " +
                    string.Join(", ", watch.Select((c, i) => $"{c.SourceVolume}={perConstraint[i] * 100:F1}")));
                if (worst > 0.03f) failures.Add($"{name}, {ground}: {worst * 100:F1} cm at frame {worstFrame} ({worstWhat})");
            }
        }
        Assert.True(failures.Count == 0, "the limbs are not where the reproduction of the official solver puts them (limit 3 cm): " + string.Join("; ", failures));
    }

    [ReproductionFact]
    public void The_ground_sit_holds_hands_and_feet_on_the_ground()
    {
        // the target of each limb constraint is the ground: after the ease-in, its source point is within a few centimetres of the ground plane (or short of it when out of reach)
        var clip = AnimationClip.Parse(File.ReadAllBytes(Environment.GetEnvironmentVariable(GroundConstraintTests.GroundSitFactAttribute.Variable)!));
        var (rig, shape, hip, lift) = Avatar(null);
        float below = hip + lift;
        var pose = GroundConstraintTests.Run(clip, rig, shape, below, 2.5f, out _);
        var joints = new AvatarJoints(rig) { Shape = shape, Constraint = pose };
        joints.Begin(AvatarJoints.TracksFor(rig, clip), clip.TimeAt(2.5));
        int held = 0;
        // (the constraints that let go before then, the hands on the ground, are not held at that time)
        foreach (var c in clip.Constraints.Where(c => c.ChainLength > 0 && c.TargetsGround && c.EaseOutStop >= 10f))
        {
            float z = Vector3.Transform(c.SourceOffset, joints.World(rig.Skeleton.IndexOf(c.SourceVolume))).Z;
            float want = -below + c.TargetOffset.Z;
            _out.WriteLine($"{c.SourceVolume} chain {c.ChainLength}: source {z:F3}, ground {want:F3}");
            Assert.InRange(z, want - 0.10f, want + 0.10f);   // the official solver stops a few centimetres short for the lower legs (the reproduction above agrees)
            held++;
        }
        // the hands held to the upper legs end on them
        foreach (var c in clip.Constraints.Where(c => c.ChainLength > 0 && !c.TargetsGround && c.EaseOutStop >= 10f))
        {
            var hand = Vector3.Transform(c.SourceOffset, joints.World(rig.Skeleton.IndexOf(c.SourceVolume)));
            var leg = Vector3.Transform(c.TargetOffset, joints.World(rig.Skeleton.IndexOf(c.Target)));
            _out.WriteLine($"{c.SourceVolume} to {c.Target}: {Vector3.Distance(hand, leg) * 100:F1} cm apart");
            Assert.True(Vector3.Distance(hand, leg) < 0.05f, $"{c.SourceVolume} is {Vector3.Distance(hand, leg) * 100:F1} cm from its place on {c.Target}");
            held++;
        }
        Assert.True(held > 0, "the animation has limb constraints that hold a point to the ground");
    }
}
