// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Shell.Sound;

namespace Viewer.Godot;

/// <summary>
/// Gives every control of the interface its click, as the official viewer's controls have theirs (the rules are
/// <see cref="UiSoundRules.Clicks"/>): it watches every node that joins the tree and connects the ones that click. A button clicks when a
/// press ends inside it; a menu entry when it is chosen; a menu bar title when pressed; a slider once when pressed and once when let go.
/// Text fields, links, scroll bars and the entries of a choice list stay silent, and a
/// keystroke a text field refuses (too long) plays the refused-key sound. A control can be made silent with the metadata
/// <c>ui_sound = "silent"</c>.
/// </summary>
public sealed partial class UiSoundClicks : Node
{
    /// <summary>The metadata that makes a control silent.</summary>
    public const string SilentMeta = "ui_sound";

    private const string Hooked = "ui_sound_hooked";

    private readonly Func<UiSounds?> _sounds;

    public UiSoundClicks(Func<UiSounds?> sounds)
    {
        _sounds = sounds;
        Name = "ui_sound_clicks";
    }

    public override void _EnterTree()
    {
        GetTree().NodeAdded += OnAdded;
        Walk(GetTree().Root);
    }

    public override void _ExitTree() => GetTree().NodeAdded -= OnAdded;

    private void Walk(Node n)
    {
        OnAdded(n);
        foreach (var child in n.GetChildren(includeInternal: true))
            Walk(child);
    }

    /// <summary>What kind of control a node is, for the click rules (null: not one that the rules know).</summary>
    public static UiControlKind? KindOf(Node n)
    {
        if (n.HasMeta(SilentMeta) && n.GetMeta(SilentMeta).AsString() == "silent")
            return UiControlKind.Silent;
        return n switch
        {
            LinkButton => UiControlKind.Link,
            MenuButton or MenuBar => UiControlKind.MenuBarTitle,
            OptionButton => UiControlKind.Choice,
            BaseButton { ToggleMode: true } => UiControlKind.Toggle,
            BaseButton => UiControlKind.Button,
            Slider => UiControlKind.Slider,
            ScrollBar => UiControlKind.ScrollBar,
            LineEdit or TextEdit => UiControlKind.TextField,
            PopupMenu p => p.GetParent() is OptionButton ? UiControlKind.ChoiceListItem : UiControlKind.MenuItem,
            _ => null,
        };
    }

    private void Play(UiSoundSlot slot) => _sounds()?.Play(slot);

    private void OnAdded(Node n)
    {
        if (n.HasMeta(Hooked) || KindOf(n) is not { } kind)
            return;
        n.SetMeta(Hooked, true);
        var (down, up) = UiSoundRules.Clicks(kind);
        switch (n)
        {
            case BaseButton b:
                b.ButtonDown += () => { if (down && !Silent(b)) Play(UiSoundSlot.Click); };
                b.Pressed += () => { if (up && !Silent(b)) Play(UiSoundSlot.ClickRelease); };
                break;
            case Slider s:
                s.GuiInput += e =>
                {
                    if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } m && s.Editable && !Silent(s))
                    {
                        if (m.Pressed && down) Play(UiSoundSlot.Click);
                        else if (!m.Pressed && up) Play(UiSoundSlot.ClickRelease);
                    }
                };
                break;
            case MenuBar bar:
                bar.GuiInput += e =>
                {
                    if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } && down && !Silent(bar))
                        Play(UiSoundSlot.Click);
                };
                break;
            case LineEdit line:
                line.TextChangeRejected += _ => Play(UiSoundSlot.BadKeystroke);
                break;
            case PopupMenu menu when up:
                menu.IndexPressed += _ => Play(UiSoundSlot.ClickRelease);
                break;
        }
    }

    // a control made silent after it joined the tree
    private static bool Silent(Node n) => n.HasMeta(SilentMeta) && n.GetMeta(SilentMeta).AsString() == "silent";
}
