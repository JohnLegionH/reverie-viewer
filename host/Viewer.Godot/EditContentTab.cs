// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Viewer.Core.Inventory;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Build;
using Viewer.Shell.Inventory;

namespace Viewer.Godot;

/// <summary>
/// The Build window's Contents section, after the official floater_tools.xml Contents panel and LLPanelObjectInventory (read as behaviour;
/// indra/newview/llpanelobjectinventory.cpp): New Script, the prim's items by name with " (no copy)", " (no modify)" and " (no transfer)" for
/// what the agent may not do (getDisplayName), and for the chosen item Open (scripts, notecards and textures; openItem of each bridge),
/// Properties (showProperties: name, description, creator, owner and permissions, the name and description editable when the item may be
/// modified), Rename (renameItem), Delete (removeItem: the region takes it out, and OpenSim puts it in the remover's trash) and Copy to
/// inventory. A right click on a row offers the same as the official context menu (buildContextMenu). The list takes inventory items and
/// whole folders dropped on it (a folder's items go in one at a time, as Firestorm's Z-drop), and a row dragged out onto an inventory folder
/// is copied there (startDrag). Standard controls styled through the Theme alone. Main thread only.
/// </summary>
public sealed class EditContentTab
{
    private readonly StringTable _s;
    private readonly Func<EditController?> _edit;
    private readonly Func<InventoryModel?> _inventory;
    private readonly Func<Guid> _agent;
    private readonly Func<Guid, string?>? _names;
    private readonly ContentDropList _list;
    private readonly Button _newScript, _delete, _copy, _open, _properties, _rename;
    private readonly PopupMenu _menu;
    private readonly VBoxContainer _props;
    private readonly LineEdit _propName, _propDesc;
    private readonly Label _propCreator, _propOwner, _propYou, _propNext, _propAnyone;
    private readonly Button _propSave;
    private Guid _propItem;

    /// <summary>Open on a row (the button, a double-click or the context menu): the item's id; the host opens it by its kind.</summary>
    public Action<Guid>? OpenRequested;

    /// <summary>An item was deleted (the host closes its script or notecard window, as removeItem hides the preview).</summary>
    public Action<Guid>? Deleted;

    /// <summary>What the agent's region offers for choosing a script's engine (the host supplies it from the session; none when logged out).</summary>
    public Func<ScriptRegion>? ScriptRegionOf;

    /// <summary>Sets the engine of a listed script without saving its text (a null target clears the choice); true when the region accepted it.</summary>
    public Func<Guid, ScriptTarget?, System.Threading.Tasks.Task<ScriptEngineReply>>? SetEngine;

    /// <summary>Keeps the once-per-install early-access warning; null shows none.</summary>
    public EarlyAccessNotice? Notice;

    private readonly PopupMenu _engineMenu;
    private readonly List<ScriptTarget?> _engineTargets = new();
    private readonly List<ScriptChoice?> _engineChoices = new();
    private Guid _engineItem;
    private const int MenuEngine = 4;
    private readonly Label _status;
    private Guid _askedFor;
    private readonly List<Guid> _rows = new();
    private readonly List<string> _shownNames = new();
    /// <summary>Folders dropped before their contents had been read from the grid: their items go in once they have.</summary>
    private readonly List<Guid> _waitingFolders = new();

    private const int MenuOpen = 0, MenuProperties = 1, MenuRename = 2, MenuDelete = 3;

    public EditContentTab(StringTable strings, Func<EditController?> edit, Func<InventoryModel?> inventory, VBoxContainer page,
        Func<Guid>? agent = null, Func<Guid, string?>? names = null)
    {
        _s = strings;
        _edit = edit;
        _inventory = inventory;
        _agent = agent ?? (() => Guid.Empty);
        _names = names;
        _newScript = Btn("new_script", "edit.content.new_script", () => _edit()?.NewScript(_s.Get("edit.content.new_script.name")));
        page.AddChild(_newScript);
        _list = new ContentDropList { Name = "contents", CustomMinimumSize = new Vector2(0, 220), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.AccessibilityName = strings.Get("edit.content.list");
        _list.Accepts = ids => ids.Count > 0 && _edit()?.Object() is not null;
        _list.Dropped += Drop;
        _list.DragOut = DragOut;
        _list.SelectMode = ItemList.SelectModeEnum.Multi;   // Ctrl-click and Shift-click choose several rows
        _list.ItemActivated += i => { if (i < _rows.Count) OpenRow(_rows[(int)i]); };
        _list.ItemClicked += (index, at, button) =>
        {
            if (index >= _rows.Count)
                return;
            if (button == (long)MouseButton.Right)
                ShowMenu((int)index, at);
            else if (button == (long)MouseButton.Left && _pair.Click(_rows[(int)index]))
                OpenRow(_rows[(int)index]);   // a double-click the engine did not see (the list was drawn again between the two clicks)
        };
        _list.GuiInput += e =>
        {
            if (e is not InputEventKey { Pressed: true, Echo: false } key)
                return;
            if (key.Keycode == Key.Delete)
            {
                DeleteChosen();   // the Delete key removes the chosen items, as the official folder view's does
                _list.AcceptEvent();
            }
            else if (key.Keycode == Key.F2 && Chosen() is { } renaming && ItemOf(renaming) is { } item && TaskItemRules.CanRename(item, _agent()))
            {
                ShowProperties(renaming, true);   // F2 renames the chosen item
                _list.AcceptEvent();
            }
        };
        page.AddChild(_list);
        _menu = new PopupMenu { Name = "content_menu" };
        _menu.IdPressed += MenuChosen;
        _engineMenu = new PopupMenu { Name = "content_engine_menu" };
        _engineMenu.IdPressed += EngineChosen;
        _menu.AddChild(_engineMenu);
        page.AddChild(_menu);
        var row = new HFlowContainer { Name = "content_buttons" };
        _open = Btn("content_open", "edit.content.open", () => { if (Chosen() is { } id) OpenRequested?.Invoke(id); });
        _properties = Btn("content_properties", "edit.content.properties", () => { if (Chosen() is { } id) ShowProperties(id, false); });
        _rename = Btn("content_rename", "edit.content.rename", () => { if (Chosen() is { } id) ShowProperties(id, true); });
        _delete = Btn("content_delete", "edit.content.delete", () => { if (Chosen() is { } id) Delete(id); });
        _copy = Btn("content_copy", "edit.content.copy", () => { if (Chosen() is { } id) _edit()?.CopyItemToInventory(id); });
        foreach (var b in new[] { _open, _properties, _rename, _delete, _copy })
            row.AddChild(b);
        page.AddChild(row);

        // the item's properties (the official item profile of a task item: name, description, creator, owner, permissions)
        _props = new VBoxContainer { Name = "content_props", Visible = false };
        _propName = new LineEdit { Name = "content_prop_name", MaxLength = TaskItemRules.MaxName, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _propDesc = new LineEdit { Name = "content_prop_desc", MaxLength = TaskItemRules.MaxDescription, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _propName.AccessibilityName = strings.Get("edit.content.prop.name");
        _propDesc.AccessibilityName = strings.Get("edit.content.prop.description");
        _propName.TextSubmitted += _ => SaveProperties();
        _propDesc.TextSubmitted += _ => SaveProperties();
        _props.AddChild(Field("edit.content.prop.name", _propName));
        _props.AddChild(Field("edit.content.prop.description", _propDesc));
        _propCreator = InfoLine("content_prop_creator");
        _propOwner = InfoLine("content_prop_owner");
        _propYou = InfoLine("content_prop_you");
        _propNext = InfoLine("content_prop_next");
        _propAnyone = InfoLine("content_prop_anyone");
        var propButtons = new HBoxContainer();
        _propSave = Btn("content_prop_save", "edit.content.prop.save", SaveProperties);
        propButtons.AddChild(_propSave);
        propButtons.AddChild(Btn("content_prop_close", "edit.content.prop.close", () => { _props.Visible = false; _propItem = Guid.Empty; }));
        _props.AddChild(propButtons);
        page.AddChild(_props);

        _status = new Label { Name = "content_status", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        page.AddChild(_status);
        page.AddChild(new Label { Name = "content_hint", Text = strings.Get("edit.content.hint") + " " + strings.Get("edit.content.drag_hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart });

        Label InfoLine(string name)
        {
            var l = new Label { Name = name, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _props.AddChild(l);
            return l;
        }
    }

    private HBoxContainer Field(string key, Control control)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = _s.Get(key) });
        row.AddChild(control);
        return row;
    }

    private Button Btn(string name, string key, Action pressed)
    {
        var b = new Button { Name = name, Text = _s.Get(key) };
        b.AccessibilityName = b.Text;
        b.Pressed += pressed;
        return b;
    }

    private Guid? Chosen() => _list.GetSelectedItems() is [var i, ..] && i < _rows.Count ? _rows[i] : null;

    /// <summary>Every chosen row (Ctrl-click and Shift-click choose several).</summary>
    private List<Guid> ChosenAll() => _list.GetSelectedItems().Where(i => i < _rows.Count).Select(i => _rows[i]).ToList();

    private readonly Viewer.Shell.Inventory.DoubleClickTracker _pair = new(() => (long)Time.GetTicksMsec());
    private ulong _openedAt;

    /// <summary>Open on a row, once for one double-click (the engine's own and the tracker's can both see it).</summary>
    private void OpenRow(Guid id)
    {
        if (Time.GetTicksMsec() - _openedAt < 400)
            return;
        _openedAt = Time.GetTicksMsec();
        OpenRequested?.Invoke(id);
    }

    /// <summary>The Delete key: each chosen item goes as the Delete button sends it (an object the agent may not change keeps its contents).</summary>
    private void DeleteChosen()
    {
        foreach (var id in ChosenAll())
            Delete(id);
    }

    private TaskItem? ItemOf(Guid id) => _edit()?.ContentItem(id);

    /// <summary>What the object allows: its contents changed (modify), or items removed (modify or the agent owns it; isItemRemovable).</summary>
    private (bool Modify, bool YouOwn) ObjectRights()
    {
        var e = _edit();
        if (e?.Object() is not { } o)
            return (false, false);
        var access = e.Access(o.Id.LocalId);
        return (access.CanModify, access.Owned);
    }

    private void Delete(Guid id)
    {
        var (modify, youOwn) = ObjectRights();
        if (!modify && youOwn)
        {
            _status.Text = _s.Get("edit.content.no_mod_task");   // removeItem: CantModifyContentInNoModTask
            return;
        }
        if (_edit()?.DeleteItem(id) == true)
        {
            if (_propItem == id)
                _props.Visible = false;
            Deleted?.Invoke(id);
        }
    }

    private void ShowMenu(int index, Vector2 at)
    {
        _list.Select(index);
        var id = _rows[index];
        var item = ItemOf(id);
        var (modify, youOwn) = ObjectRights();
        _menu.Clear();
        if (item is not null && TaskItemRules.OpenKind(item) != TaskOpenKind.None)
            _menu.AddItem(_s.Get("edit.content.open"), MenuOpen);
        _menu.AddItem(_s.Get("edit.content.properties"), MenuProperties);
        if (item is not null && TaskItemRules.CanRename(item, _agent()))
            _menu.AddItem(_s.Get("edit.content.rename"), MenuRename);
        if (modify || youOwn)
            _menu.AddItem(_s.Get("edit.content.delete"), MenuDelete);
        if (item is { AssetType: 10 } && ScriptRegionOf?.Invoke() is { } region)
            AddEngineMenu(item, region);
        _menu.Position = (Vector2I)(_list.GetScreenPosition() + at);
        _menu.Popup();
    }

    /// <summary>The right-click menu as it stands, for the live checks: whether it is shown, and each entry's text, whether it is greyed and
    /// its tooltip; the Engine entry's own entries likewise.</summary>
    public object MenuReport()
    {
        static object[] Entries(PopupMenu m) => Enumerable.Range(0, m.ItemCount)
            .Select(i => (object)new { text = m.GetItemText(i), disabled = m.IsItemDisabled(i), tooltip = m.GetItemTooltip(i) }).ToArray();
        return new { visible = _menu.Visible, items = Entries(_menu), engine_items = Entries(_engineMenu) };
    }

    /// <summary>Engine > the engine choices for a listed script, where the region saves a choice. For a script the agent may not modify the entry
    /// is offered only where the region allows an owner to set it; otherwise it is greyed with the reason as its tooltip.</summary>
    private void AddEngineMenu(TaskItem item, ScriptRegion region)
    {
        uint mask = item.OwnerId == _agent() ? item.OwnerMask : item.EveryoneMask;
        var offer = ScriptEngineChoices.ContentsMenu(region, (mask & NotecardPermissions.Modify) != 0);
        if (!offer.Offered)
            return;
        _engineItem = item.ItemId;
        _engineTargets.Clear();
        _engineChoices.Clear();
        _engineMenu.Clear();
        foreach (var c in ScriptEngineChoices.OpenSim(region))
        {
            _engineMenu.AddItem(_s.Get(c.LabelKey, c.LabelArg), _engineTargets.Count);
            _engineMenu.SetItemDisabled(_engineMenu.ItemCount - 1, !c.Enabled);
            _engineTargets.Add(c.Target);
            _engineChoices.Add(c);
        }
        _engineMenu.AddItem(_s.Get("script.engine.menu.clear"), _engineTargets.Count);   // no choice: the first line or the region default
        _engineTargets.Add(null);
        _engineChoices.Add(null);
        _menu.AddSubmenuNodeItem(_s.Get("script.engine.menu"), _engineMenu, MenuEngine);
        int at = _menu.ItemCount - 1;
        _menu.SetItemDisabled(at, !offer.Enabled);
        if (offer.DisabledKey is { } reason)
            _menu.SetItemTooltip(at, _s.Get(reason));
    }

    private async void EngineChosen(long index)
    {
        if (index < 0 || index >= _engineTargets.Count || SetEngine is not { } set)
            return;
        try
        {
            var reply = await set(_engineItem, _engineTargets[(int)index]);
            _status.Text = reply.Ok ? _s.Get("script.engine.set") : ScriptEditorPanel.ScriptEngineFailureText(_s, reply);
            if (_engineChoices[(int)index] is { } picked && Notice?.TakeWarning(picked) == true)
                _status.Text += " " + _s.Get(EarlyAccessNotice.TextKey);   // once per install
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = _s.Get("script.engine.set_failed");
        }
    }

    private void MenuChosen(long id)
    {
        if (Chosen() is not { } item)
            return;
        switch (id)
        {
            case MenuOpen: OpenRequested?.Invoke(item); break;
            case MenuProperties: ShowProperties(item, false); break;
            case MenuRename: ShowProperties(item, true); break;
            case MenuDelete: Delete(item); break;
        }
    }

    /// <summary>Shows the item's properties under the list; with <paramref name="rename"/> the name field takes the keyboard, selected.</summary>
    public void ShowProperties(Guid id, bool rename)
    {
        if (ItemOf(id) is not { } item)
            return;
        _propItem = id;
        _props.Visible = true;
        _propName.Text = item.Name;
        _propDesc.Text = item.Description;
        FillProperties(item);
        if (rename && _propName.Editable)
        {
            _propName.GrabFocus();
            _propName.SelectAll();
        }
    }

    private void FillProperties(TaskItem item)
    {
        var agent = _agent();
        bool editable = TaskItemRules.CanRename(item, agent) && (_edit()?.ContentsEditable ?? false);
        _propName.Editable = _propDesc.Editable = editable;
        _propSave.Disabled = !editable;
        _propCreator.Text = _s.Get("edit.content.prop.creator") + " " + BuildNames.Person(_names, item.CreatorId, _s);
        _propOwner.Text = _s.Get("edit.content.prop.owner") + " " + BuildNames.Owner(_names, item.OwnerId, item.GroupId, item.GroupOwned, _s);
        _propYou.Text = _s.Get("edit.content.prop.you_can") + " " + Rights(TaskItemRules.MaskFor(item, agent));
        _propNext.Text = _s.Get("edit.content.prop.next_owner") + " " + Rights(item.NextOwnerMask);
        _propAnyone.Text = _s.Get("edit.content.prop.everyone") + " " + Rights(item.EveryoneMask);
    }

    private string Rights(uint mask)
    {
        var parts = new List<string>();
        if ((mask & TaskItemRules.Modify) != 0)
            parts.Add(_s.Get("edit.content.prop.modify"));
        if ((mask & TaskItemRules.Copy) != 0)
            parts.Add(_s.Get("edit.content.prop.copy"));
        if ((mask & TaskItemRules.Transfer) != 0)
            parts.Add(_s.Get("edit.content.prop.transfer"));
        return parts.Count == 0 ? _s.Get("edit.content.prop.none") : string.Join(", ", parts);
    }

    private void SaveProperties()
    {
        if (_propItem == Guid.Empty || _edit() is not { } e)
            return;
        e.UpdateItemText(_propItem, _propName.Text, _propDesc.Text, _agent());
    }

    /// <summary>The drag a row starts: the object and the item, when the official drag rules let it leave; null otherwise.</summary>
    private TaskDrag? DragOut(int index)
    {
        if (index < 0 || index >= _rows.Count || _edit() is not { } e || e.Object() is not { } o || ItemOf(_rows[index]) is not { } item)
            return null;
        return e.CanDragOut(item.ItemId, _agent()) ? new TaskDrag(o.FullId, o.Id.LocalId, item.ItemId) : null;
    }

    /// <summary>Inventory ids dropped on the list: an item goes in (a script as a script); a folder's items wait and go in one at a time.</summary>
    public void Drop(IReadOnlyList<Guid> ids)
    {
        if (_edit() is not { } e || _inventory() is not { } inv)
            return;
        foreach (var id in ids)
        {
            if (inv.Item(id) is { } item)
                e.DropItem(item.Id, item.Kind == ItemKind.Script);
            else if (inv.Folder(id) is { } folder)
            {
                if (inv.LoadState(folder.Id) == FolderLoad.Loaded)
                    QueueFolder(e, inv, folder.Id);
                else
                {
                    // the folder's contents are read from the grid first (as when it is opened in the inventory window)
                    inv.EnsureLoaded(folder.Id);
                    if (!_waitingFolders.Contains(folder.Id))
                        _waitingFolders.Add(folder.Id);
                    _status.Text = _s.Get("edit.content.loading");
                }
            }
        }
    }

    private void QueueFolder(EditController e, InventoryModel inv, Guid folder)
    {
        int n = e.DropFolder(inv.ChildItems(folder).Where(i => i.Kind is not (ItemKind.Link or ItemKind.LinkFolder)).Select(i => (i.Id, i.Kind == ItemKind.Script)));
        _status.Text = _s.Get("edit.content.folder_queued", new Counted("count.items", n));
    }

    /// <summary>Shows the primary prim's items, asking the region for them when the tab shows a prim it has not asked about.</summary>
    public void Refresh(bool shown)
    {
        var e = _edit();
        var o = e?.Object();
        var (modify, youOwn) = ObjectRights();
        if (e is not null && _inventory() is { } inv)
            foreach (var folder in _waitingFolders.ToArray())
                if (inv.LoadState(folder) is FolderLoad.Loaded or FolderLoad.Failed)
                {
                    _waitingFolders.Remove(folder);
                    if (inv.LoadState(folder) == FolderLoad.Loaded)
                        QueueFolder(e, inv, folder);
                }
        if (shown && o is not null && o.FullId != _askedFor)
        {
            _askedFor = o.FullId;
            e!.RequestContents();
        }
        if (o is null)
            _askedFor = Guid.Empty;
        var items = e?.Contents ?? [];
        var agent = _agent();
        var names = items.Select(i => TaskItemRules.DisplayName(i, agent, k => _s.Get(k))).ToList();
        if (!items.Select(i => i.ItemId).SequenceEqual(_rows) || !names.SequenceEqual(_shownNames))
        {
            var chosen = Chosen();
            _list.Clear();
            _rows.Clear();
            _shownNames.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                _list.AddItem(names[i]);
                _rows.Add(items[i].ItemId);
                _shownNames.Add(names[i]);
                if (chosen == items[i].ItemId)
                    _list.Select(i);
            }
        }
        var current = Chosen() is { } c ? items.FirstOrDefault(i => i.ItemId == c) : null;
        _newScript.Disabled = !modify;
        _delete.Disabled = !(modify || youOwn) || current is null;
        _copy.Disabled = current is null;
        _open.Disabled = current is null || TaskItemRules.OpenKind(current) == TaskOpenKind.None;
        _properties.Disabled = current is null;
        _rename.Disabled = current is null || !modify || !TaskItemRules.CanRename(current, agent);
        if (_props.Visible)
        {
            if (items.FirstOrDefault(i => i.ItemId == _propItem) is { } shownItem)
                FillProperties(shownItem);
            else if (e is { ContentsKnown: true })
                _props.Visible = false;   // the item left the object
        }
        if (e is { Pending: 0 } && _status.Text.Length > 0 && o is null)
            _status.Text = string.Empty;
        if (o is not null && e is { ContentsKnown: false })
            _status.Text = _s.Get("edit.content.loading");
        else if (o is not null && _status.Text == _s.Get("edit.content.loading"))
            _status.Text = items.Count == 0 ? _s.Get("edit.content.empty") : string.Empty;
    }

    /// <summary>A line under the list (a refusal from the host: a script that may not be opened).</summary>
    public void Say(string text) => _status.Text = text;

    /// <summary>The list, for the automation: the item names shown.</summary>
    public IReadOnlyList<string> Names => Enumerable.Range(0, _list.ItemCount).Select(i => _list.GetItemText(i)).ToList();

    /// <summary>Chooses the row with this name, for the automation (as a click on it would).</summary>
    public bool Choose(string name)
    {
        for (int i = 0; i < _list.ItemCount; i++)
            if (_list.GetItemText(i) == name)
            {
                _list.Select(i);
                return true;
            }
        return false;
    }

    /// <summary>The id of the row with this name, for the automation; empty when none.</summary>
    public Guid IdOf(string name)
    {
        for (int i = 0; i < _list.ItemCount && i < _rows.Count; i++)
            if (_list.GetItemText(i) == name)
                return _rows[i];
        return Guid.Empty;
    }

    /// <summary>The Open button (the live check clicks it).</summary>
    public Button OpenButton => _open;

    /// <summary>The list control (the live check drags onto it).</summary>
    public Control List => _list;

    /// <summary>The properties box, its name field and its lines, for the automation.</summary>
    public Control Properties => _props;
    public LineEdit PropertyName => _propName;
    public IReadOnlyList<string> PropertyLines => [_propCreator.Text, _propOwner.Text, _propYou.Text, _propNext.Text, _propAnyone.Text];
}

/// <summary>The Content tab's list: it takes inventory items and folders dragged from the inventory window, and a row dragged out of it
/// carries the item to an inventory folder.</summary>
public partial class ContentDropList : ItemList
{
    public Func<IReadOnlyList<Guid>, bool>? Accepts;
    public event Action<IReadOnlyList<Guid>>? Dropped;

    /// <summary>The drag a row starts, or null when it may not leave.</summary>
    public Func<int, TaskDrag?>? DragOut;

    private static IReadOnlyList<Guid> Ids(Variant data) =>
        data.VariantType == Variant.Type.String && TaskDrag.Decode(data.AsString()) is null ? InventoryDragSet.Decode(data.AsString()) : Array.Empty<Guid>();

    public override bool _CanDropData(Vector2 atPosition, Variant data) => Accepts?.Invoke(Ids(data)) ?? false;

    public override void _DropData(Vector2 atPosition, Variant data) => Dropped?.Invoke(Ids(data));

    public override Variant _GetDragData(Vector2 atPosition)
    {
        int index = GetItemAtPosition(atPosition, true);
        if (index < 0 || DragOut?.Invoke(index) is not { } drag)
            return default;
        SetDragPreview(new Label { Text = GetItemText(index) });
        return drag.Encode();
    }
}
