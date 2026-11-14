// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>
/// Where an avatar is drawn between the region's updates. The region sends an avatar's position, velocity and rotation
/// a few times a second; drawing each one as it arrives makes a walking avatar jump. Between updates the position is
/// carried forward along the last stated velocity: at full speed for <see cref="PhaseOutSeconds"/>, then slowing evenly
/// to a stop at <see cref="MaxExtrapolationSeconds"/>, as the official viewer does (its object update interpolation
/// times), so an avatar whose updates stop does not drift off. A region sends a moving avatar's position again only when
/// its velocity changes or it has gone 5 m, more than a second apart at a walk, so a shorter limit stalls a walking
/// avatar between updates. When an update arrives, the difference between where the avatar was being drawn and
/// where the region now puts it is kept as an offset that shrinks linearly to nothing over
/// <see cref="CorrectionSeconds"/>, so the drawn position never jumps; the rotation turns from the drawn one to the new one
/// over <see cref="TurnSeconds"/>. A correction larger than <see cref="SnapDistance"/> (a teleport within the region, a
/// sit) is taken at once. Times are in seconds on the caller's clock. Not thread-safe; one instance per avatar.
/// <para>Three refinements keep stops and fast motion clean:</para>
/// <list type="bullet">
/// <item>Another avatar is drawn <see cref="OtherDelaySeconds"/> behind the region's stated motion. When it stops, the
/// update saying so arrives a network trip and a region frame after the stop, and an avatar carried on its velocity
/// until then has gone past the stop point and must come back; drawn this little behind, it reaches the stop point
/// about as the update arrives and eases into it instead. The user's own avatar is drawn <see cref="SelfDelaySeconds"/>
/// behind: measured live, when its key comes up the region stops it 0.11 to 0.16 m short of where it is drawn at a
/// walk (30 to 40 ms of its travel), so drawn that little behind it is at the region's stop point as the key comes up.</item>
/// <item>The region sends a steadily moving avatar's position each time it has gone 5 m, so consecutive updates at
/// the same velocity are a known time apart however late each packet is. An update that arrives later than its
/// distance from the last one says is taken as of when it was due (at most <see cref="MaxLateSeconds"/> back), so a
/// late packet causes no correction, and no catch-up faster than the avatar's own speed.</item>
/// <item><see cref="Halt"/>: the user's own avatar, its movement key released, brakes to a stop within
/// <see cref="BrakeSeconds"/> (two frames, so it reads as a stop rather than a freeze) instead of being carried on until
/// the region's stop update arrives; updates still saying it moves are passed over until the region says it stopped.</item>
/// <item>A descending avatar's update is never taken as late (<see cref="Descending"/>): the one that says it has
/// reached the ground is a short step, not a late one. Keeping a drawn avatar above the terrain itself is the scene's
/// part (it knows the land).</item>
/// <item>The user's own avatar catches up gently (<see cref="SelfCatchUp"/>): a correction that moves it further along
/// its motion is drawn at no more than 7 % of its speed on top of it, so a flying avatar is not drawn faster than
/// it flies each time an update leaves it a little behind.</item>
/// </list>
/// </summary>
public sealed class AvatarMotion : IAvatarMotion
{
    public const double PhaseOutSeconds = 2.0, MaxExtrapolationSeconds = 3.0, CorrectionSeconds = 0.2, TurnSeconds = 0.15;
    public const float SnapDistance = 5f;
    /// <summary>How far behind the region's motion another avatar is drawn; how late an update may be taken as of when
    /// it was due; how long the own avatar brakes after its key is released, and how long after that braking an update
    /// that still says it moves is passed over.</summary>
    public const double OtherDelaySeconds = 0.15, SelfDelaySeconds = 0.04, MaxLateSeconds = 0.5, BrakeSeconds = 0.03, HaltHoldSeconds = 0.6;
    /// <summary>The own avatar's catch-up: a correction that moves it further along its motion is drawn at no more than
    /// this fraction of its speed on top of it, and over no more than <see cref="MaxCatchUpSeconds"/>.</summary>
    public const float SelfCatchUp = 0.07f;
    public const double MaxCatchUpSeconds = 1.0;
    /// <summary>Speeds (m/s) below which an avatar counts as stopped, and above which as moving steadily.</summary>
    public const float StoppedSpeed = 0.05f, MovingSpeed = 0.5f;

    private readonly double _delay;
    private readonly float _catchUp;
    private Vector3 _position, _velocity, _offset;
    private Quaternion _rotation = Quaternion.Identity, _turnFrom = Quaternion.Identity;
    private double _at, _anchor, _haltedAt = double.NaN, _correction = CorrectionSeconds;
    private bool _known, _braking;

    /// <param name="delaySeconds">How far behind the region's motion the avatar is drawn (<see cref="OtherDelaySeconds"/>
    /// for other avatars, <see cref="SelfDelaySeconds"/> for the user's own).</param>
    /// <param name="catchUp">0: every correction takes <see cref="CorrectionSeconds"/>. Above 0 (<see cref="SelfCatchUp"/>
    /// for the user's own avatar): a correction that moves a moving avatar further along its motion is drawn at no
    /// more than this fraction of its speed on top of it.</param>
    public AvatarMotion(double delaySeconds = 0, float catchUp = 0)
    {
        _delay = Math.Max(0, delaySeconds);
        _catchUp = Math.Max(0, catchUp);
    }

    /// <summary>Whether the avatar is braking or held after <see cref="Halt"/>.</summary>
    public bool Halted => !double.IsNaN(_haltedAt);

    /// <summary>Updates taken as of when they were due rather than when they arrived (late packets).</summary>
    public int LateUpdates { get; private set; }

    /// <summary>Updates received, and how many of them were taken at once (the first, and snaps).</summary>
    public int Updates { get; private set; }
    public int Snaps { get; private set; }

    /// <summary>An update from the region at <paramref name="now"/>.</summary>
    public void Update(Vector3 position, Vector3 velocity, Quaternion rotation, double now)
    {
        if (Halted)
        {
            if (velocity.Length() >= StoppedSpeed && now - _haltedAt < BrakeSeconds + HaltHoldSeconds
                && Vector3.Distance(At(now).Position, position) <= SnapDistance)
                return;   // the region has not stopped it yet: the braking stands
            _haltedAt = double.NaN;
        }
        double anchor = now;
        if (_known && !_braking && Steady(_velocity, velocity) && !Descending(velocity))
        {
            // a steady mover's next update is due when it has covered the distance between the two
            double due = _anchor + Vector3.Dot(position - _position, velocity) / velocity.LengthSquared();
            if (due < now && now - due <= MaxLateSeconds)
            {
                anchor = due;
                LateUpdates++;
            }
        }
        if (_known)
        {
            var (drawn, drawnRotation) = At(now);
            _offset = drawn - (position + velocity * (float)Carried(now - anchor - _delay));
            _correction = CatchUp(_offset, velocity);
            _turnFrom = drawnRotation;
            if (_offset.Length() > SnapDistance)
            {
                _offset = Vector3.Zero;
                _turnFrom = rotation;
                Snaps++;
            }
        }
        else
        {
            _offset = Vector3.Zero;
            _turnFrom = rotation;
            Snaps++;
        }
        _position = position;
        Stated = position;
        _velocity = velocity;
        _rotation = rotation;
        _at = now;
        _anchor = anchor;
        _braking = false;
        _known = true;
        Updates++;
    }

    /// <summary>How long a correction by <paramref name="offset"/> takes. The region reports a start or a speed-up a
    /// frame and a network trip after it, already that far along, and a flying avatar's updates come 5 m apart with the
    /// arrival times varying by a few hundredths of a second; drawn close behind the region (the own avatar), each such
    /// update leaves it behind where it should be, and taking that back in the usual fifth of a second draws it a
    /// quarter faster than it moves. With a catch-up fraction the gap along the motion is taken back no faster than
    /// that fraction of the speed.</summary>
    private double CatchUp(Vector3 offset, Vector3 velocity)
    {
        float speed = velocity.Length();
        if (_catchUp <= 0 || speed <= MovingSpeed) return CorrectionSeconds;
        float behind = -Vector3.Dot(offset, velocity) / speed;
        if (behind <= 0) return CorrectionSeconds;
        return Math.Clamp(behind / (_catchUp * speed), CorrectionSeconds, MaxCatchUpSeconds);
    }

    private static bool Steady(Vector3 before, Vector3 now) =>
        now.Length() > MovingSpeed && Vector3.Distance(before, now) < 0.1f * now.Length();

    /// <summary>Mostly downward. A descent ends where the avatar meets the ground or a floor, and the region sends that
    /// position at once with the falling velocity still set (the stop follows a frame later): a step shorter than the
    /// 5 m a steady mover's updates are apart, which must not be read as a late packet. Taken as of when it was due, the
    /// avatar would be carried on down through the ground until the stop arrives.</summary>
    private static bool Descending(Vector3 velocity) => -velocity.Z > 0.5f * velocity.Length();

    /// <summary>The position the region last stated (the avatar's centre), before any smoothing.</summary>
    public Vector3 Stated { get; private set; }

    /// <summary>The user's own avatar's movement key was released at <paramref name="now"/>: from where it is drawn it
    /// brakes evenly to a stop over <see cref="BrakeSeconds"/>, and updates that still say it moves are passed over until
    /// the region says it stopped (or <see cref="HaltHoldSeconds"/> after the braking).</summary>
    public void Halt(double now)
    {
        if (!_known || Halted) return;
        var velocity = Velocity(now);
        if (velocity.Length() < StoppedSpeed) return;
        var (drawn, rotation) = At(now);
        _position = drawn;
        _velocity = velocity;
        _offset = Vector3.Zero;
        _turnFrom = _rotation = rotation;
        _at = _anchor = now;
        _braking = true;
        _haltedAt = now;
    }

    /// <summary>A movement key is held again: updates are taken as they come.</summary>
    public void Resume() => _haltedAt = double.NaN;

    /// <summary>The velocity the drawn position is carried at, at <paramref name="now"/> (without the correction).</summary>
    public Vector3 Velocity(double now)
    {
        double since = now - _anchor - (_braking ? 0 : _delay);
        if (_braking) return since >= BrakeSeconds ? Vector3.Zero : _velocity * (float)(1 - since / BrakeSeconds);
        double fraction = since <= PhaseOutSeconds ? 1 : Math.Max(0, (MaxExtrapolationSeconds - since) / (MaxExtrapolationSeconds - PhaseOutSeconds));
        return _velocity * (float)fraction;
    }

    /// <summary>An update taken at once, with no correction or turn (the position's frame changed: a sit or a stand).</summary>
    public void Snap(Vector3 position, Vector3 velocity, Quaternion rotation, double now)
    {
        _known = false;
        Update(position, velocity, rotation, now);
    }

    /// <summary>Where the avatar is drawn at <paramref name="now"/>, and its rotation.</summary>
    public (Vector3 Position, Quaternion Rotation) At(double now)
    {
        double since = Math.Max(0, now - _at);
        double carriedFor = now - _anchor - (_braking ? 0 : _delay);
        double carried = _braking ? Braked(carriedFor) : Carried(carriedFor);
        float left = (float)Math.Max(0, 1 - since / _correction);
        float turned = (float)Math.Min(1, since / TurnSeconds);
        return (_position + _velocity * (float)carried + _offset * left, Quaternion.Normalize(Quaternion.Slerp(_turnFrom, _rotation, turned)));
    }

    /// <summary>How many seconds of the velocity at a halt the position has been carried <paramref name="since"/> seconds
    /// after it: the speed falling evenly to nothing at <see cref="BrakeSeconds"/>.</summary>
    public static double Braked(double since)
    {
        double t = Math.Min(since, BrakeSeconds);
        return t - t * t / (2 * BrakeSeconds);
    }

    /// <summary>How many seconds of the stated velocity the position has been carried <paramref name="since"/> seconds after
    /// an update: all of them until <see cref="PhaseOutSeconds"/>, then a speed falling evenly to nothing at
    /// <see cref="MaxExtrapolationSeconds"/>. A negative time (an avatar drawn behind its update by the delay) is carried
    /// back along the velocity.</summary>
    public static double Carried(double since)
    {
        if (since <= PhaseOutSeconds) return since;
        double fade = MaxExtrapolationSeconds - PhaseOutSeconds;
        double t = Math.Min(since, MaxExtrapolationSeconds) - PhaseOutSeconds;
        return PhaseOutSeconds + t - t * t / (2 * fade);
    }

    /// <summary>The heading of a rotation: radians about up from east (the direction the avatar's forward axis points).</summary>
    public static float Yaw(Quaternion rotation)
    {
        var f = Vector3.Transform(Vector3.UnitX, rotation);
        return MathF.Atan2(f.Y, f.X);
    }
}
