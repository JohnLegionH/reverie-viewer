// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// The colour of an unticked check box's outline. A box with no ticked fill is only an outline, so the outline must be seen against the
/// surfaces a box stands on: its contrast against each is at least 3:1 (WCAG 1.4.11, non-text contrast).
/// </summary>
public static class CheckColours
{
    /// <summary>The least contrast an outline has against the surface behind it.</summary>
    public const double MinContrast = 3.0;

    private static readonly string[] Surfaces = ["surface.base", "surface.raised", "surface.sunken", "surface.card"];

    /// <summary>The first of the look's border and text colours that reaches <see cref="MinContrast"/> against every surface a box stands on.</summary>
    public static UiColour Outline(ReverieTokens r)
    {
        foreach (var name in new[] { "border.strong", "text.muted", "text.secondary", "text.primary" })
            if (Surfaces.All(s => UiColour.Contrast(r[name], r[s]) >= MinContrast))
                return r[name];
        return r["text.strong"];
    }

    /// <summary>The surfaces an outline is held against, by token name.</summary>
    public static IReadOnlyList<string> SurfaceNames => Surfaces;
}
