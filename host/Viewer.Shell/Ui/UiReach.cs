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
        // wholly inside every area: decided by the edges themselves, not by a width worked back from them, which can differ in the last
        // bit when a place has fractions (any interface size other than 100 %)
        if (clips.All(c => c.Encloses(control)))
            return new UiReachResult(UiReachKind.Visible, control, (control.X + control.W / 2, control.Y + control.H / 2));
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
        return new UiReachResult(UiReachKind.Clipped, shown, (left + shown.W / 2, top + shown.H / 2));
    }
}
