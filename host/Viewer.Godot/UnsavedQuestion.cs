// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;

namespace Viewer.Godot;

/// <summary>
/// The save-changes question that the script, notecard and Appearance windows show under their content. In a short window (the smallest window at a
/// large interface size) the content stack is taller than the window's content area, and the bottom of it, the answer buttons, is cut.
/// The rows that are not needed to answer give up their room while the question shows, so the buttons keep their full height.
/// </summary>
internal static class UnsavedQuestion
{
    /// <summary>Hides the given rows while <paramref name="prompt"/> is shown and puts each back as it was when the question goes.</summary>
    public static void GiveUpRoom(Control prompt, params Control[] rows)
    {
        var was = new bool[rows.Length];
        bool applied = false;
        // a row that its own window code shows again while the question is up is hidden again, and comes back when the question goes
        for (int i = 0; i < rows.Length; i++)
        {
            int at = i;
            rows[at].VisibilityChanged += () =>
            {
                if (applied && rows[at].Visible)
                {
                    was[at] = true;
                    rows[at].Visible = false;
                }
            };
        }
        // VisibilityChanged also fires when a parent is hidden or shown, so act only when the question's own flag has really changed
        prompt.VisibilityChanged += () =>
        {
            if (prompt.Visible == applied)
                return;
            applied = prompt.Visible;
            for (int i = 0; i < rows.Length; i++)
            {
                if (applied)
                {
                    was[i] = rows[i].Visible;
                    rows[i].Visible = false;
                }
                else
                    rows[i].Visible = was[i];
            }
        };
    }
}
