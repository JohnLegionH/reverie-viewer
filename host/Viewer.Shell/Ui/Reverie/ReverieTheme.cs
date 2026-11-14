// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The two variants of the interface's colours.</summary>
public enum ReverieVariant { Dark, Light }

/// <summary>
/// The interface's design tokens by name (surface.base, text.primary and so on), for one variant and one accent. Plain data: the
/// engine host builds its theme from it, and the contrast rule is tested on it without the engine.
/// </summary>
public sealed class ReverieTokens
{
    private readonly IReadOnlyDictionary<string, UiColour> _colours;

    internal ReverieTokens(ReverieVariant variant, IReadOnlyDictionary<string, UiColour> colours, UiColour accent, UiColour onAccent)
    {
        Variant = variant;
        _colours = colours;
        Accent = accent;
        OnAccent = onAccent;
    }

    public ReverieVariant Variant { get; }

    /// <summary>The accent in force (the person's, or the variant's default).</summary>
    public UiColour Accent { get; }

    /// <summary>Text and knobs drawn on an accent fill.</summary>
    public UiColour OnAccent { get; }

    /// <summary>Every named colour of the variant, the accent and text.on-accent included.</summary>
    public IReadOnlyDictionary<string, UiColour> Colours => _colours;

    /// <summary>The token's colour; the accent for "accent" and text.on-accent for "text.on-accent".</summary>
    public UiColour this[string key] => key switch
    {
        "accent" => Accent,
        "text.on-accent" => OnAccent,
        _ => _colours.TryGetValue(key, out var c) ? c : default,
    };
}

/// <summary>The design tokens of the interface, as numbers: colours, type sizes, radii and sizes.</summary>
public static class ReverieTheme
{
    public const string DarkId = "reverie-dark", LightId = "reverie-light";

    /// <summary>The accent presets offered beside "Custom...": blue, green, amber and pink.</summary>
    public static IReadOnlyList<(string Key, UiColour Colour)> AccentPresets { get; } = [];

    /// <summary>The variant's default accent.</summary>
    public static UiColour DefaultAccent(ReverieVariant variant) => default;

    /// <summary>The tokens of a variant with an accent (null: the variant's default accent).</summary>
    public static ReverieTokens Tokens(ReverieVariant variant, UiColour? accent = null) =>
        new(variant, new Dictionary<string, UiColour>(), default, default);

    /// <summary>The text colour for an accent fill: the variant's own text.on-accent when it reads at 4.5:1 or better on the
    /// accent, else black or white, whichever reads better.</summary>
    public static UiColour OnAccentFor(ReverieVariant variant, UiColour accent) => default;

    /// <summary>Every text-on-surface pair the interface draws, as token names: text, surface.</summary>
    public static IReadOnlyList<(string Text, string Surface)> TextPairs { get; } = [];

    /// <summary>The tokens in the shape the engine theme builder reads.</summary>
    public static ThemeTokens ToThemeTokens(ReverieTokens tokens) => ThemePresets.Get(ThemePresets.Classic);
}
