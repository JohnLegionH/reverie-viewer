// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
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
        s.Floaters.Opened += id => { if (id == FloaterId) window.Opened?.Invoke(); };
        return window;
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
        Entry = new SpinBox
        {
            Name = "hover_value", MinValue = HoverHeightRules.Minimum, MaxValue = HoverHeightRules.Maximum, Step = HoverHeightRules.Step,
            AccessibilityName = strings.Get("hover.height"),
        };
        row.AddChild(Entry);
        AddChild(row);

        _status = new Label { Name = "hover_status", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(200, 0), ThemeTypeVariation = ReverieStyle.Muted };
        AddChild(_status);

        Slider.DragStarted += () => _dragging = true;
        Slider.DragEnded += _ => { _dragging = false; Tell(Slider.Value, true); };
        Slider.ValueChanged += v => { if (!_showing) Tell(v, !_dragging); };
        Entry.ValueChanged += v => { if (!_showing) Tell(v, true); };
    }

    private void Tell(double value, bool finished)
    {
        ShowHeight((float)value);
        Changed?.Invoke((float)value, finished);
    }

    /// <summary>Shows a height without telling the owner.</summary>
    public void ShowHeight(float metres)
    {
        _showing = true;
        Slider.Value = metres;
        Entry.Value = metres;
        _showing = false;
    }

    /// <summary>Greys the controls, with the reason, where the region does not take a hover height.</summary>
    public void ShowSupport(bool supported)
    {
        Slider.Editable = supported;
        Entry.Editable = supported;
        Slider.TooltipText = Entry.TooltipText = Entry.GetLineEdit().TooltipText = supported ? "" : _strings.Get("hover.unsupported");
        _status.Text = supported ? "" : _strings.Get("hover.unsupported");
        _status.Visible = !supported;
    }

    public void ShowRefused()
    {
        _status.Text = _strings.Get("hover.refused");
        _status.Visible = true;
    }
}
