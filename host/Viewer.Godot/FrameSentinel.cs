// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using Godot;

namespace Viewer.Godot;

/// <summary>
/// A node that only notes the clock when the engine reaches it in a frame. Two of them, one processed before every other node and one
/// after, bracket the frame's node processing for the frame-time log, so a long frame can be placed before the nodes (the engine's
/// deferred calls), among the nodes, or after them (drawing and presenting). Added only while the log is on.
/// </summary>
public partial class FrameSentinel : Node
{
    /// <summary>The clock (<see cref="Stopwatch.GetTimestamp"/>) when this node was last processed.</summary>
    public long Stamp { get; private set; }

    public override void _Process(double delta) => Stamp = Stopwatch.GetTimestamp();
}
