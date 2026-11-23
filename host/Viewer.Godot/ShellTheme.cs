// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The look that is current: the tokens the person's choice gives (plain data, tested without the engine) and the UI scale.
/// Changing it raises <see cref="Changed"/> so the frame builds its engine theme again and anything drawn by hand draws again,
/// with no restart. Main thread only.
/// </summary>
public static class UiTheme
{
    /// <summary>Theme type variations for labels and buttons, defined by <see cref="ShellTheme.Build"/>.</summary>
    public const string ErrorLabel = "ErrorLabel";
    public const string MutedLabel = "MutedLabel";
    public const string AccentLabel = "AccentLabel";
    public const string TitleLabel = "TitleLabel";
    public const string CaptionLabel = "CaptionLabel";
    public const string BrandLabel = "BrandLabel";
    public const string PrimaryButton = "PrimaryButton";
    public const string DangerButton = "DangerButton";
    public const string ToolbarButton = "ToolbarButton";
    public const string ToolbarButtonActive = "ToolbarButtonActive";
    public const string BarPanel = "BarPanel";
    public const string TitleBarPanel = "TitleBarPanel";
    public const string FlatButton = "FlatButton";
    public const string WindowPanel = "WindowPanel";
    public const string WindowPanelActive = "WindowPanelActive";
    public const string BadgePanel = "BadgePanel";
    public const string OverlayText = "OverlayText";
    public const string PasswordField = "PasswordField";

    private static ThemeTokens _tokens = ThemePresets.Get(ThemePresets.Classic);
    private static ThemeSpec? _spec;

    public static ThemeTokens Tokens => _tokens;

    /// <summary>The same values in the shape the chat code reads.</summary>
    public static ThemeSpec Spec => _spec ??= _tokens.ToSpec();

    /// <summary>The UI scale in percent (75 to 400, the official viewer's range).</summary>
    public static int ScalePercent { get; private set; } = 100;

    /// <summary>How opaque the window that has the focus is, and how opaque the others are (see <see cref="FloaterOpacity"/>).</summary>
    public static float ActiveOpacity { get; private set; } = FloaterOpacity.DefaultActive;

    public static float InactiveOpacity { get; private set; } = FloaterOpacity.DefaultInactive;

    public static event Action? Changed;

    /// <summary>Makes the two window opacities current. Nothing is raised when nothing changed.</summary>
    public static void ApplyOpacity(float active, float inactive)
    {
        if (active == ActiveOpacity && inactive == InactiveOpacity)
            return;
        ActiveOpacity = active;
        InactiveOpacity = inactive;
        Changed?.Invoke();
    }

    /// <summary>Makes <paramref name="tokens"/> and <paramref name="scalePercent"/> current. Nothing is raised when nothing changed.</summary>
    public static void Apply(ThemeTokens tokens, int scalePercent)
    {
        scalePercent = Math.Clamp(scalePercent, InterfacePreferences.MinScalePercent, InterfacePreferences.MaxScalePercent);
        if (tokens == _tokens && scalePercent == ScalePercent)
            return;
        _tokens = tokens;
        _spec = null;
        ScalePercent = scalePercent;
        Changed?.Invoke();
    }
}

/// <summary>
/// Turns the tokens into the engine theme for the window frame and everything inside it. Every colour, size and radius it
/// uses is a token; nothing here is a number of its own beyond spacing.
/// </summary>
public static class ShellTheme
{
    public static Color C(UiColour c) => new(c.R, c.G, c.B, c.A);

    /// <summary>A colour that is the person's own data (a wearable's tint), not interface styling: the three weights are red, green and blue, 0 to 1.</summary>
    public static Color DataColour(float r, float g, float b) => new(r, g, b);

    public static Theme Build(ThemeTokens t)
    {
        var theme = new Theme { DefaultFontSize = t.FontBody };
        var regular = Fonts.Regular;
        if (regular is not null)
            theme.DefaultFont = regular;
        var text = C(t.Text);
        var muted = C(t.TextSecondary);
        var accent = C(t.Accent);
        var clear = t.Bar with { A = 0f };

        // floating windows and dialogs
        theme.SetStylebox("panel", "PanelContainer", Box(t, t.Panel, t.Border, 6));
        theme.SetStylebox("panel", "PopupMenu", Box(t, t.Bar.Opaque(), t.Border, 4));
        theme.SetStylebox("panel", "PopupPanel", Box(t, t.Bar.Opaque(), t.Border, 4));
        theme.SetStylebox("hover", "PopupMenu", Box(t, t.Selection, t.Selection, 2, 0));
        theme.SetColor("font_color", "PopupMenu", text);
        theme.SetColor("font_hover_color", "PopupMenu", text);
        theme.SetColor("font_disabled_color", "PopupMenu", C(ControlStates.Faded(t.Text)));
        theme.SetColor("font_separator_color", "PopupMenu", muted);

        // a floating window: the panel colour with no inner margin, so its title bar runs edge to edge
        theme.SetTypeVariation(UiTheme.WindowPanel, "PanelContainer");
        theme.SetStylebox("panel", UiTheme.WindowPanel, Box(t, t.Panel with { A = FloaterOpacity.For(false, UiTheme.ActiveOpacity, UiTheme.InactiveOpacity, t.Panel.A) }, t.Border, 0));
        theme.SetTypeVariation(UiTheme.WindowPanelActive, "PanelContainer");
        theme.SetStylebox("panel", UiTheme.WindowPanelActive, Box(t, t.Panel with { A = FloaterOpacity.For(true, UiTheme.ActiveOpacity, UiTheme.InactiveOpacity, t.Panel.A) }, t.Border, 0));

        // the count on a toolbar button (unread notifications)
        theme.SetTypeVariation(UiTheme.BadgePanel, "PanelContainer");
        theme.SetStylebox("panel", UiTheme.BadgePanel, Box(t, t.DangerFill, t.DangerFill, 2, 9));

        // text over the 3D view: no box, a thin dark outline so it reads on any scene
        theme.SetTypeVariation(UiTheme.OverlayText, "RichTextLabel");
        theme.SetStylebox("normal", UiTheme.OverlayText, new StyleBoxEmpty());
        theme.SetStylebox("focus", UiTheme.OverlayText, new StyleBoxEmpty());
        theme.SetColor("default_color", UiTheme.OverlayText, text);
        theme.SetColor("font_outline_color", UiTheme.OverlayText, C(t.Outline));
        theme.SetConstant("outline_size", UiTheme.OverlayText, 3);
        theme.SetColor("font_outline_color", "Label", C(t.Outline));

        // bars: the menu bar, the nav bar, the toolbars and the title bars
        theme.SetTypeVariation(UiTheme.BarPanel, "PanelContainer");
        theme.SetStylebox("panel", UiTheme.BarPanel, Box(t, t.Bar.Opaque(), t.Border, 1, 0));
        theme.SetTypeVariation(UiTheme.TitleBarPanel, "PanelContainer");
        theme.SetStylebox("panel", UiTheme.TitleBarPanel, Box(t, t.Bar.Opaque(), t.Border, 2, 0));

        // text fields
        var field = Box(t, t.Input, t.Border, 4);
        var focus = Box(t, t.Input, t.Accent, 4);
        foreach (var type in new[] { "LineEdit", "TextEdit" })
        {
            theme.SetStylebox("normal", type, field);
            theme.SetStylebox("focus", type, focus);
            theme.SetStylebox("read_only", type, field);
            theme.SetColor("font_color", type, text);
            // read-only text (a licence, a certificate, a script you cannot change) is the text colour too, not the engine's half-transparent grey
            theme.SetColor(type == "TextEdit" ? "font_readonly_color" : "font_uneditable_color", type, text);
            theme.SetColor("font_placeholder_color", type, muted);
            theme.SetColor("caret_color", type, accent);
            theme.SetColor("selection_color", type, C(t.Selection));
        }
        // the password field: the same faces with room kept on the right for its show button, so the text stops before it and scrolls
        theme.SetTypeVariation(UiTheme.PasswordField, "LineEdit");
        foreach (var (state, box) in new[] { ("normal", field), ("focus", focus), ("read_only", field) })
        {
            var room = (StyleBoxFlat)box.Duplicate();
            room.ContentMarginRight = LoginScreenSpec.PasswordTextRightMargin((int)box.ContentMarginRight);
            theme.SetStylebox(state, UiTheme.PasswordField, room);
        }
        theme.SetStylebox("normal", "RichTextLabel", field);
        theme.SetStylebox("focus", "RichTextLabel", focus);
        theme.SetColor("default_color", "RichTextLabel", text);

        // labels
        theme.SetColor("font_color", "Label", text);
        Variation(theme, UiTheme.ErrorLabel, "Label", "font_color", C(t.Error));
        Variation(theme, UiTheme.MutedLabel, "Label", "font_color", muted);
        Variation(theme, UiTheme.AccentLabel, "Label", "font_color", accent);
        Variation(theme, UiTheme.BrandLabel, "Label", "font_color", accent);
        theme.SetTypeVariation(UiTheme.TitleLabel, "Label");
        theme.SetFontSize("font_size", UiTheme.TitleLabel, t.FontTitle);
        if (Fonts.SemiBold is { } semibold)
            theme.SetFont("font", UiTheme.TitleLabel, semibold);
        theme.SetTypeVariation(UiTheme.CaptionLabel, "Label");
        theme.SetFontSize("font_size", UiTheme.CaptionLabel, t.FontCaption);
        theme.SetColor("font_color", UiTheme.CaptionLabel, muted);

        // buttons: an input-coloured face, the accent on hover and focus; a filled accent button for the main action; disabled, the
        // normal face, border and text at the one disabled strength (ControlStates), in every theme
        var states = ControlStates.Button(t);
        var face = Box(t, states.Normal.Fill, states.Normal.Border, 6);
        var hover = Box(t, states.Hover.Fill, states.Hover.Border, 6);
        var pressed = Box(t, states.Pressed.Fill, states.Pressed.Border, 6);
        var disabled = Box(t, states.Disabled.Fill, states.Disabled.Border, 6);
        foreach (var type in new[] { "Button", "OptionButton", "CheckBox", "MenuButton" })
        {
            theme.SetStylebox("normal", type, face);
            theme.SetStylebox("hover", type, hover);
            theme.SetStylebox("pressed", type, pressed);
            theme.SetStylebox("focus", type, Box(t, clear, t.Accent, 6));
            theme.SetStylebox("disabled", type, disabled);
            theme.SetColor("font_color", type, C(states.Normal.Text));
            theme.SetColor("font_hover_color", type, C(states.Hover.Text));
            theme.SetColor("font_pressed_color", type, C(states.Pressed.Text));
            theme.SetColor("font_focus_color", type, C(states.Normal.Text));
            theme.SetColor("font_disabled_color", type, C(states.Disabled.Text));
            theme.SetColor("icon_disabled_color", type, C(ControlStates.Faded(new UiColour(1f, 1f, 1f))));   // an icon's own colours at the disabled strength
        }
        theme.SetTypeVariation(UiTheme.PrimaryButton, "Button");
        FilledButton(theme, UiTheme.PrimaryButton, t, t.AccentFill, t.AccentFillText);
        theme.SetTypeVariation(UiTheme.DangerButton, "Button");
        FilledButton(theme, UiTheme.DangerButton, t, t.DangerFill, t.AccentFillText);
        theme.SetTypeVariation(UiTheme.FlatButton, "Button");
        var flat = Box(t, clear, clear, 4);
        var flatTight = Box(t, clear, clear, 1);   // the toolbar's buttons: an icon over a label in a 40-unit button
        theme.SetStylebox("normal", UiTheme.FlatButton, flat);
        theme.SetStylebox("hover", UiTheme.FlatButton, Box(t, t.Selection, t.Selection, 4));
        theme.SetStylebox("pressed", UiTheme.FlatButton, Box(t, t.Selection, t.Selection, 4));
        theme.SetStylebox("focus", UiTheme.FlatButton, Box(t, clear, t.Accent, 4));
        theme.SetStylebox("disabled", UiTheme.FlatButton, flat);
        theme.SetTypeVariation(UiTheme.ToolbarButton, "Button");
        theme.SetStylebox("normal", UiTheme.ToolbarButton, flatTight);
        theme.SetStylebox("hover", UiTheme.ToolbarButton, Box(t, t.Selection, t.Selection, 1));
        theme.SetStylebox("pressed", UiTheme.ToolbarButton, Box(t, t.Selection, t.Selection, 1));
        theme.SetStylebox("focus", UiTheme.ToolbarButton, Box(t, clear, t.Accent, 1));
        theme.SetStylebox("disabled", UiTheme.ToolbarButton, flatTight);
        theme.SetConstant("h_separation", UiTheme.ToolbarButton, 0);
        theme.SetFontSize("font_size", UiTheme.ToolbarButton, t.FontCaption);
        theme.SetColor("icon_normal_color", UiTheme.ToolbarButton, accent);
        theme.SetColor("icon_hover_color", UiTheme.ToolbarButton, accent);
        theme.SetColor("icon_pressed_color", UiTheme.ToolbarButton, accent);
        theme.SetTypeVariation(UiTheme.ToolbarButtonActive, UiTheme.ToolbarButton);
        FilledButton(theme, UiTheme.ToolbarButtonActive, t, t.AccentFill, t.AccentFillText);
        theme.SetFontSize("font_size", UiTheme.ToolbarButtonActive, t.FontCaption);
        theme.SetColor("icon_normal_color", UiTheme.ToolbarButtonActive, C(t.AccentFillText));
        theme.SetColor("icon_hover_color", UiTheme.ToolbarButtonActive, C(t.AccentFillText));
        theme.SetColor("icon_pressed_color", UiTheme.ToolbarButtonActive, C(t.AccentFillText));
        theme.SetColor("font_color", "MenuBar", text);
        theme.SetColor("font_hover_color", "MenuBar", accent);
        theme.SetStylebox("normal", "MenuBar", Box(t, t.Bar.Opaque(), t.Bar.Opaque(), 2, 0));
        theme.SetStylebox("hover", "MenuBar", Box(t, t.Selection, t.Selection, 2, 0));
        theme.SetStylebox("pressed", "MenuBar", Box(t, t.Selection, t.Selection, 2, 0));

        // sliders: an input-coloured track filled with the accent up to the grabber
        theme.SetStylebox("slider", "HSlider", Box(t, t.Input, t.Border, 3));
        theme.SetStylebox("grabber_area", "HSlider", Box(t, t.AccentFill, t.AccentFill, 3));
        theme.SetStylebox("grabber_area_highlight", "HSlider", Box(t, t.Accent, t.Accent, 3));

        // lists, trees and tabs
        theme.SetStylebox("panel", "ItemList", field);
        theme.SetStylebox("panel", "Tree", field);
        foreach (var type in new[] { "ItemList", "Tree" })
        {
            theme.SetColor("font_color", type, text);
            theme.SetColor("font_selected_color", type, text);
            theme.SetStylebox("selected", type, Box(t, t.Selection, t.Selection, 2, 0));
            theme.SetStylebox("selected_focus", type, Box(t, t.Selection, t.Accent, 2, 0));
            theme.SetStylebox("cursor", type, Box(t, t.Selection, t.Selection, 2, 0));
        }
        theme.SetColor("font_selected_color", "TabContainer", text);
        theme.SetColor("font_unselected_color", "TabContainer", muted);
        theme.SetColor("font_hovered_color", "TabContainer", text);
        theme.SetStylebox("panel", "TabContainer", Box(t, clear, t.Border, 4, 0));
        theme.SetStylebox("tab_selected", "TabContainer", TabBox(t, t.Accent));
        theme.SetStylebox("tab_hovered", "TabContainer", TabBox(t, t.Border));
        theme.SetStylebox("tab_unselected", "TabContainer", TabBox(t, clear));
        theme.SetStylebox("tab_selected", "TabBar", TabBox(t, t.Accent));
        theme.SetStylebox("tab_hovered", "TabBar", TabBox(t, t.Border));
        theme.SetStylebox("tab_unselected", "TabBar", TabBox(t, clear));
        theme.SetColor("font_selected_color", "TabBar", text);
        theme.SetColor("font_unselected_color", "TabBar", muted);
        theme.SetColor("font_hovered_color", "TabBar", text);
        theme.SetColor("font_color", "CheckBox", text);
        theme.SetColor("font_color", "ProgressBar", text);
        theme.SetStylebox("background", "ProgressBar", field);
        theme.SetStylebox("fill", "ProgressBar", Box(t, t.AccentFill, t.AccentFill, 0));
        // the script editor's code field: the text field's faces, the code font, the line numbers muted, the current line faintly marked
        theme.SetStylebox("normal", "CodeEdit", field);
        theme.SetStylebox("focus", "CodeEdit", focus);
        theme.SetStylebox("read_only", "CodeEdit", field);
        theme.SetColor("font_color", "CodeEdit", text);
        theme.SetColor("font_readonly_color", "CodeEdit", text);
        theme.SetColor("caret_color", "CodeEdit", accent);
        theme.SetColor("selection_color", "CodeEdit", C(t.Selection));
        theme.SetColor("current_line_color", "CodeEdit", C(t.Selection with { A = t.Selection.A / 2f }));
        theme.SetColor("line_number_color", "CodeEdit", muted);
        theme.SetColor("background_color", "CodeEdit", C(t.Input));
        theme.SetColor("search_result_color", "CodeEdit", C(t.Selection));
        if (Fonts.Code is { } code)
            theme.SetFont("font", "CodeEdit", code);
        ReverieStyle.Add(theme, t);   // the Reverie components' variations, from the same tokens
        return theme;
    }

    /// <summary>
    /// The script editor's word colours, each one of the theme's own colours, so a theme change recolours the code with the rest of the
    /// interface. The groups are the official viewer's (indra/llui/llkeywords.cpp getColorGroup: functions, control flow, events, data types,
    /// constants, deprecated words, comments and string literals); which token each group takes is this viewer's choice.
    /// </summary>
    public static Color Syntax(ThemeTokens t, Viewer.Shell.Inventory.LslWordKind kind) => C(kind switch
    {
        Viewer.Shell.Inventory.LslWordKind.Function => t.Accent,
        Viewer.Shell.Inventory.LslWordKind.Control => t.Warning,
        Viewer.Shell.Inventory.LslWordKind.Event => t.ChatOwn,
        Viewer.Shell.Inventory.LslWordKind.Type => t.Success,
        Viewer.Shell.Inventory.LslWordKind.Constant => t.ChatSystem,
        _ => t.Error,
    });

    /// <summary>The colour of comments in the script editor.</summary>
    public static Color SyntaxComment(ThemeTokens t) => C(t.TextSecondary);

    /// <summary>The colour of string literals in the script editor.</summary>
    public static Color SyntaxString(ThemeTokens t) => C(t.ChatObject);

    /// <summary>A panel face: the fill, a border of the theme's width, the theme's corner radius.</summary>
    public static StyleBoxFlat Box(ThemeTokens t, UiColour fill, UiColour border, int margin, int? radius = null)
    {
        var box = new StyleBoxFlat { BgColor = C(fill), BorderColor = C(border) };
        box.SetBorderWidthAll(t.BorderWidth);
        box.SetCornerRadiusAll(radius ?? t.Radius);
        box.SetContentMarginAll(margin);
        return box;
    }

    /// <summary>A tab's face: a line of the given colour along the bottom edge only, as the active-tab line.</summary>
    private static StyleBoxFlat TabBox(ThemeTokens t, UiColour line)
    {
        var box = new StyleBoxFlat { BgColor = C(t.Bar with { A = 0f }), BorderColor = C(line) };
        box.BorderWidthBottom = Math.Max(2, t.BorderWidth);
        box.SetCornerRadiusAll(0);
        box.ContentMarginLeft = 10;
        box.ContentMarginRight = 10;
        box.ContentMarginTop = 4;
        box.ContentMarginBottom = 4;
        return box;
    }

    private static void Variation(Theme theme, string name, string baseType, string colourName, Color colour)
    {
        theme.SetTypeVariation(name, baseType);
        theme.SetColor(colourName, name, colour);
    }

    private static void FilledButton(Theme theme, string name, ThemeTokens t, UiColour fill, UiColour fillText)
    {
        var states = ControlStates.Filled(t, fill, fillText);
        int margin = name == UiTheme.ToolbarButtonActive ? 1 : 6;
        var face = Box(t, states.Normal.Fill, states.Normal.Border, margin);
        var edge = Box(t, states.Hover.Fill, states.Hover.Border, margin);
        theme.SetStylebox("normal", name, face);
        theme.SetStylebox("hover", name, edge);
        theme.SetStylebox("pressed", name, edge);
        theme.SetStylebox("disabled", name, Box(t, states.Disabled.Fill, states.Disabled.Border, margin));
        theme.SetStylebox("focus", name, Box(t, t.Bar with { A = 0f }, t.Accent, 6));
        foreach (var colour in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            theme.SetColor(colour, name, C(states.Normal.Text));
        theme.SetColor("font_disabled_color", name, C(states.Disabled.Text));
    }
}

/// <summary>
/// The interface fonts, made once from the files carried in the shell assembly: Inter (SIL OFL 1.1), with Noto Sans (SIL OFL 1.1)
/// attached for the characters Inter does not have (Devanagari and the rest of Noto Sans's set), so a machine without those system
/// fonts still shows that text; past Noto, the engine falls back to the fonts installed on the system (CJK, Arabic, emoji). The engine
/// sizes a line by the tallest font in a chain, and Noto stands taller than Inter, so Noto is given Inter's ascent and descent at
/// every size: a line keeps Inter's height whichever font draws its letters. This is done once, here, for the one engine theme.
/// </summary>
public static class Fonts
{
    private static FontFile? _inter, _interSemiBold, _noto, _notoBold;
    private static Font? _regular, _semiBold;

    /// <summary>Inter with Noto Sans attached: the theme's font.</summary>
    public static Font? Regular => _regular ??= Chain(InterRegular, Noto);

    /// <summary>Inter SemiBold with Noto Sans Bold attached.</summary>
    public static Font? SemiBold => _semiBold ??= Chain(InterSemiBold, NotoBold);

    /// <summary>Noto Sans, regular (fitted to Inter's line height).</summary>
    public static FontFile? Noto => _noto ??= Fitted(Make("NotoSans-Regular"), InterRegular);

    /// <summary>Noto Sans, bold (fitted to Inter SemiBold's line height).</summary>
    public static FontFile? NotoBold => _notoBold ??= Fitted(Make("NotoSans-Bold"), InterSemiBold);

    /// <summary>Inter regular on its own, without anything attached (the line height the interface keeps).</summary>
    public static FontFile? InterRegular => _inter ??= Make("Inter-Regular");

    public static FontFile? InterSemiBold => _interSemiBold ??= Make("Inter-SemiBold");

    private static Font? _code;

    /// <summary>The script editor's font: the system's fixed-width font (Consolas, Menlo, DejaVu Sans Mono or the system's monospace
    /// family), so the columns the compiler names line up.</summary>
    public static Font Code => _code ??= new SystemFont { FontNames = ["Consolas", "Menlo", "DejaVu Sans Mono", "Liberation Mono", "monospace"], AllowSystemFallback = true };

    /// <summary>The sizes whose metrics are fitted: every size the interface draws text at, at any interface size.</summary>
    public const int FittedMin = 6, FittedMax = 96;

    private static Font? Chain(FontFile? inter, FontFile? noto)
    {
        if (inter is null)
            return null;
        // a wrapper, so Inter's own file stays without a fallback (the height it keeps is measured on it)
        return noto is null ? inter : new FontVariation { BaseFont = inter, Fallbacks = [noto] };
    }

    /// <summary>The fallback takes the main font's ascent and descent at each size, so the chain's line height is the main font's.</summary>
    private static FontFile? Fitted(FontFile? fallback, FontFile? main)
    {
        if (fallback is null || main is null)
            return fallback;
        for (int size = FittedMin; size <= FittedMax; size++)
        {
            fallback.SetCacheAscent(0, size, main.GetAscent(size));
            fallback.SetCacheDescent(0, size, main.GetDescent(size));
        }
        return fallback;
    }

    private static FontFile? Make(string name)
    {
        var bytes = UiAssets.Font(name);
        return bytes.Length == 0 ? null : new FontFile { Data = bytes, AllowSystemFallback = true };
    }
}

/// <summary>
/// The icons, drawn from their svg text at the size asked for (times the interface size, so they stay sharp when the interface
/// is scaled up). Drawn white; the buttons tint them with their theme colours. Main thread only.
/// </summary>
public static class IconCache
{
    private static readonly System.Collections.Generic.Dictionary<(string Name, int Px, int Scale), ImageTexture?> Cache = new();

    public static Texture2D? Get(string name, int px)
    {
        var key = (name, px, UiTheme.ScalePercent);
        if (Cache.TryGetValue(key, out var have))
            return have;
        ImageTexture? texture = null;
        var svg = UiAssets.IconSvg(name, new UiColour(1f, 1f, 1f));
        if (svg is not null)
        {
            var image = new Image();
            if (image.LoadSvgFromString(svg, px * UiTheme.ScalePercent / 100f / 24f) == Error.Ok)
                texture = ImageTexture.CreateFromImage(image);
        }
        Cache[key] = texture;
        return texture;
    }
}
