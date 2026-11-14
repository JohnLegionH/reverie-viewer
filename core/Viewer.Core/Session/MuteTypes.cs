// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

// The block list ("mute list") as the protocol carries it. Behaviour follows the official viewer's mute list, written from a
// behaviour spec in our own words (no code, text or layout copied). Entries hold names of people and objects: they are personal
// data, so none of these records is ever written to a log or a capture with its content.

/// <summary>What kind of thing an entry blocks. The numbers are the protocol's.</summary>
public enum MuteKind { ByName = 0, Resident = 1, Object = 2, Group = 3, External = 4 }

/// <summary>The protocol's mute flags. A set bit means that property is NOT blocked (so 0 blocks everything).</summary>
public static class MuteFlag
{
    public const uint TextChat = 1;
    public const uint VoiceChat = 2;
    public const uint Particles = 4;
    public const uint ObjectSounds = 8;
    public const uint All = 15;
}

/// <summary>One entry of the block list. <paramref name="Id"/> is empty for a by-name entry.</summary>
public sealed record MuteEntry(MuteKind Kind, Guid Id, string Name, uint Flags = 0);

/// <summary>The grid's block list, as the grid sent it (an empty list is a list of nothing).</summary>
public sealed record MuteListReceived(IReadOnlyList<MuteEntry> Entries) : TransportEvent;

/// <summary>A transport that can ask the grid for the block list (the answer comes back as <see cref="MuteListReceived"/>). A read.</summary>
public interface IMuteListRequester
{
    void RequestMuteList();
}

/// <summary>Adds (or changes the flags of) one entry on the grid's list.</summary>
public sealed record MuteAddCommand(MuteEntry Entry) : TransportCommand;

/// <summary>Removes one entry from the grid's list.</summary>
public sealed record MuteRemoveCommand(Guid Id, string Name) : TransportCommand;

/// <summary>What <c>ViewerSession.Block</c> did: what the list said about the entry (a refusal sends nothing) and the pipeline's answer when one was sent.</summary>
public readonly record struct BlockOutcome(Viewer.Core.Social.MuteAddResult Result, Viewer.Core.Actions.ActionResult? Sent)
{
    /// <summary>True when the entry is now on the list (added or its flags changed) and was sent.</summary>
    public bool Done => Sent is { Sent: true } && Result is Viewer.Core.Social.MuteAddResult.Added or Viewer.Core.Social.MuteAddResult.Changed;
}
