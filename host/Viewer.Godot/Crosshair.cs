// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Godot;

namespace Viewer.Godot;

/// <summary>The mouse-look crosshair: a small plus at the middle of the window (the official viewer draws a crosshair image there
/// while the ShowCrosshairs setting is on).</summary>
internal sealed partial class Crosshair : Control
{
    public Crosshair()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        var c = Size / 2;
        var shadow = ShellTheme.C(UiTheme.Tokens.Outline);
        var line = ShellTheme.C(UiTheme.Tokens.Text);
        foreach (var (a, b) in new[] { (new Vector2(-12, 0), new Vector2(-4, 0)), (new Vector2(4, 0), new Vector2(12, 0)), (new Vector2(0, -12), new Vector2(0, -4)), (new Vector2(0, 4), new Vector2(0, 12)) })
        {
            DrawLine(c + a, c + b, shadow, 4f);
            DrawLine(c + a, c + b, line, 2f);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
    }
}
