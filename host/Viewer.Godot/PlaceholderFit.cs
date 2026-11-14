// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>Keeps a text field's hint inside the field: a hint wider than the field ends with an ellipsis instead of being cut off.</summary>
public static class PlaceholderFit
{
    public static void Keep(LineEdit edit, string full)
    {
        void Refit()
        {
            var font = edit.GetThemeFont("font");
            int size = edit.GetThemeFontSize("font_size");
            float room = edit.Size.X - edit.GetThemeStylebox("normal").GetMinimumSize().X;
            edit.PlaceholderText = room <= 0 || font is null
                ? full   // not laid out yet
                : Ellipsis.Fit(full, room, s => font.GetStringSize(s, HorizontalAlignment.Left, -1, size).X);
        }
        edit.Resized += Refit;
        edit.ThemeChanged += Refit;
        Refit();
    }
}
