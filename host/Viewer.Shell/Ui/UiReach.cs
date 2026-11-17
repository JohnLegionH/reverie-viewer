// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>How much of a control a person can reach: all of it, part of it (a scroll area or the window cuts it), or none.</summary>
public enum UiReachKind { Visible, Clipped, Outside }

/// <summary>Where a control stands against the areas that cut it, and the point a pointer should press.</summary>
/// <param name="Kind">Whether the control is wholly shown, cut, or not shown at all.</param>
/// <param name="Shown">The part of the control that is shown (empty when none is).</param>
/// <param name="Point">The middle of the shown part: where a press lands on the control. Null when nothing is shown.</param>
public readonly record struct UiReachResult(UiReachKind Kind, UiRect Shown, (float X, float Y)? Point);

/// <summary>
/// Whether a control is in view: its rectangle against every rectangle that cuts what is drawn under it (each scroll area or clipping
/// container above it, and the window itself), all in the same coordinates. Plain numbers, so the live checks' rule is tested without the
/// engine: a check presses a control only when it is wholly shown, and reports it cut otherwise.
/// </summary>
public static class UiReach
{
    public static UiReachResult Classify(UiRect control, IReadOnlyList<UiRect> clips)
    {
        if (control.W <= 0 || control.H <= 0)
            return new UiReachResult(UiReachKind.Outside, default, null);
        float left = control.X, top = control.Y, right = control.Right, bottom = control.Bottom;
        foreach (var clip in clips)
        {
            left = Math.Max(left, clip.X);
            top = Math.Max(top, clip.Y);
            right = Math.Min(right, clip.Right);
            bottom = Math.Min(bottom, clip.Bottom);
        }
        if (right <= left || bottom <= top)
            return new UiReachResult(UiReachKind.Outside, default, null);
        var shown = new UiRect(left, top, right - left, bottom - top);
        var kind = shown == control ? UiReachKind.Visible : UiReachKind.Clipped;
        return new UiReachResult(kind, shown, (left + shown.W / 2, top + shown.H / 2));
    }
}
