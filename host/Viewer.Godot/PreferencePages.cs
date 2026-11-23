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
/// One ordinary Preferences page (General, Graphics, Chat, Move &amp; View, Privacy, Cache), drawn from the settings the catalogue lists for
/// it. Every control sends its change to the <see cref="PreferencesModel"/> (a preview that everything listening sees at once) and
/// shows what the model holds, so Cancel, Reset to defaults and a preset change all come back to the controls through the same path.
/// A setting that needs a restart carries its note under it; a setting kept per account is dimmed with a note until a login.
/// </summary>
public sealed partial class PrefPageView : ScrollContainer
{
    private static readonly int[] TextureSizes = [256, 512, 1024, 2048];
    private static readonly int[] TagModes = [0, 1, 2];

    private readonly StringTable _strings;
    private readonly Func<PreferencesModel> _model;
    private readonly Action<string, bool> _say;
    private readonly Dictionary<string, Action> _sync = new();
    private readonly List<Label> _accountNotes = new();
    private readonly Dictionary<string, Control> _controls = new();
    private readonly Func<UiSounds?> _sounds;
    private readonly Dictionary<UiSoundSlot, (Label Note, OptionButton Source)> _soundStatus = new();
    private double _statusAge;
    private bool _syncing;

    public PrefPage Page { get; }

    /// <summary>The control for a setting by key (for the render cases and checks).</summary>
    public Control? ControlFor(string key) => _controls.GetValueOrDefault(key);

    public PrefPageView(StringTable strings, PrefPage page, Func<PreferencesModel> model, Action<string, bool> say,
        Func<int>? clearCache, Func<bool>? clearNames, Func<UiSounds?>? sounds = null)
    {
        _strings = strings;
        _sounds = sounds ?? (() => null);
        _model = model;
        _say = say;
        Page = page;
        Name = "page_" + page.ToString().ToLowerInvariant();
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // a page wider than its place (a narrow window at a large interface size) scrolls sideways inside the window's margin, rather
        // than widening the window's content so that its right side runs under the border
        HorizontalScrollMode = ScrollMode.Auto;
        var box = new VBoxContainer { Name = "page", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(box);
        if (page == PrefPage.Sound)
            BuildSoundPage(box);
        else
            foreach (var def in PreferenceCatalog.OfPage(page))
                AddSetting(box, def);
        if (page == PrefPage.Privacy && clearNames is not null)
            box.AddChild(ConfirmedAction("clear_names", "prefs.privacy.clear", "prefs.privacy.clear.ask", "prefs.privacy.clear.yes", "prefs.privacy.clear.no",
                () => _strings.Get(clearNames() ? "prefs.privacy.cleared" : "prefs.privacy.none")));
        if (page == PrefPage.Cache && clearCache is not null)
            box.AddChild(ConfirmedAction("clear_cache", "prefs.cache.clear", "prefs.cache.clear.ask", "prefs.cache.clear.yes", "prefs.cache.clear.no",
                () => _strings.Get("prefs.cache.cleared", new Counted("count.entries", clearCache()))));
        Sync();
    }


    /// <summary>Shows what the model holds in every control (after any change, Cancel or Reset).</summary>
    public void Sync()
    {
        _syncing = true;
        foreach (var refresh in _sync.Values)
            refresh();
        bool account = !string.IsNullOrEmpty(_model().Account);
        foreach (var note in _accountNotes)
            note.Visible = !account;
        _syncing = false;
    }

    private void Edit(PrefDef def, object value)
    {
        if (_syncing)
            return;
        _model().Edit(def, value);
        Sync();
    }

    private void AddSetting(VBoxContainer box, PrefDef def)
    {
        string label = _strings.Get(def.LabelKey);
        Control control;
        if (def.Kind == PrefKind.Switch)
        {
            var check = new CheckBox { Name = "pref_" + def.Key, Text = label };
            check.AccessibilityName = label;
            check.Toggled += on => Edit(def, on);
            _sync[def.Key] = () =>
            {
                check.SetPressedNoSignal(_model().Value(def) is true);
                DisabledTip.Set(check, !_model().Enabled(def), def.Scope == PrefScope.PerAccount ? _strings.Get("prefs.note.account") : null);
            };
            box.AddChild(check);
            control = check;
        }
        else
        {
            var row = new HBoxContainer { Name = "row_" + def.Key };
            var caption = new Label { Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(120, 0) };
            caption.AccessibilityName = label;
            row.AddChild(caption);
            control = MakeValueControl(def, label);
            row.AddChild(control);
            if (def.Kind == PrefKind.Folder)
                AddChooseFolder(row, def);
            box.AddChild(row);
        }
        _controls[def.Key] = control;
        string? note = def.Key == Settings.GraphicsBackendKey ? "prefs.backend.notice"
            : def.Apply == PrefApply.Restart ? "prefs.note.restart"
            : def.Apply == PrefApply.NextUse ? "prefs.note.nextuse" : null;
        if (note is not null)
            box.AddChild(Caption(_strings.Get(note), "note_" + def.Key));
        if (def.Key == Settings.GraphicsBackendKey && GraphicsBackendInfo.Status is { } backend)
        {
            // which backend is really running, and why it is not the one wanted when the wanted one could not start
            box.AddChild(Caption(backend.InUseText(_strings), "backend_inuse"));
            if (backend.NoticeText(_strings) is { } why)
                box.AddChild(Caption(why, "backend_why"));
        }
        if (def.Scope == PrefScope.PerAccount)
        {
            var account = Caption(_strings.Get("prefs.note.account"), "account_" + def.Key);
            _accountNotes.Add(account);
            box.AddChild(account);
        }
    }

    private Control MakeValueControl(PrefDef def, string label)
    {
        switch (def.Kind)
        {
            case PrefKind.Real when def.Key == UiSoundSettings.Volume.Key || def.Key == AudioLevels.Master.Key || def.Key == AudioLevels.Sounds.Key || def.Key == AudioLevels.Ambient.Key:
                return Slider("pref_" + def.Key, label, () => (double)_model().Value(def)!, v => Edit(def, v), def.Key);
            case PrefKind.Whole when def.Key == Settings.MaxTextureResolution.Key:
                return Options(def, label, TextureSizes.Select(s => s.ToString(CultureInfo.InvariantCulture)).ToList(),
                    () => Array.IndexOf(TextureSizes, (int)_model().Value(def)!), i => TextureSizes[i]);
            case PrefKind.Whole when def.Key == Settings.NameTagMode.Key:
                return Options(def, label, TagModes.Select(m => _strings.Get("prefs.c.avatar_name_tag_mode." + m)).ToList(),
                    () => Array.IndexOf(TagModes, (int)_model().Value(def)!), i => TagModes[i]);
            case PrefKind.Whole:
            case PrefKind.Real:
            {
                bool whole = def.Kind == PrefKind.Whole;
                // a real number keeps the two decimals it is stored with: the engine snaps a value to its step counted from the minimum,
                // so a step of 0.1 above a minimum of 0.01 made a typed 1.5 into 1.51; the arrows still move by 0.1
                var spin = new SpinBox { Name = "pref_" + def.Key, MinValue = def.Min, MaxValue = def.Max, Step = whole ? 1 : 0.01, CustomArrowStep = whole ? 0 : 0.1,
                    Rounded = whole, CustomMinimumSize = new Vector2(110, 0) };
                spin.AccessibilityName = label;
                spin.ValueChanged += v => Edit(def, whole ? (object)(int)Math.Round(v) : Math.Round(v, 2));
                _sync[def.Key] = () =>
                {
                    spin.SetValueNoSignal(Convert.ToDouble(_model().Value(def), CultureInfo.InvariantCulture));
                    spin.Editable = _model().Enabled(def);
                };
                return spin;
            }
            case PrefKind.Choice:
            {
                var ids = def.Choices.ToList();
                var labels = ids.Select(id => _strings.Get("prefs.c." + def.Key + "." + id)).ToList();
                var option = Options(def, label, labels, () => ids.IndexOf((string)_model().Value(def)!), i => ids[i]);
                if (def.Key == Settings.GraphicsPresetKey)
                    option.SetItemDisabled(ids.IndexOf(GraphicsPresets.Custom), true);   // Custom is shown when the values stand for no preset; it is not chosen
                return option;
            }
            default:
            {
                var line = new LineEdit { Name = "pref_" + def.Key, CustomMinimumSize = new Vector2(160, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
                line.AccessibilityName = label;
                line.TextSubmitted += text => Edit(def, text.Trim());
                line.FocusExited += () => Edit(def, line.Text.Trim());
                _sync[def.Key] = () => line.Text = _model().Value(def) as string ?? "";
                return line;
            }
        }
    }

    /// <summary>A Choose... button beside a folder setting's field: it opens the system's folder picker and puts the chosen folder in the field.</summary>
    private void AddChooseFolder(HBoxContainer row, PrefDef def)
    {
        var choose = new Button { Name = "choose_" + def.Key, Text = _strings.Get("prefs.cache.choose") };
        choose.AccessibilityName = choose.Text;
        var dialog = new FileDialog
        {
            Name = "choose_" + def.Key + "_dialog", FileMode = FileDialog.FileModeEnum.OpenDir, Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true, Title = _strings.Get("prefs.cache.choose.title"),
        };
        dialog.DirSelected += dir => { Edit(def, dir.Trim()); Sync(); };
        choose.Pressed += () => dialog.PopupCentered(new Vector2I(700, 450));
        row.AddChild(choose);
        row.AddChild(dialog);
    }

    private OptionButton Options(PrefDef def, string label, List<string> texts, Func<int> selected, Func<int, object> valueAt)
    {
        var option = new OptionButton { Name = "pref_" + def.Key, CustomMinimumSize = new Vector2(160, 0) };
        option.AccessibilityName = label;
        foreach (var text in texts)
            option.AddItem(text);
        option.ItemSelected += i => Edit(def, valueAt((int)i));
        _sync[def.Key] = () =>
        {
            int i = selected();
            if (i >= 0)
                option.Select(i);
            option.Disabled = !_model().Enabled(def);
        };
        return option;
    }

    private Label Caption(string text, string name)
    {
        var l = new Label { Name = name, Text = text, ThemeTypeVariation = UiTheme.CaptionLabel, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(120, 0) };
        l.AccessibilityName = text;
        return l;
    }

    /// <summary>A button that asks before it acts: pressing it shows the question with a yes and a no; the yes runs the action and reports its answer.</summary>
    private VBoxContainer ConfirmedAction(string name, string buttonKey, string askKey, string yesKey, string noKey, Func<string> run)
    {
        var holder = new VBoxContainer { Name = name };
        var button = new Button { Name = name + "_button", Text = _strings.Get(buttonKey) };
        button.AccessibilityName = button.Text;
        var ask = new HBoxContainer { Name = name + "_ask", Visible = false };
        var question = new Label { Text = _strings.Get(askKey), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(120, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        question.AccessibilityName = question.Text;
        var yes = new Button { Name = name + "_yes", Text = _strings.Get(yesKey), ThemeTypeVariation = UiTheme.DangerButton };
        yes.AccessibilityName = yes.Text;
        var no = new Button { Name = name + "_no", Text = _strings.Get(noKey) };
        no.AccessibilityName = no.Text;
        ask.AddChild(question);
        ask.AddChild(yes);
        ask.AddChild(no);
        button.Pressed += () => ask.Visible = true;
        no.Pressed += () => ask.Visible = false;
        yes.Pressed += () =>
        {
            ask.Visible = false;
            _say(run(), false);
        };
        holder.AddChild(button);
        holder.AddChild(ask);
        return holder;
    }

    // ---------------- Sound: every interface sound, to hear and to choose ----------------

    /// <summary>A 0 to 1 level slider kept in step with the model under <paramref name="syncKey"/>.</summary>
    private HSlider Slider(string name, string label, Func<double> read, Action<double> write, string syncKey)
    {
        var slider = new HSlider { Name = name, MinValue = 0, MaxValue = 1, Step = 0.05, CustomMinimumSize = new Vector2(120, 20), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        slider.AccessibilityName = label;
        slider.ValueChanged += v =>
        {
            if (!_syncing)
                write(Math.Round(v, 2));
        };
        _sync[syncKey] = () => slider.SetValueNoSignal(read());
        return slider;
    }

    public override void _Process(double delta)
    {
        // a grid sound's answer arrives later than the edit that asked for it
        if (_soundStatus.Count == 0 || !IsVisibleInTree())
            return;
        _statusAge += delta;
        if (_statusAge < 0.5)
            return;
        _statusAge = 0;
        RefreshSoundStatus();
    }
}
