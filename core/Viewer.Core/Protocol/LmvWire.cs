// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>The instant message that answers an inventory offer: its dialog, the conversation (transaction) id and the binary bucket.</summary>
internal readonly record struct InventoryOfferReply(InstantMessageDialog Dialog, Guid Session, byte[] Bucket);

/// <summary>
/// Inventory offers and attaching a world object, the protocol adapter's side. Offers arrive as instant messages (dialog 4 from a
/// resident, 9 from an object); the answer is an instant message too (dialog + 1 accepts, dialog + 2 declines).
/// </summary>
internal sealed partial class LmvTransport
{
    /// <summary>An inventory offer as the core's record; null for any other dialog or a message too short to hold its item.</summary>
    internal static InventoryOffered? ToInventoryOffer(InstantMessage im)
    {
        byte[] bucket = im.BinaryBucket ?? [];
        switch (im.Dialog)
        {
            case InstantMessageDialog.InventoryOffered when bucket.Length >= 17:   // the asset type, then the item's id (anything after it is not read)
                return new InventoryOffered(im.FromAgentID.Guid, im.FromAgentName ?? "", false, im.IMSessionID.Guid, bucket[0], new Guid(bucket.AsSpan(1, 16), bigEndian: true), im.Message ?? "");
            case InstantMessageDialog.TaskInventoryOffered when bucket.Length >= 1:   // the asset type only; the item is not in inventory yet
                return new InventoryOffered(im.FromAgentID.Guid, im.FromAgentName ?? "", true, im.IMSessionID.Guid, bucket[0], Guid.Empty, im.Message ?? "");
            default:
                return null;
        }
    }

    /// <summary>The message that answers an offer.</summary>
    internal static InventoryOfferReply ToInventoryOfferReply(InventoryOfferAnswerCommand c)
    {
        var dialog = c.FromObject
            ? (c.Accept ? InstantMessageDialog.TaskInventoryAccepted : InstantMessageDialog.TaskInventoryDeclined)
            : (c.Accept ? InstantMessageDialog.InventoryAccepted : InstantMessageDialog.InventoryDeclined);
        byte[] bucket = c.Accept && c.FolderId != Guid.Empty ? c.FolderId.ToByteArray(bigEndian: true) : [];
        return new InventoryOfferReply(dialog, c.TransactionId, bucket);
    }

    /// <summary>The attachment point byte of the attach message: the point with the "add" flag, so the object is added to what is worn there.</summary>
    internal static byte AttachPointByte(int point) => (byte)(point | 0x80);

    /// <summary>The ObjectDrop message: the objects (roots, by local id) the avatar wears are put down in the world.</summary>
    internal static LibreMetaverse.Packets.ObjectDropPacket BuildObjectDrop(UUID agent, UUID session, uint localId) => new()
    {
        Header = { Reliable = true },
        AgentData = { AgentID = agent, SessionID = session },
        ObjectData = [new LibreMetaverse.Packets.ObjectDropPacket.ObjectDataBlock { ObjectLocalID = localId }],
    };

    private CommandDispatch ExecuteWire(TransportCommand command)
    {
        var self = _client.Self;
        switch (command)
        {
            case InventoryOfferAnswerCommand answer:
            {
                var reply = ToInventoryOfferReply(answer);
                self.InstantMessage(self.Name ?? "", new UUID(answer.FromId), "", new UUID(reply.Session), reply.Dialog, InstantMessageOnline.Online,
                    LibreMetaverse.Vector3.Zero, UUID.Zero, reply.Bucket);
                break;
            }
            case AttachWorldObjectCommand attach:
                if (_client.Network.CurrentSim is not { } sim)
                    return CommandDispatch.NotSent("no_region");
                _client.Objects.AttachObject(sim, attach.LocalId, (AttachmentPoint)AttachPointByte(attach.Point), LibreMetaverse.Quaternion.Identity);
                break;
            case DropAttachmentCommand drop:
                if (_client.Network.CurrentSim is not { } dropSim)
                    return CommandDispatch.NotSent("no_region");
                _client.Network.SendPacket(BuildObjectDrop(self.AgentID, self.SessionID, drop.LocalId), dropSim);
                break;
        }
        return CommandDispatch.Handed;
    }
}
