// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// One pointer drag on a window's title bar (<see cref="ResizeEdges.None"/>: a move) or on a resize edge or corner. As the official floater's drag
/// and resize handles do (they act only while they hold the mouse capture), the drag lasts only while the button that started it is held: a step
/// that arrives without the button ends it, so a release the window never saw cannot leave it resizing as the pointer merely passes over. Every step
/// places the window from its rectangle at the press and the pointer's whole travel since, so going out and back ends where it began. The window's
/// minimum and the main view are the <see cref="FloaterManager"/>'s to keep.
/// </summary>
public sealed class FloaterDrag
{
    private ResizeEdges _edges;
    private UiRect _start;
    private float _pressX, _pressY;

    /// <summary>True between a press on an open window and the end of the drag.</summary>
    public bool Active { get; private set; }

    /// <summary>Starts a drag at the pointer (x, y) on <paramref name="id"/>; nothing starts when that window is not open.</summary>
    public void Begin(FloaterManager floaters, string id, ResizeEdges edges, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(floaters);
        if (floaters.RectOf(id) is not { } start)
        {
            Active = false;
            return;
        }
        _edges = edges;
        _start = start;
        _pressX = x;
        _pressY = y;
        Active = true;
    }

    /// <summary>One pointer step: moves or resizes the window; false (and the drag over) when no drag is going or the button is no longer held.</summary>
    public bool Track(FloaterManager floaters, string id, float x, float y, bool buttonHeld)
    {
        ArgumentNullException.ThrowIfNull(floaters);
        if (!Active)
            return false;
        if (!buttonHeld || !floaters.IsOpen(id))
        {
            Active = false;
            return false;
        }
        float dx = x - _pressX, dy = y - _pressY;
        if (_edges == ResizeEdges.None)
            floaters.Move(id, _start.X + dx, _start.Y + dy);
        else
            floaters.ResizeBy(id, _edges, _start, dx, dy);
        return true;
    }

    /// <summary>Ends the drag (the button was released).</summary>
    public void End() => Active = false;
}
