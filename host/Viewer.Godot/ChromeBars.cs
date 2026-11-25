// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Godot;
using Viewer.Shell;
using Viewer.Core.Session;
using System.Collections.Generic;
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
/// The chat bar above the toolbar: a box to type in, Say / Shout / Whisper, and send. It speaks into the same nearby chat as
/// the nearby chat window (Enter says; Ctrl+Enter shouts; Shift+Enter whispers). The box grows with its wrapped text up to five
/// lines, upward over the view; Ctrl+Up and Ctrl+Down bring back earlier lines; a line that starts with "/" offers the active
/// gestures' triggers it begins (Tab or a click completes). Push-to-talk is not here because the viewer has none yet.
/// </summary>
public sealed partial class ChatBar : PanelContainer
{
    private CommsHub? _hub;
    private readonly StringTable _strings;
    private readonly ChatLineHistory _history = new();
    private readonly PanelContainer _completion;
    private readonly VBoxContainer _offers;
    private IReadOnlyList<string> _offered = [];
    private string _text = "";   // the text as last accepted
    private string? _set;        // text set in code, which is not typing
    private int _lines = 1;

    public TextEdit Input { get; }
    public OptionButton Kind { get; }
    public Button SendButton { get; }

    /// <summary>The active gestures' triggers, for completion.</summary>
    public Func<IReadOnlyList<string>>? Triggers { get; set; }

    /// <summary>The least height the layout gives the bar's row.</summary>
    public float RowFloor { get; set; }

    /// <summary>How much taller than its one-line height the bar is now; it grows upward by this much. At one line the row is held up
    /// by the layout's least height or the buttons beside the box, so it grows by less than the lines added: counting the lines alone
    /// would make the grown box taller than its lines, and a box that is not a whole number of lines tall scrolls to part of a line.</summary>
    public float ExtraHeight
    {
        get
        {
            float row = Math.Max(Kind.GetCombinedMinimumSize().Y, SendButton.GetCombinedMinimumSize().Y);
            float one = Math.Max(row, Input.GetLineHeight() + Input.GetThemeStylebox("normal").GetMinimumSize().Y);
            one = Math.Max(one, RowFloor - GetThemeStylebox("panel").GetMinimumSize().Y);
            return Math.Max(0, Input.CustomMinimumSize.Y - one);
        }
    }

    /// <summary>The number of lines the box shows.</summary>
    public int LinesShown => _lines;

    /// <summary>The bar's height changed.</summary>
    public event Action? HeightChanged;

    /// <summary>The box took no more text (it is full), or there is no earlier or later line to recall.</summary>
    public event Action? Rejected;

    public bool CompletionOpen => _completion.Visible;

    /// <summary>The triggers on offer, the one Tab completes first.</summary>
    public IReadOnlyList<string> Offered => CompletionOpen ? _offered : [];

    public ChatBar(StringTable strings)
    {
        _strings = strings;
        Name = "chat_bar";
        ThemeTypeVariation = UiTheme.BarPanel;
        MouseFilter = MouseFilterEnum.Stop;
        AccessibilityName = strings.Get("chatbar.title");
        var row = new HBoxContainer { Name = "chat_row" };
        AddChild(row);
        Input = new TextEdit
        {
            Name = "input", PlaceholderText = strings.Get("chat.input.hint"), TooltipText = strings.Get("chat.input.tip"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill, ThemeTypeVariation = UiTheme.ChatInput, WrapMode = TextEdit.LineWrappingMode.Boundary,
            CaretMultiple = false,   // one caret, as a one-line field has
        };
        Input.AccessibilityName = strings.Get("chat.input");
        Input.TextChanged += OnTextChanged;
        Input.FocusExited += () => _hub?.Nearby.InputLostFocus();
        Input.FocusExited += () => Callable.From(CloseCompletionWhenLeft).CallDeferred();
        Input.GuiInput += OnKey;   // before the box's own key handling
        Input.Resized += () => Callable.From(Refit).CallDeferred();
        Input.ThemeChanged += () => Callable.From(Refit).CallDeferred();
        row.AddChild(Input);
        // the engine draws the line just scrolled out into the box's top padding: the box's face is drawn over that strip again
        var cover = new Control { Name = "top_cover", MouseFilter = MouseFilterEnum.Ignore };
        cover.SetAnchorsPreset(LayoutPreset.TopWide);
        cover.Draw += () =>
        {
            if (Input.ScrollVertical > 0 && Input.GetThemeStylebox("normal") is StyleBoxFlat face)
                cover.DrawRect(new Rect2(face.BorderWidthLeft, face.BorderWidthTop, cover.Size.X - face.BorderWidthLeft - face.BorderWidthRight, face.ContentMarginTop - face.BorderWidthTop), face.BgColor);
        };
        Input.Draw += cover.QueueRedraw;   // scrolling redraws the box
        Input.AddChild(cover);
        Kind = new OptionButton { Name = "kind", SizeFlagsVertical = SizeFlags.ShrinkEnd };
        Kind.AccessibilityName = strings.Get("chat.kind");
        Kind.AddItem(strings.Get("chat.kind.whisper"), (int)ChatKind.Whisper);
        Kind.AddItem(strings.Get("chat.kind.say"), (int)ChatKind.Say);
        Kind.AddItem(strings.Get("chat.kind.shout"), (int)ChatKind.Shout);
        Kind.Select(Kind.GetItemIndex((int)ChatKind.Say));
        Kind.ItemSelected += i => { if (_hub is not null) _hub.Nearby.Chosen = (ChatKind)Kind.GetItemId((int)i); };
        row.AddChild(Kind);
        SendButton = new Button { Name = "send", Text = strings.Get("chat.send"), ThemeTypeVariation = UiTheme.ChatSend, SizeFlagsVertical = SizeFlags.ShrinkEnd };
        SendButton.AccessibilityName = strings.Get("chat.send");
        SendButton.Pressed += () => Submit(null);
        row.AddChild(SendButton);
        // the offers float above the box: placed in the window's own coordinates, so the bar's layout leaves them alone
        _completion = new PanelContainer { Name = "gesture_completion", TopLevel = true, Visible = false, ThemeTypeVariation = UiTheme.ChatOffers, MouseFilter = MouseFilterEnum.Stop };
        _offers = new VBoxContainer { Name = "offers" };
        _completion.AddChild(_offers);
        AddChild(_completion);
        Bind(null);
    }

    /// <summary>Speaks into a session's nearby chat (null: no session; the bar is off).</summary>
    public void Bind(CommsHub? hub)
    {
        if (!ReferenceEquals(hub, _hub))
            _history.Clear();   // another session's lines are not offered back
        _hub = hub;
        if (hub is not null)
            hub.Nearby.Chosen = (ChatKind)Kind.GetSelectedId();
        else
            CloseCompletion();
        Input.Editable = hub is not null;
        SendButton.Disabled = hub is null;
        SendButton.Modulate = ShellTheme.Strength(hub is not null);
    }

    /// <summary>Puts the keyboard in the chat line (llfloaterimnearbychat.cpp startChat): <paramref name="line"/> replaces the text when given,
    /// and the caret goes to the end.</summary>
    public void Start(string? line)
    {
        if (_hub is null)
            return;
        Input.GrabFocus();
        if (line is not null)
            SetLine(line);
        else
            CaretToEnd();
    }

    public void Submit(ChatKind? keyKind)
    {
        if (_hub is null)
            return;
        string line = Input.Text;
        CloseCompletion();
        if (_hub.Nearby.Send(line, keyKind))
        {
            _history.Remember(line);
            SetLine("");
        }
    }

    /// <summary>Closes the gesture offers, leaving the text.</summary>
    public void CloseCompletion()
    {
        _offered = [];
        _completion.Visible = false;
    }

    /// <summary>Text put in by code: not typing, so nothing is told of it and nothing is offered.</summary>
    private void SetLine(string line)
    {
        PutText(line);
        CloseCompletion();
    }

    private void PutText(string line)
    {
        _text = line;
        _set = line;
        Input.Text = line;
        Input.ClearUndoHistory();   // text put in by code is not undone: Ctrl+Z after a send must not bring the sent line back
        CaretToEnd();
        Refit();
    }

    private void CaretToEnd() => Input.SetCaretColumn(Input.GetLine(0).EnumerateRunes().Count());

    private void OnTextChanged()
    {
        string now = Input.Text;
        // the engine says the text changed after text set in code too, a frame later: that is not typing
        if (_set is { } set)
        {
            _set = null;
            if (set == now)
                return;
        }
        var (kept, caret) = ChatBoxRules.Clean(_text, now, CaretInText());
        if (kept != now)
        {
            if (kept.EnumerateRunes().Count() == ChatBoxRules.MaxCharacters && now.EnumerateRunes().Count(r => !System.Text.Rune.IsControl(r)) > ChatBoxRules.MaxCharacters)
                Rejected?.Invoke();
            Input.Text = kept;
            Input.ClearUndoHistory();   // else undo would only bring the left-out text back to be left out again
            Input.SetCaretColumn(caret);
            _set = kept;
        }
        bool same = kept == _text;
        _text = kept;
        Callable.From(Refit).CallDeferred();
        if (same)
            return;   // nothing was taken in: that is not typing
        _hub?.Nearby.InputChanged(kept);
        Offer(kept);
    }

    /// <summary>The caret's place in the whole text, in characters (line breaks counted, before they are left out).</summary>
    private int CaretInText()
    {
        int at = Input.GetCaretColumn();
        for (int i = 0; i < Input.GetCaretLine(); i++)
            at += Input.GetLine(i).EnumerateRunes().Count() + 1;
        return at;
    }

    private void Refit()
    {
        int wrapped = 0;
        for (int i = 0; i < Input.GetLineCount(); i++)
            wrapped += 1 + Input.GetLineWrapCount(i);
        int lines = ChatBoxRules.VisibleLines(wrapped);
        float margins = Input.GetThemeStylebox("normal").GetMinimumSize().Y;
        var height = new Vector2(0, lines * Input.GetLineHeight() + margins);
        bool changed = lines != _lines;
        if (Input.CustomMinimumSize != height)
            Input.CustomMinimumSize = height;
        _lines = lines;
        if (wrapped <= ChatBoxRules.MaxVisibleLines)
            Input.ScrollVertical = 0;
        else
            Input.AdjustViewportToCaret();
        if (changed)
            HeightChanged?.Invoke();
        if (CompletionOpen)
            Callable.From(PlaceCompletion).CallDeferred();
    }

    private void Offer(string text)
    {
        var matches = Input.HasFocus() && Triggers is { } triggers ? GestureCompletion.Matches(text, triggers()) : [];
        if (matches.Count == 0)
        {
            CloseCompletion();
            return;
        }
        _offered = matches;
        foreach (var old in _offers.GetChildren())
        {
            _offers.RemoveChild(old);
            old.QueueFree();
        }
        foreach (var trigger in matches)
        {
            var button = new Button { Text = trigger, Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None, ThemeTypeVariation = _offers.GetChildCount() == 0 ? UiTheme.PrimaryButton : UiTheme.FlatButton };
            button.AccessibilityName = trigger;
            button.Pressed += () => Accept(trigger);
            _offers.AddChild(button);
        }
        var hint = new Label { Name = "hint", Text = _strings.Get("chat.gesture.tip"), ThemeTypeVariation = UiTheme.CaptionLabel };
        hint.AccessibilityName = hint.Text;
        _offers.AddChild(hint);
        _completion.Visible = true;
        PlaceCompletion();
        Callable.From(PlaceCompletion).CallDeferred();
    }

    private void PlaceCompletion()
    {
        if (!CompletionOpen)
            return;
        var size = _completion.GetCombinedMinimumSize();
        _completion.Size = size;
        var box = Input.GetGlobalRect();
        _completion.GlobalPosition = new Vector2(box.Position.X + (int)PanelPadding.Inner, box.Position.Y - size.Y - 2);
    }

    private void Accept(string trigger)
    {
        string line = GestureCompletion.Complete(Input.Text, trigger);
        CloseCompletion();
        PutText(line);
        Input.GrabFocus();
    }

    private void CloseCompletionWhenLeft()
    {
        if (!Input.HasFocus())
            CloseCompletion();
    }

    private void OnKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k)
            return;
        switch (k.Keycode)
        {
            // every Enter is taken, held ones too, so the box never starts a second line; only the first press sends
            case Key.Enter or Key.KpEnter:
                if (!k.Echo)
                    Submit(k.CtrlPressed ? ChatKind.Shout : k.ShiftPressed ? ChatKind.Whisper : null);
                break;
            // Tab completes an offer; otherwise it moves to the next control, as in a one-line field
            case Key.Tab when !k.CtrlPressed:
                if (k.Echo)
                    break;   // a Tab held a moment too long must not complete and then leave the box
                if (CompletionOpen && !k.ShiftPressed)
                    Accept(_offered[0]);
                else
                    (k.ShiftPressed ? Input.FindPrevValidFocus() : Input.FindNextValidFocus())?.GrabFocus();
                break;
            case Key.Up or Key.Down when k.CtrlPressed && !k.AltPressed && !k.ShiftPressed:
                if (!Input.Editable)
                    break;
                if ((k.Keycode == Key.Up ? _history.Previous(Input.Text) : _history.Next(Input.Text)) is { } line)
                    SetLine(line);
                else
                    Rejected?.Invoke();
                break;
            default:
                return;
        }
        Input.AcceptEvent();
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

    /// <summary>The chat bar grows upward over the view: the newest lines stay above it.</summary>
    public void KeepAbove(ChatBar chat)
    {
        float top = OffsetTop, bottom = OffsetBottom;
        chat.HeightChanged += () =>
        {
            OffsetTop = top - chat.ExtraHeight;
            OffsetBottom = bottom - chat.ExtraHeight;
        };
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
