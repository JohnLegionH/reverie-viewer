// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;

namespace Viewer.Core.Inventory;

/// <summary>Which fields of an item's properties may be changed by the agent.</summary>
public sealed record ItemEditRights(bool Name, bool Description, bool NextModify, bool NextCopy, bool NextTransfer)
{
    public static readonly ItemEditRights None = new(false, false, false, false, false);
}

/// <summary>The values the properties window asks for. Nothing else about an item can be asked for.</summary>
public sealed record ItemEditRequest(string Name, string Description, bool NextModify, bool NextCopy, bool NextTransfer);

/// <summary>
/// The rules for changing an item's name, description and next-owner permissions (the official viewer's item profile,
/// llsidepaneliteminfo.cpp, read as behaviour). <see cref="Apply"/> starts from the item the inventory holds and can change only
/// those three: the base, owner, group and everyone masks, the owner, creator and sale are carried over as they were, and a
/// next-owner bit is added only when it is in both the base mask and the owner mask, so no request can give a right the person
/// does not hold.
/// </summary>
public static class ItemEdit
{
    public const int MaxDescriptionBytes = 127;
    /// <summary>The item flag that makes new next-owner permissions apply to an object when it is rezzed.</summary>
    public const uint SlamPermissions = 0x100;

    private const int CallingCardType = 2, LandmarkType = 3, SettingsType = 25;

    /// <summary>The permission bits both the base and the owner mask allow: all a next owner can be given.</summary>
    private static uint Held(ItemPermissions p) => p.BaseMask & p.OwnerMask;

    public static ItemEditRights RightsFor(InventoryModel model, InventoryItemData item)
    {
        if (item.Permissions is not { } p || item.Kind is ItemKind.Link or ItemKind.LinkFolder || model.IsInLibrary(item.ParentId))
            return ItemEditRights.None;
        var trash = model.FolderOfKind(FolderKind.Trash);
        if (trash != Guid.Empty && model.IsDescendantOf(item.Id, trash))
            return ItemEditRights.None;
        bool modify = p.AllowsModifyBy(model.OwnerId);
        uint held = Held(p);
        bool next = !p.GroupOwned && p.OwnerId == model.OwnerId && (held & ItemPermissions.Transfer) != 0
            && item.InventoryType is not (CallingCardType or LandmarkType);
        return new ItemEditRights(
            modify && item.InventoryType != CallingCardType,
            modify,
            next && (held & ItemPermissions.Modify) != 0,
            next && (held & ItemPermissions.Copy) != 0 && item.InventoryType != SettingsType,
            next && (p.NextOwnerMask & ItemPermissions.Copy) != 0);
    }

    /// <summary>What a window's values ask for. <paramref name="shown"/> is what the window was filled with: a value the user has
    /// not changed asks for the item's own, so a window not refilled yet does not undo a change made elsewhere.</summary>
    public static ItemEditRequest Changes(InventoryItemData item, ItemEditRequest shown, ItemEditRequest now)
    {
        uint next = item.Permissions?.NextOwnerMask ?? 0;
        bool Box(bool was, bool isNow, uint bit) => isNow != was ? isNow : (next & bit) != 0;
        return new ItemEditRequest(
            now.Name != shown.Name ? now.Name : item.Name,
            now.Description != shown.Description ? now.Description : item.Description,
            Box(shown.NextModify, now.NextModify, ItemPermissions.Modify),
            Box(shown.NextCopy, now.NextCopy, ItemPermissions.Copy),
            Box(shown.NextTransfer, now.NextTransfer, ItemPermissions.Transfer));
    }

    /// <summary>The item as it would be saved: <paramref name="current"/> with the asked values in the fields that may change.
    /// Equal to <paramref name="current"/> when nothing may or does change.</summary>
    public static InventoryItemData Apply(InventoryModel model, InventoryItemData current, ItemEditRequest wanted)
    {
        if (current.Permissions is not { } p)
            return current;
        var rights = RightsFor(model, current);
        string name = current.Name, description = current.Description;
        if (rights.Name && wanted.Name != current.Name && IntentRules.NameOk(wanted.Name))
            name = IntentRules.Normalised(wanted.Name);
        if (rights.Description && wanted.Description != current.Description)
            description = CleanDescription(wanted.Description);

        uint next = p.NextOwnerMask, held = Held(p);
        void Set(bool open, uint bit, bool on)
        {
            if (!open || on == ((next & bit) != 0))
                return;
            next = on ? next | (bit & held) : next & ~bit;
        }
        Set(rights.NextModify, ItemPermissions.Modify, wanted.NextModify);
        Set(rights.NextCopy, ItemPermissions.Copy, wanted.NextCopy);
        // the transfer box works only while copy is on: with copy off, transfer is turned on (a next owner can always copy or give away)
        if (rights.NextTransfer && (next & ItemPermissions.Copy) != 0)
            Set(true, ItemPermissions.Transfer, wanted.NextTransfer);
        if (next != p.NextOwnerMask && (next & ItemPermissions.Copy) == 0)
            next |= ItemPermissions.Transfer & held;

        uint flags = next != p.NextOwnerMask && current.Kind == ItemKind.Object ? current.Flags | SlamPermissions : current.Flags;
        return current with
        {
            Name = name, Description = description, Flags = flags,
            Permissions = next == p.NextOwnerMask ? p : p with { NextOwnerMask = next },
        };
    }

    /// <summary>Control characters dropped, and the text cut to <see cref="MaxDescriptionBytes"/> UTF-8 bytes between characters.</summary>
    private static string CleanDescription(string text)
    {
        var kept = new System.Text.StringBuilder();
        int bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (System.Text.Rune.IsControl(rune))
                continue;
            bytes += rune.Utf8SequenceLength;
            if (bytes > MaxDescriptionBytes)
                break;
            kept.Append(rune.ToString());
        }
        return kept.ToString();
    }
}
