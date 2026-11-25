// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;

namespace Viewer.Godot;

/// <summary>A viewport's picture for the checks that look at pixels. The viewport's texture is read once and kept (ReadBackScanTests).</summary>
internal sealed class WindowPicture(Viewport viewport)
{
    private ViewportTexture? _texture;

    public Image Take()
    {
        _texture ??= viewport.GetTexture();
        return _texture.GetImage();
    }
}
