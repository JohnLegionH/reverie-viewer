// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llface.cpp
 * @brief LLFace class implementation
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

// Adapted 2026 by Legion Builds. The rule that lets a face without a material be drawn as a cut-out (LLFace::canRenderAsMask,
// newview/llface.cpp, with LLPipeline::sAutoMaskAlphaDeferred and sAutoMaskAlphaNonDeferred, newview/pipeline.cpp, and the cutoff
// chosen in LLVolumeGeometryManager, newview/llvovolume.cpp) is rewritten in C# for our own types. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

namespace Viewer.Core.World;

/// <summary>What the cut-out rule reads of a face: its colour's alpha (0 to 1), its glow, whether its texture's alpha is a mask
/// (<see cref="Viewer.Core.Imaging.AlphaMask"/>), and what kind of face it is.</summary>
public readonly record struct AutoMaskFace(float ColourAlpha, float Glow, bool TextureIsMask, bool Rigged, bool Fullbright, bool Hud, bool HasPbrMaterial, bool LegacyMaterialBlends);

/// <summary>The two official settings of the rule: RenderAutoMaskAlphaDeferred (lit faces; on by default) and
/// RenderAutoMaskAlphaNonDeferred (fullbright and heads-up faces, which the lit pipeline does not draw; its default is also on).</summary>
public readonly record struct AutoMaskSettings(bool Deferred, bool NonDeferred)
{
    /// <summary>The official defaults: both on.</summary>
    public static AutoMaskSettings Official => new(true, true);
}

/// <summary>
/// When a face with a partly transparent texture is drawn as a cut-out (solid where the alpha reaches a cutoff, nothing elsewhere)
/// instead of blended, as the official viewer decides it for a face that has no material of its own.
/// </summary>
public static class AutoMask
{
    /// <summary>The official cutoff for such a face (its draw info's mask cutoff when it has no legacy material).</summary>
    public const float Cutoff = 0.33f;

    /// <summary>Whether the face may be drawn as a cut-out.</summary>
    public static bool CanRenderAsMask(AutoMaskFace face, AutoMaskSettings settings)
    {
        if (face.HasPbrMaterial) return false;        // a PBR material has its own alpha mode
        if (face.Rigged) return false;                // a rigged face is never masked automatically
        if (face.LegacyMaterialBlends) return false;  // a material that says blend
        if (face.ColourAlpha < 1f || face.Glow != 0f || !face.TextureIsMask) return false;   // face alpha; a glowing mask is not made; the texture does not qualify
        return face.Hud || face.Fullbright ? settings.NonDeferred : settings.Deferred;       // the lit pipeline does not draw fullbright and heads-up faces
    }
}
