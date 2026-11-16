// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.World;

namespace Viewer.Shell.Build;

/// <summary>
/// Puts the new-prim settings on each box Create makes, once the region sends it: a box the create tool asked for arrives made selected,
/// owned by the agent with every permission, and each such arrival takes one waiting create (Firestorm counts its creates the same way and
/// applies its build preferences to the next such object; read as behaviour). Only the settings that differ from the region's own are sent.
/// </summary>
public sealed class NewPrimTracker
{
    private readonly Func<ActionIntent, ActionResult> _submit;
    private readonly Func<NewPrimDefaults> _defaults;

    /// <summary>The faces of the box Create makes (a square profile on a straight path).</summary>
    public const int BoxFaces = 6;

    public NewPrimTracker(Func<ActionIntent, ActionResult> submit, Func<NewPrimDefaults> defaults)
    {
        _submit = submit;
        _defaults = defaults;
    }

    /// <summary>Creates sent and not yet seen.</summary>
    public int Waiting { get; private set; }

    /// <summary>A create was sent.</summary>
    public void Expect() { }

    /// <summary>An object arrived in the world: when it is a box this viewer just made, the settings go on it. True when it was one.</summary>
    public bool Arrived(ObjectState o) => false;
}
