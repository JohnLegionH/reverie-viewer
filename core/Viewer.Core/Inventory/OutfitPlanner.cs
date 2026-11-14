// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using Viewer.Core.Actions;

namespace Viewer.Core.Inventory;

/// <summary>
/// Works out outfit changes from the inventory model. The rules follow the official viewer's handling of the current outfit
/// folder when the grid bakes the look: an item is worn by a link to it in that folder; wearing replaces the topmost layer of a
/// clothing type and adding stacks above it, up to 60 clothing layers in all; a body part always replaces the one of its type
/// and can never be taken off; an object is rezzed on a point of the avatar and linked; a clothing link's description keeps
/// the layer order as "@" followed by the type number times 100 plus the layer; wearing an outfit keeps the body parts the
/// outfit does not name, replaces all clothing and attachments, and links back to the outfit folder it came from.
/// Pure: reads the model, changes nothing, and never refuses silently (a refusal carries a code).
/// </summary>
public static class OutfitPlanner
{
    public const int MaxClothingLayers = 60;
    public const int MaxAttachmentPoint = 55;
    /// <summary>The most items one outfit may hold (the official viewer's limit for modifying an outfit).</summary>
    public const int MaxOutfitItems = 125;

    private const string DefaultOutfitName = "New Outfit";

    // ---------------- reading ----------------

    private static Guid CofOf(InventoryModel m) => m.FolderOfKind(FolderKind.CurrentOutfit);

    private static bool CofKnown(InventoryModel m, out Guid cof)
    {
        cof = CofOf(m);
        return cof != Guid.Empty && m.LoadState(cof) == FolderLoad.Loaded;
    }

    private sealed record Linked(InventoryItemData Link, InventoryItemData? Target, WearableType Type, int Layer);

    /// <summary>The links in the current outfit folder, each with the item it points to (null while that item is not loaded),
    /// the wearable type (from the item, else from the link's description) and the layer.</summary>
    private static List<Linked> Links(InventoryModel m, Guid cof)
    {
        var list = new List<Linked>();
        foreach (var link in m.ChildItems(cof))
        {
            if (link.LinkedId is not { } id || link.Kind == ItemKind.LinkFolder)
                continue;
            var target = m.Item(id);
            var type = target is null ? WearableType.None : WearableTypeOf(target);
            int layer = 0;
            if (TryOrder(link.Description, out var descType, out int descLayer))
            {
                if (type == WearableType.None && target is null)
                    type = descType;
                layer = descLayer;
            }
            list.Add(new Linked(link, target, type, layer));
        }
        return list;
    }

    private static bool TryOrder(string? description, out WearableType type, out int layer)
    {
        type = WearableType.None;
        layer = 0;
        if (description is not { Length: > 1 } d || d[0] != '@' || !int.TryParse(d.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            return false;
        type = (WearableType)(n / 100);
        layer = n % 100;
        return true;
    }

    private static string Order(WearableType type, int layer) => "@" + ((int)type * 100 + layer).ToString(CultureInfo.InvariantCulture);

    /// <summary>The item a request means: a link in the user's inventory stands for the item it points to.</summary>
    private static InventoryItemData? Resolve(InventoryModel m, Guid id)
    {
        var item = m.Item(id);
        if (item?.LinkedId is { } target && item.Kind == ItemKind.Link)
            return m.Item(target);
        return item;
    }

    public static bool IsWorn(InventoryModel model, Guid itemId) =>
        CofKnown(model, out var cof) && model.ChildItems(cof).Any(i => i.LinkedId == itemId);

    public static WearableType WearableTypeOf(InventoryItemData item) =>
        item.Kind is ItemKind.Clothing or ItemKind.Bodypart && (item.Flags & 0xFF) <= (uint)WearableType.Universal ? (WearableType)(item.Flags & 0xFF) : WearableType.None;

    public static bool IsBodyPart(WearableType t) => t is >= WearableType.Shape and <= WearableType.Eyes;

    private static bool IsWearableKind(InventoryItemData i) => i.Kind is ItemKind.Clothing or ItemKind.Bodypart or ItemKind.Object;

    /// <summary>A refusal for an item that cannot be put on at all, or null. An item of the Library can be worn: it is copied first.</summary>
    private static string? Unwearable(InventoryModel m, InventoryItemData? target)
    {
        if (target is null || !IsWearableKind(target))
            return "outfit.not_wearable";
        var trash = m.FolderOfKind(FolderKind.Trash);
        if (trash != Guid.Empty && m.IsDescendantOf(target.Id, trash))
            return "outfit.in_trash";
        return null;
    }

    private static OutfitPlan Plan(InventoryModel m, Guid cof, List<NewOutfitLink> create, IEnumerable<Guid> remove, List<AttachRequest>? attach = null, IEnumerable<Guid>? detach = null)
    {
        attach ??= new List<AttachRequest>();
        return new(new OutfitChange(cof, create, remove.Distinct().ToList(), attach, (detach ?? Array.Empty<Guid>()).Distinct().ToList(), CopiesFor(m, create, attach)), null);
    }

    /// <summary>
    /// The Library items a change links or puts on. The official viewer copies each into the account's inventory first, into the
    /// folder for its kind, and wears the copy (LLAppearanceMgr::wearItemsOnAvatar: an item under the Library root is copied with
    /// copy_inventory_item and the copy is worn when it arrives).
    /// </summary>
    private static List<LibraryCopy> CopiesFor(InventoryModel m, IEnumerable<NewOutfitLink> create, IEnumerable<AttachRequest> attach)
    {
        var copies = new List<LibraryCopy>();
        foreach (var id in create.Where(l => !l.IsFolder).Select(l => l.TargetId).Concat(attach.Select(a => a.ItemId)).Distinct())
        {
            if (m.Item(id) is not { } item || !m.IsInLibrary(item.ParentId))
                continue;
            var kind = item.Kind switch { ItemKind.Clothing => FolderKind.Clothing, ItemKind.Bodypart => FolderKind.Bodypart, _ => FolderKind.Object };
            var folder = m.FolderOfKind(kind);
            copies.Add(new LibraryCopy(id, folder != Guid.Empty ? folder : m.RootId));
        }
        return copies;
    }

    // ---------------- wear and add ----------------

    public static OutfitPlan Wear(InventoryModel model, Guid itemId, bool add)
    {
        var target = Resolve(model, itemId);
        if (Unwearable(model, target) is { } refusal)
            return OutfitPlan.Refuse(refusal);
        if (target!.Kind == ItemKind.Object)
            return Attach(model, target.Id, 0, add);   // wearing an object puts it on its saved point
        if (!CofKnown(model, out var cof))
            return OutfitPlan.Refuse("outfit.cof_unknown");
        var links = Links(model, cof);
        if (links.Any(l => l.Link.LinkedId == target.Id))
            return OutfitPlan.Refuse("outfit.already_worn");

        var type = WearableTypeOf(target);
        var remove = new List<Guid>();
        if (IsBodyPart(type))
        {
            remove.AddRange(links.Where(l => l.Type == type).Select(l => l.Link.Id));
            return Plan(model, cof, [new NewOutfitLink(target.Id, target.Name, "")], remove);
        }

        var same = links.Where(l => l.Type == type).OrderBy(l => l.Layer).ThenBy(l => l.Link.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int clothing = links.Count(l => l.Type != WearableType.None && !IsBodyPart(l.Type));
        if (!add || clothing >= MaxClothingLayers)
        {
            if (same.Count == 0)
            {
                if (add)
                    return OutfitPlan.Refuse("outfit.too_many");
            }
            else
            {
                remove.Add(same[^1].Link.Id);
                same.RemoveAt(same.Count - 1);
            }
        }
        return Plan(model, cof, [new NewOutfitLink(target.Id, target.Name, Order(type, same.Count))], remove);
    }

    // ---------------- detach ----------------

    /// <summary>
    /// Takes a worn object off. Official viewer: Detach on an attachment's menu and "Detach From Yourself" in inventory remove the item's
    /// link from the current outfit and send DetachAttachmentIntoInv with the item id (llviewermenu.cpp, LLAttachmentDetach;
    /// llvoavatarself.cpp, detachAttachmentIntoInventory). An object worn without a link (one put on from the ground) has no link to
    /// remove; <paramref name="attached"/> says which items the avatar wears, so the detach is still sent.
    /// </summary>
    public static OutfitPlan Detach(InventoryModel model, Guid itemId, IReadOnlySet<Guid> attached)
    {
        if (!CofKnown(model, out var cof))
            return OutfitPlan.Refuse("outfit.cof_unknown");
        var links = Links(model, cof);
        var asLink = links.FirstOrDefault(l => l.Link.Id == itemId);
        Guid target = asLink?.Link.LinkedId ?? itemId;
        var mine = links.Where(l => l.Link.LinkedId == target).ToList();
        if (mine.Count == 0 && !attached.Contains(target))
            return OutfitPlan.Refuse("outfit.not_worn");
        if (model.Item(target) is { } item && item.Kind != ItemKind.Object)
            return OutfitPlan.Refuse("outfit.not_wearable");
        var remove = mine.Select(l => l.Link.Id).ToList();
        var plan = Plan(model, cof, [], remove, detach: [target]);
        return remove.Count == 0 ? plan : plan with { Change = plan.Change! with { NewDescriptions = Renumber(links, remove) } };
    }

    /// <summary>Takes every worn object off (Detach All).</summary>
    public static OutfitPlan DetachAll(InventoryModel model, IReadOnlySet<Guid> attached)
    {
        if (!CofKnown(model, out var cof))
            return OutfitPlan.Refuse("outfit.cof_unknown");
        var links = Links(model, cof);
        // every object the avatar wears: those with a link in the outfit and those put on without one
        var linked = links.Where(l => model.Item(l.Link.LinkedId ?? Guid.Empty) is { Kind: ItemKind.Object }).ToList();
        var targets = linked.Select(l => l.Link.LinkedId!.Value).Concat(attached).Distinct().ToList();
        if (targets.Count == 0)
            return OutfitPlan.Refuse("outfit.not_worn");
        var remove = linked.Select(l => l.Link.Id).ToList();
        var plan = Plan(model, cof, [], remove, detach: targets);
        return remove.Count == 0 ? plan : plan with { Change = plan.Change! with { NewDescriptions = Renumber(links, remove) } };
    }

    // ---------------- take off ----------------

    /// <summary>
    /// Takes every clothing layer off in one change: each link to clothing in the current outfit is removed, body parts and worn objects stay
    /// (the official viewer's LLAppearanceMgr::removeAllClothesFromAvatar). A link whose item is not loaded counts as clothing when its layer
    /// note names a clothing type.
    /// </summary>
    public static OutfitPlan TakeOffAllClothes(InventoryModel model)
    {
        if (!CofKnown(model, out var cof))
            return OutfitPlan.Refuse("outfit.cof_unknown");
        var links = Links(model, cof);
        var remove = links.Where(l => l.Target is { } t ? t.Kind == ItemKind.Clothing && !IsBodyPart(WearableTypeOf(t)) : l.Type != WearableType.None && !IsBodyPart(l.Type))
            .Select(l => l.Link.Id).ToList();
        if (remove.Count == 0)
            return OutfitPlan.Refuse("outfit.not_worn");
        return Plan(model, cof, [], remove);
    }

    public static OutfitPlan TakeOff(InventoryModel model, Guid itemId)
    {
        if (!CofKnown(model, out var cof))
            return OutfitPlan.Refuse("outfit.cof_unknown");
        var links = Links(model, cof);
        var asLink = links.FirstOrDefault(l => l.Link.Id == itemId);
        Guid target = asLink?.Link.LinkedId ?? itemId;
        var mine = links.Where(l => l.Link.LinkedId == target).ToList();
        if (mine.Count == 0)
            return OutfitPlan.Refuse("outfit.not_worn");
        var item = mine[0].Target;
        if (item is null)
        {
            // an item not loaded yet: only a clothing link (it carries a layer order) is known to be safe to take off
            if (mine[0].Type == WearableType.None || IsBodyPart(mine[0].Type))
                return OutfitPlan.Refuse("outfit.not_loaded");
        }
        else if (item.Kind == ItemKind.Bodypart || IsBodyPart(WearableTypeOf(item)))
        {
            return OutfitPlan.Refuse("outfit.bodypart");
        }
        var remove = mine.Select(l => l.Link.Id).ToList();
        bool attachment = item?.Kind == ItemKind.Object;
        var plan = Plan(model, cof, [], remove, detach: attachment ? [target] : null);
        return plan with { Change = plan.Change! with { NewDescriptions = Renumber(links, remove) } };
    }

    /// <summary>
    /// The layer notes to write once the links in <paramref name="removed"/> are gone, so each clothing type's remaining links are
    /// numbered 0, 1, 2 and so on. As the official viewer does (LLAppearanceMgr::getWearableOrderingDescUpdates): a type's links are
    /// sorted by layer, those whose note is not a valid one for the type after the valid ones, then by name; any link whose note
    /// differs from its new number is written again.
    /// </summary>
    private static List<LinkDescription> Renumber(List<Linked> links, ICollection<Guid> removed)
    {
        var updates = new List<LinkDescription>();
        foreach (var group in links.Where(l => !removed.Contains(l.Link.Id) && l.Type != WearableType.None && !IsBodyPart(l.Type)).GroupBy(l => l.Type))
        {
            bool Valid(Linked l) => TryOrder(l.Link.Description, out var t, out _) && t == group.Key;
            var sorted = group.OrderBy(l => Valid(l) ? 0 : 1).ThenBy(l => Valid(l) ? l.Layer : 0).ThenBy(l => l.Link.Name, StringComparer.Ordinal).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                string note = Order(group.Key, i);
                if (sorted[i].Link.Description != note)
                    updates.Add(new LinkDescription(sorted[i].Link.Id, note));
            }
        }
        return updates;
    }

    // ---------------- attach ----------------

    public static OutfitPlan Attach(InventoryModel model, Guid itemId, int point, bool add)
    {
        var target = Resolve(model, itemId);
        if (Unwearable(model, target) is { } refusal)
            return OutfitPlan.Refuse(refusal);
        if (target!.Kind != ItemKind.Object)
            return OutfitPlan.Refuse("outfit.not_wearable");
        if (point < 0 || point > MaxAttachmentPoint)
            return OutfitPlan.Refuse("outfit.bad_point");
        if (!CofKnown(model, out var cof))
            return OutfitPlan.Refuse("outfit.cof_unknown");
        if (model.ChildItems(cof).Any(l => l.LinkedId == target.Id))
            return OutfitPlan.Refuse("outfit.already_worn");
        if (model.AttachmentLimit >= 0 && WornObjects(model, cof) + 1 > model.AttachmentLimit)
            return OutfitPlan.Refuse("outfit.attach_limit");
        return Plan(model, cof, [new NewOutfitLink(target.Id, target.Name, "")], [], [new AttachRequest(target.Id, point, add)]);
    }

    /// <summary>How many objects the current outfit folder links to (the objects the avatar wears). Links to items not loaded yet
    /// are not counted: what they are is not known.</summary>
    private static int WornObjects(InventoryModel m, Guid cof) =>
        m.ChildItems(cof).Count(l => l.Kind != ItemKind.LinkFolder && l.LinkedId is { } id && m.Item(id)?.Kind == ItemKind.Object);

    // ---------------- wear an outfit ----------------

    public static OutfitPlan WearOutfit(InventoryModel model, Guid outfitFolderId)
    {
        var folder = model.Folder(outfitFolderId);
        if (folder is null || folder.Kind is not (FolderKind.Outfit or FolderKind.None) || model.IsSystemFolder(folder.Id) || model.IsInLibrary(folder.Id))
            return OutfitPlan.Refuse("outfit.no_outfit");
        if (model.LoadState(folder.Id) != FolderLoad.Loaded)
            return OutfitPlan.Refuse("outfit.not_loaded");
        var members = new List<InventoryItemData>();
        foreach (var link in model.ChildItems(folder.Id))
        {
            if (link.Kind == ItemKind.LinkFolder)
                continue;
            var target = link.LinkedId is { } id ? model.Item(id) : link;
            if (target is null)
                return OutfitPlan.Refuse("outfit.not_loaded");   // never wear part of an outfit
            if (IsWearableKind(target) && !members.Any(x => x.Id == target.Id))
                members.Add(target);
        }
        if (members.Count == 0)
            return OutfitPlan.Refuse("outfit.empty");
        if (members.Count > MaxOutfitItems)
            return OutfitPlan.Refuse("outfit.too_many");
        if (members.Any(t => Unwearable(model, t) is { } r && r != "outfit.library"))
            return OutfitPlan.Refuse("outfit.not_wearable");
        if (!CofKnown(model, out var cof))
            return OutfitPlan.Refuse("outfit.cof_unknown");

        var names = StringComparer.OrdinalIgnoreCase;
        var bodyParts = members.Where(t => t.Kind == ItemKind.Bodypart).GroupBy(WearableTypeOf).Select(g => g.OrderBy(t => t.Name, names).ThenBy(t => t.Id).First()).ToList();
        var clothing = members.Where(t => t.Kind == ItemKind.Clothing)
            .OrderBy(t => (int)WearableTypeOf(t)).ThenBy(t => LayerIn(model, folder.Id, t.Id)).ThenBy(t => t.Name, names).Take(MaxClothingLayers).ToList();
        var objects = members.Where(t => t.Kind == ItemKind.Object).ToList();
        // an outfit keeps no object that is not in it, so the objects it names are all the avatar will wear
        if (model.AttachmentLimit >= 0 && objects.Count > model.AttachmentLimit)
            return OutfitPlan.Refuse("outfit.attach_limit");
        var keep = new HashSet<Guid>(bodyParts.Concat(clothing).Concat(objects).Select(t => t.Id));

        var remove = new List<Guid>();
        var detach = new List<Guid>();
        var alreadyLinked = new HashSet<Guid>();
        Guid? sameBase = null;
        foreach (var link in model.ChildItems(cof))
        {
            if (link.Kind == ItemKind.LinkFolder)
            {
                if (link.LinkedId == folder.Id) sameBase = link.Id; else remove.Add(link.Id);
                continue;
            }
            if (link.LinkedId is not { } id)
                continue;
            var item = model.Item(id);
            var type = item is null ? WearableType.None : WearableTypeOf(item);
            bool replacedBodyPart = item is not null && IsBodyPart(type) && bodyParts.Any(b => WearableTypeOf(b) == type);
            bool managed = item is not null && (item.Kind == ItemKind.Clothing || item.Kind == ItemKind.Object || replacedBodyPart);
            if (!managed)
                continue;   // body parts the outfit does not name, and anything this viewer does not manage, stay
            if (keep.Contains(id))
            {
                alreadyLinked.Add(id);
                continue;
            }
            remove.Add(link.Id);
            if (item!.Kind == ItemKind.Object)
                detach.Add(id);
        }

        var create = new List<NewOutfitLink>();
        var layerOf = new Dictionary<WearableType, int>();
        foreach (var t in bodyParts.Where(t => !alreadyLinked.Contains(t.Id)))
            create.Add(new NewOutfitLink(t.Id, t.Name, ""));
        foreach (var t in clothing)
        {
            var type = WearableTypeOf(t);
            int layer = layerOf.GetValueOrDefault(type);
            layerOf[type] = layer + 1;
            if (!alreadyLinked.Contains(t.Id))
                create.Add(new NewOutfitLink(t.Id, t.Name, Order(type, layer)));
        }
        var attach = new List<AttachRequest>();
        foreach (var t in objects.Where(t => !alreadyLinked.Contains(t.Id)))
        {
            create.Add(new NewOutfitLink(t.Id, t.Name, ""));
            attach.Add(new AttachRequest(t.Id, 0, false));
        }
        if (sameBase is null)
            create.Add(new NewOutfitLink(folder.Id, folder.Name, "", IsFolder: true));
        return Plan(model, cof, create, remove, attach, detach);
    }

    private static int LayerOf(InventoryModel m, Guid folderId, Guid target)
    {
        var link = m.ChildItems(folderId).FirstOrDefault(i => i.LinkedId == target);
        return link is not null && TryOrder(link.Description, out _, out int layer) ? layer : 0;
    }

    private static int LayerIn(InventoryModel m, Guid folderId, Guid target) => LayerOf(m, folderId, target);

    // ---------------- save ----------------

    public static (OutfitSavePlan? Plan, string? Refusal) SaveOutfit(InventoryModel model, string name)
    {
        // an account that has no My Outfits folder gets one made under the root (the official viewer makes a missing system folder)
        var mine = model.FolderOfKind(FolderKind.MyOutfits);
        if (mine == Guid.Empty && model.RootId == Guid.Empty)
            return (null, "outfit.no_folder");
        if (!CofKnown(model, out var cof))
            return (null, "outfit.cof_unknown");
        if (!model.ChildItems(cof).Any(i => i.LinkedId is not null && i.Kind != ItemKind.LinkFolder))
            return (null, "outfit.empty");
        string clean = InventoryNames.Clean(name ?? "");
        if (clean.Length == 0)
            clean = DefaultOutfitName;
        if (!InventoryNames.Ok(clean))
            return (null, "outfit.bad_name");
        return (new OutfitSavePlan(cof, mine, clean, mine == Guid.Empty ? model.RootId : Guid.Empty), null);
    }

    // ---------------- the current outfit as a list ----------------

    public static IReadOnlyList<OutfitEntry> Current(InventoryModel model)
    {
        if (!CofKnown(model, out var cof))
            return Array.Empty<OutfitEntry>();
        var entries = new List<(OutfitEntry Entry, int Sort)>();
        foreach (var l in Links(model, cof))
        {
            var kind = l.Target?.Kind;
            int section = kind == ItemKind.Object ? 0 : kind == ItemKind.Bodypart || IsBodyPart(l.Type) && kind is not null ? 1
                : l.Type != WearableType.None ? 2 : 3;
            string name = l.Target?.Name ?? l.Link.Name;
            bool can = section is 0 or 2;
            entries.Add((new OutfitEntry(l.Link.Id, l.Link.LinkedId!.Value, name, section, l.Type, l.Layer, can), 0));
        }
        return entries.Select(e => e.Entry)
            .OrderBy(e => e.Section)
            .ThenBy(e => e.Section == 2 ? (int)e.Type : 0)
            .ThenByDescending(e => e.Section == 2 ? e.Layer : 0)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
