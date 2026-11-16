// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Build;

/// <summary>
/// What the create tool hands back after it places a box. Behaviour follows the official viewer's LLToolPlacer and its
/// CreateToolKeepSelected setting, and the selection of an object that arrives made selected (llviewerobjectlist.cpp), read as behaviour only.
/// </summary>
public sealed partial class EditController
{
    /// <summary>"Keep Tool selected" (CreateToolKeepSelected, off by default): the create tool stays after a placement.</summary>
    public bool KeepToolSelected { get; set; }

    /// <summary>The create tool placed a box: unless Keep Tool selected is ticked, the tool becomes Edit with the Move handles (the official
    /// placer switches to the translate tool after a placement). True when the create tool stays for the next click.</summary>
    public bool Placed()
    {
        if (KeepToolSelected)
            return true;
        Manip = ManipMode.Move;
        SelectTool(BuildTool.Edit);
        return false;
    }

    /// <summary>The box the create tool asked for arrived, made selected by the region: it becomes the selection while build mode is on (the
    /// official viewer selects such an object unless the plain pointer tool is in use, which here means build mode has closed).</summary>
    public void Created(uint local)
    {
        if (!BuildMode || _world.Object(new Viewer.Core.World.ObjectId(_world.Epoch, local)) is null)
            return;
        SendDeselect(_selection.Where(s => s != local));
        _selection.Clear();
        _faceSelection.Clear();
        Add(local);
        Changed?.Invoke();
    }
}
