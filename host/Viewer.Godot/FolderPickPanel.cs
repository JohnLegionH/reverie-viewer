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
/// Folder. A search field narrows the list. It is the content of a window of its own (title bar and close button) that stays open while the
/// person clicks elsewhere, until Take (or a double-click on a row) or Cancel. Standard controls styled through the Theme alone. Main thread only.
/// </summary>
public partial class FolderPickPanel : VBoxContainer
{
    public const string FloaterId = "edit_take_folder";

    private InventoryModel? _model;
    private readonly Action<Guid> _use;
    private readonly Action _close;
    private readonly ItemList _list;
    private readonly LineEdit _search;
    private List<(Guid Id, string Path)> _rows = new();
    private string _shown = "\0";
    private int _revision = -1;

    public ItemList List => _list;

    /// <param name="use">Takes the selected objects into the chosen folder.</param>
    /// <param name="close">Closes the window this panel is shown in.</param>
    public FolderPickPanel(StringTable strings, Action<Guid> use, Action close)
    {
        _use = use;
        _close = close;
        Name = "folder_pick";
        _search = new LineEdit { Name = "folder_search", PlaceholderText = strings.Get("appearance.texture.search") };
        _search.AccessibilityName = strings.Get("appearance.texture.search");
        AddChild(_search);
        _list = new ItemList { Name = "folder_list", CustomMinimumSize = new Vector2(320, 300), SizeFlagsVertical = SizeFlags.ExpandFill };
        _list.AccessibilityName = strings.Get("edit.take_folder.title");
        _list.ItemActivated += i => Use((int)i);
        AddChild(_list);
        var row = new HBoxContainer { Name = "folder_buttons", Alignment = BoxContainer.AlignmentMode.End };
        var take = new Button { Name = "folder_use", Text = strings.Get("edit.take_folder.take") };
        take.AccessibilityName = take.Text;
        take.Pressed += () => { var s = _list.GetSelectedItems(); if (s.Length > 0) Use(s[0]); };
        row.AddChild(take);
        var cancel = new Button { Name = "folder_cancel", Text = strings.Get("edit.picker.cancel") };
        cancel.AccessibilityName = cancel.Text;
        cancel.Pressed += () => _close();
        row.AddChild(cancel);
        AddChild(row);
    }

    /// <summary>Shows the folders of <paramref name="model"/>, from the top, with an empty search.</summary>
    public void Begin(InventoryModel model)
    {
        _model = model;
        _search.Clear();
        _shown = "\0";
        _revision = -1;
    }

    public override void _Process(double delta)
    {
        if (_model is not null && IsVisibleInTree() && (_shown != _search.Text || _revision != _model.Revision))
            Fill();
    }

    private static string PathOf(InventoryModel model, InventoryFolderData f)
    {
        var parts = new List<string> { f.Name };
        var at = f.ParentId;
        for (int depth = 0; depth < 32 && at != Guid.Empty && at != model.RootId && model.Folder(at) is { } parent; depth++)
        {
            parts.Insert(0, parent.Name);
            at = parent.ParentId;
        }
        return string.Join(" / ", parts);
    }

    private void Fill()
    {
        if (_model is not { } model)
            return;
        string needle = _search.Text.Trim();
        var trash = model.FolderOfKind(FolderKind.Trash);
        _rows = model.AllFolders()
            .Where(f => f.Id != model.RootId && !model.IsInLibrary(f.Id) && f.Id != trash && !(trash != Guid.Empty && model.IsDescendantOf(f.Id, trash)))
            .Select(f => (f.Id, Path: PathOf(model, f)))
            .Where(r => needle.Length == 0 || r.Path.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Take(500).ToList();
        _shown = _search.Text;
        _revision = model.Revision;
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
        _close();
        return true;
    }

    /// <summary>The rows' paths (for the automation).</summary>
    public IReadOnlyList<string> Paths => _rows.Select(r => r.Path).ToList();
}
