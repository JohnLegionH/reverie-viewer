// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;

namespace Viewer.Core.Interaction;

/// <summary>How one object's floating text is drawn this frame.</summary>
public readonly record struct HoverTextStyle(bool Visible, float R, float G, float B, float Alpha);

/// <summary>Floating text from scripts: colour, fade and distance rules, and where it sits.</summary>
public static class HoverTextRules
{
    public const float DefaultMaxDistance = 64f, FadeStart = 8f, FadeRange = 4f, AnchorHeightFactor = 0.6f;

    /// <summary>The colour and opacity at a distance in metres; not visible without text, past the draw limit, or past the end of the fade.</summary>
    public static HoverTextStyle Style(PrimInteraction? interaction, float distance, float maxDistance = DefaultMaxDistance)
    {
        if (interaction is not { HoverText.Length: > 0 } || !(distance <= maxDistance))
            return default;
        float fade = distance <= FadeStart ? 1f : Math.Max(0f, 1f - (distance - FadeStart) / FadeRange);
        if (fade <= 0f)
            return default;
        uint c = interaction.HoverColour;
        return new HoverTextStyle(true, ((c >> 24) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f * fade);
    }

    /// <summary>The point the text hangs from: the prim's position raised by 0.6 of its height.</summary>
    public static Vector3 Anchor(Vector3 primPosition, float scaleZ) => primPosition + new Vector3(0f, 0f, AnchorHeightFactor * scaleZ);

    /// <summary>The 0-255 channels packed as 0xRRGGBBAA.</summary>
    public static uint Pack(byte r, byte g, byte b, byte a) => (uint)r << 24 | (uint)g << 16 | (uint)b << 8 | a;
}
