// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell.Build;

/// <summary>A press with the Edit or Move tool off the handles, until it is released: a click selects, a drag draws a selection box
/// (pressing on an object and dragging does not move it: with the official tools only the handles move the selection).</summary>
public sealed class DragSelect(Vector2 from, bool shift)
{
    public Vector2 From { get; } = from;
    public bool Shift { get; } = shift;
    public bool Moved { get; private set; }
    /// <summary>The official drag starts past a two-pixel slop.</summary>
    private const float Slop = 2f;

    private Vector2 _at;

    /// <summary>The rectangle to draw, from the press to the pointer in either direction; null until the press has become a drag.</summary>
    public (Vector2 Min, Vector2 Size)? Box => Moved ? (Vector2.Min(From, _at), Vector2.Abs(_at - From)) : null;

    public void Move(Vector2 at)
    {
        _at = at;
        if (!Moved && Vector2.Distance(at, From) > Slop)
            Moved = true;
    }
}
