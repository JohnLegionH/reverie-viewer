// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Build;

/// <summary>What the Object tab's copy and paste carry.</summary>
public enum ObjectClip { Position, Size, Rotation, All, Parameters }

/// <summary>Copy and paste on the Object and Texture tabs.</summary>
public sealed partial class EditController
{
    /// <summary>Copies the primary object's value; returns its text.</summary>
    public string? Copy(ObjectClip what) => null;

    public bool CanPaste(ObjectClip what) => false;

    public bool Paste(ObjectClip what) => false;

    public bool CopyFaces() => false;

    public bool CanPasteFaces => false;

    public bool PasteFaces() => false;
}
