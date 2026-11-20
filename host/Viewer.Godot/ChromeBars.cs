// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Godot;
using Viewer.Shell;
using Viewer.Core.Session;
using Viewer.Shell.Chat;

namespace Viewer.Godot;

/// <summary>
/// The navigation bar under the menu bar: the home button, and the location field with its pin. Typing a region name and
/// pressing Enter asks for a teleport there; the field shows where the avatar is. Back, forward, favourites and search are not
/// here because the viewer has none of those yet.
/// </summary>
public sealed partial class NavBar : PanelContainer
{
    private readonly TextureRect _pin;

    public Button HomeButton { get; }
    public LineEdit Location { get; }

    /// <summary>The person asked to teleport to the region named in the field.</summary>
    public event Action<string>? LocationSubmitted;

    public NavBar(StringTable strings)
    {
        Name = "nav_bar";
        ThemeTypeVariation = UiTheme.BarPanel;
        MouseFilter = MouseFilterEnum.Stop;
        AccessibilityName = strings.Get("nav.bar");
        var row = new HBoxContainer { Name = "nav_row" };
        AddChild(row);
        HomeButton = new Button { Name = "home", ThemeTypeVariation = UiTheme.FlatButton, TooltipText = strings.Get("nav.home"), Icon = IconCache.Get(UiIcons.Home, 18), FocusMode = FocusModeEnum.Click };
        HomeButton.AddThemeConstantOverride("icon_max_width", 18);
        HomeButton.AccessibilityName = strings.Get("nav.home");
        row.AddChild(HomeButton);
        _pin = new TextureRect { Name = "pin", Texture = IconCache.Get(UiIcons.Pin, 16), StretchMode = TextureRect.StretchModeEnum.KeepCentered, CustomMinimumSize = new Vector2(20, 0), MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(_pin);
        Location = new LineEdit { Name = "location", PlaceholderText = strings.Get("nav.location.hint"), SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = 128 };
        Location.AccessibilityName = strings.Get("nav.location");
        PlaceholderFit.Keep(Location, strings.Get("nav.location.hint"));
        Location.TextSubmitted += text => { if (text.Trim().Length > 0) LocationSubmitted?.Invoke(text.Trim()); };
        row.AddChild(Location);
        Restyle();
    }

    /// <summary>Shows where the avatar is, unless the person is typing in the field.</summary>
    public void SetLocation(string text)
    {
        if (!Location.HasFocus())
            Location.Text = text;
    }

    public override void _EnterTree() => UiTheme.Changed += Restyle;

    public override void _ExitTree() => UiTheme.Changed -= Restyle;

    private void Restyle()
    {
        _pin.Modulate = ShellTheme.C(UiTheme.Tokens.Accent);
        HomeButton.Icon = IconCache.Get(UiIcons.Home, 18);
        _pin.Texture = IconCache.Get(UiIcons.Pin, 16);
    }
}

/// <summary>
/// The chat bar above the toolbar: a line to type in, Say / Shout / Whisper, and send. It speaks into the same nearby chat as
/// the nearby chat window (Enter says; Ctrl+Enter shouts; Shift+Enter whispers). Gestures and push-to-talk are not here
/// because the viewer has neither yet.
/// </summary>
public sealed partial class ChatBar : PanelContainer
{
    private CommsHub? _hub;

    public LineEdit Input { get; }
    public OptionButton Kind { get; }
    public Button SendButton { get; }

    public ChatBar(StringTable strings)
    {
        Name = "chat_bar";
        ThemeTypeVariation = UiTheme.BarPanel;
        MouseFilter = MouseFilterEnum.Stop;
        AccessibilityName = strings.Get("chatbar.title");
        var row = new HBoxContainer { Name = "chat_row" };
        AddChild(row);
        Input = new LineEdit { Name = "input", PlaceholderText = strings.Get("chat.input.hint"), SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = 1023 };
        Input.AccessibilityName = strings.Get("chat.input");
        PlaceholderFit.Keep(Input, strings.Get("chat.input.hint"));
        Input.TextChanged += t => _hub?.Nearby.InputChanged(t);
        Input.FocusExited += () => _hub?.Nearby.InputLostFocus();
        Input.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Echo: false } k && k.Keycode is Key.Enter or Key.KpEnter)
            {
                Submit(k.CtrlPressed ? ChatKind.Shout : k.ShiftPressed ? ChatKind.Whisper : null);
                Input.AcceptEvent();
            }
        };
        row.AddChild(Input);
        Kind = new OptionButton { Name = "kind" };
        Kind.AccessibilityName = strings.Get("chat.kind");
        Kind.AddItem(strings.Get("chat.kind.whisper"), (int)ChatKind.Whisper);
        Kind.AddItem(strings.Get("chat.kind.say"), (int)ChatKind.Say);
        Kind.AddItem(strings.Get("chat.kind.shout"), (int)ChatKind.Shout);
        Kind.Select(Kind.GetItemIndex((int)ChatKind.Say));
        Kind.ItemSelected += i => { if (_hub is not null) _hub.Nearby.Chosen = (ChatKind)Kind.GetItemId((int)i); };
        row.AddChild(Kind);
        SendButton = new Button { Name = "send", Text = strings.Get("chat.send"), ThemeTypeVariation = UiTheme.PrimaryButton };
        SendButton.AccessibilityName = strings.Get("chat.send");
        SendButton.Pressed += () => Submit(null);
        row.AddChild(SendButton);
        Bind(null);
    }

    /// <summary>Speaks into a session's nearby chat (null: no session; the bar is off).</summary>
    public void Bind(CommsHub? hub)
    {
        _hub = hub;
        if (hub is not null)
            hub.Nearby.Chosen = (ChatKind)Kind.GetSelectedId();
        Input.Editable = hub is not null;
        SendButton.Disabled = hub is null;
    }

    /// <summary>Puts the keyboard in the chat line (llfloaterimnearbychat.cpp startChat): <paramref name="line"/> replaces the text when given,
    /// and the caret goes to the end.</summary>
    public void Start(string? line)
    {
        if (_hub is null)
            return;
        Input.GrabFocus();
        if (line is not null)
            Input.Text = line;
        Input.CaretColumn = Input.Text.Length;
    }

    public void Submit(ChatKind? keyKind)
    {
        if (_hub is null)
            return;
        if (_hub.Nearby.Send(Input.Text, keyKind))
            Input.Clear();
    }
}

/// <summary>
/// The last few nearby chat lines over the bottom-left of the view, with a thin dark outline so they read on any scene. They
/// let the pointer through. The lines come from the same nearby chat as the chat window and the chat bar.
/// </summary>
public sealed partial class NearbyLines : RichTextLabel
{
    /// <summary>How many lines show.</summary>
    public const int ShownLines = 6;

    private readonly StringTable _strings;
    private readonly SettingsService? _settings;
    private CommsHub? _hub;
    private bool _dirty = true;
    private (int Count, DateTimeOffset First, DateTimeOffset Last, int Fading) _shown;
    private bool _listening;

    /// <summary>How many lines the overlay shows now.</summary>
    public int ShownCount => _shown.Count;

    public NearbyLines(StringTable strings, SettingsService? settings = null)
    {
        _strings = strings;
        _settings = settings;
        ApplySize();
        Name = "nearby_lines";
        ThemeTypeVariation = UiTheme.OverlayText;
        BbcodeEnabled = true;
        ScrollActive = false;
        FitContent = true;
        MouseFilter = MouseFilterEnum.Ignore;
        AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AccessibilityName = strings.Get("chat.history");
        AnchorTop = 1;
        AnchorBottom = 1;
        OffsetLeft = 12;
        OffsetBottom = -12;
        OffsetRight = 520;
        GrowVertical = GrowDirection.Begin;
    }

    public void Bind(CommsHub? hub)
    {
        if (_settings is not null && !_listening)
        {
            _settings.Changed += SettingChanged;
            _listening = true;
        }
        if (_hub is not null)
            _hub.Nearby.Changed -= MarkDirty;
        _hub = hub;
        if (hub is not null)
            hub.Nearby.Changed += MarkDirty;
        _dirty = true;
    }

    private void MarkDirty() => _dirty = true;

    public override void _EnterTree() => UiTheme.Changed += MarkDirty;

    public override void _ExitTree() => UiTheme.Changed -= MarkDirty;

    public override void _Process(double delta)
    {
        // the lines to show: the newest few, and with a time set in Preferences only those newer than it (so the text is looked at every
        // frame, but only made again when the shown lines or the look changed)
        var lines = _hub?.Nearby.Lines;
        var visible = lines is null ? [] : NearbyOverlayRules.Shown(lines, DateTimeOffset.Now, _settings?.Get(Settings.ChatOverlaySeconds) ?? Settings.ChatOverlaySeconds.Default,
            _settings?.Get(Settings.ChatOverlayFadeSeconds) ?? Settings.ChatOverlayFadeSeconds.Default, ShownLines);
        // a line's opacity changes at one moment (when its lifetime ends), so the text is made again then as well as when the lines change
        int fading = visible.Count(v => v.Opacity < 1f);
        var now = (visible.Count, visible.Count > 0 ? visible[0].Line.At : default, visible.Count > 0 ? visible[^1].Line.At : default, fading);
        if (!_dirty && now == _shown)
            return;
        _dirty = false;
        _shown = now;
        Text = string.Join("\n", visible.Select(l => ChatText.Format(l.Line, UiTheme.Spec, _strings, false, l.Opacity, HostLinks.Router)));
    }

    private void SettingChanged(string key)
    {
        _dirty = true;
        if (key == Settings.ChatFontSize.Key)
            ApplySize();
    }

    /// <summary>The lines are drawn at the chat text size of Preferences (the same setting as the chat window's text).</summary>
    private void ApplySize()
    {
        if (_settings is null)
            return;
        int size = _settings.Get(Settings.ChatFontSize);
        foreach (var font in new[] { "normal_font_size", "bold_font_size", "italics_font_size" })
            AddThemeFontSizeOverride(font, size);
    }
}
