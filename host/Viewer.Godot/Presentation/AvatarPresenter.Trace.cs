// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Globalization;
using System.Text;

namespace Viewer.Godot.Presentation;

public sealed partial class AvatarPresenter
{
    private static string V(System.Numerics.Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.000},{v.Y:0.000},{v.Z:0.000}");

    /// <summary>For the per-frame crossing trace (a development aid): each entry this presenter's scene has for <paramref name="agent"/>, whether it is held
    /// and whether it places the body this frame, its motion (updates taken, last stated place, velocity, the motion shared or not), where the scene places
    /// it now and where the layer drew the body it names. Plain text; main thread.</summary>
    internal string TraceAgent(Guid agent)
    {
        var sb = new StringBuilder();
        double now = Scene.Now;
        foreach (var e in Scene.Entries)
        {
            if (e.AgentId != agent) continue;
            var d = e.Handle as DrawnAvatar;
            bool drives = d is not null && ReferenceEquals(d.DriverP, this) && ReferenceEquals(d.DriverE, e);
            bool shared = Scene.OwnMotion?.Shares(e.Motion) ?? false;
            sb.Append(CultureInfo.InvariantCulture, $" entry={e.Id.LocalId} held={(Scene.IsHeld(e) ? 1 : 0)} drives={(drives ? 1 : 0)} shared={(shared ? 1 : 0)} motion={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e.Motion) & 0xFFFF:x}");
            sb.Append(CultureInfo.InvariantCulture, $" upd={e.Motion.Updates} stated={V(e.Motion.Stated)} vel={V(e.Motion.Velocity(now))} now={now:0.0000}");
            if (Scene.Place(e, out var soles, out _)) sb.Append(" place=").Append(V(soles));
            if (_layer is not null && d is { Id: >= 0 } && _layer.TryDrawnSoles(d.Id, out var drawn)) sb.Append(" drawn=").Append(V(drawn));
        }
        return sb.ToString();
    }
}
