// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// The order of one frame for the regions that share the avatars' drawn bodies: what every region has sent is taken first, then the bodies are
/// placed. The official viewer takes every update of a frame in its network step before it moves any object or drawable (llappviewer.cpp,
/// LLAppViewer::idle: idleNetwork, then gObjectList.update, which runs each object's idleUpdate), so a region whose update is taken after the body
/// was placed would leave the body a frame behind it and the camera, which reads the avatar after the regions' frames, on the new place.
/// </summary>
public static class SharedBodyFrame
{
    /// <summary>One frame for <paramref name="regions"/>: <paramref name="take"/> takes a region's changes, <paramref name="place"/> places and
    /// poses the bodies a region drives.</summary>
    public static void Run<T>(IReadOnlyList<T> regions, Action<T> take, Action<T> place)
    {
        foreach (var r in regions) take(r);
        foreach (var r in regions) place(r);
    }
}
