// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// --tr-uichrome=PCT:WxH [--tr-golden=file]: the in-world frame in a window of W x H pixels at an interface size of PCT %, drawn the way
    /// the window applies its content scale (the viewport's 2D size is W/s x H/s and stretched). The run FAILS unless the menu row, the
    /// navigation bar, the chat bar, the toolbar and the clock are wholly inside the window, every toolbar button that shows is inside the
    /// toolbar, every button is either shown or behind the More button, and each open floating window lies inside the main view.
    /// </summary>
    private async Task<int> UiChromeRun(string spec, string? golden)
    {
        var parts = spec.Split(':', 'x');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pct)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int w) || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int h))
            throw new ArgumentException("--tr-uichrome must be PCT:WxH, e.g. 200:1152x648");
        var strings = StringTable.English();
        UiTheme.Apply(ThemePresets.Get(ThemePresets.Classic), pct);
        var rig = MakeRig(new Vector2I(w, h));
        rig.Viewport.Size2DOverride = new Vector2I((int)MathF.Round(w * 100f / pct), (int)MathF.Round(h * 100f / pct));
        rig.Viewport.Size2DOverrideStretch = true;
        var shell = new ShellRoot(new ShellController(strings, () => { })) { ScaleSink = _ => { }, FixedClockText = TestClockText };   // the override above stands in for the window's content scale
        rig.Viewport.AddChild(shell);
        var settings = new SettingsService(null);
        var backdrop = new TextureRect { Name = "backdrop", Texture = Backdrop(), StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        shell.MainView.AddChild(backdrop);
        var chat = ChatFeature.Install(shell, settings);
        PeopleFeature.Install(shell, settings, chat.Conversations);
        InventoryFeature.Install(shell);
        OutfitFeature.Install(shell);
        TravelFeature.Install(shell, settings);
        NotificationFeature.Install(shell);
        InteractFeature.Install(shell);
        PreferencesFeature.Install(shell, settings);
        shell.ShowWorldRows(true);
        shell.SetConnected(true);
        shell.SetBrand(ExampleBranding().Name);
        shell.Navigation.SetLocation("Example Region");
        shell.SetUnread(3);
        shell.Shell.Floaters.Open(ChatFeature.NearbyId);
        shell.Shell.Floaters.Open(PeopleFeature.PeopleId);
        shell.Shell.Floaters.Open(PreferencesFeature.FloaterId);
        await Frames(12);

        var view = shell.GetViewportRect();
        var viewport = new Rect2(view.Position, view.Size);
        const float slack = 0.5f;
        var fails = new List<string>();
        Rect2? Rect(string name)
        {
            var node = shell.FindChild(name, true, false) as Control;
            return node is { Visible: true } && node.IsVisibleInTree() ? node.GetGlobalRect() : null;
        }
        bool Inside(Rect2 outer, Rect2 inner) =>
            inner.Position.X >= outer.Position.X - slack && inner.Position.Y >= outer.Position.Y - slack
            && inner.End.X <= outer.End.X + slack && inner.End.Y <= outer.End.Y + slack;
        var rects = new Dictionary<string, object?>();
        foreach (var name in new[] { "menu_row", "nav_bar", "chat_bar", "toolbar_bar", "clock", "connection" })
        {
            var r = Rect(name);
            rects[name] = r is { } q ? new[] { q.Position.X, q.Position.Y, q.Size.X, q.Size.Y } : null;
            if (r is not { } rr)
                fails.Add($"{name} is not shown");
            else if (!Inside(viewport, rr))
                fails.Add($"{name} {rr} is not inside the window {viewport}");
        }
        // the rows stack top to bottom without drawing over each other (a bar taller than the room the layout gave it covers its neighbour)
        var column = new[] { "menu_row", "nav_bar", "main_view", "chat_bar", "toolbar_bar" };
        for (int i = 0; i + 1 < column.Length; i++)
            if (Rect(column[i]) is { } upper && Rect(column[i + 1]) is { } lower && upper.End.Y > lower.Position.Y + slack)
                fails.Add($"{column[i]} (bottom {upper.End.Y}) covers {column[i + 1]} (top {lower.Position.Y})");
        if (Rect("menu_row") is { } menuRow)
            foreach (var name in new[] { "clock", "connection", "menu_bar" })
                if (Rect(name) is { } child && !Inside(menuRow, child))
                    fails.Add($"{name} {child} is not inside the menu row {menuRow}");
        int total = shell.Shell.Toolbar.Buttons.Count, shown = 0;
        bool more = false;
        if (Rect("toolbar_bar") is { } toolbarBar)
        {
            foreach (var b in shell.Shell.Toolbar.Buttons)
                if (Rect(b.Id) is { } br)
                {
                    shown++;
                    if (!Inside(toolbarBar, br))
                        fails.Add($"toolbar button {b.Id} {br} is not inside the toolbar {toolbarBar}");
                }
            if (Rect("toolbar_more") is { } moreRect)
            {
                more = true;
                if (!Inside(toolbarBar, moreRect))
                    fails.Add($"the More button {moreRect} is not inside the toolbar {toolbarBar}");
            }
            int hidden = shell.HiddenToolbarButtons;
            if (shown + hidden != total)
                fails.Add($"{total} toolbar buttons, {shown} shown and {hidden} behind More");
            if ((hidden > 0) != more)
                fails.Add(hidden > 0 ? "buttons are hidden but the More button is not shown" : "the More button is shown with nothing behind it");
        }
        if (Rect("main_view") is { } mainView)
            foreach (var f in shell.Shell.Floaters.OpenFloaters)
                if (Rect("floater_" + f.Id) is { } fr)
                {
                    if (!Inside(mainView, fr))
                        fails.Add($"window {f.Id} {fr} is not inside the main view {mainView}");
                }
                else
                    fails.Add($"window {f.Id} is open but not shown");
        // the Preferences window keeps its Reset, Cancel and Save buttons in view at every interface size (a window that needs more room than
        // the main view has must shrink its content, not push the buttons below the window's edge)
        if (shell.FindChild("floater_" + PreferencesFeature.FloaterId, true, false) is Control prefsWindow)
        {
            var prefsRect = prefsWindow.GetGlobalRect();
            foreach (var name in new[] { "reset", "cancel", "save" })
                if (prefsWindow.FindChild(name, true, false) is Control button)
                {
                    var br = button.GetGlobalRect();
                    if (!button.IsVisibleInTree() || !Inside(prefsRect, br))
                        fails.Add($"the Preferences button {name} {br} is not inside its window {prefsRect}");
                }
                else
                    fails.Add($"the Preferences button {name} is not found");
        }
        _result["ui_chrome"] = new Dictionary<string, object?>
        {
            ["percent"] = pct, ["window"] = $"{w}x{h}", ["logical"] = $"{view.Size.X}x{view.Size.Y}", ["toolbar_buttons"] = total, ["shown"] = shown, ["more"] = more,
            ["rects"] = rects, ["problems"] = fails,
        };
        foreach (var f in fails)
            _failures.Add("chrome: " + f);

        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        Directory.CreateDirectory(_out);
        img.SavePng(Path.Combine(_out, _tag + ".png"));
        if (golden is not null)
        {
            if (!Path.IsPathFullyQualified(golden) || !File.Exists(golden))
                _failures.Add("golden not found: " + Path.GetFileName(golden));
            else
            {
                var g = Image.LoadFromFile(golden);
                g.Convert(Image.Format.Rgba8);
                var diff = g.GetWidth() != w || g.GetHeight() != h
                    ? new DiffResult(false, g.GetWidth(), g.GetHeight(), 0, 0, 0, 0, "golden size differs")
                    : ImageDiff.Compare(img.GetData(), g.GetData(), w, h, 4, DiffTolerance.Terrain);
                _result["diff"] = new Dictionary<string, object?>
                {
                    ["golden"] = Path.GetFileName(golden), ["pass"] = diff.Pass, ["differing"] = diff.Differing,
                    ["differing_fraction"] = diff.DifferingFraction, ["mean_abs_error"] = Math.Round(diff.MeanAbsError, 4),
                    ["max_channel_error"] = diff.MaxChannelError, ["tolerance"] = DiffTolerance.Terrain, ["summary"] = diff.ToString(),
                };
                if (!diff.Pass)
                    _failures.Add("diff " + diff);
            }
        }
        shell.QueueFree();
        UiTheme.Apply(ThemePresets.Get(ThemePresets.Classic), 100);
        return _failures.Count == 0 ? 0 : 1;
    }
}
