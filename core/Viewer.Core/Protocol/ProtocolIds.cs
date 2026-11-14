// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;

namespace Viewer.Core.Protocol;

/// <summary>
/// The protocol boundary for identifiers. LibreMetaverse types are allowed only inside
/// Viewer.Core.Protocol; everything outside it sees plain .NET types (ADR-005, ADR-007).
/// </summary>
internal static class ProtocolIds
{
    public static Guid ToGuid(UUID id) => id.Guid;

    public static UUID FromGuid(Guid id) => new(id);
}
