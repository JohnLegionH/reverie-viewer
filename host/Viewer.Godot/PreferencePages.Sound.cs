// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Sound;

namespace Viewer.Godot;

/// <summary>
/// A container that gives its one child the room it asks for but never more than <see cref="MaxWidth"/> across, from the left edge, so a
/// page keeps a readable line length in a wide window (the label and its control stay side by side instead of a gap opening between them).
/// </summary>
public sealed partial class MaxWidthBox : Container
{
    /// <summary>The widest the child is made, in interface units.</summary>
    public float MaxWidth { get; set; } = 620;

    public override Vector2 _GetMinimumSize()
    {
        var min = Vector2.Zero;
        foreach (var node in GetChildren())
            if (node is Control { Visible: true } c)
                min = min.Max(c.GetCombinedMinimumSize());
        return min;
    }

    public override void _Notification(int what)
    {
        if (what != NotificationSortChildren)
            return;
        foreach (var node in GetChildren())
            if (node is Control { Visible: true } c)
                FitChildInRect(c, new Rect2(0, 0, Math.Min(Size.X, Math.Max(MaxWidth, c.GetCombinedMinimumSize().X)), Size.Y));
    }
}

/// <summary>
/// The Preferences > Sound page: a Volume section with one row a level (name, slider, value,
/// mute), the Options switches, a collapsed More section for the two thresholds, and the interface sounds as a compact table in a section
/// that is closed until opened. The settings, their keys and their saved values are the catalogue's; only the layout is here.
/// </summary>
public sealed partial class PrefPageView
{
    /// <summary>The settings key that keeps which of the page's collapsible sections are open (their ids, comma separated).</summary>
    public const string SoundSectionsKey = "prefs_sound_sections_open";

    private static readonly UiSoundSource[] Sources = [UiSoundSource.Grid, UiSoundSource.Off];
    private const float NameColumn = 150;

    private void BuildSoundPage(VBoxContainer box)
    {
        var width = new MaxWidthBox { Name = "sound_width", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(width);
        var margin = new MarginContainer { Name = "sound_margin" };
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 12);
        width.AddChild(margin);
        var page = new VBoxContainer { Name = "sound_content", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        page.AddThemeConstantOverride("separation", 10);
        margin.AddChild(page);

        // Volume: one row each (name, slider, value, mute), as the official viewer's Sound & Media tab
        Section(page, "volume", false, null, null, out var volume);
        var rows = new GridContainer { Name = "sound_volumes", Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("h_separation", 10);
        rows.AddThemeConstantOverride("v_separation", 8);
        volume.AddChild(rows);
        AddVolumeRow(rows, "master", AudioLevels.Master.Key, AudioLevels.MuteMaster.Key);
        AddVolumeRow(rows, "sounds", AudioLevels.Sounds.Key, AudioLevels.MuteSounds.Key);
        AddVolumeRow(rows, "ambient", AudioLevels.Ambient.Key, AudioLevels.MuteAmbient.Key);
        AddVolumeRow(rows, "interface", UiSoundSettings.Volume.Key, UiSoundSettings.MuteAll.Key);

        // Options: plain tick boxes
        Section(page, "options", false, null, null, out var options);
        foreach (var key in new[] { AudioLevels.CollisionSounds.Key, AudioLevels.GestureSounds.Key, AudioLevels.Mono.Key, UiSoundSettings.MuteWhenMinimized.Key })
            AddSetting(options, PreferenceCatalog.Find(key)!);
        if (_controls.GetValueOrDefault(AudioLevels.Mono.Key) is { } mono)
            mono.TooltipText = _strings.Get("prefs.sound.mono.tip");

        // More: what few users change, closed until opened
        Section(page, "more", true, () => _strings.Get("prefs.sound.more.summary",
            Convert.ToDouble(_model().Value(PreferenceCatalog.Find(UiSoundSettings.MoneyThreshold.Key)!), System.Globalization.CultureInfo.InvariantCulture).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture),
            Convert.ToDouble(_model().Value(PreferenceCatalog.Find(UiSoundSettings.HealthThreshold.Key)!), System.Globalization.CultureInfo.InvariantCulture).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)),
            null, out var more);
        AddLabeledSetting(more, PreferenceCatalog.Find(UiSoundSettings.MoneyThreshold.Key)!);
        AddLabeledSetting(more, PreferenceCatalog.Find(UiSoundSettings.HealthThreshold.Key)!);

        // Interface sounds: one line a sound, closed until opened, its header saying how many play
        var all = new Button { Name = "sound_all_" + UiSoundSettings.IdOf(UiSoundSource.Grid), Text = _strings.Get("prefs.sound.all_grid") };
        all.AccessibilityName = all.Text;
        all.Pressed += () =>
        {
            _model().EditAllSlotSources(UiSoundSource.Grid);
            _sounds()?.Preload();
            Sync();
            RefreshSoundStatus();
        };
        Section(page, "interface", true, () => _strings.Get("prefs.sound.summary", _model().InterfaceSoundsOn, PreferencesModel.InterfaceSoundCount), all, out var list);
        AddInterfaceTable(list);
        RefreshSoundStatus();
    }

    private HashSet<string> OpenSoundSections() =>
        (_model().Service.GetText(SoundSectionsKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    /// <summary>One section: a flat header with a rule under it. A collapsible one has a chevron, is closed until the user opens it (the choice is
    /// kept), and says in one line what it holds while it is closed; <paramref name="extra"/> sits at the right end of its header.</summary>
    private void Section(VBoxContainer parent, string id, bool collapsible, Func<string>? summary, Control? extra, out VBoxContainer body)
    {
        string title = _strings.Get(id == "interface" ? "prefs.sound.heading" : "prefs.sound.section." + id);
        var holder = new VBoxContainer { Name = "section_" + id };
        holder.AddThemeConstantOverride("separation", 6);
        var content = new VBoxContainer { Name = "section_" + id + "_body" };
        content.AddThemeConstantOverride("separation", 6);
        bool open = !collapsible || OpenSoundSections().Contains(id);
        if (!collapsible)
        {
            var heading = new Label { Name = "section_" + id + "_title", Text = title, ThemeTypeVariation = UiTheme.TitleLabel };
            heading.AccessibilityName = title;
            holder.AddChild(heading);
        }
        else
        {
            var head = new HBoxContainer { Name = "section_" + id + "_header" };
            head.AddThemeConstantOverride("separation", 10);
            var toggle = new Button
            {
                Name = "section_" + id + "_toggle", Text = title, ToggleMode = true, ButtonPressed = open, Alignment = HorizontalAlignment.Left,
                ThemeTypeVariation = ReverieStyle.SectionHeader, Icon = ReverieIcons.Get(open ? LucideIcons.Down : LucideIcons.Submenu),
            };
            toggle.AccessibilityName = title;
            var line = new Label { Name = "section_" + id + "_summary", ThemeTypeVariation = UiTheme.CaptionLabel, Visible = !open, ClipText = true };
            line.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
            head.AddChild(toggle);
            head.AddChild(line);
            head.AddChild(spacer);
            if (extra is not null)
                head.AddChild(extra);
            _sync["section_summary_" + id] = () =>
            {
                line.Text = summary!();
                line.AccessibilityName = line.Text;
            };
            toggle.Toggled += on =>
            {
                content.Visible = on;
                line.Visible = !on;
                toggle.Icon = ReverieIcons.Get(on ? LucideIcons.Down : LucideIcons.Submenu);
                var now = OpenSoundSections();
                if (on) now.Add(id); else now.Remove(id);
                _model().Service.SetText(SoundSectionsKey, string.Join(",", now.Order()));
            };
            holder.AddChild(head);
        }
        holder.AddChild(new HSeparator { Name = "section_" + id + "_rule", ThemeTypeVariation = ReverieStyle.SectionDivider, MouseFilter = MouseFilterEnum.Ignore });
        content.Visible = open;
        holder.AddChild(content);
        parent.AddChild(holder);
        body = content;
    }

    private static string Percent(double level) => Math.Round(level * 100).ToString("0", System.Globalization.CultureInfo.CurrentCulture) + "%";

    /// <summary>One volume: the name, the slider, the value and the mute button, on one row.</summary>
    private void AddVolumeRow(GridContainer grid, string row, string levelKey, string muteKey)
    {
        var level = PreferenceCatalog.Find(levelKey)!;
        var mute = PreferenceCatalog.Find(muteKey)!;
        string title = _strings.Get("prefs.sound.row." + row);
        var name = new Label { Name = "volume_" + row + "_name", Text = title, CustomMinimumSize = new Vector2(NameColumn, 0), TooltipText = _strings.Get("prefs.sound.row." + row + ".tip") };
        name.AccessibilityName = title;
        name.MouseFilter = MouseFilterEnum.Pass;
        grid.AddChild(name);

        var slider = Slider("pref_" + levelKey, title, () => (double)_model().Value(level)!, v => Edit(level, v), levelKey);
        slider.CustomMinimumSize = new Vector2(160, 20);
        slider.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(slider);

        var value = new Label { Name = "volume_" + row + "_value", HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(48, 0) };
        value.AccessibilityName = title;
        grid.AddChild(value);

        var button = new Button { Name = "pref_" + muteKey, CustomMinimumSize = new Vector2(84, 0) };
        button.Pressed += () => Edit(mute, _model().Value(mute) is not true);
        grid.AddChild(button);

        _controls[levelKey] = slider;
        _controls[muteKey] = button;
        _sync["volume_text_" + levelKey] = () => value.Text = Percent((double)_model().Value(level)!);
        _sync[muteKey] = () =>
        {
            bool muted = _model().Value(mute) is true;
            button.Text = _strings.Get(muted ? "prefs.sound.unmute" : "prefs.sound.mute");
            button.AccessibilityName = button.Text + " " + title;
            slider.Modulate = new Color(1, 1, 1, muted ? 0.5f : 1f);   // a muted level is shown dimmed
        };
    }

    /// <summary>A setting beside its label: the label in a fixed column, the control right after it.</summary>
    private void AddLabeledSetting(VBoxContainer box, PrefDef def)
    {
        string label = _strings.Get(def.LabelKey);
        var row = new HBoxContainer { Name = "row_" + def.Key };
        row.AddThemeConstantOverride("separation", 10);
        var caption = new Label { Text = label, CustomMinimumSize = new Vector2(NameColumn + 100, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        caption.AccessibilityName = label;
        row.AddChild(caption);
        var control = MakeValueControl(def, label);
        row.AddChild(control);
        box.AddChild(row);
        _controls[def.Key] = control;
    }

    /// <summary>The interface sounds, one line each: name, source, On, volume and value, Play. A sound the grid has nothing for shows a dim
    /// note in the source column.</summary>
    private void AddInterfaceTable(VBoxContainer box)
    {
        box.AddChild(Caption(_strings.Get("prefs.sound.note.grid"), "sound_note"));
        var table = new GridContainer { Name = "sound_list", Columns = 6, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        table.AddThemeConstantOverride("h_separation", 8);
        table.AddThemeConstantOverride("v_separation", 4);
        foreach (var column in new[] { "sound", "source", "on", "volume", "", "" })
        {
            var head = new Label { Name = "sound_col_" + (column.Length == 0 ? "x" : column), Text = column.Length == 0 ? "" : _strings.Get("prefs.sound.col." + column), ThemeTypeVariation = UiTheme.CaptionLabel };
            head.AccessibilityName = head.Text;
            table.AddChild(head);
        }
        string[] sourceTexts = Sources.Select(s => _strings.Get("prefs.sound.source." + UiSoundSettings.IdOf(s))).ToArray();
        foreach (var info in UiSoundCatalog.All)
        {
            var slot = info.Slot;
            string key = info.KeyName;
            string title = _strings.Get("prefs.sound.slot." + key);
            var name = new Label { Name = "sound_" + key + "_name", Text = title, CustomMinimumSize = new Vector2(130, 0), ClipText = true, TooltipText = title };
            name.AccessibilityName = title;
            name.MouseFilter = MouseFilterEnum.Pass;
            table.AddChild(name);

            var cell = new HBoxContainer { Name = "sound_" + key + "_cell" };
            var source = new OptionButton { Name = "sound_" + key + "_source", CustomMinimumSize = new Vector2(100, 0) };
            source.AccessibilityName = title;
            foreach (var text in sourceTexts)
                source.AddItem(text);
            source.ItemSelected += i =>
            {
                if (_syncing)
                    return;
                _model().EditSlotSource(slot, Sources[(int)i]);
                if (Sources[(int)i] == UiSoundSource.Grid)
                    _sounds()?.Preload();
                Sync();
                RefreshSoundStatus();
            };
            _sync["slot_source_" + key] = () => source.Select(Array.IndexOf(Sources, _model().SlotSource(slot)));
            var note = new Label { Name = "sound_" + key + "_status", ThemeTypeVariation = UiTheme.CaptionLabel, CustomMinimumSize = new Vector2(100, 0), ClipText = true, Visible = false };
            note.MouseFilter = MouseFilterEnum.Pass;
            _soundStatus[slot] = (note, source);
            cell.AddChild(source);
            cell.AddChild(note);
            table.AddChild(cell);

            var on = new CheckBox { Name = "sound_" + key + "_on", Text = "" };
            on.AccessibilityName = title + " " + _strings.Get("prefs.sound.enabled");
            on.TooltipText = _strings.Get("prefs.sound.enabled");
            on.Toggled += v =>
            {
                if (_syncing)
                    return;
                _model().EditSlotEnabled(slot, v);
                Sync();
            };
            _sync["slot_on_" + key] = () => on.SetPressedNoSignal(_model().SlotEnabled(slot));
            table.AddChild(on);

            var volume = Slider("sound_" + key + "_volume", title + " " + _strings.Get("prefs.sound.volume"),
                () => _model().SlotVolume(slot), v => { _model().EditSlotVolume(slot, v); Sync(); }, "slot_volume_" + key);
            volume.CustomMinimumSize = new Vector2(80, 20);
            volume.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            table.AddChild(volume);

            var value = new Label { Name = "sound_" + key + "_value", HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(44, 0) };
            value.AccessibilityName = title + " " + _strings.Get("prefs.sound.volume");
            _sync["slot_value_" + key] = () => value.Text = Percent(_model().SlotVolume(slot));
            table.AddChild(value);

            var play = new Button { Name = "sound_" + key + "_play", Text = _strings.Get("prefs.sound.play") };
            play.AccessibilityName = title + " " + play.Text;
            play.SetMeta(UiSoundClicks.SilentMeta, "silent");   // the button plays the sound itself, without a click over it
            play.Pressed += () =>
            {
                _sounds()?.Audition(slot);
                RefreshSoundStatus();
            };
            table.AddChild(play);
        }
        box.AddChild(table);
    }

    /// <summary>What the grid said about each sound set to it: not on this grid, silent on the grid (no official sound), or being fetched. The
    /// note takes the place of the source choice while the grid has nothing to play.</summary>
    private void RefreshSoundStatus()
    {
        var sounds = _sounds();
        foreach (var (slot, (note, source)) in _soundStatus)
        {
            string text = sounds?.StatusOf(slot) switch
            {
                UiSoundStatus.NotOnGrid => _strings.Get("prefs.sound.status.not_on_grid"),
                UiSoundStatus.NoGridSound => _strings.Get("prefs.sound.status.no_grid_sound"),
                UiSoundStatus.Fetching => _strings.Get("prefs.sound.status.fetching"),
                _ => "",
            };
            if (note.Text != text)
            {
                note.Text = text;
                note.TooltipText = text;
                note.AccessibilityName = text;
            }
            note.Visible = text.Length > 0;
            source.Visible = text.Length == 0;
        }
    }
}
