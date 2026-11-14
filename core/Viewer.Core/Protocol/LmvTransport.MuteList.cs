// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Packets;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

internal sealed partial class LmvTransport
{
    /// <summary>The generic message the grid sends in place of a list file when the account has nothing blocked.</summary>
    internal const string EmptyMuteListMethod = "emptymutelist";

    /// <summary>
    /// The grid's answer to a block list request for an account with nothing blocked: an empty list. The official viewer treats the same
    /// message as the list having loaded (indra/newview/llmutelist.cpp, LLDispatchEmptyMuteList); the protocol library does not handle it,
    /// so the list would stay "requested" until the wait ran out. Null for any other generic message.
    /// </summary>
    internal static MuteListReceived? ToEmptyMuteList(GenericMessagePacket packet) =>
        string.Equals(Utils.BytesToString(packet.MethodData.Method), EmptyMuteListMethod, StringComparison.Ordinal) ? new MuteListReceived([]) : null;

    private void OnGenericMessage(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is GenericMessagePacket p && ToEmptyMuteList(p) is { } empty)
            Raise(empty);
    }
}
