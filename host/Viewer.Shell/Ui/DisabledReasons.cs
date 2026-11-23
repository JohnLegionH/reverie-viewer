// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Ui;

/// <summary>
/// Why a control is disabled, as a tooltip (UI-BASICS H2). A disabled control with a reason shows the reason on hover; an enabled one
/// shows its normal tooltip, if it has one. Plain data, tested without the engine; the engine's <c>DisabledTip</c> applies it.
/// </summary>
public static class DisabledReasons
{
    /// <summary>The tooltip text a control shows: the reason while it is disabled (when there is one), otherwise its normal tooltip.</summary>
    public static string? TooltipFor(bool disabled, string? reason, string? normal) =>
        disabled && !string.IsNullOrWhiteSpace(reason) ? reason : normal;

    /// <summary>The string-table keys of the reasons given to the controls that use <c>DisabledTip</c>.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "disabled.inventory.wear", "disabled.inventory.add", "disabled.inventory.attach","disabled.inventory.take_off", "disabled.inventory.detach",
        "disabled.outfit.take_off", "disabled.outfit.wear", "disabled.outfit.save",
        "disabled.notifications.mark_read",
        "disabled.map.teleport", "disabled.map.find_friend",
        "disabled.teleport.busy", "disabled.teleport.no_friends",
        "disabled.edit.link", "disabled.edit.unlink",
        "disabled.content.pick", "disabled.content.open", "disabled.content.rename", "disabled.content.delete",
        "disabled.script.save", "disabled.login.remember_password",
    ];
}
