// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Notifications;

/// <summary>
/// Which notification the development automation's dialog command answers, and what it may report about the alert on screen. An alert
/// is modal and is not in <see cref="NotificationModel.List"/>, so it is named on its own (<see cref="Alert"/>). The report carries ids,
/// string-table keys, indices and flags; the text a person reads is never returned.
/// </summary>
public static class DialogTarget
{
    /// <summary>The index that names the alert on screen rather than a notification of the list.</summary>
    public const int Alert = -1;

    /// <summary>The alert on screen for <see cref="Alert"/>; else the list's notification at <paramref name="nth"/>; null when there is none.</summary>
    public static Notification? Pick(NotificationModel model, int nth) => nth == Alert ? model.Modal : nth < 0 ? null : model.List.ElementAtOrDefault(nth);

    /// <summary>One button of the alert as the automation reports it: its index, its label's string-table key (null for a literal label),
    /// its node name in the dialog, and whether it is the default (filled, takes Enter) or the cancel button (takes Esc).</summary>
    public sealed record AlertButton(int Index, string? Key, string Node, bool IsDefault, bool IsCancel);

    /// <summary>The node name of the alert's button with this index; no toast or window uses it, so the ui command finds this one.</summary>
    public static string NodeOf(int index) => "alert_button_" + index;

    /// <summary>The alert's buttons, in the order they are drawn; empty without an alert.</summary>
    public static IReadOnlyList<AlertButton> Buttons(Notification? alert) => alert is null
        ? Array.Empty<AlertButton>()
        : alert.Buttons.Select(b => new AlertButton(b.Index, b.Label.Key, NodeOf(b.Index), alert.DefaultButton == b.Index, alert.CancelButton == b.Index)).ToArray();
}
