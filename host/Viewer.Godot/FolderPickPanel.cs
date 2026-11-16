// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Inventory;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// Chooses one of the account's own inventory folders (not the Library's, not the Trash or anything in it), shown by its path, for Take Into
/// Folder. A search field narrows the list. Standard controls styled through the Theme alone. Main thread only.
/// </summary>
public partial class FolderPickPanel : PopupPanel
{
    private readonly InventoryModel _model;
    private readonly Action<Guid> _use;
    private readonly ItemList _list;
    private readonly LineEdit _search;
    private List<(Guid Id, string Path)> _rows = new();
    private string _shown = "\0";
    private int _revision = -1;

    public ItemList List => _list;

    public FolderPickPanel(StringTable strings, InventoryModel model, Action<Guid> use)
    {
        _model = model;
        _use = use;
        Name = "folder_pick";
        var box = new VBoxContainer();
        box.AddChild(new Label { Name = "folder_pick_title", Text = strings.Get("edit.take_folder.title") });
        _search = new LineEdit { Name = "folder_search", PlaceholderText = strings.Get("appearance.texture.search") };
        _search.AccessibilityName = strings.Get("appearance.texture.search");
        box.AddChild(_search);
        _list = new ItemList { Name = "folder_list", CustomMinimumSize = new Vector2(320, 300) };
        _list.AccessibilityName = strings.Get("edit.take_folder.title");
        _list.ItemActivated += i => Use((int)i);
        box.AddChild(_list);
        var take = new Button { Name = "folder_use", Text = strings.Get("edit.take_folder.take") };
        take.AccessibilityName = take.Text;
        take.Pressed += () => { var s = _list.GetSelectedItems(); if (s.Length > 0) Use(s[0]); };
        box.AddChild(take);
        AddChild(box);
    }

    public override void _Process(double delta)
    {
        if (Visible && (_shown != _search.Text || _revision != _model.Revision))
            Fill();
    }

    private string PathOf(InventoryFolderData f)
    {
        var parts = new List<string> { f.Name };
        var at = f.ParentId;
        for (int depth = 0; depth < 32 && at != Guid.Empty && at != _model.RootId && _model.Folder(at) is { } parent; depth++)
        {
            parts.Insert(0, parent.Name);
            at = parent.ParentId;
        }
        return string.Join(" / ", parts);
    }

    private void Fill()
    {
        string needle = _search.Text.Trim();
        var trash = _model.FolderOfKind(FolderKind.Trash);
        _rows = _model.AllFolders()
            .Where(f => f.Id != _model.RootId && !_model.IsInLibrary(f.Id) && f.Id != trash && !(trash != Guid.Empty && _model.IsDescendantOf(f.Id, trash)))
            .Select(f => (f.Id, Path: PathOf(f)))
            .Where(r => needle.Length == 0 || r.Path.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Take(500).ToList();
        _shown = _search.Text;
        _revision = _model.Revision;
        _list.Clear();
        foreach (var r in _rows)
            _list.AddItem(r.Path);
    }

    /// <summary>Takes into the folder of the row at <paramref name="index"/>.</summary>
    public bool Use(int index)
    {
        if (index < 0 || index >= _rows.Count)
            return false;
        _use(_rows[index].Id);
        Hide();
        return true;
    }

    /// <summary>The rows' paths (for the automation).</summary>
    public IReadOnlyList<string> Paths => _rows.Select(r => r.Path).ToList();
}
