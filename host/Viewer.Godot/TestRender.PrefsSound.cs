// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

namespace Viewer.Godot;

public partial class TestRender
{
    private static readonly (string Row, string Level, string Mute)[] SoundRows =
    [
        ("master", "audio_level_master", "audio_mute_master"), ("sounds", "audio_level_sounds", "audio_mute_sounds"),
        ("ambient", "audio_level_ambient", "audio_mute_ambient"), ("interface", "ui_sound_volume", "ui_sound_mute_all"),
    ];

    /// <summary>
    /// The layout standard (UI-STANDARD) applied to the Preferences > Sound page as drawn: a control beside its label (U1), one row a volume (U2),
    /// a value on every slider (U3), sections closed or open as asked and a one-line summary on a closed one (U6, U7, U8), no "not built"
    /// wording (U9), and no control nearer the page's edge than 12 units (U10). Failures go to the case's failures.
    /// </summary>
    private void SoundPageStandard(ShellRoot shell, bool open, int pct, Image img)
    {
        var problems = new List<string>();
        if (shell.FindChild("page_sound", true, false) is not PrefPageView page)
        {
            _failures.Add("the Sound page is not in the window");
            return;
        }
        var view = page.GetGlobalRect();
        T? Find<T>(string name) where T : Node => page.FindChild(name, true, false) as T;
        float CentreY(Control c) => c.GetGlobalRect().Position.Y + c.GetGlobalRect().Size.Y / 2;
        var rows = new List<string>();

        foreach (var (row, level, mute) in SoundRows)
        {
            var name = Find<Label>("volume_" + row + "_name");
            var slider = Find<HSlider>("pref_" + level);
            var value = Find<Label>("volume_" + row + "_value");
            var button = Find<Button>("pref_" + mute);
            if (name is null || slider is null || value is null || button is null)
            {
                problems.Add($"U2: the {row} row lacks its name, slider, value or mute button");
                continue;
            }
            float spread = new[] { CentreY(name), CentreY(slider), CentreY(value), CentreY(button) } is { } ys ? ys.Max() - ys.Min() : 0;
            float gap = slider.GetGlobalRect().Position.X - name.GetGlobalRect().End.X;
            rows.Add($"{row}: spread {spread:F1}, name-to-slider gap {gap:F1}, value '{value.Text}', button '{button.Text}'");
            if (spread > 4)
                problems.Add($"U2: the {row} row's parts are {spread:F1} units apart vertically");
            if (gap > 16 || gap < 0)
                problems.Add($"U1: the {row} slider is {gap:F1} units from its name");
            if (!Regex.IsMatch(value.Text, @"^\d+%$"))
                problems.Add($"U3: the {row} slider shows '{value.Text}' instead of its value");
        }

        // sections: closed until opened, with a one-line summary while closed
        foreach (var (id, pattern) in new[] { ("more", @"^Money sound above \S+, pain sound above \S+$"), ("interface", @"^Grid's sounds, \d+ of \d+ on$") })
        {
            var body = Find<Control>("section_" + id + "_body");
            var summary = Find<Label>("section_" + id + "_summary");
            if (body is null || summary is null)
            {
                problems.Add($"U7: the {id} section is missing");
                continue;
            }
            if (body.Visible != open)
                problems.Add($"U6: the {id} section is {(body.Visible ? "open" : "closed")} when it should be {(open ? "open" : "closed")}");
            if (!open && !Regex.IsMatch(summary.Text, pattern))
                problems.Add($"U8: the closed {id} section says '{summary.Text}'");
            rows.Add($"{id}: body visible {body.Visible}, summary '{summary.Text}' visible {summary.Visible}");
        }

        // the table: one line a sound
        int lines = 0;
        float worstLine = 0;
        foreach (var name in page.FindChildren("sound_*_name", "Label", true, false).OfType<Label>())
        {
            if (!name.IsVisibleInTree())
                continue;
            string key = name.Name.ToString()["sound_".Length..^"_name".Length];
            var parts = new Control?[] { Find<Control>("sound_" + key + "_cell"), Find<Control>("sound_" + key + "_on"), Find<Control>("sound_" + key + "_volume"),
                Find<Control>("sound_" + key + "_value"), Find<Control>("sound_" + key + "_play") };
            float spread = parts.Where(p => p is not null).Select(p => Math.Abs(CentreY(p!) - CentreY(name))).DefaultIfEmpty(0).Max();
            worstLine = Math.Max(worstLine, spread);
            lines++;
            if (spread > 4)
                problems.Add($"U7: the line of '{name.Text}' is {spread:F1} units tall across its parts");
        }
        rows.Add($"table: {lines} lines visible, worst spread {worstLine:F1}");

        // wording and edges
        var edge = new List<string>();
        foreach (var node in page.FindChildren("*", "Control", true, false).OfType<Control>())
        {
            if (!node.IsVisibleInTree())
                continue;
            string text = node switch { Label l => l.Text, Button b => b.Text, _ => "" };
            if (Regex.IsMatch(text, "not built|coming soon|TODO", RegexOptions.IgnoreCase))
                problems.Add($"U9: '{text}' says something is not built");
            if (node is Container || node.Size.X <= 0 || node is Separator || node is ScrollBar)
                continue;
            var r = node.GetGlobalRect();
            if (r.Position.X < view.Position.X + 12 - 0.5f)
                edge.Add($"{node.Name} starts {r.Position.X - view.Position.X:F1} from the left edge");
            if (r.End.X > view.End.X - 12 + 0.5f && r.Position.X < view.End.X)
                edge.Add($"{node.Name} ends {view.End.X - r.End.X:F1} from the right edge");
        }
        foreach (var e in edge.Take(6))
            problems.Add("U10: " + e);

        // U4: an unticked box, measured on the picture: the strongest colour on the row through the box against the colour inside it
        if (Find<CheckBox>("pref_" + Viewer.Shell.Sound.AudioLevels.Mono.Key) is { ButtonPressed: false } unticked && view.Encloses(unticked.GetGlobalRect()))
        {
            var box = unticked.GetGlobalRect();
            float k = pct / 100f;
            int y = (int)((box.Position.Y + box.Size.Y / 2) * k);
            int left = (int)(box.Position.X * k), right = (int)((box.Position.X + 26) * k);
            Color Px(int x) => img.GetPixel(Math.Clamp(x, 0, img.GetWidth() - 1), Math.Clamp(y, 0, img.GetHeight() - 1));
            var inside = Px(left + (int)(11 * k));
            Viewer.Shell.UiColour Ui(Color c) => new(c.R, c.G, c.B);
            double best = 1;
            for (int x = left; x <= right; x++)
                best = Math.Max(best, Viewer.Shell.UiColour.Contrast(Ui(Px(x)), Ui(inside)));
            rows.Add($"unticked box: outline against the inside {best:F2}:1");
            if (best < Viewer.Shell.CheckColours.MinContrast)
                problems.Add($"U4: the unticked box's outline is {best:F2}:1 against its inside (needs 3:1)");
        }
        else if (!open)
            problems.Add("U4: no unticked check box found to measure");   // with the sections open the page is scrolled to the table, away from the box

        _result["sound_standard"] = new { pct, open, rows, edge_problems = edge.Count, problems };
        _failures.AddRange(problems.Select(p => "Sound page: " + p));
    }
}
