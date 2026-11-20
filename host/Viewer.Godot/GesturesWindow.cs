// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Inventory;

namespace Viewer.Godot;

/// <summary>Adds the Gestures window to the shell: a floater and its entry in the Me (Avatar) menu, on Ctrl+G.</summary>
public static class GesturesFeature
{
    public const string FloaterId = "gestures";

    public static GesturesWindow Install(ShellRoot shell)
    {
        var s = shell.Shell;
        var window = new GesturesWindow(s.Strings);
        // the official window is 313 by 465, at least 240 by 350 (floater_gesture.xml); ours is wider for the three columns
        shell.AddFloater(new FloaterSpec(FloaterId, "gestures.title", new UiSize(420, 480), new UiSize(300, 350)), window);
        // Avatar > Gestures... toggles the window and is checked while it is open (menu_viewer.xml "Gestures": Floater.Toggle and
        // Floater.Visible "gestures", shortcut control|G, which ViewerShortcuts gives the entry)
        s.Commands.Register("floater.gestures", () => s.Floaters.Toggle(FloaterId));
        s.Menus.AddEntry("me", new MenuEntry("me.gestures", "menu.me.gestures", "floater.gestures", Order: 7, Checked: () => s.Floaters.IsOpen(FloaterId)));
        s.Floaters.Opened += id => { if (id == FloaterId) window.Opened?.Invoke(); };
        return window;
    }
}

/// <summary>
/// The Gestures window, as the official one (llfloatergesture.cpp, floater_gesture.xml): a list of the gestures in the Gestures folder
/// and every active one, with the columns the official list shows (a check mark for an active gesture, whose name is also bold; the
/// name; the chat trigger; the key). A click chooses a row, Ctrl+click adds or takes a row out of the choice (multi_select); a
/// double-click plays the gesture (setDoubleClickCallback onClickPlay). A click on a column header sorts by it (the same column again
/// reverses it). Below: make a new gesture (new_gesture_btn), Delete (asks first, then moves the choice to the Trash), the gear menu
/// with every action, activate or deactivate the choice (activate_btn), Edit (opens the gesture editor) and Play, the default button
/// (setDefaultBtn "play_btn"), which Stop takes the place of while the chosen gesture plays (stop_btn; llfloatergesture.cpp
/// onClickPlay and updateButtons). The rows, the choice and which buttons work are <see cref="GestureList"/>'s; this draws them in
/// the shared Reverie components.
/// </summary>
public sealed partial class GesturesWindow : VBoxContainer
{
    private readonly StringTable _strings;
    private readonly VBoxContainer _rows;
    private readonly ScrollContainer _scroll;
    private readonly Label _empty;
    private readonly List<Button> _rowButtons = new();
    private readonly List<TextureRect> _marks = new();
    private readonly TextureRect[] _sortArrows = new TextureRect[3];
    private readonly HBoxContainer _confirmBar;
    private readonly Label _confirmText;
    private IReadOnlyList<Guid> _pendingDelete = [];

    public GestureList List { get; } = new();
    public RvIconButton NewButton { get; }
    public RvButton ActivateButton { get; }
    public RvButton EditButton { get; }
    public RvButton PlayButton { get; }
    public RvButton StopButton { get; }
    public RvIconButton DeleteButton { get; }
    public MenuButton GearButton { get; }
    public RvButton ConfirmYes { get; }
    public RvButton ConfirmNo { get; }
    public Label ConfirmText => _confirmText;
    public bool Confirming => _confirmBar.Visible;

    /// <summary>The rows drawn now, in list order.</summary>
    public IReadOnlyList<Button> RowButtons => _rowButtons;

    /// <summary>The window has opened (the rows are read again).</summary>
    public Action? Opened { get; set; }

    /// <summary>Make new gesture (onClickNew).</summary>
    public Action? NewRequested { get; set; }

    /// <summary>The activate button: the gestures to activate (true) or deactivate (false).</summary>
    public Action<IReadOnlyList<(Guid ItemId, bool Activate)>>? ToggleRequested { get; set; }

    /// <summary>Edit: opens the gesture in the gesture editor (onClickEdit).</summary>
    public Action<Guid>? EditRequested { get; set; }

    /// <summary>Play or a double-click: plays the gesture, activating it first when it is not active (onClickPlay).</summary>
    public Action<Guid>? PlayRequested { get; set; }

    /// <summary>Stop: the chosen gestures that are playing.</summary>
    public Action<IReadOnlyList<Guid>>? StopRequested { get; set; }

    /// <summary>Delete, once the question is answered Yes: the chosen gestures, to be moved to the Trash.</summary>
    public Action<IReadOnlyList<Guid>>? DeleteRequested { get; set; }

    public GesturesWindow(StringTable strings)
    {
        _strings = strings;
        Name = "gestures";
        AccessibilityName = strings.Get("gestures.title");
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var header = new Control { Name = "header", CustomMinimumSize = new Vector2(0, ReverieTheme.ListRow), MouseFilter = MouseFilterEnum.Ignore };
        header.AddChild(Columns(null, HeaderCell(GestureColumn.Name, "gestures.col.name"), HeaderCell(GestureColumn.Trigger, "gestures.col.trigger"),
            HeaderCell(GestureColumn.Key, "gestures.col.key")));
        AddChild(header);

        _scroll = new ScrollContainer { Name = "scroll", SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FocusMode = FocusModeEnum.All };
        _scroll.AccessibilityName = strings.Get("gestures.list");
        _scroll.GuiInput += OnListKey;
        _rows = new VBoxContainer { Name = "gesture_list", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _scroll.AddChild(_rows);
        AddChild(_scroll);

        _empty = new Label { Name = "empty", ThemeTypeVariation = ReverieStyle.Muted, Text = strings.Get("gestures.empty"), AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        AddChild(_empty);

        // the question before Delete moves gestures to the Trash, in the window (as the inventory's does), not in a dialog
        _confirmBar = new HBoxContainer { Name = "confirm_bar", Visible = false };
        _confirmText = new Label { Name = "confirm_text", SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _confirmText.AccessibilityName = strings.Get("inventory.confirm.bar");
        ConfirmYes = new RvButton(strings.Get("inventory.confirm.yes"), primary: true) { Name = "confirm_yes" };
        ConfirmYes.Pressed += () => { var chosen = _pendingDelete; CancelDelete(); if (chosen.Count > 0) DeleteRequested?.Invoke(chosen); };
        ConfirmNo = new RvButton(strings.Get("inventory.confirm.no")) { Name = "confirm_no" };
        ConfirmNo.Pressed += CancelDelete;
        _confirmBar.AddChild(_confirmText);
        _confirmBar.AddChild(ConfirmYes);
        _confirmBar.AddChild(ConfirmNo);
        AddChild(_confirmBar);

        var bar = new HBoxContainer { Name = "bottom_panel" };
        NewButton = new RvIconButton(LucideIcons.Add, strings.Get("gestures.new")) { Name = "new_gesture_btn" };
        NewButton.Pressed += () => NewRequested?.Invoke();
        bar.AddChild(NewButton);
        DeleteButton = new RvIconButton(LucideIcons.Trash, strings.Get("gestures.delete.tip")) { Name = "delete_btn" };
        DeleteButton.Pressed += AskDelete;
        bar.AddChild(DeleteButton);
        GearButton = new MenuButton
        {
            Name = "gear_btn", Icon = ReverieIcons.Get(LucideIcons.More), TooltipText = strings.Get("gestures.menu"), ThemeTypeVariation = ReverieStyle.IconButton,
            CustomMinimumSize = new Vector2(ReverieTheme.IconButton, ReverieTheme.IconButton), IconAlignment = HorizontalAlignment.Center, ExpandIcon = false,
        };
        GearButton.AccessibilityName = strings.Get("gestures.menu");
        var gear = GearButton.GetPopup();
        foreach (var (id, key) in new[] { (GearActivate, "gestures.activate"), (GearEdit, "gestures.edit"), (GearPlay, "gestures.play"), (GearStop, "gestures.stop"), (GearDelete, "gestures.delete"), (GearNew, "gestures.new") })
            gear.AddItem(strings.Get(key), id);
        gear.IdPressed += OnGear;
        bar.AddChild(GearButton);
        ActivateButton = new RvButton(strings.Get("gestures.activate")) { Name = "activate_btn", TooltipText = strings.Get("gestures.activate.tip") };
        ActivateButton.Pressed += () => { if (List.Toggle() is { Count: > 0 } changes) ToggleRequested?.Invoke(changes); };
        bar.AddChild(ActivateButton);
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        EditButton = new RvButton(strings.Get("gestures.edit")) { Name = "edit_btn", TooltipText = strings.Get("gestures.edit.tip") };
        EditButton.Pressed += () => { if (List.Buttons.Edit) EditRequested?.Invoke(List.Current); };
        bar.AddChild(EditButton);
        PlayButton = new RvButton(strings.Get("gestures.play"), primary: true) { Name = "play_btn", TooltipText = strings.Get("gestures.play.tip") };
        PlayButton.Pressed += Play;
        bar.AddChild(PlayButton);
        StopButton = new RvButton(strings.Get("gestures.stop"), primary: true) { Name = "stop_btn", TooltipText = strings.Get("gestures.stop.tip"), Visible = false };
        StopButton.Pressed += Stop;
        bar.AddChild(StopButton);
        AddChild(bar);
        UpdateButtons();
    }

    private const int GearActivate = 0, GearEdit = 1, GearPlay = 2, GearStop = 3, GearDelete = 4, GearNew = 5;

    /// <summary>A column header: its name over the column, with an arrow on the column the rows are sorted by (pointing up when ascending);
    /// a click sorts by the column.</summary>
    private Control HeaderCell(GestureColumn column, string key)
    {
        var cell = new HBoxContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = _strings.Get("gestures.sort.tip") };
        var label = new Label { Text = _strings.Get(key).ToUpperInvariant(), ThemeTypeVariation = ReverieStyle.Overline, MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var arrow = new TextureRect
        {
            Name = "sort_arrow", Texture = ReverieIcons.Get(LucideIcons.Down), CustomMinimumSize = new Vector2(ReverieTheme.IconSmall, ReverieTheme.IconSmall), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore, Visible = false,
        };
        cell.AddChild(label);
        cell.AddChild(arrow);
        _sortArrows[(int)column] = arrow;
        cell.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                return;
            SortBy(column);
            cell.AcceptEvent();
        };
        return cell;
    }

    /// <summary>A click on a column header, as a person makes it: sorts by the column, or reverses the order when it is the sorted one.</summary>
    public void SortBy(GestureColumn column)
    {
        List.SortBy(column);
        Fill();
    }

    /// <summary>The arrow shows on the sorted column only: up for ascending, down for descending.</summary>
    private void ShowSort()
    {
        for (int i = 0; i < _sortArrows.Length; i++)
        {
            bool on = i == (int)List.SortColumn;
            _sortArrows[i].Visible = on;
            _sortArrows[i].FlipV = on && List.SortAscending;
        }
    }

    /// <summary>The columns of a row or of the header, laid over the whole row inside its padding: the mark, then name, chat and key
    /// sharing the width two to one to one.</summary>
    private static HBoxContainer Columns(TextureRect? mark, Control name, Control trigger, Control key)
    {
        var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        line.SetAnchorsPreset(LayoutPreset.FullRect);
        line.OffsetLeft = 8;    // the list row's own padding (ReverieStyle ListRow)
        line.OffsetRight = -8;
        line.AddChild(mark ?? new Control { CustomMinimumSize = new Vector2(ReverieTheme.IconSmall, 0), MouseFilter = MouseFilterEnum.Ignore });
        foreach (var (cell, ratio) in new[] { (name, 2f), (trigger, 1f), (key, 1f) })
        {
            cell.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            cell.SizeFlagsStretchRatio = ratio;
            if (cell is Label label)
            {
                label.VerticalAlignment = VerticalAlignment.Center;
                label.ClipText = true;
                label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            }
            line.AddChild(cell);
        }
        return line;
    }

    /// <summary>Shows the rows (the choice is kept where its rows remain, else the first row is chosen).</summary>
    public void Show(IReadOnlyList<GestureListRow> rows)
    {
        List.SetRows(rows);
        Fill();
    }

    /// <summary>A click on a row, as a person makes it (with Ctrl held: <paramref name="add"/>).</summary>
    public void ClickRow(Guid itemId, bool add)
    {
        List.Click(itemId, add);
        Highlight();
        UpdateButtons();
    }

    private void Play()
    {
        if (List.Buttons.Play)
            PlayRequested?.Invoke(List.Current);
    }

    private void Stop()
    {
        if (List.ToStop() is { Count: > 0 } playing)
            StopRequested?.Invoke(playing);
    }

    /// <summary>Which gestures play now (the Stop button follows the choice).</summary>
    public void SetPlaying(IEnumerable<Guid> items)
    {
        List.SetPlaying(items);
        UpdateButtons();
    }

    /// <summary>Delete: asks first, naming the gesture, or how many gestures there are.</summary>
    private void AskDelete()
    {
        var chosen = List.ToDelete();
        if (chosen.Count == 0)
            return;
        _pendingDelete = chosen;
        _confirmText.Text = chosen.Count == 1
            ? _strings.Get("inventory.confirm.question", List.Rows.FirstOrDefault(r => r.ItemId == chosen[0])?.Name ?? "")
            : _strings.Get("gestures.delete.many", new Counted("count.gestures", chosen.Count));
        _confirmBar.Visible = true;
    }

    private void CancelDelete()
    {
        _pendingDelete = [];
        _confirmBar.Visible = false;
    }

    private void OnGear(long id)
    {
        switch ((int)id)
        {
            case GearActivate when List.Toggle() is { Count: > 0 } changes:
                ToggleRequested?.Invoke(changes);
                break;
            case GearEdit when List.Buttons.Edit:
                EditRequested?.Invoke(List.Current);
                break;
            case GearPlay:
                Play();
                break;
            case GearStop:
                Stop();
                break;
            case GearDelete:
                AskDelete();
                break;
            case GearNew:
                NewRequested?.Invoke();
                break;
        }
    }

    private void Fill()
    {
        foreach (var c in _rows.GetChildren())
        {
            _rows.RemoveChild(c);
            c.QueueFree();
        }
        _rowButtons.Clear();
        _marks.Clear();
        foreach (var row in List.Rows)
        {
            var id = row.ItemId;
            var button = new Button
            {
                Name = "row_" + _rowButtons.Count, ThemeTypeVariation = ReverieStyle.ListRow, CustomMinimumSize = new Vector2(0, ReverieTheme.ListRow),
                FocusMode = FocusModeEnum.None, TooltipText = row.Name,
            };
            button.AccessibilityName = row.Active ? row.Name + ", " + _strings.Get("gestures.col.active") : row.Name;
            var mark = new TextureRect
            {
                Name = "active", Texture = row.Active ? ReverieIcons.Get(LucideIcons.Check) : null, CustomMinimumSize = new Vector2(ReverieTheme.IconSmall, ReverieTheme.IconSmall),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore,
            };
            _marks.Add(mark);
            // an active gesture's name is bold, as the official list sets it (addGesture: LLFontGL::BOLD)
            var name = new Label { Name = "name", Text = row.Name, ThemeTypeVariation = row.Active ? ReverieStyle.CardHeading : ReverieStyle.Muted, MouseFilter = MouseFilterEnum.Ignore };
            var trigger = new Label { Name = "trigger", Text = row.Trigger, ThemeTypeVariation = ReverieStyle.Muted, MouseFilter = MouseFilterEnum.Ignore };
            var key = new Label { Name = "shortcut", Text = row.Shortcut, ThemeTypeVariation = ReverieStyle.Muted, MouseFilter = MouseFilterEnum.Ignore };
            button.AddChild(Columns(mark, name, trigger, key));
            button.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } b)
                    return;
                if (b.DoubleClick)
                {
                    ClickRow(id, add: false);
                    Play();
                }
                else
                    ClickRow(id, b.CtrlPressed);
                _scroll.GrabFocus();
                button.AcceptEvent();
            };
            _rows.AddChild(button);
            _rowButtons.Add(button);
        }
        _empty.Visible = List.Rows.Count == 0;
        ShowSort();
        Tint();
        Highlight();
        UpdateButtons();
    }

    /// <summary>Up and Down move the choice, Enter plays (the default button).</summary>
    private void OnListKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k || List.Rows.Count == 0)
            return;
        int at = List.Rows.ToList().FindIndex(r => r.ItemId == List.Current);
        switch (k.Keycode)
        {
            case Key.Up:
                ClickRow(List.Rows[Math.Max(0, at - 1)].ItemId, add: false);
                break;
            case Key.Down:
                ClickRow(List.Rows[Math.Min(List.Rows.Count - 1, at + 1)].ItemId, add: false);
                break;
            case Key.Enter or Key.KpEnter:
                Play();
                break;
            default:
                return;
        }
        _scroll.AcceptEvent();
    }

    private void Highlight()
    {
        var chosen = List.Selected;
        for (int i = 0; i < _rowButtons.Count && i < List.Rows.Count; i++)
        {
            bool on = chosen.Contains(List.Rows[i].ItemId);
            _rowButtons[i].ThemeTypeVariation = on ? ReverieStyle.ListRowSelected : ReverieStyle.ListRow;
            if (on && List.Rows[i].ItemId == List.Current && _rowButtons[i].IsInsideTree())
                _scroll.EnsureControlVisible(_rowButtons[i]);
        }
    }

    private void UpdateButtons()
    {
        var b = List.Buttons;
        ActivateButton.Text = _strings.Get(b.Toggle == GestureToggle.Deactivate ? "gestures.deactivate" : "gestures.activate");
        ActivateButton.AccessibilityName = ActivateButton.Text;
        ActivateButton.Disabled = b.Toggle == GestureToggle.None;
        EditButton.Disabled = !b.Edit;
        PlayButton.Disabled = !b.Play;
        // Stop takes Play's place while the chosen gesture plays
        PlayButton.Visible = !b.Stop;
        StopButton.Visible = b.Stop;
        DeleteButton.Disabled = !b.Delete;
        var gear = GearButton.GetPopup();
        gear.SetItemText(gear.GetItemIndex(GearActivate), _strings.Get(b.Toggle == GestureToggle.Deactivate ? "gestures.deactivate" : "gestures.activate"));
        gear.SetItemDisabled(gear.GetItemIndex(GearActivate), b.Toggle == GestureToggle.None);
        gear.SetItemDisabled(gear.GetItemIndex(GearEdit), !b.Edit);
        gear.SetItemDisabled(gear.GetItemIndex(GearPlay), !b.Play);
        gear.SetItemDisabled(gear.GetItemIndex(GearStop), !b.Stop);
        gear.SetItemDisabled(gear.GetItemIndex(GearDelete), !b.Delete);
    }

    /// <summary>The check marks take the list rows' text colour, so they show in the light and the dark look.</summary>
    private void Tint()
    {
        foreach (var mark in _marks)
            mark.SelfModulate = GetThemeColor("font_color", ReverieStyle.ListRow);
        foreach (var arrow in _sortArrows)
            if (arrow is not null)
                arrow.SelfModulate = GetThemeColor("font_color", ReverieStyle.Overline);
    }

    public override void _Notification(int what)
    {
        if (what is (int)NotificationThemeChanged or (int)NotificationEnterTree)
            Tint();
    }
}
