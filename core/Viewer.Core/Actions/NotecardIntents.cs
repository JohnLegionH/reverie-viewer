// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Actions;

/// <summary>Copies an item embedded in a notecard into the user's inventory (the official viewer's "Copy to inventory",
/// indra/newview/llviewertexteditor.cpp copyInventory, which leaves the folder to the grid). The card must be in the user's
/// own inventory; <see cref="Viewer.Core.Inventory.NotecardRules.CanCopyOut"/> says when the button is offered.</summary>
public sealed record NotecardCopyItemIntent(Guid NotecardItemId, Guid EmbeddedItemId, Guid FolderId = default) : ActionIntent
{
    public override ActionKind Kind => ActionKind.NotecardCopyItem;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (NotecardItemId == Guid.Empty)
            issues.Add(new("notecard.no_card"));
        if (EmbeddedItemId == Guid.Empty)
            issues.Add(new("notecard.no_item"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new CopyFromNotecardCommand(NotecardItemId, EmbeddedItemId, FolderId);
}
