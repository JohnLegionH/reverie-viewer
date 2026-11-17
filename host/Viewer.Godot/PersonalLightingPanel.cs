// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The Personal Lighting window, opened from World > Environment as in the official viewer. Its controls and ranges follow the official window
/// (floater_adjust_environment.xml): five colour swatches (Ambient, Blue Horizon, Blue Density, Sun Color, Cloud Color), Haze Horizon, Haze Density,
/// Cloud Coverage, Cloud Scale, Brightness (the scene gamma) and the glow's size and focus. Changes apply as they are made, for this viewer only;
/// Reset puts the region's environment back. The cloud and water images, reflection-probe ambiance, the sun's and moon's places and sizes and the star
/// brightness of the official window are not here.
/// </summary>
public static class PersonalLightingFeature
{
    public const string FloaterId = "personal_lighting";

    public static PersonalLightingPanel Install(ShellRoot shell, PersonalLighting model, Func<SkyFrame?> frame)
    {
        var s = shell.Shell;
        var panel = new PersonalLightingPanel(s.Strings, model, frame);
        var scroll = new ScrollContainer { Name = "personal_scroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(panel);
        shell.AddFloater(new FloaterSpec(FloaterId, "personal.title", new UiSize(420, 560), new UiSize(320, 280)), scroll);
        // what the window shows is read again each time it opens
        bool wasOpen = false;
        s.Floaters.Changed += () =>
        {
            bool open = System.Linq.Enumerable.Any(s.Floaters.OpenFloaters, f => f.Id == FloaterId);
            if (open && !wasOpen) panel.Refresh();
            wasOpen = open;
        };
        return panel;
    }
}

public sealed partial class PersonalLightingPanel : VBoxContainer
{
    private static readonly (PersonalField Field, string Key)[] Swatches =
    [
        (PersonalField.Ambient, "personal.ambient"), (PersonalField.BlueHorizon, "personal.blue_horizon"), (PersonalField.BlueDensity, "personal.blue_density"),
        (PersonalField.SunColor, "personal.sun_color"), (PersonalField.CloudColor, "personal.cloud_color"),
    ];

    private static readonly (PersonalField Field, string Key)[] Sliders =
    [
        (PersonalField.HazeHorizon, "personal.haze_horizon"), (PersonalField.HazeDensity, "personal.haze_density"), (PersonalField.CloudCoverage, "personal.cloud_coverage"),
        (PersonalField.CloudScale, "personal.cloud_scale"), (PersonalField.SceneGamma, "personal.gamma"), (PersonalField.GlowSize, "personal.glow_size"),
        (PersonalField.GlowFocus, "personal.glow_focus"),
    ];

    private readonly PersonalLighting _model;
    private readonly Func<SkyFrame?> _frame;
    private readonly Dictionary<PersonalField, ColourButton> _pickers = new();
    private readonly Dictionary<PersonalField, (HSlider Slider, Label Value)> _sliders = new();
    private bool _syncing;

    public Button ResetButton { get; }

    public IReadOnlyDictionary<PersonalField, ColourButton> Pickers => _pickers;

    public IReadOnlyDictionary<PersonalField, (HSlider Slider, Label Value)> SliderRows => _sliders;

    public PersonalLightingPanel(StringTable strings, PersonalLighting model, Func<SkyFrame?> frame)
    {
        _model = model;
        _frame = frame;
        Name = "personal_lighting";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 6);
        foreach (var (field, key) in Swatches)
        {
            var row = new HBoxContainer { Name = "row_" + field };
            var label = new Label { Name = "label_" + field, Text = strings.Get(key), CustomMinimumSize = new Vector2(130, 0) };
            label.AccessibilityName = label.Text;
            var picker = new ColourButton { Name = "swatch_" + field, CustomMinimumSize = new Vector2(64, 28), TooltipText = strings.Get("colour.swatch.tip") };
            picker.AccessibilityName = label.Text;
            var f = field;
            picker.ColorChanged += c => { if (!_syncing) _model.SetColour(f, new System.Numerics.Vector3(c.R, c.G, c.B)); };
            row.AddChild(label);
            row.AddChild(picker);
            AddChild(row);
            _pickers[field] = picker;
        }
        foreach (var (field, key) in Sliders)
        {
            var (min, max, step) = PersonalLighting.Range(field);
            var box = new VBoxContainer { Name = "row_" + field };
            var label = new Label { Name = "label_" + field, Text = strings.Get(key) };
            label.AccessibilityName = label.Text;
            var line = new HBoxContainer();
            var slider = new HSlider { Name = "slider_" + field, MinValue = min, MaxValue = max, Step = step, CustomMinimumSize = new Vector2(220, 24), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            slider.AccessibilityName = label.Text;
            var value = new Label { Name = "value_" + field, CustomMinimumSize = new Vector2(52, 0), HorizontalAlignment = HorizontalAlignment.Right };
            value.AccessibilityName = label.Text;
            var f = field;
            slider.ValueChanged += v => { value.Text = v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); if (!_syncing) _model.SetValue(f, v); };
            line.AddChild(slider);
            line.AddChild(value);
            box.AddChild(label);
            box.AddChild(line);
            AddChild(box);
            _sliders[field] = (slider, value);
        }
        ResetButton = new Button { Name = "reset", Text = strings.Get("personal.reset") };
        ResetButton.AccessibilityName = ResetButton.Text;
        ResetButton.Pressed += () => { _model.Reset(); Refresh(); };
        AddChild(ResetButton);
        Refresh();
    }

    /// <summary>Shows what is in force: what the window has set, else what the sky frame in use has.</summary>
    public void Refresh()
    {
        var frame = _frame() ?? RegionEnvironment.Default.SkyAt(0.5);
        _syncing = true;
        try
        {
            foreach (var (field, picker) in _pickers)
            {
                var c = _model.Colour(field, frame);
                picker.Color = Presentation.SwatchColour.Of(c);
            }
            foreach (var (field, row) in _sliders)
            {
                row.Slider.Value = _model.Value(field, frame);
                row.Value.Text = row.Slider.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        finally { _syncing = false; }
    }
}
