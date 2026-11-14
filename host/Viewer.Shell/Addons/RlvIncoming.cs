// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Shell.Addons;

/// <summary>What the restraints make of an incoming chat line or instant message before it is shown.</summary>
/// <param name="Shown">The event to show (the same, or with its text changed); null to show nothing.</param>
/// <param name="AutoReplyTo">When set, the avatar to send <paramref name="AutoReply"/> to.</param>
public readonly record struct RlvIncomingResult(TransportEvent? Shown, Guid? AutoReplyTo = null, string? AutoReply = null);

/// <summary>
/// Incoming chat and IMs under RLV, before the chat windows see them: an owner-say command line is kept out while RLV runs,
/// others' local chat is filtered under @recvchat, a person's IM is shown as blocked (and the sender told) under @recvim, and
/// a group's messages are dropped. The user's own lines, owner-say and lines to the user only are never filtered (RLVa,
/// llviewermessage.cpp process_chat_from_simulator).
/// </summary>
public static class RlvIncoming
{
    public static RlvIncomingResult Apply(TransportEvent e, AddonHost host, RlvGate gate, Guid self)
    {
        try
        {
            switch (e)
            {
                case ChatReceived { Type: ChatLineType.OwnerSay } owner:
                    return host.Swallows(new Viewer.Addons.ChatLine(Viewer.Addons.ChatKind.OwnerSay, owner.SourceId, owner.FromName, owner.Text.Reveal()))
                        ? new(null) : new(e);
                case ChatReceived { Type: ChatLineType.Say or ChatLineType.Shout or ChatLineType.Whisper } chat
                    when chat.Source == ChatSource.Agent && chat.SourceId != self || chat.Source == ChatSource.Object && chat.OwnerId != self:
                {
                    // others' avatars and objects; the user's own objects are left alone (RLVa spares the user's worn objects)
                    string text = chat.Text.Reveal();
                    string? shown = gate.Incoming(chat.SourceId, chat.Source == ChatSource.Agent, text);
                    return shown is null ? new(null) : shown == text ? new(e) : new(chat with { Text = new MessageBody(shown, chat.Text.Channel) });
                }
                case InstantMessageReceived { Kind: ImKind.GroupMessage } group:
                    return gate.CanReceiveGroup(group.SessionId) ? new(e) : new(null);
                case InstantMessageReceived { Kind: ImKind.Message, Group: false } im when im.FromAgent != Guid.Empty && im.FromAgent != self:
                {
                    var r = gate.IncomingIm(im.FromAgent, im.Text.Reveal());
                    if (r.AutoReply is null)
                        return new(e);
                    var shown = r.Text is null ? null : im with { Text = new MessageBody(r.Text, im.Text.Channel) };
                    return new(shown, im.FromAgent, r.AutoReply);
                }
                default:
                    return new(e);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(e);   // fails open
        }
    }
}
