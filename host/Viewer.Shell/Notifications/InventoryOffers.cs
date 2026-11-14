// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Session;

namespace Viewer.Shell.Notifications;

// Behaviour follows the official viewer's inventory offers, written from a behaviour spec in our own words (no code, text or layout copied).

/// <summary>
/// Turns inventory offers (from a resident or an object) into notifications with Keep, Discard and Block, and sends the answers through
/// the action pipeline. An offer from a blocked sender is declined at once and never shown. Nothing is accepted without the user's
/// answer. Main thread only.
/// </summary>
public sealed class InventoryOffers
{
    /// <summary>The Keep and Discard buttons' indices.</summary>
    public const int KeepIndex = 0, DiscardIndex = 1;

    private readonly NotificationModel _model;
    private readonly Func<ActionIntent, ActionResult> _submit;

    public InventoryOffers(NotificationModel model, Func<ActionIntent, ActionResult> submit)
    {
        _model = model;
        _submit = submit;
    }

    /// <summary>Whether a sender is blocked (id, name). Nothing is until the block list is wired.</summary>
    public Func<Guid, string, bool> IsBlocked { get; set; } = (_, _) => false;

    /// <summary>The folder an item of an asset type goes to when kept; empty when it is not known (the item stays where the grid put it).</summary>
    public Func<int, Guid> FolderFor { get; set; } = _ => Guid.Empty;

    /// <summary>The Trash folder; empty when not known.</summary>
    public Func<Guid> TrashFolder { get; set; } = () => Guid.Empty;

    /// <summary>Blocks a resident or an object (kind, id, name) when the user chooses Block.</summary>
    public Action<MuteKind, Guid, string>? Block { get; set; }

    /// <summary>Offers declined at once because the sender is blocked.</summary>
    public int Declined { get; private set; }

    public void Receive(TransportEvent e)
    {
        if (e is not InventoryOffered offer)
            return;
        if (IsBlocked(offer.FromId, offer.FromName))
        {
            Declined++;
            Decline(offer);
            return;
        }
        _model.Post(new Notification
        {
            Kind = NotificationKind.Offer,
            Title = NoticeText.Raw(offer.FromName),
            Body = NoticeText.Raw(offer.Description),
            Buttons = [new NoticeButton(NoticeText.Of("button.keep"), KeepIndex), new NoticeButton(NoticeText.Of("button.discard"), DiscardIndex)],
            CanBlock = true,
            Source = offer.FromId,
            Sound = offer.FromObject ? null : Viewer.Shell.Sound.UiSoundSlot.NewIncomingIMSession,   // a person's give; an object's is silent
            Responder = a =>
            {
                switch (a.Button)
                {
                    case KeepIndex:
                        _submit(new InventoryOfferAnswerIntent(offer.FromId, offer.TransactionId, offer.FromObject, true, FolderFor(offer.AssetType)));
                        break;
                    case DiscardIndex:
                        Decline(offer);
                        break;
                    case NotificationAnswer.BlockIndex:
                        Decline(offer);
                        Block?.Invoke(offer.FromObject ? MuteKind.Object : MuteKind.Resident, offer.FromId, offer.FromName);
                        break;
                }
            },
        });
    }

    /// <summary>Declines the offer; an item a resident's offer already put in inventory goes to the Trash (the official viewer discards it the same way).</summary>
    private void Decline(InventoryOffered offer)
    {
        _submit(new InventoryOfferAnswerIntent(offer.FromId, offer.TransactionId, offer.FromObject, false, Guid.Empty));
        var trash = TrashFolder();
        if (!offer.FromObject && offer.ItemId != Guid.Empty && trash != Guid.Empty)
            _submit(new InventoryMoveIntent(offer.ItemId, trash));
    }
}
