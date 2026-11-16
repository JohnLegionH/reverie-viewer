// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The window frame, built in code from a <see cref="ShellController"/>, top to bottom: the top bar (the app menu and the official
/// top-level menus, Back, Home and the location field in the world, and the status area), the main view, the chat bar when a session
/// is in the world, and the dock floating 14 above the bottom row over the world. The floaters sit above everything. The main view and
/// the floater layer let the pointer through to the world wherever nothing is drawn, so the camera keeps its mouse. Every text comes
/// from the string table and every control has an accessible name. Where things go is decided by the controller (tested without the
/// engine); this draws it. No layout file of any viewer is used.
/// </summary>
public partial class ShellRoot : Control
{
    private readonly StringTable _strings;
    private readonly Control _floaterLayer;
    private readonly Dictionary<string, FloaterFrame> _frames = new();
    private PopupMenu? _dockMenu;
    private bool _connected;

    /// <summary>The id of the top menu open now, or null.</summary>
    public string? OpenMenu => TopBar.Menus.OpenMenu is { } i && i < TopBar.Menus.Menus.Count ? TopBar.Menus.Menus[i].Id : null;

    /// <summary>The ids of the top menus whose dropdown is on screen (for the automation's check).</summary>
    public string[] VisibleMenus() => TopBar.Menus.Popups.Where(p => p.Visible).Select(p => p.Name.ToString()).ToArray();

    public ShellController Shell { get; }

    /// <summary>Where the interface scale goes when it changes: null means the window's content scale factor (the running viewer);
    /// a render case that draws the shell in a sub-viewport sets its own, so the main window is left alone.</summary>
    public Action<float>? ScaleSink { get; set; }

    /// <summary>How many rows the dock takes at the current width (it wraps before any button is hidden).</summary>
    public int DockRows { get; private set; } = 1;

    /// <summary>The region between the bars: the login screen and, later, the world's own overlays go here.</summary>
    public Control MainView { get; }

    public TopBar TopBar { get; }

    /// <summary>The top bar's menu bar.</summary>
    public RvMenuBar Menu => TopBar.Menus;

    public DockModel DockModel { get; }
    public Dock Dock { get; }

    /// <summary>The row at the dock's end where an add-on's older quick controls go.</summary>
    public HBoxContainer ToolbarRow => Dock.QuickRow;
    public ChatBar Chat { get; }

    /// <summary>An add-on's dock button was clicked, or an entry of its menu chosen (the entry's index; -1 for the click).</summary>
    public event Action<DockItem, int>? AddonDockUsed;

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

        Chat = new ChatBar(_strings) { Visible = false };
        AddChild(Chat);

        // the dock floats over the world, centred, 14 above the bottom row
        DockModel = DockModel.Standard();
        // the dock shows in the world only: before a login its buttons have nothing to open, and floating over the login screen it
        // would cover the login form
        Dock = new Dock(DockModel, _strings) { IsOpen = DockItemOpen, Visible = false };
        Dock.Clicked += OnDockClicked;
        Dock.MenuRequested += OpenDockMenu;
        DockModel.Changed += () => { if (IsInsideTree()) ApplyLayout(); };
        AddChild(Dock);

        TopBar = new TopBar(_strings, TopMenus());
        TopBar.ShowPlace(false);
        TopBar.Rearranged += () => { if (IsInsideTree()) ApplyLayout(); };
        AddChild(TopBar);
        // feature code adds its menus and entries when it is ready, after the frame is drawn: draw them again then
        shell.Menus.Changed += RebuildMenus;
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

    /// <summary>The grid's name in the status area (null: none yet).</summary>
    public void SetBrand(string? gridName) => TopBar.SetGrid(gridName);

    /// <summary>The location field's text (null: no region yet).</summary>
    public void SetLocation(LocationParts? parts) => TopBar.Location.Show(parts);

    /// <summary>Shows the world's parts of the frame (Back, Home, the location field and the chat bar) while a session is in the world.</summary>
    public void ShowWorldRows(bool inWorld)
    {
        Chat.Visible = inWorld;
        Dock.Visible = inWorld;
        TopBar.ShowPlace(inWorld);
        Shell.Chrome = new ChromeRows(Toolbar: false, NavBar: false, ChatBar: inWorld);
        if (IsInsideTree())
            ApplyLayout();
    }

    private PanelContainer? _caption;
    private SceneTreeTimer? _captionTimer;

    /// <summary>A short caption near the top centre for four seconds (spec 8's visual cue), such as what Check for updates found.</summary>
    public void ShowNotice(string text)
    {
        if (_caption is null)
        {
            _caption = new PanelContainer { Name = "caption", ThemeTypeVariation = ReverieStyle.Pill, MouseFilter = MouseFilterEnum.Ignore };
            _caption.AddChild(new Label { Name = "text", ThemeTypeVariation = ReverieStyle.Status, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore });
            AddChild(_caption);
        }
        var label = _caption.GetNode<Label>("text");
        label.Text = text;
        label.AccessibilityName = text;
        _caption.Visible = true;
        _caption.ResetSize();
        var size = _caption.GetCombinedMinimumSize();
        _caption.Size = size;
        _caption.Position = new Vector2(MathF.Round((GetViewportRect().Size.X - size.X) / 2), MainArea.Position.Y + 12);
        var timer = _captionTimer = GetTree().CreateTimer(4);
        timer.Timeout += () => { if (_captionTimer == timer && _caption is not null) _caption.Visible = false; };
    }

    /// <summary>The connection state in the status area.</summary>
    public void SetConnected(bool connected)
    {
        _connected = connected;
        TopBar.SetConnection(_strings.Get(connected ? "status.connected" : "status.disconnected"), connected);
    }

    /// <summary>Unread notices: the Notices button shows attention and the count (0: none).</summary>
    public void SetUnread(int count)
    {
        if (DockModel.Item("notices") is { } notices)
            DockModel.SetAttention(notices, count > 0, count > 0 ? count : null);
    }

    /// <summary>Unread conversations: the Convos button shows attention until it is opened (0: none).</summary>
    public void SetUnreadConversations(int count)
    {
        if (DockModel.Item("convos") is { } convos)
            DockModel.SetAttention(convos, count > 0, count > 0 ? count : null);
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

    /// <summary>The pieces that take their picture from the tokens directly.</summary>
    private void Restyle()
    {
        SetConnected(_connected);
        Dock.Rebuild();
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
            TopBar.SetTime(ClockText.For(DateTime.Now, value, System.Globalization.CultureInfo.CurrentCulture));
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
            TopBar.SetTime(ClockText.For(DateTime.Now, FixedClockText, System.Globalization.CultureInfo.CurrentCulture));
        }
    }

    /// <summary>Set on a text field that answers Escape itself (a name prompt cancels on it), so the shell's Escape leaves it alone.</summary>
    public const string TakesEscapeMeta = "takes_escape";

    /// <summary>The right-click menu, once the interaction feature is installed: Esc closes it before anything else sees the key.</summary>
    public ContextMenuPanel? ContextMenu { get; set; }

    public override void _Input(InputEvent e)
    {
        // an open right-click menu takes Esc first and closes with no choice, as the official viewer's menu holder does before anything
        // else sees the key (llviewerwindow.cpp handleKey); the camera's Esc never runs
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && ContextMenu is { IsOpen: true } menu)
        {
            menu.Dismiss();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }
            && Shell.Focus.OnEscape(GetViewport().GuiGetFocusOwner()?.HasMeta(TakesEscapeMeta) == true) != EscapeResult.None)
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        // a menu shortcut with Ctrl or Alt reaches the menus before the control that holds the keyboard, so it works while typing
        if (e is InputEventKey { Pressed: true, Echo: false } k && (k.CtrlPressed || k.AltPressed) && RunShortcut(k))
            GetViewport().SetInputAsHandled();
    }

    /// <summary>A menu shortcut without Ctrl or Alt (G, Shift+X) reaches the menus only when the control holding the keyboard did not take
    /// it: a text field types it instead.</summary>
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k && !k.CtrlPressed && !k.AltPressed && RunShortcut(k))
            GetViewport().SetInputAsHandled();
    }

    /// <summary>Runs the menu entry whose shortcut was pressed, read from the menus now (no menu needs to have been opened). True when one ran.</summary>
    private bool RunShortcut(InputEventKey k)
    {
        if (ChordOf(k) is not { } chord)
            return false;
        bool typing = GetViewport().GuiGetFocusOwner() is LineEdit { Editable: true } or TextEdit { Editable: true };
        if (MenuShortcuts.Route(TopBarMenuMap.Build(Shell.Menus, Shell.Commands), chord, typing) is not { CommandId: { } command })
            return false;
        return Shell.Commands.Execute(command);
    }

    /// <summary>A key event as a menu shortcut's chord: a letter, a digit, a full stop or a comma with its modifier keys; null for any other key.</summary>
    public static KeyChord? ChordOf(InputEventKey k)
    {
        var code = k.Keycode != Key.None ? k.Keycode : k.PhysicalKeycode;
        return code is >= Key.A and <= Key.Z or >= Key.Key0 and <= Key.Key9 or Key.Period or Key.Comma
            ? new KeyChord(((char)(long)code).ToString(), k.CtrlPressed, k.ShiftPressed, k.AltPressed)
            : null;
    }

    /// <summary>A pointer press the interface did not take is a press on the world.</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true })
            Shell.Focus.OnWorldPointerDown();
    }

    /// <summary>Adds a panel's content to its floater; the floater itself is registered with the controller's floaters first.</summary>
    public void SetFloaterContent(string floaterId, Control content) => _frames[floaterId].SetContent(content);

    /// <summary>The window a built-in dock button opens: the one the feature's toolbar entry names for the same command, else the
    /// window of the command's own name.</summary>
    private string? FloaterOf(DockItem item)
    {
        if (item.Owner is not null || item.CommandId is not { } command)
            return null;
        if (Shell.Toolbar.Buttons.FirstOrDefault(b => b.CommandId == command)?.FloaterId is { } named)
            return named;
        return command.StartsWith("floater.", StringComparison.Ordinal) && Shell.Floaters.Specs.Any(f => f.Id == command["floater.".Length..]) ? command["floater.".Length..] : null;
    }

    /// <summary>A built-in button shows as on while its window is open.</summary>
    private bool DockItemOpen(DockItem item) => FloaterOf(item) is { } floater && Shell.Floaters.IsOpen(floater);

    /// <summary>A click on a dock button: an open window that is behind comes to the front; anything else runs the button's command
    /// (a built-in button) or tells its add-on.</summary>
    private void OnDockClicked(DockItem item)
    {
        if (item.Owner is not null)
        {
            AddonDockUsed?.Invoke(item, -1);
            return;
        }
        if (item.Attention && item.Id == "convos")
            DockModel.SetAttention(item, false);   // the ring stays until the conversations are opened
        if (FloaterOf(item) is { } floater && Shell.Floaters.IsOpen(floater) && Shell.Floaters.FocusedId != floater)
            Shell.Floaters.Focus(floater);
        else if (item.CommandId is { } command)
            Shell.Commands.Execute(command);
    }

    /// <summary>An entry of an add-on dock button's menu chosen (the automation's check chooses one as the pointer does).</summary>
    public void DockMenuChoose(DockItem item, int index) => AddonDockUsed?.Invoke(item, index);

    /// <summary>A dock button's menu (its arrow or a right-click), above the button.</summary>
    private void OpenDockMenu(DockItem item, Vector2 at)
    {
        if (item.Menu.Count == 0)
            return;
        if (_dockMenu is null)
        {
            _dockMenu = new PopupMenu { Name = "dock_menu", ThemeTypeVariation = ReverieStyle.Popup };
            AddChild(_dockMenu);
        }
        var menu = _dockMenu;
        menu.Clear();
        foreach (var signal in menu.GetSignalConnectionList(PopupMenu.SignalName.IdPressed))
            menu.Disconnect(PopupMenu.SignalName.IdPressed, signal["callable"].AsCallable());
        for (int i = 0; i < item.Menu.Count; i++)
        {
            var e = item.Menu[i];
            if (e.Separator)
            {
                menu.AddSeparator("", i);
                continue;
            }
            if (e.Checked is { } on)
            {
                menu.AddCheckItem(e.Label, i);
                menu.SetItemChecked(menu.ItemCount - 1, on);
            }
            else
                menu.AddItem(e.Label, i);
        }
        menu.IdPressed += id => AddonDockUsed?.Invoke(item, (int)id);
        menu.ResetSize();
        var size = menu.GetContentsMinimumSize();
        menu.Popup(new Rect2I((Vector2I)(at - new Vector2(0, size.Y + 6)).Round(), Vector2I.Zero));
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
    public void RebuildMenus() => TopBar.SetMenus(TopMenus(), _strings.Get("shell.menubar"));

    /// <summary>The top bar's menus from the model: the app menu, then the official menus; entries are read each time a menu opens.</summary>
    private IReadOnlyList<RvMenu> TopMenus()
    {
        var views = TopBarMenuMap.Build(Shell.Menus, Shell.Commands);
        return views.Select(v => new RvMenu(_strings.Get(v.TitleKey), () => Entries(v.Id), v.IsApp, v.Id)).ToArray();
    }

    private IReadOnlyList<RvMenuEntry> Entries(string menuId)
    {
        var view = TopBarMenuMap.Build(Shell.Menus, Shell.Commands).FirstOrDefault(m => m.Id == menuId);
        return view is null ? [] : view.Entries.Select(ToEntry).ToArray();
    }

    private RvMenuEntry ToEntry(TopMenuEntryView e) =>
        new(_strings.Get(e.LabelKey), e.Icon, ShortcutText(e.Shortcut), e.CommandId is { } c ? () => Shell.Commands.Execute(c) : null, e.Enabled, e.Checked,
            e.Children?.Select(ToEntry).ToArray(), false, e.Id);

    /// <summary>A shortcut as the official menus write it ("control|U") in the engine's key text ("Ctrl+U"), or none.</summary>
    private static string? ShortcutText(string? shortcut) =>
        Accelerator(shortcut) is var key && key != Key.None ? OS.GetKeycodeString(key) : null;

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
        TopBar.FitTo(size.X);   // the narrow-window rule works on the window's width in interface units
        // the old toolbar and navigation rows are gone: the dock floats, so no row is kept for them whatever the controller says
        Shell.Chrome = Shell.Chrome with { Toolbar = false, NavBar = false };
        var needs = new BarHeights(TopBar.GetCombinedMinimumSize().Y, 0, Chat.GetCombinedMinimumSize().Y, 0);
        var regions = Shell.Layout(new UiSize(size.X, size.Y), needs);
        MainArea = new Rect2(regions.Main.X, regions.Main.Y, regions.Main.W, regions.Main.H);
        Place(TopBar, regions.MenuBar);
        Place(MainView, regions.Main);
        Place(Chat, regions.ChatBar);
        Chat.Visible = regions.ChatBar.H > 0;
        // the dock: centred, 14 above the bottom row (the chat bar in the world, the window's edge before), wrapping within the width
        Dock.QuickRow.Visible = Dock.QuickRow.GetChildCount() > 0;
        Dock.MaxWidth = Math.Max(ReverieTheme.DockButtonWidth + 2 * (ReverieTheme.DockPadding + 1), size.X - 2 * ReverieTheme.DockEdge);
        var dock = Dock.Fit();
        DockRows = Dock.Rows(dock.X - 2 * (ReverieTheme.DockPadding + 1));
        float bottom = regions.ChatBar.H > 0 ? regions.ChatBar.Y : size.Y;
        Dock.Position = new Vector2(MathF.Round((size.X - dock.X) / 2), bottom - ReverieTheme.DockEdge - dock.Y);
        Dock.Size = dock;
    }

    /// <summary>Lays the dock out again (after something in its quick row, such as an add-on's quick control, came or went).</summary>
    public void RefitToolbar()
    {
        if (IsInsideTree())
            ApplyLayout();
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
    }
}
