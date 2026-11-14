// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>The kinds of control the click rules tell apart.</summary>
public enum UiControlKind { Button, Toggle, Choice, MenuItem, MenuBarTitle, Slider, ChoiceListItem, ScrollBar, TextField, Link, Silent }

/// <summary>
/// Which events play which slot, following the official viewer's behaviour (read from its llui and newview sources; written here
/// in this viewer's own terms). A control clicks when a press ends inside it, as the official default for every control is
/// (sound on mouse up); a menu bar title clicks when pressed; a slider clicks when pressed and when released, once per drag; text,
/// links, scroll bars and the entries of a choice list are silent. Typing in a text field is silent; a refused keystroke is
/// the BadKeystroke slot, played by the engine side.
/// </summary>
public static class UiSoundRules
{
    /// <summary>Whether a control of this kind plays Click when pressed (Down) and ClickRelease when a press ends inside it (Up).</summary>
    public static (bool Down, bool Up) Clicks(UiControlKind kind) => kind switch
    {
        UiControlKind.Button or UiControlKind.Toggle or UiControlKind.Choice or UiControlKind.MenuItem => (false, true),
        UiControlKind.MenuBarTitle => (true, false),
        UiControlKind.Slider => (true, true),
        _ => (false, false),
    };

    /// <summary>The money sound for a new balance: none for the first balance known (the previous one is 0), none unless it changed by
    /// more than the threshold; down when it fell, up when it rose.</summary>
    public static UiSoundSlot? Money(int previous, int now, double threshold)
    {
        if (previous == 0 || Math.Abs((double)previous - now) <= threshold)
            return null;
        return now < previous ? UiSoundSlot.MoneyChangeDown : UiSoundSlot.MoneyChangeUp;
    }

    /// <summary>The pain voice for a health drop of more than the threshold, by the avatar's sex.</summary>
    public static UiSoundSlot? Pain(double previous, double now, double threshold, bool female) =>
        previous > now + threshold ? (female ? UiSoundSlot.HealthReductionF : UiSoundSlot.HealthReductionM) : null;

    /// <summary>The sound of a login-flow dialog: the terms and the critical message are windows in the official viewer; the others are alerts.</summary>
    public static UiSoundSlot LoginDialog(LoginDialogKind kind) =>
        kind is LoginDialogKind.Terms or LoginDialogKind.Critical ? UiSoundSlot.WindowOpen : UiSoundSlot.Alert;
}
