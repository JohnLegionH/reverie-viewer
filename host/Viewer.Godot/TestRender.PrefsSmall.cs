// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
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
        int examined = 0, statesChecked = 0, sectionToggles = 0;
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
            // the page as it opens, then with every collapsible section open, then with every one closed: each state must fit
            var toggles = scroll.FindChildren("section_*_toggle", "Button", true, false).OfType<Button>().ToList();
            var states = new List<(string Name, bool? Open)> { ("as opened", null) };
            if (toggles.Count > 0)
            {
                states.Add(("all sections open", true));
                states.Add(("all sections closed", false));
            }
            sectionToggles += toggles.Count;
            var startOpen = toggles.Select(t => t.ButtonPressed).ToList();
            foreach (var (state, open) in states)
            {
                if (open is { } want)
                {
                    foreach (var toggle in toggles)
                        if (toggle.ButtonPressed != want)
                            toggle.ButtonPressed = want;
                    await Frames(3);
                }
                statesChecked++;
                var box = scroll.GetGlobalRect();
                if (scroll.GetHScrollBar().Visible)
                    problems.Add($"{at} ({state}): a sideways scroll bar is shown (content {scroll.GetHScrollBar().MaxValue:F0} wide, window {box.Size.X:F0})");
                foreach (var node in scroll.FindChildren("*", "Control", true, false).OfType<Control>())
                {
                    if (!node.IsVisibleInTree() || node is ScrollBar || node.Size.X <= 0)
                        continue;
                    var r = node.GetGlobalRect();
                    if (r.End.X > box.End.X + slack || r.Position.X < box.Position.X - slack)
                        problems.Add($"{at} ({state}): {node.GetType().Name} '{node.Name}' spans x {r.Position.X:F0} to {r.End.X:F0}, outside the content area {box.Position.X:F0} to {box.End.X:F0}");
                }
            }
            for (int t = 0; t < toggles.Count; t++)
                if (toggles[t].ButtonPressed != startOpen[t])
                    toggles[t].ButtonPressed = startOpen[t];
            await Frames(2);
            var area = scroll.GetGlobalRect();
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
        if (sectionToggles == 0)
            problems.Add($"at {pct} % in {window}: no collapsible section was found on any page, so none was opened");
        if (examined != PreferencesPanel.Categories.Length)
            problems.Add($"at {pct} % in {window}: {examined} pages were examined, expected {PreferencesPanel.Categories.Length}");
        if (_result.TryGetValue("ui_chrome", out var chrome) && chrome is Dictionary<string, object?> map)
            map["prefs_small"] = new { examined, states_checked = statesChecked, section_toggles = sectionToggles, count = problems.Count, problems };
        foreach (var p in problems.Take(12))
            _failures.Add("preferences small window: " + p);
        if (problems.Count > 12)
            _failures.Add($"preferences small window: {problems.Count - 12} more");
    }

    /// <summary>
    /// Preferences opened at 100 % in the smallest window, then the interface size changed with the window left alone (as Preferences >
    /// Interface does it): at 150 % and 200 % the drop-down must stand in for the category list with no sideways scroll bar, and back at
    /// 100 % the list must return. The keyboard focus must follow the switch to the category control now shown.
    /// </summary>
    private async Task PreferencesLayoutSwitch(ShellRoot shell, PreferencesPanel prefs, SubViewport viewport, int w, int h)
    {
        var problems = new List<string>();
        // the window's content scale, as the real window applies it: the logical size of the view follows the interface size
        shell.ScaleSink = factor =>
        {
            viewport.Size2DOverride = new Vector2I((int)MathF.Round(w / factor), (int)MathF.Round(h / factor));
            viewport.Size2DOverrideStretch = true;
        };
        var dropdown = prefs.FindChild("categories_dropdown", true, false) as OptionButton;
        var list = prefs.FindChild("categories_scroll", true, false) as ScrollContainer;
        if (dropdown is null || list is null)
        {
            _failures.Add("preferences layout switch: the category list or the drop-down is not found");
            return;
        }
        prefs.ShowCategory("general");
        foreach (var target in new[] { 200, 150 })
        {
            string step = $"100 % to {target} % in {w}x{h}";
            ((Button)prefs.FindChild("category_general", true, false)!).GrabFocus();
            await Frames(2);
            if (dropdown.Visible || !list.IsVisibleInTree())
                problems.Add($"{step}: before the change the drop-down is shown or the list is hidden, expected the list only");
            if (list.GetViewport()?.GuiGetFocusOwner() != prefs.FindChild("category_general", true, false))
                problems.Add($"{step}: before the change the keyboard focus did not reach the category button");
            UiTheme.Apply(ThemePresets.Get(ThemePresets.Classic), target);
            await Frames(6);
            if (!dropdown.Visible || !dropdown.IsVisibleInTree() || list.Visible)
                problems.Add($"{step}: the drop-down is {(dropdown.IsVisibleInTree() ? "shown" : "not shown")} and the list is {(list.Visible ? "shown" : "hidden")}, expected the drop-down only");
            if (prefs.PageView(PrefPage.General) is { } page && FirstScroll(page) is { } scroll && scroll.GetHScrollBar().Visible)
                problems.Add($"{step}: a sideways scroll bar is shown ({scroll.GetHScrollBar().MaxValue:F0} wide in {scroll.Size.X:F0})");
            if (dropdown.GetViewport()?.GuiGetFocusOwner() != dropdown)
                problems.Add($"{step}: the keyboard focus is not on the drop-down");
            step = $"{target} % back to 100 % in {w}x{h}";
            UiTheme.Apply(ThemePresets.Get(ThemePresets.Classic), 100);
            await Frames(6);
            if (dropdown.Visible || !list.IsVisibleInTree())
                problems.Add($"{step}: the drop-down is {(dropdown.Visible ? "shown" : "hidden")} and the list is {(list.IsVisibleInTree() ? "shown" : "hidden")}, expected the list only");
            if (list.GetViewport()?.GuiGetFocusOwner() != prefs.FindChild("category_general", true, false))
                problems.Add($"{step}: the keyboard focus is not on the current category's button");
        }
        foreach (var p in problems)
            _failures.Add("preferences layout switch: " + p);
        if (_result.TryGetValue("ui_chrome", out var chrome) && chrome is Dictionary<string, object?> map)
            map["prefs_switch"] = new { count = problems.Count, problems };
    }

    /// <summary>
    /// The interface size drop-down: it offers 100, 125, 150, 175 and 200 %; a size that is not one of them (a saved 110 %) is kept as an
    /// entry of its own, shown as the choice, until another size is chosen; Cancel puts back the size that was in force when Preferences
    /// opened. (The same control is worked by pointer and keyboard events in tools\ui-size-live.ps1.)
    /// </summary>
    private async Task PreferencesSizeDropdown(PreferencesPanel prefs)
    {
        var problems = new List<string>();
        var dropdown = prefs.FindChild("scale_dropdown", true, false) as OptionButton;
        if (dropdown is null)
        {
            _failures.Add("preferences size drop-down: the size control 'scale_dropdown' is not found (the interface size is not a drop-down)");
            return;
        }
        string Items() => string.Join(" ", Enumerable.Range(0, dropdown.ItemCount).Select(i => dropdown.GetItemId(i).ToString(CultureInfo.InvariantCulture)));
        int start = prefs.Page.EffectiveScale;
        if (Items() != "100 125 150 175 200")
            problems.Add($"the drop-down offers '{Items()}', expected '100 125 150 175 200'");
        prefs.Page.SetScale(110);   // a saved size that is not on the list
        await Frames(3);
        if (Items() != "100 110 125 150 175 200" || dropdown.Selected < 0 || dropdown.GetItemId(dropdown.Selected) != 110)
            problems.Add($"a saved 110 % is not kept as its own entry (entries '{Items()}', shown {dropdown.GetItemId(Math.Max(0, dropdown.Selected))})");
        // the choice goes through the drop-down's own signal, as a pick from its list does
        int at150 = Enumerable.Range(0, dropdown.ItemCount).First(i => dropdown.GetItemId(i) == 150);
        dropdown.Select(at150);
        dropdown.EmitSignal(OptionButton.SignalName.ItemSelected, (long)at150);
        await Frames(3);
        if (Items() != "100 125 150 175 200" || dropdown.Selected < 0 || dropdown.GetItemId(dropdown.Selected) != 150 || UiTheme.ScalePercent != 150)
            problems.Add($"after choosing 150 % the odd entry stays or the choice is not shown (entries '{Items()}')");
        prefs.CancelEdits();
        await Frames(3);
        if (UiTheme.ScalePercent != start || dropdown.Selected < 0 || dropdown.GetItemId(dropdown.Selected) != start)
            problems.Add($"Cancel did not put back {start} % (in force {UiTheme.ScalePercent}, drop-down shows {dropdown.GetItemId(dropdown.Selected)})");
        if (_result.TryGetValue("ui_chrome", out var chrome) && chrome is Dictionary<string, object?> map)
            map["prefs_size_dropdown"] = new { count = problems.Count, problems };
        foreach (var p in problems)
            _failures.Add("preferences size drop-down: " + p);
    }

    private static ScrollContainer? FirstScroll(Control page) => page as ScrollContainer ?? page.FindChild("*", true, false) as ScrollContainer;
}
