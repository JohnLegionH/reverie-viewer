// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Actions;

/// <summary>How inventory names are cleaned and checked. Behaviour follows the official viewer: a name is at most 63
/// characters, the grid's field separator ('|') becomes a space, and surrounding space is trimmed.</summary>
internal static class InventoryNames
{
    public static string Clean(string name) => IntentRules.Normalised(name.Replace('|', ' '));

    public static bool Ok(string? name) =>
        name is not null && Clean(name) is { Length: > 0 and <= IntentRules.MaxInventoryNameChars } n && !IntentRules.HasControlChars(n, false);
}

/// <summary>Makes a new, plain folder under a parent folder. A system folder kind cannot be made by the user.</summary>
public sealed record InventoryFolderCreateIntent(Guid ParentId, string Name) : ActionIntent
{
    public override ActionKind Kind => ActionKind.InventoryFolderCreate;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (ParentId == Guid.Empty)
            issues.Add(new("inventory.no_folder"));
        if (!InventoryNames.Ok(Name))
            issues.Add(new("inventory.bad_name"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new CreateInventoryFolderCommand(ParentId, InventoryNames.Clean(Name));
}

/// <summary>Renames a folder. <paramref name="ParentId"/> and <paramref name="KindNumber"/> are the folder's own, sent back unchanged.</summary>
public sealed record InventoryFolderRenameIntent(Guid FolderId, Guid ParentId, int KindNumber, string NewName) : ActionIntent
{
    public override ActionKind Kind => ActionKind.InventoryFolderRename;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (FolderId == Guid.Empty)
            issues.Add(new("inventory.no_folder"));
        if (!InventoryNames.Ok(NewName))
            issues.Add(new("inventory.bad_name"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new RenameInventoryFolderCommand(FolderId, ParentId, KindNumber, InventoryNames.Clean(NewName));
}

/// <summary>Moves a folder under another folder.</summary>
public sealed record InventoryFolderMoveIntent(Guid FolderId, Guid NewParentId) : ActionIntent
{
    public override ActionKind Kind => ActionKind.InventoryFolderMove;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (FolderId == Guid.Empty || NewParentId == Guid.Empty)
            issues.Add(new("inventory.no_folder"));
        else if (FolderId == NewParentId)
            issues.Add(new("inventory.same_id"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new MoveInventoryFolderCommand(FolderId, NewParentId);
}

/// <summary>Moves a folder to the Trash folder. A protected action: refused unless <paramref name="Confirmed"/>. Never purges.</summary>
public sealed record InventoryFolderTrashIntent(Guid FolderId, Guid TrashFolderId, bool Confirmed) : ActionIntent
{
    public override ActionKind Kind => ActionKind.InventoryFolderTrash;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (FolderId == Guid.Empty)
            issues.Add(new("inventory.no_folder"));
        if (TrashFolderId == Guid.Empty)
            issues.Add(new("inventory.no_trash"));
        else if (TrashFolderId == FolderId)
            issues.Add(new("inventory.same_id"));
        if (!Confirmed)
            issues.Add(new("inventory.not_confirmed"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new MoveInventoryFolderCommand(FolderId, TrashFolderId);
}
