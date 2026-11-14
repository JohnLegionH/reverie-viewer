// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The dock: one floating bar of buttons, each an icon with its name underneath, in the groups of <see cref="DockModel"/> with a thin
/// divider between groups. It wraps to a second row before any button is hidden. A toggle shows a dot and "· On" or "· Off"; a toggle with
/// a menu has a small arrow that opens its menu, as a right-click does; attention is a 2-pixel accent ring that pulses three times and
/// stays until the button is used. Styled only through the shared theme (<see cref="ReverieStyle"/>).
/// </summary>
public partial class Dock : PanelContainer
{
    private readonly DockModel _model;
    private readonly StringTable _strings;
    private readonly HFlowContainer _flow;
    private readonly Dictionary<DockItem, Button> _buttons = new();
    private readonly HashSet<DockItem> _pulsed = new();

    /// <summary>A button's main part was clicked.</summary>
    public event Action<DockItem>? Clicked;

    /// <summary>A button's menu is to open (its arrow, or a right-click): the item and where (global coordinates).</summary>
    public event Action<DockItem, Vector2>? MenuRequested;

    /// <summary>Whether a built-in button's window is open (the button then shows as on).</summary>
    public Func<DockItem, bool>? IsOpen { get; set; }

    /// <summary>The row at the dock's end where an add-on's older quick controls go (interface 0.4); empty unless an add-on uses one.</summary>
    public HBoxContainer QuickRow { get; }

    public IReadOnlyDictionary<DockItem, Button> Buttons => _buttons;

    public Dock(DockModel model, StringTable strings)
    {
        _model = model;
        _strings = strings;
        Name = "dock";
        ThemeTypeVariation = ReverieStyle.Dock;
        MouseFilter = MouseFilterEnum.Stop;
        AccessibilityName = strings.Get("dock.bar");
        _flow = new HFlowContainer { Name = "dock_flow", Alignment = FlowContainer.AlignmentMode.Center };
        _flow.AddThemeConstantOverride("h_separation", (int)ReverieTheme.DockGap);
        _flow.AddThemeConstantOverride("v_separation", (int)ReverieTheme.DockGap);
        AddChild(_flow);
        QuickRow = new HBoxContainer { Name = "quick_row", Visible = false };
        QuickRow.AddThemeConstantOverride("separation", 4);
        _model.Changed += Rebuild;
        Rebuild();
    }

    public override void _ExitTree() => _model.Changed -= Rebuild;

    /// <summary>The widest the dock may be (the window's width less its margins); it wraps to more rows within it.</summary>
    public float MaxWidth { get; set; } = float.MaxValue;

    /// <summary>The width one row of every button takes (the dock's preferred width).</summary>
    public float OneRowWidth()
    {
        float w = 0;
        int count = 0;
        foreach (var child in _flow.GetChildren().OfType<Control>().Where(c => c.Visible))
        {
            w += child.GetCombinedMinimumSize().X;
            count++;
        }
        return w + Math.Max(0, count - 1) * ReverieTheme.DockGap + 2 * (ReverieTheme.DockPadding + 1);
    }

    /// <summary>The size to place the dock at: one row when it fits, else as many rows as the flow needs within <see cref="MaxWidth"/>.</summary>
    public Vector2 Fit()
    {
        float width = Math.Min(OneRowWidth(), MaxWidth);
        _flow.CustomMinimumSize = new Vector2(width - 2 * (ReverieTheme.DockPadding + 1), 0);
        _flow.Size = new Vector2(width - 2 * (ReverieTheme.DockPadding + 1), 0);
        int rows = Rows(width - 2 * (ReverieTheme.DockPadding + 1));
        float height = rows * ReverieTheme.DockButtonHeight + (rows - 1) * ReverieTheme.DockGap + 2 * (ReverieTheme.DockPadding + 1);
        return new Vector2(width, height);
    }

    /// <summary>How many rows the buttons take at this inner width.</summary>
    public int Rows(float inner)
    {
        int rows = 1;
        float x = 0;
        foreach (var child in _flow.GetChildren().OfType<Control>().Where(c => c.Visible))
        {
            float w = child.GetCombinedMinimumSize().X;
            if (x > 0 && x + ReverieTheme.DockGap + w > inner + 0.5f)
            {
                rows++;
                x = w;
            }
            else
                x += (x > 0 ? ReverieTheme.DockGap : 0) + w;
        }
        return rows;
    }

    /// <summary>Draws the buttons again from the model (an item came or went, or changed its state).</summary>
    public void Rebuild()
    {
        foreach (var child in _flow.GetChildren())
        {
            _flow.RemoveChild(child);
            if (child != QuickRow)
                child.QueueFree();
        }
        _buttons.Clear();
        var groups = _model.Groups;
        for (int g = 0; g < groups.Count; g++)
        {
            if (g > 0)
                _flow.AddChild(Divider(g));
            foreach (var item in groups[g].Items)
            {
                var button = MakeButton(item);
                _buttons[item] = button;
                _flow.AddChild(button);
            }
        }
        _flow.AddChild(QuickRow);
        QuickRow.Visible = QuickRow.GetChildCount() > 0;
        Sync();
    }

    private static Control Divider(int index)
    {
        // a 1 x 32 line with 5 on each side, centred in the button's height
        var holder = new MarginContainer { Name = "divider_" + index, MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(1 + 2 * ReverieTheme.DockDividerMargin, ReverieTheme.DockButtonHeight) };
        holder.AddThemeConstantOverride("margin_left", (int)ReverieTheme.DockDividerMargin);
        holder.AddThemeConstantOverride("margin_right", (int)ReverieTheme.DockDividerMargin);
        var line = new Panel { ThemeTypeVariation = ReverieStyle.DockDivider, CustomMinimumSize = new Vector2(1, ReverieTheme.DockDividerHeight), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        holder.AddChild(line);
        return holder;
    }

    private Button MakeButton(DockItem item)
    {
        var button = new Button
        {
            Name = "dock_" + item.Id + (item.Owner is null ? "" : "_" + item.Owner), ThemeTypeVariation = ReverieStyle.DockButton,
            CustomMinimumSize = new Vector2(ReverieTheme.DockButtonWidth, ReverieTheme.DockButtonHeight), VerticalIconAlignment = VerticalAlignment.Top,
            IconAlignment = HorizontalAlignment.Center, ExpandIcon = false, FocusMode = FocusModeEnum.All, ClipText = true,
            TooltipText = item.Tooltip ?? "",
        };
        button.AddThemeConstantOverride("icon_max_width", (int)ReverieTheme.DockIcon);
        button.Icon = IconOf(item.Icon);
        button.Pressed += () =>
        {
            _pulsed.Remove(item);
            Clicked?.Invoke(item);
            button.ReleaseFocus();
        };
        button.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } && item.Kind == DockItemKind.ToggleWithMenu)
            {
                button.AcceptEvent();
                MenuRequested?.Invoke(item, button.GetGlobalRect().Position);
            }
        };
        if (item.Kind is DockItemKind.Toggle or DockItemKind.ToggleWithMenu)
        {
            var dot = new Panel { Name = "on_dot", ThemeTypeVariation = ReverieStyle.DockDot, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
            dot.AnchorLeft = dot.AnchorRight = 1;
            dot.OffsetLeft = -14; dot.OffsetRight = -6; dot.OffsetTop = 6; dot.OffsetBottom = 14;
            button.AddChild(dot);
        }
        if (item.Kind == DockItemKind.ToggleWithMenu)
        {
            var chevron = new Button { Name = "menu_arrow", Flat = true, FocusMode = FocusModeEnum.None, ThemeTypeVariation = ReverieStyle.IconButton, TooltipText = _strings.Get("dock.menu", item.Owner is null ? _strings.Get(item.Label) : item.Label) };
            chevron.Icon = ReverieIcons.Get(LucideIcons.Down, 12);
            chevron.AddThemeConstantOverride("icon_max_width", 12);
            chevron.AnchorLeft = chevron.AnchorRight = 1;
            chevron.AnchorTop = chevron.AnchorBottom = 0.5f;
            chevron.OffsetLeft = -16; chevron.OffsetRight = 0; chevron.OffsetTop = -14; chevron.OffsetBottom = 2;
            chevron.Pressed += () => MenuRequested?.Invoke(item, button.GetGlobalRect().Position);
            button.AddChild(chevron);
        }
        return button;
    }

    private static Texture2D? IconOf(string icon)
    {
        if (!icon.TrimStart().StartsWith("<svg", StringComparison.Ordinal))
            return ReverieIcons.Get(icon, ReverieTheme.DockIcon);
        // an add-on's own single-colour svg, drawn in white so the button's text colour tints it
        var image = new Image();
        string white = icon.Replace("currentColor", "#ffffff");
        return image.LoadSvgFromString(white, ReverieTheme.DockIcon * UiTheme.ScalePercent / 100f / 24f) == Error.Ok ? ImageTexture.CreateFromImage(image) : ReverieIcons.Get(LucideIcons.AddOn, ReverieTheme.DockIcon);
    }

    /// <summary>Brings every button's name, look and dot up to the model (and to which windows are open).</summary>
    public void Sync()
    {
        foreach (var (item, button) in _buttons)
        {
            string name = item.Shown(_strings);
            if (button.Text != name)
            {
                button.Text = name;
                button.AccessibilityName = name;
            }
            bool on = item.On || (item.Owner is null && IsOpen?.Invoke(item) == true);
            string look = item.Attention ? ReverieStyle.DockButtonAttention : on ? ReverieStyle.DockButtonOn : ReverieStyle.DockButton;
            if (button.ThemeTypeVariation != look)
                button.ThemeTypeVariation = look;
            if (button.GetNodeOrNull<Control>("on_dot") is { } dot && dot.Visible != item.On)
                dot.Visible = item.On;
            if (item.Attention && _pulsed.Add(item))
                Pulse(button);
            else if (!item.Attention)
                _pulsed.Remove(item);
        }
    }

    /// <summary>The attention ring pulses three times, then stays.</summary>
    private void Pulse(Button button)
    {
        if (!button.IsInsideTree())
            return;
        var tween = button.CreateTween();
        for (int i = 0; i < 3; i++)
        {
            tween.TweenProperty(button, "modulate:a", 0.55f, 0.3);
            tween.TweenProperty(button, "modulate:a", 1f, 0.3);
        }
    }

    public override void _Process(double delta) => Sync();
}
