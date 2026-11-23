// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The look of the controls every window shares, set once on the theme so a window gets it without any code of its own: the tick box
/// (the plain box and tick of the Sound page, on the base CheckBox type and on the Reverie check variation alike) and the number box
/// (arrows in a column of <see cref="StepperMetrics.ButtonWidth"/> joined to the field, two targets that scale with the interface size).
/// </summary>
public static class SharedControlStyle
{
    private static Color C(UiColour c) => ShellTheme.C(c);

    /// <summary>The tick box on theme type <paramref name="type"/>: the tick and its label with no bar around the row; an unticked box
    /// an outline that holds 3:1 against the surface; a disabled box dashed and dimmed (<see cref="CheckColours.Disabled"/>).</summary>
    public static void ApplyCheck(Theme theme, string type, ReverieTokens r, int fontSize)
    {
        var bare = new StyleBoxEmpty { ContentMarginLeft = 2, ContentMarginRight = 4, ContentMarginTop = 2, ContentMarginBottom = 2 };
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "hover_disabled" })
            theme.SetStylebox(state, type, bare);
        theme.SetStylebox("focus", type, ReverieStyle.FocusRing(r, 4));
        foreach (var colour in new[] { "font_color", "font_pressed_color", "font_focus_color" })
            theme.SetColor(colour, type, C(r["text.primary"]));
        theme.SetColor("font_hover_color", type, C(r["text.strong"]));
        theme.SetColor("font_hover_pressed_color", type, C(r["text.strong"]));
        theme.SetColor("font_disabled_color", type, C(ControlStates.Faded(r["text.primary"])));
        theme.SetConstant("h_separation", type, 6);
        foreach (var (icon, ticked, off) in new[] { ("unchecked", false, false), ("checked", true, false), ("unchecked_disabled", false, true), ("checked_disabled", true, true) })
            theme.SetIcon(icon, type, CheckBoxIcons.Make(r, ticked, off));
        if (fontSize > 0)
            theme.SetFontSize("font_size", type, fontSize);
    }

    /// <summary>The number box on the SpinBox type: a column of two arrows (up above, down below) joined to the field's right edge, each
    /// arrow a raised face with the outline of an unticked box and a triangle in the text colour; hover and pressed in the selection
    /// colours, disabled at 45 %.</summary>
    public static void ApplySpinBox(Theme theme, ReverieTokens r, int borderWidth)
    {
        const string t = "SpinBox";
        int width = (int)MathF.Round(StepperMetrics.ButtonWidth);
        theme.SetConstant("buttons_width", t, width);
        theme.SetConstant("field_and_buttons_separation", t, 0);
        theme.SetConstant("buttons_vertical_separation", t, 0);
        theme.SetConstant("set_min_buttons_width_from_icons", t, 0);
        theme.SetStylebox("field_and_buttons_separator", t, new StyleBoxEmpty());
        theme.SetStylebox("up_down_buttons_separator", t, new StyleBoxEmpty());
        var outline = C(CheckColours.Outline(r));
        var fill = C(r["surface.raised"]);
        var hover = C(r["surface.selected"]);
        var pressed = C(r["surface.selected.strong"]);
        var off = fill with { A = fill.A * ControlStates.DisabledStrength };
        var offOutline = outline with { A = outline.A * ControlStates.DisabledStrength };
        int radius = ReverieTheme.RadiusControl;
        foreach (var (name, up) in new[] { ("up", true), ("down", false) })
        {
            StyleBoxFlat Face(Color bg, Color edge)
            {
                var box = new StyleBoxFlat { BgColor = bg, BorderColor = edge, AntiAliasing = true };
                box.SetBorderWidthAll(borderWidth);
                if (up) box.BorderWidthBottom = 0; else box.BorderWidthTop = 0;
                if (up) box.CornerRadiusTopRight = radius; else box.CornerRadiusBottomRight = radius;
                return box;
            }
            theme.SetStylebox(name + "_background", t, Face(fill, outline));
            theme.SetStylebox(name + "_background_hovered", t, Face(hover, outline));
            theme.SetStylebox(name + "_background_pressed", t, Face(pressed, outline));
            theme.SetStylebox(name + "_background_disabled", t, Face(off, offOutline));
            var arrow = Arrow(C(r["text.primary"]), up);
            theme.SetIcon(name, t, arrow);
            theme.SetIcon(name + "_hover", t, arrow);
            theme.SetIcon(name + "_pressed", t, arrow);
            theme.SetIcon(name + "_disabled", t, Arrow(C(ControlStates.Faded(r["text.primary"])), up));
            foreach (var state in new[] { "_icon_modulate", "_hover_icon_modulate", "_pressed_icon_modulate", "_disabled_icon_modulate" })
                theme.SetColor(name + state, t, Colors.White);   // the arrows carry their own colours
        }
    }

    /// <summary>A filled triangle, 10 wide and 6 high, pointing up or down.</summary>
    private static ImageTexture Arrow(Color colour, bool up)
    {
        const int w = 10, h = 6;
        var image = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
        {
            int row = up ? y : h - 1 - y;                       // 0 at the tip
            float half = (row + 0.5f) * (w / 2f) / h;           // half the width of the triangle on this row
            for (int x = 0; x < w; x++)
            {
                float cover = Math.Clamp(half - MathF.Abs(x + 0.5f - w / 2f) + 0.5f, 0f, 1f);
                if (cover > 0)
                    image.SetPixel(x, y, new Color(colour.R, colour.G, colour.B, colour.A * cover));
            }
        }
        return ImageTexture.CreateFromImage(image);
    }
}
