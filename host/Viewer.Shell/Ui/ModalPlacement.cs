// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// Where the modal alert goes. The official viewer keeps alerts centred on the window (llscreenchannel.cpp, "Keep notifications and
/// alerts centered"); here the floating dock lies over the bottom of the world view, so the alert is centred in the part of the window
/// between the bars and the dock. When it does not fit there it moves up over what it must (never past the window's top), and its message
/// gets a scroll area so the whole dialog stays inside the window. Plain numbers in interface units, tested without the engine.
/// </summary>
public static class ModalPlacement
{
    /// <summary>The space kept between the dialog and the window's edge, the bars and the dock.</summary>
    public const float Margin = 8;

    /// <summary>The dialog's rectangle for a dialog of <paramref name="size"/> in a window of <paramref name="window"/>, with the world view
    /// between the bars at <paramref name="main"/> and the dock at <paramref name="dock"/> (null: no dock shown).</summary>
    public static UiRect Place(UiSize window, UiRect main, UiRect? dock, UiSize size)
    {
        float w = Math.Min(size.W, Math.Max(0, window.W - 2 * Margin)), h = Math.Min(size.H, MaxHeight(window));
        float x = MathF.Round((window.W - w) / 2);
        // the space for it: the world view between the bars, above the dock when the dock is shown
        float top = main.Y + Margin, bottom = main.Bottom - Margin;
        if (dock is { } d && d.H > 0 && d.Y < main.Bottom)
            bottom = Math.Min(bottom, d.Y - Margin);
        float y;
        if (h <= bottom - top)
            y = dock is null ? main.Y + (main.H - h) / 2 : top + (bottom - top - h) / 2;
        else
            y = bottom - h;   // over the bars rather than the dock where it must, as low as fits above the dock
        y = MathF.Round(Math.Clamp(y, Margin, Math.Max(Margin, window.H - Margin - h)));
        return new UiRect(x, y, w, h);
    }

    /// <summary>Whether the dialog cannot lie between the bars and the dock: the dock is then hidden while the alert shows (the dim covers it and
    /// it takes no clicks then anyway), and the dialog is placed as if there were no dock.</summary>
    public static bool HidesDock(UiSize window, UiRect main, UiRect? dock, UiSize size)
    {
        if (dock is not { } d || d.H <= 0)
            return false;
        float top = main.Y + Margin, bottom = Math.Min(main.Bottom - Margin, d.Y - Margin);
        return Math.Min(size.H, MaxHeight(window)) > bottom - top;
    }

    /// <summary>The tallest the dialog may be: the window's height less a margin above and below.</summary>
    public static float MaxHeight(UiSize window) => Math.Max(0, window.H - 2 * Margin);

    /// <summary>The widest the message may wrap at: <paramref name="wanted"/> (the official alert's 400), or less in a narrow window, where
    /// <paramref name="chrome"/> is the width the dialog adds around its message.</summary>
    public static float TextWidth(UiSize window, float wanted, float chrome) => Math.Max(Math.Min(120, wanted), Math.Min(wanted, window.W - 2 * Margin - chrome));
}
