// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;

namespace Viewer.Shell.Inventory;

/// <summary>Which mouse button a press on an inventory row used.</summary>
public enum RowButton { Left, Right, Middle }

/// <summary>Where on a row a press landed: the folder's open/close arrow, or the row's icon and name.</summary>
public enum RowPart { Arrow, Name }

/// <summary>A key the inventory tree answers to.</summary>
public enum TreeKey { Left, Right, Up, Down }

/// <summary>What a press or a key does to the inventory tree.</summary>
public enum TreeAction { None, Toggle, Select, SelectAndToggle, SelectAndMenu, Open, Expand, Collapse, SelectParent, MoveUp, MoveDown }

/// <summary>
/// How the inventory tree answers the mouse and the keyboard, as the official viewer's folder view does (indra/llui/llfolderviewitem.cpp,
/// LLFolderViewFolder::handleMouseDown and handleDoubleClick; indra/llui/llfolderview.cpp, handleKeyHere): a click on a folder's arrow
/// toggles it, a click on a name selects it, a double-click on a folder's name selects and toggles it and on an item opens it, and a
/// right-click selects the row and opens its menu without toggling anything. Right opens a closed folder; Left closes an open one, and
/// on anything else moves to the parent folder (closing it instead at the top level); Up and Down move the selection. Nothing here
/// answers the pointer merely passing over a row.
/// </summary>
public static class FolderTreeInput
{
    public static TreeAction Press(RowButton button, RowPart part, bool isFolder, bool doubleClick) => button switch
    {
        RowButton.Right => TreeAction.SelectAndMenu,
        RowButton.Left when isFolder && part == RowPart.Arrow => TreeAction.Toggle,
        RowButton.Left when doubleClick => isFolder ? TreeAction.SelectAndToggle : TreeAction.Open,
        RowButton.Left => TreeAction.Select,
        _ => TreeAction.None,
    };

    public static TreeAction Key(TreeKey key, bool isFolder, bool expanded, bool hasParentRow) => key switch
    {
        TreeKey.Up => TreeAction.MoveUp,
        TreeKey.Down => TreeAction.MoveDown,
        TreeKey.Right => isFolder && !expanded ? TreeAction.Expand : TreeAction.None,
        TreeKey.Left when isFolder && expanded => TreeAction.Collapse,
        TreeKey.Left => hasParentRow ? TreeAction.SelectParent : isFolder ? TreeAction.Collapse : TreeAction.None,
        _ => TreeAction.None,
    };
}

/// <summary>
/// Opens a closed folder that a dragged item is held over, after the official viewer's FolderAutoOpenDelay (0.75 s in its
/// settings.xml; indra/llui/llfolderviewitem.cpp opens the folder under a drag once its timer passes that). Only a real drag counts:
/// the drag is going on and its mouse button is still held. A drag the engine still thinks is going on after the button was let go
/// is stale: it opens nothing and is to be ended, so a pointer passing over folders never opens them.
/// </summary>
public sealed class DragAutoOpen
{
    public const double OfficialDelaySeconds = 0.75;

    private Guid _over;
    private long _since;
    private bool _fired;

    public DragAutoOpen(double delaySeconds = OfficialDelaySeconds) => DelayMs = (long)Math.Round(Math.Max(0, delaySeconds) * 1000);

    public long DelayMs { get; }

    /// <summary>One look at what is under the pointer; returns the folder to open now, else Guid.Empty.</summary>
    public Guid Step(Guid folderUnderPointer, bool folderClosed, bool dragging, bool buttonHeld, long nowMs)
    {
        if (!dragging || !buttonHeld || folderUnderPointer == Guid.Empty)
        {
            _over = Guid.Empty;
            _fired = false;
            return Guid.Empty;
        }
        if (folderUnderPointer != _over)
        {
            _over = folderUnderPointer;
            _since = nowMs;
            _fired = false;
        }
        if (_fired || !folderClosed || nowMs - _since < DelayMs)
            return Guid.Empty;
        _fired = true;
        return _over;
    }

    /// <summary>The engine thinks a drag is going on but no button is held: it was never ended and is to be cancelled.</summary>
    public static bool IsStaleDrag(bool dragging, bool buttonHeld) => dragging && !buttonHeld;
}
