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

/// <summary>What a drag going on over the inventory tree shows: the row being dragged (drawn with a dashed outline), and the
/// folder it would move into (drawn with the drop-target outline and the text "Drop to move here"). Guid.Empty where there is none.</summary>
public readonly record struct DropCue(Guid Source, Guid Target);

/// <summary>
/// The inventory tree's look during a drag. A folder never opens because something is dragged over it, however long it is held there
/// (the official viewer opens one after FolderAutoOpenDelay; this viewer does not): the folder under the pointer is only marked as the
/// drop target, and a drop on it moves the item in whether the folder is open or closed. Only a real drag counts: the drag is going on
/// and its mouse button is still held. A drag the engine still thinks is going on after the button was let go is stale and is ended.
/// </summary>
public static class DragFeedback
{
    /// <summary>The string table's key for the text shown on the drop-target folder.</summary>
    public const string TextKey = "inventory.drop.here";

    /// <summary>What to mark: the dragged row, and the folder under the pointer when the item may move into it.</summary>
    public static DropCue During(Guid dragged, Guid folderUnderPointer, bool canDrop, bool dragging, bool buttonHeld)
    {
        if (!dragging || !buttonHeld || dragged == Guid.Empty)
            return default;
        bool target = canDrop && folderUnderPointer != Guid.Empty && folderUnderPointer != dragged;
        return new DropCue(dragged, target ? folderUnderPointer : Guid.Empty);
    }

    /// <summary>The engine thinks a drag is going on but no button is held: it was never ended and is to be cancelled.</summary>
    public static bool IsStaleDrag(bool dragging, bool buttonHeld) => dragging && !buttonHeld;
}
