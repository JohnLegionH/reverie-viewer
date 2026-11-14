// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Notifications;

/// <summary>How a script dialog's buttons are placed: rows of three, the first button bottom-left, rows added upward, Ignore on its own row below.</summary>
public sealed record DialogLayout(IReadOnlyList<IReadOnlyList<int>> Rows)
{
    public const int MaxButtons = 12, PerRow = 3;

    /// <summary>Rows from top to bottom, each holding the script's button indices left to right. Ignore is drawn below the last row.</summary>
    public static DialogLayout For(int buttonCount)
    {
        int n = Math.Clamp(buttonCount, 0, MaxButtons);
        var rows = new List<IReadOnlyList<int>>();
        // the script's first button is bottom-left and rows are added upward, so the top row is the partial one
        for (int start = (n - 1) / PerRow * PerRow; start >= 0 && n > 0; start -= PerRow)
            rows.Add(Enumerable.Range(start, Math.Min(PerRow, n - start)).ToArray());
        return new DialogLayout(rows);
    }

    /// <summary>The labels the dialog shows: the first twelve.</summary>
    public static IReadOnlyList<string> Clip(IReadOnlyList<string> labels) => labels.Take(MaxButtons).ToArray();
}
