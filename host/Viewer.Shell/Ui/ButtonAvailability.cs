// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Ui;

/// <summary>When a button that acts on something is enabled: only while there is something to act on. Pure.</summary>
public static class ButtonAvailability
{
    /// <summary>Mark all as read: enabled while at least one notice is waiting.</summary>
    public static bool MarkAllReadEnabled(int waiting) => waiting > 0;

    /// <summary>Block this resident: enabled while at least one friend is chosen.</summary>
    public static bool BlockResidentEnabled(int chosenFriends) => chosenFriends > 0;
}
