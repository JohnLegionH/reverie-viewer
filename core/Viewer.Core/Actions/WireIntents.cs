// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Actions;

/// <summary>Accepts or declines an inventory offer from a resident or an object (see <see cref="InventoryOffered"/>).</summary>
public sealed record InventoryOfferAnswerIntent(Guid FromId, Guid TransactionId, bool FromObject, bool Accept, Guid FolderId) : ActionIntent
{
    public override ActionKind Kind => ActionKind.InventoryOfferAnswer;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (FromId == Guid.Empty)
            issues.Add(new("offer.no_sender"));
        if (TransactionId == Guid.Empty)
            issues.Add(new("offer.no_transaction"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new InventoryOfferAnswerCommand(FromId, TransactionId, FromObject, Accept, FolderId);
}

/// <summary>Attaches an object lying in the world to an attachment point of the own avatar.</summary>
public sealed record AttachWorldObjectIntent(uint LocalId, int Point) : ActionIntent
{
    public override ActionKind Kind => ActionKind.AttachWorldObject;

    public override IReadOnlyList<ActionIssue> Validate()
    {
        var issues = new List<ActionIssue>();
        if (LocalId == 0)
            issues.Add(new("attach.no_object"));
        if (Point is < 1 or > Viewer.Core.Inventory.OutfitPlanner.MaxAttachmentPoint)
            issues.Add(new("attach.bad_point"));
        return issues;
    }

    internal override TransportCommand? ToCommand() => new AttachWorldObjectCommand(LocalId, Point);
}

/// <summary>Drops a worn object into the world where the avatar stands: the object's root, by its local id (the official viewer's
/// Attachment.Drop, llviewermenu.cpp callback_attachment_drop, which sends the ObjectDrop message for the selected root).</summary>
public sealed record DropAttachmentIntent(uint LocalId) : ActionIntent
{
    public override ActionKind Kind => ActionKind.DropAttachment;
    public override IReadOnlyList<ActionIssue> Validate() => LocalId == 0 ? [new("drop.no_object")] : [];
    internal override TransportCommand? ToCommand() => new DropAttachmentCommand(LocalId);
}
