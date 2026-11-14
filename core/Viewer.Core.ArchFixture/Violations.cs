// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.ArchFixture.World;

/// <summary>Violation 1: a Godot type in the public surface of a "core" assembly.</summary>
public sealed class GodotLeak
{
    public Godot.Vector3 Position { get; set; }
}

/// <summary>Violation 2: a LibreMetaverse type used outside any *.Protocol namespace.</summary>
public sealed class ProtocolLeak
{
    public Guid Convert(LibreMetaverse.UUID id) => id.Guid;
}

/// <summary>Violation 3 (ADR-006): statics that could hold per-session state. SessionInvariantTests must catch both.</summary>
public static class SessionStatics
{
    public static int SessionCounter;
    public static readonly Dictionary<Guid, string> SessionsById = new();
}

/// <summary>Violation 4 (ADR-015): a command handed to the transport without going through the action pipeline.</summary>
public sealed class PipelineBypass
{
    public bool Say(Viewer.Core.Session.ICommandTransport transport) =>
        transport.Execute(new Viewer.Core.Session.SayCommand("hello", 0, Viewer.Core.Session.ChatKind.Say)).Sent;
}
