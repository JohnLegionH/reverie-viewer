// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Imaging;

namespace Viewer.Core.World;

/// <summary>How a face uses its colour's alpha when drawn.</summary>
public enum FaceAlpha : byte
{
    /// <summary>Alpha ignored; the face is solid.</summary>
    Opaque = 0,
    /// <summary>Blended over what is behind it.</summary>
    Blend = 1,
    /// <summary>Solid where alpha reaches the cutoff, nothing elsewhere.</summary>
    Mask = 2,
    /// <summary>Solid; alpha says how much of the face shows its colour unlit (a glow mask), the rest lit.</summary>
    Emissive = 3,
}

/// <summary>
/// A legacy-material face as it is drawn: its alpha handling, the normal and specular maps with their placements, the
/// specular colour (0-1), the glossiness (0-1) and the environment intensity (0-1).
/// </summary>
public readonly record struct LegacyLook(FaceAlpha Alpha, float Cutoff, MaterialMap Normal, MaterialMap Specular, Rgba SpecularColour,
    float Glossiness, float Environment)
{
    public bool HasNormalMap => Normal.TextureId != Guid.Empty;
    public bool HasSpecularMap => Specular.TextureId != Guid.Empty;
}

/// <summary>
/// The rules that decide how a face is drawn from its material, written from the documented and observed behaviour of
/// Second Life's viewer (llvovolume.cpp: the draw info's specular colour, exponent, environment intensity and mask cutoff;
/// materialF.glsl: the diffuse alpha modes) and from glTF 2.0's alphaMode:
/// <list type="bullet">
/// <item>A face whose colour alpha is below 1 is blended, whatever its material says (it is drawn with the translucent faces).</item>
/// <item>Legacy alpha modes: none ignores the texture's alpha; blend blends when the texture has alpha; mask draws where the
/// texture's alpha reaches the cutoff (the byte / 255) and nothing elsewhere, and a cutoff of 0 or a texture without alpha
/// is solid; emissive draws solid and uses the alpha as the share of the face shown unlit.</item>
/// <item>A legacy material's specular map: its colour times the specular colour, glossiness the exponent / 255 and the
/// environment intensity / 255. Without a specular map, the face's shiny level: 0, 0.25, 0.5 or 0.75 as colour, glossiness and environment alike.</item>
/// <item>PBR (glTF alphaMode): opaque ignores alpha, blend blends, mask draws where alpha reaches the alpha cutoff.</item>
/// <item>A face without a material keeps the plain rule: blended when its texture has alpha.</item>
/// </list>
/// </summary>
public static class FaceLooks
{
    /// <summary>The plain rule (no material): blended when the colour or a texture already on screen has alpha.</summary>
    public static FaceAlpha Plain(float colourAlpha, AlphaClass? texture) =>
        colourAlpha < Solid || texture is AlphaClass.Binary or AlphaClass.Blended ? FaceAlpha.Blend : FaceAlpha.Opaque;

    /// <summary>
    /// The plain rule with the official cut-out rule (<see cref="AutoMask"/>): a face whose colour or texture has alpha is blended,
    /// unless it may be drawn as a cut-out, and a face of the heads-up display is always blended.
    /// </summary>
    public static FaceAlpha Plain(AlphaClass? texture, AutoMaskFace face, AutoMaskSettings settings)
    {
        var plain = Plain(face.ColourAlpha, texture);
        return plain == FaceAlpha.Blend && !face.Hud && AutoMask.CanRenderAsMask(face, settings) ? FaceAlpha.Mask : plain;
    }

    /// <summary>A legacy-material face's look; <paramref name="texture"/> is null while its texture is not on screen.</summary>
    public static LegacyLook Legacy(LegacyMaterial m, float colourAlpha, AlphaClass? texture, byte shiny = 0)
    {
        bool hasAlpha = texture is AlphaClass.Binary or AlphaClass.Blended;
        var alpha = colourAlpha < Solid ? FaceAlpha.Blend : m.DiffuseAlphaMode switch
        {
            LegacyAlphaMode.Blend => hasAlpha ? FaceAlpha.Blend : FaceAlpha.Opaque,
            LegacyAlphaMode.Mask => hasAlpha && m.AlphaMaskCutoff > 0 ? FaceAlpha.Mask : FaceAlpha.Opaque,
            LegacyAlphaMode.Emissive => FaceAlpha.Emissive,
            _ => FaceAlpha.Opaque,
        };
        // llvovolume.cpp (the draw info): a face whose material has a specular map takes the material's colour, exponent and environment intensity;
        // any other face has the specular of its own shiny level, which stands for the colour, the glossiness and the environment intensity alike
        bool spec = m.Specular.TextureId != Guid.Empty;
        float level = ShinyLevel(shiny);
        return new LegacyLook(alpha, alpha == FaceAlpha.Mask ? m.AlphaMaskCutoff / 255f : 0f, m.Normal, m.Specular,
            spec ? m.SpecularColor : new Rgba(level, level, level, level),
            spec ? m.SpecularExponent / 255f : level, spec ? m.EnvironmentIntensity / 255f : level);
    }

    /// <summary>The specular a face's shiny level (none, low, medium, high) gives (llvovolume.cpp).</summary>
    private static float ShinyLevel(byte shiny) => (shiny & 3) * 0.25f;

    /// <summary>A PBR face's alpha handling and cutoff (0 unless masked).</summary>
    public static (FaceAlpha Alpha, float Cutoff) Pbr(PbrMaterial m) => m.AlphaMode switch
    {
        PbrAlphaMode.Blend => (FaceAlpha.Blend, 0f),
        PbrAlphaMode.Mask => (FaceAlpha.Mask, m.AlphaCutoff),
        _ => (FaceAlpha.Opaque, 0f),
    };

    /// <summary>A colour alpha at or above this is solid (the plain rule's threshold, as before).</summary>
    private const float Solid = 0.999f;
}
