// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;

namespace Viewer.Godot;

/// <summary>The rectangle a drag selection draws over the world: a thin bright edge and a faint fill. It takes no input and draws only
/// when the rectangle changes.</summary>
public partial class DragBoxOverlay : Control
{
    private static readonly Color Edge = new(1f, 0.9f, 0.2f), Fill = new(1f, 0.9f, 0.2f, 0.08f);
    private Rect2? _box;

    /// <summary>The rectangle as it was last drawn (for the live checks).</summary>
    public Rect2? Drawn { get; private set; }

    /// <summary>How many times a rectangle has been drawn (for the live checks).</summary>
    public int Draws { get; private set; }

    public DragBoxOverlay()
    {
        Name = "drag_box";
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
    }

    public void Show(Rect2? box)
    {
        if (box == _box)
            return;
        _box = box;
        Visible = box is not null;
        if (box is null)
            Drawn = null;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_box is not { } r)
            return;
        DrawRect(r, Fill);
        DrawRect(r, Edge, filled: false, width: 1f);
        Drawn = r;
        Draws++;
    }
}
