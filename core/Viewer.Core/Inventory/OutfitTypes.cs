// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Inventory;

/// <summary>The protocol's wearable type numbers. The first four are body parts; the rest are clothing layers.</summary>
public enum WearableType
{
    None = -1,
    Shape = 0, Skin = 1, Hair = 2, Eyes = 3,
    Shirt = 4, Pants = 5, Shoes = 6, Socks = 7, Jacket = 8, Gloves = 9, Undershirt = 10, Underpants = 11, Skirt = 12,
    Alpha = 13, Tattoo = 14, Physics = 15, Universal = 16,
}

/// <summary>A link to make in the current outfit folder. <paramref name="IsFolder"/> is true for the link to the outfit folder
/// the current look was taken from.</summary>
public sealed record NewOutfitLink(Guid TargetId, string Name, string Description, bool IsFolder = false);

/// <summary>Asks the region to rez an inventory object on the avatar. Point 0 means the point the object was last worn at.
/// <paramref name="Add"/> keeps what is already on that point.</summary>
public sealed record AttachRequest(Guid ItemId, int Point, bool Add);

/// <summary>
/// One change of the current outfit, worked out from the inventory and checked against the rules before it is sent: links to
/// make and remove in the current outfit folder, objects to put on and take off. The grid's server-side baking is then asked
/// to rebuild the look from the folder; this viewer never bakes or sends a look of its own.
/// </summary>
public sealed record OutfitChange(Guid CofId, IReadOnlyList<NewOutfitLink> Create, IReadOnlyList<Guid> RemoveLinks,
    IReadOnlyList<AttachRequest> Attach, IReadOnlyList<Guid> Detach, IReadOnlyList<LibraryCopy>? CopyFirst = null, IReadOnlyList<LinkDescription>? NewDescriptions = null)
{
    public bool IsEmpty => Create.Count == 0 && RemoveLinks.Count == 0 && Attach.Count == 0 && Detach.Count == 0 && Renumbered.Count == 0;

    /// <summary>Layer notes of links that stay in the current outfit folder and are written again so each clothing type's layers run
    /// 0, 1, 2 and so on after a layer has gone.</summary>
    public IReadOnlyList<LinkDescription> Renumbered => NewDescriptions ?? Array.Empty<LinkDescription>();

    /// <summary>The Library items that are copied into the account's inventory before they are linked or put on.</summary>
    public IReadOnlyList<LibraryCopy> Copies => CopyFirst ?? Array.Empty<LibraryCopy>();

    /// <summary>This change with each copied Library item replaced by the copy the grid made of it, in the links and the attachments.</summary>
    public OutfitChange WithCopies(IReadOnlyDictionary<Guid, Guid> copyOf) =>
        copyOf.Count == 0 ? this : this with
        {
            Create = Create.Select(l => copyOf.TryGetValue(l.TargetId, out var c) ? l with { TargetId = c } : l).ToList(),
            Attach = Attach.Select(a => copyOf.TryGetValue(a.ItemId, out var c) ? a with { ItemId = c } : a).ToList(),
        };
}

/// <summary>A new description (the layer note) for a link that is already in the current outfit folder.</summary>
public sealed record LinkDescription(Guid LinkId, string Description);

/// <summary>A Library item to copy into the folder <paramref name="FolderId"/> of the account's inventory before it is worn,
/// as the official viewer copies one before wearing it.</summary>
public sealed record LibraryCopy(Guid ItemId, Guid FolderId);

/// <summary>A plan, or the reason there is none (a code the interface maps to a text; never user content).</summary>
public readonly record struct OutfitPlan(OutfitChange? Change, string? Refusal)
{
    public bool Ok => Change is not null;
    public static OutfitPlan Refuse(string code) => new(null, code);
}

/// <summary>What the outfit save needs: where the new outfit folder goes and what it is called.</summary>
public sealed record OutfitSavePlan(Guid CofId, Guid MyOutfitsId, string Name, Guid RootId = default);

/// <summary>One line of the current-outfit view: a link in the current outfit folder, resolved to the item it points to.</summary>
/// <param name="Section">0 attachments, 1 body parts, 2 clothing.</param>
public sealed record OutfitEntry(Guid LinkId, Guid TargetId, string Name, int Section, WearableType Type, int Layer, bool CanTakeOff);

/// <summary>An outfit command has run its course: the links are changed (or saving failed) and, for a change, the grid's
/// server-side baking was asked to rebuild the look from the current outfit folder.</summary>
/// <param name="Save">True for saving an outfit, false for a change of the current outfit.</param>
/// <param name="Reason">A short code when <paramref name="Ok"/> is false (<c>no_service</c>, <c>no_version</c>, <c>refused</c>, <c>missing_item</c>, <c>copy_failed</c>, <c>failed</c>).</param>
/// <param name="Touched">Folders of the account's inventory the command put something in (where Library copies landed); they are read again.</param>
public sealed record OutfitCommandFinished(bool Save, bool Ok, string Reason, int CofVersion, Guid Folder = default, IReadOnlyList<Guid>? Touched = null) : TransportEvent;

/// <summary>The rebuild of the look asked for at login has run its course: the region's server-side baking was asked to rebuild the
/// look from the current outfit folder at <paramref name="CofVersion"/>.</summary>
/// <param name="Reason">A short code when <paramref name="Ok"/> is false, as for <see cref="OutfitCommandFinished"/>.</param>
public sealed record LookRebuildFinished(bool Ok, string Reason, int CofVersion) : TransportEvent;
