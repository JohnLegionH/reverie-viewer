// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Viewer.Core.Social;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The People window's Nearby tab: the avatars in range, nearest first, with their distance. A double-click sends a message; More (and a
/// right-click) opens the same menu a clicked name has. It only shows what it is given and asks; it reads the list once a second while
/// it is on screen and not at all otherwise.
/// </summary>
public sealed partial class NearbyTab : VBoxContainer
{
    private readonly StringTable _strings;
    private readonly List<Guid> _rows = new();
    private double _wait;
    private Guid _selected;

    public ItemList List { get; }
    public Label Note { get; }
    public Button MessageButton { get; }
    public Button MoreButton { get; }

    /// <summary>Who is near, nearest first, with the name to show for each; null while there is no session.</summary>
    public Func<IReadOnlyList<(NearbyAvatar Avatar, string Name)>?>? Source { get; set; }
    public event Action<Guid>? MessageRequested;
    public Action<Guid>? MenuRequested { get; set; }

    public NearbyTab(StringTable strings)
    {
        _strings = strings;
        Name = "nearby_tab";
        List = new ItemList { Name = "nearby_list", AccessibilityName = strings.Get("nearby.list"), SizeFlagsVertical = SizeFlags.ExpandFill, AllowRmbSelect = true };
        List.ItemSelected += i => { _selected = RowId((int)i); ShowButtons(); };
        List.ItemActivated += i => { var id = RowId((int)i); if (id != Guid.Empty) MessageRequested?.Invoke(id); };
        List.ItemClicked += (i, _, button) =>
        {
            if (button != (long)MouseButton.Right) return;
            _selected = RowId((int)i);
            ShowButtons();
            if (_selected != Guid.Empty) MenuRequested?.Invoke(_selected);
        };
        AddChild(List);
        Note = new Label { Name = "nearby_note", Text = strings.Get("nearby.none"), ThemeTypeVariation = ReverieStyle.Muted };
        AddChild(Note);
        var buttons = new HBoxContainer { Name = "nearby_buttons" };
        MessageButton = new RvButton(strings.Get("friends.message")) { Name = "nearby_message", Disabled = true };
        MessageButton.Pressed += () => { if (_selected != Guid.Empty) MessageRequested?.Invoke(_selected); };
        buttons.AddChild(MessageButton);
        MoreButton = new RvButton(strings.Get("nearby.more")) { Name = "nearby_more", Disabled = true };
        MoreButton.Pressed += () => { if (_selected != Guid.Empty) MenuRequested?.Invoke(_selected); };
        buttons.AddChild(MoreButton);
        AddChild(buttons);
    }

    private Guid RowId(int index) => index >= 0 && index < _rows.Count ? _rows[index] : Guid.Empty;

    private void ShowButtons()
    {
        bool none = _selected == Guid.Empty;
        MessageButton.Disabled = none;
        MoreButton.Disabled = none;
    }

    public void Tick(double delta)
    {
        _wait -= delta;
        if (_wait > 0) return;
        _wait = 1.0;
        Refresh();
    }

    public void Refresh()
    {
        var rows = Source?.Invoke() ?? [];
        bool same = rows.Count == _rows.Count;
        for (int i = 0; same && i < rows.Count; i++)
            same = rows[i].Avatar.Id == _rows[i];
        if (!same)
        {
            List.Clear();
            _rows.Clear();
            foreach (var row in rows)
            {
                _rows.Add(row.Avatar.Id);
                List.AddItem(row.Name);
            }
        }
        for (int i = 0; i < rows.Count; i++)
        {
            string distance = rows[i].Avatar.Distance.ToString("0.0", CultureInfo.CurrentCulture);
            List.SetItemText(i, _strings.Get(rows[i].Avatar.Approximate ? "nearby.row.approximate" : "nearby.row", rows[i].Name, distance));
        }
        int kept = _rows.IndexOf(_selected);
        if (kept < 0) _selected = Guid.Empty;
        else if (!List.IsSelected(kept)) List.Select(kept);
        Note.Visible = rows.Count == 0;
        ShowButtons();
    }
}
