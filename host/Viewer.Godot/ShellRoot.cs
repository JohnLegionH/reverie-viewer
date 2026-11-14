// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The window frame, built in code from a <see cref="ShellController"/>, top to bottom: the menu bar (the grid's mark and name
/// in the accent, the menus, and on the right the clock and the connection), the navigation bar and the chat bar when a
/// session is in the world, the main view, and the toolbar with an icon above each label. The floaters sit above everything. The
/// main view and the floater layer let the pointer through to the world wherever nothing is drawn, so the camera keeps its mouse.
/// Every text comes from the string table and every control has an accessible name. Where things go is decided by the
/// controller (tested without the engine); this draws it. Behaviour follows common viewer conventions (menu bar on top,
/// toolbar at the bottom, floating panels); no layout file of any viewer is used.
/// </summary>
public partial class ShellRoot : Control
{
    private readonly StringTable _strings;
    private readonly MenuBar _menuBar;
    private readonly PanelContainer _menuRow;
    private readonly ColorRect _brandMark;
    private readonly Label _brandName;
    private readonly Label _clock;
    private readonly TextureRect _connectionIcon;
    private readonly Label _connectionLabel;
    private readonly PanelContainer _toolbarPanel;
    private readonly HBoxContainer _toolbar;
    private readonly HBoxContainer _toolbarRow;
    private readonly Control _floaterLayer;
    private readonly Dictionary<string, FloaterFrame> _frames = new();
    private readonly Dictionary<string, (Button Button, ToolbarButton Model)> _toolbarButtons = new();
    private readonly List<PopupMenu> _popups = new();
    /// <summary>Which top menu is open, and whether a press on the bar is the click that just closed one (see <see cref="MenuToggle"/>).</summary>
    private readonly MenuToggle _menuToggle = new();

    /// <summary>The id of the top menu open now, or null.</summary>
    public string? OpenMenu => _menuToggle.Open;

    /// <summary>The ids of the top menus whose popup is on screen (for the automation's check).</summary>
    public string[] VisibleMenus() => _popups.Where(p => p.Visible).Select(p => p.Name.ToString()).ToArray();
    private Button? _moreButton;
    private ContextMenuPanel? _moreMenu;
    private PanelContainer? _badge;
    private Label? _badgeText;
    private bool _connected;

    public ShellController Shell { get; }

    /// <summary>Where the interface scale goes when it changes: null means the window's content scale factor (the running viewer);
    /// a render case that draws the shell in a sub-viewport sets its own, so the main window is left alone.</summary>
    public Action<float>? ScaleSink { get; set; }

    /// <summary>How many toolbar buttons are behind the More button at the current width.</summary>
    public int HiddenToolbarButtons { get; private set; }

    /// <summary>The region between the bars: the login screen and, later, the world's own overlays go here.</summary>
    public Control MainView { get; }

    public MenuBar Menu => _menuBar;
    public HBoxContainer Toolbar => _toolbar;
    /// <summary>The row the toolbar sits in; add-ons' quick controls go at its end.</summary>
    public HBoxContainer ToolbarRow => _toolbarRow;
    public NavBar Navigation { get; }
    public ChatBar Chat { get; }

    public IReadOnlyDictionary<string, FloaterFrame> Frames => _frames;

    /// <summary>The part of the window the bars leave (interface units, as laid out last): what a picture of the world shows between the menu rows and the chat row and toolbar.</summary>
    public Rect2 MainArea { get; private set; }

    public ShellRoot(ShellController shell)
    {
        Shell = shell;
        _strings = shell.Strings;
        Name = "shell";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        MainView = new Control { Name = "main_view", MouseFilter = MouseFilterEnum.Ignore };
        MainView.AccessibilityName = _strings.Get("shell.main");
        AddChild(MainView);

        Navigation = new NavBar(_strings) { Visible = false };
        AddChild(Navigation);
        Chat = new ChatBar(_strings) { Visible = false };
        AddChild(Chat);

        _toolbarPanel = new PanelContainer { Name = "toolbar_bar", ThemeTypeVariation = UiTheme.BarPanel, MouseFilter = MouseFilterEnum.Stop };
        _toolbar = new HBoxContainer { Name = "toolbar", Alignment = BoxContainer.AlignmentMode.Center };
        _toolbar.AddThemeConstantOverride("separation", 4);
        _toolbar.AccessibilityName = _strings.Get("shell.toolbar");
        _toolbar.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _toolbarRow = new HBoxContainer { Name = "toolbar_row" };
        _toolbarRow.AddChild(_toolbar);
        _toolbarPanel.AddChild(_toolbarRow);
        AddChild(_toolbarPanel);

        _menuRow = new PanelContainer { Name = "menu_row", ThemeTypeVariation = UiTheme.BarPanel, MouseFilter = MouseFilterEnum.Stop };
        var row = new HBoxContainer { Name = "menu_content" };
        _menuRow.AddChild(row);
        _brandMark = new ColorRect { Name = "brand_mark", CustomMinimumSize = new Vector2(14, 14), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(_brandMark);
        _brandName = new Label { Name = "brand_name", ThemeTypeVariation = UiTheme.BrandLabel, Text = _strings.Get("brand.default") };
        _brandName.AccessibilityName = _brandName.Text;
        row.AddChild(_brandName);
        _menuBar = new MenuBar { Name = "menu_bar" };
        _menuBar.AccessibilityName = _strings.Get("shell.menubar");
        // a menu opens on a click and, while one is open, follows the pointer across the bar (the engine's switch on hover); the press
        // that closes a menu reaches its popup first, so the same press on the bar must not open a menu again
        _menuBar.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && _menuToggle.Open is null
                && _lastClosedMenu is { } closed && !_menuToggle.PressOpens(closed, (long)Time.GetTicksMsec()))
                _menuBar.AcceptEvent();
        };
        row.AddChild(_menuBar);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        _clock = new Label { Name = "clock", ThemeTypeVariation = UiTheme.MutedLabel };
        _clock.AccessibilityName = _strings.Get("status.clock");
        row.AddChild(_clock);
        _connectionIcon = new TextureRect { Name = "connection_icon", StretchMode = TextureRect.StretchModeEnum.KeepCentered, CustomMinimumSize = new Vector2(20, 0), MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(_connectionIcon);
        _connectionLabel = new Label { Name = "connection", ThemeTypeVariation = UiTheme.MutedLabel };
        row.AddChild(_connectionLabel);
        AddChild(_menuRow);

        BuildMenus();
        RebuildToolbar();
        // feature code adds its menus, entries and buttons when it is ready, after the frame is drawn: draw them again then
        shell.Menus.Changed += RebuildMenus;
        shell.Toolbar.Changed += RebuildToolbar;
        Theme = ShellTheme.Build(UiTheme.Tokens);
        UiTheme.Changed += OnThemeChanged;
        Restyle();
        SetConnected(false);

        _floaterLayer = new Control { Name = "floaters", MouseFilter = MouseFilterEnum.Ignore };
        _floaterLayer.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_floaterLayer);
        foreach (var spec in shell.Floaters.Specs)
            AddFrame(spec);
        shell.Floaters.Changed += SyncFloaters;

        shell.Focus.TextReleaseRequested += () => GetViewport()?.GuiReleaseFocus();
    }

    /// <summary>The grid's name shown in the menu bar next to its mark (null: the viewer's own placeholder name).</summary>
    public void SetBrand(string? gridName)
    {
        _brandName.Text = BrandText.For(gridName, _strings);
        _brandName.AccessibilityName = _brandName.Text;
    }

    /// <summary>Shows the navigation bar and the chat bar (a session is in the world) or hides them.</summary>
    public void ShowWorldRows(bool inWorld)
    {
        Navigation.Visible = inWorld;
        Chat.Visible = inWorld;
        Shell.Chrome = new ChromeRows(Toolbar: true, NavBar: inWorld, ChatBar: inWorld);
        if (IsInsideTree())
            ApplyLayout();
    }

    /// <summary>The connection mark on the right of the menu bar.</summary>
    public void SetConnected(bool connected)
    {
        _connected = connected;
        _connectionLabel.Text = _strings.Get(connected ? "status.connected" : "status.disconnected");
        _connectionLabel.AccessibilityName = _connectionLabel.Text;
        _connectionIcon.Texture = IconCache.Get(UiIcons.Connection, 16);
        _connectionIcon.Modulate = ShellTheme.C(connected ? UiTheme.Tokens.Success : UiTheme.Tokens.TextSecondary);
    }

    /// <summary>The count on the notifications button (0: none shown).</summary>
    public void SetUnread(int count)
    {
        if (_badge is null || _badgeText is null)
            return;
        _badge.Visible = count > 0;
        _badgeText.Text = count > 99 ? _strings.Get("toolbar.unread.cap") : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_toolbarButtons.TryGetValue("toolbar_notifications", out var b))
            b.Button.AccessibilityName = count > 0 ? _strings.Get("toolbar.unread", count) : _strings.Get(b.Model.LabelKey);
    }

    /// <summary>Builds the engine theme again from the current tokens, applies the UI scale and redraws what draws itself.</summary>
    private void OnThemeChanged()
    {
        Theme = ShellTheme.Build(UiTheme.Tokens);
        Restyle();
        if (IsInsideTree())
        {
            ApplyScale();
            ApplyLayout();
        }
        Redraw(this);
    }

    /// <summary>The pieces that take their colour or picture from the tokens directly.</summary>
    private void Restyle()
    {
        _brandMark.Color = ShellTheme.C(UiTheme.Tokens.Accent);
        SetConnected(_connected);
        foreach (var (button, model) in _toolbarButtons.Values)
            if (model.Icon is { } icon)
                button.Icon = IconCache.Get(icon, 18);
        if (_moreButton is not null)
            _moreButton.Icon = IconCache.Get(UiIcons.Down, 18);
    }

    private static void Redraw(Node node)
    {
        if (node is CanvasItem item)
            item.QueueRedraw();
        foreach (var child in node.GetChildren())
            Redraw(child);
    }

    public override void _ExitTree() => UiTheme.Changed -= OnThemeChanged;

    public override void _Ready()
    {
        Shell.Focus.SetWindowFocused(GetWindow().HasFocus());
        ApplyScale();
        GetViewport().SizeChanged += ApplyLayout;
        ApplyLayout();
        SyncFloaters();
    }

    private void ApplyScale()
    {
        float factor = UiTheme.ScalePercent / 100f;
        if (ScaleSink is { } sink)
            sink(factor);
        else
            GetWindow().ContentScaleFactor = factor;
    }

    private double _clockAge = 1;

    /// <summary>A text the clock shows instead of the time of day (test pictures set one so pictures taken hours apart match).</summary>
    public string? FixedClockText
    {
        get => _fixedClock;
        set
        {
            _fixedClock = value;
            _clock.Text = ClockText.For(DateTime.Now, value, System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    private string? _fixedClock;

    /// <summary>Whether a text field has the keyboard is read once a frame: the engine says nothing when a field loses focus.</summary>
    public override void _Process(double delta)
    {
        FitOpenedWindows();
        var owner = GetViewport().GuiGetFocusOwner();
        Shell.Focus.SetTextEntryActive(owner is LineEdit { Editable: true } or TextEdit { Editable: true });
        if ((_clockAge += delta) >= 1)
        {
            _clockAge = 0;
            _clock.Text = ClockText.For(DateTime.Now, FixedClockText, System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Set on a text field that answers Escape itself (a name prompt cancels on it), so the shell's Escape leaves it alone.</summary>
    public const string TakesEscapeMeta = "takes_escape";

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }
            && Shell.Focus.OnEscape(GetViewport().GuiGetFocusOwner()?.HasMeta(TakesEscapeMeta) == true) != EscapeResult.None)
            GetViewport().SetInputAsHandled();
    }

    /// <summary>A pointer press the interface did not take is a press on the world.</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true })
            Shell.Focus.OnWorldPointerDown();
    }

    /// <summary>Adds a panel's content to its floater; the floater itself is registered with the controller's floaters first.</summary>
    public void SetFloaterContent(string floaterId, Control content) => _frames[floaterId].SetContent(content);

    /// <summary>Draws the toolbar again from its model (after feature code added a button).</summary>
    public void RebuildToolbar()
    {
        foreach (var child in _toolbar.GetChildren())
        {
            _toolbar.RemoveChild(child);
            child.QueueFree();
        }
        _toolbarButtons.Clear();
        _badge = null;
        _badgeText = null;
        foreach (var b in Shell.Toolbar.Buttons)
        {
            string label = _strings.Get(b.LabelKey);
            var button = new Button
            {
                Name = b.Id, Text = label, ThemeTypeVariation = UiTheme.ToolbarButton, CustomMinimumSize = new Vector2(64, 0),
                VerticalIconAlignment = VerticalAlignment.Top, IconAlignment = HorizontalAlignment.Center, ExpandIcon = false, FocusMode = FocusModeEnum.Click,
            };
            button.AddThemeConstantOverride("icon_max_width", 18);
            if (b.Icon is { } icon)
                button.Icon = IconCache.Get(icon, 18);
            button.AccessibilityName = label;
            string command = b.CommandId;
            var model = b;
            button.Pressed += () =>
            {
                // an open window that is behind is brought to the front, as the official toolbar does; anything else runs the command
                if (model.FloaterId is { } floater && Shell.Floaters.IsOpen(floater) && Shell.Floaters.FocusedId != floater)
                    Shell.Floaters.Focus(floater);
                else
                    Shell.Commands.Execute(command);
                button.ReleaseFocus();   // a click leaves no focus outline behind; the pressed look is only the window being open
            };
            _toolbar.AddChild(button);
            _toolbarButtons[b.Id] = (button, b);
            if (b.Id == "toolbar_notifications")
            {
                _badge = new PanelContainer { Name = "unread_badge", ThemeTypeVariation = UiTheme.BadgePanel, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
                _badge.AnchorLeft = 1; _badge.AnchorRight = 1; _badge.OffsetLeft = -22; _badge.OffsetRight = 0; _badge.OffsetTop = 0; _badge.OffsetBottom = 14;
                _badgeText = new Label { Name = "unread_count", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
                _badgeText.AddThemeColorOverride("font_color", ShellTheme.C(UiTheme.Tokens.AccentFillText));
                _badge.AddChild(_badgeText);
                button.AddChild(_badge);
            }
        }
        // the button at the right end that holds the buttons that do not fit the width (shown by FitToolbar)
        if (_moreButton is not null)
        {
            _toolbarRow.RemoveChild(_moreButton);
            _moreButton.QueueFree();
        }
        _moreButton = new Button
        {
            Name = "toolbar_more", Text = _strings.Get("toolbar.more"), ThemeTypeVariation = UiTheme.ToolbarButton, CustomMinimumSize = new Vector2(64, 0),
            VerticalIconAlignment = VerticalAlignment.Top, IconAlignment = HorizontalAlignment.Center, ExpandIcon = false, FocusMode = FocusModeEnum.Click, Visible = false,
        };
        _moreButton.AddThemeConstantOverride("icon_max_width", 18);
        _moreButton.Icon = IconCache.Get(UiIcons.Down, 18);
        _moreButton.AccessibilityName = _strings.Get("toolbar.more.named");
        _moreButton.Pressed += OpenMoreMenu;
        _toolbarRow.AddChild(_moreButton);
        if (IsInsideTree())
        {
            SyncToolbarState();
            ApplyLayout();
        }
    }

    /// <summary>The buttons that do not fit the bar's width, in a menu above the More button; choosing one does what its button does.</summary>
    private void OpenMoreMenu()
    {
        if (_moreButton is null)
            return;
        _moreMenu ??= new ContextMenuPanel(_strings);
        if (_moreMenu.GetParent() is null)
            MainView.AddChild(_moreMenu);
        var hidden = Shell.Toolbar.Buttons.Skip(Shell.Toolbar.Buttons.Count - HiddenToolbarButtons).ToList();
        var entries = hidden.Select(b => new ContextEntry(b.Id, b.LabelKey)).ToList();
        var at = _moreButton.GetGlobalRect().Position;
        _moreMenu.Open(at, entries, id =>
        {
            if (hidden.FirstOrDefault(b => b.Id == id) is { } picked)
                Shell.Commands.Execute(picked.CommandId);
        }, MainView.Size);
    }

    /// <summary>A button shows as active while the window it opens is open.</summary>
    private void SyncToolbarState()
    {
        foreach (var (button, model) in _toolbarButtons.Values)
            button.ThemeTypeVariation = model.IsPressed(Shell.Floaters) ? UiTheme.ToolbarButtonActive : UiTheme.ToolbarButton;
    }

    /// <summary>A menu entry's shortcut as the engine's key code ("control|U" is Ctrl+U), or none: the menu shows it beside the entry and the
    /// menu bar runs the entry when it is pressed.</summary>
    public static Key Accelerator(string? shortcut)
    {
        if (string.IsNullOrEmpty(shortcut))
            return Key.None;
        Key key = Key.None;
        foreach (var part in shortcut.Split('|'))
        {
            switch (part.ToLowerInvariant())
            {
                case "control": key |= (Key)KeyModifierMask.MaskCtrl; break;
                case "shift": key |= (Key)KeyModifierMask.MaskShift; break;
                case "alt": key |= (Key)KeyModifierMask.MaskAlt; break;
                case { Length: 1 } c when char.IsLetterOrDigit(c[0]): key |= (Key)char.ToUpperInvariant(c[0]); break;
                default: return Key.None;
            }
        }
        return key;
    }

    /// <summary>Draws the menu bar again from its model (after feature code added a menu or an entry).</summary>
    public void RebuildMenus()
    {
        foreach (var popup in _popups)
        {
            _menuBar.RemoveChild(popup);
            popup.QueueFree();
        }
        _popups.Clear();
        BuildMenus();
    }

    /// <summary>Registers a floater after the frame is built (a feature that becomes ready later) and gives it its content.</summary>
    public void AddFloater(FloaterSpec spec, Control content)
    {
        Shell.Floaters.Register(spec);
        AddFrame(spec);
        _frames[spec.Id].SetContent(content);
        SyncFloaters();
    }

    /// <summary>Sets a floater's title to a text of its own (a window whose title is what it shows, such as a notecard's name).</summary>
    public void SetFloaterTitle(string id, string title)
    {
        if (!_frames.TryGetValue(id, out var frame))
            return;
        frame.Title.Text = title;
        frame.Title.AccessibilityName = title;
    }

    private string? _lastClosedMenu;

    private void BuildMenus()
    {
        int index = 0;
        foreach (var menu in Shell.Menus.Menus)
        {
            string title = _strings.Get(menu.LabelKey);
            var popup = new PopupMenu { Name = menu.Id };
            popup.AccessibilityName = title;
            _menuBar.AddChild(popup);
            _menuBar.SetMenuTitle(index++, title);
            string menuId = menu.Id;
            var submenus = new Dictionary<string, PopupMenu>();   // one popup per submenu entry, made the first time the menu opens
            void Fill()
            {
                // read the model each time the menu opens: an entry may have been added, enabled or checked since
                var entries = Shell.Menus.Menus.First(m => m.Id == menuId).Entries;
                popup.Clear();
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry.Children is not { } kids)
                    {
                        popup.AddItem(_strings.Get(entry.LabelKey), i, Accelerator(entry.Shortcut));
                        popup.SetItemDisabled(i, !entry.IsEnabled);
                        continue;
                    }
                    string entryId = entry.Id;
                    if (!submenus.TryGetValue(entryId, out var sub))
                    {
                        sub = new PopupMenu { Name = entryId.Replace('.', '_') };
                        sub.AccessibilityName = _strings.Get(entry.LabelKey);
                        popup.AddChild(sub);
                        sub.IdPressed += id => Shell.Menus.Activate(Shell.Menus.Menus.First(m => m.Id == menuId).Entries.First(e => e.Id == entryId).Children![(int)id].Id, Shell.Commands);
                        submenus[entryId] = sub;
                    }
                    sub.Clear();
                    for (int k = 0; k < kids.Count; k++)
                    {
                        if (kids[k].Checked is null) sub.AddItem(_strings.Get(kids[k].LabelKey), k, Accelerator(kids[k].Shortcut));
                        else { sub.AddCheckItem(_strings.Get(kids[k].LabelKey), k); sub.SetItemChecked(k, kids[k].IsChecked); }
                        sub.SetItemDisabled(k, !kids[k].IsEnabled);
                    }
                    popup.AddSubmenuNodeItem(_strings.Get(entry.LabelKey), sub, i);
                }
            }
            Fill();
            popup.AboutToPopup += Fill;
            popup.AboutToPopup += () => _menuToggle.Opened(menuId);
            popup.PopupHide += () => { _menuToggle.Closed(menuId, (long)Time.GetTicksMsec()); _lastClosedMenu = menuId; };
            popup.IdPressed += id => Shell.Menus.Activate(Shell.Menus.Menus.First(m => m.Id == menuId).Entries[(int)id].Id, Shell.Commands);
            _popups.Add(popup);
        }
    }

    private void AddFrame(FloaterSpec spec)
    {
        var frame = new FloaterFrame(Shell.Floaters, spec, _strings);
        if (spec.Id == ShellController.AboutFloaterId)
        {
            // a wrapping label needs a width to wrap to, else its height is that of a one-letter-wide column
            float width = spec.MinSize.W - 24;
            var box = new VBoxContainer { Name = "about_content" };
            var paragraphs = Viewer.Shell.AboutText.Paragraphs(_strings, Viewer.Core.Identity.ViewerIdentity.Current);
            var release = new Label { Name = "release", Text = paragraphs[0], ThemeTypeVariation = UiTheme.TitleLabel, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(width, 0) };
            release.AccessibilityName = release.Text;
            var about = new Label { Name = "body", Text = string.Join("\n\n", paragraphs.Skip(1).Take(paragraphs.Count - 2)), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(width, 0) };
            about.AccessibilityName = _strings.Get("about.title");
            var disclaimer = new Label { Name = "disclaimer", Text = paragraphs[^1], AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(width, 0) };
            disclaimer.AccessibilityName = disclaimer.Text;
            var licences = new Button { Name = "licences", Text = _strings.Get("about.licences"), SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
            licences.AccessibilityName = licences.Text;
            licences.Pressed += () => Shell.Commands.Execute("floater.licences");
            box.AddChild(release);
            box.AddChild(about);
            box.AddChild(disclaimer);
            box.AddChild(licences);
            frame.SetContent(box);
        }
        else if (spec.Id == ShellController.LicencesFloaterId)
        {
            // the viewer's licence and the third-party notices travel inside the shell assembly; the engine's own text and the
            // notices of what it bundles are read from the running engine
            var docs = AboutText.Licences(_strings, Engine.GetLicenseText(), EngineNotices());
            var box = new VBoxContainer { Name = "licences_content" };
            var pick = new OptionButton { Name = "document" };
            pick.AccessibilityName = _strings.Get("about.licences.title");
            foreach (var d in docs)
                pick.AddItem(d.Title);
            var text = new TextEdit { Name = "text", Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(spec.MinSize.W - 24, spec.MinSize.H - 90) };
            text.AccessibilityName = docs[0].Title;
            text.Text = docs[0].Text;
            pick.ItemSelected += i => { text.Text = docs[(int)i].Text; text.AccessibilityName = docs[(int)i].Title; };
            box.AddChild(pick);
            box.AddChild(text);
            frame.SetContent(box);
        }
        _frames[spec.Id] = frame;
        _floaterLayer.AddChild(frame);
    }

    /// <summary>The engine's notices for the components it bundles, as the running engine lists them: each component with its
    /// copyright lines and licence, then each licence's text.</summary>
    private static string EngineNotices()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var component in Engine.GetCopyrightInfo())
        {
            sb.Append(component["name"].AsString()).Append('\n');
            foreach (var part in component["parts"].AsGodotArray<global::Godot.Collections.Dictionary>())
            {
                foreach (var line in part["copyright"].AsStringArray())
                    sb.Append("    Copyright ").Append(line).Append('\n');
                sb.Append("    License: ").Append(part["license"].AsString()).Append('\n');
            }
            sb.Append('\n');
        }
        foreach (var (name, text) in Engine.GetLicenseInfo())
            sb.Append("License ").Append(name.AsString()).Append(":\n\n").Append(text.AsString()).Append("\n\n");
        return sb.ToString();
    }

    private void ApplyLayout()
    {
        // the window's size in interface units (the engine has already divided the pixels by the scale), and the height each bar's
        // content needs: a bar taller than its design height would draw over its neighbour or past the window's edge
        var size = GetViewportRect().Size;
        var needs = new BarHeights(_menuRow.GetCombinedMinimumSize().Y, Navigation.GetCombinedMinimumSize().Y, Chat.GetCombinedMinimumSize().Y, _toolbarPanel.GetCombinedMinimumSize().Y);
        var regions = Shell.Layout(new UiSize(size.X, size.Y), needs);
        MainArea = new Rect2(regions.Main.X, regions.Main.Y, regions.Main.W, regions.Main.H);
        FitMenuRow(regions.MenuBar.W);
        FitToolbar(regions.Toolbar.W);
        Place(_menuRow, regions.MenuBar);
        Place(Navigation, regions.NavBar);
        Navigation.Visible = regions.NavBar.H > 0;
        Place(MainView, regions.Main);
        Place(Chat, regions.ChatBar);
        Chat.Visible = regions.ChatBar.H > 0;
        Place(_toolbarPanel, regions.Toolbar);
        _toolbar.Visible = regions.Toolbar.H > 0;
        _toolbarPanel.Visible = regions.Toolbar.H > 0;
    }

    /// <summary>A menu row wider than the window drops its optional parts: first the grid's name, then the connection's words (its
    /// mark stays). The menus and the clock always stay.</summary>
    private void FitMenuRow(float width)
    {
        _brandName.Visible = true;
        _connectionLabel.Visible = true;
        if (_menuRow.GetCombinedMinimumSize().X > width)
            _brandName.Visible = false;
        if (_menuRow.GetCombinedMinimumSize().X > width)
            _connectionLabel.Visible = false;
    }

    /// <summary>Fits the toolbar to its row again (after something else in the row, such as an add-on's quick control, came or went).</summary>
    public void RefitToolbar()
    {
        if (IsInsideTree())
            ApplyLayout();
    }

    /// <summary>The toolbar buttons that fit the width show; the rest are behind the More button at the right end (<see cref="ToolbarFit"/>).</summary>
    private void FitToolbar(float width)
    {
        var buttons = Shell.Toolbar.Buttons.Select(b => _toolbarButtons.TryGetValue(b.Id, out var pair) ? pair.Button : null).Where(b => b is not null).Select(b => b!).ToList();
        if (_moreButton is null)
            return;
        var widths = buttons.Select(b => b.GetCombinedMinimumSize().X).ToList();
        float margins = _toolbarPanel.GetThemeStylebox("panel").GetMinimumSize().X;
        float separation = _toolbar.GetThemeConstant("separation");
        // anything else in the row (the add-ons' quick controls) keeps its own width; the buttons fit in what is left
        float others = 0;
        foreach (var child in _toolbarRow.GetChildren())
        {
            if (child is Control c && c != _toolbar && c != _moreButton && c.Visible)
                others += c.GetCombinedMinimumSize().X + _toolbarRow.GetThemeConstant("separation");
        }
        var fit = ToolbarFit.Plan(widths, width - margins - others, separation, _moreButton.GetCombinedMinimumSize().X);
        for (int i = 0; i < buttons.Count; i++)
            buttons[i].Visible = i < fit.Shown;
        _moreButton.Visible = fit.NeedsMore;
        HiddenToolbarButtons = fit.Hidden;
    }

    private static void Place(Control c, UiRect r)
    {
        c.Position = new Vector2(r.X, r.Y);
        c.Size = new Vector2(r.W, r.H);
    }

    private readonly HashSet<string> _fitted = new();

    // windows opened this run that are measured for their content a few frames after they first show: on the first frame wrapped
    // text has no width yet and asks for far more height than it will take
    private readonly Dictionary<string, ulong> _fitDue = new();
    private const ulong FitAfterFrames = 3;

    private void FitOpenedWindows()
    {
        if (_fitDue.Count == 0)
            return;
        ulong now = Engine.GetProcessFrames();
        foreach (var (id, due) in _fitDue.ToArray())
        {
            if (now < due)
                continue;
            _fitDue.Remove(id);
            if (Shell.Floaters.IsOpen(id) && _frames.TryGetValue(id, out var frame))
            {
                var need = frame.NeededSize();
                Shell.Floaters.FitToContent(id, need.X, need.Y);
            }
        }
    }

    /// <summary>Puts every frame where the manager says it is, shows the open ones, and stacks them in its order. The manager's
    /// rectangle is the one truth for a window's place and size, so what is drawn is what a drag changed.</summary>
    private void SyncFloaters()
    {
        var open = Shell.Floaters.OpenFloaters;
        // each frame is shown or hidden once, by whether it is open: hiding an open window, even for the moment before it is shown again, makes the
        // engine drop the pointer it holds, which ends a drag after its first step
        var openIds = new HashSet<string>(open.Select(o => o.Id));
        foreach (var (id, frame) in _frames)
            frame.Visible = openIds.Contains(id);
        for (int i = 0; i < open.Count; i++)
        {
            var f = _frames[open[i].Id];
            f.ThemeTypeVariation = open[i].Focused ? UiTheme.WindowPanelActive : UiTheme.WindowPanel;   // the focused window has its own opacity
            // a window opens big enough for its content when the main view has room (once, measured once it has laid out); where it
            // has not, or after a drag made it smaller, the content scrolls
            if (_fitted.Add(open[i].Id))
                _fitDue[open[i].Id] = Engine.GetProcessFrames() + FitAfterFrames;
            f.Position = new Vector2(open[i].Rect.X, open[i].Rect.Y);
            f.Size = new Vector2(open[i].Rect.W, f.Minimized ? FloaterFrame.TitleBarHeight : open[i].Rect.H);
            _floaterLayer.MoveChild(f, i);   // the nearer, the later
        }
        SyncToolbarState();
    }
}
