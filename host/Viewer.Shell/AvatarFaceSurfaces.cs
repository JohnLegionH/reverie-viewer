// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;

namespace Viewer.Shell;

/// <summary>The surfaces an expression change reshapes, by part index (the index of the surface in the avatar's own body mesh), with their new data.</summary>
public sealed record FaceSurfaces(int[] Parts, PreparedSkinnedSurface[] Surfaces);

/// <summary>
/// The avatar's face when its expression weights change: only the parts whose morphs the expressions touch (the head and eyelashes) are worked out
/// again and encoded, so a fade costs a few surfaces, not the whole body. Pure and thread-safe: made off the main thread, handed to the engine on it.
/// </summary>
public static class AvatarFaceSurfaces
{
    /// <summary>The parameters an expression can change: every expression's own and the closed mouth.</summary>
    public static IEnumerable<string> ExpressionParams => Emotes.Params.Values.Append(Emotes.ClosedMouth);

    /// <summary>The weights given by name to a body morph: the expression weights, with the hand pose's on top (a name in both takes the hand's); null when both are empty.</summary>
    public static IReadOnlyDictionary<string, float>? Named(IReadOnlyDictionary<string, float>? hands, IReadOnlyDictionary<string, float>? face)
    {
        if (face is not { Count: > 0 }) return hands is { Count: > 0 } ? hands : null;
        var merged = new Dictionary<string, float>(face, StringComparer.Ordinal);
        if (hands is not null) foreach (var (name, w) in hands) merged[name] = w;
        return merged;
    }

    /// <summary>The head and eyelash surfaces (the parts of <paramref name="types"/>) with these weights, encoded as the whole body's morph would encode them;
    /// null when the body has no part of those types.</summary>
    public static FaceSurfaces? Prepare(AvatarBody body, ShapeDefinition def, AvatarShape shape, IReadOnlyDictionary<string, float>? named, bool shapeless, IReadOnlySet<string> types)
    {
        var parts = BodyMorph.ComputeParts(body, def, shape, named, shapeless, types);
        if (parts is null) return null;
        var at = new List<int>();
        var surfaces = new List<PreparedSkinnedSurface>();
        for (int i = 0; i < parts.Length; i++)
            if (parts[i] is { } m) { at.Add(i); surfaces.Add(AvatarSurfaceData.Encode(body.Parts[i], m, body.Palette.Length)); }
        return new FaceSurfaces(at.ToArray(), surfaces.ToArray());
    }
}
