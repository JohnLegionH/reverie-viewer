// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>What a window's title-bar button shows: the icon, and the string-table keys of its tooltip and its spoken name.</summary>
public sealed record TitleButtonFace(string Icon, string TipKey, string NameKey);

public static class FloaterButtons
{
    /// <summary>The button beside close: it minimizes a whole window and restores a minimized one.</summary>
    public static TitleButtonFace Minimize(bool minimized) => minimized
        ? new(UiIcons.Restore, "floater.restore", "floater.restore.named")
        : new(UiIcons.Minimize, "floater.minimize", "floater.minimize.named");
}
