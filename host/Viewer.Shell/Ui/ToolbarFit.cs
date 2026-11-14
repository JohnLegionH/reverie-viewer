// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>How many toolbar buttons show in a bar of a given width, and how many go behind the "More" button.</summary>
/// <param name="Shown">The buttons drawn in the bar, from the left.</param>
/// <param name="Hidden">The buttons behind the "More" button; when it is above zero the "More" button is drawn too.</param>
public readonly record struct ToolbarFitResult(int Shown, int Hidden)
{
    public bool NeedsMore => Hidden > 0;
}

/// <summary>Decides which toolbar buttons fit a width (pure numbers, so it is tested without the engine).</summary>
public static class ToolbarFit
{
    /// <summary>Buttons that are all <paramref name="buttonWidth"/> wide.</summary>
    public static ToolbarFitResult Plan(int count, float available, float buttonWidth, float separation, float moreWidth) =>
        Plan(Enumerable.Repeat(buttonWidth, Math.Max(0, count)).ToArray(), available, separation, moreWidth);

    /// <summary>
    /// Every button shows when they all fit. Otherwise as many as fit, from the left, show together with the "More" button at the right
    /// end (a separation follows each shown button), and the rest are behind it; when not even one button fits with it, only "More" shows.
    /// </summary>
    public static ToolbarFitResult Plan(IReadOnlyList<float> widths, float available, float separation, float moreWidth)
    {
        int count = widths.Count;
        if (count == 0)
            return new ToolbarFitResult(0, 0);
        float all = widths.Sum() + separation * (count - 1);
        if (all <= available)
            return new ToolbarFitResult(count, 0);
        int shown = 0;
        float used = moreWidth;
        while (shown < count - 1 && used + widths[shown] + separation <= available)
        {
            used += widths[shown] + separation;
            shown++;
        }
        return new ToolbarFitResult(shown, count - shown);
    }
}
