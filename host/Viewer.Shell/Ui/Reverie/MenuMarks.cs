// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>How a dropdown row is drawn: the icon in the icon column (null for none) and whether the row is a check item.</summary>
public readonly record struct MenuMark(string? Icon, bool CheckItem);

/// <summary>
/// What a dropdown row shows for its icon and check state. A row with an icon of its own keeps it, and a row that can be ticked is a check
/// item. A row with no icon of its own shows the tick in the icon column only while it is ticked, so in a list of choices only the choice in use
/// shows a mark.
/// </summary>
public static class MenuMarks
{
    public static MenuMark For(string? ownIcon, bool? isChecked) =>
        isChecked is null ? new MenuMark(ownIcon, false)
        : ownIcon is not null ? new MenuMark(ownIcon, true)
        : new MenuMark(isChecked == true ? LucideIcons.Check : null, false);
}
