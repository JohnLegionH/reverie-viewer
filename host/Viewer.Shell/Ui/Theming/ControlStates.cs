// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>One state of a control: the face's fill and border and the text (and icon) colour drawn on it.</summary>
public readonly record struct ControlFace(UiColour Fill, UiColour Border, UiColour Text);

/// <summary>The states of one kind of button: normal, hover, pressed and disabled.</summary>
public sealed record ControlStateSet(ControlFace Normal, ControlFace Hover, ControlFace Pressed, ControlFace Disabled);

/// <summary>
/// The control states every window takes its look from, made from the theme's tokens (plain data, tested without the engine).
/// A disabled control is its normal face, border and text at 45 % strength, in every theme: opacity rather than a colour of its
/// own, so it still reads as disabled in a theme whose secondary text is as bright as its text (high contrast). Both the classic
/// controls and the Reverie components take the same strength from here.
/// </summary>
public static class ControlStates
{
    /// <summary>How strongly a disabled control is drawn: 45 % of its enabled opacity.</summary>
    public const float DisabledStrength = 0.45f;

    /// <summary>The colour at the disabled strength: the same colour, its opacity times <see cref="DisabledStrength"/>.</summary>
    public static UiColour Faded(UiColour c) => c with { A = c.A * DisabledStrength };

    /// <summary>A face drawn disabled: fill, border and text each at the disabled strength.</summary>
    public static ControlFace Disabled(ControlFace normal) => new(Faded(normal.Fill), Faded(normal.Border), Faded(normal.Text));

    /// <summary>An ordinary button, tick box, option button or menu button: an input-coloured face, the accent on hover, the
    /// selection colour pressed.</summary>
    public static ControlStateSet Button(ThemeTokens t)
    {
        var normal = new ControlFace(t.Input, t.Border, t.Text);
        return new(normal, new(t.Bar.Opaque(), t.Accent, t.Text), new(t.Selection, t.Accent, t.Text), Disabled(normal));
    }

    /// <summary>A filled button (the main action, a dangerous action): the fill with its own text colour; the text's edge on hover.</summary>
    public static ControlStateSet Filled(ThemeTokens t, UiColour fill, UiColour fillText)
    {
        var normal = new ControlFace(fill, fill, fillText);
        var edge = new ControlFace(fill, t.Text, fillText);
        return new(normal, edge, edge, Disabled(normal));
    }

}
