// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Globalization;
using Godot;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Viewer.Shell.Ui;

namespace Viewer.Godot;

/// <summary>The Item Properties window, opened from an inventory item's right-click menu.</summary>
public static class ItemPropertiesFeature
{
    public const string FloaterId = "item_properties";

    public static ItemPropertiesPanel Install(ShellRoot shell)
    {
        var panel = new ItemPropertiesPanel(shell.Shell.Strings);
        shell.AddFloater(new FloaterSpec(FloaterId, "item_properties.title", new UiSize(380, 440), new UiSize(320, 260)), panel);
        return panel;
    }
}

/// <summary>
/// One inventory item's name, description, creator, owner, acquired date, the permissions held and the next owner's. A change is
/// saved as it is made (a text field on Enter, on leaving it or when the window closes; a box when it is ticked), as in the
/// official viewer. What may change is decided by <see cref="ItemEdit"/>; the rest is greyed. Nothing runs while the window is
/// hidden.
/// </summary>
public partial class ItemPropertiesPanel : ScrollContainer
{
    private const long RedrawGapMs = 250;

    private readonly StringTable _strings;
    private InventoryModel? _model;
    private InventoryActions? _actions;
    private Func<Guid, string?> _nameOf = _ => null;
    private string _shownName = "", _shownDescription = "";
    private readonly bool[] _shownNext = new bool[3];
    private bool _filling, _namesPending;
    private volatile bool _modelDirty;
    private long _drawnAt;

    public LineEdit NameEdit { get; }
    public LineEdit DescriptionEdit { get; }
    public Label CreatorValue { get; }
    public Label OwnerValue { get; }
    public Label AcquiredValue { get; }
    public Label HeldLabel { get; }
    public CheckBox[] Held { get; }
    public CheckBox[] Next { get; }
    public Label Notice { get; }

    /// <summary>The item shown (the item a link points to); Guid.Empty for none.</summary>
    public Guid ItemId { get; private set; }

    public ItemPropertiesPanel(StringTable strings)
    {
        _strings = strings;
        Name = "item_properties_panel";
        HorizontalScrollMode = ScrollMode.Disabled;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        var column = new VBoxContainer { Name = "ip_column", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(column);

        var fields = new GridContainer { Name = "ip_fields", Columns = 2 };
        column.AddChild(fields);
        NameEdit = Field(fields, "ip_name", "item_properties.name", MaxNameLength);
        DescriptionEdit = Field(fields, "ip_description", "item_properties.description", ItemEdit.MaxDescriptionBytes);
        CreatorValue = Value(fields, "ip_creator", "item_properties.creator");
        OwnerValue = Value(fields, "ip_owner", "item_properties.owner");
        AcquiredValue = Value(fields, "ip_acquired", "item_properties.acquired");

        HeldLabel = new Label { Name = "ip_held_label", Text = strings.Get("item_properties.you_can") };
        column.AddChild(HeldLabel);
        Held = Boxes(column, "ip_held", "item_properties.held");
        column.AddChild(new Label { Name = "ip_next_label", Text = strings.Get("item_properties.next_owner") });
        Next = Boxes(column, "ip_next", "item_properties.next_owner");
        foreach (var box in Next)
            box.Toggled += _ => Commit();

        Notice = new Label { Name = "ip_notice", ThemeTypeVariation = UiTheme.MutedLabel, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(200, 0) };
        column.AddChild(Notice);

        VisibilityChanged += () => { if (IsVisibleInTree()) Refresh(); else Commit(); };
        Bind(null, null, null);
    }

    private const int MaxNameLength = 63;   // the inventory name limit the update is checked against

    private LineEdit Field(GridContainer grid, string name, string key, int maxLength)
    {
        Caption(grid, name, key);
        var edit = new LineEdit { Name = name, MaxLength = maxLength, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        edit.AccessibilityName = _strings.Get(key);
        edit.TextSubmitted += _ => Commit();
        edit.FocusExited += Commit;
        // Esc puts back the item's text and leaves the field; the shell's own Esc handling would leave it first, which saves it
        edit.SetMeta(ShellRoot.TakesEscapeMeta, true);
        // a press anywhere else, an empty part of the window included, leaves the field and so saves it
        edit.SetMeta(ShellRoot.CommitsOnPressOutsideMeta, true);
        edit.GuiInput += e => CancelOnEscape(e, edit);
        grid.AddChild(edit);
        return edit;
    }

    private void CancelOnEscape(InputEvent e, LineEdit field)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            return;
        field.Text = field == NameEdit ? _shownName : _shownDescription;
        field.AcceptEvent();
        field.ReleaseFocus();
    }

    private Label Value(GridContainer grid, string name, string key)
    {
        Caption(grid, name, key);
        var value = new Label { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(160, 0) };
        grid.AddChild(value);
        return value;
    }

    private void Caption(GridContainer grid, string name, string key) => grid.AddChild(new Label { Name = name + "_caption", Text = _strings.Get(key) });

    private CheckBox[] Boxes(VBoxContainer column, string name, string groupKey)
    {
        var row = new HFlowContainer { Name = name };
        column.AddChild(row);
        var boxes = new CheckBox[3];
        string[] ids = ["modify", "copy", "transfer"];
        for (int i = 0; i < 3; i++)
        {
            string label = _strings.Get("item_properties." + ids[i]);
            boxes[i] = new CheckBox { Name = name + "_" + ids[i], Text = label };
            boxes[i].AccessibilityName = _strings.Get(groupKey) + " " + label;
            row.AddChild(boxes[i]);
        }
        return boxes;
    }

    /// <summary>Shows a session's inventory (null: no session). <paramref name="nameOf"/> gives an agent's name, or null while it is
    /// being asked for.</summary>
    public void Bind(InventoryModel? model, InventoryActions? actions, Func<Guid, string?>? nameOf)
    {
        if (_model is not null)
            _model.Changed -= MarkDirty;
        _model = model;
        _actions = actions;
        _nameOf = nameOf ?? (_ => null);
        ItemId = Guid.Empty;
        if (model is not null)
            model.Changed += MarkDirty;
        Notice.Text = string.Empty;
        Refresh();
    }

    private void MarkDirty() => _modelDirty = true;

    /// <summary>Shows an item (a link shows the item it points to). False when the inventory does not hold it.</summary>
    public bool ShowItem(Guid id)
    {
        Commit();   // text typed for the item shown before is saved to it
        var item = _model?.Item(id);
        if (item?.LinkedId is { } target && item.Kind == ItemKind.Link)
            item = _model!.Item(target);
        if (item is null)
            return false;
        ItemId = item.Id;
        Notice.Text = string.Empty;
        _shownName = _shownDescription = "";
        NameEdit.Text = DescriptionEdit.Text = string.Empty;
        Refresh();
        return true;
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree() || !(_modelDirty || _namesPending))
            return;
        if ((long)Time.GetTicksMsec() - _drawnAt >= RedrawGapMs)
            Refresh();
    }

    /// <summary>Fills the window from the inventory. A text field the user is typing in is left as typed.</summary>
    public void Refresh()
    {
        _modelDirty = false;
        _drawnAt = (long)Time.GetTicksMsec();
        _filling = true;
        try
        {
            var item = _model?.Item(ItemId);
            var rights = item is null ? ItemEditRights.None : ItemEdit.RightsFor(_model!, item);
            Fill(NameEdit, ref _shownName, item?.Name ?? "", rights.Name);
            Fill(DescriptionEdit, ref _shownDescription, item?.Description ?? "", rights.Description);

            var p = item?.Permissions;
            _namesPending = false;
            CreatorValue.Text = p is null ? "" : Who(p.CreatorId);
            OwnerValue.Text = p is null ? "" : p.GroupOwned ? _strings.Get("item_properties.group_owned") : Who(p.OwnerId);
            AcquiredValue.Text = item?.CreatedAt is { } at ? at.ToLocalTime().ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture) : p is null ? "" : _strings.Get("item_properties.unknown");

            // "You can" for the agent's own item (the owner mask), "Owner can" for anyone else's, as the official viewer labels it
            HeldLabel.Text = _strings.Get(p is not null && p.OwnerId != _model!.OwnerId ? "item_properties.owner_can" : "item_properties.you_can");
            Show(Held, p?.OwnerMask ?? 0, false, false, false);
            Show(Next, p?.NextOwnerMask ?? 0, rights.NextModify, rights.NextCopy, rights.NextTransfer);
            for (int i = 0; i < 3; i++)
                _shownNext[i] = Next[i].ButtonPressed;
            if (item is null)
                Notice.Text = _strings.Get("item_properties.none");
        }
        finally { _filling = false; }
    }

    private void Fill(LineEdit edit, ref string shown, string value, bool open)
    {
        bool typing = open && edit.HasFocus() && edit.Text != shown;
        if (!typing)
        {
            if (edit.Text != value)
                edit.Text = value;
            shown = edit.Text;   // what the field holds: a value longer than it takes is not read as typed
        }
        edit.Editable = open;
        // the theme draws a read-only field's text as an editable one's: a locked field is greyed here
        if (open)
            edit.RemoveThemeColorOverride("font_uneditable_color");
        else
            edit.AddThemeColorOverride("font_uneditable_color", ShellTheme.C(UiTheme.Tokens.TextSecondary));
        edit.TooltipText = open ? "" : _strings.Get("disabled.item_properties.field");
    }

    private void Show(CheckBox[] boxes, uint mask, bool modify, bool copy, bool transfer)
    {
        uint[] bits = [ItemPermissions.Modify, ItemPermissions.Copy, ItemPermissions.Transfer];
        bool[] open = [modify, copy, transfer];
        for (int i = 0; i < 3; i++)
        {
            boxes[i].SetPressedNoSignal((mask & bits[i]) != 0);
            DisabledTip.Set(boxes[i], !open[i], _strings.Get("disabled.item_properties.permission"));
        }
    }

    private string Who(Guid agent)
    {
        if (agent == Guid.Empty)
            return _strings.Get("item_properties.unknown");
        if (_nameOf(agent) is { } name)
            return name;
        _namesPending = true;
        return _strings.Get("item_properties.loading");
    }

    /// <summary>Saves what the window now says, when it differs from the item.</summary>
    public void Commit()
    {
        if (_filling || _actions is null || _model?.Item(ItemId) is not { } item)
            return;
        var wanted = ItemEdit.Changes(item,
            new ItemEditRequest(_shownName, _shownDescription, _shownNext[0], _shownNext[1], _shownNext[2]),
            new ItemEditRequest(NameEdit.Text, DescriptionEdit.Text, Next[0].ButtonPressed, Next[1].ButtonPressed, Next[2].ButtonPressed));
        var result = _actions.SaveProperties(item.Id, wanted);
        Notice.Text = !result.Submitted && result.Code is not null ? _strings.Get("item_properties.not_saved") : "";
        // show what the item holds now (a name that was refused goes back)
        var held = _model.Item(item.Id);
        if (held is not null)
        {
            NameEdit.Text = held.Name;
            DescriptionEdit.Text = held.Description;
            _shownName = NameEdit.Text;
            _shownDescription = DescriptionEdit.Text;
        }
        Refresh();
    }
}
