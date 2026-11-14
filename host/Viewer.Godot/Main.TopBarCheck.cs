// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

#if NV_AUTOMATION
using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The top bar and the dock as a person meets them (development builds only): the window title, the menu titles, a hover over the
/// titles with nothing open, a click on a title and the entries it shows with their icons, the location field's text, a typed place
/// submitted as Enter does, the status area's text and whether it takes clicks, and the dock's buttons with their names, a click on a
/// button and its menu.
/// </summary>
public partial class Main
{
    public async Task<object?> TopBarCall(string action, string? text, int index)
    {
        if (_shell is null)
            return null;
        var bar = _shell.TopBar;
        var menus = bar.Menus;
        async Task Frames(int n)
        {
            for (int i = 0; i < n; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        switch (action)
        {
            case "hover_all":
            {
                // the pointer moves across every title with nothing open: no menu may open
                menus.CloseMenus();
                for (int i = 0; i < menus.Titles.Count; i++)
                {
                    menus.HoverTitle(i);
                    await Frames(2);
                }
                return new { opened = _shell.VisibleMenus(), open = _shell.OpenMenu };
            }
            case "click":
            {
                menus.CloseMenus();
                await Frames(1);
                menus.ClickTitle(Math.Clamp(index, 0, menus.Titles.Count - 1));
                await Frames(3);
                var popup = menus.Popups[Math.Clamp(index, 0, menus.Popups.Count - 1)];
                var items = Enumerable.Range(0, popup.ItemCount).Where(i => !popup.IsItemSeparator(i))
                    .Select(i => new { text = popup.GetItemText(i), icon = popup.GetItemIcon(i) is not null, disabled = popup.IsItemDisabled(i) }).ToArray();
                var shown = _shell.VisibleMenus();
                menus.CloseMenus();
                return new { title = menus.Titles[Math.Clamp(index, 0, menus.Titles.Count - 1)].Text, open = shown, items };
            }
            case "type":
            {
                // the location field clicked, a place typed, Enter
                bar.Location.BeginEdit();
                await Frames(1);
                bar.Location.Edit.Text = text ?? "";
                bar.Location.Submit(text ?? "");
                await Frames(2);
                return new { editing = bar.Location.Editing };
            }
        }
        var status = new[] { bar.Grid, bar.Connection }.Select(c => new { text = c.Text, takes_mouse = c.MouseFilter != Control.MouseFilterEnum.Ignore, focus = c.FocusMode != Control.FocusModeEnum.None }).ToArray();
        var time = bar.FindChild("time", true, false) as Label;
        var zone = bar.FindChild("zone_status", true, false) as Control;
        return new
        {
            window_title = GetWindow().Title,
            titles = menus.Titles.Select(t => t.Text).ToArray(),
            app_mark = menus.Titles.Count > 0 && menus.Titles[0].Icon is not null,
            location = bar.Location.DisplayText,
            location_shown = bar.Location.IsVisibleInTree(),
            status,
            time = time?.Text,
            status_zone_takes_mouse = zone?.MouseFilter != Control.MouseFilterEnum.Ignore,
            dock = _shell.Dock.Buttons.Select(b => new { id = b.Key.Id, owner = b.Key.Owner, name = b.Value.Text, icon = b.Value.Icon is not null, on = b.Key.On }).ToArray(),
            dock_rows = _shell.DockRows,
            quick_controls = _shell.ToolbarRow.GetChildCount(),
        };
    }

    /// <summary>A dock button clicked (by id and add-on), or its menu entry chosen (index ≥ 0), as the pointer does it.</summary>
    public object? DockCall(string id, string? owner, int menuIndex)
    {
        if (_shell is null)
            return null;
        var item = _shell.DockModel.Item(id, string.IsNullOrEmpty(owner) ? null : owner);
        if (item is null || !_shell.Dock.Buttons.TryGetValue(item, out var button))
            return new { refused = "no_button" };
        if (menuIndex < 0)
            button.EmitSignal(BaseButton.SignalName.Pressed);
        else
            _shell.DockMenuChoose(item, menuIndex);
        return new { id, name = item.Shown(_shell.Shell.Strings), on = item.On, menu = item.Menu.Select(m => m.Separator ? "-" : m.Label).ToArray() };
    }
}
#endif
