// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>A key with its modifier keys, as a menu entry's shortcut names it ("control|shift|L" is Ctrl+Shift+L).</summary>
public sealed record KeyChord(string Key, bool Control = false, bool Shift = false, bool Alt = false)
{
    /// <summary>Whether Ctrl or Alt is held: such a chord reaches the menus even while a text field holds the keyboard.</summary>
    public bool Modified => Control || Alt;

    /// <summary>Reads a shortcut as the official menus write it; null when it is empty or names a key that is not a letter or a digit.</summary>
    public static KeyChord? Parse(string? shortcut)
    {
        if (string.IsNullOrEmpty(shortcut))
            return null;
        bool control = false, shift = false, alt = false;
        string? key = null;
        foreach (var part in shortcut.Split('|'))
        {
            switch (part.ToLowerInvariant())
            {
                case "control": control = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case { Length: 1 } c when char.IsAsciiLetterOrDigit(c[0]) && key is null: key = c.ToUpperInvariant(); break;
                default: return null;
            }
        }
        return key is null ? null : new KeyChord(key, control, shift, alt);
    }
}

/// <summary>
/// The menus' keyboard shortcuts, taken from the menus themselves when the key is pressed, so every shortcut a menu shows works from the
/// start, whether or not its menu has ever been opened, and an entry's enabled state is read at that moment.
/// The rules follow the official viewer's key handling (llviewerwindow.cpp handleKey, read as behaviour): a chord with Ctrl or Alt goes to
/// the menus before the control that holds the keyboard, so it works while typing; a chord without them is typed into a text field that
/// holds the keyboard and reaches the menus only when none does; a disabled entry does nothing.
/// </summary>
public static class MenuShortcuts
{
    /// <summary>Every entry of the menus that shows a shortcut the keyboard can give, with that shortcut, in menu order.</summary>
    public static IReadOnlyList<(TopMenuEntryView Entry, KeyChord Chord)> All(IReadOnlyList<TopMenuView> menus) =>
        Flat(menus.SelectMany(m => m.Entries))
            .Select(e => (Entry: e, Chord: KeyChord.Parse(e.Shortcut)))
            .Where(x => x.Chord is not null)
            .Select(x => (x.Entry, x.Chord!))
            .ToArray();

    /// <summary>Whether a text field that holds the keyboard takes this chord itself (its own Undo and Redo) before the menus:
    /// the official viewer gives Undo and Redo to whatever handles the edit menu, a line editor while it has the keyboard and the selection
    /// otherwise (llviewermenu.cpp Edit.Undo through gEditMenuHandler).</summary>
    public static bool TextFieldFirst(KeyChord chord) => chord.Control && !chord.Shift && !chord.Alt && (chord.Key is "Z" or "Y");

    /// <summary>The entry a chord runs now, or null: none has it, it is disabled, or a text field takes it.</summary>
    public static TopMenuEntryView? Route(IReadOnlyList<TopMenuView> menus, KeyChord chord, bool textEntry)
    {
        if (textEntry && (!chord.Modified || TextFieldFirst(chord)))
            return null;   // typed into the field, or the field's own undo and redo
        foreach (var e in Flat(menus.SelectMany(m => m.Entries)))
            if (e.CommandId is not null && KeyChord.Parse(e.Shortcut) == chord)
                return e.Enabled ? e : null;
        return null;
    }

    private static IEnumerable<TopMenuEntryView> Flat(IEnumerable<TopMenuEntryView> entries) =>
        entries.SelectMany(e => e.Children is { } kids ? Flat(kids).Prepend(e) : [e]);
}
