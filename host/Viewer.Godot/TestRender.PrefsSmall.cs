// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// Preferences in the smallest supported window (1152x648) at the interface size now set: on every page there is no sideways scroll
    /// bar, every control lies inside the content area's width, every category can be reached from the category list (a button wholly
    /// inside the list, or the drop-down that stands in for the list when the window is too narrow for both) and selects its page, and on
    /// General the Language drop-down is whole. Problems go to the chrome result ("prefs_small") and the case's failures.
    /// </summary>
    private async Task PreferencesSmallWindow(ShellRoot shell, PreferencesPanel prefs, int pct, string window)
    {
        const float slack = 0.5f;
        var problems = new List<string>();
        int examined = 0;
        var dropdown = prefs.FindChild("categories_dropdown", true, false) as OptionButton;
        var list = prefs.FindChild("categories_scroll", true, false) as ScrollContainer;
        // the category list is a drop-down in a window too narrow for the list beside a page (150 % and 200 % here), the list otherwise
        bool expectDropdown = pct >= 150;
        if ((dropdown is { Visible: true }) != expectDropdown)
            problems.Add($"at {pct} % in {window}: the category drop-down is {(expectDropdown ? "not shown" : "shown")}, expected the opposite");
        if (list is { Visible: true } == expectDropdown)
            problems.Add($"at {pct} % in {window}: the category list is {(expectDropdown ? "shown" : "hidden")}, expected the opposite");
        foreach (var id in PreferencesPanel.Categories)
        {
            string at = $"at {pct} % in {window}, page {id}";
            // reach the page the way a person does: through the drop-down when it is shown, else the list's button
            prefs.ShowCategory(PreferencesPanel.Categories[0] == id ? PreferencesPanel.Categories[1] : PreferencesPanel.Categories[0]);
            await Frames(3);
            if (dropdown is { Visible: true } && dropdown.IsVisibleInTree())
            {
                int index = Array.IndexOf(PreferencesPanel.Categories, id);
                dropdown.Select(index);
                dropdown.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index);
            }
            else if (prefs.FindChild("category_" + id, true, false) is Button button && button.IsVisibleInTree() && list is not null
                && list.GetGlobalRect() is var listRect && button.GetGlobalRect() is var buttonRect
                && buttonRect.Position.Y >= listRect.Position.Y - slack && buttonRect.End.Y <= listRect.End.Y + slack)
                button.EmitSignal(BaseButton.SignalName.Pressed);
            else
            {
                problems.Add($"{at}: its category cannot be reached (no drop-down shown, and its button is hidden or cut by the list)");
                continue;
            }
            await Frames(3);
            if (dropdown is { Visible: true } && dropdown.Selected != Array.IndexOf(PreferencesPanel.Categories, id))
                problems.Add($"{at}: the drop-down shows item {dropdown.Selected}, not this category");
            if (prefs.CurrentCategory != id)
            {
                problems.Add($"{at}: choosing it from the category list showed '{prefs.CurrentCategory}'");
                continue;
            }
            ScrollContainer? scroll = id == "interface"
                ? prefs.FindChild("scroll", true, false) as ScrollContainer
                : Enum.TryParse<PrefPage>(id, true, out var pageId) ? prefs.PageView(pageId) : null;
            if (scroll is null || !scroll.IsVisibleInTree())
            {
                problems.Add($"{at}: the page is not shown");
                continue;
            }
            examined++;
            var area = scroll.GetGlobalRect();
            if (scroll.GetHScrollBar().Visible)
                problems.Add($"{at}: a sideways scroll bar is shown (content {scroll.GetHScrollBar().MaxValue:F0} wide, window {area.Size.X:F0})");
            foreach (var node in scroll.FindChildren("*", "Control", true, false).OfType<Control>())
            {
                if (!node.IsVisibleInTree() || node is ScrollBar || node.Size.X <= 0)
                    continue;
                var r = node.GetGlobalRect();
                if (r.End.X > area.End.X + slack || r.Position.X < area.Position.X - slack)
                    problems.Add($"{at}: {node.GetType().Name} '{node.Name}' spans x {r.Position.X:F0} to {r.End.X:F0}, outside the content area {area.Position.X:F0} to {area.End.X:F0}");
            }
            if (id == "general" && prefs.PageView(PrefPage.General)?.ControlFor(Settings.UiLanguageKey) is OptionButton language)
            {
                var r = language.GetGlobalRect();
                if (!language.IsVisibleInTree() || r.End.X > area.End.X + slack)
                    problems.Add($"{at}: the Language drop-down is cut off (right edge {r.End.X:F0}, content area ends {area.End.X:F0})");
                if (language.Size.X + slack < language.GetCombinedMinimumSize().X)
                    problems.Add($"{at}: the Language drop-down is narrower ({language.Size.X:F0}) than its longest item needs ({language.GetCombinedMinimumSize().X:F0})");
            }
            else if (id == "general")
                problems.Add($"{at}: the Language drop-down is not found");
        }
        if (examined != PreferencesPanel.Categories.Length)
            problems.Add($"at {pct} % in {window}: {examined} pages were examined, expected {PreferencesPanel.Categories.Length}");
        if (_result.TryGetValue("ui_chrome", out var chrome) && chrome is Dictionary<string, object?> map)
            map["prefs_small"] = new { examined, count = problems.Count, problems };
        foreach (var p in problems.Take(12))
            _failures.Add("preferences small window: " + p);
        if (problems.Count > 12)
            _failures.Add($"preferences small window: {problems.Count - 12} more");
    }
}
