// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Appearance;

/// <summary>What a wearable's texture slot shows: the picture of the chosen texture (a swatch) and a button that opens the picker. The
/// slot never shows the texture's id as text.</summary>
public readonly record struct TextureSlotLook(bool ShowsSwatch, string ButtonKey)
{
    /// <summary>The look of a slot holding <paramref name="asset"/> (an empty id: nothing chosen).</summary>
    public static TextureSlotLook Of(Guid asset) =>
        asset == Guid.Empty ? new(false, "appearance.texture.empty") : new(true, "appearance.texture.change");
}
