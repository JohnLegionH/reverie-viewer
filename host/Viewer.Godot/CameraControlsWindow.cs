// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>Adds the Camera controls window to the shell: a floater and its entry in the Avatar menu, on Ctrl+K, as the official viewer's.</summary>
public static class CameraControlsFeature
{
    public const string FloaterId = "camera_controls";

    public static CameraControlsWindow Install(ShellRoot shell, SettingsService settings, Action<CameraPreset> apply, Func<(System.Numerics.Vector3 Offset, System.Numerics.Vector3 Focus)> current)
    {
        var s = shell.Shell;
        var window = new CameraControlsWindow(s.Strings, settings, apply, current);
        shell.AddFloater(new FloaterSpec(FloaterId, "camera.title", new UiSize(360, 240), new UiSize(320, 220)), window);
        s.Commands.Register("floater.camera_controls", () => s.Floaters.Toggle(FloaterId));
        s.Menus.AddEntry("me", new MenuEntry("me.camera", "menu.me.camera", "floater.camera_controls", Order: 6, Checked: () => s.Floaters.IsOpen(FloaterId)));
        return window;
    }
}

/// <summary>
/// The Camera controls window: the front, side and rear views, the list of views to use, and saving the view on screen under a name or
/// removing a saved one. The picked view and the saved ones are kept in the settings. It does no work from frame to frame.
/// </summary>
public sealed partial class CameraControlsWindow : VBoxContainer
{
    /// <summary>The settings keys: the saved views, and the name of the picked one.</summary>
    public const string SavedKey = "camera_presets", PickedKey = "camera_preset";

    private readonly StringTable _strings;
    private readonly SettingsService _settings;
    private readonly Action<CameraPreset> _apply;
    private readonly Func<(System.Numerics.Vector3 Offset, System.Numerics.Vector3 Focus)> _current;
    private readonly List<CameraPreset> _choices = [];
    private readonly Label _status;

    /// <summary>Whether the view on screen is mouselook, which is not a view that can be saved.</summary>
    public Func<bool>? InMouseLook { get; set; }

    public CameraPresets Presets { get; }
    public OptionButton Use { get; }
    public LineEdit PresetName { get; }
    public Button SaveButton { get; }
    public Button DeleteButton { get; }

    public CameraControlsWindow(StringTable strings, SettingsService settings, Action<CameraPreset> apply,
        Func<(System.Numerics.Vector3 Offset, System.Numerics.Vector3 Focus)> current)
    {
        _strings = strings;
        _settings = settings;
        _apply = apply;
        _current = current;
        Presets = CameraPresets.FromText(settings.GetText(SavedKey));
        Name = "camera_controls";
        AccessibilityName = strings.Get("camera.title");

        var views = new HBoxContainer { Name = "camera_views" };
        foreach (var view in CameraPresets.BuiltIn)
        {
            var button = new RvButton(ViewName(view)) { Name = "camera_" + view.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            button.Pressed += () => Pick(view);
            views.AddChild(button);
        }
        AddChild(views);

        var useRow = new HBoxContainer { Name = "camera_use_row" };
        var useLabel = new Label { Text = strings.Get("camera.use") };
        useRow.AddChild(useLabel);
        Use = new OptionButton { Name = "camera_use", SizeFlagsHorizontal = SizeFlags.ExpandFill, AccessibilityName = useLabel.Text };
        Use.ItemSelected += i => { if (i >= 0 && i < _choices.Count) Pick(_choices[(int)i]); };
        useRow.AddChild(Use);
        DeleteButton = new RvButton(strings.Get("camera.delete")) { Name = "camera_delete" };
        DeleteButton.Pressed += DeletePicked;
        useRow.AddChild(DeleteButton);
        AddChild(useRow);

        var saveRow = new HBoxContainer { Name = "camera_save_row" };
        PresetName = new LineEdit
        {
            Name = "camera_name", PlaceholderText = strings.Get("camera.name"), AccessibilityName = strings.Get("camera.name"),
            MaxLength = CameraPresets.NameLimit, SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        PresetName.TextSubmitted += _ => SaveCurrent();
        saveRow.AddChild(PresetName);
        SaveButton = new RvButton(strings.Get("camera.save")) { Name = "camera_save" };
        SaveButton.Pressed += SaveCurrent;
        saveRow.AddChild(SaveButton);
        AddChild(saveRow);

        _status = new Label { Name = "camera_status", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(200, 0), ThemeTypeVariation = ReverieStyle.Muted };
        AddChild(_status);
        Fill();
    }

    /// <summary>The view picked last (the rear view until another is picked, and when the picked one is gone).</summary>
    public CameraPreset Picked
    {
        get
        {
            string name = _settings.GetText(PickedKey) ?? "";
            foreach (var view in CameraPresets.BuiltIn)
                if (view.Name == name) return view;
            return Presets.Find(name) ?? CameraPresets.Rear;
        }
    }

    private string ViewName(CameraPreset view) => view == CameraPresets.Front ? _strings.Get("camera.front")
        : view == CameraPresets.Side ? _strings.Get("camera.side")
        : view == CameraPresets.Rear ? _strings.Get("camera.rear")
        : view.Name;

    private void Fill()
    {
        _choices.Clear();
        _choices.AddRange(CameraPresets.BuiltIn);
        _choices.AddRange(Presets.Saved);
        Use.Clear();
        var picked = Picked;
        for (int i = 0; i < _choices.Count; i++)
        {
            Use.AddItem(ViewName(_choices[i]));
            if (_choices[i] == picked) Use.Select(i);
        }
        DeleteButton.Disabled = Presets.Find(picked.Name) is null;
    }

    /// <summary>Goes to a view and keeps it as the picked one.</summary>
    public void Pick(CameraPreset view)
    {
        _settings.SetText(PickedKey, view.Name);
        _apply(view);
        _status.Text = string.Empty;
        Fill();
    }

    private void SaveCurrent()
    {
        if (InMouseLook?.Invoke() == true)
        {
            _status.Text = _strings.Get("camera.mouselook");
            return;
        }
        var (offset, focus) = _current();
        if (!Presets.Save(PresetName.Text, offset, focus))
        {
            _status.Text = _strings.Get("camera.not-saved");
            return;
        }
        _settings.SetText(SavedKey, Presets.ToText());
        var saved = Presets.Find(PresetName.Text.Trim())!;
        PresetName.Clear();
        Pick(saved);
    }

    private void DeletePicked()
    {
        if (!Presets.Remove(Picked.Name))
            return;
        _settings.SetText(SavedKey, Presets.ToText());
        Pick(CameraPresets.Rear);
    }
}
