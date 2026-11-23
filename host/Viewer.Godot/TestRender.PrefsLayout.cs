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
    /// <summary>The widest a Preferences page's content may be, in interface units (UI-STANDARD U1: about 640 at 100 %).</summary>
    private const float PrefsContentMax = 640;

    /// <summary>The most a label's right edge may be from its control (U1: a fixed label column, then the control right next to it).</summary>
    private const float PrefsLabelGap = 16;

    /// <summary>
    /// The layout standard applied to every Preferences page as drawn, at the interface sizes 100, 150 and 200 % and at the window's
    /// default width and at one and a half times it: no row label is clipped or wrapped, every control sits beside its label (gap at most
    /// 16 units), the content is no wider than 640 units from the page's left edge, and a closed "More" section shows its title and its
    /// one-line summary whole. Failures go to the case's failures with the page and size.
    /// </summary>
    private async Task PreferencesLayout(StringTable strings, ThemeTokens tokens)
    {
        var problems = new List<string>();
        var table = new List<string>();
        foreach (int pct in new[] { 100, 150, 200 })
        {
            float s = pct / 100f;
            UiTheme.Apply(tokens, pct);
            var rig = MakeRig(new Vector2I(UiWidth, UiHeight));
            rig.Viewport.Size2DOverride = new Vector2I((int)MathF.Round(UiWidth / s), (int)MathF.Round(UiHeight / s));
            rig.Viewport.Size2DOverrideStretch = true;
            var shell = new ShellRoot(new ShellController(strings, () => { })) { ScaleSink = _ => { }, FixedClockText = TestClockText };
            rig.Viewport.AddChild(shell);
            var settings = new SettingsService(null);
            ThemeStore.Save(settings, "render", ThemeChoice.Default with { Preset = ThemePresets.Classic });
            var prefs = PreferencesFeature.Install(shell, settings, () => 0, () => false);
            var floaters = shell.Shell.Floaters;
            floaters.Open(PreferencesFeature.FloaterId);
            await Frames(8);
            var spec = floaters.SpecOf(PreferencesFeature.FloaterId)!;
            float height = floaters.RectOf(PreferencesFeature.FloaterId)?.H ?? spec.DefaultSize.H;
            foreach (float factor in new[] { 1f, 1.5f })
            {
                float w = Math.Min(spec.DefaultSize.W * factor, floaters.Bounds.W);
                floaters.Move(PreferencesFeature.FloaterId, floaters.Bounds.X, floaters.Bounds.Y);
                floaters.Resize(PreferencesFeature.FloaterId, w, height);
                foreach (var category in PreferencesPanel.Categories)
                {
                    prefs.ShowCategory(category);
                    await Frames(3);
                    if (prefs.FindChild("page_" + category, true, false) is not Control page || !page.IsVisibleInTree())
                        continue;   // the Interface page is its own scroll area; it has no page_ node
                    var found = PrefsPageProblems(page, category);
                    table.Add($"at {pct} %, width {w:F0}, page {category}: {found.Count} problems");
                    problems.AddRange(found.Select(p => $"at {pct} %, width {w:F0}, page {category}: {p}"));
                }
            }
            rig.Viewport.QueueFree();
            await Frames(2);
        }
        _result["preferences_layout"] = new { pages = table, problems = problems.Count };
        foreach (var line in problems.Take(12))
            _failures.Add("preferences layout: " + line);
        if (problems.Count > 12)
            _failures.Add($"preferences layout: {problems.Count - 12} more (preferences_layout in the result)");
    }

    /// <summary>A sentence under a setting or a section header (a note, a summary, a status): it may take the room it needs and wrap; the checks on row labels leave it.</summary>
    private static bool IsNote(Label label)
    {
        string name = label.Name.ToString();
        return name.StartsWith("note_", StringComparison.Ordinal) || name.StartsWith("account_", StringComparison.Ordinal) || name.StartsWith("backend_", StringComparison.Ordinal)
            || name.EndsWith("_summary", StringComparison.Ordinal) || name.EndsWith("_status", StringComparison.Ordinal) || name.StartsWith("sound_note", StringComparison.Ordinal);
    }

    private static List<string> PrefsPageProblems(Control page, string category)
    {
        var found = new List<string>();
        var view = page.GetGlobalRect();
        float rightmost = 0;
        foreach (var node in page.FindChildren("*", "Control", true, false).OfType<Control>())
        {
            if (!node.IsVisibleInTree() || node is Container || node.Size.X <= 0)
                continue;
            if (node is Label label && label.Text.Length > 0 && !IsNote(label))
            {
                var font = label.GetThemeFont("font");
                int size = label.GetThemeFontSize("font_size");
                float textWidth = font.GetStringSize(label.Text, HorizontalAlignment.Left, -1, size).X;
                if (label.AutowrapMode != TextServer.AutowrapMode.Off && label.GetLineCount() > 1)
                    found.Add($"label '{label.Text}' wraps onto {label.GetLineCount()} lines");
                else if (label.Size.X + 0.5f < textWidth)
                    found.Add($"label '{label.Text}' is cut off ({label.Size.X:F0} wide, text {textWidth:F0})");
            }
            if (node is Button { Text.Length: > 0 } button && node.Name.ToString().Contains("_toggle", StringComparison.Ordinal))
            {
                var font = button.GetThemeFont("font");
                float textWidth = font.GetStringSize(button.Text, HorizontalAlignment.Left, -1, button.GetThemeFontSize("font_size")).X;
                if (button.Size.X + 0.5f < textWidth)
                    found.Add($"section title '{button.Text}' is cut off ({button.Size.X:F0} wide, text {textWidth:F0})");
            }
            rightmost = Math.Max(rightmost, node.GetGlobalRect().End.X);
        }
        // one setting a row: the control right next to its label
        foreach (var row in page.FindChildren("row_*", "HBoxContainer", true, false).OfType<HBoxContainer>())
        {
            if (!row.IsVisibleInTree() || row.GetChildCount() < 2 || row.GetChild(0) is not Label caption || row.GetChild(1) is not Control control)
                continue;
            float gap = control.GetGlobalRect().Position.X - caption.GetGlobalRect().End.X;
            if (gap > PrefsLabelGap)
                found.Add($"'{caption.Text}' is {gap:F0} units from its control (at most {PrefsLabelGap:F0})");
        }
        if (rightmost - view.Position.X > PrefsContentMax + 24)
            found.Add($"the content is {rightmost - view.Position.X:F0} units wide (at most {PrefsContentMax:F0} plus padding)");
        // a closed section says what it holds, in one line that fits
        foreach (var summary in page.FindChildren("section_*_summary", "Label", true, false).OfType<Label>())
        {
            var holder = summary.GetParent()?.GetParent();
            var body = holder?.FindChild(summary.Name.ToString().Replace("_summary", "_body"), true, false) as Control;
            if (body is null || body.Visible)
                continue;
            float textWidth = summary.GetThemeFont("font").GetStringSize(summary.Text, HorizontalAlignment.Left, -1, summary.GetThemeFontSize("font_size")).X;
            if (summary.Text.Length == 0 || !summary.Visible)
                found.Add($"closed section '{summary.Name}' shows no summary");
            else if (summary.Size.X + 0.5f < textWidth)
                found.Add($"the summary '{summary.Text}' is cut off ({summary.Size.X:F0} wide, text {textWidth:F0})");
        }
        return found;
    }
}
