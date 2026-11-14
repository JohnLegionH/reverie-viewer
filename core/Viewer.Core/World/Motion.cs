// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// What the agent was being TOLD to do when a self sample arrived (W-30): lets prediction tell "the sim has not started
/// me yet" from "the sim has stopped me". Run is either way the sim can be running us: the always-run toggle or a
/// double-tapped forward key.
/// </summary>
public readonly record struct MovementFlags(bool Forward, bool Back, bool Run, bool Fly);

/// <summary>
/// One sim-stated sample of this session's own avatar (the data contract the prediction work consumes), as the
/// adapter reports it. <see cref="ArrivalMs"/> is a monotonic stamp taken on the packet thread at the instant the
/// update arrived, not when anyone got round to reading it (the gateway's poll carried 261-975 ms of its own lateness).
/// </summary>
public sealed record SelfMotion(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Velocity,
    Vector3 AngularVelocity,
    Vector3 Acceleration,
    double ArrivalMs,
    MovementFlags Movement);

/// <summary>A self sample accepted into the world state, scoped to the region epoch it arrived in.</summary>
public sealed record SelfMotionSample(int Epoch, SelfMotion Motion);

/// <summary>
/// W-30: an update that changes nothing produces nothing (from the gateway's AgentSession.PositionGate). The sim sends
/// the own-position update on a state change as well as on its 5 m stride, so without this a standing avatar re-reports
/// the same sample. A rotation alone is a change. <see cref="Reset"/> forces the next sample through (region change,
/// teleport, a new consumer). Not thread-safe on its own: the world state calls it under its lock.
/// </summary>
public sealed class PositionGate
{
    private Vector3 _pos;
    private Quaternion _rot;
    private Vector3 _vel;
    private bool _everPassed;

    public void Reset() => _everPassed = false;

    public bool ShouldPass(Vector3 pos, Quaternion rot, Vector3 vel)
    {
        var dp = pos - _pos;
        var dv = vel - _vel;
        bool changed = !_everPassed
            || dp.LengthSquared() > 1e-4f
            || dv.LengthSquared() > 1e-4f
            || rot != _rot;
        if (!changed)
            return false;
        _pos = pos;
        _rot = rot;
        _vel = vel;
        _everPassed = true;
        return true;
    }
}

/// <summary>An llTargetOmega spin: unit axis and rate in rad/s.</summary>
public readonly record struct TargetOmega(Vector3 Axis, float Rate);

/// <summary>
/// llSetTextureAnim as the simulator sent it (WV-4b): flags passed through raw, so a mode the presentation does not
/// implement is still visible. Face -1 is ALL_SIDES (0xFF on the wire).
/// </summary>
public sealed record TextureAnim(int Face, uint Flags, int SizeX, int SizeY, float Start, float Length, float Rate)
{
    /// <summary>Null when flags are 0: every prim carries the struct on the wire, and flags 0 is how the sim says "none".</summary>
    public static TextureAnim? FromWire(uint face, uint flags, uint sizeX, uint sizeY, float start, float length, float rate) =>
        flags == 0 ? null : new TextureAnim(face >= 0xFF ? -1 : (int)face, flags, (int)sizeX, (int)sizeY, start, length, rate);
}

/// <summary>The motion-field rules of the gateway's WV-4b (AgentSession.NonZero / OmegaOf), on plain vectors.</summary>
public static class MotionFields
{
    /// <summary>A motion vector worth carrying: zero (the overwhelmingly common case) is null.</summary>
    public static Vector3? NonZero(Vector3 v) => v == Vector3.Zero ? null : v;

    /// <summary>
    /// A prim's angular velocity read as an llTargetOmega spin. The simulator sends axis and rate multiplied together,
    /// so the split is a normalise. Null for no spin or a non-finite vector.
    /// </summary>
    public static TargetOmega? OmegaOf(Vector3 omega)
    {
        if (omega == Vector3.Zero)
            return null;
        float rate = omega.Length();
        if (rate <= 0f || float.IsNaN(rate) || float.IsInfinity(rate))
            return null;
        return new TargetOmega(omega / rate, rate);
    }
}
