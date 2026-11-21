// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Interaction;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>One entry of a right-click menu. <paramref name="LabelKey"/> is also its accessible name. An entry with
/// <paramref name="Children"/> opens a sub-menu; <paramref name="Literal"/> is a label that is not a string-table key (a worn item's name).</summary>
public sealed record ContextEntry(string Id, string LabelKey, bool Enabled = true, IReadOnlyList<ContextEntry>? Children = null, string? Literal = null, string? ReasonKey = null);

/// <summary>What the menu for an object depends on.</summary>
public readonly record struct ObjectMenuContext(PrimInteraction? Self, PrimInteraction? Root, bool Attachment, bool SittingOnIt, bool Sitting, bool IsBlocked = false, bool CanDetach = false,
    bool ForSale = false, bool CanBuy = false, bool NoMoney = false);

/// <summary>What the menu for an avatar depends on.</summary>
/// <param name="NoMoney">The grid has no money: there is no Pay.</param>
public readonly record struct AvatarMenuContext(bool IsSelf, bool IsFriend, bool IsBlocked = false, bool NoMoney = false);

/// <summary>An item the own avatar wears: its id in inventory and its name.</summary>
public readonly record struct WornEntry(Guid Id, string Name, Viewer.Core.Inventory.WearableType Type = Viewer.Core.Inventory.WearableType.None, int Layer = 0, bool IsObject = false);

/// <summary>What the menu for the own avatar depends on.</summary>
/// <param name="CanDetach">Whether a worn object may be taken off (restraints may lock one); null: every one may.</param>
public readonly record struct SelfMenuContext(bool Sitting, bool Flying, IReadOnlyList<WornEntry> Worn, IReadOnlyList<WornEntry>? Attachments = null,
    Func<Guid, bool>? CanDetach = null);

/// <summary>What the menu for a spot of ground depends on: whether it is within the avatar's reach for sitting.</summary>
public readonly record struct LandMenuContext(bool PointKnown, bool WithinSitReach);

/// <summary>The attachment points the "Attach To" menus offer: the body's, and the screen's (HUD) in a menu of their own.</summary>
public static class AttachmentMenu
{
    /// <summary>The first and last HUD attachment points (the points shown on the screen, not on the body).</summary>
    public const int FirstHud = 31, LastHud = 38;

    public static IReadOnlyList<int> HudPoints { get; } = Enumerable.Range(FirstHud, LastHud - FirstHud + 1).ToArray();

    public static IReadOnlyList<int> BodyPoints { get; } = Enumerable.Range(1, 55).Where(p => p < FirstHud || p > LastHud).ToArray();
}

/// <summary>The right-click menus as data: which entries appear and which are enabled. The viewer draws them; choosing one runs a command.</summary>
public static class ContextMenus
{
    public const float SitReachMetres = 3f;

    public static IReadOnlyList<ContextEntry> ForObject(ObjectMenuContext c)
    {
        var root = c.Root ?? c.Self;
        if (c.Attachment)
        {
            // An object the user wears: touch, inspect and Detach (official viewer, menu_attachment_self.xml: "Detach item", enabled by
            // Attachment.EnableDetach). The world-object entries (sit, take, attach) do not apply to something worn.
            return
            [
                new("touch", "menu.touch", ClickAction.Touchable(c.Self, c.Root)),
                new("inspect", "menu.inspect"),
                new("detach", "menu.detach", c.CanDetach),
                // Drop (menu_attachment_self.xml, Attachment.Drop) asks first, as the official viewer's AttachmentDrop notification does:
                // choosing it closes the menu and opens the confirmation dialog; only its first button sends the drop.
                new("drop", "menu.drop", c.CanDetach),
            ];
        }
        var list = new List<ContextEntry>
        {
            new("touch", "menu.touch", ClickAction.Touchable(c.Self, c.Root)),
            c.SittingOnIt ? new("stand", "menu.stand") : new("sit", "menu.sit_here"),
            new("open", "menu.open", ClickAction.OpenAllowed(root)),
            new("inspect", "menu.inspect"),
        };
        uint flags = root?.Flags ?? 0;
        // Pay only for an object that takes money (a script with a money handler); with no currency on the grid none does
        if (!c.NoMoney && ((flags & ObjectFlagBits.TakesMoney) != 0 || ((c.Self?.Flags ?? 0) & ObjectFlagBits.TakesMoney) != 0))
            list.Insert(2, new("pay", "menu.pay"));
        if (!c.Attachment)
        {
            // Buy takes Take's place for an object that is for sale and not the agent's own (llviewermenu.cpp, is_selection_buy_not_take)
            if (c.ForSale && (flags & ObjectFlagBits.YouOwner) == 0)
                list.Add(new("buy", "menu.buy", c.CanBuy));
            else
                list.Add(new("take", "menu.take", Viewer.Core.Building.BuildRules.CanTake(flags, c.Attachment, c.SittingOnIt)));
            if ((flags & ObjectFlagBits.Copy) != 0)
                list.Add(new("take_copy", "menu.take_copy"));
            // Delete sends the object to the agent's Trash. It asks first, with the official question, only for an object that is locked,
            // cannot be copied or is not the agent's own (llselectmgr.cpp, selectDelete); the controller opens that dialog when the entry is chosen.
            list.Add(new("delete", "menu.delete", Viewer.Core.Building.BuildRules.CanDelete(flags, c.Attachment)));
            // "Put on" in the official viewer's object menu (menu_object.xml): Attach and Attach HUD, each a list of points. Enabled only for an
            // object the user owns and is not sitting on (llviewermenu.cpp, object_is_wearable)
            bool wearable = (flags & ObjectFlagBits.YouOwner) != 0 && !c.SittingOnIt;
            list.Add(new("attach", "menu.attach", wearable, AttachPoints(AttachmentMenu.BodyPoints, wearable)));
            list.Add(new("attach_hud", "menu.attach_hud", wearable, AttachPoints(AttachmentMenu.HudPoints, wearable)));
        }
        list.Add(c.IsBlocked ? new("unblock", "menu.unblock") : new("block", "menu.block"));
        // Return stands with Block in the official "Manage" list (menu_object.xml); it asks first with the official question (the controller
        // opens the dialog). The region decides whether the agent may return the object.
        list.Add(new("return", "menu.return", Viewer.Core.Building.BuildRules.CanReturn(c.Attachment)));
        return list;
    }

    private static IReadOnlyList<ContextEntry> AttachPoints(IReadOnlyList<int> points, bool enabled) =>
        points.Select(p => new ContextEntry($"attach/{p}", $"outfit.point.{p}", enabled)).ToArray();

    public static IReadOnlyList<ContextEntry> ForAvatar(AvatarMenuContext c)
    {
        if (c.IsSelf)
            return [new("appearance", "menu.appearance")];
        var list = new List<ContextEntry> { new("im", "menu.im") };
        if (!c.IsFriend)
            list.Add(new("add_friend", "menu.add_friend"));
        list.Add(new("offer_teleport", "menu.offer_teleport"));
        list.Add(new("request_teleport", "menu.request_teleport"));
        list.Add(new("inspect", "menu.inspect"));
        list.Add(c.IsBlocked ? new("unblock", "menu.unblock") : new("block", "menu.block"));
        return list;
    }


    /// <summary>The menu a resident's name in nearby chat or an instant message opens, in the official viewer's order for an avatar's name
    /// (menu_url_agent.xml and menu_avatar_icon.xml): View Profile, IM, Offer and Request Teleport, Add Friend, Pay, Share, Block, Report Abuse,
    /// Copy Name. What this viewer cannot do yet is greyed and carries the reason as <see cref="ContextEntry.ReasonKey"/>.</summary>
    public static IReadOnlyList<ContextEntry> ForName(AvatarMenuContext c)
    {
        const string notBuilt = "menu.name.reason.not_built";
        var list = new List<ContextEntry>();
        list.Add(new("profile", "menu.name.profile", false, ReasonKey: notBuilt));
        if (!c.IsSelf)
        {
            list.Add(new("im", "menu.im"));
            list.Add(new("offer_teleport", "menu.offer_teleport"));
            list.Add(new("request_teleport", "menu.request_teleport"));
            list.Add(c.IsFriend ? new("remove_friend", "menu.name.remove_friend", false, ReasonKey: "menu.name.reason.use_people") : new("add_friend", "menu.add_friend"));
            if (!c.NoMoney)
                list.Add(new("pay", "menu.name.pay", false, ReasonKey: notBuilt));
            list.Add(new("share", "menu.name.share", false, ReasonKey: notBuilt));
            list.Add(c.IsBlocked ? new("unblock", "menu.unblock") : new("block", "menu.block"));
            list.Add(new("report", "menu.name.report", false, ReasonKey: notBuilt));
        }
        list.Add(new("copy_name", "menu.name.copy_name"));
        return list;
    }
    /// <summary>The menu of the own avatar: Appearance, Inventory, Take Off (the worn items), Stand Up while seated, Fly or Stop Flying.
    /// After the official viewer's own-avatar menu (menu_avatar_self.xml), limited to what this viewer has.</summary>
    public static IReadOnlyList<ContextEntry> ForSelf(SelfMenuContext c)
    {
        // Take Off: the worn items, then Detach with the objects worn (official viewer, menu_avatar_self.xml: the Take Off menu holds a
        // "Detach" sub-menu of the attachments); Detach All follows it.
        // Clothes first: one entry per clothing type in the official menu's order, each enabled while one of that type is worn (it takes off the
        // top layer of that type), then All Clothes (menu_avatar_self.xml, the "Clothes" menu; llviewermenu.cpp, LLEditEnableTakeOff).
        var clothes = ClothesOrder.Select(t => new ContextEntry($"take_off_type/{TypeName(t)}", $"menu.take_off.{TypeName(t)}", c.Worn.Any(w => !w.IsObject && w.Type == t)))
            .Append(new ContextEntry("take_off_all", "menu.take_off.all_clothes")).ToArray();
        var takeOff = new List<ContextEntry> { new("clothes", "menu.take_off.clothes", true, clothes) };
        // an object restraints have locked is greyed (RLVa greys Take Off and Detach for it)
        var canDetach = c.CanDetach ?? (_ => true);
        takeOff.AddRange(c.Worn.Select(w => new ContextEntry($"take_off/{w.Id}", "menu.take_off", !w.IsObject || canDetach(w.Id), null, w.Name)));
        var attachments = c.Attachments ?? [];
        if (attachments.Count > 0)
            takeOff.Add(new("detach", "menu.detach", true, attachments.Select(a => new ContextEntry($"detach/{a.Id}", "menu.detach", canDetach(a.Id), null, a.Name)).ToArray()));
        var list = new List<ContextEntry>
        {
            new("appearance", "menu.appearance"),
            new("inventory", "menu.inventory"),
            new("take_off", "menu.take_off", c.Worn.Count > 0 || attachments.Count > 0, takeOff),
            new("detach_all", "menu.detach_all", attachments.Any(a => canDetach(a.Id))),   // menu_avatar_self.xml: Detach All, enabled while something is worn
        };
        if (c.Sitting)
            list.Add(new("stand", "menu.stand"));
        else
            list.Add(c.Flying ? new("stop_fly", "menu.stop_fly") : new("fly", "menu.fly"));
        return list;
    }

    /// <summary>The clothing types of the own avatar's Take Off, then Clothes menu, in the official menu's order (menu_avatar_self.xml).</summary>
    public static readonly IReadOnlyList<Viewer.Core.Inventory.WearableType> ClothesOrder =
    [
        Viewer.Core.Inventory.WearableType.Shirt, Viewer.Core.Inventory.WearableType.Pants, Viewer.Core.Inventory.WearableType.Skirt,
        Viewer.Core.Inventory.WearableType.Shoes, Viewer.Core.Inventory.WearableType.Socks, Viewer.Core.Inventory.WearableType.Jacket,
        Viewer.Core.Inventory.WearableType.Gloves, Viewer.Core.Inventory.WearableType.Undershirt, Viewer.Core.Inventory.WearableType.Underpants,
        Viewer.Core.Inventory.WearableType.Tattoo, Viewer.Core.Inventory.WearableType.Physics, Viewer.Core.Inventory.WearableType.Alpha,
    ];

    /// <summary>The type's name as the official viewer's wearable dictionary spells it (llwearabletype.cpp): "shirt", "alpha" and so on.</summary>
    public static string TypeName(Viewer.Core.Inventory.WearableType t) => t.ToString().ToLowerInvariant();

    public static IReadOnlyList<ContextEntry> ForLand(LandMenuContext c) =>
    [
        // the official ground menu (menu_land.xml) in its order: About Land, Sit Here, Buy This Land, Buy Pass, Build, Edit Terrain, Block
        // Particle Owner. Only what Reverie can do is here; buying land, editing terrain and blocking a particle owner wait for their features.
        new("about_land", "menu.about_land"),
        new("sit_here", "menu.sit_here", c.PointKnown && c.WithinSitReach),
        new("build", "menu.build"),
    ];
}
