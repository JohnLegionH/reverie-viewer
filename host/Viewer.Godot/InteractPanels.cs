// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>A list of label and value rows in a floater (Inspect, About Land, the contents of an object). Read-only.</summary>
public partial class RowsPanel : ScrollContainer
{
    private readonly StringTable _strings;
    private readonly VBoxContainer _rows = new() { Name = "rows", SizeFlagsHorizontal = SizeFlags.ExpandFill };

    public RowsPanel(StringTable strings, string name)
    {
        _strings = strings;
        Name = name;
        HorizontalScrollMode = ScrollMode.Disabled;
        AddChild(_rows);
    }

    /// <summary>Replaces the rows. A label or value that is a string-table key is shown in words.</summary>
    public void Set(IEnumerable<(string LabelKey, string Value)> rows)
    {
        foreach (var child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var (labelKey, value) in rows)
        {
            string label = _strings.Has(labelKey) ? _strings.Get(labelKey) : labelKey;
            string text = _strings.Has(value) ? _strings.Get(value) : value;
            var row = new HBoxContainer { Name = "row_" + labelKey };
            var l = new Label { Text = label, CustomMinimumSize = new Vector2(120, 0), SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
            l.AccessibilityName = label;
            var v = new Label { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(160, 0) };
            v.AccessibilityName = label + ": " + text;
            row.AddChild(l);
            row.AddChild(v);
            _rows.AddChild(row);
        }
    }

    /// <summary>The text of every row, label then value, for checks.</summary>
    public IReadOnlyList<(string Label, string Value)> Texts()
    {
        var list = new List<(string, string)>();
        foreach (var child in _rows.GetChildren())
            if (child is HBoxContainer { } row && row.GetChildCount() == 2)
                list.Add((((Label)row.GetChild(0)).Text, ((Label)row.GetChild(1)).Text));
        return list;
    }
}

/// <summary>The right-click menu: a small panel of buttons at the mouse, closed by choosing an entry or clicking away. As the official
/// viewer's menus (llmenugl.cpp), it never holds the keyboard: its entries do not take the keyboard focus, and closing it hands the
/// keyboard back to the world if anything in it had it, so the arrow keys move the avatar again at once.</summary>
public partial class ContextMenuPanel : PanelContainer
{
    private readonly StringTable _strings;
    private readonly VBoxContainer _box = new() { Name = "entries" };
    private Action<string>? _choose;

    public ContextMenuPanel(StringTable strings)
    {
        _strings = strings;
        Name = "context_menu";
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 100;
        AddChild(_box);
    }

    public bool IsOpen => Visible;

    public IReadOnlyList<string> EntryIds
    {
        get
        {
            var ids = new List<string>();
            foreach (var c in _box.GetChildren())
                if (c is Button b)
                    ids.Add(b.Name.ToString()["entry_".Length..]);
            return ids;
        }
    }

    public void Open(Vector2 at, IReadOnlyList<ContextEntry> entries, Action<string> choose, Vector2 limit)
    {
        Close();
        if (entries.Count == 0)
            return;
        _choose = choose;
        // the point is in window pixels; the menu lives in the main view, so it is placed and kept inside that view
        if (GetParent() is Control view)
        {
            at -= view.GlobalPosition;
            limit = view.Size;
        }
        _at = at;
        _limit = limit;
        Fill(entries, null);
        Visible = true;
    }

    private Vector2 _at, _limit;

    /// <summary>Draws a list of entries; an entry with a sub-menu shows the sub-menu in the same panel (with a Back entry first), so the
    /// menu stays on screen however many points it lists.</summary>
    private void Fill(IReadOnlyList<ContextEntry> entries, IReadOnlyList<ContextEntry>? parent)
    {
        foreach (var c in _box.GetChildren())
        {
            _box.RemoveChild(c);
            c.QueueFree();
        }
        if (parent is not null)
        {
            var back = new Button { Name = "entry_back", Text = _strings.Get("menu.back"), Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None };
            back.AccessibilityName = _strings.Get("menu.back");
            back.Pressed += () => Fill(parent, null);
            _box.AddChild(back);
        }
        var host = _box;
        if (parent is not null && entries.Count > 12)
        {
            // a long list (the attachment points) scrolls
            var scroll = new ScrollContainer { Name = "entries_scroll", CustomMinimumSize = new Vector2(0, 12 * 31), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var inner = new VBoxContainer { Name = "entries_inner", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            scroll.AddChild(inner);
            _box.AddChild(scroll);
            host = inner;
        }
        foreach (var e in entries)
        {
            string text = e.Literal ?? (_strings.Has(e.LabelKey) ? _strings.Get(e.LabelKey) : e.Id);
            bool sub = e.Children is { Count: > 0 };
            var b = new Button { Name = "entry_" + e.Id, Text = sub ? text + "  >" : text, Disabled = !e.Enabled, Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None };
            b.AccessibilityName = text;
            string id = e.Id;
            if (sub)
            {
                var kids = e.Children!;
                b.Pressed += () => { Fill(kids, entries); ResetSize(); Reposition(); };
            }
            else
                b.Pressed += () => { var c = _choose; Close(); c?.Invoke(id); };
            host.AddChild(b);
        }
        ResetSize();
        Reposition();
    }

    /// <summary>Replaces the entries of an open menu (the properties of the clicked object arrived after the menu was drawn).</summary>
    public void Update(IReadOnlyList<ContextEntry> entries)
    {
        if (Visible && _choose is not null && entries.Count > 0)
            Fill(entries, null);
    }

    private void Reposition()
    {
        var size = GetCombinedMinimumSize();
        Position = new Vector2(Math.Min(_at.X, Math.Max(0, _limit.X - size.X)), Math.Min(_at.Y, Math.Max(0, _limit.Y - size.Y)));
    }

    /// <summary>Raised when Esc closes the menu with no choice, so the right-click selection goes as when the menu is clicked away.</summary>
    public event Action? Dismissed;

    /// <summary>Esc with the menu open: the menu closes with no choice.</summary>
    public void Dismiss()
    {
        if (!Visible)
            return;
        Close();
        Dismissed?.Invoke();
    }

    public void Close()
    {
        if (IsInsideTree() && GetViewport().GuiGetFocusOwner() is { } owner && IsAncestorOf(owner))
            GetViewport().GuiReleaseFocus();
        Visible = false;
        _choose = null;
        foreach (var c in _box.GetChildren())
        {
            _box.RemoveChild(c);
            c.QueueFree();
        }
    }
}

/// <summary>Installs the interaction windows and the stand button into the shell.</summary>
public static class InteractFeature
{
    public const string InspectId = "inspect", LandId = "about_land", ContentsId = "object_contents", BuyId = "object_buy", PayId = "object_pay";

    public sealed record Panels(ContextMenuPanel Menu, RowsPanel Inspect, RowsPanel Land, ContentsPanel Contents, Button Stand, Button StopFlying, BuyPanel Buy, PayPanel Pay);

    public static Panels Install(ShellRoot shell)
    {
        var s = shell.Shell;
        var menu = new ContextMenuPanel(s.Strings);
        shell.MainView.AddChild(menu);
        shell.ContextMenu = menu;
        ReverieRestyle.Apply(menu, RestylePlace.Menu);   // drawn as the dropdown menu (UI spec section 4)
        var inspect = new RowsPanel(s.Strings, "inspect_rows");
        var land = new RowsPanel(s.Strings, "land_rows");
        var contents = new ContentsPanel(s.Strings);
        var buy = new BuyPanel(s.Strings);
        var pay = new PayPanel(s.Strings);
        shell.AddFloater(new FloaterSpec(InspectId, "inspect.title", new UiSize(360, 240), new UiSize(260, 160)), inspect);
        shell.AddFloater(new FloaterSpec(LandId, "land.title", new UiSize(380, 420), new UiSize(280, 240)), land);
        shell.AddFloater(new FloaterSpec(ContentsId, "contents.title", new UiSize(340, 300), new UiSize(240, 160)), contents);
        shell.AddFloater(new FloaterSpec(BuyId, "buy.title", new UiSize(340, 300), new UiSize(240, 200)), buy);
        shell.AddFloater(new FloaterSpec(PayId, "pay.title", new UiSize(300, 180), new UiSize(240, 140)), pay);
        string standText = s.Strings.Get("interact.stand");
        var stand = new Button { Name = "stand_button", Text = standText, Visible = false, TooltipText = s.Strings.Get("interact.stand.tip") };
        stand.AccessibilityName = standText;
        stand.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        stand.OffsetBottom = -80;
        stand.OffsetTop = -112;
        stand.OffsetLeft = -60;
        stand.OffsetRight = 60;
        shell.MainView.AddChild(stand);
        // Stop Flying takes the Stand button's place while flying (the official viewer shows one or the other in the same panel)
        string stopText = s.Strings.Get("interact.stop_flying");
        var stop = new Button { Name = "stop_flying_button", Text = stopText, Visible = false, TooltipText = s.Strings.Get("interact.stop_flying.tip") };
        stop.AccessibilityName = stopText;
        stop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        stop.OffsetBottom = -80;
        stop.OffsetTop = -112;
        stop.OffsetLeft = -60;
        stop.OffsetRight = 60;
        shell.MainView.AddChild(stop);
        return new Panels(menu, inspect, land, contents, stand, stop, buy, pay);
    }
}
