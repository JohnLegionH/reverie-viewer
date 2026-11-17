// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Sound;

namespace Viewer.Godot;

/// <summary>
/// The Sound Tuner: pick one of our interface sounds and see its recipe's numbers as sliders (the same table the generator makes the
/// shipped files from). Moving a slider makes the sound again with the shared synthesizer and plays it at once. Save keeps the tuned
/// recipes in the user folder (ui-sounds-tuned.json), and the viewer plays them from then on; Reset puts the sound back to its committed
/// recipe. Opened from Preferences > Sound > Tune.
/// </summary>
public sealed partial class SoundTunerPanel : VBoxContainer
{
    public const string FloaterId = "sound_tuner";

    /// <summary>The shortest gap between two plays while a slider is dragged.</summary>
    private const ulong PlayGapMs = 120;

    private readonly StringTable _strings;
    private readonly UiSoundTuning _tuning;
    private readonly Action<string> _play;
    private readonly OptionButton _pick;
    private readonly VBoxContainer _sliders;
    private readonly Label _status;
    private readonly List<(string Key, HSlider Slider, Label Value)> _rows = new();
    private SoundRecipe _recipe;
    private ulong _lastPlay;

    public SoundTunerPanel(StringTable strings, UiSoundTuning tuning, Action<string> play)
    {
        _strings = strings;
        _tuning = tuning;
        _play = play;
        Name = "sound_tuner";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddChild(new Label { Name = "tuner_note", Text = strings.Get("tuner.note"), AutowrapMode = TextServer.AutowrapMode.WordSmart });

        var top = new HBoxContainer { Name = "tuner_top" };
        top.AddChild(new Label { Text = strings.Get("tuner.sound") });
        _pick = new OptionButton { Name = "tuner_pick", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _pick.AccessibilityName = strings.Get("tuner.sound");
        foreach (var file in SoundTunerList.Files)
            _pick.AddItem(file == SoundTunerList.Footstep ? strings.Get("tuner.footstep")
                : strings.Get("prefs.sound.slot." + UiSoundCatalog.All.First(i => i.File == file).KeyName));
        _pick.ItemSelected += i => Choose((int)i);
        top.AddChild(_pick);
        var playButton = new Button { Name = "tuner_play", Text = strings.Get("tuner.play") };
        playButton.SetMeta(UiSoundClicks.SilentMeta, "silent");
        playButton.Pressed += () => Play(force: true);
        top.AddChild(playButton);
        AddChild(top);

        var scroll = new ScrollContainer { Name = "tuner_scroll", SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _sliders = new VBoxContainer { Name = "tuner_sliders", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_sliders);
        AddChild(scroll);

        var bottom = new HBoxContainer { Name = "tuner_bottom" };
        var save = new Button { Name = "tuner_save", Text = strings.Get("tuner.save") };
        save.Pressed += () => Save();
        var reset = new Button { Name = "tuner_reset", Text = strings.Get("tuner.reset") };
        reset.Pressed += Reset;
        _status = new Label { Name = "tuner_status", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bottom.AddChild(save);
        bottom.AddChild(reset);
        bottom.AddChild(_status);
        AddChild(bottom);

        _recipe = Recipe(0);
        Choose(0);
    }

    /// <summary>The file of the sound shown now (an interface slot's, or our footstep's).</summary>
    public string File { get; private set; } = SoundTunerList.Files[0];

    /// <summary>The interface slot shown now; null while our footstep is shown.</summary>
    public UiSoundSlot? Slot => UiSoundCatalog.All.FirstOrDefault(i => i.File == File)?.Slot;

    /// <summary>The recipe shown now (tuned or committed).</summary>
    public SoundRecipe Current => _recipe;

    private SoundRecipe Recipe(int index) => _tuning.Current(SoundTunerList.Files[index])!;

    /// <summary>Shows a sound's sliders.</summary>
    public void Choose(int index)
    {
        if (index < 0 || index >= SoundTunerList.Files.Count)
            return;
        File = SoundTunerList.Files[index];
        _pick.Select(index);
        _recipe = Recipe(index);
        Build();
        ShowTuned();
    }

    /// <summary>Moves one slider (by its key), as a drag does: the sound is made again and played.</summary>
    public bool Set(string key, double value)
    {
        var next = UiSoundParams.With(_recipe, key, value);
        if (next.Equals(_recipe))
            return UiSoundParams.Of(_recipe).Any(p => p.Key == key);
        _recipe = next;
        _tuning.Set(next);
        foreach (var (k, slider, label) in _rows)
        {
            var p = UiSoundParams.Of(_recipe).First(x => x.Key == k);
            slider.SetValueNoSignal(p.Value);
            label.Text = Number(p.Value);
        }
        ShowTuned();
        Play(force: false);
        return true;
    }

    /// <summary>Saves the tuned recipes. False when the file could not be written.</summary>
    public bool Save()
    {
        bool ok = _tuning.Save();
        _status.Text = _strings.Get(ok ? "tuner.saved" : "tuner.not_saved");
        return ok;
    }

    /// <summary>Puts the sound shown back to its committed recipe (and saves).</summary>
    public void Reset()
    {
        _tuning.Reset(File);
        Choose(SoundTunerList.Files.ToList().IndexOf(File));
        Play(force: true);
    }

    private void Build()
    {
        foreach (var child in _sliders.GetChildren())
        {
            _sliders.RemoveChild(child);
            child.QueueFree();
        }
        _rows.Clear();
        foreach (var p in UiSoundParams.Of(_recipe))
        {
            var row = new HBoxContainer { Name = "tuner_row_" + p.Key.Replace('.', '_') };
            row.AddChild(new Label { Text = p.Label, CustomMinimumSize = new Vector2(220, 0), ClipText = true });
            var slider = new HSlider
            {
                Name = "tuner_" + p.Key.Replace('.', '_'), MinValue = p.Min, MaxValue = p.Max, Step = Step(p), Value = p.Value,
                SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(160, 20),
            };
            slider.AccessibilityName = p.Label;
            var value = new Label { Text = Number(p.Value), CustomMinimumSize = new Vector2(70, 0) };
            string key = p.Key;
            slider.ValueChanged += v => Set(key, v);
            row.AddChild(slider);
            row.AddChild(value);
            _sliders.AddChild(row);
            _rows.Add((key, slider, value));
        }
    }

    // fine steps for small numbers (levels, ratios), whole steps for times and pitches
    private static double Step(UiSoundParam p) => p.Max - p.Min <= 20 ? 0.01 : p.Max - p.Min <= 2000 ? 0.5 : 1;

    private static string Number(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private void ShowTuned() =>
        _status.Text = _tuning.IsTuned(File) ? _strings.Get("tuner.tuned") : "";

    private void Play(bool force)
    {
        ulong now = Time.GetTicksMsec();
        if (!force && now - _lastPlay < PlayGapMs)
            return;
        _lastPlay = now;
        _play(File);
    }
}
