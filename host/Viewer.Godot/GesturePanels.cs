// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Viewer.Core.Inventory;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The gesture editor, after the official gesture window (indra/newview/llpreviewgesture.cpp, floater_preview_gesture.xml): the trigger,
/// the replacement text and the shortcut key with its modifier; a library of step kinds (Animation, Sound, Chat, Wait) with "Add &gt;&gt;";
/// the steps in order with Up, Down and Remove; the chosen step's options (an animation to start or stop, a sound, a line of chat, a wait
/// for a time or until the animations are done); Active, Preview (plays the gesture as it stands on the user) and Save. One window, reused.
/// No layout file or artwork of any viewer is used.
/// </summary>
public partial class GesturePanel : VBoxContainer
{
    public const string FloaterId = "gesture";

    private readonly StringTable _s;
    private GestureDocument _doc = new();
    private bool _syncing, _dirty, _canEdit = true;
    private IReadOnlyList<(Guid Id, string Name)> _animations = [];
    private IReadOnlyList<(Guid Id, string Name)> _sounds = [];

    public LineEdit TriggerField { get; }
    public LineEdit ReplaceField { get; }
    public OptionButton ModifierChoice { get; }
    public OptionButton KeyChoice { get; }
    public ItemList Library { get; }
    public Button AddButton { get; }
    public ItemList StepList { get; }
    public Button UpButton { get; }
    public Button DownButton { get; }
    public Button RemoveButton { get; }
    public VBoxContainer Options { get; }
    public OptionButton AnimationChoice { get; }
    public CheckBox AnimStart { get; }
    public CheckBox AnimStop { get; }
    public OptionButton SoundChoice { get; }
    public LineEdit ChatField { get; }
    public CheckBox WaitAnims { get; }
    public CheckBox WaitTime { get; }
    public SpinBox WaitSeconds { get; }
    public CheckBox ActiveCheck { get; }
    public Button PreviewButton { get; }
    public Button SaveButton { get; }
    public Label StatusLabel { get; }

    /// <summary>Save pressed: the gesture as it stands.</summary>
    public Action<GestureDocument>? SaveRequested;
    /// <summary>Preview pressed: the gesture as it stands, to play on the user.</summary>
    public Action<GestureDocument>? PreviewRequested;
    /// <summary>Active ticked or cleared.</summary>
    public Action<bool>? ActiveChanged;

    public GestureDocument Document => _doc;
    public bool IsDirty => _dirty;

    public GesturePanel(StringTable strings)
    {
        _s = strings;
        Name = "gesture_panel";
        AccessibilityName = strings.Get("gesture.panel");

        var grid = new GridContainer { Name = "fields", Columns = 2 };
        TriggerField = Field(grid, "trigger", "gesture.trigger");
        ReplaceField = Field(grid, "replace", "gesture.replace");
        grid.AddChild(new Label { Text = strings.Get("gesture.key") });
        var keyRow = new HBoxContainer { Name = "key_row", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ModifierChoice = new OptionButton { Name = "modifier" };
        ModifierChoice.AddItem(strings.Get("gesture.modifier.none"), (int)GestureKeys.MaskNone);
        ModifierChoice.AddItem(strings.Get("gesture.modifier.shift"), (int)GestureKeys.MaskShift);
        ModifierChoice.AddItem(strings.Get("gesture.modifier.ctrl"), (int)GestureKeys.MaskControl);
        ModifierChoice.AccessibilityName = strings.Get("gesture.key");
        ModifierChoice.ItemSelected += _ => FromControls();
        keyRow.AddChild(ModifierChoice);
        KeyChoice = new OptionButton { Name = "key" };
        KeyChoice.AddItem(strings.Get("gesture.key.none"), GestureKeys.None);
        foreach (var k in GestureKeys.Offered)
            KeyChoice.AddItem(GestureKeys.Label(k), k);
        KeyChoice.AccessibilityName = strings.Get("gesture.key");
        KeyChoice.ItemSelected += _ => FromControls();
        keyRow.AddChild(KeyChoice);
        grid.AddChild(keyRow);
        AddChild(grid);

        var lists = new HBoxContainer { Name = "lists", SizeFlagsVertical = SizeFlags.ExpandFill };
        var left = new VBoxContainer { Name = "library_box", CustomMinimumSize = new Vector2(120, 0) };
        left.AddChild(new Label { Text = strings.Get("gesture.library") });
        Library = new ItemList { Name = "library", SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (var k in new[] { "animation", "sound", "chat", "wait" })
            Library.AddItem(strings.Get("gesture.library." + k));
        Library.Select(0);
        Library.AccessibilityName = strings.Get("gesture.library");
        Library.ItemActivated += _ => AddStep();
        left.AddChild(Library);
        AddButton = Btn("add", "gesture.add", AddStep);
        left.AddChild(AddButton);
        lists.AddChild(left);

        var right = new VBoxContainer { Name = "steps_box", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddChild(new Label { Text = strings.Get("gesture.steps") });
        StepList = new ItemList { Name = "steps", SizeFlagsVertical = SizeFlags.ExpandFill };
        StepList.AccessibilityName = strings.Get("gesture.steps");
        StepList.ItemSelected += _ => ShowOptions();
        right.AddChild(StepList);
        var stepButtons = new HBoxContainer { Name = "step_buttons" };
        UpButton = Btn("up", "gesture.up", () => Move(-1));
        DownButton = Btn("down", "gesture.down", () => Move(1));
        RemoveButton = Btn("remove", "gesture.remove", RemoveStep);
        stepButtons.AddChild(UpButton);
        stepButtons.AddChild(DownButton);
        stepButtons.AddChild(RemoveButton);
        right.AddChild(stepButtons);
        lists.AddChild(right);
        AddChild(lists);

        Options = new VBoxContainer { Name = "options" };
        var animRow = new HBoxContainer { Name = "anim_row" };
        AnimationChoice = new OptionButton { Name = "animation", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AnimationChoice.AccessibilityName = strings.Get("gesture.anim.pick");
        AnimationChoice.ItemSelected += _ => FromOptions();
        animRow.AddChild(AnimationChoice);
        var startStop = new ButtonGroup();
        AnimStart = new CheckBox { Name = "anim_start", Text = strings.Get("gesture.anim.start"), ButtonGroup = startStop, ButtonPressed = true };
        AnimStop = new CheckBox { Name = "anim_stop", Text = strings.Get("gesture.anim.stop"), ButtonGroup = startStop };
        AnimStart.Toggled += _ => FromOptions();
        AnimStop.Toggled += _ => FromOptions();
        animRow.AddChild(AnimStart);
        animRow.AddChild(AnimStop);
        Options.AddChild(animRow);
        SoundChoice = new OptionButton { Name = "sound", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        SoundChoice.AccessibilityName = strings.Get("gesture.sound.pick");
        SoundChoice.ItemSelected += _ => FromOptions();
        Options.AddChild(SoundChoice);
        ChatField = new LineEdit { Name = "chat", PlaceholderText = strings.Get("gesture.chat.text"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ChatField.AccessibilityName = ChatField.PlaceholderText;
        ChatField.TextChanged += _ => FromOptions();
        Options.AddChild(ChatField);
        var waitRow = new HBoxContainer { Name = "wait_row" };
        WaitAnims = new CheckBox { Name = "wait_anims", Text = strings.Get("gesture.wait.anims") };
        WaitAnims.Toggled += on => { if (!_syncing) { _syncing = true; WaitTime!.ButtonPressed = !on; _syncing = false; FromOptions(); } };
        waitRow.AddChild(WaitAnims);
        WaitTime = new CheckBox { Name = "wait_time", Text = strings.Get("gesture.wait.time") };
        WaitTime.Toggled += on => { if (!_syncing) { _syncing = true; WaitAnims.ButtonPressed = !on; _syncing = false; FromOptions(); } };
        waitRow.AddChild(WaitTime);
        WaitSeconds = new SpinBox { Name = "wait_seconds", MinValue = 0, MaxValue = 3600, Step = 0.1, Value = 1 };
        WaitSeconds.ValueChanged += _ => FromOptions();
        waitRow.AddChild(WaitSeconds);
        Options.AddChild(waitRow);
        AddChild(Options);

        AddChild(new Label { Name = "help", Text = strings.Get("gesture.help"), ThemeTypeVariation = UiTheme.MutedLabel, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(200, 0) });

        var foot = new HBoxContainer { Name = "foot" };
        ActiveCheck = new CheckBox { Name = "active", Text = strings.Get("gesture.active") };
        ActiveCheck.AccessibilityName = ActiveCheck.Text;
        ActiveCheck.Toggled += on => { if (!_syncing) ActiveChanged?.Invoke(on); };
        foot.AddChild(ActiveCheck);
        StatusLabel = new Label { Name = "status", ThemeTypeVariation = UiTheme.MutedLabel, SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(80, 0) };
        foot.AddChild(StatusLabel);
        PreviewButton = Btn("preview", "gesture.preview", () => PreviewRequested?.Invoke(_doc));
        foot.AddChild(PreviewButton);
        SaveButton = Btn("save", "gesture.save", () => SaveRequested?.Invoke(_doc));
        SaveButton.ThemeTypeVariation = UiTheme.PrimaryButton;
        foot.AddChild(SaveButton);
        AddChild(foot);
        Sync();
    }

    private LineEdit Field(GridContainer grid, string name, string key)
    {
        grid.AddChild(new Label { Text = _s.Get(key) });
        var f = new LineEdit { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        f.AccessibilityName = _s.Get(key);
        f.TextChanged += _ => FromControls();
        grid.AddChild(f);
        return f;
    }

    private Button Btn(string name, string key, Action pressed)
    {
        var b = new Button { Name = name, Text = _s.Get(key) };
        b.AccessibilityName = b.Text;
        b.Pressed += pressed;
        return b;
    }

    /// <summary>Shows a gesture: <paramref name="animations"/> and <paramref name="sounds"/> are what the pickers offer (the inventory's own).</summary>
    public void Show(GestureDocument doc, bool active, bool canEdit, IReadOnlyList<(Guid Id, string Name)> animations, IReadOnlyList<(Guid Id, string Name)> sounds)
    {
        _doc = doc;
        _canEdit = canEdit;
        _animations = animations;
        _sounds = sounds;
        _syncing = true;
        TriggerField.Text = doc.Trigger;
        ReplaceField.Text = doc.Replace;
        ModifierChoice.Select(Math.Max(0, ModifierChoice.GetItemIndex((int)doc.Mask)));
        KeyChoice.Select(Math.Max(0, KeyChoice.GetItemIndex(doc.Key)));
        ActiveCheck.ButtonPressed = active;
        _syncing = false;
        _dirty = false;
        StepList.DeselectAll();
        Sync();
        if (doc.Steps.Count > 0)
        {
            StepList.Select(0);
            ShowOptions();
        }
        StatusLabel.Text = "";
    }

    public void ShowStatus(string key) => StatusLabel.Text = _s.Get(key);

    public void MarkSaved()
    {
        _dirty = false;
        Sync();
    }

    public void SetActive(bool on)
    {
        _syncing = true;
        ActiveCheck.ButtonPressed = on;
        _syncing = false;
    }

    private void FromControls()
    {
        if (_syncing)
            return;
        _doc.Trigger = TriggerField.Text.Trim();
        _doc.Replace = ReplaceField.Text;
        uint mask = (uint)ModifierChoice.GetSelectedId();
        byte key = (byte)KeyChoice.GetSelectedId();
        if (!GestureKeys.Allowed(key, mask))
        {
            // Ctrl+F10 is not offered (llpreviewgesture.cpp disables it): the modifier goes back to none
            mask = GestureKeys.MaskNone;
            _syncing = true;
            ModifierChoice.Select(0);
            _syncing = false;
        }
        _doc.Key = key;
        _doc.Mask = mask;
        Changed();
    }

    private void Changed()
    {
        _dirty = true;
        Sync();
    }

    /// <summary>Adds a step of the library's chosen kind after the chosen step, or at the end (llpreviewgesture.cpp onClickAdd).</summary>
    public void AddStep()
    {
        if (!_canEdit)
            return;
        int kind = Library.GetSelectedItems() is { Length: > 0 } sel ? sel[0] : 0;
        var step = kind switch
        {
            0 => GestureStep.Animation(_animations.FirstOrDefault().Name ?? "", _animations.FirstOrDefault().Id, false),
            1 => GestureStep.Sound(_sounds.FirstOrDefault().Name ?? "", _sounds.FirstOrDefault().Id),
            2 => GestureStep.Chat(""),
            _ => GestureStep.WaitTime(1f),
        };
        int at = StepList.GetSelectedItems() is { Length: > 0 } s ? s[0] + 1 : _doc.Steps.Count;
        _doc.Steps.Insert(at, step);
        Changed();
        StepList.Select(at);
        ShowOptions();
    }

    private int Selected => StepList.GetSelectedItems() is { Length: > 0 } s ? s[0] : -1;

    private void Move(int by)
    {
        int i = Selected;
        if (i < 0 || !(by < 0 ? _doc.MoveUp(i) : _doc.MoveDown(i)))
            return;
        Changed();
        StepList.Select(i + by);
        ShowOptions();
    }

    private void RemoveStep()
    {
        int i = Selected;
        if (i < 0 || !_doc.Remove(i))
            return;
        Changed();
        if (_doc.Steps.Count > 0)
            StepList.Select(Math.Min(i, _doc.Steps.Count - 1));
        ShowOptions();
    }

    /// <summary>The options of the chosen step.</summary>
    private void ShowOptions()
    {
        int i = Selected;
        var step = i >= 0 && i < _doc.Steps.Count ? _doc.Steps[i] : null;
        _syncing = true;
        Options.GetNode<Control>("anim_row").Visible = step?.Kind == GestureStepKind.Animation;
        SoundChoice.Visible = step?.Kind == GestureStepKind.Sound;
        ChatField.Visible = step?.Kind == GestureStepKind.Chat;
        Options.GetNode<Control>("wait_row").Visible = step?.Kind == GestureStepKind.Wait;
        if (step is not null)
        {
            Fill(AnimationChoice, _animations, step.Kind == GestureStepKind.Animation ? (step.AssetId, step.Name) : default);
            Fill(SoundChoice, _sounds, step.Kind == GestureStepKind.Sound ? (step.AssetId, step.Name) : default);
            AnimStart.ButtonPressed = !step.Stops;
            AnimStop.ButtonPressed = step.Stops;
            ChatField.Text = step.Text;
            WaitAnims.ButtonPressed = step.WaitsForAnimations;
            WaitTime.ButtonPressed = step.WaitsForTime;
            WaitSeconds.Value = step.Seconds;
        }
        _syncing = false;
        Sync();
    }

    private static void Fill(OptionButton choice, IReadOnlyList<(Guid Id, string Name)> items, (Guid Id, string Name) current)
    {
        choice.Clear();
        var all = items.ToList();
        if (current.Id != Guid.Empty && !all.Any(a => a.Id == current.Id))
            all.Insert(0, current);   // a step naming something not in the inventory keeps it
        foreach (var (_, name) in all)
            choice.AddItem(name);
        choice.SetMeta("ids", string.Join(",", all.Select(a => a.Id.ToString("N"))));
        int at = all.FindIndex(a => a.Id == current.Id);
        if (at >= 0)
            choice.Select(at);
    }

    private static (Guid Id, string Name) Picked(OptionButton choice)
    {
        int i = choice.Selected;
        string[] ids = choice.HasMeta("ids") ? choice.GetMeta("ids").AsString().Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        return i >= 0 && i < ids.Length ? (Guid.ParseExact(ids[i], "N"), choice.GetItemText(i)) : (Guid.Empty, "");
    }

    /// <summary>The chosen step from its options.</summary>
    private void FromOptions()
    {
        if (_syncing || Selected is var i && (i < 0 || i >= _doc.Steps.Count))
            return;
        var step = _doc.Steps[i];
        _doc.Steps[i] = step.Kind switch
        {
            GestureStepKind.Animation when Picked(AnimationChoice) is var a => GestureStep.Animation(a.Name, a.Id, AnimStop.ButtonPressed),
            GestureStepKind.Sound when Picked(SoundChoice) is var snd => GestureStep.Sound(snd.Name, snd.Id),
            GestureStepKind.Chat => GestureStep.Chat(ChatField.Text),
            _ => WaitAnims.ButtonPressed ? GestureStep.WaitAnimations() : GestureStep.WaitTime((float)WaitSeconds.Value),
        };
        Changed();
    }

    /// <summary>The step list's labels (llpreviewgesture.cpp getLabel) and the buttons' state.</summary>
    private void Sync()
    {
        int sel = Selected;
        var labels = _doc.Steps.Select(Label).ToArray();
        if (StepList.ItemCount != labels.Length || Enumerable.Range(0, labels.Length).Any(i => StepList.GetItemText(i) != labels[i]))
        {
            StepList.Clear();
            foreach (var l in labels)
                StepList.AddItem(l);
            if (sel >= 0 && sel < labels.Length)
                StepList.Select(sel);
        }
        sel = Selected;
        TriggerField.Editable = ReplaceField.Editable = _canEdit;
        ModifierChoice.Disabled = KeyChoice.Disabled = AddButton.Disabled = !_canEdit;
        UpButton.Disabled = !_canEdit || sel <= 0;
        DownButton.Disabled = !_canEdit || sel < 0 || sel >= _doc.Steps.Count - 1;
        RemoveButton.Disabled = !_canEdit || sel < 0;
        SaveButton.Visible = _canEdit;
        SaveButton.Disabled = !_dirty;
    }

    private string Label(GestureStep s) => s.Kind switch
    {
        GestureStepKind.Animation => _s.Get(s.Stops ? "gesture.step.stop" : "gesture.step.start", s.Name),
        GestureStepKind.Sound => _s.Get("gesture.step.sound", s.Name),
        GestureStepKind.Chat => _s.Get("gesture.step.chat", s.Text),
        _ => s.WaitsForAnimations ? _s.Get("gesture.step.wait_anims") : _s.Get("gesture.step.wait_time", s.Seconds.ToString("0.##", CultureInfo.CurrentCulture)),
    };
}
