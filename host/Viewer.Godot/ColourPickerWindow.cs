// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>A small button that shows one colour; the picker's swatches and the parts of a <see cref="ColourButton"/> are made of it.</summary>
public partial class ColourChip : Button
{
    private Color _colour = ColourPickerWindow.Data(1, 1, 1);

    public Color Colour
    {
        get => _colour;
        set
        {
            if (_colour == value) return;
            _colour = value;
            QueueRedraw();
        }
    }

    public ColourChip()
    {
        CustomMinimumSize = new Vector2(22, 22);
        FocusMode = FocusModeEnum.All;
    }

    public override void _Draw()
    {
        var inner = new Rect2(Vector2.One * 3, Size - Vector2.One * 6);
        if (_colour.A < 1f)
        {
            // a see-through colour over a light and dark check, so its transparency shows
            float cell = MathF.Max(4, inner.Size.Y / 2);
            for (float y = 0; y < inner.Size.Y; y += cell)
                for (float x = 0; x < inner.Size.X; x += cell)
                {
                    bool dark = ((int)(x / cell) + (int)(y / cell)) % 2 == 1;
                    var r = new Rect2(inner.Position + new Vector2(x, y), new Vector2(MathF.Min(cell, inner.Size.X - x), MathF.Min(cell, inner.Size.Y - y)));
                    DrawRect(r, dark ? ColourPickerWindow.Data(0.4f, 0.4f, 0.4f) : ColourPickerWindow.Data(0.8f, 0.8f, 0.8f));
                }
        }
        DrawRect(inner, Disabled ? _colour.Darkened(0.55f) : _colour);
    }
}

/// <summary>
/// A colour a window lets the person choose: it shows the colour, and a press opens the shared colour picker (<see cref="ColourPickerWindow"/>)
/// for it. Each change made in the picker reaches <see cref="ColorChanged"/> as it is made, and Cancel raises it again with the old colour.
/// A caller that must put back more than one value on Cancel (the Build window's faces, each with its own colour) gives its own
/// <see cref="Request"/>. Every colour-choosing control in the viewer is one of these.
/// </summary>
public partial class ColourButton : ColourChip
{
    /// <summary>The colour changed in the picker (live, or on OK), or was put back by Cancel.</summary>
    public event Action<Color>? ColorChanged;

    /// <summary>Whether the picker offers Transparency for this colour.</summary>
    public bool EditAlpha { get; set; }

    /// <summary>The picker window's title, from the string table.</summary>
    public string TitleKey { get; set; } = "colour.title";

    /// <summary>A caller's own request (what to apply and how to put back); null: the swatch's colour itself.</summary>
    public Func<ColourRequest?>? Request { get; set; }

    /// <summary>The colour shown (the name the engine's colour button used, so callers read it the same way).</summary>
    public Color Color
    {
        get => Colour;
        set => Colour = value;
    }

    public ColourButton()
    {
        CustomMinimumSize = new Vector2(64, 24);
        Pressed += Open;
    }

    /// <summary>Opens the shared picker for this colour.</summary>
    public void Open()
    {
        if (Disabled || ColourPickerWindow.For(this) is not { } window)
            return;
        var request = Request?.Invoke() ?? new ColourRequest(TitleKey, ColourPickerWindow.ToUi(Color), EditAlpha, c =>
        {
            Color = ColourPickerWindow.ToGodot(c);
            ColorChanged?.Invoke(Color);
        });
        window.Open(request, this);
    }
}

/// <summary>
/// The one colour picker window of the viewer, after the official viewer's colour picker floater (floater_color_picker.xml and
/// llfloatercolorpicker.cpp, read as behaviour only): a square of saturation and value with a hue strip, the New and Was colours, Pick
/// (the official pipette, here taking the colour of any point on the screen), R, G, B (0 to 255), H (0 to 360), S and L (0 to 100) as the
/// official fields, and Hex; Transparency where the caller allows it; 16 standard colours (the official first palette row), My colours
/// and the last five colours; "Apply as I choose" (the official Apply now, on by default); OK keeps, Cancel puts the old colour back. It is
/// an ordinary window, so it stays open while the person clicks elsewhere. What it decides is in <see cref="ColourSession"/> and
/// <see cref="ColourPalette"/> (tested without the engine). Main thread only.
/// </summary>
public partial class ColourPickerWindow : VBoxContainer
{
    public const string FloaterId = "colour_picker";

    /// <summary>The settings the colour lists and "Apply as I choose" are kept in (the viewer sets its own at start).</summary>
    public static SettingsService Settings { get; set; } = new(null);

    private static readonly Dictionary<ShellRoot, ColourPickerWindow> Windows = new();

    private readonly ShellRoot _shell;
    private readonly StringTable _s;
    private readonly ColourSquare _square;
    private readonly HueStrip _hue;
    private readonly ColourChip _new, _was;
    private readonly SpinBox _r, _g, _b, _hField, _sField, _lField;
    private readonly LineEdit _hex;
    private readonly HBoxContainer _alphaRow;
    private readonly HSlider _transparency;
    private readonly HFlowContainer _mine;
    private readonly HBoxContainer _recent;
    private readonly Label _mineEmpty, _pickHint;
    private readonly CheckBox _live;
    private readonly Button _pick;
    private ColourSession? _session;
    private Node? _source;
    private float _h, _sat, _val, _alpha = 1f;
    private bool _filling;
    private PickOverlay? _overlay;
    private ViewportTexture? _screen;

    /// <summary>The window of the shell <paramref name="inside"/> belongs to, made the first time it is asked for; null outside a shell.</summary>
    public static ColourPickerWindow? For(Node inside)
    {
        for (Node? n = inside; n is not null; n = n.GetParent())
            if (n is ShellRoot shell)
                return Install(shell);
        return null;
    }

    /// <summary>Adds the window to a shell (once).</summary>
    public static ColourPickerWindow Install(ShellRoot shell)
    {
        if (Windows.TryGetValue(shell, out var w) && IsInstanceValid(w))
            return w;
        w = new ColourPickerWindow(shell);
        Windows[shell] = w;
        shell.TreeExiting += () => Windows.Remove(shell);
        return w;
    }

    public static UiColour ToUi(Color c) => new(c.R, c.G, c.B, c.A);

    // the picker draws colours as data (the colour chosen, the square and strip it is chosen on, the marks on them, the check behind a
    // see-through colour), not theme colours: black and white marks show on any colour
    internal static Color Data(float r, float g, float b) => ShellTheme.DataColour(r, g, b);
    private static UiColour Ui(float r, float g, float b, float a) => new(r, g, b, a);
    public static Color ToGodot(UiColour c) => ShellTheme.DataColour(c.R, c.G, c.B) with { A = c.A };   // the chosen colour itself, not a theme colour

    private ColourPickerWindow(ShellRoot shell)
    {
        _shell = shell;
        _s = shell.Shell.Strings;
        Name = "colour_picker";
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var top = new HBoxContainer { Name = "colour_top" };
        _square = new ColourSquare { Name = "colour_square", CustomMinimumSize = new Vector2(220, 180), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _square.AccessibilityName = _s.Get("colour.square");
        _square.Changed += (s, v) => { _sat = s; _val = v; FromHsv(); };
        top.AddChild(_square);
        var side = new VBoxContainer { Name = "colour_side" };
        side.AddChild(new Label { Text = _s.Get("colour.new") });
        _new = new ColourChip { Name = "colour_new", CustomMinimumSize = new Vector2(64, 36), MouseFilter = MouseFilterEnum.Ignore, FocusMode = FocusModeEnum.None };
        _new.AccessibilityName = _s.Get("colour.new");
        side.AddChild(_new);
        side.AddChild(new Label { Text = _s.Get("colour.was") });
        // a press on Was takes the old colour again
        _was = new ColourChip { Name = "colour_was", CustomMinimumSize = new Vector2(64, 36), TooltipText = _s.Get("colour.was") };
        _was.AccessibilityName = _s.Get("colour.was");
        _was.Pressed += () => { if (_session is { } ss) Set(ss.Was); };
        side.AddChild(_was);
        _pick = new Button { Name = "pick", Text = _s.Get("colour.pick"), TooltipText = _s.Get("colour.pick.tip"), ToggleMode = true,
            ThemeTypeVariation = ReverieVariations.Secondary };   // a lone toggle keeps its outline (a list row would read as plain text); the hint line below says when it is on
        _pick.AccessibilityName = _pick.Text;
        _pick.Toggled += on => { if (on) StartPick(); else StopPick(); };
        side.AddChild(_pick);
        top.AddChild(side);
        AddChild(top);
        _pickHint = new Label { Name = "pick_hint", Text = _s.Get("colour.pick.active"), Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_pickHint);

        _hue = new HueStrip { Name = "colour_hue", CustomMinimumSize = new Vector2(220, 18) };
        _hue.AccessibilityName = _s.Get("colour.hue");
        _hue.Changed += h => { _h = h; FromHsv(); };
        AddChild(_hue);

        var fields = new GridContainer { Name = "colour_fields", Columns = 6 };
        _r = Field(fields, "colour_r", "colour.r", 255);
        _g = Field(fields, "colour_g", "colour.g", 255);
        _b = Field(fields, "colour_b", "colour.b", 255);
        _hField = Field(fields, "colour_h", "colour.h", 360);
        _sField = Field(fields, "colour_s", "colour.s", 100);
        _lField = Field(fields, "colour_l", "colour.l", 100);
        foreach (var f in new[] { _r, _g, _b })
            f.ValueChanged += _ => { if (!_filling) Set(Ui((float)_r.Value / 255f, (float)_g.Value / 255f, (float)_b.Value / 255f, _alpha)); };
        foreach (var f in new[] { _hField, _sField, _lField })
            f.ValueChanged += _ => { if (!_filling) Set(ColourMath.FromHsl((float)_hField.Value / 360f, (float)_sField.Value / 100f, (float)_lField.Value / 100f, _alpha)); };
        AddChild(fields);

        var hexRow = new HBoxContainer { Name = "hex_row" };
        hexRow.AddChild(new Label { Text = _s.Get("colour.hex") });
        _hex = new LineEdit { Name = "hex", SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = 9 };
        _hex.AccessibilityName = _s.Get("colour.hex");
        _hex.TextSubmitted += ReadHex;
        _hex.FocusExited += () => ReadHex(_hex.Text);
        hexRow.AddChild(_hex);
        AddChild(hexRow);

        _alphaRow = new HBoxContainer { Name = "transparency_row" };
        _alphaRow.AddChild(new Label { Text = _s.Get("colour.transparency") });
        _transparency = new HSlider { Name = "transparency", MinValue = 0, MaxValue = 100, Step = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(120, 20) };
        _transparency.AccessibilityName = _s.Get("colour.transparency");
        _transparency.ValueChanged += v => { if (!_filling) { _alpha = 1f - (float)v / 100f; FromHsv(); } };
        _alphaRow.AddChild(_transparency);
        AddChild(_alphaRow);

        AddChild(new Label { Name = "standard_label", Text = _s.Get("colour.standard") });
        var standard = new GridContainer { Name = "standard", Columns = 8 };
        for (int i = 0; i < ColourPalette.Standard.Count; i++)
            standard.AddChild(Chip("standard_" + i, ColourPalette.Standard[i]));
        AddChild(standard);

        var mineHead = new HBoxContainer { Name = "mine_head" };
        mineHead.AddChild(new Label { Text = _s.Get("colour.mine"), SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var save = new Button { Name = "save_colour", Text = _s.Get("colour.mine.save"), TooltipText = _s.Get("colour.mine.save.tip") };
        save.AccessibilityName = _s.Get("colour.mine.save.tip");
        save.Pressed += () => { if (_session is { } ss) { Palette.Save(ss.New); FillLists(); } };
        mineHead.AddChild(save);
        AddChild(mineHead);
        _mine = new HFlowContainer { Name = "mine" };
        AddChild(_mine);
        _mineEmpty = new Label { Name = "mine_empty", Text = _s.Get("colour.mine.empty") };
        AddChild(_mineEmpty);
        AddChild(new Label { Name = "recent_label", Text = _s.Get("colour.recent") });
        _recent = new HBoxContainer { Name = "recent" };
        AddChild(_recent);

        _live = new CheckBox { Name = "apply_as_i_choose", Text = _s.Get("colour.apply_as_i_choose"), TooltipText = _s.Get("colour.apply_as_i_choose.tip") };
        _live.AccessibilityName = _live.Text;
        _live.Toggled += on =>
        {
            if (_filling) return;
            Settings.Set(ColourPalette.ApplyAsIChoose, on);
            if (_session is { } ss) ss.ApplyAsIChoose = on;
        };
        AddChild(_live);

        var buttons = new HBoxContainer { Name = "colour_buttons", Alignment = AlignmentMode.End };
        var ok = new Button { Name = "colour_ok", Text = _s.Get("colour.ok") };
        ok.AccessibilityName = ok.Text;
        ok.Pressed += () => End(keep: true);
        var cancel = new Button { Name = "colour_cancel", Text = _s.Get("colour.cancel") };
        cancel.AccessibilityName = cancel.Text;
        cancel.Pressed += () => End(keep: false);
        buttons.AddChild(ok);
        buttons.AddChild(cancel);
        // OK and Cancel are the window's footer, outside the scrolled content: in view at any window size
        shell.AddFloater(new FloaterSpec(FloaterId, "colour.title", new UiSize(380, 640), new UiSize(340, 520)), this, buttons);
        shell.Shell.Floaters.Closed += id =>
        {
            if (id == FloaterId)
            {
                StopPick();
                _session?.Closed();
                return;
            }
            // the picker belongs to the colour that opened it: when that colour's window closes, the picker closes too (its old colour goes back)
            Callable.From(CloseIfSourceGone).CallDeferred();
        };
    }

    private ColourPalette Palette => new(Settings);

    /// <summary>The session open now (null when the window is closed or ended).</summary>
    public ColourSession? Session => _session is { Ended: false } s ? s : null;

    /// <summary>The control the open session came from.</summary>
    public Node? Source => Session is null ? null : _source;

    /// <summary>True while the window is open.</summary>
    public bool IsOpen => _shell.Shell.Floaters.IsOpen(FloaterId);

    /// <summary>The square of saturation and value, for the live check's pointer.</summary>
    public Control Square => _square;

    /// <summary>Opens the picker for a request. A session still open for another colour ends as a close does (its old colour goes back).</summary>
    public void Open(ColourRequest request, Node? source = null)
    {
        if (_session is { Ended: false } open)
        {
            if (source is not null && source == _source)
            {
                _shell.Shell.Floaters.Focus(FloaterId);
                return;
            }
            open.Closed();
        }
        _source = source;
        // a colour that leaves the screen (its window rebuilt or freed) takes its picker with it, before it is gone
        if (source is not null)
            source.TreeExiting += () => { if (_source == source) CloseNow(); };
        _session = new ColourSession(request, Palette, Settings.Get(ColourPalette.ApplyAsIChoose));
        _shell.SetFloaterTitle(FloaterId, _s.Get(request.TitleKey));
        _alphaRow.Visible = request.AllowAlpha;
        _was.Colour = ToGodot(_session.Was);
        var (h, s, v) = ColourMath.ToHsv(_session.Was);
        (_h, _sat, _val, _alpha) = (h, s, v, _session.Was.A);
        Show(_session.Was);
        FillLists();
        _shell.Shell.Floaters.Open(FloaterId);
    }

    private void CloseNow()
    {
        if (Session is not null)
            _shell.Shell.Floaters.Close(FloaterId);
        if (Session is { } s)
            s.Closed();   // closed already: the session still ends, so nothing more reaches the target
        _source = null;
    }

    private void CloseIfSourceGone()
    {
        if (Session is not null && _source is Control c && (!IsInstanceValid(c) || !c.IsVisibleInTree()))
            CloseNow();
    }

    /// <summary>Closes the window if the open session came from <paramref name="source"/> or a control inside it (its old colour goes back).</summary>
    public void CloseIfFrom(Node source)
    {
        if (Source is { } s && (s == source || source.IsAncestorOf(s)))
            _shell.Shell.Floaters.Close(FloaterId);
    }

    /// <summary>Chooses a colour as the person would (a swatch, a field or Pick).</summary>
    public void Set(UiColour c)
    {
        var (h, s, v) = ColourMath.ToHsv(c);
        // a grey has no hue of its own: the strip keeps where it was
        if (s > 0.0001f && v > 0.0001f) _h = h;
        (_sat, _val) = (s, v);
        if (_session?.AllowAlpha == true) _alpha = c.A;
        Apply(ColourMath.FromHsv(_h, _sat, _val, _alpha));
    }

    private void FromHsv() => Apply(ColourMath.FromHsv(_h, _sat, _val, _alpha));

    private void Apply(UiColour c)
    {
        if (_session is not { Ended: false } session)
            return;
        session.Choose(c);
        Show(session.New);
    }

    private void Show(UiColour c)
    {
        _filling = true;
        try
        {
            _new.Colour = ToGodot(c);
            _square.Set(_h, _sat, _val);
            _hue.Hue = _h;
            _r.SetValueNoSignal(Math.Round(c.R * 255));
            _g.SetValueNoSignal(Math.Round(c.G * 255));
            _b.SetValueNoSignal(Math.Round(c.B * 255));
            var (hh, ss, ll) = ColourMath.ToHsl(c);
            _hField.SetValueNoSignal(Math.Round((ss > 0.0001f ? hh : _h) * 360));
            _sField.SetValueNoSignal(Math.Round(ss * 100));
            _lField.SetValueNoSignal(Math.Round(ll * 100));
            if (!_hex.HasFocus())
                _hex.Text = ColourMath.ToHex(c, _session?.AllowAlpha == true);
            _transparency.SetValueNoSignal(Math.Round((1 - c.A) * 100));
            _live.SetPressedNoSignal(_session?.ApplyAsIChoose ?? Settings.Get(ColourPalette.ApplyAsIChoose));
        }
        finally { _filling = false; }
    }

    private void ReadHex(string text)
    {
        if (_filling || _session is null) return;
        if (ColourMath.TryParseHex(text, out var c))
            Set(text.Trim().TrimStart('#').Length == 8 ? c : c with { A = _alpha });
        else
            Show(_session.New);   // not a colour: the field shows the colour again
    }

    private SpinBox Field(GridContainer grid, string name, string key, double max)
    {
        grid.AddChild(new Label { Text = _s.Get(key) });
        var s = new SpinBox { Name = name, MinValue = 0, MaxValue = max, Step = 1, CustomMinimumSize = new Vector2(64, 0), AccessibilityName = _s.Get(key) };
        grid.AddChild(s);
        return s;
    }

    private ColourChip Chip(string name, UiColour c)
    {
        var chip = new ColourChip { Name = name, Colour = ToGodot(c), TooltipText = ColourMath.ToHex(c, c.A < 1f) };
        chip.AccessibilityName = chip.TooltipText;
        chip.Pressed += () => Set(c);
        return chip;
    }

    private void FillLists()
    {
        foreach (var box in new Container[] { _mine, _recent })
            foreach (var child in box.GetChildren())
            {
                box.RemoveChild(child);
                child.QueueFree();
            }
        var mine = Palette.Saved;
        for (int i = 0; i < mine.Count; i++)
            _mine.AddChild(Chip("mine_" + i, mine[i]));
        _mineEmpty.Visible = mine.Count == 0;
        var recent = Palette.Recent;
        for (int i = 0; i < recent.Count; i++)
            _recent.AddChild(Chip("recent_" + i, recent[i]));
    }

    private void End(bool keep)
    {
        StopPick();
        if (keep) _session?.Ok(); else _session?.Cancel();
        _shell.Shell.Floaters.Close(FloaterId);
    }

    // ---------------- Pick: the colour of any point on the screen ----------------

    private void StartPick()
    {
        if (_overlay is not null) return;
        _overlay = new PickOverlay { Name = "colour_pick_overlay" };
        _overlay.Picked += at =>
        {
            _screen ??= GetViewport().GetTexture();   // read once and kept (ReadBackScanTests)
            var img = _screen.GetImage();
            var view = GetViewport().GetVisibleRect().Size;
            int x = Math.Clamp((int)(at.X * img.GetWidth() / view.X), 0, img.GetWidth() - 1);
            int y = Math.Clamp((int)(at.Y * img.GetHeight() / view.Y), 0, img.GetHeight() - 1);
            var c = img.GetPixel(x, y);
            StopPick();
            Set(Ui(c.R, c.G, c.B, _alpha));
        };
        _overlay.Stopped += StopPick;
        _shell.AddChild(_overlay);
        _shell.MoveChild(_overlay, -1);
        _pickHint.Visible = true;
    }

    private void StopPick()
    {
        if (_overlay is { } o)
        {
            _overlay = null;
            if (IsInstanceValid(o)) o.QueueFree();
        }
        _pickHint.Visible = false;
        _pick.SetPressedNoSignal(false);
    }

    /// <summary>A layer over the whole screen while Pick is on: a click takes the colour under it, Esc or a right click stops.</summary>
    private sealed partial class PickOverlay : Control
    {
        public event Action<Vector2>? Picked;
        public event Action? Stopped;

        public PickOverlay()
        {
            SetAnchorsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Stop;
            MouseDefaultCursorShape = CursorShape.Cross;
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton { Pressed: true } mb)
            {
                AcceptEvent();
                if (mb.ButtonIndex == MouseButton.Left) Picked?.Invoke(mb.GlobalPosition);
                else Stopped?.Invoke();
            }
        }

        public override void _UnhandledKeyInput(InputEvent e)
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                GetViewport().SetInputAsHandled();
                Stopped?.Invoke();
            }
        }
    }

    /// <summary>The square of saturation (across) and value (up) for one hue; a press or drag chooses.</summary>
    private sealed partial class ColourSquare : Control
    {
        private const int Cells = 48;
        private float _h = -1, _s, _v;
        private ImageTexture? _tex;
        public event Action<float, float>? Changed;

        public ColourSquare() => FocusMode = FocusModeEnum.All;

        public void Set(float h, float s, float v)
        {
            if (Math.Abs(h - _h) > 0.0001f || _tex is null)
            {
                _h = h;
                var img = Image.CreateEmpty(Cells, Cells, false, Image.Format.Rgb8);
                for (int y = 0; y < Cells; y++)
                    for (int x = 0; x < Cells; x++)
                    {
                        var c = ColourMath.FromHsv(h, x / (float)(Cells - 1), 1 - y / (float)(Cells - 1));
                        img.SetPixel(x, y, ToGodot(c));
                    }
                if (_tex is null) _tex = ImageTexture.CreateFromImage(img); else _tex.Update(img);
            }
            (_s, _v) = (s, v);
            QueueRedraw();
        }

        public override void _Draw()
        {
            if (_tex is not null) DrawTextureRect(_tex, new Rect2(Vector2.Zero, Size), false);
            var at = new Vector2(_s * Size.X, (1 - _v) * Size.Y);
            DrawArc(at, 6, 0, MathF.Tau, 20, Data(0, 0, 0), 2);
            DrawArc(at, 4, 0, MathF.Tau, 20, Data(1, 1, 1), 2);
        }

        public override void _GuiInput(InputEvent e)
        {
            bool press = e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true };
            bool drag = e is InputEventMouseMotion m && (m.ButtonMask & MouseButtonMask.Left) != 0;
            if (!press && !drag) return;
            var p = ((InputEventMouse)e).Position;
            Changed?.Invoke(Math.Clamp(p.X / Size.X, 0, 1), 1 - Math.Clamp(p.Y / Size.Y, 0, 1));
            AcceptEvent();
        }
    }

    /// <summary>The strip of hues, left to right; a press or drag chooses the hue.</summary>
    private sealed partial class HueStrip : Control
    {
        private static ImageTexture? _strip;
        private float _hue;
        public event Action<float>? Changed;

        public float Hue
        {
            get => _hue;
            set { _hue = value; QueueRedraw(); }
        }

        public HueStrip() => FocusMode = FocusModeEnum.All;

        public override void _Draw()
        {
            if (_strip is null)
            {
                var img = Image.CreateEmpty(180, 1, false, Image.Format.Rgb8);
                for (int x = 0; x < 180; x++)
                {
                    var c = ColourMath.FromHsv(x / 179f, 1, 1);
                    img.SetPixel(x, 0, ToGodot(c));
                }
                _strip = ImageTexture.CreateFromImage(img);
            }
            DrawTextureRect(_strip, new Rect2(Vector2.Zero, Size), false);
            float x0 = _hue * Size.X;
            DrawRect(new Rect2(x0 - 2, 0, 4, Size.Y), Data(1, 1, 1), false, 2);
        }

        public override void _GuiInput(InputEvent e)
        {
            bool press = e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true };
            bool drag = e is InputEventMouseMotion m && (m.ButtonMask & MouseButtonMask.Left) != 0;
            if (!press && !drag) return;
            Changed?.Invoke(Math.Clamp(((InputEventMouse)e).Position.X / Size.X, 0, 0.9999f));
            AcceptEvent();
        }
    }

    /// <summary>The window's state for the automation: the New and Was colours and the lists.</summary>
    public object Texts() => new
    {
        open = IsOpen, @new = Session is { } s ? ColourMath.ToHex(s.New, true) : null, was = Session is { } w ? ColourMath.ToHex(w.Was, true) : null,
        live = _live.ButtonPressed, alpha = _alphaRow.Visible, mine = _mine.GetChildCount(), recent = _recent.GetChildCount(),
        hex = _hex.Text, rgb = new[] { _r.Value, _g.Value, _b.Value }, hsl = new[] { _hField.Value, _sField.Value, _lField.Value },
    };
}
