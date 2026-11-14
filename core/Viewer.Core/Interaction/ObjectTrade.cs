// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Interaction;

/// <summary>The protocol's permission bits that matter for trading and copying.</summary>
public static class PermissionBits
{
    public const uint Transfer = 1u << 13, Modify = 1u << 14, Copy = 1u << 15;
}

/// <summary>The sale types of an object (the protocol's numbers).</summary>
public static class SaleTypes
{
    public const byte Not = 0, Original = 1, Copy = 2, Contents = 3;
}

/// <summary>Whether the items inside an object may be copied to the agent, as the official viewer decides it when contents are copied
/// out of an object (an item the agent may copy and that may be transferred to the agent is copied; otherwise, when the agent owns the
/// object, the items are moved out of it; otherwise nothing is copied).</summary>
public readonly record struct ContentsPlan(bool Accepted, bool IsMove);

public static class ObjectContents
{
    /// <summary>The agent may copy the item and receive it: its owner mask allows copying (or, for another owner, the everyone mask does),
    /// and the item may be transferred to the agent.</summary>
    public static bool CanCopyItem(TaskItem item, Guid agent)
    {
        bool owner = item.OwnerId == agent;
        bool copy = ((owner ? item.OwnerMask : item.EveryoneMask) & PermissionBits.Copy) != 0;
        bool transfer = owner || (item.OwnerMask & PermissionBits.Transfer) != 0;
        return copy && transfer;
    }

    /// <summary>Whether copying the contents goes ahead, as the official viewer's loop decides it (llinventorybridge.cpp, move_inv_category_world_to_agent):
    /// an item that can be copied accepts; otherwise, for an object the agent owns, the item is moved out and the copy is still accepted; any
    /// other item leaves the answer as it was, and an answer of "no" ends the loop. So the first item decides, and the region refuses what the
    /// agent may not take.</summary>
    public static ContentsPlan Plan(IReadOnlyList<TaskItem> items, Guid agent, bool youOwnObject)
    {
        if (items.Count == 0)
            return new ContentsPlan(false, false);
        bool accepted = false, move = false;
        foreach (var item in items)
        {
            if (CanCopyItem(item, agent))
                accepted = true;
            else if (youOwnObject)
            {
                move = true;
                accepted = true;
            }
            if (!accepted)
                break;
        }
        return new ContentsPlan(accepted, move);
    }
}

/// <summary>What an object's sale information means for buying it.</summary>
public static class ObjectSale
{
    /// <summary>The sale type the properties state (an older region may only say that it is for sale: then the original).</summary>
    public static byte TypeOf(ObjectPropertiesReceived p) => p.SaleType is >= SaleTypes.Original and <= SaleTypes.Contents ? p.SaleType : p.ForSale ? SaleTypes.Original : SaleTypes.Not;

    public static bool IsForSale(ObjectPropertiesReceived p) => TypeOf(p) != SaleTypes.Not;

    /// <summary>Buying is possible: the owner's mask allows transfer, and a copy sale needs the owner's mask to allow copying
    /// (llviewermenu.cpp, for_sale_selection).</summary>
    public static bool CanBuy(ObjectPropertiesReceived p) =>
        IsForSale(p) && (p.OwnerMask & PermissionBits.Transfer) != 0 && ((p.OwnerMask & PermissionBits.Copy) != 0 || TypeOf(p) != SaleTypes.Copy);
}
