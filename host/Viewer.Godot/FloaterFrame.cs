// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// One floating panel's frame, built in code from a <see cref="FloaterSpec"/>: a 24-unit title bar (dragged to move it) with a
/// minimize and a close button only, the panel's content below, and a grip in the bottom-right corner (dragged to resize) when
/// the spec allows it. Minimize folds the window up to its title bar; the same button opens it again. Where it is and whether
/// it is open belong to the <see cref="FloaterManager"/>; this only draws that and reports the pointer's drags back to it.
/// Behaviour taken from the usual floating-window conventions, not from any viewer's layout files.
/// </summary>
public partial class FloaterFrame : PanelContainer
{
    /// <summary>The title bar's height, in interface units.</summary>
    public const float TitleBarHeight = 24;

    /// <summary>The gap between the left edge of the title bar and the title.</summary>
    public const float TitleInset = 8;

    private readonly FloaterManager _floaters;
    private readonly string _id;
    private readonly Control? _grip;
    private readonly Control? _foot;
    private readonly FloaterDrag _drag = new();

    public Label Title { get; }
    public Button CloseButton { get; }
    public Button MinimizeButton { get; }
    public VBoxContainer Body { get; }
    public PanelContainer TitleBar { get; }

    /// <summary>True while the window is folded up to its title bar.</summary>
    public bool Minimized { get; private set; }

    public FloaterFrame(FloaterManager floaters, FloaterSpec spec, StringTable strings)
    {
        _floaters = floaters;
        _id = spec.Id;
        Name = "floater_" + spec.Id;
        string title = strings.Get(spec.TitleKey);
        AccessibilityName = title;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        // the manager keeps the window at least spec.MinSize unless the main view is smaller; the engine's own minimum must not
        // hold the window open past the main view, so it asks only for the title bar and the grip
        CustomMinimumSize = new Vector2(0, TitleBarHeight);
        ThemeTypeVariation = UiTheme.WindowPanel;

        Body = new VBoxContainer();
        Body.AddThemeConstantOverride("separation", 0);
        AddChild(Body);
        TitleBar = new PanelContainer { Name = "title_bar", MouseFilter = MouseFilterEnum.Stop, ThemeTypeVariation = UiTheme.TitleBarPanel, CustomMinimumSize = new Vector2(0, TitleBarHeight) };
        var bar = new HBoxContainer { Name = "title_row", MouseFilter = MouseFilterEnum.Ignore };
        TitleBar.AddChild(bar);
        Body.AddChild(TitleBar);
        Title = new Label { Text = title, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, ThemeTypeVariation = UiTheme.TitleLabel };
        Title.AccessibilityName = title;
        bar.AddChild(new Control { Name = "title_inset", CustomMinimumSize = new Vector2(TitleInset, 0), MouseFilter = MouseFilterEnum.Ignore });
        bar.AddChild(Title);
        MinimizeButton = TitleButton("minimize", UiIcons.Minimize, strings.Get("floater.minimize"));
        MinimizeButton.AccessibilityName = strings.Get("floater.minimize.named", title);
        MinimizeButton.Pressed += ToggleMinimized;
        bar.AddChild(MinimizeButton);
        CloseButton = TitleButton("close", UiIcons.Close, strings.Get("floater.close"));
        CloseButton.AccessibilityName = strings.Get("floater.close.named", title);
        CloseButton.Pressed += () => _floaters.Close(_id);
        bar.AddChild(CloseButton);
        TitleBar.GuiInput += e => OnBarInput(e);

        if (spec.Resizable)
        {
            _grip = new Control { Name = "grip", CustomMinimumSize = new Vector2(14, 14), MouseDefaultCursorShape = CursorShape.Fdiagsize, MouseFilter = MouseFilterEnum.Stop };
            _grip.AccessibilityName = strings.Get("floater.resize.named", title);
            _grip.GuiInput += e => OnGripInput(e);
            // the grip is the last row of the panel, at its right edge
            _foot = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = MouseFilterEnum.Ignore };
            _foot.AddChild(_grip);
            Body.AddChild(_foot);
            AddResizeHandles();
        }
        GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) _floaters.Focus(_id); };
    }

    private Button TitleButton(string name, string icon, string tip)
    {
        var b = new Button { Name = name, TooltipText = tip, ThemeTypeVariation = UiTheme.FlatButton, Flat = true, FocusMode = FocusModeEnum.Click };
        b.Icon = IconCache.Get(icon, 14);
        b.ExpandIcon = false;
        b.AddThemeConstantOverride("icon_max_width", 14);
        return b;
    }

    /// <summary>The content of the panel (below the title bar).</summary>
    public void SetContent(Control content)
    {
        content.SizeFlagsVertical = SizeFlags.ExpandFill;
        _pad = new MarginContainer { Name = "content_pad", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            _pad.AddThemeConstantOverride(side, (int)PanelPadding.Inner);
        _pad.AddChild(content);
        // the content scrolls when the window is smaller than it needs (a small screen or a large interface size), instead of
        // making the window bigger than the screen; at a size that suits the content nothing scrolls and nothing is drawn extra
        var scroll = new ScrollContainer { Name = "content_scroll", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_pad);
        Body.AddChild(scroll);
        if (_foot is not null)
            Body.MoveChild(_foot, -1);   // the grip stays the last row
    }

    /// <summary>A row under the content that does not scroll with it (a dialog's OK and Cancel): it stays in view however small the
    /// window is, as the official floaters keep their buttons on the floater itself, outside any scroll list (floater_color_picker.xml).</summary>
    public void SetFooter(Control footer)
    {
        _footer = new MarginContainer { Name = "footer_pad", SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            _footer.AddThemeConstantOverride(side, (int)PanelPadding.Inner);
        _footer.AddChild(footer);
        Body.AddChild(_footer);
        if (_foot is not null)
            Body.MoveChild(_foot, -1);   // the grip stays the last row
    }

    private MarginContainer? _footer;

    /// <summary>Folds the window up to its title bar, or opens it again.</summary>
    public void ToggleMinimized()
    {
        Minimized = !Minimized;
        foreach (var child in Body.GetChildren())
            if (child is Control c && c != TitleBar)
                c.Visible = !Minimized;
        CustomMinimumSize = Minimized ? new Vector2(CustomMinimumSize.X, TitleBarHeight) : _restoreMinimum;
        if (Minimized)
            Size = new Vector2(Size.X, TitleBarHeight);
        else
            _floaters.Focus(_id);   // the manager's own size is put back on the next sync
    }

    private Vector2 _restoreMinimum;
    private MarginContainer? _pad;

    /// <summary>The size the window needs to show its content without scrolling (title bar, content, grip, frame). Content inside a
    /// scroll area counts at its full size: a scroll area asks for almost nothing itself, so a window of settings in one would
    /// otherwise open needing to scroll.</summary>
    public Vector2 NeededSize()
    {
        var chrome = GetThemeStylebox("panel").GetMinimumSize();
        float h = TitleBar.GetCombinedMinimumSize().Y + (_foot?.GetCombinedMinimumSize().Y ?? 0) + (_footer?.GetCombinedMinimumSize().Y ?? 0);
        var pad = _pad?.GetCombinedMinimumSize() ?? Vector2.Zero;
        if (_pad is not null)
            pad += ScrolledAway(_pad);
        return new Vector2(pad.X + chrome.X, h + pad.Y + chrome.Y);
    }

    /// <summary>How much more room the content inside the scroll areas under a node needs than those areas ask for.</summary>
    private static Vector2 ScrolledAway(Node node)
    {
        var extra = Vector2.Zero;
        foreach (var child in node.GetChildren())
        {
            if (child is not Control { Visible: true } c)
                continue;
            Vector2 more = Vector2.Zero;
            if (c is ScrollContainer scroll)
            {
                foreach (var inner in scroll.GetChildren())
                    if (inner is Control { Visible: true } content)
                        more = (content.GetCombinedMinimumSize() - scroll.GetCombinedMinimumSize()).Max(Vector2.Zero);
            }
            else
                more = ScrolledAway(c);
            extra = new Vector2(Math.Max(extra.X, more.X), extra.Y + more.Y);
        }
        return extra;
    }

    public override void _EnterTree() => _restoreMinimum = _restoreMinimum == default ? CustomMinimumSize : _restoreMinimum;

    private void OnBarInput(InputEvent e) => OnDragInput(e, ResizeEdges.None);

    private void OnGripInput(InputEvent e) => OnDragInput(e, ResizeEdges.Right | ResizeEdges.Bottom);

    // the strips along the four edges and the four corners that resize the window, drawn by nothing and laid over the frame's border
    private const float EdgeThickness = 4, CornerSize = 8;
    private Control? _handles;

    private void AddResizeHandles()
    {
        _handles = new Control { Name = "resize_handles", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_handles);
        (string Name, ResizeEdges Edges, CursorShape Cursor)[] spec =
        [
            ("left", ResizeEdges.Left, CursorShape.Hsize), ("right", ResizeEdges.Right, CursorShape.Hsize),
            ("top", ResizeEdges.Top, CursorShape.Vsize), ("bottom", ResizeEdges.Bottom, CursorShape.Vsize),
            ("top_left", ResizeEdges.Left | ResizeEdges.Top, CursorShape.Fdiagsize), ("bottom_right", ResizeEdges.Right | ResizeEdges.Bottom, CursorShape.Fdiagsize),
            ("top_right", ResizeEdges.Right | ResizeEdges.Top, CursorShape.Bdiagsize), ("bottom_left", ResizeEdges.Left | ResizeEdges.Bottom, CursorShape.Bdiagsize),
        ];
        foreach (var (name, edges, cursor) in spec)
        {
            // the title-bar buttons keep their whole area: a strip never takes a click meant for close or minimize
            var h = new FloaterResizeHandle(edges, EdgeThickness, p => MinimizeButton.GetGlobalRect().HasPoint(p) || CloseButton.GetGlobalRect().HasPoint(p))
            {
                Name = "resize_" + name, MouseDefaultCursorShape = cursor,
            };
            var captured = edges;
            h.GuiInput += e => OnDragInput(e, captured);
            _handles.AddChild(h);
        }
        _handles.Resized += PlaceResizeHandles;
    }

    private void PlaceResizeHandles()
    {
        if (_handles is null) return;
        Vector2 s = _handles.Size;
        foreach (var child in _handles.GetChildren())
        {
            if (child is not Control h) continue;
            bool left = h.Name.ToString().Contains("left"), right = h.Name.ToString().Contains("right");
            bool top = h.Name.ToString().Contains("top"), bottom = h.Name.ToString().Contains("bottom");
            bool corner = (left || right) && (top || bottom);
            float cw = corner ? CornerSize : EdgeThickness;
            float x = left ? 0 : right ? s.X - cw : CornerSize;
            float y = top ? 0 : bottom ? s.Y - (corner ? CornerSize : EdgeThickness) : CornerSize;
            float w = corner ? CornerSize : (left || right) ? EdgeThickness : Math.Max(0, s.X - 2 * CornerSize);
            float hgt = corner ? CornerSize : (top || bottom) ? EdgeThickness : Math.Max(0, s.Y - 2 * CornerSize);
            h.Position = new Vector2(x, y);
            h.Size = new Vector2(w, hgt);
        }
    }

    /// <summary>A drag on the title bar (edges None: a move), an edge, a corner or the grip: the window follows the pointer's travel since the press,
    /// for as long as the left button stays held (a step without it ends the drag, whether or not the release reached this window).</summary>
    private void OnDragInput(InputEvent e, ResizeEdges edges)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                if (b.Pressed)
                {
                    _floaters.Focus(_id);
                    _drag.Begin(_floaters, _id, edges, b.GlobalPosition.X, b.GlobalPosition.Y);
                }
                else
                    _drag.End();
                break;
            case InputEventMouseMotion m when _drag.Active:
                _drag.Track(_floaters, _id, m.GlobalPosition.X, m.GlobalPosition.Y, (m.ButtonMask & MouseButtonMask.Left) != 0);
                break;
        }
    }
}
