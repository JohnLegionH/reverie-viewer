// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Shell.Build;

/// <summary>What Open does with an item inside an object.</summary>
public enum TaskOpenKind { None, Script, Notecard, Texture }

/// <summary>
/// The official viewer's rules for the items inside an object (indra/newview/llpanelobjectinventory.cpp, read as behaviour; nothing copied).
/// The agent's rights on an item are the owner mask when the agent owns it, otherwise the everyone mask (LLAgent::allowOperation without
/// group powers, which this viewer does not track).
/// </summary>
public static class TaskItemRules
{
    public const uint Transfer = 1u << 13, Modify = 1u << 14, Copy = 1u << 15;

    /// <summary>The longest names and descriptions an item takes (the inventory's DB_INV_ITEM_NAME_STR_LEN and DB_INV_ITEM_DESC_STR_LEN).</summary>
    public const int MaxName = 63, MaxDescription = 127;

    public static uint MaskFor(TaskItem item, Guid agent) => item.OwnerId == agent ? item.OwnerMask : item.EveryoneMask;

    public static bool Allows(TaskItem item, Guid agent, uint bit) => (MaskFor(item, agent) & bit) == bit;

    /// <summary>Scripts open in the script editor (LLTaskLSLBridge), notecards in the notecard window (LLTaskNotecardBridge) and textures in the
    /// texture preview (LLTaskTextureBridge), by the item's asset type (10, 7 and 0).</summary>
    public static TaskOpenKind OpenKind(TaskItem item) => item.AssetType switch
    {
        10 => TaskOpenKind.Script,
        7 => TaskOpenKind.Notecard,
        0 => TaskOpenKind.Texture,
        _ => TaskOpenKind.None,
    };

    /// <summary>LLTaskInvFVBridge::isItemRenameable: the item may be modified.</summary>
    public static bool CanRename(TaskItem item, Guid agent) => Allows(item, agent, Modify);

    /// <summary>LLTaskNotecardBridge::openItem: the card may be copied, or the object may be modified.</summary>
    public static bool CanOpenNotecard(TaskItem item, Guid agent, bool objectModify) => Allows(item, agent, Copy) || objectModify;

    /// <summary>LLTaskInvFVBridge::startDrag: a no-copy item of an attachment never leaves; otherwise an item leaves when it may be copied and
    /// given to the agent (the agent owns it, or its owner may transfer it), or when the agent owns the object.</summary>
    public static bool CanDragOut(TaskItem item, Guid agent, bool youOwnObject, bool attachment)
    {
        bool copy = Allows(item, agent, Copy);
        if (!copy && attachment)
            return false;
        bool transferTo = item.OwnerId == agent || (item.OwnerMask & Transfer) != 0;
        return (copy && transferTo) || youOwnObject;
    }

    /// <summary>The name the list shows: the item's name with " (no copy)", " (no modify)" and " (no transfer)" for what the agent may not do
    /// (LLTaskInvFVBridge::getDisplayName).</summary>
    public static string DisplayName(TaskItem item, Guid agent, Func<string, string> strings)
    {
        var name = item.Name;
        if (!Allows(item, agent, Copy))
            name += strings("edit.content.no_copy");
        if (!Allows(item, agent, Modify))
            name += strings("edit.content.no_modify");
        if (!Allows(item, agent, Transfer))
            name += strings("edit.content.no_transfer");
        return name;
    }
}

/// <summary>An item dragged out of an object's contents, as the drag carries it: the object, its region handle and the item.</summary>
public sealed record TaskDrag(Guid ObjectId, uint LocalId, Guid ItemId)
{
    public const string Prefix = "task:";

    public string Encode() => FormattableString.Invariant($"{Prefix}{ObjectId:D}:{LocalId}:{ItemId:D}");

    /// <summary>The drag's item, or null for anything else (an inventory drag carries plain ids).</summary>
    public static TaskDrag? Decode(string? text)
    {
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var parts = text[Prefix.Length..].Split(':');
        return parts.Length == 3 && Guid.TryParse(parts[0], out var obj) && uint.TryParse(parts[1], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out uint local) && Guid.TryParse(parts[2], out var item) && obj != Guid.Empty && item != Guid.Empty
            ? new TaskDrag(obj, local, item) : null;
    }
}
