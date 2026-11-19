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

    private static UiColour H(string hex) => ThemePack.ParseColour(hex)!.Value;

    private static readonly IReadOnlyDictionary<string, UiColour> Dark = new Dictionary<string, UiColour>
    {
        ["surface.base"] = H("#1e2128"), ["surface.sunken"] = H("#1a1d23"), ["surface.raised"] = H("#272b33"),
        ["surface.card"] = H("#23262d"), ["surface.selected"] = H("#343944"), ["surface.selected.strong"] = H("#434956"),
        ["border.default"] = H("#3a404c"), ["border.control"] = H("#434956"), ["border.strong"] = H("#8b93a3"),
        ["text.primary"] = H("#eef0f4"), ["text.strong"] = H("#ffffff"), ["text.secondary"] = H("#d3d8e0"),
        ["text.muted"] = H("#b4bbc7"), ["text.on-accent"] = H("#0f1420"), ["status.ok"] = H("#5fe08a"),
        ["warn.surface"] = H("#2a2618"), ["warn.border"] = H("#6b5a2e"), ["warn.text"] = H("#f6dfa4"),
    };

    // the light variant was specified without warning or status colours: these are its own, darker counterparts of the dark ones
    private static readonly IReadOnlyDictionary<string, UiColour> Light = new Dictionary<string, UiColour>
    {
        ["surface.base"] = H("#f4f5f7"), ["surface.sunken"] = H("#e9ebef"), ["surface.raised"] = H("#ffffff"),
        ["surface.card"] = H("#ffffff"), ["surface.selected"] = H("#dde1e8"), ["surface.selected.strong"] = H("#c9ced8"),
        ["border.default"] = H("#d5d9e1"), ["border.control"] = H("#b9c0cc"), ["border.strong"] = H("#6b7384"),
        ["text.primary"] = H("#1b1e24"), ["text.strong"] = H("#000000"), ["text.secondary"] = H("#3d4450"),
        ["text.muted"] = H("#4f5766"), ["text.on-accent"] = H("#ffffff"), ["status.ok"] = H("#1f8a4c"),
        ["warn.surface"] = H("#fbf1d6"), ["warn.border"] = H("#c9a54a"), ["warn.text"] = H("#5a4200"),
    };

    /// <summary>The accent presets offered beside "Custom...": blue, green, amber and pink.</summary>
    public static IReadOnlyList<(string Key, UiColour Colour)> AccentPresets { get; } =
        [("blue", H("#6aa5ff")), ("green", H("#5fe08a")), ("amber", H("#f2b85c")), ("pink", H("#f08fb0"))];

    /// <summary>The variant's default accent (the light variant's is darker, so accent text and fills keep 4.5:1 on its surfaces: #1a64cf reads 5.1:1 on its bar).</summary>
    public static UiColour DefaultAccent(ReverieVariant variant) => variant == ReverieVariant.Light ? H("#1a64cf") : H("#6aa5ff");

    /// <summary>The tokens of a variant with an accent (null: the variant's default accent).</summary>
    public static ReverieTokens Tokens(ReverieVariant variant, UiColour? accent = null)
    {
        var a = (accent ?? DefaultAccent(variant)).Opaque().Quantized();
        return new ReverieTokens(variant, variant == ReverieVariant.Light ? Light : Dark, a, OnAccentFor(variant, a));
    }

    /// <summary>The text colour for an accent fill: the variant's own text.on-accent when it reads at 4.5:1 or better on the
    /// accent, else black or white, whichever reads better.</summary>
    public static UiColour OnAccentFor(ReverieVariant variant, UiColour accent)
    {
        var own = (variant == ReverieVariant.Light ? Light : Dark)["text.on-accent"];
        if (UiColour.Contrast(own, accent) >= Readability.MinimumRatio)
            return own;
        UiColour black = new(0f, 0f, 0f), white = new(1f, 1f, 1f);
        return UiColour.Contrast(black, accent) >= UiColour.Contrast(white, accent) ? black : white;
    }

    private static readonly string[] TextTokens = ["text.primary", "text.strong", "text.secondary", "text.muted"];

    private static readonly string[] Surfaces =
        ["surface.base", "surface.sunken", "surface.raised", "surface.card", "surface.selected", "surface.selected.strong"];

    /// <summary>Every text-on-surface pair the interface draws, as token names: text, surface. Every text colour on every surface,
    /// warning text on its card, text on an accent fill, and the accent drawn as text on the bar.</summary>
    public static IReadOnlyList<(string Text, string Surface)> TextPairs { get; } =
        [.. TextTokens.SelectMany(t => Surfaces.Select(s => (t, s))), ("warn.text", "warn.surface"), ("text.on-accent", "accent"), ("accent", "surface.base")];

    /// <summary>Type sizes (logical pixels at 100 %): window titles, body, menu titles, status, small, caption, dock names, overlines.</summary>
    public const float TypeTitle = 14, TypeBody = 13.5f, TypeMenu = 13.5f, TypeStatus = 13, TypeSmall = 12.5f, TypeCaption = 12, TypeDock = 11, TypeOverline = 11.5f;

    /// <summary>Corner radii: window, dock, dock button, card, segmented track, control.</summary>
    public const int RadiusWindow = 14, RadiusDock = 20, RadiusDockButton = 14, RadiusCard = 12, RadiusTrack = 10, RadiusControl = 8;

    /// <summary>The floating shadow: offset down, blur, and black at this opacity.</summary>
    public const int ShadowOffsetY = 8, ShadowBlur = 24;
    public const float ShadowOpacity = 0.35f, FloatingOpacity = 0.95f;

    /// <summary>Sizes: the top bar, a menu title, the location field, a top-bar icon button, a window title bar and its buttons,
    /// a list row (in a sidebar), a chip, a button, a segment, the switch and its knob.</summary>
    public const float TopBarHeight = 38, MenuTitleHeight = 28, MenuTitlePadding = 12, LocationHeight = 26, LocationMaxWidth = 440,
        LocationMinWidth = 240, IconButton = 28, IconSmall = 16, TitleBarHeight = 42, TitleButton = 30, ListRow = 32, SidebarRow = 34,
        ChipHeight = 28, ButtonHeight = 36, SegmentHeight = 28, SwitchWidth = 42, SwitchHeight = 24, SwitchKnob = 18;

    /// <summary>The dock: a button, its icon, the dock's padding and gap, a divider and its margin, the distance from the window's edge;
    /// the chat pill and context pill's height and their icon buttons.</summary>
    public const float DockButtonWidth = 62, DockButtonHeight = 54, DockIcon = 26, DockPadding = 6, DockGap = 2, DockDividerHeight = 32,
        DockDividerMargin = 5, DockEdge = 14, PillHeight = 44, PillButton = 32, PillIcon = 18;

    /// <summary>The tokens in the shape the engine theme builder reads; colours the design does not name keep the Classic values.</summary>
    public static ThemeTokens ToThemeTokens(ReverieTokens r)
    {
        var classic = ThemePresets.Get(ThemePresets.Classic);
        return classic with
        {
            Panel = r["surface.base"] with { A = FloatingOpacity }, Bar = r["surface.base"], Input = r["surface.raised"], Border = r["border.control"],
            Text = r["text.primary"], TextSecondary = r["text.secondary"], Accent = r.Accent, AccentFill = r.Accent,
            AccentFillText = r.OnAccent, Warning = r["warn.border"], WarningText = r["warn.text"], Success = r["status.ok"],
            ChatAgent = r["text.primary"], ChatTyping = r["text.muted"], Radius = RadiusControl, BorderWidth = 1,
            FontBody = 13, FontCaption = 12, FontTitle = 14,
        };
    }

    /// <summary>The variant a preset id stands for, or null when it is no Reverie preset.</summary>
    /// <summary>The variant whose text reads on the window panel the person's look has.</summary>
    /// (The bar can be set apart from the panel; windows and their controls sit on the panel, so the panel decides.)
    public static ReverieVariant VariantFor(ThemeTokens t) => t.Panel.Opaque().Luminance > 0.5 ? ReverieVariant.Light : ReverieVariant.Dark;

    public static ReverieVariant? VariantOf(string? id) => id switch { DarkId => ReverieVariant.Dark, LightId => ReverieVariant.Light, _ => null };

    /// <summary>The body text size the Reverie type scale is drawn for (its theme tokens' body size).</summary>
    public const int BaseFontBody = 13;

    /// <summary>Whether a look asks for heavier lines or larger text than the Reverie design draws (the high-contrast preset does:
    /// 2-pixel borders and 16-pixel text). Such a look keeps its own colours, outlines and text size in the Reverie components.</summary>
    public static bool KeepsOwnLook(ThemeTokens t) => t.BorderWidth > 1 || t.FontBody > BaseFontBody;

    /// <summary>
    /// What the Reverie components draw with for the person's look. For the Reverie, classic and power-user looks: the Reverie tokens of
    /// the variant the window panel calls for, with the look's accent, 1-pixel borders and the Reverie type sizes. For a look that keeps
    /// its own (<see cref="KeepsOwnLook"/>): the look's own colours (panel, field, border, text, accent and its selected fill), its border
    /// width and its text size, and every row and segment outlined, the selected one in the accent, as the classic controls draw it.
    /// </summary>
    public static ReverieLook LookFor(ThemeTokens t)
    {
        var variant = VariantFor(t);
        if (!KeepsOwnLook(t))
            return new ReverieLook(Tokens(variant, t.Accent), 1, 1f, false);
        var panel = t.Panel.Opaque();
        var input = t.Input.Opaque();
        var selected = Over(t.Selection, panel);
        var colours = new Dictionary<string, UiColour>
        {
            ["surface.base"] = panel, ["surface.sunken"] = input, ["surface.raised"] = input, ["surface.card"] = panel,
            ["surface.selected"] = selected, ["surface.selected.strong"] = selected,
            ["border.default"] = t.Border, ["border.control"] = t.Border, ["border.strong"] = t.Border,
            ["text.primary"] = t.Text, ["text.strong"] = t.Text, ["text.secondary"] = t.TextSecondary, ["text.muted"] = t.TextSecondary,
            ["text.on-accent"] = t.AccentFillText, ["status.ok"] = t.Success,
            ["warn.surface"] = t.Warning, ["warn.border"] = t.Warning, ["warn.text"] = t.WarningText,
        };
        var tokens = new ReverieTokens(variant, colours, t.Accent.Opaque(), t.AccentFillText);
        return new ReverieLook(tokens, t.BorderWidth, MathF.Max(1f, t.FontBody / (float)BaseFontBody), true);
    }

    /// <summary><paramref name="top"/> drawn over the opaque <paramref name="under"/>.</summary>
    private static UiColour Over(UiColour top, UiColour under) =>
        new(top.R * top.A + under.R * (1 - top.A), top.G * top.A + under.G * (1 - top.A), top.B * top.A + under.B * (1 - top.A));
}

/// <summary>What the Reverie components draw with: the colour tokens, the border width, the factor on every type size, and whether
/// every row and segment carries an outline (the selected one in the accent).</summary>
public sealed record ReverieLook(ReverieTokens Tokens, int BorderWidth, float TypeScale, bool OutlinedRows);
