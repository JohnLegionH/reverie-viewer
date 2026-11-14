// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Build;

namespace Viewer.Godot;

/// <summary>
/// Grid Options (Ctrl+Shift+B): the ruler mode (World, Local, Reference), the grid unit, how far the drawn grid reaches, sub-units and the grid's
/// opacity, after the official build options floater (read as behaviour). It shows what <see cref="EditController"/> holds and hands every
/// change to it. Standard controls styled through the Theme alone. Main thread only.
/// </summary>
public partial class GridPanel : VBoxContainer
{
    public const string FloaterId = "grid_options";

    private readonly Func<EditController?> _edit;
    private readonly OptionButton _mode;
    private readonly SpinBox _unit, _extent;
    private readonly CheckBox _subUnits;
    private readonly HSlider _opacity;
    private bool _filling;

    public GridPanel(StringTable strings, Func<EditController?> edit)
    {
        _edit = edit;
        Name = "grid_panel";
        Label Caption(string key) { var l = new Label { Text = strings.Get(key) }; AddChild(l); return l; }

        Caption("grid.mode");
        _mode = new OptionButton { Name = "mode" };
        _mode.AddItem(strings.Get("grid.mode.world"), (int)GridMode.World);
        _mode.AddItem(strings.Get("grid.mode.local"), (int)GridMode.Local);
        _mode.AddItem(strings.Get("grid.mode.reference"), (int)GridMode.Reference);
        _mode.AccessibilityName = strings.Get("grid.mode");
        _mode.ItemSelected += i => { if (!_filling && _edit() is { } e) e.Grid = (GridMode)_mode.GetItemId((int)i); };
        AddChild(_mode);

        Caption("grid.unit");
        _unit = new SpinBox { Name = "unit", MinValue = 0.01, MaxValue = 100, Step = 0.01, Value = 0.5 };
        _unit.GetLineEdit().AccessibilityName = strings.Get("grid.unit");
        _unit.ValueChanged += v => { if (!_filling && _edit() is { } e) e.GridUnit = (float)v; };
        AddChild(_unit);

        Caption("grid.extent");
        _extent = new SpinBox { Name = "extent", MinValue = 1, MaxValue = 512, Step = 0.5, Value = 12 };
        _extent.GetLineEdit().AccessibilityName = strings.Get("grid.extent");
        _extent.ValueChanged += v => { if (!_filling && _edit() is { } e) e.GridExtent = (float)v; };
        AddChild(_extent);

        _subUnits = new CheckBox { Name = "sub_units", Text = strings.Get("grid.sub_units") };
        _subUnits.AccessibilityName = _subUnits.Text;
        _subUnits.Toggled += on => { if (!_filling && _edit() is { } e) e.GridSubUnits = on; };
        AddChild(_subUnits);

        Caption("grid.opacity");
        _opacity = new HSlider { Name = "opacity", MinValue = 0.1, MaxValue = 1, Step = 0.05, Value = 0.7 };
        _opacity.AccessibilityName = strings.Get("grid.opacity");
        _opacity.ValueChanged += v => { if (!_filling && _edit() is { } e) e.GridOpacity = (float)v; };
        AddChild(_opacity);
    }

    /// <summary>Shows the controller's grid settings.</summary>
    public void Refresh()
    {
        if (_edit() is not { } e)
            return;
        _filling = true;
        try
        {
            _mode.Select(_mode.GetItemIndex((int)e.Grid));
            _unit.SetValueNoSignal(e.GridUnit);
            _extent.SetValueNoSignal(e.GridExtent);
            _subUnits.SetPressedNoSignal(e.GridSubUnits);
            _opacity.SetValueNoSignal(e.GridOpacity);
        }
        finally { _filling = false; }
    }
}
