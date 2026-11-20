// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>What the keys that start chat put in the chat line (llviewerinput.cpp start_gesture, llfloaterimnearbychat.cpp startChat).</summary>
public static class ChatStart
{
    /// <summary>The text the gesture key sets: "/" when the line is empty, null (leave the text as it is) when something is typed already.</summary>
    public static string? GestureLine(string current) => current.Length == 0 ? "/" : null;
}
