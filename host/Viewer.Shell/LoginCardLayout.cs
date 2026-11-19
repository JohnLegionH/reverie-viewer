// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// How big the login card is for a window, as data. The official viewer lays its login form (panel_login.xml, designed at 1024 x 768)
/// over the whole width of the window, each field about a fifth of it; Reverie's form is a column on the left, so the column takes a
/// share of the window's width instead of a fixed size, and never grows past what the window can show. Sizes are in layout units: a
/// window's pixels divided by the interface-size factor (at 200 percent a 1152 x 648 window lays out as 576 x 324).
/// </summary>
public static class LoginCardLayout
{
    /// <summary>The card's share of the window's width.</summary>
    public const float WidthShare = 0.40f;

    /// <summary>The narrowest card (where the window has room for it) and the widest.</summary>
    public const float MinWidth = 360, MaxWidth = 560;

    /// <summary>Space kept free at each side of the card (the card sits this far from the left edge) and above and below it.</summary>
    public const float SideMargin = 32, VerticalMargin = 40;

    /// <summary>The card's width in a window of <paramref name="windowWidth"/> layout units.</summary>
    public static float Width(float windowWidth) =>
        MathF.Max(0, MathF.Min(MathF.Min(MathF.Max(windowWidth * WidthShare, MinWidth), MaxWidth), windowWidth - 2 * SideMargin));

    /// <summary>The tallest the card may be in a window of <paramref name="windowHeight"/> layout units: it is centred and keeps the
    /// margin above and below, where the release line and the corner buttons stand. A taller form scrolls inside the card.</summary>
    public static float MaxHeight(float windowHeight) => MathF.Max(0, windowHeight - 2 * VerticalMargin);

    /// <summary>A window shorter than this (in layout units: a 1152 x 648 window at 200 percent is 324) gets the compact card.</summary>
    public const float CompactBelowHeight = 400;

    /// <summary>Whether the card is laid out tightly for a window of <paramref name="windowHeight"/> layout units: closer rows, a smaller
    /// header and a shorter status area, so the Log in button shows without scrolling.</summary>
    public static bool Compact(float windowHeight) => windowHeight < CompactBelowHeight;

    /// <summary>Space between the form's rows and round the card, in layout units.</summary>
    public static int RowSeparation(float windowHeight) => Compact(windowHeight) ? 2 : 6;
    public static int CardPadding(float windowHeight) => Compact(windowHeight) ? 10 : 20;

    /// <summary>The header's mark size and the gap under the header.</summary>
    public static int HeaderMark(float windowHeight) => Compact(windowHeight) ? 14 : 22;
    public static int HeaderGap(float windowHeight) => Compact(windowHeight) ? 2 : 6;

    /// <summary>How tall the status area grows before it scrolls.</summary>
    public static float StatusMaxHeight(float windowHeight) => Compact(windowHeight) ? 36 : 100;
}
