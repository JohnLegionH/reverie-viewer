// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Appearance;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Appearance;

namespace Viewer.Godot;

/// <summary>Adds the Appearance window to the shell: a floater, an entry in the Me menu and a toolbar button.</summary>
public static class AppearanceFeature
{
    public const string FloaterId = "appearance";

    public static AppearancePanel Install(ShellRoot shell, long redrawGapMs = 250)
    {
        var s = shell.Shell;
        var panel = new AppearancePanel(s.Strings, redrawGapMs);
        shell.AddFloater(new FloaterSpec(FloaterId, "appearance.title", new UiSize(470, 640), new UiSize(380, 460)), panel);
        s.Commands.Register("floater.appearance", () => s.Floaters.Toggle(FloaterId));
        s.Menus.AddEntry("me", new MenuEntry("me.appearance", "menu.me.appearance", "floater.appearance", Order: 5));
        s.Toolbar.Add(new ToolbarButton("toolbar_appearance", "toolbar.appearance", "floater.appearance", UiIcons.Account, FloaterId));
        s.Floaters.SetCloseGuard(FloaterId, () => panel.MayClose(() => s.Floaters.Close(FloaterId, force: true)));
        return panel;
    }
}

/// <summary>
/// The Appearance window: an Outfit tab (what is worn, take off, wear, save an outfit: the outfit window's own panel) and an Edit
/// tab (pick something worn, change its sliders, textures and colours, Save, Save As, Revert). It draws <see cref="AppearanceEditing"/>
/// and hands every action to it; saving goes through the action pipeline and the grid rebuilds the look. Sliders are made when a group is
/// opened. Behaviour follows the official viewer's appearance editor (indra/newview/llpaneleditwearable.cpp); layout and artwork are this
/// viewer's own.
/// </summary>
public partial class AppearancePanel : VBoxContainer
{
    private readonly StringTable _strings;
    private static ThemeSpec _theme => UiTheme.Spec;
    private readonly ConcurrentQueue<WearableSaveFinished> _finished = new();
    private readonly HashSet<string> _openGroups = new();
    private readonly Dictionary<int, HSlider> _sliders = new();
    private readonly Dictionary<int, Label> _sliderValues = new();
    private readonly Dictionary<int, Button> _slotButtons = new();
    private readonly Dictionary<ColourSwatch, ColourButton> _swatches = new();
    private InventoryModel? _model;
    private Func<Guid, Guid> _textureAssetOf = _ => Guid.Empty;
    private Func<Guid, bool>? _textureUsable;
    private Func<Guid, Task<Viewer.Core.Imaging.DecodedTexture?>>? _textureImage;
    private readonly Dictionary<Guid, ImageTexture> _swatchImages = new();
    private Action? _afterDecision, _afterSave;
    private bool _syncing, _dirty = true, _rebuild = true;
    private Guid _builtFor;
    private TexturePickerPopup? _picker;

    public TabContainer Tabs { get; }
    public OutfitPanel Outfit { get; }
    public AppearanceEditing? Editing { get; private set; }
    public ItemList WornList { get; }
    public Label PickLabel { get; }
    public Label NoticeLabel { get; }
    public Label StatusLabel { get; }
    public LineEdit NameField { get; }
    public VBoxContainer Body { get; }
    public ScrollContainer BodyScroll { get; }
    public Button SaveButton { get; }
    public Button SaveAsButton { get; }
    public LineEdit SaveAsName { get; }
    public Button RevertButton { get; }
    public PanelContainer UnsavedPrompt { get; }
    public Button PromptSave { get; }
    public Button PromptDiscard { get; }
    public Button PromptCancel { get; }

    public AppearancePanel(StringTable strings, long redrawGapMs = 250)
    {
        _strings = strings;
        Name = "appearance_panel";
        Tabs = new TabContainer { Name = "appearance_tabs", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Tabs.AccessibilityName = strings.Get("appearance.title");
        AddChild(Tabs);

        Outfit = new OutfitPanel(strings, redrawGapMs) { Name = "outfit_tab" };
        Tabs.AddChild(Outfit);

        var edit = new VBoxContainer { Name = "edit_tab" };
        Tabs.AddChild(edit);
        Tabs.SetTabTitle(0, strings.Get("appearance.tab.outfit"));
        Tabs.SetTabTitle(1, strings.Get("appearance.tab.edit"));

        edit.AddChild(Header("worn_header", "appearance.worn"));
        WornList = new ItemList { Name = "worn_list", CustomMinimumSize = new Vector2(0, 96), SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.All, SelectMode = ItemList.SelectModeEnum.Single };
        WornList.AccessibilityName = strings.Get("appearance.worn.list");
        WornList.ItemSelected += OnWornSelected;
        edit.AddChild(WornList);

        PickLabel = ChatFeature.Note("pick", strings.Get("appearance.pick"), _theme);
        PickLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        edit.AddChild(PickLabel);
        NoticeLabel = ChatFeature.Note("notice", strings.Get("appearance.title"), _theme);
        NoticeLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        NoticeLabel.ThemeTypeVariation = UiTheme.ErrorLabel;
        NoticeLabel.Text = string.Empty;
        edit.AddChild(NoticeLabel);

        var nameRow = new HBoxContainer { Name = "name_row" };
        nameRow.AddChild(Header("name_label", "appearance.name"));
        NameField = new LineEdit { Name = "wearable_name", SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = 63 };
        NameField.AccessibilityName = strings.Get("appearance.name");
        NameField.TextChanged += t => { if (!_syncing && Editing?.Editor is { } e) e.Name = t; };
        nameRow.AddChild(NameField);
        edit.AddChild(nameRow);

        BodyScroll = new ScrollContainer { Name = "edit_scroll", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        Body = new VBoxContainer { Name = "edit_body", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        BodyScroll.AddChild(Body);
        edit.AddChild(BodyScroll);

        UnsavedPrompt = new PanelContainer { Name = "unsaved_prompt", Visible = false };
        var promptBox = new VBoxContainer();
        promptBox.AddChild(Header("unsaved_title", "appearance.unsaved.title"));
        var promptText = ChatFeature.Note("unsaved_body", strings.Get("appearance.unsaved.body"), _theme);
        promptText.Text = strings.Get("appearance.unsaved.body");
        promptText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        promptBox.AddChild(promptText);
        var promptRow = new HBoxContainer();
        PromptSave = Button("unsaved_save", "appearance.unsaved.save", OnPromptSave);
        PromptDiscard = Button("unsaved_discard", "appearance.unsaved.discard", OnPromptDiscard);
        PromptCancel = Button("unsaved_cancel", "appearance.unsaved.cancel", OnPromptCancel);
        promptRow.AddChild(PromptSave);
        promptRow.AddChild(PromptDiscard);
        promptRow.AddChild(PromptCancel);
        promptBox.AddChild(promptRow);
        UnsavedPrompt.AddChild(promptBox);
        edit.AddChild(UnsavedPrompt);

        var saveRow = new HBoxContainer { Name = "save_row" };
        SaveButton = Button("save", "appearance.save", () => Editing?.Save());
        RevertButton = Button("revert", "appearance.revert", OnRevert);
        saveRow.AddChild(SaveButton);
        saveRow.AddChild(RevertButton);
        edit.AddChild(saveRow);
        var asRow = new HBoxContainer { Name = "save_as_row" };
        SaveAsName = new LineEdit { Name = "save_as_name", PlaceholderText = strings.Get("appearance.save_as.name"), SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = 63 };
        SaveAsName.AccessibilityName = strings.Get("appearance.save_as.name");
        SaveAsName.TextChanged += _ => _dirty = true;
        asRow.AddChild(SaveAsName);
        SaveAsButton = Button("save_as", "appearance.save_as", OnSaveAs);
        asRow.AddChild(SaveAsButton);
        edit.AddChild(asRow);
        StatusLabel = ChatFeature.Note("status", strings.Get("appearance.title"), _theme);
        StatusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        StatusLabel.Text = strings.Get("appearance.status.clean");
        edit.AddChild(StatusLabel);
        // in the smallest window at a large interface size the Edit tab is taller than the window, so the rest of the tab gives up its room while the question shows
        UnsavedQuestion.GiveUpRoom(UnsavedPrompt, edit.GetChildren().OfType<Control>().Where(c => c != UnsavedPrompt).ToArray());
        Refresh();
    }

    private Label Header(string name, string key)
    {
        var l = new Label { Name = name, Text = _strings.Get(key) };
        l.AccessibilityName = _strings.Get(key);
        return l;
    }

    private Button Button(string name, string key, Action pressed)
    {
        var b = new Button { Name = name, Text = _strings.Get(key) };
        b.AccessibilityName = _strings.Get(key);
        b.Pressed += pressed;
        return b;
    }

    // ---------------- binding ----------------

    /// <summary>Shows a session's appearance (null model: no session; the window is empty and its buttons are off).</summary>
    public void Bind(InventoryModel? model, Func<ActionIntent, ActionResult>? submit, Func<Guid, WearableItemInfo?>? itemInfo = null,
        Func<Guid, int, CancellationToken, Task<AssetFetchResult>>? fetch = null, EditParamCatalog? catalog = null, Func<bool>? isMale = null,
        Func<Guid, Guid>? textureAssetOf = null, Action<IReadOnlyDictionary<int, float>?>? onPreview = null, Func<Guid, bool>? textureUsable = null,
        Func<Guid, Task<Viewer.Core.Imaging.DecodedTexture?>>? textureImage = null)
    {
        if (Editing is { } old)
        {
            old.Changed -= MarkDirty;
            old.DiscardAndClose();
        }
        if (_model is not null)
            _model.Changed -= MarkDirty;
        Outfit.Bind(model, submit);
        _model = model;
        _textureAssetOf = textureAssetOf ?? (_ => Guid.Empty);
        _textureUsable = textureUsable;
        _textureImage = textureImage;
        _swatchImages.Clear();
        _afterDecision = _afterSave = null;
        UnsavedPrompt.Visible = false;
        if (model is null || submit is null)
        {
            Editing = null;
        }
        else
        {
            Editing = new AppearanceEditing(model, itemInfo ?? (_ => null), fetch ?? ((_, _, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Failed))), catalog,
                isMale ?? (() => false), submit);
            Editing.Changed += MarkDirty;
            if (onPreview is not null)
                Editing.PreviewChanged += onPreview;
            model.Changed += MarkDirty;
        }
        _builtFor = Guid.Empty;
        _rebuild = _dirty = true;
        Refresh();
    }

    private void MarkDirty() => _dirty = true;

    /// <summary>Draws the window's contents now (the entry the start-up check and the render cases use).</summary>
    public void Rebuild()
    {
        _dirty = false;
        RebuildBody();
        Refresh();
    }

    /// <summary>A wearable save has finished on the session (any thread): taken on the next frame.</summary>
    public void SaveFinished(WearableSaveFinished done) => _finished.Enqueue(done);

    public override void _Process(double delta)
    {
        while (_finished.TryDequeue(out var done))
            Editing?.OnSaveFinished(done);
        if (!_dirty && !_rebuild)
            return;
        if (Editing is { State: EditState.Ready or EditState.Closed } && _rebuild)
            RebuildBody();
        _dirty = false;
        Refresh();
    }

    // ---------------- the worn list ----------------

    private IReadOnlyList<OutfitEntry> _worn = [];

    private void RefreshWorn()
    {
        var worn = Editing?.Worn() ?? [];
        if (worn.Select(w => (w.TargetId, w.Name)).SequenceEqual(_worn.Select(w => (w.TargetId, w.Name))) && WornList.ItemCount == worn.Count)
            return;
        _worn = worn;
        _syncing = true;
        WornList.Clear();
        int select = -1;
        for (int i = 0; i < worn.Count; i++)
        {
            WornList.AddItem(worn[i].Name);
            WornList.SetItemTooltip(i, _strings.Get("appearance.type." + WearableKinds.Name(worn[i].Type)));
            if (Editing is { } ed && ed.ItemId == worn[i].TargetId && ed.State != EditState.Closed)
                select = i;
        }
        if (select >= 0) WornList.Select(select);
        _syncing = false;
    }

    private void OnWornSelected(long index)
    {
        if (_syncing || Editing is not { } ed || index < 0 || index >= _worn.Count)
            return;
        var target = _worn[(int)index].TargetId;
        if (target == ed.ItemId && ed.State == EditState.Ready)
            return;
        Guarded(() => _ = OpenAsync(target), () => RefreshWornSelection());
    }

    private void RefreshWornSelection()
    {
        _syncing = true;
        WornList.DeselectAll();
        if (Editing is { State: not EditState.Closed } ed)
        {
            int at = _worn.ToList().FindIndex(w => w.TargetId == ed.ItemId);
            if (at >= 0) WornList.Select(at);
        }
        _syncing = false;
    }

    /// <summary>Opens a worn wearable for editing (also the entry the start-up check uses).</summary>
    public async Task OpenAsync(Guid itemId)
    {
        if (Editing is not { } ed)
            return;
        _openGroups.Clear();
        _builtFor = Guid.Empty;
        await ed.OpenAsync(itemId);
        _rebuild = _dirty = true;
    }

    // ---------------- asking before leaving unsaved changes ----------------

    /// <summary>Runs <paramref name="proceed"/> now when nothing is unsaved; otherwise asks Save, Don't save or Cancel first and runs it after Save or Don't save.</summary>
    private void Guarded(Action proceed, Action? cancelled = null)
    {
        if (Editing is not { HasUnsavedChanges: true })
        {
            proceed();
            return;
        }
        _afterDecision = proceed;
        _afterCancel = cancelled;
        UnsavedPrompt.Visible = true;
        Tabs.CurrentTab = 1;
    }

    private Action? _afterCancel;
    private string _stateNotice = "";

    /// <summary>The window is being closed: true when it may close now; false when the question was asked (<paramref name="closeWindow"/> runs after the answer).</summary>
    public bool MayClose(Action closeWindow)
    {
        if (Editing is not { HasUnsavedChanges: true } ed)
        {
            Editing?.RequestClose();
            return true;
        }
        Guarded(closeWindow);
        return false;
    }

    private void OnPromptSave()
    {
        if (Editing is not { } ed)
            return;
        UnsavedPrompt.Visible = false;
        if (!ed.Save())
        {
            UnsavedPrompt.Visible = true;   // it could not be sent: the edits and the question stay
            _dirty = true;
            return;
        }
        _afterSave = _afterDecision;
        _afterDecision = null;
        _afterCancel = null;
        _dirty = true;
    }

    private void OnPromptDiscard()
    {
        UnsavedPrompt.Visible = false;
        Editing?.DiscardAndClose();
        var after = _afterDecision;
        _afterDecision = _afterCancel = null;
        _builtFor = Guid.Empty;
        _rebuild = _dirty = true;
        after?.Invoke();
    }

    private void OnPromptCancel()
    {
        UnsavedPrompt.Visible = false;
        var cancelled = _afterCancel;
        _afterDecision = _afterCancel = null;
        cancelled?.Invoke();
        _dirty = true;
    }

    private void OnRevert()
    {
        Editing?.Editor?.Revert();
        _rebuild = _dirty = true;
        _builtFor = Guid.Empty;
    }

    private void OnSaveAs() => Editing?.SaveAs(SaveAsName.Text);

    // ---------------- the body: sliders, textures, colours ----------------

    private void RebuildBody()
    {
        _rebuild = false;
        foreach (var c in Body.GetChildren())
        {
            Body.RemoveChild(c);
            c.QueueFree();
        }
        _sliders.Clear();
        _sliderValues.Clear();
        _slotButtons.Clear();
        _swatches.Clear();
        if (Editing is not { State: EditState.Ready, Editor: { } editor } ed)
        {
            _builtFor = Guid.Empty;
            return;
        }
        _builtFor = ed.ItemId;
        var type = editor.Type;
        if (!editor.CanModify)
            Body.AddChild(Wrapped("readonly", _strings.Get("appearance.readonly")));
        else if (!ed.CanSaveAs)
            Body.AddChild(Wrapped("nocopy", _strings.Get("appearance.nocopy")));
        var tabs = ed.Catalog.Subparts(type, ed.IsMale);
        if (tabs.Count == 0 && ed.Catalog.ParamsOf(type).Count == 0 && WearableKinds.Slots(type).Count + WearableKinds.Swatches(type).Count > 0)
            Body.AddChild(Wrapped("nodef", _strings.Get("appearance.nodef")));
        if (_openGroups.Count == 0 && tabs.Count > 0)
            _openGroups.Add(tabs[0].EditGroup);

        foreach (var swatch in WearableKinds.Swatches(type))
            AddSwatch(editor, swatch);
        foreach (var slot in WearableKinds.Slots(type))
            AddSlot(editor, slot);
        if (WearableKinds.Slots(type).Count + WearableKinds.Swatches(type).Count > 0)
            Body.AddChild(Wrapped("preview_note", _strings.Get("appearance.preview.note")));
        foreach (var tab in tabs)
            AddGroup(editor, tab);
    }

    private Label Wrapped(string name, string text)
    {
        var l = ChatFeature.Note(name, text, _theme);
        l.Text = text;
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return l;
    }

    private string GroupTitle(string group) => _strings.Has("appearance.group." + group) ? _strings.Get("appearance.group." + group) : group;

    private string SlotTitle(string slot) => _strings.Has("appearance.slot." + slot) ? _strings.Get("appearance.slot." + slot) : slot;

    private void AddGroup(WearableEditor editor, EditSubpart tab)
    {
        bool open = _openGroups.Contains(tab.EditGroup);
        // a collapsible section header: flat, a chevron (down when open, right when shut), the label in weight 600, a thin divider under it
        var header = new Button
        {
            Name = "group_" + tab.EditGroup, Text = GroupTitle(tab.EditGroup), ToggleMode = true, ButtonPressed = open, Alignment = HorizontalAlignment.Left,
            ThemeTypeVariation = ReverieStyle.SectionHeader, Icon = ReverieIcons.Get(open ? LucideIcons.Down : LucideIcons.Submenu),
        };
        header.AccessibilityName = _strings.Get("appearance.group.more", GroupTitle(tab.EditGroup));
        header.Toggled += on =>
        {
            if (on) _openGroups.Add(tab.EditGroup); else _openGroups.Remove(tab.EditGroup);
            _rebuild = _dirty = true;
        };
        Body.AddChild(header);
        Body.AddChild(new HSeparator { Name = "group_" + tab.EditGroup + "_divider", ThemeTypeVariation = ReverieStyle.SectionDivider, MouseFilter = MouseFilterEnum.Ignore });
        if (!open)
            return;
        foreach (var p in tab.Params)
            AddSlider(editor, p);
    }

    private void AddSlider(WearableEditor editor, EditParam p)
    {
        var box = new VBoxContainer { Name = "param_" + p.Id };
        var top = new HBoxContainer();
        var name = new Label { Text = p.Label, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        name.AccessibilityName = p.Label;
        var value = new Label { Text = p.Id.ToString(CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(44, 0) };
        value.AccessibilityName = _strings.Get("appearance.slider.named", p.Label, p.LabelMin, p.LabelMax);
        top.AddChild(name);
        top.AddChild(value);
        var slider = new HSlider { Name = "slider_" + p.Id, MinValue = p.Min, MaxValue = p.Max, Step = Math.Max((p.Max - p.Min) / 1000.0, 0.0001), Value = editor.Value(p.Id),
            SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.All, Editable = editor.CanModify };
        slider.AccessibilityName = _strings.Get("appearance.slider.named", p.Label, p.LabelMin, p.LabelMax);
        slider.ValueChanged += v => { if (!_syncing) editor.SetValue(p.Id, (float)v); };
        var ends = new HBoxContainer();
        var lo = new Label { Text = p.LabelMin, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        var hi = new Label { Text = p.LabelMax, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right, ClipText = true };
        lo.AccessibilityName = p.LabelMin;
        hi.AccessibilityName = p.LabelMax;
        ends.AddChild(lo);
        ends.AddChild(hi);
        box.AddChild(top);
        box.AddChild(slider);
        box.AddChild(ends);
        Body.AddChild(box);
        _sliders[p.Id] = slider;
        _sliderValues[p.Id] = value;
        value.Text = editor.Value(p.Id).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void AddSwatch(WearableEditor editor, ColourSwatch swatch)
    {
        var row = new HBoxContainer { Name = "colour_row" };
        var label = new Label { Text = _strings.Get("appearance.colour"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        label.AccessibilityName = _strings.Get("appearance.colour");
        var (r, g, b) = editor.Colour(swatch);
        var pick = new ColourButton { Name = "colour_" + swatch.Slot, Color = ShellTheme.DataColour(r, g, b), CustomMinimumSize = new Vector2(96, 28), Disabled = !editor.CanModify };
        pick.AccessibilityName = _strings.Get("appearance.colour.named", SlotTitleOfSwatch(editor.Type, swatch));
        pick.ColorChanged += c => { if (!_syncing) editor.SetColour(swatch, c.R, c.G, c.B); };
        row.AddChild(label);
        row.AddChild(pick);
        Body.AddChild(row);
        _swatches[swatch] = pick;
    }

    private string SlotTitleOfSwatch(WearableType type, ColourSwatch swatch) =>
        WearableKinds.Slots(type).FirstOrDefault(s => s.Index == swatch.Slot) is { } s ? SlotTitle(s.Name) : _strings.Get("appearance.type." + WearableKinds.Name(type));

    private void AddSlot(WearableEditor editor, TextureSlot slot)
    {
        var row = new HBoxContainer { Name = "slot_row_" + slot.Index };
        var label = new Label { Text = SlotTitle(slot.Name), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        label.AccessibilityName = SlotTitle(slot.Name);
        var asset = editor.Texture(slot.Index);
        var look = TextureSlotLook.Of(asset);
        var button = new Button { Name = "slot_" + slot.Index, Text = _strings.Get(look.ButtonKey), CustomMinimumSize = new Vector2(110, 0), Disabled = !editor.CanModify, ClipText = true };
        button.AccessibilityName = _strings.Get("appearance.texture.pick") + ": " + SlotTitle(slot.Name);
        button.Pressed += () => ChooseTexture(editor, slot);
        row.AddChild(label);
        if (look.ShowsSwatch)
            row.AddChild(Swatch(slot.Index, asset));
        row.AddChild(button);
        Body.AddChild(row);
        _slotButtons[slot.Index] = button;
    }

    /// <summary>The picture of a slot's texture: a small framed square that shows the image once it is fetched and decoded (the same image
    /// is kept for the next time the body is built), and stays an empty frame while it is read or when it cannot be.</summary>
    private Control Swatch(int index, Guid asset)
    {
        const float size = 28;
        var frame = new Panel { Name = "swatch_" + index, CustomMinimumSize = new Vector2(size, size), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var image = new TextureRect
        {
            Name = "swatch_image", ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true,
        };
        image.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, 2);
        frame.AddChild(image);
        if (_swatchImages.TryGetValue(asset, out var known))
            image.Texture = known;
        else if (_textureImage is { } load)
            _ = FillSwatch(image, asset, load);
        return frame;
    }

    private async Task FillSwatch(TextureRect image, Guid asset, Func<Guid, Task<Viewer.Core.Imaging.DecodedTexture?>> load)
    {
        Viewer.Core.Imaging.DecodedTexture? decoded;
        try { decoded = await load(asset); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { decoded = null; }
        if (decoded is null || !IsInstanceValid(image))
            return;
        var texture = ImageTexture.CreateFromImage(Image.CreateFromData(decoded.Width, decoded.Height, false, Image.Format.Rgba8, decoded.Rgba));
        _swatchImages[asset] = texture;
        image.Texture = texture;
    }

    private void ChooseTexture(WearableEditor editor, TextureSlot slot)
    {
        if (_model is null)
            return;
        _picker?.QueueFree();
        _picker = new TexturePickerPopup(_strings, _model, _textureUsable, slot.AllowNone, itemId =>
        {
            var asset = _textureAssetOf(itemId);
            if (asset == Guid.Empty)
            {
                NoticeLabel.Text = _strings.Get("appearance.texture.unavailable");
                return;
            }
            editor.SetTexture(slot.Index, asset);
            NoticeLabel.Text = string.Empty;
            _rebuild = _dirty = true;
        }, slot.AllowNone ? () => { editor.SetTexture(slot.Index, Guid.Empty); _rebuild = _dirty = true; } : null,
        () => { editor.ResetTexture(slot.Index); NoticeLabel.Text = string.Empty; _rebuild = _dirty = true; }, editor.IsDefaultTexture(slot.Index) || !editor.CanModify);
        AddChild(_picker);
        _picker.PopupCentered(new Vector2I(360, 420));
    }

    /// <summary>Chooses a texture for a slot by inventory item id (the entry the start-up check uses).</summary>
    public void PickTexture(int slot, Guid itemId)
    {
        if (Editing?.Editor is not { } e)
            return;
        var asset = _textureAssetOf(itemId);
        if (asset != Guid.Empty)
            e.SetTexture(slot, asset);
        _rebuild = _dirty = true;
    }

    // ---------------- keeping the controls in step ----------------

    private void SyncControls()
    {
        if (Editing?.Editor is not { } e)
            return;
        _syncing = true;
        foreach (var (id, slider) in _sliders)
        {
            slider.SetValueNoSignal(e.Value(id));
            _sliderValues[id].Text = e.Value(id).ToString("0.##", CultureInfo.InvariantCulture);
        }
        foreach (var (swatch, pick) in _swatches)
        {
            var (r, g, b) = e.Colour(swatch);
            pick.Color = ShellTheme.DataColour(r, g, b);
        }
        if (NameField.Text != e.Name)
            NameField.Text = e.Name;
        _syncing = false;
    }

    private void Refresh()
    {
        RefreshWorn();
        var ed = Editing;
        bool ready = ed is { State: EditState.Ready, Editor: not null };
        PickLabel.Visible = !ready && ed?.State != EditState.Loading && ed?.State != EditState.Failed;
        NameField.Editable = ready && ed!.Editor!.CanModify;
        BodyScroll.Visible = ready;
        string stateNotice = ed switch
        {
            { State: EditState.Failed } => _strings.Has("appearance.failed." + ed.Reason) ? _strings.Get("appearance.failed." + ed.Reason) : _strings.Get("appearance.failed.asset_unavailable"),
            { State: EditState.Loading } => _strings.Get("appearance.loading"),
            _ => "",
        };
        if (stateNotice.Length > 0)
            NoticeLabel.Text = stateNotice;
        else if (_stateNotice.Length > 0 && NoticeLabel.Text == _stateNotice)
            NoticeLabel.Text = string.Empty;
        _stateNotice = stateNotice;
        if (!ready)
        {
            if (NameField.Text.Length > 0) { _syncing = true; NameField.Text = string.Empty; _syncing = false; }
            _builtFor = Guid.Empty;
        }
        else
        {
            if (_builtFor != ed!.ItemId && !_rebuild)
                _rebuild = true;
            SyncControls();
        }
        bool dirty = ed?.HasUnsavedChanges ?? false;
        SaveButton.Disabled = !(ed?.CanSave ?? false) || !dirty;
        RevertButton.Disabled = !dirty;
        SaveAsName.Editable = ed?.CanSaveAs ?? false;
        SaveAsButton.Disabled = !(ed?.CanSaveAs ?? false) || SaveAsName.Text.Trim().Length == 0;
        StatusLabel.Text = ed is null ? _strings.Get("appearance.status.clean") : ed.SaveState switch
        {
            EditSaveState.Saving => _strings.Get("appearance.status.saving"),
            EditSaveState.Saved when !dirty => _strings.Get("appearance.status.saved"),
            EditSaveState.SavedNotRebuilt when !dirty => _strings.Get("appearance.status.saved_not_rebuilt"),
            EditSaveState.Failed => _strings.Get("appearance.status.failed", ReasonText(ed.SaveReason)),
            _ => dirty ? _strings.Get("appearance.status.dirty") : _strings.Get("appearance.status.clean"),
        };
        if (_afterSave is not null && ed is { SaveState: EditSaveState.Saved or EditSaveState.SavedNotRebuilt })
        {
            var after = _afterSave;
            _afterSave = null;
            _builtFor = Guid.Empty;
            _rebuild = true;
            after();
        }
        else if (_afterSave is not null && ed is { SaveState: EditSaveState.Failed })
        {
            _afterSave = null;   // the save failed: the edits stay and nothing else happens
        }
    }

    private string ReasonText(string code)
    {
        string key = "appearance.reason." + code;
        return _strings.Has(key) ? _strings.Get(key) : _strings.Get("appearance.reason.other");
    }
}

/// <summary>A small window over the account's inventory textures: a search box, the list, None (where the slot may be empty) and Cancel.</summary>
public partial class TexturePickerPopup : PopupPanel
{
    private readonly StringTable _strings;
    private readonly InventoryModel _model;
    private readonly Action<Guid> _chosen;
    private readonly ItemList _list;
    private readonly LineEdit _search;
    private readonly Label _count;
    private IReadOnlyList<PickRow> _rows = [];
    private string _shown = "\0";

    public ItemList List => _list;
    public LineEdit Search => _search;

    private readonly Func<Guid, bool>? _usable;

    /// <param name="reset">Puts the slot back to its type's default texture (the official picker's Default button); null: no such button.</param>
    /// <param name="atDefault">The slot already holds its default, so the button is off.</param>
    public TexturePickerPopup(StringTable strings, InventoryModel model, Func<Guid, bool>? usable, bool allowNone, Action<Guid> chosen, Action? none,
        Action? reset = null, bool atDefault = false)
    {
        _strings = strings;
        _model = model;
        _usable = usable;
        _chosen = chosen;
        Name = "texture_picker";
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var title = new Label { Text = strings.Get("appearance.texture.pick") };
        title.AccessibilityName = strings.Get("appearance.texture.pick");
        box.AddChild(title);
        _search = new LineEdit { Name = "texture_search", PlaceholderText = strings.Get("appearance.texture.search") };
        _search.AccessibilityName = strings.Get("appearance.texture.search");
        _search.TextChanged += _ => Fill();
        box.AddChild(_search);
        _list = new ItemList { Name = "texture_list", SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 240) };
        _list.AccessibilityName = strings.Get("appearance.texture.list");
        _list.ItemActivated += i => Choose((int)i);
        _list.ItemSelected += i => _selected = (int)i;
        box.AddChild(_list);
        _count = new Label { Name = "texture_count" };
        _count.AccessibilityName = strings.Get("appearance.texture.list");
        box.AddChild(_count);
        var row = new HBoxContainer();
        var ok = new Button { Name = "texture_use", Text = strings.Get("appearance.texture.pick") };
        ok.AccessibilityName = strings.Get("appearance.texture.pick");
        ok.Pressed += () => Choose(_selected);
        row.AddChild(ok);
        if (reset is not null)
        {
            var d = new Button { Name = "texture_default", Text = strings.Get("appearance.texture.default"), Disabled = atDefault };
            d.AccessibilityName = strings.Get("appearance.texture.default");
            d.Pressed += () => { reset(); Hide(); };
            row.AddChild(d);
        }
        if (allowNone && none is not null)
        {
            var b = new Button { Name = "texture_none", Text = strings.Get("appearance.texture.none") };
            b.AccessibilityName = strings.Get("appearance.texture.none");
            b.Pressed += () => { none(); Hide(); };
            row.AddChild(b);
        }
        var cancel = new Button { Name = "texture_cancel", Text = strings.Get("appearance.texture.cancel") };
        cancel.AccessibilityName = strings.Get("appearance.texture.cancel");
        cancel.Pressed += Hide;
        row.AddChild(cancel);
        box.AddChild(row);
        AddChild(box);
        ReverieRestyle.Apply(this);   // a popup is not a floater: it takes the Reverie look itself
        Fill();
    }

    private int _selected = -1;

    public override void _Process(double delta)
    {
        if (!Visible)
            return;
        // folders that hold textures are read as they are needed; the list follows
        bool ready = TexturePicker.Prepare(_model);
        if (!ready || _shown == "\0")
            Fill();
    }

    private void Fill()
    {
        bool ready = TexturePicker.Prepare(_model);
        _rows = TexturePicker.Rows(_model, _search.Text, _usable);
        _shown = _search.Text;
        _list.Clear();
        foreach (var r in _rows)
            _list.AddItem(r.InLibrary ? _strings.Get("appearance.texture.library", r.Name) : r.Name);
        _count.Text = ready ? _strings.Get("appearance.texture.count", new Counted("count.textures", _rows.Count)) : _strings.Get("appearance.texture.loading");
        _selected = -1;
    }

    /// <summary>Uses the row at <paramref name="index"/> (the entry the start-up check uses).</summary>
    public void Choose(int index)
    {
        if (index < 0 || index >= _rows.Count)
            return;
        _chosen(_rows[index].ItemId);
        Hide();
    }
}
