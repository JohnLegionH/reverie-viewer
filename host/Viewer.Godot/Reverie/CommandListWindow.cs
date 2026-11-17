// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The command list (Ctrl+Shift+K, or a click on the top bar's search box): one search over every window, menu command and setting,
/// which also takes the official viewer's and Firestorm's names. A search field, then the results grouped as Best match, Windows,
/// Commands and Settings; each result shows its name, where it lives in this viewer, its shortcut, and where the official viewer keeps it
/// (Firestorm, when the official viewer has no place for it). With the field empty it shows the recent picks. Up and Down move, Enter
/// opens, Ctrl+Enter opens the menu or window it lives in and highlights it there, Esc closes.
/// </summary>
public sealed partial class CommandListWindow : PanelContainer
{
    public const float Width = 600, ListHeight = 380;

    private readonly StringTable _strings;
    private readonly Func<CommandIndex> _index;
    private readonly CommandRecents _recents;
    private readonly LineEdit _field;
    private readonly VBoxContainer _list;
    private readonly ScrollContainer _scroll;
    private readonly Label _notBuilt;
    private readonly List<PanelContainer> _rows = new();
    private CommandListState _state;

    /// <summary>Opens a result (Enter or a click).</summary>
    public Action<CommandItem>? OpenItem { get; set; }

    /// <summary>Shows where a result lives: its menu opened at it, or Preferences at its row (Ctrl+Enter).</summary>
    public Action<CommandItem>? ShowPlace { get; set; }

    public LineEdit Field => _field;
    public CommandListState State => _state;

    /// <summary>The result rows shown now, in keyboard order.</summary>
    public IReadOnlyList<PanelContainer> Rows => _rows;

    /// <summary>The line naming a feature this viewer does not have yet (hidden when there is none).</summary>
    public Label NotBuiltLine => _notBuilt;

    public CommandListWindow(StringTable strings, Func<CommandIndex> index, CommandRecents recents)
    {
        _strings = strings;
        _index = index;
        _recents = recents;
        _state = new CommandListState(index(), recents);
        Name = "command_list";
        ThemeTypeVariation = ReverieStyle.Popup;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        AccessibilityName = strings.Get("cmdlist.title");
        CustomMinimumSize = new Vector2(Width, 0);

        var box = new VBoxContainer { Name = "box" };
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);

        _field = new LineEdit
        {
            Name = "field", PlaceholderText = strings.Get("cmdlist.placeholder"), ThemeTypeVariation = ReverieStyle.LocationEdit,
            ClearButtonEnabled = true, CustomMinimumSize = new Vector2(0, 36), RightIcon = ReverieIcons.Get(LucideIcons.Search),
        };
        _field.AccessibilityName = strings.Get("cmdlist.field");
        _field.TextChanged += t => { _state.SetQuery(t); Fill(); };
        _field.GuiInput += OnFieldKey;
        box.AddChild(_field);

        _scroll = new ScrollContainer { Name = "scroll", CustomMinimumSize = new Vector2(0, ListHeight), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _list = new VBoxContainer { Name = "results", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 2);
        _scroll.AddChild(_list);
        box.AddChild(_scroll);

        _notBuilt = new Label { Name = "not_built", ThemeTypeVariation = ReverieStyle.Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        box.AddChild(_notBuilt);

        var hint = new Label { Name = "keys", ThemeTypeVariation = ReverieStyle.Caption, Text = strings.Get("cmdlist.keys") };
        box.AddChild(hint);
    }

    public bool IsOpen => Visible;

    /// <summary>Opens the list with an empty field (the recent picks show), read from the menus and Preferences as they are now.</summary>
    public void Open()
    {
        _state = new CommandListState(_index(), _recents);
        Visible = true;
        _field.Text = "";
        _state.SetQuery("");
        Fill();
        _field.GrabFocus();
    }

    public void Close()
    {
        Visible = false;
        if (_field.HasFocus())
            _field.ReleaseFocus();
    }

    public void Toggle()
    {
        if (Visible)
            Close();
        else
            Open();
    }

    /// <summary>Types a query as a person would (the field's text, then the results).</summary>
    public void Type(string text)
    {
        _field.Text = text;
        _state.SetQuery(text);
        Fill();
    }

    /// <summary>A key in the list: "Up", "Down", "Enter" (with Ctrl: show its place) or "Escape".</summary>
    public void Press(string key, bool control = false)
    {
        var (action, item) = _state.Key(key, control);
        switch (action)
        {
            case CommandListAction.Close:
                Close();
                return;
            case CommandListAction.Open when item is not null:
                Close();
                OpenItem?.Invoke(item);
                return;
            case CommandListAction.ShowPlace when item is not null:
                Close();
                ShowPlace?.Invoke(item);
                return;
        }
        Highlight();
    }

    private void OnFieldKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k)
            return;
        string? key = k.Keycode switch
        {
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Enter or Key.KpEnter => "Enter",
            Key.Escape => "Escape",
            _ => null,
        };
        if (key is null)
            return;
        Press(key, k.CtrlPressed);
        _field.AcceptEvent();
    }

    /// <summary>Esc closes the list from anywhere in it, and a press outside it closes it.</summary>
    public override void _Input(InputEvent e)
    {
        if (!Visible)
            return;
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventMouseButton { Pressed: true } b && !GetGlobalRect().HasPoint(b.Position))
            Close();
    }

    private void Fill()
    {
        foreach (var c in _list.GetChildren())
        {
            _list.RemoveChild(c);
            c.QueueFree();
        }
        _rows.Clear();
        if (_state.Query.Trim().Length == 0)
        {
            if (_state.Recent.Count > 0)
                Heading("cmdlist.recent");
            foreach (var item in _state.Recent)
                AddRow(item);
            if (_state.Recent.Count == 0)
                _list.AddChild(new Label { Name = "empty", ThemeTypeVariation = ReverieStyle.Muted, Text = _strings.Get("cmdlist.start") });
        }
        else
        {
            var g = _state.Groups;
            if (g.Best is { } best)
            {
                Heading("cmdlist.best");
                AddRow(best);
            }
            Group("cmdlist.windows", g.Windows);
            Group("cmdlist.commands", g.Commands);
            Group("cmdlist.settings", g.Settings);
            if (g.Best is null)
                _list.AddChild(new Label { Name = "empty", ThemeTypeVariation = ReverieStyle.Muted, Text = _strings.Get("cmdlist.none") });
        }
        if (_state.NotBuilt is { } row)
        {
            string where = row.SlPlaces.Count > 0
                ? _strings.Get("cmdlist.not_built.sl", CommandText.Leaf(row.SlPlaces[0]), CommandText.Shown(row.SlPlaces[0]) + (row.SlKey is { } k && !k.Equals("none", StringComparison.OrdinalIgnoreCase) ? " (" + k + ")" : ""))
                : _strings.Get("cmdlist.not_built.fs", CommandText.Leaf(row.FsPlaces[0]), CommandText.Shown(row.FsPlaces[0]));
            _notBuilt.Text = where;
            _notBuilt.Visible = true;
        }
        else
            _notBuilt.Visible = false;
        Highlight();
    }

    private void Heading(string key) =>
        _list.AddChild(new Label { Name = "heading_" + key[(key.IndexOf('.') + 1)..], ThemeTypeVariation = ReverieStyle.Overline, Text = _strings.Get(key).ToUpperInvariant() });

    private void Group(string key, IReadOnlyList<CommandItem> items)
    {
        if (items.Count == 0)
            return;
        Heading(key);
        foreach (var item in items)
            AddRow(item);
    }

    private void AddRow(CommandItem item)
    {
        int index = _rows.Count;
        var row = new PanelContainer { Name = "row_" + index, ThemeTypeVariation = ReverieStyle.ListRow, MouseFilter = MouseFilterEnum.Stop, TooltipText = item.Place };
        row.AccessibilityName = item.Name;
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 12);
        row.AddChild(line);

        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 0);
        var name = new Label { Name = "name", Text = item.Name, ClipText = true, Modulate = item.Enabled ? Colors.White : new Color(1, 1, 1, ControlStates.DisabledStrength) };
        var place = new Label { Name = "place", Text = item.Place, ThemeTypeVariation = ReverieStyle.Muted, ClipText = true };
        left.AddChild(name);
        left.AddChild(place);
        line.AddChild(left);

        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkEnd, CustomMinimumSize = new Vector2(Width * 0.42f, 0) };
        right.AddThemeConstantOverride("separation", 0);
        var key = new Label { Name = "shortcut", Text = item.Shortcut ?? "", HorizontalAlignment = HorizontalAlignment.Right, ThemeTypeVariation = ReverieStyle.Muted };
        string elsewhere = item.ElsewherePlace is { } p
            ? _strings.Get(item.ElsewhereViewer == "SL" ? "cmdlist.in_sl" : "cmdlist.in_fs", p + (item.ElsewhereShortcut is { } s ? " (" + s + ")" : ""))
            : "";
        var other = new Label { Name = "elsewhere", Text = elsewhere, HorizontalAlignment = HorizontalAlignment.Right, ThemeTypeVariation = ReverieStyle.Muted, ClipText = true };
        right.AddChild(key);
        right.AddChild(other);
        line.AddChild(right);

        row.GuiInput += e =>
        {
            if (e is InputEventMouseMotion)
            {
                if (_state.Selected != index)
                {
                    _state.Select(index);
                    Highlight();
                }
            }
            else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } b)
            {
                _state.Select(index);
                Press("Enter", b.CtrlPressed);
            }
        };
        _list.AddChild(row);
        _rows.Add(row);
    }

    private void Highlight()
    {
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].ThemeTypeVariation = i == _state.Selected ? ReverieStyle.ListRowSelected : ReverieStyle.ListRow;
        if (_state.Selected >= 0 && _state.Selected < _rows.Count && _rows[_state.Selected].IsInsideTree())
            _scroll.EnsureControlVisible(_rows[_state.Selected]);
    }
}

/// <summary>Installs the command list into the shell: the window over everything, its key, the top bar's search box, and what a result opens.</summary>
public static class CommandListFeature
{
    public static CommandListWindow Install(ShellRoot shell, SettingsService settings, PreferencesPanel? preferences)
    {
        var s = shell.Shell;
        CommandIndex Index() => CommandIndex.Build(TopBarMenuMap.Build(s.Menus, s.Commands), s.Commands.Has, s.Strings, PreferenceCatalog.All,
            preferences is null ? [] : PreferencesPanel.Categories, FeatureMap.Bundled, CommandTargets.Bundled);
        var window = new CommandListWindow(s.Strings, Index, new CommandRecents(settings));
        window.OpenItem = item => Open(shell, preferences, item, highlight: false);
        window.ShowPlace = item => Open(shell, preferences, item, highlight: true);
        shell.CommandList = window;
        shell.AddChild(window);
        shell.TopBar.Search.Pressed += () => { window.Open(); shell.PlaceCommandList(); };
        return window;
    }

    private static void Open(ShellRoot shell, PreferencesPanel? preferences, CommandItem item, bool highlight)
    {
        var s = shell.Shell;
        if (item.PrefKey is { } key && preferences is not null)
        {
            s.Floaters.Open(PreferencesFeature.FloaterId);
            preferences.ShowSetting(key);
        }
        else if (item.PrefPageId is { } page && preferences is not null)
        {
            s.Floaters.Open(PreferencesFeature.FloaterId);
            preferences.ShowCategory(page);
        }
        else if (highlight && item.MenuPath.Count > 0)
            shell.OpenMenuAt(item.MenuPath);
        else if (item.CommandId is { } command && item.Enabled)
        {
            // a window opens (a toggle would close one already open)
            if (command.StartsWith("floater.", StringComparison.Ordinal) && s.Floaters.Specs.Any(f => "floater." + f.Id == command) && s.Floaters.IsOpen(command[8..]))
                s.Floaters.Open(command[8..]);
            else
                s.Commands.Execute(command);
        }
    }
}
