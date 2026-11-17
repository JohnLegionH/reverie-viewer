// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The Preferences window: a list of categories (General, Graphics, Chat, Move &amp; View, Interface, Privacy, Cache) and the page of the
/// one chosen. The Interface page is drawn from an <see cref="InterfacePreferences"/> (tested without the engine); every change the
/// person makes goes to it and the whole interface restyles at once through <see cref="UiTheme"/>. The other pages are drawn from the
/// settings the <see cref="PreferenceCatalog"/> lists and edit through a <see cref="PreferencesModel"/>. On every page Save stores
/// what the page changed, Cancel (and closing the window) puts back what was saved, and Reset puts the page's defaults in
/// place (for the Interface page, the preset's own colours). The Sound page also lists every interface sound with a Play button. Categories
/// for features the viewer does not have yet (voice) are not shown.
/// </summary>
public static class PreferencesFeature
{
    public const string FloaterId = "preferences";

    /// <summary>Adds the window, the Me menu entry and its command to a shell that is already drawn.</summary>
    public static PreferencesPanel Install(ShellRoot shell, SettingsService settings, Func<int>? clearCache = null, Func<bool>? clearNames = null)
    {
        var s = shell.Shell;
        var panel = new PreferencesPanel(s.Strings, settings, clearCache, clearNames);
        panel.CloseRequested = () => s.Floaters.Close(FloaterId);
        shell.AddFloater(new FloaterSpec(FloaterId, "prefs.title", new UiSize(700, 620), new UiSize(520, 360)), panel);
        s.Commands.Register("floater.preferences", () => s.Floaters.Toggle(FloaterId));
        s.Menus.AddEntry("me", new MenuEntry("me.preferences", "menu.me.preferences", "floater.preferences", Order: 10));
        // closing the window with edits not saved puts the saved look back
        bool wasOpen = false;
        s.Floaters.Changed += () =>
        {
            bool open = System.Linq.Enumerable.Any(s.Floaters.OpenFloaters, f => f.Id == FloaterId);
            if (wasOpen && !open)
                panel.CancelEdits();
            wasOpen = open;
        };
        return panel;
    }
}

public sealed partial class PreferencesPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private readonly SettingsService _settings;
    private InterfacePreferences _page;
    private string _account = "";
    private bool _syncing;
    private PreferencesModel _model;
    private readonly Dictionary<string, Button> _nav = new();
    private readonly Dictionary<PrefPage, PrefPageView> _views = new();
    private readonly Label _title;
    private readonly ScrollContainer _interfaceScroll;
    private readonly Label _status;
    private string _current = "general";

    /// <summary>The categories in the order shown: the official viewer's order, with the Interface page where its Colors tab is.</summary>
    public static readonly string[] Categories = ["general", "graphics", "sound", "chat", "moveview", "interface", "privacy", "cache", "building"];

    /// <summary>The category now shown.</summary>
    public string CurrentCategory => _current;

    /// <summary>The interface sounds, for the Sound page's Play buttons and the grid's answers (null: none yet).</summary>
    public Viewer.Shell.Sound.UiSounds? Sounds { get; set; }

    /// <summary>Opens the Sound Tuner (the Sound page's Tune button).</summary>
    public Action? OpenTuner { get; set; }

    /// <summary>The page logic for the ordinary pages.</summary>
    public PreferencesModel Model => _model;

    /// <summary>The control of a setting on its page (for checks).</summary>
    public Control? ControlFor(string key) => _views.Values.Select(v => v.ControlFor(key)).FirstOrDefault(c => c is not null);

    private readonly Dictionary<string, Button> _cards = new();
    private readonly Dictionary<string, ColourButton> _pickers = new();
    private readonly List<(Label Name, Label Ratio, Label Verdict)> _rows = new();
    private readonly Label _message;
    private readonly HSlider _scale;
    private readonly FloaterOpacityPrefs _opacity;
    private readonly (HSlider Slider, Label Value) _activeOpacity, _inactiveOpacity;

    /// <summary>The slider for the opacity of the window that has the focus (for checks).</summary>
    public HSlider ActiveOpacitySlider => _activeOpacity.Slider;

    /// <summary>The slider for the opacity of the other windows (for checks).</summary>
    public HSlider InactiveOpacitySlider => _inactiveOpacity.Slider;

    /// <summary>The window opacity edits (for checks).</summary>
    public FloaterOpacityPrefs Opacity => _opacity;
    private readonly Label _scaleValue;
    private readonly CheckBox _gridAccent;
    private readonly Label _gridNote;
    private readonly ColourButton _customAccent;
    private readonly HBoxContainer _confirm;
    private readonly FileDialog _import;
    private readonly FileDialog _export;

    public Button SaveButton { get; }
    public Button CancelButton { get; }

    /// <summary>Asked to close the window: Cancel and OK both end with it.</summary>
    public Action? CloseRequested { get; set; }
    public Button ResetButton { get; }
    public Button ImportButton { get; }
    public Button ExportButton { get; }

    /// <summary>The page being edited (rebuilt when the account or the grid's accent changes).</summary>
    public InterfacePreferences Page => _page;

    public PreferencesPanel(StringTable strings, SettingsService settings, Func<int>? clearCache = null, Func<bool>? clearNames = null)
    {
        _strings = strings;
        _settings = settings;
        _model = new PreferencesModel(settings, null);
        var firstChoice = ThemeStore.Load(settings, null);
        _page = new InterfacePreferences(firstChoice, null, ThemeStore.ScaleOwn(settings, null, firstChoice), DisplayScaleProbe.Reading());
        _opacity = new FloaterOpacityPrefs(settings);
        Name = "preferences";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var body = new HBoxContainer { Name = "body", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(body);
        // the list of pages scrolls when the window is too short for it (a large interface size), so the window can shrink
        // to the main view and keep the buttons below in sight
        var navScroll = new ScrollContainer { Name = "categories_scroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(150, 0) };
        body.AddChild(navScroll);
        var nav = new VBoxContainer { Name = "categories", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        // when the list is taller than its place (a theme with deeper buttons put the last one behind a scroll bar at the window's usual
        // size), the buttons close up with no gap between them; the list only scrolls when even that does not fit (a large interface size)
        navScroll.Resized += () => FitCategories(navScroll, nav);
        nav.AccessibilityName = strings.Get("prefs.categories");
        navScroll.AddChild(nav);
        foreach (var id in Categories)
        {
            var go = new Button { Name = "category_" + id, Text = strings.Get("prefs.category." + id), ToggleMode = true, Alignment = HorizontalAlignment.Left };
            go.AccessibilityName = go.Text;
            string target = id;
            go.Pressed += () => ShowCategory(target);
            nav.AddChild(go);
            _nav[id] = go;
        }
        var content = new VBoxContainer { Name = "content", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddChild(content);
        _title = new Label { Name = "category", ThemeTypeVariation = UiTheme.TitleLabel };
        content.AddChild(_title);

        var scroll = new ScrollContainer { Name = "scroll", SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        content.AddChild(scroll);
        _interfaceScroll = scroll;
        foreach (var pageId in Enum.GetValues<PrefPage>())
        {
            var view = new PrefPageView(strings, pageId, () => _model, Say, clearCache, clearNames, () => Sounds, () => OpenTuner?.Invoke()) { Visible = false };
            content.AddChild(view);
            _views[pageId] = view;
        }
        var box = new VBoxContainer { Name = "page", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(box);

        // theme cards
        box.AddChild(Heading(strings.Get("prefs.preset.heading")));
        var cards = new HFlowContainer { Name = "presets" };
        foreach (var id in ThemePresets.Ids)
        {
            var card = new VBoxContainer { Name = "card_" + id };
            var swatches = new HBoxContainer { Name = "swatches" };
            var look = ThemePresets.Get(id);
            foreach (var colour in new[] { look.Panel.Opaque(), look.Bar, look.Accent })
                swatches.AddChild(new ColorRect { CustomMinimumSize = new Vector2(24, 14), Color = ShellTheme.C(colour), MouseFilter = MouseFilterEnum.Ignore });
            card.AddChild(swatches);
            var button = new Button { Name = "preset_" + id, Text = ThemePresets.NameOf(id), ToggleMode = true };
            button.AccessibilityName = ThemePresets.NameOf(id);
            string preset = id;
            button.Pressed += () => { if (!_syncing) _page.SelectPreset(preset); else button.SetPressedNoSignal(true); };
            card.AddChild(button);
            _cards[id] = button;
            cards.AddChild(card);
        }
        box.AddChild(cards);

        // accent
        box.AddChild(Heading(strings.Get("prefs.accent.heading")));
        var accents = new HBoxContainer { Name = "accents" };
        foreach (var (key, colour) in ThemeSwatches.Accents)
        {
            var swatch = new Button { Name = "accent_" + key, CustomMinimumSize = new Vector2(28, 28), TooltipText = strings.Get("prefs.accent." + key) };
            swatch.AccessibilityName = strings.Get("prefs.accent." + key);
            foreach (var state in new[] { "normal", "hover", "pressed", "focus" })
                swatch.AddThemeStyleboxOverride(state, ShellTheme.Box(UiTheme.Tokens, colour, state == "normal" ? UiTheme.Tokens.Border : UiTheme.Tokens.Text, 0));
            var chosen = colour;
            swatch.Pressed += () => _page.SetAccent(chosen);
            accents.AddChild(swatch);
        }
        _customAccent = new ColourButton { Name = "accent_custom", CustomMinimumSize = new Vector2(48, 28) };
        _customAccent.AccessibilityName = strings.Get("prefs.accent.custom");
        _customAccent.ColorChanged += c => { if (!_syncing) _page.SetAccent(Pick(c)); };
        accents.AddChild(_customAccent);
        box.AddChild(accents);

        _gridAccent = new CheckBox { Name = "grid_accent", Text = strings.Get("prefs.grid.accent") };
        _gridAccent.AccessibilityName = strings.Get("prefs.grid.accent");
        _gridAccent.Toggled += on => { if (!_syncing) _page.SetUseGridAccent(on); };
        box.AddChild(_gridAccent);
        _gridNote = new Label { Name = "grid_note", Text = strings.Get("prefs.grid.none"), ThemeTypeVariation = UiTheme.CaptionLabel };
        _gridNote.AccessibilityName = strings.Get("prefs.grid.none");
        box.AddChild(_gridNote);

        // colours
        box.AddChild(Heading(strings.Get("prefs.colours.heading")));
        var grid = new GridContainer { Name = "colours", Columns = 2 };
        Row(grid, "panel", "prefs.colour.panel", c => _page.SetPanel(c));
        Row(grid, "bar", "prefs.colour.bar", c => _page.SetBar(c));
        Row(grid, "text", "prefs.colour.text", c => _page.SetText(c));
        Row(grid, "text2", "prefs.colour.text2", c => _page.SetTextSecondary(c));
        box.AddChild(grid);

        // window opacity: the focused window and the others, as in the official viewer's colours page
        box.AddChild(Heading(strings.Get("prefs.opacity.heading")));
        _activeOpacity = OpacityRow(box, "active", _opacity.SetActive);
        _inactiveOpacity = OpacityRow(box, "inactive", _opacity.SetInactive);

        // scale
        box.AddChild(Heading(strings.Get("prefs.scale.heading")));
        var scaleRow = new HBoxContainer { Name = "scale_row" };
        _scale = new HSlider { Name = "scale", MinValue = InterfacePreferences.MinScalePercent, MaxValue = InterfacePreferences.MaxScalePercent, Step = 5, Value = 100, CustomMinimumSize = new Vector2(220, 24), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _scale.AccessibilityName = strings.Get("prefs.scale.named");
        _scale.ValueChanged += v => { if (!_syncing) _page.SetScale((int)Math.Round(v)); };
        scaleRow.AddChild(_scale);
        _scaleValue = new Label { Name = "scale_value", Text = strings.Get("prefs.scale.value", 100) };
        _scaleValue.AccessibilityName = strings.Get("prefs.scale.named");
        scaleRow.AddChild(_scaleValue);
        box.AddChild(scaleRow);

        // import and export
        var files = new HBoxContainer { Name = "files" };
        ImportButton = Button(strings.Get("prefs.import"), "import");
        ExportButton = Button(strings.Get("prefs.export"), "export");
        files.AddChild(ImportButton);
        files.AddChild(ExportButton);
        box.AddChild(files);

        // readability
        box.AddChild(Heading(strings.Get("prefs.readability.heading")));
        var read = new GridContainer { Name = "readability", Columns = 3 };
        foreach (var key in new[] { "text-on-panel", "secondary-on-panel", "accent-on-bar", "button-text" })
        {
            var name = new Label { Name = "read_" + key, Text = strings.Get("prefs.read." + key), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            name.AccessibilityName = name.Text;
            var ratio = new Label { Name = "ratio_" + key };
            var verdict = new Label { Name = "verdict_" + key };
            read.AddChild(name);
            read.AddChild(ratio);
            read.AddChild(verdict);
            _rows.Add((name, ratio, verdict));
        }
        box.AddChild(read);

        _message = new Label { Name = "message", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0) };
        box.AddChild(_message);

        // the question that follows Save when a row is too low
        _confirm = new HBoxContainer { Name = "confirm", Visible = false };
        var ask = new Label { Name = "confirm_text", Text = strings.Get("prefs.confirm.low"), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(260, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ask.AccessibilityName = ask.Text;
        _confirm.AddChild(ask);
        var anyway = Button(strings.Get("prefs.confirm.save"), "confirm_save");
        anyway.ThemeTypeVariation = UiTheme.DangerButton;
        anyway.Pressed += () => Ok(confirmed: true);
        var keep = Button(strings.Get("prefs.confirm.keep"), "confirm_keep");
        keep.Pressed += () => _confirm.Visible = false;
        _confirm.AddChild(anyway);
        _confirm.AddChild(keep);
        AddChild(_confirm);

        _status = new Label { Name = "status", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0), ThemeTypeVariation = UiTheme.MutedLabel };
        AddChild(_status);

        // footer
        var footer = new HBoxContainer { Name = "footer" };
        ResetButton = Button(strings.Get("prefs.reset"), "reset");
        ResetButton.Pressed += ResetCurrent;
        footer.AddChild(ResetButton);
        footer.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        CancelButton = Button(strings.Get("prefs.cancel"), "cancel");
        CancelButton.Pressed += CancelAndClose;
        SaveButton = Button(strings.Get("prefs.ok"), "save");
        SaveButton.ThemeTypeVariation = UiTheme.PrimaryButton;
        SaveButton.Pressed += () => Ok(confirmed: false);
        footer.AddChild(CancelButton);
        footer.AddChild(SaveButton);
        AddChild(footer);

        _import = new FileDialog { Name = "import_dialog", FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = strings.Get("prefs.dialog.import") };
        _import.AddFilter("*.json");
        _import.FileSelected += path => ImportFrom(path);
        AddChild(_import);
        _export = new FileDialog { Name = "export_dialog", FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = strings.Get("prefs.dialog.export") };
        _export.AddFilter("*.json");
        _export.FileSelected += path => ExportTo(path);
        AddChild(_export);
        ImportButton.Pressed += () => _import.PopupCentered(new Vector2I(640, 440));
        ExportButton.Pressed += () => _export.PopupCentered(new Vector2I(640, 440));

        Attach(_page);
        UiTheme.ApplyOpacity(_opacity.Active, _opacity.Inactive);
        settings.Changed += key =>
        {
            if (key == Settings.ActiveFloaterTransparency.Key || key == Settings.InactiveFloaterTransparency.Key)
            {
                UiTheme.ApplyOpacity(_opacity.Active, _opacity.Inactive);
                SyncOpacity();
            }
            SyncViews();
        };
        SyncOpacity();
        ShowCategory(_current);
    }

    private (HSlider Slider, Label Value) OpacityRow(Control box, string which, Func<float, bool> set)
    {
        var row = new HBoxContainer { Name = "opacity_" + which + "_row" };
        var name = new Label { Name = "opacity_" + which + "_label", Text = _strings.Get("prefs.opacity." + which), CustomMinimumSize = new Vector2(120, 0) };
        name.AccessibilityName = name.Text;
        row.AddChild(name);
        var slider = new HSlider { Name = "opacity_" + which, MinValue = 0, MaxValue = 100, Step = 1, CustomMinimumSize = new Vector2(220, 24), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        slider.AccessibilityName = _strings.Get("prefs.opacity." + which + ".named");
        slider.ValueChanged += v => { if (!_syncing) set((float)(v / 100.0)); };
        row.AddChild(slider);
        var value = new Label { Name = "opacity_" + which + "_value" };
        value.AccessibilityName = slider.AccessibilityName;
        row.AddChild(value);
        box.AddChild(row);
        return (slider, value);
    }

    /// <summary>The sliders show the opacities now in force (a preview counts).</summary>
    private void SyncOpacity()
    {
        bool was = _syncing;
        _syncing = true;
        foreach (var (row, v) in new[] { (_activeOpacity, _opacity.Active), (_inactiveOpacity, _opacity.Inactive) })
        {
            int percent = (int)Math.Round(v * 100f);
            row.Slider.Value = percent;
            row.Value.Text = _strings.Get("prefs.opacity.value", percent);
        }
        _syncing = was;
    }

    /// <summary>The page view of a settings page (null for the interface page, which is drawn apart).</summary>
    public PrefPageView? PageView(PrefPage page) => _views.GetValueOrDefault(page);

    /// <summary>The category list keeps the theme's gap between its buttons when they fit its place, and closes the gaps when they do not.</summary>
    private static void FitCategories(ScrollContainer scroll, VBoxContainer nav)
    {
        nav.RemoveThemeConstantOverride("separation");
        int gap = nav.GetThemeConstant("separation");
        int buttons = nav.GetChildCount();
        float height = 0;
        foreach (var c in nav.GetChildren())
            if (c is Control ctl && ctl.Visible)
                height += ctl.GetCombinedMinimumSize().Y;
        if (height + gap * Math.Max(0, buttons - 1) > scroll.Size.Y)
            nav.AddThemeConstantOverride("separation", 0);
    }

    /// <summary>Shows one category's page (a name from <see cref="Categories"/>).</summary>
    public void ShowCategory(string id)
    {
        if (Array.IndexOf(Categories, id) < 0)
            return;
        _current = id;
        _title.Text = _strings.Get("prefs.category." + id);
        _title.AccessibilityName = _title.Text;
        foreach (var (key, button) in _nav)
            button.SetPressedNoSignal(key == id);
        _interfaceScroll.Visible = id == "interface";
        foreach (var (page, view) in _views)
            view.Visible = id == page.ToString().ToLowerInvariant();
        ResetButton.Text = _strings.Get(id == "interface" ? "prefs.reset" : "prefs.reset.page");
        ResetButton.AccessibilityName = ResetButton.Text;
        _status.Text = string.Empty;
        SyncViews();
    }

    /// <summary>Shows the page a setting is on, scrolled to its row, with the keyboard on its control so the row is highlighted (the
    /// command list's settings results open here). False when no page shows that setting.</summary>
    public bool ShowSetting(string key)
    {
        if (PreferenceCatalog.Find(key) is not { } def)
            return false;
        ShowCategory(def.Page.ToString().ToLowerInvariant());
        if (ControlFor(key) is not { } control)
            return false;
        // once the row shows (the window may open in the same frame, and takes the keyboard as it opens), for at most ten frames
        var tree = GetTree();
        int frames = 0;
        void Step()
        {
            tree.ProcessFrame -= Step;
            frames++;
            if (!control.IsVisibleInTree() || frames < 2)
            {
                if (frames < 10)
                    tree.ProcessFrame += Step;
                return;
            }
            for (Node? n = control.GetParent(); n is not null; n = n.GetParent())
                if (n is ScrollContainer scroll)
                {
                    scroll.EnsureControlVisible(control);
                    break;
                }
            // the row's control, or the part of it that takes the keyboard (a number box's text field)
            if (Focusable(control) is { } target)
                target.GrabFocus();
        }
        static Control? Focusable(Control c)
        {
            if (c.FocusMode != FocusModeEnum.None && c.Visible)
                return c;
            foreach (var child in c.GetChildren(includeInternal: true))
                if (child is Control k && Focusable(k) is { } found)
                    return found;
            return null;
        }
        if (tree is not null)
            tree.ProcessFrame += Step;
        return true;
    }

    private PrefPage? CurrentPage => Enum.TryParse<PrefPage>(_current, ignoreCase: true, out var page) ? page : null;

    private void SyncViews()
    {
        foreach (var view in _views.Values)
            view.Sync();
    }

    private void ResetCurrent()
    {
        if (CurrentPage is { } page)
            _model.Reset(page);
        else
        {
            _page.ResetToPreset();
            _opacity.Reset();
        }
    }

    /// <summary>Cancel: every change made since the window opened is put back, on every page, and the window closes.</summary>
    public void CancelAndClose()
    {
        CancelEdits();
        Say(string.Empty, error: false);
        CloseRequested?.Invoke();
    }

    /// <summary>OK: every page is kept and the window closes. A readability row that is too low asks first and the window stays open;
    /// so does a settings file that cannot be written.</summary>
    public bool Ok(bool confirmed)
    {
        if (Save(confirmed) != SaveOutcome.Saved)
            return false;
        if (!_model.SaveAll())
        {
            Say(_strings.Get("prefs.save.failed.page"), error: true);
            return false;
        }
        Say(string.Empty, error: false);
        CloseRequested?.Invoke();
        return true;
    }

    /// <summary>Starts the page from this account's saved theme and the grid's accent (null: the grid has none). Edits not saved are dropped.</summary>
    public void Bind(string accountKey, UiColour? gridAccent)
    {
        _account = accountKey;
        _model = new PreferencesModel(_settings, accountKey);
        var choice = ThemeStore.Load(_settings, accountKey);
        Attach(new InterfacePreferences(choice, gridAccent, ThemeStore.ScaleOwn(_settings, accountKey, choice), DisplayScaleProbe.Reading()));
        Apply();
        SyncViews();
    }

    /// <summary>The grid published an accent (null: it has none, or the typed grid changed). The page is made again from the saved theme with it.</summary>
    public void SetGridAccent(UiColour? accent)
    {
        if (accent == _page.GridAccent)
            return;
        Attach(_page.WithGridAccent(accent));
        Apply();
    }

    /// <summary>Puts back the saved look (the Cancel button, and closing the window with edits not saved).</summary>
    public void CancelEdits()
    {
        _confirm.Visible = false;
        _page.Cancel();
        _opacity.Cancel();
        foreach (var page in Enum.GetValues<PrefPage>())
            _model.Cancel(page);
        Apply();
        Refresh();
    }

    /// <summary>Reads a theme file the person chose. A broken file is refused with a message and nothing changes.</summary>
    public bool ImportFrom(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say(_strings.Get("prefs.import.failed", e.GetType().Name), error: true);
            return false;
        }
        if (!_page.Import(text, out var error))
        {
            Say(_strings.Get("prefs.import.failed", error ?? ""), error: true);
            return false;
        }
        Say(_strings.Get("prefs.import.done"), error: false);
        return true;
    }

    /// <summary>Writes the look on screen as a theme file.</summary>
    public bool ExportTo(string path)
    {
        try { File.WriteAllText(path, _page.Export(Path.GetFileNameWithoutExtension(path))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say(_strings.Get("prefs.export.failed"), error: true);
            return false;
        }
        Say(_strings.Get("prefs.export.done"), error: false);
        return true;
    }

    /// <summary>Saves for the account. A readability row that is too low asks first (the question appears; Save anyway stores it).</summary>
    public SaveOutcome Save(bool confirmed)
    {
        var outcome = _page.Save(_settings, _account, confirmed);
        if (outcome == SaveOutcome.Saved && !_opacity.Save())
            outcome = SaveOutcome.Failed;
        _confirm.Visible = outcome == SaveOutcome.NeedsConfirmation;
        if (outcome == SaveOutcome.Saved)
            Say(_strings.Get("prefs.saved"), error: false);
        else if (outcome == SaveOutcome.Failed)
            Say(_strings.Get("prefs.save.failed"), error: true);
        return outcome;
    }

    private void Attach(InterfacePreferences page)
    {
        _page = page;
        _page.Changed += () => { Apply(); Refresh(); };
        Refresh();
    }

    /// <summary>The whole interface takes the working look.</summary>
    private void Apply() => UiTheme.Apply(_page.Tokens, _page.EffectiveScale);

    private void Refresh()
    {
        _syncing = true;
        var t = _page.Tokens;
        foreach (var (id, button) in _cards)
            button.SetPressedNoSignal(_page.Working.Preset == id);
        _scale.Value = _page.EffectiveScale;
        _scaleValue.Text = _strings.Get("prefs.scale.value", _page.EffectiveScale);
        _gridAccent.ButtonPressed = _page.Working.UseGridAccent;
        _gridAccent.Disabled = _page.GridAccent is null;
        _gridNote.Visible = _page.GridAccent is null;
        _customAccent.Color = ShellTheme.C(t.Accent);
        _pickers["panel"].Color = ShellTheme.C(t.Panel.Opaque());
        _pickers["bar"].Color = ShellTheme.C(t.Bar);
        _pickers["text"].Color = ShellTheme.C(t.Text);
        _pickers["text2"].Color = ShellTheme.C(t.TextSecondary);
        var rows = _page.Readability;
        for (int i = 0; i < _rows.Count && i < rows.Count; i++)
        {
            _rows[i].Ratio.Text = _strings.Get("prefs.read.ratio", rows[i].Ratio.ToString("0.0", CultureInfo.InvariantCulture));
            _rows[i].Verdict.Text = _strings.Get(rows[i].Readable ? "prefs.read.ok" : "prefs.read.low");
            _rows[i].Verdict.ThemeTypeVariation = rows[i].Readable ? UiTheme.AccentLabel : UiTheme.ErrorLabel;
            _rows[i].Ratio.AccessibilityName = _rows[i].Ratio.Text;
            _rows[i].Verdict.AccessibilityName = _rows[i].Verdict.Text;
            _rows[i].Name.AccessibilityName = _strings.Get("prefs.read.named", _rows[i].Name.Text, _rows[i].Ratio.Text, _rows[i].Verdict.Text);
        }
        _confirm.Visible = _confirm.Visible && System.Linq.Enumerable.Any(rows, r => !r.Readable);
        _syncing = false;
    }

    private void Say(string text, bool error)
    {
        var label = _current == "interface" ? _message : _status;
        label.Text = text;
        label.AccessibilityName = text;
        label.ThemeTypeVariation = error ? UiTheme.ErrorLabel : UiTheme.MutedLabel;
    }

    private static UiColour Pick(Color c) => new(c.R, c.G, c.B);

    private Label Heading(string text)
    {
        var l = new Label { Text = text, ThemeTypeVariation = UiTheme.TitleLabel };
        l.AccessibilityName = text;
        return l;
    }

    private static Button Button(string text, string name)
    {
        var b = new Button { Name = name, Text = text };
        b.AccessibilityName = text;
        return b;
    }

    private void Row(GridContainer grid, string key, string labelKey, Action<UiColour?> set)
    {
        string text = _strings.Get(labelKey);
        var label = new Label { Name = "label_" + key, Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        label.AccessibilityName = text;
        var picker = new ColourButton { Name = "pick_" + key, CustomMinimumSize = new Vector2(96, 28) };
        picker.AccessibilityName = text;
        picker.ColorChanged += c => { if (!_syncing) set(Pick(c)); };
        _pickers[key] = picker;
        grid.AddChild(label);
        grid.AddChild(picker);
    }
}
