// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using Viewer.Core.Diagnostics;

namespace Viewer.Core.Protocol;

/// <summary>
/// A record of what a region crossing does, for finding why an avatar misbehaves at a border: each crossed-region message, each
/// promotion (the region the viewer takes the agent to be in and where in it), and a rate-limited line for the agent updates (the
/// circuit each goes to, with the controls, heading, camera and position they carry). Off unless switched on; only the development
/// automation switches it on.
/// </summary>
internal sealed class CrossingTrace
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(250);

    private readonly CoreLog _log;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private DateTime _lastUpdate = DateTime.MinValue;
    private ulong _lastCircuit;
    private uint _lastControls;

    public CrossingTrace(CoreLog log, Func<DateTime> now)
    {
        _log = log;
        _now = now;
    }

    private volatile bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    private static string P(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.00},{v.Y:0.00},{v.Z:0.00}");

    /// <summary>A crossed-region message arrived: the destination's handle and the position and look-at it carries.</summary>
    public void Message(ulong handle, Vector3 position, Vector3 lookAt)
    {
        if (!Enabled)
            return;
        _log.Info("trace.cross.message", CoreLog.F("handle", handle), CoreLog.F("pos", P(position)), CoreLog.F("look", P(lookAt)));
    }

    /// <summary>A crossing into a circuit the viewer already holds: the capability seed that circuit's child connection has, and the one the
    /// crossed-region message carries. Only a short fingerprint of each is written (a seed is a capability: its address is never logged).</summary>
    public void Seeds(ulong handle, Uri? child, Uri? message)
    {
        if (!Enabled)
            return;
        static string Fingerprint(Uri? u) => u is null ? "none"
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(u.AbsoluteUri)), 0, 6).ToLowerInvariant();
        bool same = child is not null && message is not null && child.Equals(message);
        _log.Info("trace.cross.seeds", CoreLog.F("handle", handle), CoreLog.F("child", Fingerprint(child)), CoreLog.F("message", Fingerprint(message)), CoreLog.F("same", same));
    }

    /// <summary>The viewer made a region the agent's own.</summary>
    public void Promoted(ulong handle, string region, Vector3 position, int circuits)
    {
        if (!Enabled)
            return;
        _log.Info("trace.cross.promoted", CoreLog.F("handle", handle), CoreLog.F("region", region), CoreLog.F("pos", P(position)), CoreLog.F("circuits", circuits));
        lock (_lock) _lastUpdate = DateTime.MinValue;   // the first update after a promotion is always written
    }

    /// <summary>One agent update about to go to the circuit of <paramref name="circuit"/>. True when it was written to the log.</summary>
    public bool Update(ulong circuit, bool movementComplete, uint controls, float yaw, Vector3 camera, Vector3 position)
    {
        if (!Enabled)
            return false;
        var now = _now();
        lock (_lock)
        {
            bool changed = circuit != _lastCircuit || controls != _lastControls;
            if (!changed && now - _lastUpdate < UpdateInterval)
                return false;
            _lastUpdate = now;
            _lastCircuit = circuit;
            _lastControls = controls;
        }
        _log.Info("trace.cross.update", CoreLog.F("circuit", circuit), CoreLog.F("complete", movementComplete), CoreLog.F("controls", controls.ToString("x", CultureInfo.InvariantCulture)),
            CoreLog.F("yaw", yaw.ToString("0.00", CultureInfo.InvariantCulture)), CoreLog.F("camera", P(camera)), CoreLog.F("pos", P(position)));
        return true;
    }
}
