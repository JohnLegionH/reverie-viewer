// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;

namespace Viewer.Core.Interaction;

/// <summary>The region's object flag bits that matter for clicking (the same bits as the protocol's object update flags).</summary>
public static class ObjectFlagBits
{
    public const uint Physics = 1u << 0;
    /// <summary>The object was just made by the agent's own create tool and arrives selected.</summary>
    public const uint CreateSelected = 1u << 1;
    public const uint Modify = 1u << 2;
    public const uint Copy = 1u << 3;
    public const uint YouOwner = 1u << 5;
    public const uint Scripted = 1u << 6;
    public const uint HandleTouch = 1u << 7;
    public const uint TakesMoney = 1u << 9;
    /// <summary>The agent may move the object (it is not locked).</summary>
    public const uint Move = 1u << 8;
    public const uint InventoryEmpty = 1u << 11;
    /// <summary>The object may be transferred to another owner.</summary>
    public const uint Transfer = 1u << 17;
}

/// <summary>The cursor an object under the mouse earns.</summary>
public enum HoverCursor { Arrow, Touch, Sit, Open, Pay, Buy, Zoom, Grab }

/// <summary>What a left click on an object does in this viewer.</summary>
public enum LeftClick { Touch, Sit, Open, OpenDenied, Swallowed, Unhandled, Buy, Pay }

/// <summary>Click actions: which action an object's click runs, whether it can be touched, and the cursor it earns.</summary>
public static class ClickAction
{
    public const byte Touch = 0, Sit = 1, Buy = 2, Pay = 3, Open = 4, Play = 5, OpenMedia = 6, Zoom = 7, Disabled = 8, Ignore = 9;

    /// <summary>The action a click runs: none for no object or an attachment; the object's own when non-zero or when its root's is disabled;
    /// else the root's; touch when neither sets one.</summary>
    public static byte Final(PrimInteraction? self, PrimInteraction? root, bool attachment)
    {
        if (self is null || attachment)
            return Touch;
        byte own = self.ClickAction, top = (root ?? self).ClickAction;
        if (own != Touch || top == Disabled)
            return own != Touch ? own : top;
        return top;
    }

    private static bool Uses(PrimInteraction? self, PrimInteraction? root, bool attachment)
    {
        if (self is null || attachment)
            return false;
        byte own = self.ClickAction, top = (root ?? self).ClickAction;
        return (own != Touch || top != Touch) && Final(self, root, false) != Disabled;
    }

    private static bool HasFlag(PrimInteraction? self, PrimInteraction? root, uint bit) =>
        ((self?.Flags ?? 0) & bit) != 0 || (((root ?? self)?.Flags ?? 0) & bit) != 0;

    /// <summary>True when the object, or its root, has a touch handler and the object's own action is not disabled.</summary>
    public static bool Touchable(PrimInteraction? self, PrimInteraction? root) =>
        self is not null && self.ClickAction != Disabled && HasFlag(self, root, ObjectFlagBits.HandleTouch);

    /// <summary>True when the root has contents and the agent owns it or may modify it.</summary>
    public static bool OpenAllowed(PrimInteraction? root) =>
        root is not null && (root.Flags & ObjectFlagBits.InventoryEmpty) == 0 && (root.Flags & (ObjectFlagBits.YouOwner | ObjectFlagBits.Modify)) != 0;

    /// <summary>The cursor for an object under the mouse.</summary>
    public static HoverCursor Cursor(PrimInteraction? self, PrimInteraction? root, bool attachment, bool sitting)
    {
        if (self is null || attachment)
            return HoverCursor.Arrow;
        if (Uses(self, root, attachment))
        {
            switch (Final(self, root, attachment))
            {
                case Sit: return sitting ? HoverCursor.Arrow : HoverCursor.Sit;
                case Buy: return HoverCursor.Buy;
                case Open: return OpenAllowed(root ?? self) ? HoverCursor.Open : HoverCursor.Arrow;
                case Pay: return HasFlag(self, root, ObjectFlagBits.TakesMoney) ? HoverCursor.Pay : HoverCursor.Arrow;
                case Zoom: return HoverCursor.Zoom;
                default: return HoverCursor.Arrow;
            }
        }
        if (HasFlag(self, root, ObjectFlagBits.Physics))
            return HoverCursor.Grab;
        return Touchable(self, root) ? HoverCursor.Touch : HoverCursor.Arrow;
    }

    /// <summary>What a left click on the object does.</summary>
    public static LeftClick Left(PrimInteraction? self, PrimInteraction? root, bool attachment, bool sitting)
    {
        if (attachment)
            return LeftClick.Unhandled;
        if (self is null || !Uses(self, root, attachment))
            return Final(self, root, attachment) == Disabled ? LeftClick.Swallowed : LeftClick.Touch;
        switch (Final(self, root, attachment))
        {
            case Sit: return sitting ? LeftClick.Unhandled : LeftClick.Sit;
            case Open: return OpenAllowed(root ?? self) ? LeftClick.Open : LeftClick.OpenDenied;
            case Disabled: return LeftClick.Swallowed;
            case Touch: return LeftClick.Touch;
            case Buy: return LeftClick.Buy;
            case Pay: return HasFlag(self, root, ObjectFlagBits.TakesMoney) ? LeftClick.Pay : LeftClick.Unhandled;
            default: return LeftClick.Unhandled;   // play, open media, zoom: not handled in this viewer
        }
    }
}
