// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.World;

/// <summary>A legacy colour (texture-entry RGBA), 0..1 per channel.</summary>
public readonly record struct Rgba(float R, float G, float B, float A)
{
    public static readonly Rgba White = new(1, 1, 1, 1);
}

/// <summary>
/// A prim's per-face legacy diffuse colour and alpha (no textures until): the texture entry's default face
/// colour, and the faces that override it. Value equality over the overrides (a duplicated update stays a no-op).
/// </summary>
public sealed class FaceColors : IEquatable<FaceColors>
{
    private readonly Rgba?[] _faces;

    public FaceColors(Rgba @default, IEnumerable<Rgba?>? faces = null)
    {
        Default = @default;
        _faces = faces?.ToArray() ?? [];
    }

    public Rgba Default { get; }

    /// <summary>Face <paramref name="face"/>'s colour: its own override, else the default.</summary>
    public Rgba this[int face] => face >= 0 && face < _faces.Length && _faces[face] is { } c ? c : Default;

    public int OverrideCount => _faces.Count(f => f is not null);

    public bool Equals(FaceColors? other) => other is not null && Default == other.Default && _faces.SequenceEqual(other._faces);
    public override bool Equals(object? obj) => obj is FaceColors f && Equals(f);
    public override int GetHashCode() => _faces.Aggregate(Default.GetHashCode(), (h, f) => HashCode.Combine(h, f));
    public IReadOnlyList<Rgba?> Overrides => _faces;
}

/// <summary>SL texture coordinate generation for a face: the mesh's own UVs, or planar projection (LLFace::planarProjection).</summary>
public enum TexGen { Default = 0, Planar = 1 }

/// <summary>
/// One face's texture-entry settings: texture, repeats, offset, rotation (radians), texgen, fullbright, glow.
/// Colour and alpha stay in <see cref="FaceColors"/>. Glow is recorded, not drawn yet.
/// </summary>
public readonly record struct FaceTexture(Guid TextureId, float RepeatU, float RepeatV, float OffsetU, float OffsetV, float Rotation,
    TexGen TexGen, bool Fullbright, float Glow)
{
    public static FaceTexture Plain(Guid id) => new(id, 1, 1, 0, 0, 0, TexGen.Default, false, 0);
}

/// <summary>
/// A prim's per-face texture entries: the texture entry's default face and the faces that override it; value
/// equality over the overrides (a duplicated update stays a no-op), like <see cref="FaceColors"/>.
/// </summary>
public sealed class FaceTextures : IEquatable<FaceTextures>
{
    private readonly FaceTexture?[] _faces;

    public FaceTextures(FaceTexture @default, IEnumerable<FaceTexture?>? faces = null)
    {
        Default = @default;
        _faces = faces?.ToArray() ?? [];
    }

    public FaceTexture Default { get; }
    public FaceTexture this[int face] => face >= 0 && face < _faces.Length && _faces[face] is { } f ? f : Default;
    public IReadOnlyList<FaceTexture?> Overrides => _faces;

    public bool Equals(FaceTextures? other) => other is not null && Default == other.Default && _faces.SequenceEqual(other._faces);
    public override bool Equals(object? obj) => obj is FaceTextures f && Equals(f);
    public override int GetHashCode() => _faces.Aggregate(Default.GetHashCode(), (h, f) => HashCode.Combine(h, f));
}
