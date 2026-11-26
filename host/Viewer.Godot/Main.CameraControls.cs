// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Godot;

/// <summary>The Camera controls window and the picked camera view. Nothing here runs from frame to frame.</summary>
public partial class Main
{
    private CameraControlsWindow? _cameraControls;

    private void InstallCameraControls()
    {
        if (_shell is null)
            return;
        _cameraControls ??= CameraControlsFeature.Install(_shell, UserSettings(null),
            view => _follow.ApplyView(view.Offset, view.Focus), () => _follow.CurrentView());
        var picked = _cameraControls.Picked;
        _follow.ApplyView(picked.Offset, picked.Focus);   // the view picked last is the view of a new session
    }
}
