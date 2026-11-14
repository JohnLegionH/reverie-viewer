// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Core.Tests;

/// <summary>
/// The user's own avatar moved by scripted key events with no login: the real key input, the own-avatar driver, the
/// drawn motion and the follow camera, against a small region model. The model steps every 1/11 s (OpenSim's default
/// frame rate) and answers the controls the viewer sends, a network trip late: a move bit moves the avatar along the
/// body rotation it was sent at 2.46 m/s, 4.1 m/s once the fast bit is set; a release without the stop bit keeps it
/// moving a quarter of a second more (OpenSim's delayed stop), a release with the stop bit stops it at the next
/// frame. It sends the avatar's motion as OpenSim's ScenePresence.Update does: when the velocity changes by more than
/// 0.1 m/s, when it stops, when the heading changes while it moves, or when it has gone 5 m since the last update.
/// The viewer draws at 60 frames a second. Not shared between tests.
/// </summary>
public sealed class MovementRig
{
    public const double Frame = 1.0 / 60, Tick = 1.0 / 11;
    private const uint SelfLocal = 800;
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");

    /// <summary>A key event: the command goes down or up at <see cref="At"/> seconds.</summary>
    public readonly record struct Key(double At, bool Down, MoveCommand Command);

    /// <summary>One drawn frame.</summary>
    public readonly record struct Sample(double At, Vector3 Soles, Vector3 Eye, float Heading, Vector3 Target = default);

    /// <summary>What the mouse does: enters or leaves mouse-look, moves (<c>Dx</c> pixels right, <c>Dy</c> down) or presses or releases the left button.</summary>
    public enum LookKind { Enter, Leave, Move, ClickDown, ClickUp }

    /// <summary>A mouse event at <see cref="At"/> seconds.</summary>
    public readonly record struct Look(double At, LookKind Kind, float Dx = 0, float Dy = 0);

    public sealed class Result
    {
        public List<Sample> Frames { get; } = [];
        /// <summary>Every update the viewer sent: when, and its control bits.</summary>
        public List<(double At, uint Controls)> Sent { get; } = [];
        /// <summary>The body rotation of every update the viewer sent.</summary>
        public List<(double At, Quaternion Body)> SentBody { get; } = [];
        public int Halts { get; set; }
        /// <summary>Every run state told to the region: when, and whether the avatar runs.</summary>
        public List<(double At, bool Run)> RunMessages { get; } = [];
    }

    /// <summary>The presentation side of the backlog case: every change applied costs a millisecond of the frame's budget.</summary>
    private sealed class SceneSink(AvatarScene scene, long[] ticks) : IPresentationSink
    {
        public PresentationCoordinator? Queue;
        public void Apply(WorldChange change) { if (Queue?.AppliedEarly != true) scene.Apply(change); ticks[0]++; }
        public bool Step() => false;
    }

    private sealed record Packet(double Arrives, Vector3 Position, Quaternion Rotation, Vector3 Velocity, bool First);

    private sealed class Region(Random rng, double latency, double jitter, double wobble)
    {
        public Vector3 Position = new(100, 100, 25);
        public Vector3 Velocity;
        public Quaternion Rotation = Quaternion.Identity;
        public readonly Queue<Packet> Wire = new();
        private Vector3 _sentPosition, _sentVelocity;
        private Quaternion _sentRotation = Quaternion.Identity;
        private double _nextExtra = 0.3 + rng.NextDouble() * 1.1, _lastArrival, _delayedStop = -1;
        private bool _moving;
        private int _sent;
        private float _lastJitterZ;

        public void Step(double now, uint controls, Quaternion body)
        {
            const uint move = AgentControlFlags.AtPos | AgentControlFlags.NudgeAtPos;
            Rotation = body;
            float heading = AvatarMotion.Yaw(body);
            var dir = new Vector3(MathF.Cos(heading), MathF.Sin(heading), 0);
            // flying: the fast bit moves the avatar at 16 m/s, and the up and down bits climb and sink at 16 m/s (what the region reports live) (walking ignores them)
            bool flying = (controls & AgentControlFlags.Fly) != 0;
            float climb = flying ? ((controls & AgentControlFlags.UpPos) != 0 ? 16f : 0f) - ((controls & AgentControlFlags.UpNeg) != 0 ? 16f : 0f) : 0f;
            if ((controls & move) != 0 || climb != 0)
            {
                _moving = true;
                _delayedStop = -1;
                float speed = (controls & move) == 0 ? 0f : flying ? ((controls & AgentControlFlags.FastAt) != 0 ? 16f : 2.46f) : ((controls & AgentControlFlags.FastAt) != 0 ? 4.1f : 2.46f);
                Velocity = dir * speed + new Vector3(0, 0, climb);
            }
            else if (_moving && (controls & AgentControlFlags.Stop) != 0)
            {
                _moving = false;
                Velocity = Vector3.Zero;
            }
            else if (_moving && _delayedStop < 0) _delayedStop = now + 0.25;
            if (_delayedStop >= 0 && now >= _delayedStop) { _moving = false; _delayedStop = -1; Velocity = Vector3.Zero; }

            Position += Velocity * (float)Tick;
            bool turned = _moving && Quaternion.Dot(Rotation, _sentRotation) < 0.99995f;
            bool send = _sent == 0
                || Vector3.Distance(Velocity, _sentVelocity) > 0.1f
                || (Velocity == Vector3.Zero && _sentVelocity != Vector3.Zero)
                || turned
                || Vector3.Distance(Position, _sentPosition) > 5f;
            // uneven ground: while moving, every step changes the height a little, so the region reports a new velocity every step
            var wobbleOffset = Vector3.Zero;
            if (wobble > 0 && _moving)
            {
                wobbleOffset = new Vector3((float)((rng.NextDouble() * 2 - 1) * wobble * 0.5), (float)((rng.NextDouble() * 2 - 1) * wobble * 0.5), (float)((rng.NextDouble() * 2 - 1) * wobble));
                send = true;
            }
            if (now >= _nextExtra)
            {
                send = true;
                _nextExtra = now + 0.3 + rng.NextDouble() * 1.1;
            }
            if (!send) return;
            double arrives = Math.Max(now + latency + rng.NextDouble() * jitter, _lastArrival);   // never overtakes an earlier packet
            _lastArrival = arrives;
            var wobbleVelocity = wobble > 0 && _moving ? new Vector3(0, 0, (wobbleOffset.Z - _lastJitterZ) / (float)Tick) : Vector3.Zero;
            _lastJitterZ = wobbleOffset.Z;
            Wire.Enqueue(new Packet(arrives, Position + wobbleOffset, Rotation, Velocity + wobbleVelocity, _sent == 0));
            (_sentPosition, _sentVelocity, _sentRotation) = (Position, Velocity, Rotation);
            _sent++;
        }
    }

    /// <summary>Runs <paramref name="keys"/> for <paramref name="seconds"/> and returns what was drawn and sent.
    /// <paramref name="backlog"/>: object changes already waiting in the presentation queue when the run starts (two are applied a
    /// frame), with the avatar fast lane in front of it, as in a region whose objects are still being taken in; 0: avatar changes
    /// are applied as they come. <paramref name="wobble"/>: metres of noise on the height the region states for a moving avatar (half as much sideways), as
    /// walking over uneven ground gives; <paramref name="camera"/>: the follow camera to use (default: a new one).</summary>
    public static Result Run(IEnumerable<Key> keys, double seconds, int seed = 31, double latency = 0.02, double jitter = 0.02, double wobble = 0, FollowCamera? camera = null, IEnumerable<Look>? looks = null, ScriptControls? script = null, int backlog = 0)
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        double clock = 0;
        var scene = new AvatarScene(s.World, ServerAppearanceTests.Me, () => clock);
        var changes = new List<WorldChange>();
        PresentationCoordinator? queue = null;
        if (backlog > 0)
        {
            long[] ticks = [0];
            var sink = new SceneSink(scene, ticks);
            queue = sink.Queue = new PresentationCoordinator(sink, budgetMs: 2, clock: () => ticks[0], ticksPerSecond: 1000)
            {
                IsUrgent = c => c.Kind is WorldChangeKind.AvatarAdded or WorldChangeKind.AvatarChanged or WorldChangeKind.AvatarMoved or WorldChangeKind.AvatarRemoved,
                ApplyUrgent = scene.Apply,
            };
            for (uint i = 0; i < backlog; i++) queue.Enqueue(new WorldChange(WorldChangeKind.ObjectAdded, new ObjectId(1, 5000 + i)));
            s.World.Changed += queue.Enqueue;
        }
        else s.World.Changed += changes.Add;

        var region = new Region(new Random(seed), latency, jitter, wobble);
        var toRegion = new Queue<(double Arrives, uint Controls, Quaternion Body)>();
        var result = new Result();
        var driver = new OwnAvatarDriver(scene, s.World, u =>
        {
            toRegion.Enqueue((clock + 0.03, u.Controls, u.BodyRotation));
            result.Sent.Add((clock, u.Controls));
            result.SentBody.Add((clock, u.BodyRotation));
            return true;
        }, run =>
        {
            result.RunMessages.Add((clock, run));
            return true;
        });
        camera ??= new FollowCamera();
        var pending = new Queue<Key>(keys.OrderBy(k => k.At));
        var mouse = new Queue<Look>((looks ?? []).OrderBy(l => l.At));
        driver.Input.Script = script;
        uint controls = 0;
        var body = Quaternion.Identity;
        double regionAt = 0;
        Vector3 eye = default, look = Vector3.UnitX;
        bool wasHalted = false;
        for (int frame = 0; frame * Frame <= seconds; frame++)
        {
            double now = frame * Frame;
            clock = now;
            while (pending.TryPeek(out var k) && k.At <= now)
            {
                pending.Dequeue();
                if (k.Down) driver.Input.Press(k.Command, now);
                else driver.Input.Release(k.Command);
            }
            while (regionAt <= now)
            {
                while (toRegion.TryPeek(out var c) && c.Arrives <= regionAt) { var u = toRegion.Dequeue(); controls = u.Controls; body = u.Body; }
                region.Step(regionAt, controls, body);
                regionAt += Tick;
            }
            while (region.Wire.TryPeek(out var p) && p.Arrives <= now)
            {
                region.Wire.Dequeue();
                if (p.First) t.Raise(new AvatarUpdated(Here, new AvatarData(SelfLocal, ServerAppearanceTests.Me, "Test User", p.Position, p.Rotation, new Vector3(0.45f, 0.6f, 1.9f), p.Velocity, 0)));
                else t.Raise(new ObjectMoved(Here, SelfLocal, true, p.Position, p.Rotation, p.Velocity, Vector3.Zero, Vector3.Zero));
            }
            foreach (var c in changes.ToList()) scene.Apply(c);
            changes.Clear();
            queue?.RunFrame();
            while (mouse.TryPeek(out var l) && l.At <= now)
            {
                mouse.Dequeue();
                switch (l.Kind)
                {
                    case LookKind.Enter: driver.Input.EnterMouseLook(); camera.EnterMouselook(); scene.HideSelf = true; break;
                    case LookKind.Leave: driver.Input.LeaveMouseLook(); camera.LeaveMouselook(); scene.HideSelf = false; break;
                    case LookKind.Move: driver.Input.LookBy(l.Dx, l.Dy, Frame); break;
                    case LookKind.ClickDown: driver.Input.LeftButton(true); break;
                    case LookKind.ClickUp: driver.Input.LeftButton(false); break;
                }
            }
            driver.Frame(now, Frame, eye, look);
            if (scene.Self is { } self)
            {
                bool halted = self.Motion.Halted;
                if (halted && !wasHalted) result.Halts++;
                wasHalted = halted;
                if (scene.Place(self, out var soles, out float yaw))
                {
                    (eye, look) = driver.Input.MouseLook
                        ? camera.MouselookPose(soles + new Vector3(0, 0, 0.95f), 1.9f, yaw, driver.Input.Pitch, Frame)
                        : camera.Pose(soles + new Vector3(0, 0, 0.95f), yaw, Frame);
                    result.Frames.Add(new Sample(now, soles, eye, yaw, look));
                }
            }
        }
        return result;
    }
}

/// <summary>
/// A measure of how rough a drawn path is, from the samples of one run: positions at a fixed rate give speed
/// (first difference), acceleration (second) and jerk (third). The single figure is the root mean square of the jerk's
/// size over the span of the run, in m/s³, taken for the avatar's soles and for the camera's eye; a path that moves
/// in even steps scores low and one that stops and starts, or steps back, scores high. Also: the peak acceleration,
/// and the lowest speed between two moments (a stall inside a walk).
/// </summary>
public static class JerkMeter
{
    public readonly record struct Figures(double RmsJerk, double PeakJerk, double PeakAcceleration);

    public static Figures Of(IReadOnlyList<Vector3> path, double dt, double from, double to, double t0 = 0)
    {
        var p = path.Select((v, i) => (v, at: t0 + i * dt)).Where(x => x.at >= from - 1e-9 && x.at <= to + 1e-9).Select(x => x.v).ToList();
        var v1 = new List<Vector3>();
        for (int i = 1; i < p.Count; i++) v1.Add((p[i] - p[i - 1]) / (float)dt);
        var a = new List<Vector3>();
        for (int i = 1; i < v1.Count; i++) a.Add((v1[i] - v1[i - 1]) / (float)dt);
        var j = new List<float>();
        for (int i = 1; i < a.Count; i++) j.Add(((a[i] - a[i - 1]) / (float)dt).Length());
        if (j.Count == 0) return default;
        return new Figures(Math.Sqrt(j.Select(x => (double)x * x).Average()), j.Max(), a.Max(x => x.Length()));
    }

    /// <summary>The figures of a run's drawn soles and camera eye between <paramref name="from"/> and <paramref name="to"/> seconds.</summary>
    public static (Figures Avatar, Figures Camera) Of(MovementRig.Result run, double from, double to)
    {
        var frames = run.Frames;
        double t0 = frames[0].At;
        return (Of(frames.Select(f => f.Soles).ToList(), MovementRig.Frame, from, to, t0),
            Of(frames.Select(f => f.Eye).ToList(), MovementRig.Frame, from, to, t0));
    }

    /// <summary>The drawn ground speed (m/s) at the frame at <paramref name="at"/>, over the frame before it.</summary>
    public static float SpeedAt(MovementRig.Result run, double at)
    {
        int i = run.Frames.FindIndex(f => f.At >= at - 1e-9);
        if (i < 1) return 0;
        var d = run.Frames[i].Soles - run.Frames[i - 1].Soles;
        return new Vector2(d.X, d.Y).Length() / (float)MovementRig.Frame;
    }

    /// <summary>The lowest ground speed (m/s) the drawn avatar has between <paramref name="from"/> and <paramref name="to"/>.</summary>
    public static float LowestSpeed(MovementRig.Result run, double from, double to)
    {
        float lowest = float.MaxValue;
        var f = run.Frames;
        for (int i = 1; i < f.Count; i++)
        {
            if (f[i].At < from || f[i].At > to) continue;
            var d = f[i].Soles - f[i - 1].Soles;
            lowest = MathF.Min(lowest, new Vector2(d.X, d.Y).Length() / (float)MovementRig.Frame);
        }
        return lowest;
    }
}
