// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Core.Session;

namespace Viewer.Core.Actions;

/// <summary>Changes the current outfit: links made and removed, objects put on and taken off. The plan comes from
/// <see cref="OutfitPlanner"/>; the intent checks it once more before anything is sent.</summary>
public sealed record OutfitChangeIntent(OutfitChange Change) : ActionIntent
{
    public override ActionKind Kind => ActionKind.OutfitChange;

    /// <summary>The most links one change may make (the official viewer's limit for an outfit).</summary>
    public const int MaxLinks = OutfitPlanner.MaxOutfitItems;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        var c = Change;
        if (c is null)
            return [new("outfit.nothing")];
        if (c.CofId == Guid.Empty)
            issues.Add(new("outfit.no_cof"));
        if (c.IsEmpty)
            issues.Add(new("outfit.nothing"));
        if (c.Create.Count > MaxLinks || c.Attach.Count > MaxLinks || c.Copies.Count > MaxLinks)
            issues.Add(new("outfit.too_many"));
        // a copy needs the item to copy and the folder to copy it into, and something that uses the copy
        if (c.Copies.Any(x => x.ItemId == Guid.Empty || x.FolderId == Guid.Empty)
            || c.Copies.Any(x => !c.Create.Any(l => l.TargetId == x.ItemId) && !c.Attach.Any(a => a.ItemId == x.ItemId)))
            issues.Add(new("outfit.no_item"));
        if (c.Create.Any(l => l.TargetId == Guid.Empty) || c.RemoveLinks.Any(g => g == Guid.Empty) || c.Detach.Any(g => g == Guid.Empty)
            || c.Attach.Any(a => a.ItemId == Guid.Empty))
            issues.Add(new("outfit.no_item"));
        if (c.Attach.Any(a => a.Point < 0 || a.Point > OutfitPlanner.MaxAttachmentPoint))
            issues.Add(new("outfit.bad_point"));
        if (c.Renumbered.Any(d => d.LinkId == Guid.Empty))
            issues.Add(new("outfit.no_item"));
        if (c.Renumbered.Any(d => d.Description is null || d.Description.Length > 255 || IntentRules.HasControlChars(d.Description, false)))
            issues.Add(new("outfit.bad_name"));
        if (c.Create.Any(l => l.Name.Length > IntentRules.MaxInventoryNameChars * 4 || l.Description.Length > 255 || IntentRules.HasControlChars(l.Name, false)))
            issues.Add(new("outfit.bad_name"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new ChangeOutfitCommand(Change);
}

/// <summary>Asks the region's server-side baking to rebuild the look from the current outfit folder as it stands (the session sends it
/// once at login in a region that bakes on the server).</summary>
public sealed record LookRebuildIntent(Guid CofId) : ActionIntent
{
    public override ActionKind Kind => ActionKind.LookRebuild;

    public override IReadOnlyList<ActionIssue> Validate() => CofId == Guid.Empty ? [new("outfit.no_cof")] : [];

    internal override TransportCommand? ToCommand() => new RebuildLookCommand(CofId);
}

/// <summary>Saves the current look as a new outfit folder under My Outfits.</summary>
public sealed record OutfitSaveIntent(Guid CofId, Guid MyOutfitsId, string Name, Guid RootId = default) : ActionIntent
{
    public override ActionKind Kind => ActionKind.OutfitSave;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (CofId == Guid.Empty)
            issues.Add(new("outfit.no_cof"));
        if (MyOutfitsId == Guid.Empty && RootId == Guid.Empty)
            issues.Add(new("outfit.no_folder"));
        if (!InventoryNames.Ok(Name))
            issues.Add(new("outfit.bad_name"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new SaveOutfitCommand(CofId, MyOutfitsId, InventoryNames.Clean(Name), RootId);
}
