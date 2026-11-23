// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Outfit;

namespace Viewer.Godot;

/// <summary>Adds the outfit window to the shell: a floater, a Content menu entry and a toolbar button.</summary>
public static class OutfitFeature
{
    public const string FloaterId = "outfits";

    public static OutfitPanel Install(ShellRoot shell, long redrawGapMs = 250)
    {
        var s = shell.Shell;
        var panel = new OutfitPanel(s.Strings, redrawGapMs);
        shell.AddFloater(new FloaterSpec(FloaterId, "outfit.title", new UiSize(380, 520), new UiSize(300, 340)), panel);
        s.Commands.Register("floater.outfits", () => s.Floaters.Toggle(FloaterId));
        if (!s.Menus.Menus.Any(m => m.Id == "content"))   // the inventory feature adds this menu; whichever installs first makes it
            s.Menus.AddMenu("content", "menu.content");
        s.Menus.AddEntry("content", new MenuEntry("content.outfits", "menu.content.outfits", "floater.outfits"));
        s.Toolbar.Add(new ToolbarButton("toolbar_outfits", "toolbar.outfits", "floater.outfits", UiIcons.Outfits, FloaterId));
        return panel;
    }

    /// <summary>The text for a refusal code (<c>outfit.already_worn</c>, a pipeline code, or nothing).</summary>
    public static string RefusalText(StringTable strings, string? code)
    {
        string tail = code is null ? "other" : code.StartsWith("outfit.", StringComparison.Ordinal) ? code["outfit.".Length..] : code;
        string key = "outfit.refused." + tail;
        return strings.Has(key) ? strings.Get(key) : strings.Get("outfit.refused.other");
    }
}

/// <summary>
/// The outfit floater: what is worn now (attachments, body parts, then clothing by type, highest layer first), the saved outfits
/// under My Outfits, and buttons to take an item off, wear an outfit (replacing the current one) and save the current look as a
/// new outfit. It draws <see cref="OutfitView"/> and hands the buttons to <see cref="OutfitActions"/>; the look itself is rebuilt
/// by the grid and arrives as the avatar's own appearance. Behaviour follows the official viewer's outfit panels; the layout and
/// icons are this viewer's own.
/// </summary>
public partial class OutfitPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private static ThemeSpec _theme => UiTheme.Spec;
    private InventoryModel? _model;
    private OutfitView? _view;
    private OutfitActions? _actions;
    private Guid _selectedWorn, _selectedOutfit;
    private bool _dirty = true, _modelDirty, _building;
    private readonly RedrawGate _gate;
    private bool _drawn;
    private string _resultKey = "";

    public Tree CurrentTree { get; }
    public Tree SavedTree { get; }
    public Button TakeOffButton { get; }
    public Button WearOutfitButton { get; }
    public Button SaveButton { get; }
    public LineEdit NameField { get; }
    public Label BaseLabel { get; }
    public Label SavedNone { get; }
    public Label NoticeLabel { get; }
    public Label StatusLabel { get; }

    public OutfitPanel(StringTable strings, long redrawGapMs = 250)
    {
        _strings = strings;
        _gate = new RedrawGate(redrawGapMs);
        Name = "outfit_panel";

        AddChild(Header("current_header", "outfit.current"));
        CurrentTree = MakeTree("current_tree", "outfit.current");
        CurrentTree.ItemSelected += () => { if (!_building) { _selectedWorn = IdOf(CurrentTree.GetSelected()); Refresh(); } };
        AddChild(CurrentTree);
        BaseLabel = ChatFeature.Note("base", strings.Get("outfit.current"), _theme);
        AddChild(BaseLabel);
        var wornRow = new HBoxContainer { Name = "worn_buttons" };
        TakeOffButton = Button("take_off", "outfit.button.take_off", OnTakeOff);
        wornRow.AddChild(TakeOffButton);
        AddChild(wornRow);

        AddChild(Header("saved_header", "outfit.saved"));
        SavedTree = MakeTree("saved_tree", "outfit.saved.tree");
        SavedTree.ItemSelected += () => { if (!_building) { _selectedOutfit = IdOf(SavedTree.GetSelected()); Refresh(); } };
        AddChild(SavedTree);
        SavedNone = ChatFeature.Note("saved_none", strings.Get("outfit.saved.none"), _theme);
        SavedNone.Text = strings.Get("outfit.saved.none");
        AddChild(SavedNone);
        var savedRow = new HBoxContainer { Name = "saved_buttons" };
        WearOutfitButton = Button("wear_outfit", "outfit.button.wear", OnWearOutfit);
        savedRow.AddChild(WearOutfitButton);
        AddChild(savedRow);

        var saveRow = new HBoxContainer { Name = "save_row" };
        NameField = new LineEdit { Name = "outfit_name", PlaceholderText = strings.Get("outfit.name.hint"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        NameField.AccessibilityName = strings.Get("outfit.name.label");
        saveRow.AddChild(NameField);
        SaveButton = Button("save_outfit", "outfit.button.save", OnSave);
        saveRow.AddChild(SaveButton);
        AddChild(saveRow);

        NoticeLabel = ChatFeature.Note("notice", strings.Get("outfit.notice"), _theme);
        NoticeLabel.ThemeTypeVariation = UiTheme.ErrorLabel;
        AddChild(NoticeLabel);
        StatusLabel = ChatFeature.Note("status", strings.Get("outfit.status"), _theme);
        AddChild(StatusLabel);
        Bind(null, null);
    }

    private Label Header(string name, string key)
    {
        var l = new Label { Name = name, Text = _strings.Get(key) };
        l.AccessibilityName = _strings.Get(key);
        return l;
    }

    private Tree MakeTree(string name, string key)
    {
        var t = new Tree { Name = name, HideRoot = true, SelectMode = Tree.SelectModeEnum.Single, SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.All, EnableDragUnfolding = false };   // folders open on a click only, never under a passing drag
        t.AccessibilityName = _strings.Get(key);
        return t;
    }

    private Button Button(string name, string key, Action pressed)
    {
        var b = new Button { Name = name, Text = _strings.Get(key) };
        b.AccessibilityName = _strings.Get(key);
        b.Pressed += pressed;
        return b;
    }

    private static Guid IdOf(TreeItem? item) =>
        item is not null && item.GetMetadata(0).VariantType == Variant.Type.String && Guid.TryParse(item.GetMetadata(0).AsString(), out var g) ? g : Guid.Empty;

    /// <summary>Shows a session's outfit (null: no session; the window is empty and its buttons are off).</summary>
    /// <summary>Whether a worn object may be taken off now (restraints may lock one); null: every one may.</summary>
    public Func<Guid, bool>? CanDetachItem { get; set; }

    public void Bind(InventoryModel? model, Func<ActionIntent, ActionResult>? submit)
    {
        if (_model is not null)
            _model.Changed -= MarkDirty;
        _model = model;
        _view = model is null ? null : new OutfitView(model);
        _actions = model is null || submit is null ? null : new OutfitActions(model, submit, canDetach: id => CanDetachItem?.Invoke(id) ?? true);
        if (model is not null)
            model.Changed += MarkDirty;
        _drawn = false;
        _selectedWorn = _selectedOutfit = Guid.Empty;
        _resultKey = "";
        NoticeLabel.Text = string.Empty;
        _dirty = true;
    }

    private void MarkDirty() => _modelDirty = true;

    internal OutfitActions? Actions => _actions;
    internal OutfitView? View => _view;

    /// <summary>An outfit command has finished on the session: say how it went.</summary>
    public void ShowResult(OutfitCommandFinished done)
    {
        _resultKey = done.Ok ? (done.Save ? "outfit.status.save_done" : "outfit.status.done") : done.Reason == "no_service" ? "outfit.status.no_service" : "outfit.status.failed:" + done.Reason;
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        long now = (long)Time.GetTicksMsec();
        if (!_gate.Due(now, _dirty, _modelDirty, IsVisibleInTree() || !_drawn))   // the first draw after a bind happens even hidden: it asks for the folders
            return;
        _dirty = _modelDirty = false;
        _gate.Drew(now);
        _drawn = true;
        Rebuild();
    }

    public void Rebuild()
    {
        _building = true;
        try
        {
            CurrentTree.Clear();
            SavedTree.Clear();
            var cr = CurrentTree.CreateItem();
            var sr = SavedTree.CreateItem();
            if (_view is not null)
            {
                _view.Prepare();
                string[] sections = ["outfit.section.attachments", "outfit.section.bodyparts", "outfit.section.clothing", "outfit.section.other"];
                TreeItem? picked = null;
                foreach (var group in _view.Current().GroupBy(e => e.Section))
                {
                    var head = CurrentTree.CreateItem(cr);
                    head.SetText(0, _strings.Get(sections[Math.Min(group.Key, 3)]));
                    head.SetSelectable(0, false);
                    foreach (var e in group)
                    {
                        var row = CurrentTree.CreateItem(head);
                        row.SetText(0, e.Name);
                        row.SetMetadata(0, e.TargetId.ToString());
                        if (e.TargetId == _selectedWorn)
                            picked = row;
                    }
                }
                if (picked is not null) picked.Select(0); else _selectedWorn = Guid.Empty;
                TreeItem? pickedOutfit = null;
                foreach (var f in _view.Saved())
                {
                    var row = SavedTree.CreateItem(sr);
                    row.SetText(0, f.Name);
                    row.SetMetadata(0, f.Id.ToString());
                    if (f.Id == _selectedOutfit)
                        pickedOutfit = row;
                }
                if (pickedOutfit is not null) pickedOutfit.Select(0); else _selectedOutfit = Guid.Empty;
                SavedNone.Visible = sr.GetChildCount() == 0;
            }
        }
        finally { _building = false; }
        Refresh();
    }

    private void Refresh()
    {
        bool bound = _actions is not null && _view is not null;
        DisabledTip.Set(TakeOffButton, !bound || _selectedWorn == Guid.Empty || !_actions!.CanTakeOff(_selectedWorn), _strings.Get("disabled.outfit.take_off"));
        DisabledTip.Set(WearOutfitButton, !bound || _selectedOutfit == Guid.Empty || !_actions!.CanWearOutfit(_selectedOutfit), _strings.Get("disabled.outfit.wear"));
        DisabledTip.Set(SaveButton, !bound, _strings.Get("disabled.outfit.save"));
        NameField.Editable = bound;
        if (_view is null)
        {
            StatusLabel.Text = _strings.Get("outfit.status.waiting");
            BaseLabel.Text = string.Empty;
            return;
        }
        string? from = _view.BaseOutfitName();
        BaseLabel.Text = from is null ? string.Empty : _strings.Get("outfit.base", from);
        if (_resultKey.StartsWith("outfit.status.failed:", StringComparison.Ordinal))
            StatusLabel.Text = _strings.Get("outfit.status.failed", _resultKey["outfit.status.failed:".Length..]);
        else if (_resultKey.Length > 0)
            StatusLabel.Text = _strings.Get(_resultKey);
        else
            StatusLabel.Text = _strings.Get("outfit.status.items", new Counted("count.items", _view.Current().Count));
    }

    // ---------------- buttons (also the entries the start-up check uses) ----------------

    public void SelectWorn(Guid targetId) { _selectedWorn = targetId; _dirty = true; }

    public void SelectOutfit(Guid folderId) { _selectedOutfit = folderId; _dirty = true; }

    public void OnTakeOff()
    {
        if (_actions is not null && _selectedWorn != Guid.Empty)
            Show(_actions.TakeOff(_selectedWorn));
    }

    public void OnWearOutfit()
    {
        if (_actions is not null && _selectedOutfit != Guid.Empty)
            Show(_actions.WearOutfit(_selectedOutfit));
    }

    public void OnSave()
    {
        if (_actions is not null)
            Show(_actions.SaveOutfit(NameField.Text));
    }

    private void Show(OutfitActionResult result)
    {
        _dirty = true;
        NoticeLabel.Text = result.Submitted ? string.Empty : OutfitFeature.RefusalText(_strings, result.Code);
        if (result.Submitted)
            _resultKey = "outfit.status.sending";
    }
}
