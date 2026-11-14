// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>The Reverie scenes --tr-ui accepts (with --tr-theme=reverie-dark or reverie-light): the top bar at 100 % and 200 %,
    /// the top bar with the World menu open, and the component gallery (every component in every state).</summary>
    public static readonly string[] ReverieScenes = ["reverie-topbar-100", "reverie-topbar-200", "reverie-topbar-menu", "reverie-gallery"];

    /// <summary>Text in scripts the carried fonts do not have (CJK, Hangul, Arabic) and an emoji: the gallery shows it and the case
    /// fails when no font in the chain has a glyph for it (it would draw as boxes).</summary>
    internal const string OtherScripts = "日本語 中文 한국어 العربية 😀";

    /// <summary>Example text of the gallery (render-case data, not interface text).</summary>
    internal const string ExampleAddress = "Example Region/128/64/22", ExampleContent = "Window content";

    private async Task<int> ReverieSceneRun(string scene, string look, string? golden)
    {
        if (ReverieTheme.VariantOf(look) is null)
            throw new ArgumentException("the Reverie scenes take --tr-theme=reverie-dark or reverie-light");
        var strings = StringTable.English();
        bool gallery = scene == "reverie-gallery";
        int pct = scene == "reverie-topbar-200" ? 200 : 100;
        var size = gallery ? new Vector2I(1280, 800) : new Vector2I(UiWidth, UiHeight);
        UiTheme.Apply(ThemePresets.Get(look), pct);
        var rig = MakeRig(size);
        rig.Viewport.Size2DOverride = new Vector2I(size.X * 100 / pct, size.Y * 100 / pct);
        rig.Viewport.Size2DOverrideStretch = true;
        var root = new Control { Name = "reverie_root", Theme = ShellTheme.Build(UiTheme.Tokens) };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        rig.Viewport.AddChild(root);
        var backdrop = new TextureRect { Texture = Backdrop(), StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(backdrop);
        var problems = new List<string>();

        if (gallery)
        {
            var g = new ComponentGallery(strings);
            g.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(g);
            await Frames(3);
            g.ForceStates();
            if (!Fonts.Covers(OtherScripts))
                problems.Add("a CJK, Hangul, Arabic or emoji character has no glyph in the font chain (it would draw as a box)");
            _result["glyphs"] = new { text = OtherScripts, covered = Fonts.Covers(OtherScripts) };
        }
        else
        {
            var bar = ExampleTopBar(strings);
            bar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
            root.AddChild(bar);
            await Frames(3);
            if (scene == "reverie-topbar-menu")
                bar.Menus.ClickTitle(3);   // World
            await Frames(3);
            // the narrow-window rule: at 200 % the 960-pixel picture is 480 interface units wide, too narrow for one row
            bool shouldWrap = pct == 200;
            if (bar.LocationOnOwnRow != shouldWrap)
                problems.Add($"location row: on its own row {bar.LocationOnOwnRow}, expected {shouldWrap}");
            if (bar.GetCombinedMinimumSize().X > rig.Viewport.Size2DOverride.X + 0.5f)
                problems.Add($"the top bar needs {bar.GetCombinedMinimumSize().X} units, more than the {rig.Viewport.Size2DOverride.X} it has: something is cut off");
            if (scene == "reverie-topbar-menu" && bar.Menus.OpenMenu != 3)
                problems.Add("the World menu did not open on its click");
            _result["top_bar"] = new { location_own_row = bar.LocationOnOwnRow, location = bar.Location.DisplayText, open_menu = bar.Menus.OpenMenu };
        }
        await Frames(10);
        _failures.AddRange(problems);
        SaveAndDiff(rig, size, golden);
        root.QueueFree();
        UiTheme.Apply(ThemePresets.Get(ThemePresets.Classic), 100);
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>The menus of the top bar in the render cases: the app menu and the official top-level menus with example entries.</summary>
    internal static List<RvMenu> ExampleMenus(StringTable strings) =>
    [
        new(strings.Get("topbar.app"), () =>
        [
            new(strings.Get("menu.app.about"), LucideIcons.Grid), new(strings.Get("menu.app.preferences"), null, "Ctrl+P"),
            new(strings.Get("menu.app.addons"), LucideIcons.AddOn), new(strings.Get("menu.app.updates"), LucideIcons.Return), RvMenuEntry.Line,
            new(strings.Get("menu.app.logout")), new(strings.Get("menu.app.quit"), null, "Ctrl+Q"),
        ], IsApp: true),
        new("Me", () => [new("Inventory", LucideIcons.Inventory, "Ctrl+I"), new("Outfits", LucideIcons.Outfits), new("Appearance", LucideIcons.Appearance)]),
        new("Communicate", () => [new("Nearby chat", LucideIcons.Chat, "Ctrl+H"), new("Conversations", LucideIcons.Conversations, "Ctrl+T"), new("People", LucideIcons.People)]),
        new("World", () =>
        [
            new("Teleport...", LucideIcons.Teleport), new("Go home", LucideIcons.Home, "Ctrl+Shift+H"), new("Set home to here", LucideIcons.Home), RvMenuEntry.Line,
            new("World map", LucideIcons.Map, "Ctrl+M"), new("Mini-map", LucideIcons.MiniMap, "Ctrl+Shift+M"), RvMenuEntry.Line,
            new("Environment", LucideIcons.Grid, Children: [new("Sunrise"), new("Midday", Checked: true), new("Sunset"), new("Midnight")]),
        ]),
        new("Build", () => [new("Build", LucideIcons.Build, "Ctrl+B")]),
        new("Content", () => [new("Add-ons...", LucideIcons.AddOn)]),
        new("Help", () => [new(strings.Get("menu.app.about"), LucideIcons.Grid)]),
    ];

    private static TopBar ExampleTopBar(StringTable strings)
    {
        var bar = new TopBar(strings, ExampleMenus(strings));
        bar.SetGrid("Example Grid");
        bar.SetTime(TestClockText);
        bar.SetConnection(strings.Get("status.connected"), true);
        bar.Location.Show(LocationField.Display("Example Region", "Example Parcel", new System.Numerics.Vector3(128, 64, 22), 0));
        return bar;
    }

    /// <summary>Saves the picture under the case's tag and, with a golden, diffs it as the other interface scenes are diffed.</summary>
    private void SaveAndDiff(Rig rig, Vector2I size, string? golden)
    {
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        Directory.CreateDirectory(_out);
        img.SavePng(Path.Combine(_out, _tag + ".png"));
        if (golden is null)
            return;
        if (!Path.IsPathFullyQualified(golden) || !File.Exists(golden))
        {
            _failures.Add("golden not found: " + Path.GetFileName(golden));
            return;
        }
        var g = Image.LoadFromFile(golden);
        g.Convert(Image.Format.Rgba8);
        var diff = g.GetWidth() != size.X || g.GetHeight() != size.Y
            ? new DiffResult(false, g.GetWidth(), g.GetHeight(), 0, 0, 0, 0, "golden size differs")
            : ImageDiff.Compare(img.GetData(), g.GetData(), size.X, size.Y, 4, DiffTolerance.Terrain);
        _result["diff"] = new Dictionary<string, object?>
        {
            ["golden"] = Path.GetFileName(golden), ["pass"] = diff.Pass, ["differing"] = diff.Differing, ["differing_fraction"] = diff.DifferingFraction,
            ["mean_abs_error"] = Math.Round(diff.MeanAbsError, 4), ["max_channel_error"] = diff.MaxChannelError, ["summary"] = diff.ToString(),
        };
        if (!diff.Pass)
            _failures.Add("diff " + diff);
    }
}

/// <summary>Every Reverie component in every state (normal, hover, pressed or open, keyboard focus, disabled), for the render cases.
/// States the pointer would make are shown with the theme's own face for that state.</summary>
public partial class ComponentGallery : MarginContainer
{
    private readonly StringTable _strings;
    private readonly List<(Control Control, string State)> _forced = new();

    public ComponentGallery(StringTable strings)
    {
        _strings = strings;
        Name = "component_gallery";
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            AddThemeConstantOverride(side, 16);
        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 20);
        AddChild(columns);
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 12);
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        right.AddThemeConstantOverride("separation", 12);
        columns.AddChild(left);
        columns.AddChild(right);

        string[] states = ["normal", "hover", "pressed", "focus", "disabled"];
        HBoxContainer Row(VBoxContainer into, string label)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            row.AddChild(new Label { Text = label, ThemeTypeVariation = ReverieStyle.Caption, CustomMinimumSize = new Vector2(96, 0), VerticalAlignment = VerticalAlignment.Center });
            into.AddChild(row);
            return row;
        }
        void States(VBoxContainer into, string label, Func<string, Button> make)
        {
            var row = Row(into, label);
            foreach (var s in states)
            {
                var b = make(s);
                row.AddChild(b);
                _forced.Add((b, s));
            }
        }

        States(left, "Primary", s => new RvButton(Cap(s), primary: true));
        States(left, "Secondary", s => new RvButton(Cap(s)));
        States(left, "Menu title", s => new Button { Text = s == "pressed" ? "Open" : Cap(s), ThemeTypeVariation = s == "pressed" ? ReverieStyle.MenuTitleOpen : ReverieStyle.MenuTitle, CustomMinimumSize = new Vector2(0, ReverieTheme.MenuTitleHeight) });
        States(left, "Chip", s => new RvChip(Cap(s)));
        States(left, "Icon button", s => new RvIconButton(LucideIcons.Home, Cap(s)));
        States(left, "List row", s => new RvListRow(Cap(s), LucideIcons.Folder, "12", s == "normal" ? false : null) { Selected = s == "pressed", CustomMinimumSize = new Vector2(120, ReverieTheme.ListRow) });

        var toggles = Row(left, "Switch");
        foreach (var (on, state) in new[] { (true, "normal"), (false, "normal"), (true, "focus"), (false, "disabled") })
        {
            var sw = new RvSwitch(on ? "On" : "Off", on);
            toggles.AddChild(sw);
            if (state == "focus") _forced.Add((sw, "focus"));
            if (state == "disabled") sw.Disabled = true;
        }
        var sliderRow = Row(left, "Slider");
        sliderRow.AddChild(new RvSlider("Interface size", 75, 200, 125, " %") { CustomMinimumSize = new Vector2(260, 0) });
        var disabledSlider = new RvSlider("Disabled", 0, 100, 40) { CustomMinimumSize = new Vector2(160, 0) };
        disabledSlider.Slider.Editable = false;
        disabledSlider.Modulate = disabledSlider.Modulate with { A = 0.45f };
        sliderRow.AddChild(disabledSlider);
        var segRow = Row(left, "Segmented");
        segRow.AddChild(new RvSegmented("Show", ["All", "Recent", "Worn"], 0));
        var focusedSeg = new RvSegmented("Theme", ["Dark", "Light"], 1);
        segRow.AddChild(focusedSeg);
        _forced.Add((focusedSeg.Segments[1], "focus"));
        Row(left, "Stepper").AddChild(new RvStepper(["Drop a folder", "Check the sorting", "Name and save"], 1) { CustomMinimumSize = new Vector2(480, 28) });
        var loc = Row(left, "Location");
        var display = new RvLocationField(strings) { CustomMinimumSize = new Vector2(320, ReverieTheme.LocationHeight) };
        display.Show(LocationField.Display("Example Region", "Example Parcel", new System.Numerics.Vector3(128, 64, 22), 0));
        loc.AddChild(display);
        var editing = new RvLocationField(strings) { Name = "location_editing", CustomMinimumSize = new Vector2(260, ReverieTheme.LocationHeight) };
        loc.AddChild(editing);
        _forced.Add((editing, "edit"));
        var status = Row(left, "Status");
        status.AddChild(new RvStatusChip("Example Grid", LucideIcons.Grid));
        status.AddChild(new RvStatusChip(strings.Get("status.connected")));
        var off = new RvStatusChip(strings.Get("status.disconnected")) { DotColour = "muted" };
        status.AddChild(off);
        Row(left, "Other scripts").AddChild(new Label { Text = TestRender.OtherScripts, ThemeTypeVariation = ReverieStyle.Status });

        right.AddChild(new RvCard("Animation Overrider", "Built in. Plays your own animations for standing, walking, sitting and flying."));
        right.AddChild(new RvCard("Example add-on", "Full access. Only turn it on if you trust who made it.", warning: true));
        var sidebar = new RvSidebarList("Sets") { CustomMinimumSize = new Vector2(0, 0) };
        sidebar.Add("Default set", LucideIcons.Overrider, selected: true);
        sidebar.Add("Dance set", LucideIcons.Overrider);
        sidebar.Add("New set", LucideIcons.Add);
        right.AddChild(sidebar);
        var holder = new Control { CustomMinimumSize = new Vector2(400, 200), ClipContents = false };
        string? kept = null;
        var chrome = new RvWindowChrome(strings, "Inventory", new Vector2(400, 200), new Vector2(240, 160), () => kept, t => kept = t);
        holder.AddChild(chrome);
        right.AddChild(holder);
        chrome.Content.AddChild(new Label { Text = TestRender.ExampleContent, ThemeTypeVariation = ReverieStyle.Status });
        var menuHolder = new Control { Name = "menu_holder", CustomMinimumSize = new Vector2(0, 1) };
        right.AddChild(menuHolder);
    }

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Shows each forced state with the theme's own face for it, and opens an example dropdown.</summary>
    public void ForceStates()
    {
        foreach (var (control, state) in _forced)
        {
            switch (state)
            {
                case "hover" when control is Button b:
                    b.AddThemeStyleboxOverride("normal", b.GetThemeStylebox("hover"));
                    break;
                case "pressed" when control is Button b && (string)b.ThemeTypeVariation is not (ReverieStyle.MenuTitleOpen or ReverieStyle.ListRowSelected):
                    b.AddThemeStyleboxOverride("normal", b.GetThemeStylebox("pressed"));
                    break;
                case "disabled" when control is Button b:
                    b.Disabled = true;
                    break;
                case "focus":
                    var ring = new Panel { MouseFilter = MouseFilterEnum.Ignore };
                    var box = control is Button fb ? fb.GetThemeStylebox("focus") : ReverieStyle.FocusRing(ReverieStyle.TokensFor(UiTheme.Tokens), (int)(ReverieTheme.SwitchHeight / 2));
                    ring.AddThemeStyleboxOverride("panel", box);
                    ring.SetAnchorsPreset(LayoutPreset.FullRect);
                    if (control is RvSwitch)
                        ring.Size = new Vector2(ReverieTheme.SwitchWidth, ReverieTheme.SwitchHeight);
                    control.AddChild(ring);
                    break;
                case "edit" when control is RvLocationField f:
                    f.BeginEdit();
                    f.Edit.Text = TestRender.ExampleAddress;
                    break;
            }
        }
        // an example dropdown with icons, shortcut text, a separator, a check mark and a submenu arrow, open at the lower right
        var popup = new PopupMenu { ThemeTypeVariation = ReverieStyle.Popup };
        AddChild(popup);
        RvMenuBar.Fill(popup, TestRender.ExampleMenus(_strings)[3].Entries());
        popup.Popup(new Rect2I(1000, 520, 0, 0));
    }
}
