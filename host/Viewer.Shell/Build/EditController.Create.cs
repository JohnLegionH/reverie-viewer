// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Build;

/// <summary>
/// What the create tool hands back after it places a box. Behaviour follows the official viewer's LLToolPlacer and its
/// CreateToolKeepSelected setting (read as behaviour only).
/// </summary>
public sealed partial class EditController
{
    /// <summary>"Keep Tool selected" (CreateToolKeepSelected, off by default).</summary>
    public bool KeepToolSelected { get; set; }

    /// <summary>The create tool placed a box. True when the create tool stays for the next click.</summary>
    public bool Placed() => false;

    /// <summary>The box the create tool asked for arrived (made selected by the region).</summary>
    public void Created(uint local)
    {
    }
}
