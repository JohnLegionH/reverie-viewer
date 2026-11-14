// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>
/// A transport whose current circuit can be made to take in no more packets, so it goes quiet and the library times it out as it would on a
/// dead network. Present in every build (the core and shell assemblies are the same in the development and the shipping export, which a
/// scan checks), but only the development build's automation command calls it: no shipping code does, and the export scan checks that.
/// </summary>
public interface IInboundGate
{
    /// <summary>Stops taking in packets from the current circuit. False when there is no circuit to stop.</summary>
    bool CloseInbound();
}
