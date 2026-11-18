// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;

namespace Viewer.Core.Inventory;

/// <summary>What a click on an item embedded in a notecard does.</summary>
public enum EmbeddedClick { None, Open, OfferCopy }

/// <summary>
/// The official viewer's rules for editing a notecard, in our words (no code of it is used):
/// <list type="bullet">
/// <item>the text holds at most 65,536 bytes of UTF-8 and the description 127 (floater_preview_notecard.xml lines 57 and 70; the text
/// editor counts UTF-8 bytes, lltexteditor.cpp line 1192);</item>
/// <item>a card may be edited when its permissions let the agent modify it and, inside an object, the object may be modified too
/// (llpreviewnotecard.cpp loadAsset and LLPreview::canModify); a card in the shared library never;</item>
/// <item>an item may be dropped into the text when it is of a kind the editor embeds and its next owner may copy, modify and
/// transfer it (llviewertexteditor.cpp handleDragAndDrop, lines 883-940);</item>
/// <item>a click on an embedded landmark, texture, sound, calling card, settings or material opens it; on anything else it asks
/// "Copy this item to your inventory?" (llviewertexteditor.cpp openEmbeddedItem, lines 1129-1165; notifications.xml ConfirmItemCopy).</item>
/// </list>
/// </summary>
public static class NotecardEditRules
{
    public const int MaxTextBytes = 65536, MaxDescriptionBytes = 127;

    /// <summary>The bits an item's next owner must hold for it to go into a notecard (the official PERM_ITEM_UNRESTRICTED).</summary>
    public const uint Unrestricted = NotecardPermissions.Modify | NotecardPermissions.Copy | NotecardPermissions.Transfer;

    public static bool CanModify(NotecardAccess access, bool objectModify)
    {
        if (access.InLibrary || (access.ObjectId != Guid.Empty && !objectModify))
            return false;
        var p = access.Permissions;
        uint mask = p.Owner == access.AgentId ? p.OwnerMask : p.EveryoneMask;
        return (mask & NotecardPermissions.Modify) != 0;
    }

    public static bool CanEmbed(ItemKind kind, uint nextOwnerMask) =>
        kind is ItemKind.Settings or ItemKind.CallingCard or ItemKind.Texture or ItemKind.Sound or ItemKind.Landmark or ItemKind.Script
            or ItemKind.Clothing or ItemKind.Object or ItemKind.Notecard or ItemKind.Bodypart or ItemKind.Animation or ItemKind.Gesture
            or ItemKind.Mesh or ItemKind.Material
        && (nextOwnerMask & Unrestricted) == Unrestricted;

    public static EmbeddedClick ClickOf(ItemKind kind) => kind switch
    {
        ItemKind.Texture or ItemKind.Sound or ItemKind.Landmark or ItemKind.CallingCard or ItemKind.Settings or ItemKind.Material => EmbeddedClick.Open,
        ItemKind.Notecard or ItemKind.Script or ItemKind.Clothing or ItemKind.Object or ItemKind.Bodypart or ItemKind.Animation or ItemKind.Gesture => EmbeddedClick.OfferCopy,
        _ => EmbeddedClick.None,
    };

    /// <summary>The text's length as the limit counts it: bytes of UTF-8, an embedded item being one character of four bytes.</summary>
    public static int TextBytes(string text) => Encoding.UTF8.GetByteCount(text);
}

public enum NotecardSaveStatus { Saved, NoService, Failed }

/// <summary>The result of saving a notecard: the asset the grid stored, or why it was not saved.</summary>
public sealed record NotecardSaveResult(NotecardSaveStatus Status, Guid NewAssetId, string? Reason = null)
{
    public static NotecardSaveResult Saved(Guid asset) => new(NotecardSaveStatus.Saved, asset);
    public static NotecardSaveResult Of(NotecardSaveStatus status, string? reason = null) => new(status, Guid.Empty, reason);
}

/// <summary>Saves notecards. Implemented by the protocol adapter.</summary>
public interface INotecardSaveTransport
{
    /// <summary>Saves a card's file over its item: UpdateNotecardAgentInventory for a card in the inventory, UpdateNotecardTaskInventory for
    /// one inside an object (<see cref="NotecardAccess.ObjectId"/>), then the uploader those name (llpreviewnotecard.cpp saveIfNeeded).</summary>
    Task<NotecardSaveResult> SaveNotecardAsync(NotecardAccess access, byte[] file, CancellationToken ct);

    /// <summary>An item of the agent's inventory as an item to embed (index 0), or null when it is not known.</summary>
    NotecardEmbeddedItem? EmbeddableItem(Guid itemId) => null;

    /// <summary>Changes an inventory item's description (the official window saves the description field as an item update). False when not sent.</summary>
    bool SetItemDescription(Guid itemId, string description) => false;
}
