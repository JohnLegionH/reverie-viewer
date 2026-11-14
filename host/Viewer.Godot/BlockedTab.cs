// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Social;

namespace Viewer.Godot;

/// <summary>
/// The People floater's Blocked tab, built by hand in code. It only draws <see cref="BlockedModel"/> (tested without the engine) and
/// hands clicks back to it; every text comes from the string table and every control is named for assistive technology. Behaviour
/// follows the official viewer's blocked list (the entries with their kinds, unblock, block an object by name).
/// </summary>
public partial class BlockedTab : VBoxContainer
{
    private readonly StringTable _strings;
    private static ThemeSpec _theme => UiTheme.Spec;
    private BlockedModel? _model;
    private bool _dirty = true, _syncing;
    private List<BlockedRow> _rows = new();
    private int _selected = -1;

    public ItemList List { get; }
    public Label Note { get; }
    public Label Empty { get; }
    public Label Status { get; }
    public Button UnblockButton { get; }
    public LineEdit NameField { get; }
    public Button BlockNameButton { get; }

    public BlockedTab(StringTable strings)
    {
        _strings = strings;
        Name = "blocked_tab";
        Note = ChatFeature.Note("blocked_note", strings.Get("blocked.asking"), _theme);
        AddChild(Note);
        List = new ItemList { Name = "blocked_list", SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(200, 80), SelectMode = ItemList.SelectModeEnum.Single };
        List.AccessibilityName = strings.Get("blocked.list");
        List.ItemSelected += i => { if (!_syncing) { _selected = (int)i; _dirty = true; } };
        AddChild(List);
        Empty = ChatFeature.Note("blocked_empty", strings.Get("blocked.none"), _theme);
        Empty.Text = strings.Get("blocked.none");
        AddChild(Empty);
        UnblockButton = new Button { Name = "unblock", Text = strings.Get("blocked.unblock") };
        UnblockButton.AccessibilityName = strings.Get("blocked.unblock");
        UnblockButton.Pressed += OnUnblock;
        AddChild(UnblockButton);
        var byName = new HBoxContainer { Name = "block_name_row" };
        NameField = new LineEdit { Name = "block_name", PlaceholderText = strings.Get("blocked.name_prompt"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        NameField.AccessibilityName = strings.Get("blocked.block_name");
        NameField.TextSubmitted += _ => OnBlockName();
        BlockNameButton = new Button { Name = "block_name_button", Text = strings.Get("blocked.block") };
        BlockNameButton.AccessibilityName = strings.Get("blocked.block_name");
        BlockNameButton.Pressed += OnBlockName;
        byName.AddChild(NameField);
        byName.AddChild(BlockNameButton);
        AddChild(byName);
        Status = ChatFeature.Note("blocked_status", strings.Get("blocked.list"), _theme);
        Status.ThemeTypeVariation = UiTheme.ErrorLabel;
        AddChild(Status);
        Bind(null);
    }

    /// <summary>Shows a session's block list (null: no session; everything is off).</summary>
    public void Bind(BlockedModel? model)
    {
        if (_model is not null)
            _model.Changed -= MarkDirty;
        _model = model;
        _selected = -1;
        if (model is not null)
            model.Changed += MarkDirty;
        _dirty = true;
    }

    private void MarkDirty() => _dirty = true;

    private void OnUnblock()
    {
        if (_model is not null && _selected >= 0 && _selected < _rows.Count)
            _model.Unblock(_rows[_selected]);
        _dirty = true;
    }

    private void OnBlockName()
    {
        if (_model is null)
            return;
        if (_model.BlockObjectName(NameField.Text))
            NameField.Clear();
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        if (!_dirty)
            return;
        _dirty = false;
        _syncing = true;
        try { Refresh(); }
        finally { _syncing = false; }
    }

    private void Refresh()
    {
        var m = _model;
        _rows = m?.Rows.ToList() ?? new();
        List.Clear();
        foreach (var r in _rows)
        {
            int i = List.AddItem(_strings.Get("blocked.row", r.Name, _strings.Get(r.TypeKey)));
            List.SetItemTooltip(i, _strings.Get("blocked.unblock.for", r.Name));
        }
        if (_selected >= _rows.Count) _selected = -1;
        if (_selected >= 0) List.Select(_selected);
        Empty.Visible = m is not null && _rows.Count == 0;
        string? note = m?.NoteKey;
        Note.Visible = note is not null;
        if (note is not null)
        {
            Note.Text = _strings.Get(note);
            Note.AccessibilityName = Note.Text;
        }
        UnblockButton.Disabled = m is null || _selected < 0;
        BlockNameButton.Disabled = m is null;
        NameField.Editable = m is not null;
        Status.Visible = m?.Status is not null;
        if (m?.Status is { } st)
        {
            Status.Text = st.Arg is null ? _strings.Get(st.Key) : _strings.Get(st.Key, st.Arg);
            Status.AccessibilityName = Status.Text;
        }
    }
}
