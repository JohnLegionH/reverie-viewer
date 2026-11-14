// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>The Lucide icons drawn white at the size asked for times the interface size (so they stay sharp at 200 %); controls tint
/// them with their theme's text colour. Main thread only.</summary>
public static class ReverieIcons
{
    /// <summary>A picture of an icon at <paramref name="px"/> interface units, whatever the interface size (the texture itself is sharp at it).</summary>
    public static TextureRect Picture(string name, float px = ReverieTheme.IconSmall) => new()
    {
        Texture = Get(name, px), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        CustomMinimumSize = new Vector2(px, px), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static readonly Dictionary<(string Name, int Px, int Scale), ImageTexture?> Cache = new();

    public static Texture2D? Get(string name, float px = ReverieTheme.IconSmall)
    {
        int size = (int)MathF.Round(px);
        var key = (name, size, UiTheme.ScalePercent);
        if (Cache.TryGetValue(key, out var have))
            return have;
        ImageTexture? texture = null;
        var svg = LucideIcons.Svg(name, new UiColour(1f, 1f, 1f));
        if (svg is not null)
        {
            var image = new Image();
            if (image.LoadSvgFromString(svg, size * UiTheme.ScalePercent / 100f / 24f) == Error.Ok)
                texture = ImageTexture.CreateFromImage(image);
        }
        Cache[key] = texture;
        return texture;
    }
}

/// <summary>A button in the Reverie style: primary (the accent fill) or secondary (outlined), a pill 36 high.</summary>
public partial class RvButton : Button
{
    public RvButton(string text, bool primary = false)
    {
        Text = text;
        AccessibilityName = text;
        ThemeTypeVariation = primary ? ReverieStyle.Primary : ReverieStyle.Secondary;
        CustomMinimumSize = new Vector2(0, ReverieTheme.ButtonHeight);
        FocusMode = FocusModeEnum.All;
    }
}

/// <summary>A small pill for an animation or a similar item.</summary>
public partial class RvChip : Button
{
    public RvChip(string text)
    {
        Text = text;
        AccessibilityName = text;
        ThemeTypeVariation = ReverieStyle.Chip;
        CustomMinimumSize = new Vector2(0, ReverieTheme.ChipHeight);
    }
}

/// <summary>An icon-only button of the top bar or a title bar (28 square, a 16 icon), named for screen readers.</summary>
public partial class RvIconButton : Button
{
    public RvIconButton(string icon, string name, float size = ReverieTheme.IconButton)
    {
        Icon = ReverieIcons.Get(icon);
        IconAlignment = HorizontalAlignment.Center;
        TooltipText = name;
        AccessibilityName = name;
        ThemeTypeVariation = ReverieStyle.IconButton;
        CustomMinimumSize = new Vector2(size, size);
        ExpandIcon = false;
    }
}

/// <summary>A switch: the accent fill with the knob at the right when on; outlined with the knob at the left when off. Space, Enter
/// or a click toggles it; the focus ring is always drawn while it has the keyboard.</summary>
public partial class RvSwitch : Control
{
    private bool _on;

    public event Action<bool>? Toggled;

    public RvSwitch(string name, bool on = false)
    {
        _on = on;
        AccessibilityName = name;
        FocusMode = FocusModeEnum.All;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        CustomMinimumSize = new Vector2(ReverieTheme.SwitchWidth, ReverieTheme.SwitchHeight);
    }

    public bool On
    {
        get => _on;
        set { if (_on == value) return; _on = value; QueueRedraw(); Toggled?.Invoke(value); }
    }

    /// <summary>Drawn disabled: 45 % opacity, no response.</summary>
    public bool Disabled
    {
        get => _disabled;
        set { _disabled = value; Modulate = Modulate with { A = value ? 0.45f : 1f }; FocusMode = value ? FocusModeEnum.None : FocusModeEnum.All; }
    }

    private bool _disabled;

    /// <summary>Draws the focus ring without the keyboard (the gallery shows the state this way).</summary>
    public bool ShowsFocus { get; set; }

    public override void _GuiInput(InputEvent e)
    {
        if (_disabled)
            return;
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } || e.IsActionPressed("ui_accept"))
        {
            On = !On;
            AcceptEvent();
        }
    }

    public override void _Notification(int what)
    {
        if (what is (int)NotificationFocusEnter or (int)NotificationFocusExit or (int)NotificationThemeChanged)
            QueueRedraw();
    }

    public override void _Draw()
    {
        var s = ReverieStyle.Switch;
        var size = new Vector2(ReverieTheme.SwitchWidth, ReverieTheme.SwitchHeight);
        var at = new Vector2(0, (Size.Y - size.Y) / 2);
        float r = size.Y / 2, knob = ReverieTheme.SwitchKnob / 2f;
        var track = new StyleBoxFlat { BgColor = _on ? GetThemeColor("on", s) : default, BorderColor = _on ? GetThemeColor("on", s) : GetThemeColor("off_border", s), AntiAliasing = true };
        track.SetCornerRadiusAll((int)r);
        track.SetBorderWidthAll(_on ? 0 : 2);
        DrawStyleBox(track, new Rect2(at, size));
        var centre = at + new Vector2(_on ? size.X - r : r, r);
        DrawCircle(centre, knob - (_on ? 0 : 2), _on ? GetThemeColor("knob_on", s) : GetThemeColor("knob_off", s), true, -1, true);
        if (HasFocus() || ShowsFocus)
        {
            var ring = new StyleBoxFlat { DrawCenter = false, BorderColor = GetThemeColor("focus", s), AntiAliasing = true };
            ring.SetBorderWidthAll(2);
            ring.SetCornerRadiusAll((int)r + 4);
            DrawStyleBox(ring, new Rect2(at - new Vector2(4, 4), size + new Vector2(8, 8)));
        }
    }
}

/// <summary>A slider: the accent fill up to a white knob, the value shown at the right.</summary>
public partial class RvSlider : HBoxContainer
{
    public HSlider Slider { get; }
    public Label Value { get; }

    public RvSlider(string name, double min, double max, double value, string unit = "")
    {
        Slider = new HSlider { MinValue = min, MaxValue = max, Value = value, Step = 1, ThemeTypeVariation = ReverieStyle.Slider, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        Slider.AccessibilityName = name;
        Value = new Label { ThemeTypeVariation = ReverieStyle.Status, CustomMinimumSize = new Vector2(48, 0), HorizontalAlignment = HorizontalAlignment.Right };
        Slider.ValueChanged += v => Value.Text = $"{v:0}{unit}";
        Value.Text = $"{value:0}{unit}";
        AddChild(Slider);
        AddChild(Value);
    }
}

/// <summary>A segmented control: a track with one selected segment. Left and Right move the selection.</summary>
public partial class RvSegmented : PanelContainer
{
    private readonly List<Button> _segments = new();
    private int _selected;

    public event Action<int>? Selected;

    public RvSegmented(string name, IReadOnlyList<string> labels, int selected = 0)
    {
        ThemeTypeVariation = ReverieStyle.Track;
        AccessibilityName = name;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 2);
        AddChild(row);
        for (int i = 0; i < labels.Count; i++)
        {
            int index = i;
            var b = new Button { Text = labels[i], CustomMinimumSize = new Vector2(0, ReverieTheme.SegmentHeight), FocusMode = FocusModeEnum.All };
            b.AccessibilityName = labels[i];
            b.Pressed += () => Select(index);
            b.GuiInput += e =>
            {
                if (e.IsActionPressed("ui_left")) { Select(Math.Max(0, _selected - 1)); _segments[_selected].GrabFocus(); b.AcceptEvent(); }
                else if (e.IsActionPressed("ui_right")) { Select(Math.Min(_segments.Count - 1, _selected + 1)); _segments[_selected].GrabFocus(); b.AcceptEvent(); }
            };
            _segments.Add(b);
            row.AddChild(b);
        }
        Select(selected);
    }

    public int Current => _selected;

    public IReadOnlyList<Button> Segments => _segments;

    public void Select(int index)
    {
        _selected = Math.Clamp(index, 0, _segments.Count - 1);
        for (int i = 0; i < _segments.Count; i++)
            _segments[i].ThemeTypeVariation = i == _selected ? ReverieStyle.SegmentSelected : ReverieStyle.Segment;
        Selected?.Invoke(_selected);
    }
}

/// <summary>A card: a bordered group inside a window, radius 12; the warning variant carries the warning colours and icon.</summary>
public partial class RvCard : PanelContainer
{
    public VBoxContainer Body { get; }

    public RvCard(string? heading, string? text, bool warning = false)
    {
        ThemeTypeVariation = warning ? ReverieStyle.CardWarning : ReverieStyle.Card;
        Body = new VBoxContainer();
        Body.AddThemeConstantOverride("separation", 6);
        AddChild(Body);
        if (heading is not null)
        {
            var row = new HBoxContainer();
            if (warning)
                row.AddChild(ReverieIcons.Picture(LucideIcons.Warning));
            row.AddChild(new Label { Text = heading, ThemeTypeVariation = warning ? ReverieStyle.WarnText : ReverieStyle.CardHeading });
            Body.AddChild(row);
        }
        if (text is not null)
            Body.AddChild(new Label { Text = text, ThemeTypeVariation = warning ? ReverieStyle.WarnText : ReverieStyle.Status, AutowrapMode = TextServer.AutowrapMode.WordSmart });
    }

    public override void _Notification(int what)
    {
        if (what is (int)NotificationThemeChanged or (int)NotificationEnterTree)
            foreach (var n in FindChildren("*", "TextureRect", true, false))
                ((TextureRect)n).SelfModulate = GetThemeColor("font_color", ReverieStyle.WarnText);
    }
}

/// <summary>A list row: a disclosure arrow (for a folder), an icon, the label and optional trailing text. A folder opens or closes on a
/// click of its row only; hovering does nothing.</summary>
public partial class RvListRow : Button
{
    private readonly Label _trailing;
    private bool _selected;

    public RvListRow(string label, string? icon = null, string? trailing = null, bool? open = null, bool sidebar = false)
    {
        Text = label;
        AccessibilityName = label;
        _icon = icon;
        Icon = icon is null ? null : ReverieIcons.Get(icon);
        Alignment = HorizontalAlignment.Left;
        CustomMinimumSize = new Vector2(0, sidebar ? ReverieTheme.SidebarRow : ReverieTheme.ListRow);
        ThemeTypeVariation = ReverieStyle.ListRow;
        IsOpen = open;
        _trailing = new Label { Text = trailing ?? "", ThemeTypeVariation = ReverieStyle.Muted, MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _trailing.AnchorLeft = _trailing.AnchorRight = 1;
        _trailing.AnchorTop = 0;
        _trailing.AnchorBottom = 1;
        _trailing.OffsetLeft = -64;
        _trailing.OffsetRight = -10;
        AddChild(_trailing);
        if (open is not null)
            Pressed += () => IsOpen = !IsOpen;
    }

    /// <summary>For a folder, whether it is open (null: not a folder). Shown by the arrow before the label.</summary>
    public bool? IsOpen
    {
        get => _open;
        set { _open = value; if (value is { } o) Icon = ReverieIcons.Get(o ? LucideIcons.Down : LucideIcons.Submenu); else Icon = _icon is null ? null : ReverieIcons.Get(_icon); }
    }

    private bool? _open;
    private readonly string? _icon;

    public bool Selected
    {
        get => _selected;
        set { _selected = value; ThemeTypeVariation = value ? ReverieStyle.ListRowSelected : ReverieStyle.ListRow; }
    }
}

/// <summary>A sidebar: the sunken surface with an upper-case section label and rows 34 high.</summary>
public partial class RvSidebarList : PanelContainer
{
    public VBoxContainer Rows { get; }

    public RvSidebarList(string? section)
    {
        ThemeTypeVariation = ReverieStyle.Sidebar;
        Rows = new VBoxContainer();
        Rows.AddThemeConstantOverride("separation", 2);
        AddChild(Rows);
        if (section is not null)
            Rows.AddChild(new Label { Text = section.ToUpperInvariant(), ThemeTypeVariation = ReverieStyle.Overline });
    }

    public RvListRow Add(string label, string? icon = null, bool selected = false)
    {
        var row = new RvListRow(label, icon, sidebar: true) { Selected = selected };
        Rows.AddChild(row);
        return row;
    }
}

/// <summary>A status chip: an icon or a dot and text. Plain text: not focusable and not clickable unless it has an action.</summary>
public partial class RvStatusChip : HBoxContainer
{
    private readonly Label _text;
    private readonly Control _mark;
    private readonly string _colourName;

    public RvStatusChip(string text, string? icon = null, string dotColour = "ok")
    {
        _colourName = dotColour;
        MouseFilter = MouseFilterEnum.Ignore;
        FocusMode = FocusModeEnum.None;
        AddThemeConstantOverride("separation", 6);
        if (icon is not null)
            _mark = ReverieIcons.Picture(icon);
        else
            _mark = new Dot { CustomMinimumSize = new Vector2(8, 8), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_mark);
        _text = new Label { Text = text, ThemeTypeVariation = ReverieStyle.Status, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_text);
        AccessibilityName = text;
    }

    public string Text
    {
        get => _text.Text;
        set { _text.Text = value; AccessibilityName = value; }
    }

    /// <summary>The dot's colour by its name in the status colours: "ok", "accent" or "muted".</summary>
    public string DotColour
    {
        set { if (_mark is Dot d) { d.ColourName = value; d.QueueRedraw(); } }
    }

    public override void _Notification(int what)
    {
        if (what is (int)NotificationThemeChanged or (int)NotificationEnterTree && _mark is TextureRect t)
            t.SelfModulate = GetThemeColor("font_color", ReverieStyle.Status);
    }

    private partial class Dot : Control
    {
        public string ColourName = "ok";

        public override void _Draw() => DrawCircle(Size / 2, Math.Min(Size.X, Size.Y) / 2, GetThemeColor(ColourName, ReverieStyle.Status), true, -1, true);
    }
}

/// <summary>A stepper: numbered steps with the current one in the accent.</summary>
public partial class RvStepper : Control
{
    private readonly string[] _steps;
    private int _current;

    public RvStepper(IReadOnlyList<string> steps, int current = 0)
    {
        _steps = [.. steps];
        _current = current;
        CustomMinimumSize = new Vector2(0, 28);
        MouseFilter = MouseFilterEnum.Ignore;
        AccessibilityName = string.Join(", ", steps);
    }

    public int Current
    {
        get => _current;
        set { _current = Math.Clamp(value, 0, _steps.Length - 1); QueueRedraw(); }
    }

    public override void _Draw()
    {
        var s = ReverieStyle.Stepper;
        var font = GetThemeFont("font", "Label");
        int size = GetThemeFontSize("font_size", ReverieStyle.Status);
        float x = 0, r = 11, mid = Size.Y / 2;
        for (int i = 0; i < _steps.Length; i++)
        {
            bool now = i == _current;
            var centre = new Vector2(x + r, mid);
            DrawCircle(centre, r, now ? GetThemeColor("current", s) : GetThemeColor("other", s), true, -1, true);
            if (!now)
                DrawCircle(centre, r, GetThemeColor("other_border", s), false, 1, true);
            string n = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var nSize = font.GetStringSize(n, HorizontalAlignment.Left, -1, size);
            DrawString(font, centre + new Vector2(-nSize.X / 2, nSize.Y / 2 - 3), n, HorizontalAlignment.Left, -1, size, now ? GetThemeColor("current_text", s) : GetThemeColor("text", s));
            x += 2 * r + 8;
            DrawString(font, new Vector2(x, mid + size / 2f - 2), _steps[i], HorizontalAlignment.Left, -1, size, now ? GetThemeColor("text", s) : GetThemeColor("muted", s));
            x += font.GetStringSize(_steps[i], HorizontalAlignment.Left, -1, size).X + 24;
        }
    }
}
