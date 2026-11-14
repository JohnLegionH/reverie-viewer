// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;

namespace Viewer.Core.World;

/// <summary>A PBR material's texture slots, in the order the glTF override message indexes them ("tex", "ti").</summary>
public enum PbrSlot { BaseColor = 0, Normal = 1, MetallicRoughness = 2, Emissive = 3 }

/// <summary>How a PBR material uses its base colour's alpha (glTF alphaMode).</summary>
public enum PbrAlphaMode : byte { Opaque = 0, Blend = 1, Mask = 2 }

/// <summary>
/// A texture slot's placement (glTF KHR_texture_transform): scale, then rotation (radians), then offset, applied to the
/// face's texture coordinates with V running down the image, as glTF defines them.
/// </summary>
public readonly record struct TextureTransform(float OffsetU, float OffsetV, float ScaleU, float ScaleV, float Rotation)
{
    public static readonly TextureTransform Identity = new(0, 0, 1, 1, 0);
}

/// <summary>
/// A PBR material as it is drawn: a glTF 2.0 material asset (<see cref="FromGltf"/>) with a face's glTF override applied
/// (<see cref="WithOverride"/>). Texture ids are empty for an absent slot. Occlusion is read from the metallic-roughness
/// texture's red channel (an ORM texture), as Second Life's PBR materials define it; a separate occlusion texture is not
/// used. Value equality, so faces with the same resolved material share one engine material.
/// </summary>
public sealed record PbrMaterial
{
    public Guid BaseColorTexture { get; init; }
    public Guid NormalTexture { get; init; }
    public Guid MetallicRoughnessTexture { get; init; }
    public Guid EmissiveTexture { get; init; }
    /// <summary>Linear base colour factor, alpha included (glTF default white).</summary>
    public Rgba BaseColor { get; init; } = Rgba.White;
    /// <summary>Linear emissive factor (glTF default black; alpha unused).</summary>
    public Rgba Emissive { get; init; } = new(0, 0, 0, 1);
    public float Metallic { get; init; } = 1f;
    public float Roughness { get; init; } = 1f;
    public PbrAlphaMode AlphaMode { get; init; }
    public float AlphaCutoff { get; init; } = 0.5f;
    public bool DoubleSided { get; init; }
    public TextureTransform BaseColorTransform { get; init; } = TextureTransform.Identity;
    public TextureTransform NormalTransform { get; init; } = TextureTransform.Identity;
    public TextureTransform MetallicRoughnessTransform { get; init; } = TextureTransform.Identity;
    public TextureTransform EmissiveTransform { get; init; } = TextureTransform.Identity;

    /// <summary>The override message's "clear this texture" id (all bits set); an empty id in an override keeps the asset's.</summary>
    public static readonly Guid OverrideClearsTexture = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public Guid Texture(PbrSlot slot) => slot switch
    {
        PbrSlot.BaseColor => BaseColorTexture, PbrSlot.Normal => NormalTexture,
        PbrSlot.MetallicRoughness => MetallicRoughnessTexture, _ => EmissiveTexture,
    };

    public TextureTransform Transform(PbrSlot slot) => slot switch
    {
        PbrSlot.BaseColor => BaseColorTransform, PbrSlot.Normal => NormalTransform,
        PbrSlot.MetallicRoughness => MetallicRoughnessTransform, _ => EmissiveTransform,
    };

    private PbrMaterial WithTexture(PbrSlot slot, Guid id) => slot switch
    {
        PbrSlot.BaseColor => this with { BaseColorTexture = id }, PbrSlot.Normal => this with { NormalTexture = id },
        PbrSlot.MetallicRoughness => this with { MetallicRoughnessTexture = id }, _ => this with { EmissiveTexture = id },
    };

    private PbrMaterial WithTransform(PbrSlot slot, TextureTransform t) => slot switch
    {
        PbrSlot.BaseColor => this with { BaseColorTransform = t }, PbrSlot.Normal => this with { NormalTransform = t },
        PbrSlot.MetallicRoughness => this with { MetallicRoughnessTransform = t }, _ => this with { EmissiveTransform = t },
    };

    /// <summary>Every texture the material names, by slot (absent slots left out).</summary>
    public IEnumerable<(PbrSlot Slot, Guid Id)> Textures()
    {
        foreach (var s in Slots)
            if (Texture(s) != Guid.Empty) yield return (s, Texture(s));
    }

    /// <summary>The slots in the override message's order (a new list each call: no shared mutable state).</summary>
    public static IReadOnlyList<PbrSlot> Slots => [PbrSlot.BaseColor, PbrSlot.Normal, PbrSlot.MetallicRoughness, PbrSlot.Emissive];

    /// <summary>
    /// The first material of a glTF document (the JSON inside a material asset): its factors, alpha mode, double-sided
    /// flag, and each slot's texture (the image its texture names, whose uri is the texture's asset id) with its
    /// KHR_texture_transform. Absent fields take glTF's defaults. Null when the document has no material or is not JSON.
    /// </summary>
    public static PbrMaterial? FromGltf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("materials", out var mats) || mats.ValueKind != JsonValueKind.Array || mats.GetArrayLength() == 0
                || mats[0].ValueKind != JsonValueKind.Object)
                return null;
            var m = mats[0];
            var r = new PbrMaterial();
            if (m.TryGetProperty("pbrMetallicRoughness", out var pmr) && pmr.ValueKind == JsonValueKind.Object)
            {
                if (Floats(pmr, "baseColorFactor", 4) is { } bc) r = r with { BaseColor = new Rgba(bc[0], bc[1], bc[2], bc[3]) };
                if (Float(pmr, "metallicFactor") is { } mf) r = r with { Metallic = mf };
                if (Float(pmr, "roughnessFactor") is { } rf) r = r with { Roughness = rf };
                r = r.Slot(root, pmr, "baseColorTexture", PbrSlot.BaseColor).Slot(root, pmr, "metallicRoughnessTexture", PbrSlot.MetallicRoughness);
            }
            r = r.Slot(root, m, "normalTexture", PbrSlot.Normal).Slot(root, m, "emissiveTexture", PbrSlot.Emissive);
            if (Floats(m, "emissiveFactor", 3) is { } ef) r = r with { Emissive = new Rgba(ef[0], ef[1], ef[2], 1) };
            if (m.TryGetProperty("alphaMode", out var am) && am.ValueKind == JsonValueKind.String)
                r = r with { AlphaMode = am.GetString() switch { "BLEND" => PbrAlphaMode.Blend, "MASK" => PbrAlphaMode.Mask, _ => PbrAlphaMode.Opaque } };
            if (Float(m, "alphaCutoff") is { } ac) r = r with { AlphaCutoff = ac };
            if (m.TryGetProperty("doubleSided", out var ds) && ds.ValueKind is JsonValueKind.True or JsonValueKind.False) r = r with { DoubleSided = ds.GetBoolean() };
            return r;
        }
        catch (JsonException) { return null; }
    }

    // a slot's texture: textures[index].source -> images[source].uri (the asset id), and its KHR_texture_transform
    private PbrMaterial Slot(JsonElement root, JsonElement owner, string name, PbrSlot slot)
    {
        if (!owner.TryGetProperty(name, out var info) || info.ValueKind != JsonValueKind.Object)
            return this;
        var r = this;
        if (info.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number && ix.TryGetInt32(out int t)
            && Element(root, "textures", t) is { } tex && tex.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Number && src.TryGetInt32(out int s)
            && Element(root, "images", s) is { } img && img.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String
            && Guid.TryParse(uri.GetString(), out var id))
            r = r.WithTexture(slot, id);
        if (info.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object
            && ext.TryGetProperty("KHR_texture_transform", out var kt) && kt.ValueKind == JsonValueKind.Object)
        {
            var o = Floats(kt, "offset", 2);
            var sc = Floats(kt, "scale", 2);
            r = r.WithTransform(slot, new TextureTransform(o?[0] ?? 0, o?[1] ?? 0, sc?[0] ?? 1, sc?[1] ?? 1, Float(kt, "rotation") ?? 0));
        }
        return r;
    }

    private static JsonElement? Element(JsonElement root, string array, int i) =>
        root.TryGetProperty(array, out var a) && a.ValueKind == JsonValueKind.Array && i >= 0 && i < a.GetArrayLength() && a[i].ValueKind == JsonValueKind.Object ? a[i] : null;

    private static float? Float(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : null;

    private static float[]? Floats(JsonElement o, string name, int n)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < n)
            return null;
        var f = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (v[i].ValueKind != JsonValueKind.Number) return null;
            f[i] = (float)v[i].GetDouble();
        }
        return f;
    }

    /// <summary>
    /// This material with a face's glTF override (<see cref="FaceMaterialOverride.Json"/>) applied, as the official
    /// viewer applies one: a texture id replaces the slot's, the all-bits id clears it, an empty or missing id keeps it;
    /// base colour, emissive colour, metallic, roughness, alpha mode, alpha cutoff and double-sided replace the asset's
    /// when present; a texture transform's offset, scale and rotation each replace the asset's when present and not the
    /// default (zero offset, unit scale, zero rotation), so an override cannot set a transform back to its default.
    /// An override that is not a JSON object changes nothing.
    /// </summary>
    public PbrMaterial WithOverride(string? overrideJson)
    {
        if (string.IsNullOrEmpty(overrideJson))
            return this;
        try
        {
            using var doc = JsonDocument.Parse(overrideJson);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object)
                return this;
            var r = this;
            if (o.TryGetProperty("tex", out var tex) && tex.ValueKind == JsonValueKind.Array)
                for (int i = 0; i < Math.Min(tex.GetArrayLength(), 4); i++)
                    if (tex[i].ValueKind == JsonValueKind.String && Guid.TryParse(tex[i].GetString(), out var id) && id != Guid.Empty)
                        r = r.WithTexture((PbrSlot)i, id == OverrideClearsTexture ? Guid.Empty : id);
            if (Floats(o, "bc", 4) is { } bc) r = r with { BaseColor = new Rgba(bc[0], bc[1], bc[2], bc[3]) };
            if (Floats(o, "ec", 3) is { } ec) r = r with { Emissive = new Rgba(ec[0], ec[1], ec[2], 1) };
            if (Float(o, "mf") is { } mf) r = r with { Metallic = mf };
            if (Float(o, "rf") is { } rf) r = r with { Roughness = rf };
            if (o.TryGetProperty("am", out var am) && am.ValueKind == JsonValueKind.Number && am.TryGetInt32(out int a) && a is >= 0 and <= 2)
                r = r with { AlphaMode = (PbrAlphaMode)a };
            if (Float(o, "ac") is { } ac) r = r with { AlphaCutoff = ac };
            if (o.TryGetProperty("ds", out var ds) && ds.ValueKind is JsonValueKind.True or JsonValueKind.False) r = r with { DoubleSided = ds.GetBoolean() };
            if (o.TryGetProperty("ti", out var ti) && ti.ValueKind == JsonValueKind.Array)
                for (int i = 0; i < Math.Min(ti.GetArrayLength(), 4); i++)
                {
                    if (ti[i].ValueKind != JsonValueKind.Object) continue;
                    var t = r.Transform((PbrSlot)i);
                    if (Floats(ti[i], "o", 2) is { } off && (off[0] != 0 || off[1] != 0)) t = t with { OffsetU = off[0], OffsetV = off[1] };
                    if (Floats(ti[i], "s", 2) is { } sc && (sc[0] != 1 || sc[1] != 1)) t = t with { ScaleU = sc[0], ScaleV = sc[1] };
                    if (Float(ti[i], "r") is { } rot && rot != 0) t = t with { Rotation = rot };
                    r = r.WithTransform((PbrSlot)i, t);
                }
            return r;
        }
        catch (JsonException) { return this; }
    }
}
