// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Grids;

namespace Viewer.Godot;

/// <summary>
/// The login screen, built in code from <see cref="LoginScreenSpec"/>: the login picture with a compact card on its left. The card, top
/// to bottom: the viewer's mark and name; Grid (the grids on the list, then Add a grid… and Manage grids…); Name (with the names saved
/// for the chosen grid, each of which can be forgotten); Password (with a show/hide button inside the field); one line with Remember name
/// and Remember password; Start at (Last location, Home, A region…, the last showing a region field); Log in; the status line; and the
/// grid's own sign-up and forgot-password links when it publishes them. The version sits at the bottom left, Preferences and Help at the
/// bottom right, and the grid's news (when it publishes any) at the right in a wide enough window. Enter in any text field logs in, as the
/// official viewer's login panel does with its name and password fields (llpanellogin.cpp, setCommitCallback onClickConnect). Every
/// visible text and every accessible name comes from the string table. The password field hands its text to the model once and is cleared.
/// </summary>
public partial class LoginScreen : Control
{
    /// <summary>The card's width.</summary>
    public const float CardWidth = 360;
    /// <summary>The news sits at the right only in a window wide enough for it beside the card.</summary>
    private const float NewsMinWindow = 1200;
    private const int AddItemId = 1_000_000, ManageItemId = 1_000_001, PlaceholderItemId = 1_000_002, ForgetBase = 10_000;

    private readonly StringTable _strings;
    private readonly LoginModel? _model;
    private readonly SettingsService? _settings;
    private readonly GridList _grids;
    private readonly SavedNames _names;
    private readonly ColorRect _background;
    private readonly ColorRect? _scrim;
    private readonly ColorRect _mark;
    private readonly OptionButton _grid;
    private readonly LineEdit _username;
    private readonly MenuButton _savedNames;
    private readonly LineEdit _password;
    private readonly Button _reveal;
    private readonly CheckBox _remember;
    /// <summary>Remember password: keeps only the login hash, protected for this user (see <see cref="ILoginVault"/>); off by default.</summary>
    private readonly CheckBox _rememberPassword;
    /// <summary>The one line that says why remember password is not offered on this computer (shown in place of the tick).</summary>
    private readonly Label _rememberPasswordNote = new() { Name = "remember_password_note", AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
    private readonly RvSegmented _start;
    private readonly LineEdit _startRegion;
    private readonly Button _login;
    private readonly Button _cancel;
    private readonly Label _status;
    private readonly ScrollContainer _statusScroll;
    private bool _syncing;
    private bool _hasNews;
    private readonly PanelContainer _newsPanel;
    private readonly VBoxContainer _newsBox;
    private readonly HBoxContainer _links;
    private readonly LinkButton _register;
    private readonly LinkButton _forgot;
    private GridBranding _branding = GridBranding.None;
    private LoginOverlay? _overlay;

    /// <summary>The dialogs of the login flow (terms, critical message, update, token, certificate) drawn over this screen.</summary>
    public readonly LoginDialogHost Dialogs;
    public readonly Dictionary<string, Control> ById = new();
    public readonly List<Label> Labels = new();

    /// <summary>Raised when the Preferences button is pressed.</summary>
    public event Action? PreferencesRequested;
    /// <summary>Raised when the Help button is pressed, with the chosen grid's help page when it publishes one.</summary>
    public event Action<Uri?>? HelpRequested;

    public LoginScreen() : this(StringTable.English(), null) { }

    public LoginScreen(StringTable strings, LoginModel? model, SettingsService? settings = null, GridList? grids = null, SavedNames? names = null)
    {
        _strings = strings;
        _model = model;
        _settings = settings;
        _grids = grids ?? new GridList(null);
        _names = names ?? new SavedNames(null);
        Dialogs = new LoginDialogHost(this);
        if (model is not null)
            model.Prompts = Dialogs;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        _background = new ColorRect { Name = "background", MouseFilter = MouseFilterEnum.Ignore };
        _background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_background);
        // the login picture, covering the window with its proportions kept, under a light scrim; if it cannot be read the plain background stays
        var splash = new Image();
        if (UiAssets.DefaultSplash() is { Length: > 0 } splashBytes && splash.LoadJpgFromBuffer(splashBytes) == Error.Ok)
        {
            var picture = new TextureRect
            {
                Name = "splash", Texture = ImageTexture.CreateFromImage(splash), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore,
            };
            picture.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(picture);
            _scrim = new ColorRect { Name = "splash_scrim", MouseFilter = MouseFilterEnum.Ignore };
            _scrim.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(_scrim);
        }

        // the grid's news, right, only when the grid published some and the window is wide enough
        _newsPanel = new PanelContainer { Name = "news", Visible = false, MouseFilter = MouseFilterEnum.Ignore, ThemeTypeVariation = ReverieStyle.Window };
        _newsPanel.AnchorLeft = 1; _newsPanel.AnchorRight = 1;
        _newsPanel.OffsetLeft = -348; _newsPanel.OffsetRight = -32; _newsPanel.OffsetTop = 32;
        _newsPanel.AccessibilityName = strings.Get("login.news.heading");
        var newsMargin = Margins(new MarginContainer(), 16);
        _newsBox = new VBoxContainer { Name = "news_items" };
        newsMargin.AddChild(_newsBox);
        _newsPanel.AddChild(newsMargin);
        AddChild(_newsPanel);

        // the card, at the left, centred top to bottom
        var card = new PanelContainer { Name = "card", ThemeTypeVariation = ReverieStyle.Window };
        card.AnchorTop = 0.5f; card.AnchorBottom = 0.5f;
        card.OffsetLeft = 32; card.GrowVertical = GrowDirection.Both;
        AddChild(card);
        var margin = Margins(new MarginContainer(), 20);
        card.AddChild(margin);
        var box = new VBoxContainer { Name = "card_box", CustomMinimumSize = new Vector2(CardWidth - 40, 0) };
        box.AddThemeConstantOverride("separation", 6);
        margin.AddChild(box);

        // the viewer's mark and name
        var brandRow = new HBoxContainer { Name = "brand_row", MouseFilter = MouseFilterEnum.Ignore };
        brandRow.AddThemeConstantOverride("separation", 10);
        _mark = new ColorRect { Name = "brand_mark", CustomMinimumSize = new Vector2(22, 22), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        brandRow.AddChild(_mark);
        var appName = new Label { Name = "app_name", Text = strings.Get("topbar.app"), ThemeTypeVariation = ReverieStyle.AppName };
        appName.AccessibilityName = appName.Text;
        brandRow.AddChild(appName);
        box.AddChild(brandRow);
        Labels.Add(appName);
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6), MouseFilter = MouseFilterEnum.Ignore });

        Control Field(LoginControl c)
        {
            var label = new Label { Name = c.Id + "_label", Text = strings.Get(c.LabelKey), ThemeTypeVariation = UiTheme.CaptionLabel };
            label.AccessibilityName = label.Text;
            box.AddChild(label);
            Labels.Add(label);
            return label;
        }
        LoginControl Spec(string id) => LoginScreenSpec.Controls.Single(c => c.Id == id);
        T Register<T>(T control, LoginControl c) where T : Control
        {
            control.Name = c.Id;
            control.AccessibilityName = strings.Get(c.AccessibleNameKey);
            ById[c.Id] = control;
            return control;
        }

        // Grid
        Field(Spec("grid"));
        _grid = Register(new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true }, Spec("grid"));
        box.AddChild(_grid);

        // Name, with the names saved for the chosen grid
        Field(Spec("username"));
        var nameRow = new HBoxContainer();
        _username = Register(new LineEdit { PlaceholderText = strings.Get(Spec("username").HintKey!), SizeFlagsHorizontal = SizeFlags.ExpandFill }, Spec("username"));
        nameRow.AddChild(_username);
        _savedNames = new MenuButton { Name = "saved_names", Icon = ReverieIcons.Get(LucideIcons.Down), Flat = false, FocusMode = FocusModeEnum.All, TooltipText = strings.Get("login.names.saved") };
        _savedNames.AccessibilityName = strings.Get("login.names.saved");
        ById["saved_names"] = _savedNames;
        nameRow.AddChild(_savedNames);
        box.AddChild(nameRow);

        // Password, with show/hide inside the field: the field's text stops before the button and scrolls (the theme's password
        // field keeps the button's width free on its right), so the button never covers it
        Field(Spec("password"));
        _password = Register(new LineEdit { Secret = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, ThemeTypeVariation = UiTheme.PasswordField }, Spec("password"));
        _reveal = new Button
        {
            Name = "reveal", Icon = ReverieIcons.Get(LucideIcons.Show), IconAlignment = HorizontalAlignment.Center, FocusMode = FocusModeEnum.All,
            ThemeTypeVariation = ReverieStyle.IconButton, TooltipText = strings.Get("login.password.show"),
            CustomMinimumSize = new Vector2(LoginScreenSpec.RevealButtonWidth, 0),
        };
        _reveal.AccessibilityName = strings.Get("login.password.show");
        _reveal.SetAnchorsPreset(LayoutPreset.RightWide);
        _reveal.GrowHorizontal = GrowDirection.Begin;
        _reveal.OffsetLeft = -LoginScreenSpec.RevealButtonInset - LoginScreenSpec.RevealButtonWidth;
        _reveal.OffsetRight = -LoginScreenSpec.RevealButtonInset;
        _password.AddChild(_reveal);
        ById["reveal"] = _reveal;
        _reveal.Pressed += () =>
        {
            _password.Secret = !_password.Secret;
            _reveal.Icon = ReverieIcons.Get(_password.Secret ? LucideIcons.Show : LucideIcons.Hide);
            _reveal.TooltipText = _reveal.AccessibilityName = strings.Get(_password.Secret ? "login.password.show" : "login.password.hide");
        };
        box.AddChild(_password);

        // Remember name and Remember password, on one line
        var ticks = new HBoxContainer { Name = "remember_row" };
        ticks.AddThemeConstantOverride("separation", 16);
        _remember = Register(new CheckBox { Text = strings.Get("login.remember") }, Spec("remember"));
        _rememberPassword = Register(new CheckBox { Text = strings.Get("login.remember_password") }, Spec("remember_password"));
        // plain tick boxes with their labels, as the official viewer and Firestorm draw their login options: the check box look of
        // the restyled windows (no bar around the row; dimmed when off limits), which follows every theme preset, high contrast included
        _remember.ThemeTypeVariation = ReverieStyle.Check;
        _rememberPassword.ThemeTypeVariation = ReverieStyle.Check;
        ticks.AddChild(_remember);
        ticks.AddChild(_rememberPassword);
        box.AddChild(ticks);
        _rememberPasswordNote.ThemeTypeVariation = UiTheme.MutedLabel;
        box.AddChild(_rememberPasswordNote);

        // Start at
        Field(Spec("start"));
        _start = Register(new RvSegmented(strings.Get("login.start.label"),
            [strings.Get("login.start.last"), strings.Get("login.start.home"), strings.Get("login.start.region")]), Spec("start"));
        // the same segmented control as the windows' view switches (Inventory's tabs): its track as wide as its segments, the segments
        // as tall as their label
        _start.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        RvSegmented.Compact(_start);
        box.AddChild(_start);
        _startRegion = Register(new LineEdit { PlaceholderText = strings.Get(Spec("start_region").HintKey!), Visible = false, CustomMinimumSize = new Vector2(CardWidth - 40, 0) }, Spec("start_region"));
        box.AddChild(_startRegion);
        var (kind, region) = StartChoice.Parse(model?.StartLocation);
        _start.Select((int)kind);
        _startRegion.Text = region ?? "";
        _startRegion.Visible = kind == StartKind.Region;
        _start.Selected += i => _startRegion.Visible = i == (int)StartKind.Region;

        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6), MouseFilter = MouseFilterEnum.Ignore });
        _login = Register(new RvButton(strings.Get("login.button.login"), primary: true) { SizeFlagsHorizontal = SizeFlags.ExpandFill }, Spec("login"));
        box.AddChild(_login);
        _cancel = Register(new RvButton(strings.Get("login.button.cancel")) { SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false }, Spec("cancel"));
        box.AddChild(_cancel);
        _status = Register(new Label { Text = strings.Get("login.status.ready"), AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = ReverieStyle.Status }, Spec("status"));
        _status.AccessibilityLive = AccessibilityServer.AccessibilityLiveMode.Polite;
        _status.CustomMinimumSize = new Vector2(CardWidth - 40, 0);
        // the status line wraps inside the card; a long message (a grid's refusal can be a dozen lines) scrolls inside a bounded
        // area instead of making the card taller than the window, as the official viewer keeps a long login error inside its box
        _statusScroll = new ScrollContainer { Name = "status_scroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = false };
        _statusScroll.AddChild(_status);
        _status.MinimumSizeChanged += FitStatus;
        box.AddChild(_statusScroll);
        Labels.Add(_status);

        _links = new HBoxContainer { Name = "account_links", Visible = false };
        _links.AddThemeConstantOverride("separation", 16);
        _register = new LinkButton { Name = "register", Text = strings.Get("login.register"), Visible = false };
        _register.AccessibilityName = _register.Text;
        _forgot = new LinkButton { Name = "forgot", Text = strings.Get("login.forgot"), Visible = false };
        _forgot.AccessibilityName = _forgot.Text;
        _links.AddChild(_register);
        _links.AddChild(_forgot);
        box.AddChild(_links);

        // the version at the bottom left; Preferences and Help at the bottom right
        var release = new Label { Name = "release", ThemeTypeVariation = UiTheme.MutedLabel, MouseFilter = MouseFilterEnum.Ignore,
            Text = AboutText.LoginRelease(strings, ShownIdentity.Current) };
        release.AccessibilityName = release.Text;
        release.AnchorTop = 1; release.AnchorBottom = 1;
        release.OffsetLeft = 32; release.OffsetTop = -40; release.OffsetBottom = -16;
        release.GrowVertical = GrowDirection.Begin;
        AddChild(release);
        Labels.Add(release);
        var corner = new HBoxContainer { Name = "corner_buttons" };
        corner.AnchorLeft = 1; corner.AnchorRight = 1; corner.AnchorTop = 1; corner.AnchorBottom = 1;
        corner.OffsetRight = -32; corner.OffsetBottom = -16; corner.OffsetTop = -48;
        corner.GrowHorizontal = GrowDirection.Begin; corner.GrowVertical = GrowDirection.Begin;
        var prefs = new Button { Name = "preferences", Icon = IconCache.Get(UiIcons.Preferences, 18), ThemeTypeVariation = ReverieStyle.IconButton, TooltipText = strings.Get("login.preferences"), CustomMinimumSize = new Vector2(32, 32) };
        prefs.AccessibilityName = prefs.TooltipText;
        var help = new Button { Name = "help", Icon = ReverieIcons.Get(LucideIcons.Help, 18), IconAlignment = HorizontalAlignment.Center, ThemeTypeVariation = ReverieStyle.IconButton, TooltipText = strings.Get("login.help"), CustomMinimumSize = new Vector2(32, 32) };
        help.AccessibilityName = help.TooltipText;
        corner.AddChild(prefs);
        corner.AddChild(help);
        AddChild(corner);
        ById["preferences"] = prefs;
        ById["help"] = help;
        prefs.Pressed += () => PreferencesRequested?.Invoke();
        help.Pressed += () => HelpRequested?.Invoke(_grids.Selected?.Info?.Help);

        Resized += () => _newsPanel.Visible = _hasNews && Size.X >= NewsMinWindow;
        FillGrids();
        _grid.ItemSelected += OnGridItem;
        _savedNames.GetPopup().IdPressed += OnSavedName;
        _savedNames.AboutToPopup += FillSavedNames;

        // the remember ticks: the name tick follows the same setting Preferences edits; the password tick only while the name tick is on
        _remember.SetPressedNoSignal(_settings?.Get(Settings.LoginRememberName) ?? false);
        _remember.Toggled += on =>
        {
            if (!_syncing) _settings?.Set(Settings.LoginRememberName, on);
            ShowPasswordTick();
            if (!on && _rememberPassword.ButtonPressed)
                _rememberPassword.ButtonPressed = false;   // as a click: unticking forgets
        };
        if (_settings is not null)
        {
            _settings.Changed += key =>
            {
                _syncing = true;
                if (key == Settings.LoginRememberName.Key)
                    _remember.ButtonPressed = _settings.Get(Settings.LoginRememberName);
                else if (key == Settings.LoginStartLocationKey && _settings.GetText(key) is { Length: > 0 } start && _start.Current != (int)StartKind.Region)
                    _start.Select((int)StartChoice.Parse(start).Kind);
                _syncing = false;
            };
        }
        ShowSavedNameForGrid();

        if (_model is not null)
        {
            // Enter in any text field of the card logs in
            foreach (var c in LoginScreenSpec.Controls.Where(c => c.EnterLogsIn))
                ((LineEdit)ById[c.Id]).TextSubmitted += _ => OnLogin();
            SetUpRememberPassword(_model);
            _login.Pressed += OnLogin;
            _cancel.Pressed += () => _model.Cancel();
            _model.StatusChanged += text => Callable.From(() => { _status.Text = text; ShowBusy(); }).CallDeferred();
            AskBranding();
        }
        else
            ShowPasswordTick();
        Restyle();
        ApplyBranding(GridBranding.None);
    }

    /// <summary>The tallest the status area grows before it scrolls: about five lines of the status text.</summary>
    public const float StatusMaxHeight = 100;

    /// <summary>The status area is as tall as its text, up to <see cref="StatusMaxHeight"/>; a longer text scrolls inside it.</summary>
    private void FitStatus()
    {
        float h = _statusUnbounded ? _status.GetCombinedMinimumSize().Y : Math.Min(_status.GetCombinedMinimumSize().Y, StatusMaxHeight);
        if (!Mathf.IsEqualApprox(_statusScroll.CustomMinimumSize.Y, h))
            _statusScroll.CustomMinimumSize = new Vector2(0, h);
    }

    private static MarginContainer Margins(MarginContainer m, int px)
    {
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            m.AddThemeConstantOverride("margin_" + side, px);
        return m;
    }

    /// <summary>The person's grids and then Second Life, the chosen one shown; then Add a grid… and Manage grids…; with no grid chosen
    /// the chooser shows "Choose a grid".</summary>
    private void FillGrids()
    {
        _grid.Clear();
        int selected = -1;
        var grids = _grids.All;
        if (_grids.Selected is null)
        {
            _grid.AddIconItem(ReverieIcons.Get(LucideIcons.Grid), _strings.Get("login.grid.none"), PlaceholderItemId);
            _grid.SetItemDisabled(0, true);
            selected = 0;
        }
        for (int i = 0; i < grids.Count; i++)
        {
            var e = grids[i];
            _grid.AddIconItem(ReverieIcons.Get(LucideIcons.Grid), e.Name, i);
            _grid.SetItemMetadata(_grid.ItemCount - 1, e.Key);
            _grid.SetItemTooltip(_grid.ItemCount - 1, e.Address);
            if (e.Key == _grids.Selected?.Key)
                selected = _grid.ItemCount - 1;
        }
        _grid.AddSeparator();
        _grid.AddItem(_strings.Get("login.grid.add"), AddItemId);
        _grid.AddItem(_strings.Get("login.grid.manage"), ManageItemId);
        _grid.Select(selected);
        _grid.AccessibilityName = _strings.Get("login.grid.label") + ": " + (_grids.Selected?.Name ?? _strings.Get("login.grid.none"));
        if (_model is not null)
            _model.LoginUri = _grids.Selected?.Address ?? "";
        ShowLinks();
    }

    private void OnGridItem(long index)
    {
        int id = _grid.GetItemId((int)index);
        if (id == AddItemId)
        {
            FillGrids();   // the chooser goes back to the chosen grid
            OpenAddGrid(null);
            return;
        }
        if (id == ManageItemId)
        {
            FillGrids();
            OpenManageGrids();
            return;
        }
        if (id == PlaceholderItemId)
            return;
        _grids.Select((string)_grid.GetItemMetadata((int)index));
        FillGrids();
        GridChanged();
    }

    private void GridChanged()
    {
        ShowSavedNameForGrid();
        if (_model is not null)
            ShowSaved(_model);
        AskBranding();
    }

    /// <summary>Opens Add a grid (or Edit for a listed grid); Add grid puts it on the list and chooses it.</summary>
    public void OpenAddGrid(GridEntry? editing, Action? afterwards = null)
    {
        var panel = new GridAddPanel(_strings, typed => _model?.LookUpGridAsync(typed) ?? System.Threading.Tasks.Task.FromResult(new GridLookupResult(null, "grid.add.no_answer", false)), editing);
        panel.Done += entry =>
        {
            if (entry is not null)
            {
                if (editing is not null && editing.Key != entry.Key)
                    _grids.Remove(editing.Key);
                _grids.Add(entry);
                FillGrids();
                GridChanged();
            }
            _overlay = null;
            afterwards?.Invoke();
        };
        ShowOverlay(panel);
    }

    /// <summary>Opens Manage grids.</summary>
    public void OpenManageGrids()
    {
        GridManagePanel? panel = null;
        panel = new GridManagePanel(_strings, _grids,
            entry => _model?.RefreshGridAsync(entry) ?? System.Threading.Tasks.Task.FromResult(new GridLookupResult(null, "grid.add.no_answer", false)),
            entry =>
            {
                panel!.Visible = false;
                var edit = new GridAddPanel(_strings, typed => _model?.LookUpGridAsync(typed) ?? System.Threading.Tasks.Task.FromResult(new GridLookupResult(null, "grid.add.no_answer", false)), entry);
                edit.Done += changed =>
                {
                    if (changed is not null)
                    {
                        string? chosen = _grids.Selected?.Key;
                        if (entry.Key != changed.Key)
                            _grids.Remove(entry.Key);
                        _grids.Add(changed);
                        if (chosen is not null && chosen != entry.Key)
                            _grids.Select(chosen);   // editing a grid does not choose it
                    }
                    if (IsInstanceValid(panel))
                    {
                        panel.Visible = true;
                        panel.Fill();
                        _overlay = panel;
                    }
                    FillGrids();
                    GridChanged();
                };
                _overlay = edit;
                AddChild(edit);
            });
        panel.Changed += () => { FillGrids(); GridChanged(); };
        panel.Removed += entry => _model?.ForgetGridCertificates(entry.Address, entry.Info?.Login);
        panel.TreeExiting += () => { if (_overlay == panel) _overlay = null; };
        ShowOverlay(panel);
    }

    private void ShowOverlay(LoginOverlay panel)
    {
        if (_overlay is { } old && IsInstanceValid(old))
            old.QueueFree();
        _overlay = panel;
        AddChild(panel);
    }

    /// <summary>The panel over the screen now (Add a grid or Manage grids), if any.</summary>
    internal LoginOverlay? Overlay => _overlay is { } o && IsInstanceValid(o) && o.IsInsideTree() ? o : null;

    /// <summary>The saved names of the chosen grid, then Forget for each.</summary>
    private void FillSavedNames()
    {
        var popup = _savedNames.GetPopup();
        popup.Clear();
        var names = _names.Names(_grids.Selected?.Address ?? "");
        for (int i = 0; i < names.Count; i++)
            popup.AddItem(names[i], i);
        if (names.Count > 0)
            popup.AddSeparator();
        for (int i = 0; i < names.Count; i++)
            popup.AddItem(_strings.Get("login.names.forget", names[i]), ForgetBase + i);
    }

    private void OnSavedName(long id)
    {
        string grid = _grids.Selected?.Address ?? "";
        var names = _names.Names(grid);
        if (id >= ForgetBase && id - ForgetBase < names.Count)
        {
            string gone = names[(int)(id - ForgetBase)];
            _names.Forget(grid, gone, _model?.Vault);   // its saved password goes with it
            if (string.Equals(_username.Text.Trim(), gone, StringComparison.OrdinalIgnoreCase))
                _username.Clear();
        }
        else if (id >= 0 && id < names.Count)
            _username.Text = names[(int)id];
        if (_model is not null)
            ShowSaved(_model);
        ShowSavedNameButton();
    }

    /// <summary>The name field shows the latest name saved for the chosen grid while Remember name is on (an older version's one saved
    /// name, when the grid has none yet).</summary>
    private void ShowSavedNameForGrid()
    {
        if (_remember.ButtonPressed)
        {
            var names = _names.Names(_grids.Selected?.Address ?? "");
            _username.Text = names.Count > 0 ? names[0] : _settings is not null ? LoginNames.Load(_settings) : "";
        }
        ShowSavedNameButton();
    }

    private void ShowSavedNameButton() => _savedNames.Disabled = _names.Names(_grids.Selected?.Address ?? "").Count == 0;

    private void ShowPasswordTick()
    {
        bool enabled = StartChoice.PasswordTickEnabled(_remember.ButtonPressed);
        _rememberPassword.Disabled = !enabled;
    }

    private void ShowBusy()
    {
        bool busy = _model?.Busy ?? false;
        _login.Visible = !busy;
        _cancel.Visible = busy;
    }

    /// <summary>Shows the grid's own name, news and account links (the placeholder look for <see cref="GridBranding.None"/>).</summary>
    public void ApplyBranding(GridBranding branding)
    {
        _branding = branding;
        foreach (var child in _newsBox.GetChildren())
        {
            _newsBox.RemoveChild(child);
            child.QueueFree();
        }
        _hasNews = branding.News.Count > 0;
        _newsPanel.Visible = _hasNews && Size.X >= NewsMinWindow;
        if (branding.News.Count > 0)
        {
            var heading = new Label { Text = _strings.Get("login.news.heading"), ThemeTypeVariation = UiTheme.TitleLabel };
            heading.AccessibilityName = heading.Text;
            _newsBox.AddChild(heading);
            foreach (var item in branding.News)
            {
                if (item.Title.Length > 0)
                {
                    var t = new Label { Text = item.Title, ThemeTypeVariation = UiTheme.AccentLabel, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                    t.AccessibilityName = item.Title;
                    _newsBox.AddChild(t);
                }
                var body = new Label { Text = item.Text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                body.AccessibilityName = item.Text;
                _newsBox.AddChild(body);
            }
        }
        ShowLinks();
    }

    /// <summary>"Create an account" and "Forgot password?", only when the grid gives those pages (its grid info as listed, else as just read).</summary>
    private void ShowLinks()
    {
        var info = _grids.Selected?.Info;
        var register = info?.Register ?? _branding.Register;
        var forgot = info?.Password ?? _branding.ForgotPassword;
        _register.Visible = register is not null;
        _register.Uri = register?.ToString() ?? "";
        _forgot.Visible = forgot is not null;
        _forgot.Uri = forgot?.ToString() ?? "";
        _links.Visible = _register.Visible || _forgot.Visible;
    }

    public override void _EnterTree()
    {
        UiTheme.Changed += Restyle;
        TellShell();
    }

    public override void _ExitTree()
    {
        UiTheme.Changed -= Restyle;
        _shellTold?.SetLoginShowing(this, false);
        _shellTold = null;
    }

    public override void _Notification(int what)
    {
        if (what == (int)NotificationVisibilityChanged && IsInsideTree())
            TellShell();
    }

    /// <summary>The shell this screen last told whether it shows.</summary>
    private ShellRoot? _shellTold;

    /// <summary>Tells the shell holding this screen whether it shows: the official login screen has no search, so the top bar's search
    /// icon and its key wait for the login screen to go.</summary>
    private void TellShell()
    {
        for (Node? n = GetParent(); n is not null; n = n.GetParent())
            if (n is ShellRoot shell)
            {
                _shellTold = shell;
                shell.SetLoginShowing(this, IsVisibleInTree());
                return;
            }
    }

    private void Restyle()
    {
        _background.Color = ShellTheme.C(UiTheme.Tokens.Bar.Opaque());
        if (_scrim is not null)
            _scrim.Color = ShellTheme.C(UiTheme.Tokens.Scrim) with { A = 0.25f };
        _mark.Color = ShellTheme.C(UiTheme.Tokens.Accent);
    }

    private void AskBranding()
    {
        if (_model is null)
            return;
        _ = _model.RefreshBrandingAsync(_grids.Selected?.Address ?? "");
    }

    /// <summary>The remember-password tick: offered only where the vault can keep a hash safely (else one line says why); ticked when the
    /// grid and name have a saved hash, which an empty password field then uses; unticking forgets it.</summary>
    private void SetUpRememberPassword(LoginModel m)
    {
        if (m.Vault is not { Available: true })
        {
            _rememberPassword.Visible = false;
            _rememberPasswordNote.Text = m.Vault?.UnavailableKey is { } why ? _strings.Get(why) : _strings.Get("login.remember_password.unavailable");
            _rememberPasswordNote.AccessibilityName = _rememberPasswordNote.Text;
            _rememberPasswordNote.Visible = true;
            return;
        }
        _rememberPassword.SetPressedNoSignal((_settings?.Get(Settings.LoginRememberPassword) ?? false) && _remember.ButtonPressed);
        _rememberPassword.Toggled += on =>
        {
            TakeFields(m);
            m.SetRememberPassword(on);   // unticking forgets the hash saved for this grid and name
            _settings?.Set(Settings.LoginRememberPassword, on);
            ShowSaved(m);
        };
        _username.TextChanged += _ => ShowSaved(m);
        ShowPasswordTick();
        ShowSaved(m);
    }

    /// <summary>Says in the password field's hint when a saved password will be used (the field itself stays empty and masked).</summary>
    private void ShowSaved(LoginModel m)
    {
        TakeFields(m);
        bool saved = _rememberPassword.Visible && _remember.ButtonPressed && m.HasSavedPassword;
        if (saved && !_rememberPassword.ButtonPressed)
            _rememberPassword.SetPressedNoSignal(true);
        _password.PlaceholderText = saved ? _strings.Get("login.password.saved") : "";
    }

    private void TakeFields(LoginModel m)
    {
        m.LoginUri = _grids.Selected?.Address ?? "";
        m.UserName = _username.Text;
    }

    /// <summary>Fills the name and password fields as typed and logs in as Enter does (the automation's screen login; development builds).</summary>
    internal void TypeAndSubmit(string userName, string password, bool rememberPassword)
    {
        _username.Text = userName;
        if (rememberPassword && !_remember.ButtonPressed)
            _remember.ButtonPressed = true;
        ShowSaved(_model!);
        if (_rememberPassword.Visible && _rememberPassword.ButtonPressed != rememberPassword)
            _rememberPassword.ButtonPressed = rememberPassword;   // as a click: unticking forgets
        _password.Text = password;
        _password.GrabFocus();
        _password.EmitSignal(LineEdit.SignalName.TextSubmitted, _password.Text);
    }

    /// <summary>Sets Start at as the three-way control would (the automation's screen login).</summary>
    internal void SetStart(string start)
    {
        var (kind, region) = StartChoice.Parse(start);
        _start.Select((int)kind);
        _startRegion.Text = region ?? "";
    }

    /// <summary>Chooses the grid at this address, adding it when it is not on the list, as --grid does (the automation's screen login).</summary>
    internal void SetGrid(string address)
    {
        if (_grids.ApplyCommandLine(address) is null)
            return;
        FillGrids();
        GridChanged();
    }

    /// <summary>Shows one example grid as chosen (the render cases: a list kept in memory only).</summary>
    private bool _statusUnbounded;

    /// <summary>Puts a status text on the card as a login would (test pictures). <paramref name="unbounded"/> lets the area grow with its
    /// text, as before it was bounded: the negative control of the long-message picture.</summary>
    internal void ShowStatusForTest(string text, bool unbounded)
    {
        _statusUnbounded = unbounded;
        _status.Text = text;
        FitStatus();
    }

    internal void ShowExampleGrid(GridEntry entry)
    {
        _grids.Add(entry);
        FillGrids();
    }

    /// <summary>What the screen shows about remember password: offered, ticked, a saved hash in use (never the hash).</summary>
    internal object RememberState() => new
    {
        offered = _rememberPassword.Visible,
        ticked = _rememberPassword.ButtonPressed,
        enabled = !_rememberPassword.Disabled,
        saved_hint = _password.PlaceholderText.Length > 0,
        password_field_empty = _password.Text.Length == 0,
        note = _rememberPasswordNote.Visible,
    };

    /// <summary>The card's state and where its controls stand (viewport coordinates of their centres), for the automation's real-input checks.
    /// Never the password.</summary>
    internal object Layout()
    {
        var at = new Dictionary<string, object>();
        void Put(string id, Control c)
        {
            if (!c.IsVisibleInTree())
                return;
            var r = c.GetGlobalRect();
            at[id] = new { x = r.Position.X + r.Size.X / 2, y = r.Position.Y + r.Size.Y / 2, w = r.Size.X, h = r.Size.Y, disabled = c is BaseButton { Disabled: true } };
        }
        foreach (var (id, c) in ById)
            Put(id, c);
        for (int i = 0; i < _start.Segments.Count; i++)
            Put("start_" + i, _start.Segments[i]);
        if (Overlay is { } o)
            foreach (var (id, c) in o.ById)
                Put(id, c);
        var popup = _grid.GetPopup();
        var focused = GetViewport().GuiGetFocusOwner();
        return new
        {
            controls = at,
            focus = focused?.Name.ToString(),
            grid = _grids.Selected?.Name,
            grid_address = _grids.Selected?.Address,
            grids = _grids.All.Select(e => e.Name).ToArray(),
            grid_popup_open = popup.Visible,
            grid_popup_focused = popup.Visible ? popup.GetFocusedItem() : -1,
            grid_add_index = _grid.GetItemIndex(AddItemId),
            grid_manage_index = _grid.GetItemIndex(ManageItemId),
            names_popup_open = _savedNames.GetPopup().Visible,
            names_popup_focused = _savedNames.GetPopup().Visible ? _savedNames.GetPopup().GetFocusedItem() : -1,
            saved_names = _names.Names(_grids.Selected?.Address ?? "").ToArray(),
            username = _username.Text,
            password_typed = _password.Text.Length > 0,
            remember = _remember.ButtonPressed,
            start = _start.Current,
            overlay = Overlay?.Name.ToString(),
            add = (Overlay as GridAddPanel)?.State(),
            manage = (Overlay as GridManagePanel)?.State(),
            links = new { register = _register.Visible, forgot = _forgot.Visible },
        };
    }

    private async void OnLogin()
    {
        var m = _model!;
        if (m.Busy || Overlay is not null)
            return;
        TakeFields(m);
        m.StartLocation = StartChoice.ToStart((StartKind)_start.Current, _startRegion.Text);
        bool rememberName = _remember.ButtonPressed;
        if (_settings is not null)
        {
            // the name is kept on this computer only when the person asked; the password never is
            _settings.Set(Settings.LoginRememberName, rememberName);
            LoginNames.Save(_settings, rememberName, m.UserName);
        }
        bool remember = _rememberPassword.Visible && rememberName && _rememberPassword.ButtonPressed;
        if (_rememberPassword.Visible)
            m.SetRememberPassword(remember);
        // an empty field with a saved hash for this grid and name logs in with that hash; a typed password always wins
        if (!(_password.Text.Length == 0 && remember && m.UseSavedPassword()))
            m.SetPassword(_password.Text);
        _password.Clear();   // the field does not keep it either
        string grid = m.LoginUri, name = m.UserName;
        ShowBusy();
        bool ok = await m.LoginAsync();
        Callable.From(() =>
        {
            if (ok && rememberName)
                _names.Remember(grid, name);
            else if (ok)
                _names.Forget(grid, name, null);   // logged in with Remember name off: the name is not kept for this grid
            ShowSavedNameButton();
            ShowBusy();
            ShowSaved(m);   // a refused saved hash is gone: the hint goes with it
        }).CallDeferred();
    }
}
