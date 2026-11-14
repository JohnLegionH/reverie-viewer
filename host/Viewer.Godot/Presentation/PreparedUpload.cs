// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Core.Residency;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The engine image of a decoded level (its mip chain already built with the decode) and its average
/// colour, made on the decode's background task so the main thread only creates the GPU texture and swaps it in. Godot 4.7
/// allows this: "scenes, textures, meshes, etc - can be loaded and manipulated on a thread and then added to the active
/// scene on the main thread" (docs: tutorials/performance/thread_safe_apis). The RenderingServer calls stay on the main
/// thread: rendering/driver/threads/thread_model is left at its default (1), the separate render thread being marked
/// experimental and "Not recommended for use in production" (ProjectSettings.xml, 4.7.2-stable).
/// </summary>
public sealed class PreparedUpload : IDisposable
{
    private PreparedUpload(Image image, uint average, bool? hidesAtMask) { Image = image; Average = average; HidesAtMask = hidesAtMask; }

    public Image Image { get; }
    public uint Average { get; }

    /// <summary>Whether no cell of the level's alpha mask is drawn (<see cref="Viewer.Core.Avatars.BakeAlphaMask.HidesAt"/>): an avatar bake like that hides
    /// its body region. Null when the level is opaque or too coarse to say. Worked here, off the main thread, because the level's pixels go back to the pool once the
    /// image is made.</summary>
    public bool? HidesAtMask { get; }

    /// <summary>Background thread: wraps the level and its mip chain (the same bytes the main thread used to wrap).</summary>
    public static PreparedUpload Make(Viewer.Core.Imaging.DecodedTexture d) =>
        new(Image.CreateFromData(d.Width, d.Height, true, Image.Format.Rgba8, d.Mips!), PlaceholderColour.Average(d.Rgba),
            d.Alpha == Viewer.Core.Imaging.AlphaClass.Opaque ? null : Viewer.Core.Avatars.BakeAlphaMask.HidesAt(d.Rgba, d.Width, d.Height, d.FullWidth, d.FullHeight));

    public void Dispose() => Image.Dispose();
}
