// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Grids;

namespace Viewer.Godot;

/// <summary>A panel drawn over the login screen: a dimmed backdrop that takes the clicks, and a centred window-styled panel.</summary>
public abstract partial class LoginOverlay : Control
{
    protected readonly VBoxContainer Box;

    protected LoginOverlay(string name, string title, float width)
    {
        Name = name;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var scrim = new ColorRect { Name = name + "_scrim", MouseFilter = MouseFilterEnum.Stop, Color = ShellTheme.C(UiTheme.Tokens.Scrim) };
        scrim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(scrim);
        var panel = new PanelContainer { Name = name + "_panel", ThemeTypeVariation = ReverieStyle.Window };
        panel.AnchorLeft = 0.5f; panel.AnchorRight = 0.5f; panel.AnchorTop = 0.5f; panel.AnchorBottom = 0.5f;
        panel.GrowHorizontal = GrowDirection.Both; panel.GrowVertical = GrowDirection.Both;
        AddChild(panel);
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 20);
        panel.AddChild(margin);
        Box = new VBoxContainer { CustomMinimumSize = new Vector2(width, 0) };
        Box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(Box);
        var heading = new Label { Name = "title", Text = title, ThemeTypeVariation = ReverieStyle.WindowTitle };
        heading.AccessibilityName = title;
        Box.AddChild(heading);
    }

    /// <summary>Every named control of the panel, by name, for the automation's layout.</summary>
    public readonly Dictionary<string, Control> ById = new();

    protected T Named<T>(T control, string id) where T : Control
    {
        control.Name = id;
        ById[id] = control;
        return control;
    }

    protected static Label Line(string text, string? variation = null)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = variation ?? "" };
        l.AccessibilityName = text;
        return l;
    }
}

/// <summary>
/// Add a grid (and Edit, for a listed grid): a login-address field and Look up, which reads the grid's grid-info document and shows
/// what it found (the name, the sign-up and password pages, Hypergrid on or off, currency or none), a one-line warning when the address
/// is plain http (it can still be added), and Add grid, which hands the grid back. An address that does not answer gets a plain message.
/// Firestorm's grid manager adds a grid the same way (fsgridhandler.cpp addGrid; llfloaterpreference.cpp onClickAddGrid).
/// </summary>
public partial class GridAddPanel : LoginOverlay
{
    private readonly StringTable _strings;
    private readonly Func<string, Task<GridLookupResult>> _lookup;
    private readonly GridEntry? _editing;
    private readonly LineEdit _address;
    private readonly Label _warning;
    private readonly Label _message;
    private readonly VBoxContainer _found;
    private readonly LineEdit _name;
    private readonly Label _nameLabel;
    private readonly Button _lookUp;
    private readonly Button _add;
    private GridEntry? _result;
    private int _asked;

    /// <summary>Raised once: the grid to add, or null for Cancel.</summary>
    public event Action<GridEntry?>? Done;

    public GridAddPanel(StringTable strings, Func<string, Task<GridLookupResult>> lookup, GridEntry? editing = null)
        : base("grid_add", strings.Get(editing is null ? "grid.add.title" : "grid.edit.title"), 400)
    {
        _strings = strings;
        _lookup = lookup;
        _editing = editing;
        Box.AddChild(Line(strings.Get("grid.add.address.label"), UiTheme.CaptionLabel));
        var row = new HBoxContainer();
        _address = Named(new LineEdit { PlaceholderText = strings.Get("grid.add.address.hint"), SizeFlagsHorizontal = SizeFlags.ExpandFill, Text = editing?.Address ?? "" }, "grid_address");
        _address.AccessibilityName = strings.Get("grid.add.address.label");
        row.AddChild(_address);
        _lookUp = Named(new RvButton(strings.Get("grid.add.lookup")), "grid_lookup");
        row.AddChild(_lookUp);
        Box.AddChild(row);
        _warning = Named(Line(strings.Get(GridLookup.HttpWarningKey), ReverieStyle.WarnText), "grid_http_warning");
        _warning.Visible = false;
        Box.AddChild(_warning);
        _message = Named(Line("", ReverieStyle.Status), "grid_message");
        _message.Visible = false;
        Box.AddChild(_message);
        _found = Named(new VBoxContainer(), "grid_found");
        Box.AddChild(_found);
        _nameLabel = Line(strings.Get("grid.add.name.label"), UiTheme.CaptionLabel);
        _nameLabel.Visible = false;
        Box.AddChild(_nameLabel);
        _name = Named(new LineEdit { Visible = false }, "grid_name_edit");
        _name.AccessibilityName = strings.Get("grid.add.name.label");
        Box.AddChild(_name);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var cancel = Named(new RvButton(strings.Get("grid.add.cancel")), "grid_cancel");
        _add = Named(new RvButton(strings.Get(editing is null ? "grid.add.add" : "grid.add.save"), primary: true), "grid_add_button");
        _add.Disabled = true;
        buttons.AddChild(cancel);
        buttons.AddChild(_add);
        Box.AddChild(buttons);

        _address.TextChanged += _ => { ShowWarning(); _result = null; _add.Disabled = true; };
        _address.TextSubmitted += _ => LookUp();
        _lookUp.Pressed += LookUp;
        cancel.Pressed += () => Finish(null);
        _add.Pressed += () =>
        {
            if (_result is not { } r)
                return;
            string name = _name.Text.Trim();
            bool edited = name.Length > 0 && name != (r.Info?.Name ?? r.Name);
            Finish(r with { Name = name.Length > 0 ? name : r.Name, NameEdited = edited || (_editing?.NameEdited ?? false) && name == _editing.Name });
        };
        ShowWarning();
        if (editing is not null)
        {
            _result = editing;
            ShowFound(editing);
        }
    }

    public override void _Ready() => _address.GrabFocus();

    private void ShowWarning() =>
        _warning.Visible = GridAddress.Tidy(_address.Text).Uri is { } uri && GridLookup.HttpWarning(uri) is not null;

    private async void LookUp()
    {
        int mine = ++_asked;
        _message.Text = _strings.Get("grid.add.looking", _address.Text.Trim());
        _message.Visible = true;
        _lookUp.Disabled = true;
        var r = await _lookup(_address.Text);
        if (mine != _asked || !IsInstanceValid(this))
            return;
        _lookUp.Disabled = false;
        if (r.Entry is null)
        {
            _result = null;
            _add.Disabled = true;
            ClearFound();
            _message.Text = _strings.Get(r.ProblemKey ?? "grid.add.no_answer");
            _message.AccessibilityName = _message.Text;
            return;
        }
        _message.Visible = false;
        _result = _editing is { NameEdited: true } e ? r.Entry with { Name = e.Name, NameEdited = true } : r.Entry;
        ShowFound(_result);
    }

    private void ClearFound()
    {
        foreach (var child in _found.GetChildren())
        {
            _found.RemoveChild(child);
            child.QueueFree();
        }
        _nameLabel.Visible = _name.Visible = false;
    }

    private void ShowFound(GridEntry entry)
    {
        ClearFound();
        var info = entry.Info;
        string none = _strings.Get("grid.add.found.none");
        _found.AddChild(Line(_strings.Get("grid.add.found.name", info?.Name ?? entry.Name)));
        _found.AddChild(Line(_strings.Get("grid.add.found.signup", info?.Register?.ToString() ?? none)));
        _found.AddChild(Line(_strings.Get("grid.add.found.password", info?.Password?.ToString() ?? none)));
        _found.AddChild(Line(_strings.Get(info?.Hypergrid == true ? "grid.add.found.hypergrid_on" : "grid.add.found.hypergrid_off")));
        _found.AddChild(Line(_strings.Get(info?.Currency == true ? "grid.add.found.currency_yes" : "grid.add.found.currency_none")));
        _nameLabel.Visible = _name.Visible = true;
        _name.Text = entry.Name;
        _add.Disabled = false;
    }

    /// <summary>The panel's state for the automation: the warning, the message, what was found and whether Add grid can be pressed.</summary>
    internal object State() => new
    {
        warning = _warning.Visible,
        message = _message.Visible ? _message.Text : null,
        found = _result?.Name,
        can_add = !_add.Disabled,
        busy = _lookUp.Disabled,
    };

    private bool _finished;

    private void Finish(GridEntry? entry)
    {
        if (_finished)
            return;
        _finished = true;
        Done?.Invoke(entry);
        QueueFree();
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Finish(null);
            GetViewport().SetInputAsHandled();
        }
    }
}

/// <summary>
/// Manage grids: the listed grids with Refresh info (reads the grid's grid-info document again), Edit (the Add a grid panel for that grid)
/// and Remove (takes the grid off the list only: its saved names and passwords stay). Firestorm's grid list has the same three
/// (llfloaterpreference.cpp, onClickRefreshGrid, onClickRemoveGrid, and its grid editor).
/// </summary>
public partial class GridManagePanel : LoginOverlay
{
    private readonly StringTable _strings;
    private readonly GridList _grids;
    private readonly Func<GridEntry, Task<GridLookupResult>> _refresh;
    private readonly Action<GridEntry> _edit;
    private readonly ItemList _list;
    private readonly Label _empty;
    private readonly Label _message;
    private readonly Button _refreshButton, _editButton, _removeButton;

    /// <summary>Raised when the list changed (a grid refreshed, edited or removed) and when the panel closes.</summary>
    public event Action? Changed;

    /// <summary>Raised with a grid taken off the list (its pinned certificates are forgotten).</summary>
    public event Action<GridEntry>? Removed;

    public GridManagePanel(StringTable strings, GridList grids, Func<GridEntry, Task<GridLookupResult>> refresh, Action<GridEntry> edit)
        : base("grid_manage", strings.Get("grid.manage.title"), 460)
    {
        _strings = strings;
        _grids = grids;
        _refresh = refresh;
        _edit = edit;
        _list = Named(new ItemList { CustomMinimumSize = new Vector2(0, 200), SelectMode = ItemList.SelectModeEnum.Single }, "grid_list");
        _list.AccessibilityName = strings.Get("grid.manage.title");
        Box.AddChild(_list);
        _empty = Named(Line(strings.Get("grid.manage.empty"), ReverieStyle.Muted), "grid_list_empty");
        Box.AddChild(_empty);
        _message = Named(Line("", ReverieStyle.Status), "grid_manage_message");
        _message.Visible = false;
        Box.AddChild(_message);
        var buttons = new HBoxContainer();
        _refreshButton = Named(new RvButton(strings.Get("grid.manage.refresh")), "grid_refresh");
        _editButton = Named(new RvButton(strings.Get("grid.manage.edit")), "grid_edit");
        _removeButton = Named(new RvButton(strings.Get("grid.manage.remove")), "grid_remove");
        var close = Named(new RvButton(strings.Get("grid.manage.close"), primary: true), "grid_manage_close");
        buttons.AddChild(_refreshButton);
        buttons.AddChild(_editButton);
        buttons.AddChild(_removeButton);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        buttons.AddChild(close);
        Box.AddChild(buttons);
        _list.ItemSelected += _ => ShowButtons();
        _refreshButton.Pressed += Refresh;
        _editButton.Pressed += () => { if (Chosen() is { } e) _edit(e); };
        _removeButton.Pressed += () =>
        {
            if (Chosen() is not { } e)
                return;
            _grids.Remove(e.Key);
            Removed?.Invoke(e);
            Fill();
            Changed?.Invoke();
        };
        close.Pressed += () => { Changed?.Invoke(); QueueFree(); };
        Fill();
    }

    /// <summary>Fills the list again from the grid list (after an edit made elsewhere).</summary>
    public void Fill()
    {
        _list.Clear();
        foreach (var e in _grids.Entries)
        {
            int i = _list.AddItem(_strings.Get("grid.manage.row", e.Name, e.Address));
            _list.SetItemMetadata(i, e.Key);
            _list.SetItemTooltip(i, e.Address);
        }
        _empty.Visible = _grids.Entries.Count == 0;
        _list.Visible = _grids.Entries.Count > 0;
        if (_grids.Entries.Count > 0)
            _list.Select(0);
        ShowButtons();
    }

    private GridEntry? Chosen()
    {
        var picked = _list.GetSelectedItems();
        if (picked.Length == 0)
            return null;
        string key = (string)_list.GetItemMetadata(picked[0]);
        return _grids.Entries.FirstOrDefaultKey(key);
    }

    private void ShowButtons()
    {
        bool any = Chosen() is not null;
        _refreshButton.Disabled = _editButton.Disabled = _removeButton.Disabled = !any;
    }

    private async void Refresh()
    {
        if (Chosen() is not { } e)
            return;
        _message.Text = _strings.Get("grid.manage.refreshing", e.Name);
        _message.Visible = true;
        var r = await _refresh(e);
        if (!IsInstanceValid(this))
            return;
        if (r.Entry is { } fresh)
        {
            string? chosen = _grids.Selected?.Key;
            _grids.Add(fresh);
            if (chosen is not null)
                _grids.Select(chosen);   // refreshing a grid does not choose it
            _message.Visible = false;
            Fill();
            Changed?.Invoke();
        }
        else
            _message.Text = _strings.Get(r.ProblemKey ?? "grid.add.no_answer");
    }

    /// <summary>The rows' centres (viewport coordinates) and the list's state, for the automation.</summary>
    internal object State()
    {
        var rows = new List<object>();
        for (int i = 0; i < _list.ItemCount; i++)
        {
            var r = _list.GetItemRect(i);
            var centre = _list.GetGlobalTransformWithCanvas() * (r.Position + r.Size / 2);
            rows.Add(new { name = _list.GetItemText(i), x = centre.X, y = centre.Y, selected = _list.IsSelected(i) });
        }
        return new { rows, empty = _empty.Visible };
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Changed?.Invoke();
            QueueFree();
            GetViewport().SetInputAsHandled();
        }
    }
}

internal static class GridEntries
{
    public static GridEntry? FirstOrDefaultKey(this IReadOnlyList<GridEntry> entries, string key)
    {
        foreach (var e in entries)
            if (e.Key == key)
                return e;
        return null;
    }
}
