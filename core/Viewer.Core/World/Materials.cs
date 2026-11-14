// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.World;

/// <summary>
/// One face's material references from its texture entry: the legacy bump and shiny settings, the legacy material id
/// (a normal and specular map set the region serves through its RenderMaterials capability) and the PBR material id
/// (a glTF material asset). Empty ids mean none. Nothing here is drawn yet; the presentation keeps the legacy diffuse look.
/// </summary>
/// <param name="Bump">The texture entry's bump setting: 0 none, 1 brightness, 2 darkness, 3 and up the built-in bump maps.</param>
/// <param name="Shiny">The texture entry's shininess level, 0 (none) to 3 (high).</param>
public readonly record struct FaceMaterial(byte Bump, byte Shiny, Guid MaterialId, Guid RenderMaterialId)
{
    public bool IsNone => this == default;
}

/// <summary>
/// A prim's per-face material references, shaped like <see cref="FaceTextures"/>: the texture entry's default face and
/// the faces that override it, with value equality (a duplicated update stays a no-op). The adapter gives none (null)
/// for a prim whose every face has no material reference, so prims without materials look as they did before.
/// </summary>
public sealed class FaceMaterials : IEquatable<FaceMaterials>
{
    private readonly FaceMaterial?[] _faces;

    public FaceMaterials(FaceMaterial @default, IEnumerable<FaceMaterial?>? faces = null)
    {
        Default = @default;
        _faces = faces?.ToArray() ?? [];
    }

    public FaceMaterial Default { get; }
    public FaceMaterial this[int face] => face >= 0 && face < _faces.Length && _faces[face] is { } f ? f : Default;
    public IReadOnlyList<FaceMaterial?> Overrides => _faces;

    /// <summary>The default face and every override, in that order (overrides may be null).</summary>
    public IEnumerable<FaceMaterial?> All => new FaceMaterial?[] { Default }.Concat(_faces);

    /// <summary>
    /// A prim's face materials with its PBR material ids taken from the render material parameter: when the parameter
    /// is present it decides every face's PBR id (a face without an entry has none); the texture entry's other references
    /// stay. Null when nothing remains.
    /// </summary>
    public static FaceMaterials? WithRenderMaterials(FaceMaterials? fromTextureEntry, IReadOnlyList<RenderMaterialEntry>? entries)
    {
        if (entries is null)
            return fromTextureEntry;
        int faces = Math.Max(fromTextureEntry?.Overrides.Count ?? 0, entries.Count == 0 ? 0 : entries.Max(e => e.Face) + 1);
        var def = (fromTextureEntry?.Default ?? default) with { RenderMaterialId = Guid.Empty };
        var list = new FaceMaterial?[faces];
        for (int f = 0; f < faces; f++)
            if (fromTextureEntry is not null && f < fromTextureEntry.Overrides.Count && fromTextureEntry.Overrides[f] is { } o)
                list[f] = o with { RenderMaterialId = Guid.Empty };
        foreach (var e in entries)
            list[e.Face] = (list[e.Face] ?? def) with { RenderMaterialId = e.MaterialId };
        int last = Array.FindLastIndex(list, x => x is not null && x != def);
        var merged = new FaceMaterials(def, list.Take(last + 1));
        return def.IsNone && merged.Overrides.All(x => x is null || x.Value.IsNone) ? null : merged;
    }

    public bool Equals(FaceMaterials? other) => other is not null && Default == other.Default && _faces.SequenceEqual(other._faces);
    public override bool Equals(object? obj) => obj is FaceMaterials f && Equals(f);
    public override int GetHashCode() => _faces.Aggregate(Default.GetHashCode(), (h, f) => HashCode.Combine(h, f));
}

/// <summary>One entry of an object's render material parameter: a face and its PBR material id.</summary>
public readonly record struct RenderMaterialEntry(int Face, Guid MaterialId);

/// <summary>How a legacy material treats the diffuse texture's alpha (the RenderMaterials answer's diffuse alpha mode).</summary>
public enum LegacyAlphaMode : byte { None = 0, Blend = 1, Mask = 2, Emissive = 3 }

/// <summary>A legacy material's normal or specular map with its own placement (repeats, offset, rotation in radians).</summary>
public readonly record struct MaterialMap(Guid TextureId, float OffsetU, float OffsetV, float RepeatU, float RepeatV, float Rotation);

/// <summary>
/// A legacy material as the region serves it (the RenderMaterials capability): normal and specular maps, the specular
/// colour, exponent (glossiness) and environment intensity, and how the diffuse alpha is used (the alpha mask cutoff
/// applies to <see cref="LegacyAlphaMode.Mask"/>). Values are as the grid sends them (0-255 for the byte fields).
/// </summary>
public sealed record LegacyMaterial(Guid Id, MaterialMap Normal, MaterialMap Specular, Rgba SpecularColor, byte SpecularExponent,
    byte EnvironmentIntensity, LegacyAlphaMode DiffuseAlphaMode, byte AlphaMaskCutoff);

/// <summary>
/// One face's glTF material override as the region sends it (the GLTF override message, outside the texture entry): a
/// JSON object holding only the fields the override sets, under the message's own keys ("tex" texture ids, "bc" base
/// colour, "ec" emissive colour, "mf" metallic, "rf" roughness, "am" alpha mode, "ac" alpha cutoff, "ds" double sided,
/// "ti" texture transforms). Kept as JSON so the core reads it without the protocol library.
/// </summary>
public readonly record struct FaceMaterialOverride(int Face, string Json);

/// <summary>
/// An object's current glTF material overrides, one per overridden face, in face order; value equality, so a repeated
/// message changes nothing. A message with no faces clears every override of the object (it is then null on the object).
/// </summary>
public sealed class MaterialOverrides : IEquatable<MaterialOverrides>
{
    private readonly FaceMaterialOverride[] _faces;

    public MaterialOverrides(IEnumerable<FaceMaterialOverride> faces) =>
        _faces = faces.GroupBy(f => f.Face).Select(g => g.Last()).OrderBy(f => f.Face).ToArray();

    public IReadOnlyList<FaceMaterialOverride> Faces => _faces;

    /// <summary>Face <paramref name="face"/>'s override JSON, or null when that face has none.</summary>
    public string? this[int face] => _faces.FirstOrDefault(f => f.Face == face) is { Json: { } j } x && x.Face == face ? j : null;

    public bool Equals(MaterialOverrides? other) => other is not null && _faces.SequenceEqual(other._faces);
    public override bool Equals(object? obj) => obj is MaterialOverrides m && Equals(m);
    public override int GetHashCode() => _faces.Aggregate(0, (h, f) => HashCode.Combine(h, f));
}
