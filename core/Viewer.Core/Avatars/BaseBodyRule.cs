// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Avatars;

/// <summary>What one part of an avatar's system body (one bake slot's mesh) is drawn with.</summary>
public enum BodyPartDraw
{
    /// <summary>The part is not drawn.</summary>
    Hidden,
    /// <summary>Drawn with the bake the avatar's appearance names now.</summary>
    Bake,
    /// <summary>Drawn with the last bake that was drawn for the slot, while the new one is still arriving.</summary>
    LastGood,
    /// <summary>Drawn with the default skin.</summary>
    DefaultSkin,
}

/// <summary>What is known of one bake slot of a drawn avatar.</summary>
/// <param name="IsSelf">The user's own avatar.</param>
/// <param name="Bake">The bake id its appearance names for the slot; null when the appearance names none (no appearance yet, or a default id).</param>
/// <param name="HiddenByWornMesh">A worn mesh's face names the slot's bake placeholder (bake on mesh hides the part).</param>
/// <param name="AlphaHides">The bake's alpha has no drawn pixel at the alpha cutoff (the last decided bake's, while a new one arrives).</param>
/// <param name="TextureReady">The named bake is on the GPU.</param>
/// <param name="HasLastGood">An earlier bake of the slot is still on the GPU.</param>
public readonly record struct BodyPartState(bool IsSelf, Guid? Bake, bool HiddenByWornMesh, bool AlphaHides, bool TextureReady, bool HasLastGood);

/// <summary>
/// Whether a part of the system body is drawn, and with what. Follows the official viewer:
/// <list type="bullet">
/// <item>A part is drawn only when its bake is visible: defined (not the default ids) and not the invisible texture (LLVOAvatar::isTextureVisible,
/// llvoavatar.cpp 11746-11756, read by renderSkinned 5293-5320). For the user's own avatar the official viewer draws the part even when no bake is
/// defined (it composites its own); the own avatar keeps the default skin here in that case.</item>
/// <item>A worn mesh naming the slot's bake placeholder hides the part (updateMeshVisibility, llvoavatar.cpp 8754-8845).</item>
/// <item>A bake whose alpha is below the 0.2 cutoff everywhere draws nothing (lldrawpoolavatar.cpp 63).</item>
/// <item>While a new bake arrives the last one drawn stays on the part (the last-known-good bake, updateMeshTextures, llvoavatar.cpp 8889-8960; the
/// mesh keeps its texture until onBakedTextureLoaded switches it).</item>
/// </list>
/// One deviation, for safety: another avatar's part whose bake is defined but not on the GPU and has no earlier bake is not drawn (the official viewer
/// shows such an avatar as a loading cloud and draws the part grey once loaded); it is never drawn with the default skin.
/// The rules are facts of the official viewer's behaviour; no code is taken from it.
/// </summary>
public static class BaseBodyRule
{
    /// <summary>The texture an alpha wearable or an appearance names to make a part invisible (IMG_INVISIBLE, indra_constants.cpp).</summary>
    public static readonly Guid Invisible = Guid.Parse("3a367d1c-bef1-6d43-7595-e88c1e3aadb3");

    /// <summary>What the part is drawn with.</summary>
    public static BodyPartDraw Decide(BodyPartState s)
    {
        if (s.HiddenByWornMesh || s.Bake == Invisible || s.AlphaHides) return BodyPartDraw.Hidden;
        if (s.Bake is null) return s.IsSelf ? BodyPartDraw.DefaultSkin : BodyPartDraw.Hidden;
        if (s.TextureReady) return BodyPartDraw.Bake;
        if (s.HasLastGood) return BodyPartDraw.LastGood;
        return s.IsSelf ? BodyPartDraw.DefaultSkin : BodyPartDraw.Hidden;
    }
}
