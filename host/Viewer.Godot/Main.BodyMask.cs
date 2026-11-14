// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
#if NV_AUTOMATION
using Godot;
using Viewer.Godot.Presentation;

namespace Viewer.Godot;

/// <summary>The own avatar's outline picture for the automation API (development builds): <see cref="BodyMask"/> from the view's camera as it is.</summary>
public partial class Main
{
    /// <summary>The view's camera (null before the world is shown).</summary>
    internal Camera3D? ViewCamera => _camera;

    /// <summary>Draws the own avatar's body as a mask, or as before with null; <paramref name="hiddenToo"/> also draws the parts its worn objects hide. False when no own body is drawn.</summary>
    internal bool SetOwnMask(Material? mask, bool hiddenToo, out System.Numerics.Matrix4x4 head)
    {
        head = default;
        return OwnAvatars is { } avatars && avatars.Scene.Self is { } self && _bodyHost is { } host && host.SetMask(self.AgentId, mask, hiddenToo, out head);
    }
}
#endif
