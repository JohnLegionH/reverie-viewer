// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>A colour as four 0-1 channels (no engine type here).</summary>
public readonly record struct UiColour(float R, float G, float B, float A = 1f)
{
    /// <summary>Relative luminance (WCAG 2), for the contrast rule.</summary>
    public double Luminance => 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);

    private static double Linear(float c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>The WCAG 2 contrast ratio between two colours (1 to 21).</summary>
    public static double Contrast(UiColour a, UiColour b)
    {
        double la = a.Luminance, lb = b.Luminance;
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static int Byte(float c) => (int)Math.Round(Math.Clamp(c, 0f, 1f) * 255f);

    /// <summary>"#rrggbb", for rich text.</summary>
    public string Hex => $"#{Byte(R):x2}{Byte(G):x2}{Byte(B):x2}";

    /// <summary>Each channel kept to a whole 0-255 step, as a colour in a theme file is.</summary>
    public UiColour Quantized() => new(Byte(R) / 255f, Byte(G) / 255f, Byte(B) / 255f, Byte(A) / 255f);

    /// <summary>The same colour with full opacity.</summary>
    public UiColour Opaque() => this with { A = 1f };

    /// <summary>Each channel times <paramref name="factor"/>, not rounded.</summary>
    public UiColour Dimmed(float factor) => new(R * factor, G * factor, B * factor, A);

    /// <summary>Each channel times <paramref name="factor"/>, kept to whole 0-255 steps (rounded half up).</summary>
    public UiColour Scaled(double factor)
    {
        float Step(float c) => (float)(Math.Floor(Byte(c) * factor + 0.5) / 255.0);
        return new UiColour(Step(R), Step(G), Step(B), A);
    }
}

/// <summary>Colours that do not follow the theme: name tags are light text on a dark tag in every theme, so a light theme cannot
/// turn them unreadable.</summary>
public static class TagColours
{
    public static readonly UiColour Text = new(1f, 1f, 1f);
    public static readonly UiColour Friend = new(0.447f, 0.784f, 0.663f);
}
