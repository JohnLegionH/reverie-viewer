// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>One entry of a Reverie dropdown menu: an icon in the 16-pixel column, the label, the shortcut text at the right, a check
/// mark, a submenu, or a separator.</summary>
public sealed record RvMenuEntry(string Label, string? Icon = null, string? Shortcut = null, Action? Run = null, bool Enabled = true,
    bool? Checked = null, IReadOnlyList<RvMenuEntry>? Children = null, bool Separator = false, string? Id = null)
{
    public static RvMenuEntry Line { get; } = new("", Separator: true);
}

/// <summary>One menu of the menu bar: its title and the entries it shows when opened (asked for each time it opens, so entries that
/// do not work are left out at that moment).</summary>
public sealed record RvMenu(string Title, Func<IReadOnlyList<RvMenuEntry>> Entries, bool IsApp = false, string? Id = null);

/// <summary>
/// The menu bar: text titles, each opening its dropdown on a click only. With a menu open, moving the pointer onto another title opens
/// that one (the official viewer's behaviour); with none open, moving over the titles opens nothing. Left and Right move between the
/// titles, carrying an open menu along; Enter, Space or Down opens the focused one. Which menu is open is kept by <see cref="MenuBarState"/>.
/// </summary>
public partial class RvMenuBar : HFlowContainer
{
    private readonly List<RvMenu> _menus;
    private readonly List<Button> _titles = new();
    private readonly List<PopupMenu> _popups = new();
    private readonly MenuBarState _state;
    private int _syncing;

    public RvMenuBar(IReadOnlyList<RvMenu> menus, string accessibleName)
    {
        _menus = [.. menus];
        _state = new MenuBarState(menus.Count);
        Name = "rv_menu_bar";
        AccessibilityName = accessibleName;
        // the titles wrap to a second line only when even the menus alone are wider than the window (a large interface in a small
        // window): nothing is ever cut off
        AddThemeConstantOverride("h_separation", 0);
        AddThemeConstantOverride("v_separation", 0);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;   // one line of titles sits in the middle of the bar, as before
        for (int i = 0; i < _menus.Count; i++)
        {
            int index = i;
            var title = new Button
            {
                Name = "title_" + i, Text = _menus[i].Title, ThemeTypeVariation = _menus[i].IsApp ? ReverieStyle.AppTitle : ReverieStyle.MenuTitle, FocusMode = FocusModeEnum.All,
                CustomMinimumSize = new Vector2(0, ReverieTheme.MenuTitleHeight), SizeFlagsVertical = SizeFlags.ShrinkCenter, ActionMode = BaseButton.ActionModeEnum.Press,
            };
            title.AccessibilityName = _menus[i].Title;
            title.Pressed += () => { _state.Click(index); Sync(); };
            title.MouseEntered += () => { _state.Hover(index); Sync(); };
            title.GuiInput += e => Key(e, title);
            _titles.Add(title);
            AddChild(title);
            var popup = new PopupMenu { Name = _menus[i].Id ?? "menu_" + i, ThemeTypeVariation = ReverieStyle.Popup };
            popup.PopupHide += () => { if (_syncing == 0 && _state.Open == index) { _state.Close(); Sync(); } };
            popup.WindowInput += e => PopupKey(e, popup);
            _popups.Add(popup);
            AddChild(popup);
        }
        RestyleApp();
    }

    /// <summary>The open menu's index, or null.</summary>
    public int? OpenMenu => _state.Open;

    /// <summary>The width every title takes on one line (what the top bar's layout counts for the menus).</summary>
    public float OneRowWidth => _titles.Sum(t => t.GetCombinedMinimumSize().X);

    public IReadOnlyList<RvMenu> Menus => _menus;

    /// <summary>Fills a menu's dropdown from its entries now, as opening it does (for checks that read a dropdown's items).</summary>
    public PopupMenu Filled(int index)
    {
        Fill(_popups[index], _menus[index].Entries());
        return _popups[index];
    }

    public IReadOnlyList<Button> Titles => _titles;

    public IReadOnlyList<PopupMenu> Popups => _popups;

    /// <summary>A click on a title, as the pointer gives it (the automation and the tests use it too).</summary>
    public void ClickTitle(int index)
    {
        _state.Click(index);
        Sync();
    }

    /// <summary>The pointer is over a title.</summary>
    public void HoverTitle(int index)
    {
        _state.Hover(index);
        Sync();
    }

    public void CloseMenus()
    {
        _state.Close();
        Sync();
    }

    /// <summary>While a menu is open the dropdown takes the pointer, so the title under it is looked up each frame instead.</summary>
    public override void _Process(double delta)
    {
        if (_state.Open is null)
            return;
        var at = GetGlobalMousePosition();
        for (int i = 0; i < _titles.Count; i++)
            if (i != _state.Open && _titles[i].GetGlobalRect().HasPoint(at))
            {
                HoverTitle(i);
                break;
            }
    }

    public override void _Notification(int what)
    {
        if (what == (int)NotificationThemeChanged)
            RestyleApp();
    }

    private void RestyleApp()
    {
        for (int i = 0; i < _menus.Count; i++)
            if (_menus[i].IsApp)
            {
                var accent = GetThemeColor("accent", ReverieStyle.Status);
                _titles[i].Icon = ReverieStyle.Knob(accent, accent, 12);
            }
    }

    private void Key(InputEvent e, Button title)
    {
        if (e.IsActionPressed("ui_left") || e.IsActionPressed("ui_right"))
        {
            _state.Arrow(e.IsActionPressed("ui_left") ? -1 : 1);
            _titles[_state.Focused].GrabFocus();
            Sync();
            title.AcceptEvent();
        }
        else if (e.IsActionPressed("ui_down") || e.IsActionPressed("ui_accept"))
        {
            _state.Activate();
            Sync();
            title.AcceptEvent();
        }
    }

    private void PopupKey(InputEvent e, PopupMenu popup)
    {
        bool left = e.IsActionPressed("ui_left"), right = e.IsActionPressed("ui_right");
        if (!left && !right)
            return;
        int focused = popup.GetFocusedItem();
        if (right && focused >= 0 && popup.GetItemSubmenuNode(focused) is not null)
            return;   // Right on an entry with a submenu opens the submenu
        _state.Arrow(left ? -1 : 1);
        Sync();
        _titles[_state.Focused].GrabFocus();
    }

    private void Sync()
    {
        _syncing++;
        try
        {
            for (int i = 0; i < _titles.Count; i++)
            {
                bool open = _state.Open == i;
                _titles[i].ThemeTypeVariation = _menus[i].IsApp ? (open ? ReverieStyle.AppTitleOpen : ReverieStyle.AppTitle) : open ? ReverieStyle.MenuTitleOpen : ReverieStyle.MenuTitle;
                if (!open && _popups[i].Visible)
                    _popups[i].Hide();
            }
            if (_state.Open is { } o && !_popups[o].Visible)
            {
                Fill(_popups[o], _menus[o].Entries());
                var title = _titles[o];
                var at = title.GetGlobalTransformWithCanvas() * new Vector2(0, title.Size.Y + 2);
                _popups[o].Popup(new Rect2I((Vector2I)at.Round(), Vector2I.Zero));
            }
        }
        finally { _syncing--; }
    }

    private static readonly Dictionary<PopupMenu, List<Action?>> Actions = new();

    /// <summary>Fills a dropdown: an icon column (16 pixels, tinted with the menu's text colour), the label, the shortcut text, check
    /// marks, separators and submenus. Entries that do not work are not passed in, so none is shown dead.</summary>
    public static void Fill(PopupMenu popup, IReadOnlyList<RvMenuEntry> entries)
    {
        foreach (var child in popup.GetChildren())
            if (child is PopupMenu sub)
            {
                popup.RemoveChild(sub);
                sub.QueueFree();
            }
        popup.Clear();
        var runs = new List<Action?>();
        Actions[popup] = runs;
        if (popup.GetSignalConnectionList(PopupMenu.SignalName.IdPressed).Count == 0)
            popup.IdPressed += id => { if (Actions.TryGetValue(popup, out var r) && id >= 0 && id < r.Count) r[(int)id]?.Invoke(); };
        var tint = popup.GetThemeColor("font_color", ReverieStyle.Popup);
        foreach (var e in entries)
        {
            if (e.Separator)
            {
                popup.AddSeparator();
                continue;
            }
            int id = runs.Count;
            runs.Add(e.Run);
            if (e.Children is { } kids)
            {
                var sub = new PopupMenu { ThemeTypeVariation = ReverieStyle.Popup };
                if (e.Id is { } subId)
                    sub.Name = subId.Replace('.', '_');
                popup.AddChild(sub);
                Fill(sub, kids);
                popup.AddSubmenuNodeItem(e.Label, sub, id);
                if (e.Icon is { } subIcon && ReverieIcons.Get(subIcon) is { } subTexture)
                    popup.SetItemIcon(popup.ItemCount - 1, subTexture);   // a submenu's row has its icon in the column too
            }
            else if (e.Checked is { } on)
                popup.AddIconCheckItem(ReverieIcons.Get(e.Icon ?? LucideIcons.Check)!, e.Label, id);
            else if (e.Icon is { } icon && ReverieIcons.Get(icon) is { } texture)
                popup.AddIconItem(texture, e.Label, id);
            else
                popup.AddItem(e.Label, id);
            int index = popup.ItemCount - 1;
            if (e.Checked is { } c)
                popup.SetItemChecked(index, c);
            popup.SetItemIconModulate(index, tint);
            popup.SetItemDisabled(index, !e.Enabled);
            if (e.Shortcut is { Length: > 0 } shortcut && OS.FindKeycodeFromString(shortcut) is var key && key != global::Godot.Key.None)
                popup.SetItemAccelerator(index, key);
        }
    }
}

/// <summary>
/// The location field. Display mode shows the parcel's name when it has one of its own, the region's name once (weight 600), the visited
/// grid's host while on another grid, and the coordinates (muted). When they do not fit, the coordinates go first, then the names are
/// shortened with an ellipsis (<see cref="LocationFit"/>); the host is never cut, and the tooltip has the whole place. A click turns it into a text entry that takes a region name or a region/x/y/z address and asks for a teleport on
/// Enter; Escape cancels. The star at its right end adds a landmark.
/// </summary>
public partial class RvLocationField : PanelContainer
{
    private readonly Button _display;
    private readonly Label _parcel, _region, _host, _coords;
    private readonly LineEdit _edit;
    private LocationParts? _parts;

    public RvIconButton Star { get; }

    /// <summary>A place was typed and Enter pressed.</summary>
    public event Action<LocationAddress>? Submitted;

    public RvLocationField(StringTable strings)
    {
        Name = "location_field";
        ThemeTypeVariation = ReverieStyle.Location;
        CustomMinimumSize = new Vector2(ReverieTheme.LocationMinWidth, ReverieTheme.LocationHeight);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        AddChild(row);
        _display = new Button { Name = "location_display", ThemeTypeVariation = ReverieStyle.LocationButton, SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.All, ClipText = true, ClipContents = true };
        _display.AccessibilityName = strings.Get("topbar.location");
        var labels = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        labels.SetAnchorsPreset(LayoutPreset.FullRect);
        labels.AddThemeConstantOverride("separation", 6);
        _parcel = new Label { ThemeTypeVariation = ReverieStyle.Status, MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _region = new Label { ThemeTypeVariation = ReverieStyle.Region, MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _host = new Label { Name = "location_host", ThemeTypeVariation = ReverieStyle.Region, MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        _coords = new Label { ThemeTypeVariation = ReverieStyle.Muted, MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        labels.AddChild(_parcel);
        labels.AddChild(_region);
        labels.AddChild(_host);
        labels.AddChild(_coords);
        _display.AddChild(labels);
        _display.Pressed += BeginEdit;
        row.AddChild(_display);
        _edit = new LineEdit { Name = "location_edit", ThemeTypeVariation = ReverieStyle.LocationEdit, Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = strings.Get("topbar.location.hint"), MaxLength = 160 };
        _edit.AccessibilityName = strings.Get("topbar.location");
        _edit.TextSubmitted += Submit;
        _edit.GuiInput += e => { if (e.IsActionPressed("ui_cancel")) { EndEdit(); _edit.AcceptEvent(); } };
        _edit.FocusExited += () => { if (_edit.Visible) EndEdit(); };
        row.AddChild(_edit);
        Star = new RvIconButton(LucideIcons.Landmark, strings.Get("topbar.landmark"), ReverieTheme.LocationHeight - 4) { Name = "landmark" };
        row.AddChild(Star);
        Show(null);
    }

    public bool Editing => _edit.Visible;

    public LineEdit Edit => _edit;

    /// <summary>What display mode shows (null: no region yet).</summary>
    public void Show(LocationParts? parts)
    {
        _parts = parts;
        _parcel.Text = parts?.Parcel ?? "";
        _parcel.Visible = parts?.Parcel is not null;
        _region.Text = parts?.Region ?? "";
        _host.Text = parts?.Host ?? "";
        _host.Visible = parts?.Host is not null;
        _coords.Text = parts?.Coordinates ?? "";
        _display.TooltipText = parts?.Full ?? "";
        if (_fitWidth > 0)
            FitText(_fitWidth);
    }

    private float _fitWidth;

    /// <summary>The width a label's whole text takes in its own font (a shortened label's minimum size is only its ellipsis).</summary>
    private static float FullWidth(Label label) =>
        label.Visible && label.Text.Length > 0
            ? label.GetThemeFont("font").GetStringSize(label.Text, HorizontalAlignment.Left, -1, label.GetThemeFontSize("font_size")).X
                + (label.GetThemeStylebox("normal")?.GetMinimumSize().X ?? 0)
            : 0;

    /// <summary>Whether the region's or the parcel's name is shown shortened now, for checks.</summary>
    public bool NamesShortened { get; private set; }

    /// <summary>The field's text fitted to its width: the coordinates go first, then the parcel's and the region's names are shortened with
    /// an ellipsis; the grid's host keeps its whole width (the tooltip keeps the whole place).</summary>
    public void FitText(float width)
    {
        _fitWidth = width;
        float room = width - Star.GetCombinedMinimumSize().X - 4 - 24;   // the star, the gap and the field's rounded ends
        _coords.Visible = true;
        float parcel = FullWidth(_parcel), region = FullWidth(_region), host = FullWidth(_host), coords = FullWidth(_coords);
        var w = LocationFit.Widths(room, parcel, region, host, coords, 6);
        _coords.Visible = w.Coordinates;
        _parcel.CustomMinimumSize = new Vector2(w.Parcel, 0);
        _region.CustomMinimumSize = new Vector2(w.Region, 0);
        NamesShortened = w.Parcel + 0.5f < parcel || w.Region + 0.5f < region;
    }

    /// <summary>Display mode's text as one line, for checks: the parts that are shown, in order.</summary>
    public string DisplayText => string.Join(" | ", new[] { _parcel.Visible ? _parcel.Text : null, _region.Text, _host.Visible ? _host.Text : null, _coords.Visible ? _coords.Text : null }.Where(t => !string.IsNullOrEmpty(t)));

    /// <summary>The visited grid's host as shown, for checks.</summary>
    public Label HostLabel => _host;

    /// <summary>The tooltip's text (the whole place), for checks.</summary>
    public string Tooltip => _display.TooltipText;

    public void BeginEdit()
    {
        _display.Visible = false;
        _edit.Visible = true;
        _edit.Text = _parts is null ? "" : _parts.Place;
        _edit.GrabFocus();
        _edit.SelectAll();
    }

    public void EndEdit()
    {
        _edit.Visible = false;
        _display.Visible = true;
    }

    /// <summary>Enter: a place that reads asks for the teleport and display mode comes back; text that is no place stays for correcting.</summary>
    public void Submit(string text)
    {
        if (LocationField.Parse(text) is not { } address)
            return;
        EndEdit();
        _display.GrabFocus();
        Submitted?.Invoke(address);
    }
}

/// <summary>
/// The top bar: solid, full width, 38 high, in three zones. Left: the app menu (the accent mark and the app's name) and the official
/// top-level menus. Centre: Back, Home and the location field. Right: the status area (the grid's name with a globe, the local time,
/// the connection dot with its state, and a balance only on grids with a currency), plain text that takes neither focus nor clicks.
/// When the zones do not fit on one row the location zone drops to its own row under the menus (<see cref="TopBarLayout"/>).
/// </summary>
public partial class TopBar : PanelContainer
{
    private readonly VBoxContainer _rows;
    private readonly HBoxContainer _first, _second, _left, _centre, _right;
    private readonly Control _gapA, _gapB;
    private readonly Label _time, _balance;
    private bool _ownRow, _statusDown;

    public RvMenuBar Menus { get; private set; }
    public RvLocationField Location { get; }
    public RvIconButton Back { get; }
    public RvIconButton Home { get; }
    public RvStatusChip Grid { get; }
    public RvStatusChip Connection { get; }

    /// <summary>The search box (a search icon): opens the command list (Ctrl+Shift+K).</summary>
    public Button Search { get; }

    public TopBar(StringTable strings, IReadOnlyList<RvMenu> menus)
    {
        Name = "top_bar";
        ThemeTypeVariation = ReverieStyle.TopBar;
        MouseFilter = MouseFilterEnum.Stop;
        AccessibilityName = strings.Get("topbar.bar");
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 0);
        AddChild(_rows);
        _first = Row("row_1");
        _second = Row("row_2");
        _second.Visible = false;
        _rows.AddChild(_first);
        _rows.AddChild(_second);

        _left = new HBoxContainer { Name = "zone_left" };
        Menus = new RvMenuBar(menus, strings.Get("shell.menubar"));
        _left.AddChild(Menus);

        _centre = new HBoxContainer { Name = "zone_centre", SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        _centre.AddThemeConstantOverride("separation", 4);
        Back = new RvIconButton(LucideIcons.Back, strings.Get("topbar.back")) { Name = "back", SizeFlagsVertical = SizeFlags.ShrinkCenter };
        Home = new RvIconButton(LucideIcons.Home, strings.Get("topbar.home")) { Name = "home", SizeFlagsVertical = SizeFlags.ShrinkCenter };
        Location = new RvLocationField(strings);
        _centre.AddChild(Back);
        _centre.AddChild(Home);
        _centre.AddChild(Location);

        _right = new HBoxContainer { Name = "zone_status", MouseFilter = MouseFilterEnum.Ignore };
        _right.AccessibilityName = strings.Get("topbar.status");
        _right.AddThemeConstantOverride("separation", 14);
        Grid = new RvStatusChip("", LucideIcons.Grid) { Name = "grid" };
        _time = new Label { Name = "time", ThemeTypeVariation = ReverieStyle.Status, MouseFilter = MouseFilterEnum.Ignore, FocusMode = FocusModeEnum.None };
        _balance = new Label { Name = "balance", ThemeTypeVariation = ReverieStyle.Status, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        Connection = new RvStatusChip(strings.Get("status.disconnected")) { Name = "connection" };
        Connection.DotColour = "muted";
        // the search box: the search icon, as small as Back and Home, so the narrow-window rule still holds
        Search = new RvIconButton(LucideIcons.Search, strings.Get("cmdlist.box.tip")) { Name = "search", SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _right.AddChild(Search);
        _right.AddChild(Grid);
        _right.AddChild(_balance);
        _right.AddChild(_time);
        _right.AddChild(Connection);

        _gapA = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(TopBarLayout.ZoneGap, 0) };
        _gapB = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(TopBarLayout.ZoneGap, 0) };
        Arrange(false, false);
    }

    /// <summary>Whether the status area moved to the second row too.</summary>
    public bool StatusOnSecondRow => _statusDown;

    /// <summary>Whether the location zone is on its own row now.</summary>
    public bool LocationOnOwnRow => _ownRow;

    private static HBoxContainer Row(string name)
    {
        var row = new HBoxContainer { Name = name, CustomMinimumSize = new Vector2(0, ReverieTheme.TopBarHeight) };
        row.AddThemeConstantOverride("separation", 0);
        return row;
    }

    public void SetGrid(string? name) => Grid.Text = name ?? "";

    /// <summary>Puts a new menu bar in (a feature added a menu or an entry after the bar was built).</summary>
    public void SetMenus(IReadOnlyList<RvMenu> menus, string accessibleName)
    {
        int at = Menus.GetIndex();
        _left.RemoveChild(Menus);
        Menus.QueueFree();
        Menus = new RvMenuBar(menus, accessibleName);
        _left.AddChild(Menus);
        _left.MoveChild(Menus, at);
        Callable.From(Relayout).CallDeferred();
    }

    /// <summary>Shows or hides the centre zone (Back, Home and the location field): only in the world.</summary>
    public void ShowPlace(bool inWorld)
    {
        Back.Visible = inWorld;
        Home.Visible = inWorld;
        Location.Visible = inWorld;
        _laidOutFor = -1;   // the centre zone's width changed: lay the zones out again
    }

    /// <summary>Shows or hides the search box: not while the login screen shows.</summary>
    public void ShowSearch(bool shown)
    {
        Search.Visible = shown;
        _laidOutFor = -1;   // the right zone's width changed: lay the zones out again
    }

    public void SetTime(string text) => _time.Text = text;

    /// <summary>The balance, shown only on a grid with a currency (null hides it).</summary>
    public void SetBalance(string? text)
    {
        _balance.Text = text ?? "";
        _balance.Visible = text is not null;
    }

    /// <summary>The connection state's word, and whether it is connected (the dot is green then).</summary>
    public void SetConnection(string word, bool connected)
    {
        Connection.Text = word;
        Connection.DotColour = connected ? "ok" : "muted";
    }

    public override void _Notification(int what)
    {
        if (what is (int)NotificationEnterTree or (int)NotificationParented or (int)NotificationThemeChanged)
            Callable.From(Relayout).CallDeferred();
    }

    public override void _Process(double delta)
    {
        if (Math.Abs(Available - _laidOutFor) > 0.5f)
            Relayout();
        // the menus' titles wrapping (or unwrapping) changes the bar's height: the frame lays out again
        float height = GetCombinedMinimumSize().Y;
        if (Math.Abs(height - _laidOutHeight) > 0.5f)
        {
            _laidOutHeight = height;
            Rearranged?.Invoke();
        }
    }

    private float _laidOutHeight = -1;

    private float _laidOutFor = -1;
    private float? _width;

    /// <summary>Raised (deferred) when the zones moved to or from the second row, so the frame can give the bar its new height.</summary>
    public event Action? Rearranged;

    /// <summary>The width the frame gives the bar (in interface units); without it, the width of the bar's parent.</summary>
    public void FitTo(float width)
    {
        _width = width;
        Relayout();   // measured again each time: the interface size or the menus may have changed at the same width
    }

    private float Available => _width ?? GetParentAreaSize().X;

    /// <summary>Lays the zones out for the width the bar is given (not its own size, which its content would widen).</summary>
    private void Relayout()
    {
        float available = Available;
        _laidOutFor = available;
        // the centre zone counts only while it shows (in the world); on the login screen the menus and the status share the row
        float centreWidth = Location.Visible ? Back.GetCombinedMinimumSize().X + Home.GetCombinedMinimumSize().X + ReverieTheme.LocationMinWidth + 8 : 0;
        float leftWidth = Menus.OneRowWidth, rightWidth = _right.GetCombinedMinimumSize().X;
        bool own = TopBarLayout.LocationOnOwnRow(available - 16, leftWidth, centreWidth, rightWidth);
        bool statusDown = TopBarLayout.StatusOnSecondRow(available - 16, leftWidth, centreWidth, rightWidth);
        if (own != _ownRow || statusDown != _statusDown)
        {
            Arrange(own, statusDown);
            Callable.From(() => Rearranged?.Invoke()).CallDeferred();
        }
        // the menus keep one line while they fit the row they are on; only past that do their titles wrap
        float leftRoom = statusDown ? available - 16 - TopBarLayout.ZoneGap
            : own ? available - 16 - rightWidth - 2 * TopBarLayout.ZoneGap : leftWidth;
        Menus.CustomMinimumSize = new Vector2(Math.Max(0, Math.Min(leftWidth, leftRoom)), 0);
        // the field takes the room the zone has, from its least width (240) to its largest (440)
        float room = statusDown ? available - 16 - rightWidth - TopBarLayout.ZoneGap
            : own ? available - 16 : available - 16 - leftWidth - rightWidth - 2 * TopBarLayout.ZoneGap;
        room -= Back.GetCombinedMinimumSize().X + Home.GetCombinedMinimumSize().X + 8;
        Location.CustomMinimumSize = new Vector2(Math.Clamp(room, ReverieTheme.LocationMinWidth, ReverieTheme.LocationMaxWidth), ReverieTheme.LocationHeight);
        // what the rows really need, measured now: a row still wider than the bar gives the location field less room (never below
        // half its least width), then the menus' titles wrap, so nothing is ever cut off
        float inner = available - 16;
        if (_second.Visible && _second.GetCombinedMinimumSize().X - inner is var over2 and > 0.5f)
            Location.CustomMinimumSize = new Vector2(Math.Max(ReverieTheme.LocationMinWidth / 2, Location.CustomMinimumSize.X - over2), ReverieTheme.LocationHeight);
        Location.FitText(Location.CustomMinimumSize.X);
        if (_first.GetCombinedMinimumSize().X - inner is var over1 and > 0.5f)
            Menus.CustomMinimumSize = new Vector2(Math.Max(0, Menus.CustomMinimumSize.X - over1), 0);
    }

    private void Arrange(bool ownRow, bool statusDown)
    {
        _ownRow = ownRow;
        _statusDown = statusDown;
        foreach (var c in new Control[] { _left, _gapA, _centre, _gapB, _right })
            c.GetParent()?.RemoveChild(c);
        _first.AddChild(_left);
        _first.AddChild(_gapA);
        if (!ownRow)
            _first.AddChild(_centre);
        if (!statusDown)
        {
            _first.AddChild(_gapB);
            _first.AddChild(_right);
        }
        if (ownRow)
            _second.AddChild(_centre);
        if (statusDown)
        {
            _second.AddChild(_gapB);
            _second.AddChild(_right);   // even the menus and the status do not fit one row: the status follows the location
        }
        _second.Visible = ownRow;
        _centre.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
    }
}
