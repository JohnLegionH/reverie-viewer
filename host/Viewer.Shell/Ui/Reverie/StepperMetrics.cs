// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// The size of a number box's up and down arrows, in interface units (they scale with the interface size). Plain data, tested without
/// the engine; the engine theme reads it.
/// </summary>
public static class StepperMetrics
{
    /// <summary>The width of the arrow column and the height of one arrow, at 100 %: a column 24 wide, two arrows of 14 stacked in a
    /// 28-high number box.</summary>
    public const float ButtonWidth = 24f, ArrowHeight = 14f;

    /// <summary>The least size of a target a pointer must hit, in screen pixels (WCAG 2.5.8).</summary>
    public const float MinTarget = 24f;

    /// <summary>The arrow column's width on screen at an interface size in percent.</summary>
    public static float HitWidth(int percent) => ButtonWidth * percent / 100f;

    /// <summary>One arrow's height on screen at an interface size in percent.</summary>
    public static float HitHeight(int percent) => ArrowHeight * percent / 100f;
}
