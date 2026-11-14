// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Appearance;
using Viewer.Core.Inventory;
using Viewer.Core.Session;

namespace Viewer.Core.Actions;

/// <summary>Saves an edited wearable over its own inventory item, then asks the grid's baking service to rebuild the look.</summary>
public sealed record WearableSaveIntent(Guid ItemId, string Name, WearableType Type, byte[] Data) : ActionIntent
{
    public override ActionKind Kind => ActionKind.WearableSave;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (ItemId == Guid.Empty)
            issues.Add(new("wearable.no_item"));
        WearableRules.Check(issues, Name, Type, Data);
        return issues;
    }

    internal override TransportCommand? ToCommand() => new SaveWearableCommand(ItemId, InventoryNames.Clean(Name), WearableKinds.AssetType(Type), Data);
}

/// <summary>Saves an edited wearable as a new inventory item (a copy of the original, in a folder), wears the copy in the original's place, then asks the baking service to rebuild.</summary>
public sealed record WearableSaveAsIntent(Guid SourceItemId, Guid FolderId, string Name, WearableType Type, byte[] Data) : ActionIntent
{
    public override ActionKind Kind => ActionKind.WearableSaveAs;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (SourceItemId == Guid.Empty)
            issues.Add(new("wearable.no_item"));
        if (FolderId == Guid.Empty)
            issues.Add(new("wearable.no_folder"));
        WearableRules.Check(issues, Name, Type, Data);
        return issues;
    }

    internal override TransportCommand? ToCommand() => new SaveWearableAsCommand(SourceItemId, FolderId, InventoryNames.Clean(Name), WearableKinds.AssetType(Type), Data);
}

/// <summary>The checks both save intents share: the name, the type, and that the file is a wearable of that type within the size limit.</summary>
internal static class WearableRules
{
    public static void Check(List<ActionIssue> issues, string? name, WearableType type, byte[]? data)
    {
        if (!InventoryNames.Ok(name))
            issues.Add(new("wearable.bad_name"));
        if (!WearableKinds.TryFromNumber((int)type, out _))
            issues.Add(new("wearable.bad_type"));
        if (data is not { Length: > 0 })
            issues.Add(new("wearable.no_data"));
        else if (data.Length > WearableAsset.MaxBytes)
            issues.Add(new("wearable.too_big"));
        else if (!WearableAsset.TryParse(data, out var asset, out _) || asset.Type != type)
            issues.Add(new("wearable.bad_data"));
    }
}
