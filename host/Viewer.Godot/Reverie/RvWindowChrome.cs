// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// One window style for every window: a title bar 42 high with the title at the left and Minimise and Close at the right (in the same
/// place on every window), the content below. Dragging the title bar moves the window; dragging an edge or a corner resizes it, never
/// below its minimum. It opens centred at its default size the first time; after a move or a resize its place and size are kept
/// (through <paramref name="load"/> and <paramref name="save"/>) and a kept place that would be off-screen is pulled back
/// (<see cref="WindowChromePlace"/>).
/// </summary>
public partial class RvWindowChrome : Control
{
    private const float Edge = 6;

    private readonly PanelContainer _panel;
    private readonly Func<string?> _load;
    private readonly Action<string> _save;
    private readonly Vector2 _default, _minimum;
    private Vector2 _dragFrom;
    private Rect2 _startRect;
    private int _grab;   // 0 none; 1 move; else a bit per edge: 2 left, 4 right, 8 top, 16 bottom
    private bool _placed;

    public Label Title { get; }
    public RvIconButton Minimise { get; }
    public RvIconButton Close { get; }
    public MarginContainer Content { get; }
    public bool Minimised { get; private set; }

    public event Action? CloseRequested;

    public RvWindowChrome(StringTable strings, string title, Vector2 defaultSize, Vector2 minimumSize, Func<string?> load, Action<string> save)
    {
        _default = defaultSize;
        _minimum = minimumSize;
        _load = load;
        _save = save;
        Name = "window_chrome";
        AccessibilityName = title;
        MouseFilter = MouseFilterEnum.Stop;
        _panel = new PanelContainer { Name = "window", ThemeTypeVariation = ReverieStyle.Window, MouseFilter = MouseFilterEnum.Pass };
        _panel.SetAnchorsPreset(LayoutPreset.FullRect);
        _panel.OffsetLeft = _panel.OffsetTop = Edge / 2;
        _panel.OffsetRight = _panel.OffsetBottom = -Edge / 2;
        AddChild(_panel);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 0);
        _panel.AddChild(column);
        var bar = new PanelContainer { Name = "title_bar", ThemeTypeVariation = ReverieStyle.TitleBar, CustomMinimumSize = new Vector2(0, ReverieTheme.TitleBarHeight), MouseFilter = MouseFilterEnum.Stop };
        bar.GuiInput += TitleInput;
        column.AddChild(bar);
        var row = new HBoxContainer();
        bar.AddChild(row);
        Title = new Label { Text = title, ThemeTypeVariation = ReverieStyle.WindowTitle, SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        row.AddChild(Title);
        Minimise = new RvIconButton(LucideIcons.Minimise, strings.Get("floater.minimize"), ReverieTheme.TitleButton) { Name = "minimise" };
        Minimise.Pressed += () => SetMinimised(!Minimised);
        Close = new RvIconButton(LucideIcons.Close, strings.Get("floater.close"), ReverieTheme.TitleButton) { Name = "close" };
        Close.Pressed += () => CloseRequested?.Invoke();
        row.AddChild(Minimise);
        row.AddChild(Close);
        Content = new MarginContainer { Name = "content", SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            Content.AddThemeConstantOverride(side, 12);
        column.AddChild(Content);
    }

    /// <summary>The view the window lives in (its parent's size).</summary>
    private Vector2 View => GetParentAreaSize();

    public override void _Ready()
    {
        Place();
        GetViewport().SizeChanged += Clamp;
    }

    /// <summary>Opens at the kept place (pulled on-screen) or centred at the default size.</summary>
    public void Place()
    {
        var view = View;
        var r = WindowChromePlace.Open(WindowChromePlace.Read(_load()), _default.X, _default.Y, _minimum.X, _minimum.Y, view.X, view.Y);
        Apply(r);
        _placed = true;
    }

    private void Clamp()
    {
        if (!_placed || !IsInsideTree())
            return;
        var view = View;
        Apply(WindowChromePlace.PullOnScreen(new ChromeRect(Position.X, Position.Y, Size.X, Size.Y), _minimum.X, _minimum.Y, view.X, view.Y));
    }

    private void Apply(ChromeRect r)
    {
        Position = new Vector2(r.X, r.Y);
        Size = new Vector2(r.W, Minimised ? ReverieTheme.TitleBarHeight + Edge : r.H);
    }

    public void SetMinimised(bool on)
    {
        Minimised = on;
        Content.Visible = !on;
        Size = new Vector2(Size.X, on ? ReverieTheme.TitleBarHeight + Edge : Math.Max(_startRect.Size.Y, _minimum.Y));
    }

    private void Remember() => _save(WindowChromePlace.Write(new ChromeRect(Position.X, Position.Y, Size.X, Size.Y)));

    private void TitleInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
            Begin(b.Pressed ? 1 : 0, b.GlobalPosition);
        else if (e is InputEventMouseMotion m)
            Drag(m);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
            Begin(b.Pressed ? EdgesAt(b.Position) : 0, b.GlobalPosition);
        else if (e is InputEventMouseMotion m)
        {
            if (_grab != 0)
                Drag(m);
            else
                MouseDefaultCursorShape = Cursor(EdgesAt(m.Position));
        }
    }

    private int EdgesAt(Vector2 p)
    {
        int bits = 0;
        if (p.X < Edge) bits |= 2;
        if (p.X > Size.X - Edge) bits |= 4;
        if (p.Y < Edge) bits |= 8;
        if (p.Y > Size.Y - Edge) bits |= 16;
        return bits;
    }

    private static CursorShape Cursor(int bits) => bits switch
    {
        2 or 4 => CursorShape.Hsize,
        8 or 16 => CursorShape.Vsize,
        10 or 20 => CursorShape.Fdiagsize,
        12 or 18 => CursorShape.Bdiagsize,
        _ => CursorShape.Arrow,
    };

    private void Begin(int grab, Vector2 at)
    {
        if (grab == 0 && _grab != 0)
        {
            _grab = 0;
            Remember();   // a move or a resize is kept when the button comes up
            return;
        }
        _grab = grab;
        _dragFrom = at;
        _startRect = new Rect2(Position, Size);
    }

    /// <summary>A step of a move or a resize; a step without the button held ends it.</summary>
    private void Drag(InputEventMouseMotion m)
    {
        if (_grab == 0)
            return;
        if ((m.ButtonMask & MouseButtonMask.Left) == 0)
        {
            Begin(0, m.GlobalPosition);
            return;
        }
        var d = m.GlobalPosition - _dragFrom;
        var r = _startRect;
        if (_grab == 1)
            r.Position += d;
        else
        {
            if ((_grab & 2) != 0) { float w = Math.Max(_minimum.X, r.Size.X - d.X); r.Position = new Vector2(r.End.X - w, r.Position.Y); r.Size = new Vector2(w, r.Size.Y); }
            if ((_grab & 4) != 0) r.Size = new Vector2(Math.Max(_minimum.X, r.Size.X + d.X), r.Size.Y);
            if ((_grab & 8) != 0) { float h = Math.Max(_minimum.Y, r.Size.Y - d.Y); r.Position = new Vector2(r.Position.X, r.End.Y - h); r.Size = new Vector2(r.Size.X, h); }
            if ((_grab & 16) != 0) r.Size = new Vector2(r.Size.X, Math.Max(_minimum.Y, r.Size.Y + d.Y));
        }
        var view = View;
        Apply(WindowChromePlace.PullOnScreen(new ChromeRect(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y), _minimum.X, _minimum.Y, view.X, view.Y));
    }
}
