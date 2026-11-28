// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Globalization;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>Adds the Set hover height window to the shell: a floater and its entry in the Avatar menu, on Ctrl+Alt+H, as the official viewer's.</summary>
public static class HoverHeightFeature
{
    public const string FloaterId = "hover_height";

    public static HoverHeightWindow Install(ShellRoot shell)
    {
        var s = shell.Shell;
        var window = new HoverHeightWindow(s.Strings);
        shell.AddFloater(new FloaterSpec(FloaterId, "hover.title", new UiSize(515, 130), new UiSize(360, 120)), window);
        s.Commands.Register("floater.hover_height", () => s.Floaters.Toggle(FloaterId));
        s.Menus.AddEntry("me", new MenuEntry("me.hover", "menu.me.hover", "floater.hover_height", Order: 6, Checked: () => s.Floaters.IsOpen(FloaterId)));
        KeepKeyboard(shell, window);
        s.Floaters.Opened += id => { if (id == FloaterId) window.Opened?.Invoke(); };
        return window;
    }

    /// <summary>Closing the window gives the keyboard back to what had it last outside the window (the chat field, say), when it is in
    /// the window, on the menu bar or nowhere as the window closes. The menu bar's titles take the keyboard when pressed, so they are not
    /// counted. A field the person has left, by Esc or by a press on the world, is not given it back.</summary>
    private static void KeepKeyboard(ShellRoot shell, HoverHeightWindow window)
    {
        var floaters = shell.Shell.Floaters;
        Control? outside = null;   // the last control outside the window and the menu bar that had the keyboard
        bool inWindow = false, onBar = false, giveBack = false;
        bool InWindow(Control c) => shell.Frames[FloaterId].IsAncestorOf(c);
        bool OnBar(Control c) => shell.TopBar.IsAncestorOf(c);
        shell.GetViewport().GuiFocusChanged += c =>
        {
            onBar = OnBar(c);
            if (onBar)
                return;
            inWindow = InWindow(c);
            if (!inWindow)
                outside = c;
        };
        shell.Shell.Focus.TextReleaseRequested += () =>
        {
            if (!inWindow)
                outside = null;
        };
        window.WorldPressed = () => outside = null;
        floaters.Opened += id =>
        {
            if (id == FloaterId && !onBar && shell.GetViewport().GuiGetFocusOwner() is null)
                outside = null;   // nothing had the keyboard as the window opened
        };
        floaters.SetCloseGuard(FloaterId, () =>   // asked while the window is still shown, so where the keyboard is can still be read
        {
            var held = shell.GetViewport().GuiGetFocusOwner();
            giveBack = held is null || InWindow(held) || OnBar(held);
            return true;
        });
        floaters.Closed += id =>
        {
            if (id != FloaterId)
                return;
            window.CheckEntry();   // something typed that is no number is still said when the window is opened again
            if (giveBack && outside is not null && GodotObject.IsInstanceValid(outside) && outside.IsVisibleInTree())
                outside.GrabFocus();
            inWindow = giveBack = false;
        };
    }
}

/// <summary>The window: a slider and a typed value for the height, and, where the region does not take a hover height, both greyed with the
/// reason under them. It only shows and asks; its owner keeps the choice and sends it. It does no work of its own from frame to frame.</summary>
public sealed partial class HoverHeightWindow : VBoxContainer
{
    private readonly StringTable _strings;
    private readonly Label _status;
    private bool _showing;
    private bool _dragging;
    private bool _supported = true, _refused, _notANumber;
    private string? _typed;   // what the person has typed in the value box and not yet entered
    private float _good;   // the height last shown

    internal Action? WorldPressed { get; set; }

    public HSlider Slider { get; }
    public SpinBox Entry { get; }
    public Action? Opened { get; set; }

    /// <summary>The height was changed by the person's hand: while the slider is dragged (not finished), and once the change is finished.</summary>
    public Action<float, bool>? Changed { get; set; }

    public HoverHeightWindow(StringTable strings)
    {
        _strings = strings;
        Name = "hover_height";
        AccessibilityName = strings.Get("hover.title");

        var row = new HBoxContainer { Name = "hover_row" };
        row.AddChild(new Label { Text = strings.Get("hover.height") });
        Slider = new HSlider
        {
            Name = "hover_slider", MinValue = HoverHeightRules.Minimum, MaxValue = HoverHeightRules.Maximum, Step = HoverHeightRules.Step,
            ThemeTypeVariation = ReverieStyle.Slider, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter,
            AccessibilityName = strings.Get("hover.height"),
        };
        row.AddChild(Slider);
        Entry = new HoverValueBox(strings)
        {
            Name = "hover_value", MinValue = HoverHeightRules.Minimum, MaxValue = HoverHeightRules.Maximum, Step = HoverHeightRules.Step,
            AccessibilityName = strings.Get("hover.height"),
        };
        row.AddChild(Entry);
        AddChild(row);

        _status = new Label { Name = "hover_status", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(200, 0), ThemeTypeVariation = ReverieStyle.Muted };
        AddChild(_status);

        // Tab and Shift+Tab go round the window's two controls
        var line = Entry.GetLineEdit();
        Slider.FocusNext = Slider.FocusPrevious = Slider.GetPathTo(line);
        line.FocusNext = line.FocusPrevious = line.GetPathTo(Slider);
        line.TextChanged += text => _typed = text;
        // the engine tells of a typed change one step late, so Enter is judged by the text it submits; the late word is then dropped
        line.TextSubmitted += text => { _typed = text; CheckEntry(); };
        line.FocusExited += CheckEntry;

        Slider.DragStarted += () => _dragging = true;
        Slider.DragEnded += _ => { _dragging = false; Tell(Slider.Value, true); };
        Slider.ValueChanged += v => { if (!_showing) { _typed = null; Tell(v, !_dragging); } };
        Entry.ValueChanged += v => { if (!_showing) Tell(v, true); };
    }

    private bool TypedIsNoNumber => _typed is { } typed && !HoverHeightRules.IsTypedNumber(typed);

    /// <summary>What was typed is entered (Enter, or the box is left): something that is not a number leaves the last good value in the
    /// box, and the window says why. The typed text is kept until the engine's value box has read it too, which it may do later in the frame.</summary>
    internal void CheckEntry()
    {
        if (_typed is null)
            return;
        _notANumber = TypedIsNoNumber;
        if (_notANumber)
            PutBack();
        ShowStatus();
        Callable.From(() =>
        {
            if (TypedIsNoNumber)
                PutBack();
            _typed = null;
        }).CallDeferred();
    }

    private void PutBack()
    {
        ShowHeight(_good);
        Entry.GetLineEdit().Text = Math.Round(_good, 3).ToString("0.0##", CultureInfo.InvariantCulture);   // as the value box writes it
    }

    // A press on something that cannot take the keyboard (the window's body, its title bar, a label) leaves it in the value box, so the box
    // would never be told it was left. With something typed there and not yet entered, any press outside the box leaves it.
    public override void _Input(InputEvent e)
    {
        if (_typed is null || e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right or MouseButton.Middle } press)
            return;
        var line = Entry.GetLineEdit();
        if (line.HasFocus() && !Entry.GetGlobalRect().HasPoint(GetGlobalTransform() * ((InputEventMouseButton)MakeInputLocal(press)).Position))
            line.ReleaseFocus();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true })
            WorldPressed?.Invoke();
    }

    private void Tell(double value, bool finished)
    {
        // the engine's value box reads a leading number out of some entries that are no number ("1.2.3" as 1.2): such a change is not taken
        if (TypedIsNoNumber)
            return;
        _refused = _notANumber = false;
        ShowStatus();
        ShowHeight((float)value);
        Changed?.Invoke((float)value, finished);
    }

    /// <summary>Shows a height without telling the owner.</summary>
    public void ShowHeight(float metres)
    {
        _good = metres;
        _showing = true;
        Slider.Value = metres;
        Entry.Value = metres;
        _showing = false;
    }

    /// <summary>Greys the controls, with the reason, where the region does not take a hover height.</summary>
    public void ShowSupport(bool supported)
    {
        _supported = supported;
        ReverieStyle.SetEditable(Slider, supported);
        Entry.Editable = supported;
        Slider.TooltipText = Entry.GetLineEdit().TooltipText = supported ? "" : _strings.Get("hover.unsupported");
        ((HoverValueBox)Entry).Why = supported ? null : _strings.Get("hover.unsupported");
        ShowStatus();
    }

    /// <summary>Says the grid did not take the height; it stays said, also when the window is opened again, until the height changes.</summary>
    public void ShowRefused()
    {
        _refused = true;
        ShowStatus();
    }

    public void ForgetRefusal()
    {
        _refused = false;
        ShowStatus();
    }

    /// <summary>The message for a typed entry that is no number, for a picture of it.</summary>
    internal void ShowNotANumber()
    {
        _notANumber = true;
        ShowStatus();
    }

    internal void ForgetEntry()
    {
        _typed = null;
        _notANumber = false;
        ShowStatus();
    }

    private void ShowStatus()
    {
        // the answer to what the person just entered comes before the grid's refusal of an earlier height, also one that arrives after it;
        // a changed height ends both and is sent, so the grid refusing it is said anew; the same height entered again ends only the first
        string? key = !_supported ? "hover.unsupported" : _notANumber ? "hover.not_a_number" : _refused ? "hover.refused" : null;
        _status.Text = key is null ? "" : _strings.Get(key);
        _status.Visible = key is not null;
    }
}

/// <summary>The value box: each of its two arrows says what it does when pointed at (the upper raises, the lower lowers); where the control
/// is off, they say why.</summary>
public sealed partial class HoverValueBox(StringTable strings) : SpinBox
{
    public string? Why { get; set; }

    public override string _GetTooltip(Vector2 atPosition) => Why ?? strings.Get(atPosition.Y < Size.Y / 2 ? "hover.arrow_up" : "hover.arrow_down");
}
