// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Build;
using Viewer.Shell.Inventory;

namespace Viewer.Godot;

/// <summary>
/// The Build window's Content tab, after the official floater_tools.xml Contents panel and LLPanelObjectInventory (read as behaviour): New
/// Script, the prim's items by name, Delete and Copy to inventory for the chosen item, and the list as a drop target for inventory items and
/// whole folders (a folder's items go in one at a time, as Firestorm's Z-drop). The list is asked for when the tab shows a prim. Notecards and
/// scripts inside a prim are not opened: this viewer has no notecard or script editor yet. Standard controls styled through the Theme alone.
/// Main thread only.
/// </summary>
public sealed class EditContentTab
{
    private readonly StringTable _s;
    private readonly Func<EditController?> _edit;
    private readonly Func<InventoryModel?> _inventory;
    private readonly ContentDropList _list;
    private readonly Button _newScript, _delete, _copy;
    private readonly Label _status;
    private Guid _askedFor;
    private readonly List<Guid> _rows = new();
    /// <summary>Folders dropped before their contents had been read from the grid: their items go in once they have.</summary>
    private readonly List<Guid> _waitingFolders = new();

    public EditContentTab(StringTable strings, Func<EditController?> edit, Func<InventoryModel?> inventory, VBoxContainer page)
    {
        _s = strings;
        _edit = edit;
        _inventory = inventory;
        _newScript = Btn("new_script", "edit.content.new_script", () => _edit()?.NewScript(_s.Get("edit.content.new_script.name")));
        page.AddChild(_newScript);
        _list = new ContentDropList { Name = "contents", CustomMinimumSize = new Vector2(0, 220), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.AccessibilityName = strings.Get("edit.content.list");
        _list.Accepts = ids => ids.Count > 0 && _edit()?.Object() is not null;
        _list.Dropped += Drop;
        page.AddChild(_list);
        var row = new HBoxContainer { Name = "content_buttons" };
        _delete = Btn("content_delete", "edit.content.delete", () => { if (Chosen() is { } id) _edit()?.DeleteItem(id); });
        _copy = Btn("content_copy", "edit.content.copy", () => { if (Chosen() is { } id) _edit()?.CopyItemToInventory(id); });
        row.AddChild(_delete);
        row.AddChild(_copy);
        page.AddChild(row);
        _status = new Label { Name = "content_status", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        page.AddChild(_status);
        page.AddChild(new Label { Name = "content_hint", Text = strings.Get("edit.content.hint"), AutowrapMode = TextServer.AutowrapMode.WordSmart });
    }

    private Button Btn(string name, string key, Action pressed)
    {
        var b = new Button { Name = name, Text = _s.Get(key) };
        b.AccessibilityName = b.Text;
        b.Pressed += pressed;
        return b;
    }

    private Guid? Chosen() => _list.GetSelectedItems() is [var i, ..] && i < _rows.Count ? _rows[i] : null;

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
        _status.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, _s.Get("edit.content.folder_queued"), n);
    }

    /// <summary>Shows the primary prim's items, asking the region for them when the tab shows a prim it has not asked about.</summary>
    public void Refresh(bool shown)
    {
        var e = _edit();
        var o = e?.Object();
        bool modify = o is not null && e!.Access(o.Id.LocalId).CanModify;
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
        if (!items.Select(i => i.ItemId).SequenceEqual(_rows))
        {
            _list.Clear();
            _rows.Clear();
            foreach (var i in items)
            {
                _list.AddItem(i.Name);
                _rows.Add(i.ItemId);
            }
        }
        _newScript.Disabled = !modify;
        _delete.Disabled = !modify || Chosen() is null;
        _copy.Disabled = Chosen() is null;
        if (e is { Pending: 0 } && _status.Text.Length > 0 && o is null)
            _status.Text = string.Empty;
        if (o is not null && e is { ContentsKnown: false })
            _status.Text = _s.Get("edit.content.loading");
        else if (o is not null && _status.Text == _s.Get("edit.content.loading"))
            _status.Text = items.Count == 0 ? _s.Get("edit.content.empty") : string.Empty;
    }

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

    /// <summary>The list control (the live check drags onto it).</summary>
    public Control List => _list;
}

/// <summary>The Content tab's list: it takes inventory items and folders dragged from the inventory window.</summary>
public partial class ContentDropList : ItemList
{
    public Func<IReadOnlyList<Guid>, bool>? Accepts;
    public event Action<IReadOnlyList<Guid>>? Dropped;

    private static IReadOnlyList<Guid> Ids(Variant data) =>
        data.VariantType == Variant.Type.String ? InventoryDragSet.Decode(data.AsString()) : Array.Empty<Guid>();

    public override bool _CanDropData(Vector2 atPosition, Variant data) => Accepts?.Invoke(Ids(data)) ?? false;

    public override void _DropData(Vector2 atPosition, Variant data) => Dropped?.Invoke(Ids(data));
}
