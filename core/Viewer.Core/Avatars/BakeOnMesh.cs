// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Frozen;

namespace Viewer.Core.Avatars;

/// <summary>What a face's texture id turns out to be for an avatar's worn mesh.</summary>
public enum BomKind
{
    /// <summary>An ordinary texture: drawn as itself.</summary>
    NotBake,
    /// <summary>A bake placeholder whose slot the wearer has a bake for: draw that bake.</summary>
    Bake,
    /// <summary>A bake placeholder whose slot the wearer has no bake for (yet): draw the default texture.</summary>
    Fallback,
}

/// <summary>The texture a face draws (<see cref="Id"/>) and, for a bake placeholder, the slot it named.</summary>
public readonly record struct BomTexture(BomKind Kind, Guid Id, string? Slot);

/// <summary>
/// Bake on mesh: a face of a worn mesh whose texture is one of the bake placeholder ids (<see cref="BakeChannels"/>) draws the
/// wearer's own bake for that slot. Behaviour as the Second Life viewer's LLViewerObject::getBakedTextureForMagicId has it
/// (llviewerobject.cpp:5169-5196, read in place): a wearer with no bake for the slot, or one whose bake is missing, draws the
/// default texture (IMG_DEFAULT) and not the placeholder id, which names no asset. An avatar's own bake not arrived as a picture
/// yet draws the same way, the default texture's mid grey, until it does. The protocol's ids and the default texture's id are
/// facts of the protocol; no code is taken from the viewer.
/// </summary>
public static class BakeOnMesh
{
    /// <summary>The default texture (IMG_DEFAULT, indra_constants.cpp), what a missing bake draws. Until its picture is
    /// there the viewer draws it as a flat mid grey (llviewertexture.cpp:380-410, 0x7f in each channel).</summary>
    public static readonly Guid FallbackTexture = Guid.Parse("89556747-24cb-43ed-920b-47caed15465f");

    /// <summary>The texture a face naming <paramref name="faceTexture"/> draws on an avatar whose bakes are <paramref name="wearerBakes"/> (by slot name).</summary>
    public static BomTexture Resolve(Guid faceTexture, IReadOnlyDictionary<string, Guid> wearerBakes)
    {
        if (BakeChannels.SlotOf(faceTexture) is not { } slot) return new(BomKind.NotBake, faceTexture, null);
        return wearerBakes.TryGetValue(slot, out var bake) && bake != Guid.Empty
            ? new(BomKind.Bake, bake, slot)
            : new(BomKind.Fallback, FallbackTexture, slot);
    }
}

/// <summary>
/// The alpha rule for the body's bakes. The Second Life viewer discards an avatar body pixel whose bake alpha is below 0.2
/// (LLDrawPoolAvatar::sMinimumAlpha, lldrawpoolavatar.cpp:63, applied by avatarF.glsl:47); that is how a worn alpha layer
/// cuts holes in the system body, and how a fully transparent bake hides a region. The rule's value is a fact of the
/// viewer's behaviour; the shader here is our own.
/// </summary>
public static class BakeAlpha
{
    public const float Minimum = 0.2f;

    /// <summary>Whether a pixel of this alpha (0..255) is drawn: not below <see cref="Minimum"/>.</summary>
    public static bool IsDrawn(byte alpha) => alpha / 255f >= Minimum;

    /// <summary>Whether no pixel of the picture (RGBA, 4 bytes a pixel) is drawn, so its whole region is. A picture with no pixels
    /// (not decoded yet) hides nothing.</summary>
    public static bool HidesRegion(ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length < 4) return false;
        for (int i = 3; i < rgba.Length; i += 4)
            if (IsDrawn(rgba[i])) return false;
        return true;
    }
}

/// <summary>
/// Which parts of the system body a worn mesh's faces hide: a face naming a body bake placeholder hides the body parts that bake
/// textures (updateMeshVisibility, llvoavatar.cpp:8750-8845: head hides the head and the eyelashes, eyes both eyeballs, and
/// hair, skirt, upper and lower their own part). The extra slots (left arm, left leg, aux 1-3) hide no part.
/// </summary>
public static class BodyVisibility
{
    private static readonly System.Collections.Frozen.FrozenSet<string> Body = new[] { "head", "upper", "lower", "eyes", "skirt", "hair" }.ToFrozenSet();

    /// <summary>The body bake slots whose parts are hidden by worn faces with these textures.</summary>
    public static IReadOnlySet<string> HiddenSlots(IEnumerable<Guid> wornFaceTextures)
    {
        var hidden = new HashSet<string>();
        foreach (var id in wornFaceTextures)
            if (BakeChannels.SlotOf(id) is { } slot && Body.Contains(slot)) hidden.Add(slot);
        return hidden;
    }
}
