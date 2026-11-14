// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Residency;

/// <summary>What a face with a PBR material draws now.</summary>
public enum PbrFaceDraw
{
    /// <summary>Its PBR material, with its base colour image (or no base colour texture to wait for).</summary>
    Pbr,
    /// <summary>Its legacy texture, whose image is already on the GPU, while its PBR base colour is not.</summary>
    Legacy,
    /// <summary>Its PBR material on the placeholder: neither its base colour nor its legacy texture is on the GPU.</summary>
    Waiting,
}

/// <summary>
/// A PBR face shows its legacy texture while its PBR base colour is not on screen yet, but only a legacy texture that is
/// already on screen or that another user wants anyway: the fallback never asks for a texture of its own, so it costs
/// no fetch. Once the face has shown its base colour it stays on its PBR material (one switch, never back), so the
/// settled image is the PBR one whatever happened on the way.
/// </summary>
public static class LegacyFallback
{
    /// <summary>
    /// The face's choice. <paramref name="switched"/>: it has shown its base colour before. <paramref name="baseColourOnScreen"/>:
    /// its base colour image is on the GPU now (true when the material has no base colour texture).
    /// <paramref name="legacyOnScreen"/>: its legacy texture's image is on the GPU. <paramref name="legacyWantedElsewhere"/>:
    /// another user wants the legacy texture (it is fetched on that user's account). WaitForLegacy: the face is drawn
    /// waiting but should be presented again when the legacy texture arrives.
    /// </summary>
    public static (PbrFaceDraw Draw, bool WaitForLegacy) Choose(bool enabled, bool switched, bool baseColourOnScreen, bool legacyOnScreen, bool legacyWantedElsewhere)
    {
        if (!enabled || switched || baseColourOnScreen) return (PbrFaceDraw.Pbr, false);
        if (legacyOnScreen) return (PbrFaceDraw.Legacy, false);
        return (PbrFaceDraw.Waiting, legacyWantedElsewhere);
    }
}
