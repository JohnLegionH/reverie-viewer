// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The login screen, built in code from <see cref="LoginScreenSpec"/>: the grid's mark, name and tagline at the top left, the
/// grid's news at the left when the grid published any, and the login card in the lower centre with the grid's address and whether
/// it is secure, the account fields, remember-my-username, the start location, Log in, and the grid's own account links when it
/// published them. A grid that published nothing gets the placeholder name; the background is the default picture under a scrim (the
/// grid's own splash address is read but not fetched yet). Every visible text and
/// every accessible name comes from the string table. The password field hands its text to the model once and is cleared.
/// </summary>
public partial class LoginScreen : Control
{
    private readonly StringTable _strings;
    private readonly LoginModel? _model;
    private readonly SettingsService? _settings;
    private readonly ColorRect _background;
    private readonly ColorRect? _scrim;
    private readonly ColorRect _mark;
    private readonly Label _gridName;
    private readonly Label _tagline;
    /// <summary>The news sits beside the card only in a window wide enough for both; a narrower one leaves it out.</summary>
    private const float NewsMinWindow = 1200;
    private bool _hasNews;
    private readonly PanelContainer _newsPanel;
    private readonly VBoxContainer _newsBox;
    private readonly Label _connecting = new() { Name = "connecting", AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = UiTheme.CaptionLabel };
    private readonly CheckBox _remember = new() { Name = "remember" };
    /// <summary>Remember password: keeps only the login hash, protected for this user (see <see cref="ILoginVault"/>); off by default.</summary>
    private readonly CheckBox _rememberPassword = new() { Name = "remember_password" };
    /// <summary>The one line that says why remember password is not offered on this computer (shown in place of the tick).</summary>
    private readonly Label _rememberPasswordNote = new() { Name = "remember_password_note", AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
    private bool _syncing;
    private readonly HBoxContainer _links;
    private readonly LinkButton _register;
    private readonly LinkButton _forgot;
    /// <summary>The dialogs of the login flow (terms, critical message, update, token, certificate) drawn over this screen.</summary>
    public readonly LoginDialogHost Dialogs;
    public readonly Dictionary<string, Control> ById = new();
    public readonly List<Label> Labels = new();

    public LoginScreen() : this(StringTable.English(), null) { }

    public LoginScreen(StringTable strings, LoginModel? model, SettingsService? settings = null)
    {
        _strings = strings;
        _model = model;
        _settings = settings;
        Dialogs = new LoginDialogHost(this);
        if (model is not null)
            model.Prompts = Dialogs;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        _background = new ColorRect { Name = "background", MouseFilter = MouseFilterEnum.Ignore };
        _background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_background);

        // the default background picture, covering the window with its proportions kept, under a scrim so the grid's mark and
        // name stay readable over it; if the picture cannot be read the plain background stays
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

        // the grid's mark, name and tagline, top left
        var brand = new VBoxContainer { Name = "brand", MouseFilter = MouseFilterEnum.Ignore };
        brand.OffsetLeft = 24; brand.OffsetTop = 20; brand.OffsetRight = 460;
        var brandRow = new HBoxContainer { Name = "brand_row", MouseFilter = MouseFilterEnum.Ignore };
        _mark = new ColorRect { Name = "brand_mark", CustomMinimumSize = new Vector2(24, 24), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        brandRow.AddChild(_mark);
        _gridName = new Label { Name = "grid_name", ThemeTypeVariation = UiTheme.BrandLabel, Text = strings.Get("brand.default") };
        _gridName.AccessibilityName = _gridName.Text;
        brandRow.AddChild(_gridName);
        brand.AddChild(brandRow);
        _tagline = new Label { Name = "tagline", ThemeTypeVariation = UiTheme.MutedLabel, Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        brand.AddChild(_tagline);
        AddChild(brand);

        // the viewer's release name and version, top right, clear of the login card at every window size (the release name is the
        // string table's brand entry, not the grid's)
        var release = new Label { Name = "release", ThemeTypeVariation = UiTheme.MutedLabel, MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Right, Text = AboutText.LoginRelease(strings, Viewer.Core.Identity.ViewerIdentity.Current) };
        release.AccessibilityName = release.Text;
        release.AnchorLeft = 1; release.AnchorRight = 1;
        release.OffsetLeft = -184; release.OffsetRight = -24; release.OffsetTop = 20; release.OffsetBottom = 44;
        release.GrowHorizontal = GrowDirection.Begin;   // a line longer than the box grows to the left, so its end stays on screen
        AddChild(release);

        // the grid's news, left, only when the grid published some
        _newsPanel = new PanelContainer { Name = "news", Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _newsPanel.OffsetLeft = 24; _newsPanel.OffsetTop = 96; _newsPanel.OffsetRight = 340;
        _newsPanel.AccessibilityName = strings.Get("login.news.heading");
        _newsBox = new VBoxContainer { Name = "news_items" };
        _newsPanel.AddChild(_newsBox);
        AddChild(_newsPanel);

        // the card, lower centre
        var card = new PanelContainer { Name = "card" };
        card.AnchorLeft = 0.5f; card.AnchorRight = 0.5f; card.AnchorTop = 1; card.AnchorBottom = 1;
        card.GrowHorizontal = GrowDirection.Both; card.GrowVertical = GrowDirection.Begin;
        card.OffsetBottom = -32;
        AddChild(card);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        card.AddChild(box);
        var title = new Label { Text = strings.Get(LoginScreenSpec.TitleKey), ThemeTypeVariation = UiTheme.TitleLabel };
        title.AccessibilityName = strings.Get(LoginScreenSpec.TitleKey);
        box.AddChild(title);
        Labels.Add(title);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        foreach (var c in LoginScreenSpec.Controls)
        {
            Control control;
            switch (c.Kind)
            {
                case LoginControlKind.Text:
                case LoginControlKind.Secret:
                    var label = new Label { Text = strings.Get(c.LabelKey), ThemeTypeVariation = UiTheme.CaptionLabel };
                    label.AccessibilityName = strings.Get(c.LabelKey);
                    box.AddChild(label);
                    Labels.Add(label);
                    var edit = new LineEdit { Secret = c.Kind == LoginControlKind.Secret };
                    if (c.HintKey is not null) edit.PlaceholderText = strings.Get(c.HintKey);
                    if (c.Id == "start") edit.Text = model?.StartLocation ?? "last";
                    if (c.Id == "grid") edit.Text = model?.LoginUri ?? "";
                    box.AddChild(edit);
                    control = edit;
                    break;
                case LoginControlKind.Button:
                    control = new Button { Text = strings.Get(c.LabelKey) };
                    if (c.Id == "login")
                        ((Button)control).ThemeTypeVariation = UiTheme.PrimaryButton;
                    buttons.AddChild(control);
                    break;
                case LoginControlKind.Note:
                    var note = new Label { Text = strings.Get(c.LabelKey), AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = UiTheme.MutedLabel };
                    box.AddChild(note);
                    Labels.Add(note);
                    control = note;
                    if (c.Id == "resolved")
                    {
                        // under the grid field: the address that will be used and whether it is secure, then the address the last attempt used
                        box.AddChild(_connecting);
                        box.MoveChild(_connecting, note.GetIndex());
                    }
                    break;
                default:
                    var status = new Label { Text = strings.Get(c.LabelKey), AutowrapMode = TextServer.AutowrapMode.WordSmart };
                    status.AccessibilityLive = AccessibilityServer.AccessibilityLiveMode.Polite;
                    control = status;
                    Labels.Add(status);
                    break;
            }
            control.Name = c.Id;
            control.AccessibilityName = strings.Get(c.AccessibleNameKey);
            ById[c.Id] = control;
            if (c.Id == "password")
            {
                _remember.Text = strings.Get("login.remember");
                _remember.AccessibilityName = _remember.Text;
                box.AddChild(_remember);
                _rememberPassword.Text = strings.Get("login.remember_password");
                _rememberPassword.AccessibilityName = _rememberPassword.Text;
                box.AddChild(_rememberPassword);
                _rememberPasswordNote.ThemeTypeVariation = UiTheme.MutedLabel;
                box.AddChild(_rememberPasswordNote);
                ById["remember_password"] = _rememberPassword;
            }
            if (c.Kind == LoginControlKind.Status)
            {
                box.AddChild(buttons);
                box.AddChild(control);
            }
        }
        _links = new HBoxContainer { Name = "account_links", Visible = false };
        _register = new LinkButton { Name = "register", Text = strings.Get("login.register"), Visible = false };
        _register.AccessibilityName = _register.Text;
        _forgot = new LinkButton { Name = "forgot", Text = strings.Get("login.forgot"), Visible = false };
        _forgot.AccessibilityName = _forgot.Text;
        _links.AddChild(_register);
        _links.AddChild(_forgot);
        box.AddChild(_links);

        var grid = (LineEdit)ById["grid"];
        grid.TextChanged += _ => ShowConnecting();
        grid.TextSubmitted += _ => AskBranding();
        grid.FocusExited += AskBranding;
        ShowConnecting();
        Resized += () => _newsPanel.Visible = _hasNews && Size.X >= NewsMinWindow;
        if (_settings is not null)
        {
            // the same settings Preferences edits: this switch writes at once, and follows a change made there
            _remember.Toggled += on => { if (!_syncing) _settings.Set(Settings.LoginRememberName, on); };
            string shownStart = ((LineEdit)ById["start"]).Text;
            _settings.Changed += key =>
            {
                _syncing = true;
                if (key == Settings.LoginRememberName.Key)
                    _remember.SetPressedNoSignal(_settings.Get(Settings.LoginRememberName));
                else if (key == Settings.LoginStartLocationKey && _settings.GetText(key) is { Length: > 0 } start)
                {
                    var edit = (LineEdit)ById["start"];
                    if (edit.Text == shownStart || string.IsNullOrWhiteSpace(edit.Text))
                        edit.Text = start;   // only a field the person has not typed in
                    shownStart = start;
                }
                _syncing = false;
            };
        }
        if (_settings is not null && _settings.Get(Settings.LoginRememberName))
        {
            _remember.ButtonPressed = true;
            ((LineEdit)ById["username"]).Text = LoginNames.Load(_settings);
        }
        if (_model is not null)
        {
            // Enter in the name or the password field logs in, as the official viewer's login panel does
            ((LineEdit)ById["username"]).TextSubmitted += _ => OnLogin();
            ((LineEdit)ById["password"]).TextSubmitted += _ => OnLogin();
            SetUpRememberPassword(_model);
            ((Button)ById["login"]).Pressed += OnLogin;
            ((Button)ById["cancel"]).Pressed += () => _model.Cancel();
            _model.ResolvedChanged += text => Callable.From(() => ((Label)ById["resolved"]).Text = strings.Get("login.resolved.is", text)).CallDeferred();
            _model.StatusChanged += text => Callable.From(() => ((Label)ById["status"]).Text = text).CallDeferred();
            AskBranding();
        }
        Restyle();
        ApplyBranding(GridBranding.None);
    }

    /// <summary>Shows the grid's own name, tagline, news and account links (the placeholder look for <see cref="GridBranding.None"/>).</summary>
    public void ApplyBranding(GridBranding branding)
    {
        _gridName.Text = branding.Name ?? _strings.Get("brand.default");
        _gridName.AccessibilityName = _gridName.Text;
        _tagline.Text = branding.Tagline ?? "";
        _tagline.Visible = branding.Tagline is not null;
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
        _register.Visible = branding.Register is not null;
        _register.Uri = branding.Register?.ToString() ?? "";
        _forgot.Visible = branding.ForgotPassword is not null;
        _forgot.Uri = branding.ForgotPassword?.ToString() ?? "";
        _links.Visible = _register.Visible || _forgot.Visible;
    }

    public override void _EnterTree() => UiTheme.Changed += Restyle;

    public override void _ExitTree() => UiTheme.Changed -= Restyle;

    private void Restyle()
    {
        _background.Color = ShellTheme.C(UiTheme.Tokens.Bar.Opaque());
        if (_scrim is not null)
            _scrim.Color = ShellTheme.C(UiTheme.Tokens.Scrim);
        _mark.Color = ShellTheme.C(UiTheme.Tokens.Accent);
    }

    /// <summary>Shows the address that will be used.</summary>
    public void ShowConnecting()
    {
        string typed = ((LineEdit)ById["grid"]).Text;
        var uri = GridAddress.Tidy(typed).Uri;
        _connecting.Visible = uri is not null;
        if (uri is null)
            return;
        string host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        _connecting.Text = _strings.Get("login.connecting", host);
        _connecting.AccessibilityName = _connecting.Text;
    }

    private void AskBranding()
    {
        if (_model is null)
            return;
        _ = _model.RefreshBrandingAsync(((LineEdit)ById["grid"]).Text);
    }

    /// <summary>The remember-password tick: offered only where the vault can keep a hash safely (else one line says why); ticked when the
    /// grid and name typed have a saved hash, which an empty password field then uses; unticking forgets it.</summary>
    private void SetUpRememberPassword(LoginModel m)
    {
        if (m.Vault is not { Available: true } vault)
        {
            _rememberPassword.Visible = false;
            _rememberPasswordNote.Text = m.Vault?.UnavailableKey is { } why ? _strings.Get(why) : _strings.Get("login.remember_password.unavailable");
            _rememberPasswordNote.AccessibilityName = _rememberPasswordNote.Text;
            _rememberPasswordNote.Visible = true;
            return;
        }
        _rememberPassword.SetPressedNoSignal(_settings?.Get(Settings.LoginRememberPassword) ?? false);
        _rememberPassword.Toggled += on =>
        {
            TakeFields(m);
            m.SetRememberPassword(on);   // unticking forgets the hash saved for this grid and name
            _settings?.Set(Settings.LoginRememberPassword, on);
            ShowSaved(m);
        };
        ((LineEdit)ById["username"]).TextChanged += _ => ShowSaved(m);
        ((LineEdit)ById["grid"]).TextChanged += _ => ShowSaved(m);
        ShowSaved(m);
    }

    /// <summary>Says in the password field's hint when a saved password will be used (the field itself stays empty and masked).</summary>
    private void ShowSaved(LoginModel m)
    {
        TakeFields(m);
        bool saved = _rememberPassword.Visible && m.HasSavedPassword;
        if (saved && !_rememberPassword.ButtonPressed)
            _rememberPassword.SetPressedNoSignal(true);
        ((LineEdit)ById["password"]).PlaceholderText = saved ? _strings.Get("login.password.saved") : "";
    }

    private void TakeFields(LoginModel m)
    {
        m.LoginUri = ((LineEdit)ById["grid"]).Text;
        m.UserName = ((LineEdit)ById["username"]).Text;
    }

    /// <summary>Fills the name and password fields as typed and logs in as Enter does (the automation's screen login; development builds).</summary>
    internal void TypeAndSubmit(string userName, string password, bool rememberPassword)
    {
        ((LineEdit)ById["username"]).Text = userName;
        ShowSaved(_model!);
        if (_rememberPassword.Visible && _rememberPassword.ButtonPressed != rememberPassword)
            _rememberPassword.ButtonPressed = rememberPassword;   // as a click: unticking forgets
        var pw = (LineEdit)ById["password"];
        pw.Text = password;
        pw.GrabFocus();
        pw.EmitSignal(LineEdit.SignalName.TextSubmitted, pw.Text);
    }

    /// <summary>Puts an address in the grid field as typing it would (the automation's screen login).</summary>
    internal void SetStart(string start) => ((LineEdit)ById["start"]).Text = start;

    internal void SetGrid(string address)
    {
        var grid = (LineEdit)ById["grid"];
        grid.Text = address;
        grid.EmitSignal(LineEdit.SignalName.TextChanged, address);
    }

    /// <summary>What the screen shows about remember password: offered, ticked, a saved hash in use (never the hash).</summary>
    internal object RememberState() => new
    {
        offered = _rememberPassword.Visible,
        ticked = _rememberPassword.ButtonPressed,
        saved_hint = ((LineEdit)ById["password"]).PlaceholderText.Length > 0,
        password_field_empty = ((LineEdit)ById["password"]).Text.Length == 0,
        note = _rememberPasswordNote.Visible,
    };

    private async void OnLogin()
    {
        var m = _model!;
        if (m.Busy)
            return;
        m.LoginUri = ((LineEdit)ById["grid"]).Text;
        m.UserName = ((LineEdit)ById["username"]).Text;
        m.StartLocation = ((LineEdit)ById["start"]).Text;
        if (_settings is not null)
        {
            // the name is kept on this computer only when the person asked; the password never is
            _settings.Set(Settings.LoginRememberName, _remember.ButtonPressed);
            LoginNames.Save(_settings, _remember.ButtonPressed, m.UserName);
        }
        var pw = (LineEdit)ById["password"];
        bool remember = _rememberPassword.Visible && _rememberPassword.ButtonPressed;
        if (_rememberPassword.Visible)
            m.SetRememberPassword(remember);
        // an empty field with a saved hash for this grid and name logs in with that hash; a typed password always wins
        if (!(pw.Text.Length == 0 && remember && m.UseSavedPassword()))
            m.SetPassword(pw.Text);
        pw.Clear();   // the field does not keep it either
        await m.LoginAsync();
        Callable.From(() => ShowSaved(m)).CallDeferred();   // a refused saved hash is gone: the hint goes with it
    }
}
