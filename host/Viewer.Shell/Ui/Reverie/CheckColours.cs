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

    private static readonly string[] Surfaces = ["surface.base", "surface.raised", "surface.sunken", "surface.card", "surface.selected", "surface.selected.strong"];

    /// <summary>The first of the look's border and text colours that reaches <see cref="MinContrast"/> against every surface a box stands on (a selected row included).</summary>
    public static UiColour Outline(ReverieTokens r)
    {
        foreach (var name in new[] { "border.strong", "text.muted", "text.secondary", "text.primary" })
            if (Surfaces.All(s => UiColour.Contrast(r[name], r[s]) >= MinContrast))
                return r[name];
        return r["text.strong"];
    }

    /// <summary>The surfaces an outline is held against, by token name.</summary>
    public static IReadOnlyList<string> SurfaceNames => Surfaces;

    /// <summary>The least contrast a disabled box's outline keeps against the surface behind it: dimmer than an enabled box, never gone.</summary>
    public const double DisabledMinContrast = 2.0;

    /// <summary>The outline of a secondary button, a chip or a colour swatch: the same outline an unticked box has.</summary>
    public static UiColour ButtonOutline(ReverieTokens r) => Outline(r);

    /// <summary>How a disabled, unticked box is drawn: its outline colour at a strength, dashed or solid.</summary>
    public readonly record struct DisabledBox(UiColour Outline, float Strength, bool Dashed);

    /// <summary>
    /// The disabled box's look: the unticked outline, dashed (so it differs by shape and not only by dimness), at the disabled strength
    /// (45 %) or the least stronger one that keeps <see cref="DisabledMinContrast"/> on every surface a box stands on. The tick of a
    /// ticked, disabled box stays at the plain 45 %.
    /// </summary>
    public static DisabledBox Disabled(ReverieTokens r)
    {
        var outline = Outline(r);
        float strength = ControlStates.DisabledStrength;
        while (strength < 1f && !Surfaces.All(s => UiColour.Contrast(Over(outline, strength, r[s]), r[s]) >= DisabledMinContrast + 0.05))
            strength = MathF.Min(1f, strength + 0.05f);
        return new(outline, strength, true);
    }

    /// <summary><paramref name="top"/> (its own alpha times <paramref name="strength"/>) drawn over the opaque <paramref name="under"/>.</summary>
    public static UiColour Over(UiColour top, float strength, UiColour under)
    {
        float a = top.A * strength;
        return new UiColour(top.R * a + under.R * (1 - a), top.G * a + under.G * (1 - a), top.B * a + under.B * (1 - a));
    }
}
