// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// Every colour, size and corner radius the interface draws with, by name. The engine host turns one of these into its
/// theme and builds it again whenever the person changes a value; no interface code carries a colour, font size or radius of
/// its own (a source scan checks that). Plain data, so it is tested without the engine.
/// </summary>
public sealed record ThemeTokens(
    UiColour Panel, UiColour Bar, UiColour Input, UiColour Border, UiColour Text, UiColour TextSecondary,
    UiColour Accent, UiColour AccentFill, UiColour AccentFillText, UiColour DangerFill, UiColour Warning, UiColour WarningText,
    UiColour Success, UiColour ChatOwn, UiColour ChatAgent, UiColour ChatObject, UiColour ChatSystem, UiColour ChatTyping,
    UiColour Error, UiColour Outline, UiColour Scrim, int Radius, int BorderWidth, int FontBody, int FontCaption, int FontTitle)
{
    /// <summary>The named colours in the order a theme file lists them: key, value.</summary>
    public IReadOnlyList<(string Key, UiColour Colour)> Colours =>
    [
        ("panel", Panel), ("bar", Bar), ("input", Input), ("border", Border), ("text", Text), ("textSecondary", TextSecondary),
        ("accent", Accent), ("accentFill", AccentFill), ("accentFillText", AccentFillText), ("dangerFill", DangerFill),
        ("warning", Warning), ("warningText", WarningText), ("success", Success), ("chatOwn", ChatOwn), ("chatAgent", ChatAgent),
        ("chatObject", ChatObject), ("chatSystem", ChatSystem), ("chatTyping", ChatTyping), ("error", Error), ("outline", Outline), ("scrim", Scrim),
    ];

    /// <summary>The accent drawn faintly, for a selected row (about a fifth of the accent over what is behind it).</summary>
    public UiColour Selection => Accent with { A = 0.22f };

    /// <summary>The tokens the older chat code reads, in its own shape.</summary>
    public ThemeSpec ToSpec() => new(Panel, Border, Text, TextSecondary, Accent, Input, ChatOwn, ChatAgent, ChatObject, ChatSystem,
        ChatTyping, Error, FontBody);
}
