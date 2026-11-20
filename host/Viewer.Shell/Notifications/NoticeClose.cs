// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Notifications;

/// <summary>The words on the close button of a notice. Pure.</summary>
public static class NoticeClose
{
    /// <summary>The string-table key of the tooltip and accessible name.</summary>
    public static string TooltipKey(Notification notice) =>
        notice.ScriptDialog ? "notifications.toast.close.script_dialog"
        : notice.Body.Key is "script.permission.request" or "script.permission.caution" ? "notifications.toast.close.permission"
        : "notifications.toast.close";
}
