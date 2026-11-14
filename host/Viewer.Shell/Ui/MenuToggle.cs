// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// When a menu opens and closes, as the official viewer's menus behave: a menu opens on a click (a top menu on its title, a context menu
/// on a right-click); while one is open, moving over another title switches to it; a click on the open menu's title, Esc or a click
/// outside closes it; and once closed, hovering opens nothing until the next click. The click that closes a menu reaches the closing
/// popup first, so a press on the same title (or the same right-click target) within <see cref="SameClickMs"/> of that close is the
/// closing click itself and must not open the menu again.
/// </summary>
public sealed class MenuToggle
{
    /// <summary>How long after a menu closed a press on its own title still counts as the click that closed it.</summary>
    public const long SameClickMs = 300;

    private string? _closed;
    private long _closedAt;

    /// <summary>The menu open now, or null.</summary>
    public string? Open { get; private set; }

    /// <summary>The menu was shown.</summary>
    public void Opened(string id) => Open = id;

    /// <summary>The menu was hidden (a choice, Esc, a click outside or on its title) at <paramref name="nowMs"/>.</summary>
    public void Closed(string id, long nowMs)
    {
        if (Open == id)
            Open = null;
        _closed = id;
        _closedAt = nowMs;
    }

    /// <summary>Whether a press on <paramref name="id"/>'s title (or a right-click on its target) opens it.</summary>
    public bool PressOpens(string id, long nowMs)
    {
        if (Open == id)
            return false;   // the open menu's title: the press closes it
        if (_closed == id && nowMs - _closedAt <= SameClickMs)
            return false;   // the click that just closed it
        return true;
    }

    /// <summary>Whether the pointer moving over <paramref name="id"/>'s title opens it: only while another menu is open.</summary>
    public bool HoverOpens(string id) => Open is not null && Open != id;
}
