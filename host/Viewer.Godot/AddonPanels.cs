// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Addons;
using Viewer.Shell;
using Viewer.Shell.Addons;

namespace Viewer.Godot;

/// <summary>
/// The Add-ons window: every add-on the viewer found, with its name, version, author, job, permissions and a switch; for each
/// job, which add-on fills it; and the note of an add-on that was switched off after an error. Turning on an add-on the user
/// installed shows its permissions first, and it starts only when the user accepts them.
/// </summary>
public sealed partial class AddonsPanel : VBoxContainer
{
    public const string FloaterId = "addons";

    private readonly StringTable _strings;
    private readonly AddonHost _host;
    private readonly VBoxContainer _jobs;
    private readonly VBoxContainer _list;
    private readonly ConfirmationDialog _confirm;
    private string? _confirming;
    private bool _syncing;

    public AddonsPanel(StringTable strings, AddonHost host)
    {
        _strings = strings;
        _host = host;
        Name = "addons";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 8);

        var intro = new Label { Name = "intro", Text = strings.Get("addons.intro"), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(320, 0) };
        intro.AccessibilityName = intro.Text;
        AddChild(intro);
        _jobs = new VBoxContainer { Name = "jobs" };
        AddChild(_jobs);
        var scroll = new ScrollContainer { Name = "scroll", SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _list = new VBoxContainer { Name = "list", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_list);
        AddChild(scroll);

        _confirm = new ConfirmationDialog { Name = "confirm", Title = strings.Get("addons.confirm.title"), OkButtonText = strings.Get("addons.confirm.ok") };
        _confirm.Confirmed += () =>
        {
            if (_confirming is { } id)
                _host.SetEnabled(id, true, grantPermissions: true);
            _confirming = null;
        };
        _confirm.Canceled += () => { _confirming = null; Refresh(); };
        AddChild(_confirm);

        host.Changed += Refresh;
        Refresh();
    }

    /// <summary>The user turned an add-on's switch: an outside add-on whose permissions are not granted yet asks first.</summary>
    public void Turn(string id, bool on)
    {
        if (_host.Addons.FirstOrDefault(a => a.Id == id) is not { } entry)
            return;
        if (on && !entry.Manifest.Permissions.All(entry.Granted.Contains) && AddonConsent.Text(_strings, entry.Manifest, entry.BuiltIn) is { } consent)
        {
            _confirming = id;
            _confirm.DialogText = consent;
            _confirm.PopupCentered();
            return;
        }
        _host.SetEnabled(id, on);
    }

    private string PermissionList(IReadOnlyList<string> permissions) => AddonConsent.PermissionList(_strings, permissions);

    public void Refresh()
    {
        _syncing = true;
        try
        {
            foreach (var child in _jobs.GetChildren()) { _jobs.RemoveChild(child); child.QueueFree(); }
            foreach (var child in _list.GetChildren()) { _list.RemoveChild(child); child.QueueFree(); }

            foreach (string job in _host.Jobs)
            {
                var row = new HBoxContainer { Name = "job_" + job };
                var label = new Label { Text = _strings.Get("addons.job", job), CustomMinimumSize = new Vector2(120, 0) };
                label.AccessibilityName = label.Text;
                var choice = new OptionButton { Name = "choice", SizeFlagsHorizontal = SizeFlags.ExpandFill };
                choice.AccessibilityName = label.Text;
                var fillers = _host.Addons.Where(a => a.Manifest.Job == job).ToArray();
                choice.AddItem(_strings.Get("addons.job.none"));
                for (int i = 0; i < fillers.Length; i++)
                {
                    choice.AddItem(fillers[i].Manifest.Name);
                    if (fillers[i].Enabled) choice.Selected = i + 1;
                }
                if (choice.Selected < 0) choice.Selected = 0;
                choice.ItemSelected += index =>
                {
                    if (_syncing) return;
                    if (index == 0) { foreach (var f in fillers.Where(f => f.Enabled)) _host.SetEnabled(f.Id, false); }
                    else Turn(fillers[index - 1].Id, true);
                };
                row.AddChild(label);
                row.AddChild(choice);
                _jobs.AddChild(row);
            }

            if (_host.Addons.Count == 0)
                _list.AddChild(new Label { Name = "empty", Text = _strings.Get("addons.empty"), AutowrapMode = TextServer.AutowrapMode.WordSmart });
            foreach (var entry in _host.Addons)
            {
                var box = new VBoxContainer { Name = "addon_" + entry.Id };
                var top = new HBoxContainer { Name = "top" };
                var toggle = new CheckBox { Name = "on", Text = entry.Manifest.Name, ButtonPressed = entry.Enabled };
                toggle.AccessibilityName = _strings.Get("addons.switch", entry.Manifest.Name);
                string id = entry.Id;
                toggle.Toggled += on => { if (!_syncing) Turn(id, on); };
                top.AddChild(toggle);
                var kind = new Label { Name = "kind", Text = _strings.Get(entry.BuiltIn ? "addons.builtin" : "addons.outside"), ThemeTypeVariation = UiTheme.MutedLabel };
                top.AddChild(kind);
                box.AddChild(top);
                string details = _strings.Get("addons.details", entry.Manifest.Version, entry.Manifest.Author, entry.Manifest.Licence,
                    entry.Manifest.Job ?? _strings.Get("addons.job.none"));
                var info = new Label { Name = "details", Text = details, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0) };
                info.AccessibilityName = info.Text;
                box.AddChild(info);
                var permissions = new Label { Name = "permissions", Text = _strings.Get("addons.permissions") + "\n" + PermissionList(entry.Manifest.Permissions),
                    AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = UiTheme.MutedLabel, CustomMinimumSize = new Vector2(300, 0) };
                permissions.AccessibilityName = permissions.Text;
                box.AddChild(permissions);
                if (entry.Note is { } note)
                {
                    var warn = new Label { Name = "note", Text = note, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0), ThemeTypeVariation = UiTheme.ErrorLabel };
                    warn.AccessibilityName = warn.Text;
                    box.AddChild(warn);
                }
                _list.AddChild(box);
            }
        }
        finally { _syncing = false; }
    }
}

/// <summary>Draws an add-on's window from its list of controls, in the viewer's own style, and passes the user's input back through the host.
/// A row of controls (the quick bar) is drawn the same way, side by side.</summary>
public sealed partial class AddonWindowView : BoxContainer
{
    private readonly StringTable _strings;
    private readonly AddonHost _host;
    private readonly string _addonId;
    private readonly Func<Guid, AddonDropped?>? _resolve;
    private readonly bool _row;

    /// <summary>Told what the user dropped on one of this window's drop zones, before the add-on hears of it (the viewer keeps which
    /// notecards the user handed to add-ons).</summary>
    public Action<IReadOnlyList<AddonDropped>>? HandedOver { get; init; }
    private AddonWindow? _window;
    private bool _syncing;
    private readonly List<Action> _unhook = [];

    /// <param name="resolve">What an inventory id dragged from the inventory window is (an animation or a folder); null for anything else.</param>
    /// <param name="row">Side by side (the quick bar) rather than one under another.</param>
    public AddonWindowView(StringTable strings, AddonHost host, string addonId, Func<Guid, AddonDropped?>? resolve = null, bool row = false)
    {
        _strings = strings;
        _host = host;
        _addonId = addonId;
        _resolve = resolve;
        _row = row;
        Vertical = !row;
        Name = row ? "addon_quick" : "addon_window";
        SizeFlagsHorizontal = row ? SizeFlags.ShrinkEnd : SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", row ? 4 : 8);
        Bind(null);
    }

    /// <summary>The add-on's window as it is now (a new one each time the add-on starts), or null while the add-on is off.</summary>
    public void Bind(AddonWindow? window)
    {
        if (_window is not null)
            _window.Changed -= Rebuild;
        _window = window;
        if (window is not null)
            window.Changed += Rebuild;
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var unhook in _unhook) unhook();   // the controls drawn before are freed: they must not hear the add-on any more
        _unhook.Clear();
        foreach (var child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
        if (_window is null)
        {
            if (!_row)
                AddChild(new Label { Name = "off", Text = _strings.Get("addons.window.off"), AutowrapMode = TextServer.AutowrapMode.WordSmart });
            return;
        }
        foreach (var control in _window.Controls)
            AddChild(Make(control));
    }

    private Control Make(AddonControl control)
    {
        Control made;
        switch (control)
        {
            case AddonToggle toggle:
            {
                var box = new CheckBox();
                box.Toggled += on => { if (!_syncing) _host.Run(_addonId, () => toggle.Turn(on)); };
                made = box;
                void Sync() { _syncing = true; box.Text = toggle.Text; box.ButtonPressed = toggle.On; box.Disabled = !toggle.Enabled; box.AccessibilityName = toggle.Text; _syncing = false; }
                toggle.Changed += Sync;
                _unhook.Add(() => toggle.Changed -= Sync);
                Sync();
                break;
            }
            case AddonButton button:
                made = MakeButton(button);
                break;
            case AddonButtonRow row:
            {
                var h = new HBoxContainer();
                foreach (var b in row.Buttons)
                {
                    var made1 = MakeButton(b);
                    made1.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                    h.AddChild(made1);
                }
                made = h;
                break;
            }
            case AddonSlider slider:
            {
                var h = new HBoxContainer();
                var label = new Label { CustomMinimumSize = new Vector2(_row ? 0 : 70, 0) };
                // The wheel scrolls the window: it does not move a slider passing under the pointer. The bar itself has no step: the
                // engine counts steps from the minimum and then clamps, so a maximum off the steps could not be reached. The add-on's
                // slider chooses the value and the bar is put where it says.
                var bar = new HSlider
                {
                    Name = "slider_" + slider.Id, MinValue = slider.Min, MaxValue = slider.Max, Step = 0, Scrollable = false, FocusMode = FocusModeEnum.All,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(_row ? 100 : 80, 16),
                };
                var number = new Label { Name = "number", CustomMinimumSize = new Vector2(36, 0), HorizontalAlignment = HorizontalAlignment.Right };
                string Shown() => slider.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                void Move(double to)
                {
                    _host.Run(_addonId, () => slider.Slide(to));
                    if (!GodotObject.IsInstanceValid(bar) || !GodotObject.IsInstanceValid(number))
                        return;   // the add-on redrew its window in answer
                    bar.SetValueNoSignal(slider.Value);
                    number.Text = Shown();
                }
                bar.ValueChanged += v => { if (!_syncing) Move(v); };
                bar.GuiInput += e =>
                {
                    if (!bar.Editable || e is not InputEventKey { Pressed: true } key)
                        return;
                    double? to = key.Keycode switch
                    {
                        Key.Left or Key.Down => AddonSliderSteps.Next(slider, -1),
                        Key.Right or Key.Up => AddonSliderSteps.Next(slider, 1),
                        Key.Home => slider.Min,
                        Key.End => slider.Max,
                        _ => null,
                    };
                    if (to is not { } next)
                        return;
                    bar.AcceptEvent();
                    Move(next);
                };
                h.AddChild(label);
                h.AddChild(bar);
                h.AddChild(number);
                made = h;
                void Sync()
                {
                    _syncing = true;
                    label.Text = slider.Text;
                    bar.AccessibilityName = slider.Text;
                    bar.Value = slider.Value;
                    ReverieStyle.SetEditable(bar, slider.Enabled);
                    number.Text = Shown();
                    _syncing = false;
                }
                slider.Changed += Sync;
                _unhook.Add(() => slider.Changed -= Sync);
                Sync();
                break;
            }
            case AddonDropZone zone:
            {
                var target = new AddonDropTarget(_resolve) { CustomMinimumSize = new Vector2(240, 28) };
                target.Dropped += dropped => { HandedOver?.Invoke(dropped); _host.Run(_addonId, () => zone.Drop(dropped)); };
                made = target;
                void Sync() { target.Label.Text = zone.Text; target.AccessibilityName = zone.Text; }
                zone.Changed += Sync;
                _unhook.Add(() => zone.Changed -= Sync);
                Sync();
                break;
            }
            case AddonTextField field:
            {
                var h = new HBoxContainer();
                var label = new Label { CustomMinimumSize = new Vector2(70, 0) };
                var edit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                edit.TextChanged += text => { if (!_syncing) field.Type(text); };
                // Enter and Esc in the field are the add-on's to answer (a name prompt makes or cancels on them)
                edit.SetMeta(ShellRoot.TakesEscapeMeta, true);
                edit.TextSubmitted += _ => _host.Run(_addonId, field.Submit);
                edit.GuiInput += e =>
                {
                    if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
                    {
                        edit.AcceptEvent();
                        _host.Run(_addonId, field.Cancel);
                    }
                };
                void TakeFocus() => edit.CallDeferred(Control.MethodName.GrabFocus);
                field.FocusRequested += TakeFocus;
                _unhook.Add(() => field.FocusRequested -= TakeFocus);
                h.AddChild(label);
                h.AddChild(edit);
                made = h;
                void Sync()
                {
                    _syncing = true;
                    label.Text = field.Text;
                    edit.AccessibilityName = field.Text;
                    if (edit.Text != field.Value) edit.Text = field.Value;
                    edit.Editable = field.Enabled;
                    _syncing = false;
                }
                field.Changed += Sync;
                _unhook.Add(() => field.Changed -= Sync);
                Sync();
                break;
            }
            case AddonChoice choice:
            {
                var h = new HBoxContainer();
                var label = new Label { CustomMinimumSize = new Vector2(_row ? 0 : 70, 0) };
                // in the quick bar a list keeps a fixed width, so a long set name cannot push the toolbar's buttons away
                var list = _row ? new OptionButton { FitToLongestItem = false, CustomMinimumSize = new Vector2(140, 0), ClipText = true }
                    : new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                list.ItemSelected += index => { if (!_syncing) _host.Run(_addonId, () => choice.Choose((int)index)); };
                h.AddChild(label);
                h.AddChild(list);
                made = h;
                void Sync()
                {
                    _syncing = true;
                    label.Text = choice.Text;
                    list.AccessibilityName = choice.Text;
                    list.Clear();
                    foreach (string item in choice.Items) list.AddItem(item);
                    if (choice.Items.Count == 0)
                        list.AddItem(_strings.Get("addons.choice.empty"));   // an empty list says so rather than showing a blank select
                    list.Selected = choice.Items.Count == 0 ? 0 : choice.Selected < choice.Items.Count ? choice.Selected : -1;
                    list.Disabled = !choice.Enabled || choice.Items.Count == 0;
                    _syncing = false;
                }
                choice.Changed += Sync;
                _unhook.Add(() => choice.Changed -= Sync);
                Sync();
                break;
            }
            default:
            {
                var label = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(_row ? 0 : 240, 0) };
                made = label;
                void Sync() { label.Text = control.Text; label.AccessibilityName = control.Text; }
                control.Changed += Sync;
                _unhook.Add(() => control.Changed -= Sync);
                Sync();
                break;
            }
        }
        made.Name = control.Id;
        return made;
    }

    private Button MakeButton(AddonButton button)
    {
        var b = new Button { Name = button.Id };
        b.Pressed += () => _host.Run(_addonId, button.Press);
        void Sync() { b.Text = button.Text; b.Disabled = !button.Enabled; b.AccessibilityName = button.Text; }
        button.Changed += Sync;
        _unhook.Add(() => button.Changed -= Sync);
        Sync();
        return b;
    }
}

/// <summary>A place inventory rows can be dropped on: the inventory window drags a row as its id, and the add-on is told what
/// each one is (an animation or a folder) by the viewer's own inventory. Anything else cannot be dropped.</summary>
public sealed partial class AddonDropTarget : PanelContainer
{
    private readonly Func<Guid, AddonDropped?>? _resolve;

    public AddonDropTarget(Func<Guid, AddonDropped?>? resolve)
    {
        _resolve = resolve;
        Label = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(Label);
        MouseFilter = MouseFilterEnum.Stop;
    }

    public Label Label { get; }

    public event Action<IReadOnlyList<AddonDropped>>? Dropped;

    private AddonDropped? Resolve(Variant data) =>
        data.VariantType == Variant.Type.String && Guid.TryParse(data.AsString(), out var id) ? _resolve?.Invoke(id) : null;

    public override bool _CanDropData(Vector2 atPosition, Variant data) => Resolve(data) is not null;

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (Resolve(data) is { } dropped)
            Dropped?.Invoke([dropped]);
    }
}
