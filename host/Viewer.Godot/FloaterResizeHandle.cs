// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// One invisible resize strip or corner of a floating window. A corner other than the bottom-right one takes the pointer only along the window's
/// outer border, an L of the strip's thickness, as the official floater's resize handles do (LLResizeHandle::pointInHandle), so what lies just
/// inside the corner stays reachable. The edge strips give way to the title-bar buttons; a corner's border keeps its place over a button's outer
/// padding, as the official corners do over the close button, so the corner can still be grabbed while the button's drawn icon is never covered.
/// </summary>
public partial class FloaterResizeHandle : Control
{
    private readonly float _border;
    private readonly Func<Vector2, bool> _reserved;

    public ResizeEdges Edges { get; }

    public FloaterResizeHandle(ResizeEdges edges, float border, Func<Vector2, bool> reserved)
    {
        Edges = edges;
        _border = border;
        _reserved = reserved;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public FloaterResizeHandle() : this(ResizeEdges.None, 0, _ => false) { }

    public override bool _HasPoint(Vector2 point)
    {
        if (!new Rect2(Vector2.Zero, Size).HasPoint(point))
            return false;
        bool corner = (Edges & (ResizeEdges.Left | ResizeEdges.Right)) != 0 && (Edges & (ResizeEdges.Top | ResizeEdges.Bottom)) != 0;
        if (!corner)
            return !_reserved(GetGlobalTransform() * point);
        if (Edges == (ResizeEdges.Right | ResizeEdges.Bottom))
            return true;
        bool onSide = Edges.HasFlag(ResizeEdges.Left) ? point.X <= _border : point.X >= Size.X - _border;
        bool onEnd = Edges.HasFlag(ResizeEdges.Top) ? point.Y <= _border : point.Y >= Size.Y - _border;
        return onSide || onEnd;
    }
}
