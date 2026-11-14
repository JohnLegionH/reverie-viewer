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
/// How the inventory tree answers the mouse and the keyboard (stub).
/// </summary>
public static class FolderTreeInput
{
    public static TreeAction Press(RowButton button, RowPart part, bool isFolder, bool doubleClick) => TreeAction.None;

    public static TreeAction Key(TreeKey key, bool isFolder, bool expanded, bool hasParentRow) => TreeAction.None;
}

/// <summary>
/// Opens a closed folder that a dragged item is held over (stub).
/// </summary>
public sealed class DragAutoOpen
{
    public const double OfficialDelaySeconds = 0.75;

    public DragAutoOpen(double delaySeconds = OfficialDelaySeconds) => DelayMs = 0;

    public long DelayMs { get; }

    public Guid Step(Guid folderUnderPointer, bool folderClosed, bool dragging, bool buttonHeld, long nowMs) => Guid.Empty;

    public static bool IsStaleDrag(bool dragging, bool buttonHeld) => false;
}
