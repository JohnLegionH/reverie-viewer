// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Appearance;

namespace Viewer.Core.Inventory;

/// <summary>Where an Inventory › New entry sits: at the top of the menu, under Clothing, or under Body parts.</summary>
public enum NewItemGroup { Top, Clothing, BodyParts }

/// <summary>One Inventory › New entry: the item it makes (its default name, asset and inventory types, wearable type) and the
/// next-owner rights it is made with.</summary>
public sealed record NewItemEntry(string Id, NewItemGroup Group, string DefaultName, ItemKind Kind, int AssetType, int InventoryType, WearableType Wearable, uint NextOwnerMask)
{
    public bool IsFolder => Id == "folder";
    public bool IsWearable => Wearable != WearableType.None;
}

/// <summary>
/// Inventory › New as the official viewer offers it, in its order (menu_inventory_add.xml lines 79-270: New Folder, New Script, New
/// Notecard, New Gesture, New Clothes and New Body Parts; menu_inventory.xml's "Create new" submenu holds the same), with its default
/// names (llviewerinventory.cpp lines 1806-1808 for scripts, notecards and gestures; strings.xml "New Shirt" and the like for wearables,
/// and "New Folder"). Scripts, notecards and gestures are made with no asset, so the grid gives them its own default content
/// (create_new_item, llviewerinventory.cpp); scripts, notecards and gestures take the default next-owner rights (move, copy, modify
/// and transfer). New Material and New Settings are not offered here: this viewer has no editor for them yet.
/// </summary>
public static class NewItemCatalog
{
    // the protocol's asset and inventory type numbers (LLAssetType and LLInventoryType)
    private const int AssetLsl = 10, AssetNotecard = 7, AssetGesture = 21, InvLsl = 10, InvNotecard = 7, InvGesture = 20, InvWearable = 18;
    private const uint PermMove = 0x80000, PermCopy = 0x8000, PermModify = 0x4000, PermTransfer = 0x2000;
    // create_new_item takes next-owner rights from LLFloaterPerms::getNextOwnerPerms (llviewerinventory.cpp lines 1728-1742), whose defaults
    // are move, copy, modify and transfer (llfloaterperms.cpp; settings.xml ScriptsNextOwnerCopy and the like, all 1)
    private const uint DefaultNextOwner = PermMove | PermCopy | PermModify | PermTransfer;

    public static IReadOnlyList<NewItemEntry> Entries { get; } = Build();

    private static NewItemEntry[] Build()
    {
        var list = new List<NewItemEntry>
        {
            new("folder", NewItemGroup.Top, "New Folder", ItemKind.Other, -1, -1, WearableType.None, 0),
            new("script", NewItemGroup.Top, "New Script", ItemKind.Script, AssetLsl, InvLsl, WearableType.None, DefaultNextOwner),
            new("notecard", NewItemGroup.Top, "New Note", ItemKind.Notecard, AssetNotecard, InvNotecard, WearableType.None, DefaultNextOwner),
            new("gesture", NewItemGroup.Top, "New Gesture", ItemKind.Gesture, AssetGesture, InvGesture, WearableType.None, DefaultNextOwner),
        };
        // the clothing submenu's order (menu_inventory_add.xml lines 130-228)
        foreach (var t in new[] { WearableType.Shirt, WearableType.Pants, WearableType.Shoes, WearableType.Socks, WearableType.Jacket, WearableType.Skirt,
                     WearableType.Gloves, WearableType.Undershirt, WearableType.Underpants, WearableType.Alpha, WearableType.Tattoo, WearableType.Universal, WearableType.Physics })
            list.Add(Wearable(t, NewItemGroup.Clothing));
        foreach (var t in new[] { WearableType.Shape, WearableType.Skin, WearableType.Hair, WearableType.Eyes })
            list.Add(Wearable(t, NewItemGroup.BodyParts));
        return [.. list];
    }

    // a new wearable's rights (llwearablelist.cpp createNewWearable: next owner move and transfer)
    private static NewItemEntry Wearable(WearableType t, NewItemGroup g)
    {
        string id = WearableKinds.Name(t);
        string label = char.ToUpperInvariant(id[0]) + id[1..];
        return new(id, g, "New " + label, WearableKinds.IsBodyPart(t) ? ItemKind.Bodypart : ItemKind.Clothing, WearableKinds.AssetType(t), InvWearable, t, PermMove | PermTransfer);
    }

    /// <summary>A new item's description: the time and the type's readable name (llviewerassettype.cpp generateDescriptionFor; the names
    /// from llassettype.cpp).</summary>
    public static string DescriptionFor(NewItemEntry entry, DateTime local) =>
        local.ToString("yyyy-MM-dd HH:mm:ss ", System.Globalization.CultureInfo.InvariantCulture) + entry.Kind switch
        {
            ItemKind.Notecard => "note card",
            ItemKind.Script => "lsl2 script",
            ItemKind.Gesture => "gesture",
            ItemKind.Clothing => "clothing",
            ItemKind.Bodypart => "body part",
            _ => "",
        };

    public static NewItemEntry? Of(string id) => Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// A new wearable's file, made as the official viewer makes one (llwearablelist.cpp createNewWearable, lines 254-280): its type and
    /// name; the agent as creator and owner with base and owner rights all, group and everyone none, next owner move and transfer; not for
    /// sale; and every parameter the avatar definition gives that type at its default (setParamsToDefaults). The official viewer also
    /// writes each texture slot's default texture (setTexturesToDefaults); this file leaves the slots out, as this viewer has no table
    /// of those defaults.
    /// </summary>
    public static byte[] DefaultWearable(WearableType type, string name, EditParamCatalog catalog, Guid agent = default)
    {
        string a = agent.ToString("D"), none = Guid.Empty.ToString("D");
        var asset = new WearableAsset
        {
            Type = type,
            Name = name,
            PermissionsLines =
            [
                "\tpermissions 0", "\t{", "\t\tbase_mask\t7fffffff", "\t\towner_mask\t7fffffff", "\t\tgroup_mask\t00000000", "\t\teveryone_mask\t00000000",
                "\t\tnext_owner_mask\t00082000", "\t\tcreator_id\t" + a, "\t\towner_id\t" + a, "\t\tlast_owner_id\t" + none, "\t\tgroup_id\t" + none, "\t}",
            ],
            SaleInfoLines = ["\tsale_info\t0", "\t{", "\t\tsale_type\tnot", "\t\tsale_price\t10", "\t}"],
        };
        foreach (var p in catalog.ParamsOf(type))
            asset.Params[p.Id] = p.Default;
        return asset.ToBytes();
    }
}

/// <summary>A request to make a new inventory item: with no <see cref="Data"/> the grid gives it its default content (scripts, notecards,
/// gestures); with data (a wearable's file) the data is uploaded first and the item made from that upload.</summary>
public sealed record NewItemRequest(Guid FolderId, string Name, string Description, int AssetType, int InventoryType, WearableType Wearable, uint NextOwnerMask, byte[]? Data = null);

/// <summary>Makes inventory items. Implemented by the protocol adapter.</summary>
public interface IItemCreateTransport
{
    /// <summary>Makes the item and waits for the grid's answer: the new item's id, or empty when it was not made.</summary>
    Task<Guid> CreateItemAsync(NewItemRequest request, CancellationToken ct);

    /// <summary>The account's folder for an asset type (Scripts, Notecards, Gestures, Clothing, Body Parts), where a new item goes when no
    /// folder is chosen (findCategoryUUIDForType in the official viewer); empty when unknown.</summary>
    Guid FolderForAssetType(int assetType);
}
