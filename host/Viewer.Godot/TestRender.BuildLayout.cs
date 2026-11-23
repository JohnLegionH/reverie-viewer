// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>The Build window's label and control pairs (Placement, Shape, Look and Object), which sit side by side in a fixed label
    /// column (UI-STANDARD U1): label name, control name. A pair whose section is closed or whose row is hidden is not checked.</summary>
    private static readonly (string Label, string Control)[] LabelPairs =
    [
        ("prim_type_label", "prim_type"), ("path_cut_label", "path_cut_a"), ("hollow_label", "hollow_a"), ("hollow_shape_label", "hollow_shape"),
        ("twist_label", "twist_a"), ("taper_label", "taper_a"), ("top_shear_label", "top_shear_a"), ("slice_label", "slice_a"), ("dimple_label", "dimple_a"),
        ("profile_cut_label", "profile_cut_a"), ("hole_size_label", "hole_size_a"), ("radius_label", "radius_a"), ("revolutions_label", "revolutions_a"),
        ("skew_label", "skew_a"),
        ("texture_label", "texture_name"), ("colour_label", "colour"), ("transparency_label", "transparency"), ("glow_label", "glow"),
        ("mapping_label", "mapping"), ("scale_u_label", "scale_u"), ("scale_v_label", "scale_v"), ("per_metre_label", "per_metre"),
        ("texture_rotation_label", "texture_rotation"), ("offset_u_label", "offset_u"), ("offset_v_label", "offset_v"),
        ("name_label", "name"), ("description_label", "description"), ("creator_label", "creator"), ("owner_label", "owner"),
        ("click_action_label", "click_action"),
        ("next_owner", "next_modify"), ("for_sale_label", "for_sale"), ("fullbright_label", "fullbright"),
    ];

    /// <summary>The rows of three values (X, Y, Z): the label stands on its own line with the three boxes on one line below it (the official
    /// viewer's grouping; UI-STANDARD U1, exception for three-value rows).</summary>
    private static readonly string[] TripleRows = ["position", "size", "rotation"];

    /// <summary>Tick boxes whose row has a label in the column, with the box beside it (the label is a separate control, the box has no text).</summary>
    private static readonly (string Label, string Control)[] TickRows = [("for_sale_label", "for_sale"), ("fullbright_label", "fullbright"), ("next_owner", "next_modify")];

    /// <summary>
    /// Layout checks on the Build window as drawn in a build scene at interface size <paramref name="pct"/> (UI-STANDARD U1, U3 to U5, U8 to U12,
    /// U15): no label or button cut, controls beside their labels, nothing under the scroll bar, shared tick box styles, and each finding of
    /// the Build window (the Land segment gone, the sections fitting the view, the summary on the header line, the Contents list and its menu,
    /// the Create options, the texture shown by name, the search line). Failures go into the case's failure list.
    /// </summary>
    private void CheckBuildLayout(Control shell, string scene, int pct, EditPanel? edit, string size = "")
    {
        string tag = $"{scene}@{pct}{size}";
        if (shell.FindChild("edit_panel", true, false) is not Control panel || panel.FindChild("sections_scroll", true, false) is not ScrollContainer scroll)
        {
            _failures.Add($"{tag}: the Build window or its scroll area was not found");
            return;
        }
        const float k = 1f;   // the picture is the same window drawn larger: the nodes keep their logical sizes at every interface size
        var panelRect = panel.GetGlobalRect();
        var view = scroll.GetGlobalRect();

        bool Shown(Control c) => c.IsVisibleInTree();
        IEnumerable<T> All<T>() where T : Control => AllChildren(panel).OfType<T>().Where(Shown);

        // no label or value is cut: the text fits the label's box (a long id included)
        foreach (var l in All<Label>().Where(l => l.Text.Length > 0 && l.AutowrapMode == TextServer.AutowrapMode.Off))
        {
            float w = l.GetThemeFont("font").GetStringSize(l.Text, HorizontalAlignment.Left, -1, l.GetThemeFontSize("font_size")).X;
            if (w > l.Size.X + 1f)
                _failures.Add($"{tag}: label '{l.Name}' is cut: text {w:F0} px in a box of {l.Size.X:F0} px ('{l.Text}')");
        }
        // no button is cut or shrunk below its label
        foreach (var b in All<Button>())
            if (b.GetMinimumSize().X > b.Size.X + 0.5f)
                _failures.Add($"{tag}: button '{b.Name}' is shrunk: needs {b.GetMinimumSize().X:F0} px, has {b.Size.X:F0}");
        // nothing runs past the window's panel, or under the scroll bar
        foreach (var c in All<Control>())
            if (c.GetGlobalRect().End.X > panelRect.End.X + 0.5f)
                _failures.Add($"{tag}: '{c.Name}' runs past the right edge of the Build window ({c.GetGlobalRect().End.X:F0} against {panelRect.End.X:F0})");
        var bar = scroll.GetVScrollBar();
        if (bar.IsVisibleInTree())
            foreach (var card in scroll.FindChildren("section_card_*", "", true, false).OfType<Control>().Where(Shown))
                if (card.GetGlobalRect().End.X > bar.GlobalPosition.X - 8f * k)
                    _failures.Add($"{tag}: the card '{card.Name}' ends {bar.GlobalPosition.X - card.GetGlobalRect().End.X:F1} px from the scroll bar; at least {8 * k:F0} are needed");
        // tick boxes use the shared look: no local styles, colours or icons
        foreach (var c in All<CheckBox>())
            if (c.ThemeTypeVariation.ToString().Length > 0 || c.HasThemeStyleboxOverride("normal") || c.HasThemeStyleboxOverride("disabled") || c.HasThemeColorOverride("font_color")
                || c.HasThemeColorOverride("font_disabled_color") || c.HasThemeIconOverride("unchecked") || c.HasThemeIconOverride("checked"))
                _failures.Add($"{tag}: tick box '{c.Name}' has a local style");
        // the window's own minimum width leaves room in the smallest window (400 wide, the frame takes 24)
        float minW = panel.GetCombinedMinimumSize().X;
        if (minW > (400 - 24) * k + 0.5f)
        {
            var widest = AllChildren(panel).OfType<Control>().Where(Shown).OrderByDescending(c => c.GetCombinedMinimumSize().X).First();
            _failures.Add($"{tag}: the Build window needs {minW:F0} px of width; its smallest window gives {(400 - 24) * k:F0} (widest: '{widest.Name}' {widest.GetCombinedMinimumSize().X:F0})");
        }

        // each control beside its label, in a column of one width: the same row, close, and the label as wide as the column
        foreach (var (labelName, controlName) in LabelPairs)
        {
            if (panel.FindChild(labelName, true, false) is not Control label || panel.FindChild(controlName, true, false) is not Control control || !Shown(label) || !Shown(control))
                continue;
            var lr = label.GetGlobalRect();
            var cr = control.GetGlobalRect();
            float gap = cr.Position.X - lr.End.X;
            if (Math.Abs(cr.GetCenter().Y - lr.GetCenter().Y) > Math.Max(lr.Size.Y, cr.Size.Y) / 2f || gap < -0.5f || gap > 16f * k)
                _failures.Add($"{tag}: '{controlName}' is not beside its label '{labelName}' (label {lr.Position.X:F0},{lr.Position.Y:F0} {lr.Size.X:F0} wide; control {cr.Position.X:F0},{cr.Position.Y:F0})");
            else if (Math.Abs(lr.Size.X - EditPanel.LabelColumn) > 1f)
                _failures.Add($"{tag}: the label '{labelName}' is {lr.Size.X:F0} px wide, not the column's {EditPanel.LabelColumn:F0}");
        }

        // a tick box row has its label in the column: the label control must exist beside the box
        foreach (var (labelName, controlName) in TickRows)
            if (panel.FindChild(controlName, true, false) is Control tick && Shown(tick) && (panel.FindChild(labelName, true, false) is not Control tl || !Shown(tl)))
                _failures.Add($"{tag}: the tick box '{controlName}' has no label '{labelName}' in the label column");

        // three-value rows: the label on its own line, then the three boxes on one line, one width, never wrapping
        foreach (var row in TripleRows)
        {
            if (panel.FindChild(row + "_label", true, false) is not Control tlabel || !Shown(tlabel))
                continue;
            var boxes = "xyz".Select(a => panel.FindChild($"{row}_{a}", true, false) as Control).ToList();
            if (boxes.Any(b => b is null || !Shown(b)))
            {
                _failures.Add($"{tag}: the three boxes of '{row}' were not all found");
                continue;
            }
            var rects = boxes.Select(b => b!.GetGlobalRect()).ToList();
            if (rects.Max(r => r.GetCenter().Y) - rects.Min(r => r.GetCenter().Y) > 1f)
                _failures.Add($"{tag}: the three boxes of '{row}' are not on one line (down at {string.Join(", ", rects.Select(r => r.Position.Y.ToString("F0")))})");
            if (rects.Max(r => r.Size.X) - rects.Min(r => r.Size.X) > 1f)
                _failures.Add($"{tag}: the three boxes of '{row}' are not one width ({string.Join(", ", rects.Select(r => r.Size.X.ToString("F0")))})");
            if (tlabel.GetGlobalRect().End.Y > rects.Min(r => r.Position.Y) + 0.5f)
                _failures.Add($"{tag}: the label of '{row}' is not on its own line above the boxes");
        }

        // no segment for a tool that is not built
        if (panel.FindChild("tool_land", true, false) is not null)
            _failures.Add($"{tag}: the Build window still has a Land segment for a tool that is not built");
        // a closed section's summary sits on its header line
        foreach (var header in scroll.FindChildren("section_*", "Button", true, false).OfType<Button>().Where(Shown))
        {
            string name = header.Name.ToString()["section_".Length..];
            if (scroll.FindChild("summary_" + name, true, false) is Label summary && Shown(summary) && summary.Text.Length > 0)
            {
                var hr = header.GetGlobalRect();
                if (summary.GetGlobalRect().GetCenter().Y > hr.End.Y || summary.GetGlobalRect().GetCenter().Y < hr.Position.Y)
                    _failures.Add($"{tag}: the summary of '{name}' is under its header, not on its line");
                if (summary.GetLineCount() > 1)
                    _failures.Add($"{tag}: the summary of '{name}' wraps onto {summary.GetLineCount()} lines");
            }
        }
        // with every section closed, as many cards as fit are in the window's view
        if (scene == "build" && size.Length == 0)
        {
            var cards = scroll.FindChildren("section_card_*", "", true, false).OfType<Control>().Where(Shown).ToList();
            int inView = cards.Count(c => c.GetGlobalRect().End.Y <= view.End.Y + 0.5f);
            _result["build_cards_in_view"] = new { cards = cards.Count, in_view = inView };
            // before the fix three cards fitted, the last one cut; the selection card and the tool rows take the rest of the height at larger sizes
            int wanted = pct <= 100 ? 4 : pct <= 150 ? 3 : 2;
            if (inView < wanted)
                _failures.Add($"{tag}: only {inView} of {cards.Count} section cards are in the window's view; {wanted} are wanted");
        }
        // the Create tool has nothing to edit, so no selection card
        if (scene == "build-create" && panel.FindChild("selection_card", true, false) is Control selection && Shown(selection))
            _failures.Add($"{tag}: the selection card is shown while Create is the tool");
        if (scene == "build-create")
        {
            // the shape buttons are one size and sit on a grid
            var shapes = panel.FindChildren("shape_*", "Button", true, false).OfType<Button>().Where(Shown).ToList();
            var rows = shapes.GroupBy(b => (int)Math.Round(b.GetGlobalRect().Position.Y)).ToList();
            var columns = shapes.Select(b => (int)Math.Round(b.GetGlobalRect().Position.X)).Distinct().Count();
            if (shapes.Count == 0 || shapes.Max(b => b.Size.X) - shapes.Min(b => b.Size.X) > 3f || columns > Math.Ceiling(shapes.Count / (double)Math.Max(1, rows.Count)))
                _failures.Add($"{tag}: the shape buttons are not on a grid ({shapes.Count} buttons, {rows.Count} rows, {columns} columns, {shapes.Select(b => (int)Math.Round(b.Size.X)).Distinct().Count()} widths)");
        }
        if (scene == "build-look" && panel.FindChild("texture_name", true, false) is Label tex && Guid.TryParse(tex.Text.Replace("…", ""), out _))
            _failures.Add($"{tag}: the texture is shown by its id '{tex.Text}', not its name");
        if (scene == "build-search")
        {
            // a line says what the search found
            if (panel.FindChild("search_status", true, false) is not Label found || !Shown(found) || found.Text.Length == 0)
                _failures.Add($"{tag}: no line names what the search found");
        }
        if (scene == "build-contents" && edit is not null)
        {
            // the Contents list (long rows, the menu, the buttons)
            var list = edit.ContentTab.List as ItemList;
            if (list is null || list.ItemCount == 0)
                _failures.Add($"{tag}: the Contents list was not found or is empty");
            else
            {
                if (list.TextOverrunBehavior != TextServer.OverrunBehavior.TrimEllipsis)
                    _failures.Add($"{tag}: the Contents list does not end a long row with an ellipsis");
                // a row's text is cut by the list's own column width: it must end inside the list's border, not run to it
                if (list.FixedColumnWidth <= 0 || list.FixedColumnWidth > list.Size.X - 12 * k)
                    _failures.Add($"{tag}: the Contents rows are {list.FixedColumnWidth} px wide in a list of {list.Size.X:F0} px, so a long name runs to the border instead of ending in an ellipsis");
                for (int i = 0; i < list.ItemCount; i++)
                {
                    float w = list.GetThemeFont("font").GetStringSize(list.GetItemText(i), HorizontalAlignment.Left, -1, list.GetThemeFontSize("font_size")).X;
                    if (w > list.Size.X - 24 * k && list.GetItemTooltip(i) != list.GetItemText(i))
                        _failures.Add($"{tag}: row {i} is longer than the list and has no tooltip with its whole text");
                }
            }
            var texts = StringTable.English();
            string menu = string.Join("|", edit.ContentTab.MenuTexts());
            foreach (var key in new[] { "edit.content.open", "edit.content.properties", "edit.content.rename", "edit.content.delete", "edit.content.copy" })
                if (!menu.Contains(texts.Get(key), StringComparison.Ordinal))
                    _failures.Add($"{tag}: the right-click menu lacks '{texts.Get(key)}' that the button row has: {menu}");
            // with the Contents card at the top of the window (the scene scrolls it there), every button is whole at every interface size
            if (panel.FindChild("content_buttons", true, false) is not Control buttons)
                _failures.Add($"{tag}: the Contents button row was not found");
            else
            {
                if (buttons.GetChildren().OfType<Button>().Count(Shown) != 5)
                    _failures.Add($"{tag}: the Contents button row shows {buttons.GetChildren().OfType<Button>().Count(Shown)} buttons, not the five (Open, Properties, Rename, Delete, Copy to inventory)");
                foreach (var b in buttons.GetChildren().OfType<Button>().Where(Shown))
                {
                    var br = b.GetGlobalRect();
                    if (br.End.Y > view.End.Y + 0.5f || br.Position.Y < view.Position.Y - 0.5f || br.End.X > view.End.X + 0.5f)
                        _failures.Add($"{tag}: the Contents button '{b.Name}' is cut: {br.Position.Y:F0} to {br.End.Y:F0} down in a view of {view.Position.Y:F0} to {view.End.Y:F0}, to {br.End.X:F0} across of {view.End.X:F0}");
                }
            }
            // one region scrolls: the list has no scroll bar of its own while the window shows its rows
            if (list is not null && (list.GetVScrollBar().Visible || !list.AutoHeight))
                _failures.Add($"{tag}: the Contents list has a scroll bar of its own ({list.ItemCount} rows)");
        }
    }

    private static IEnumerable<Node> AllChildren(Node root)
    {
        foreach (var c in root.GetChildren())
        {
            yield return c;
            foreach (var d in AllChildren(c))
                yield return d;
        }
    }
}
