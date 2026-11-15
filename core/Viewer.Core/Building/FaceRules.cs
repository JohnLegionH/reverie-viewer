// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using System.Text;
using Viewer.Core.World;

namespace Viewer.Core.Building;

/// <summary>Everything the Texture tab sets on one face: texture, colour and alpha, repeats, offset, rotation (radians), mapping,
/// full bright and glow.</summary>
public readonly record struct FaceSettings(Guid TextureId, Rgba Colour, float RepeatU, float RepeatV, float OffsetU, float OffsetV, float Rotation,
    TexGen TexGen, bool Fullbright, float Glow);

/// <summary>A change made on the Texture tab: only the fields given change; the rest of each face stays as it is. Values are held to
/// the official Texture tab's ranges (repeats -100 to 100, offsets -1 to 1, glow 0 to 1, transparency 0 to 100 percent).</summary>
/// <param name="Colour">The colour's red, green and blue, 0..1 (the alpha is <paramref name="Transparency"/>'s).</param>
/// <param name="Transparency">Percent, 0 (opaque) to 100.</param>
public sealed record FaceChange(Guid? TextureId = null, Vector3? Colour = null, float? Transparency = null, float? Glow = null, bool? Fullbright = null,
    TexGen? TexGen = null, float? RepeatU = null, float? RepeatV = null, float? OffsetU = null, float? OffsetV = null, float? RotationDegrees = null)
{
    public FaceSettings ApplyTo(FaceSettings f)
    {
        var colour = f.Colour;
        if (Colour is { } c)
            colour = colour with { R = Math.Clamp(c.X, 0, 1), G = Math.Clamp(c.Y, 0, 1), B = Math.Clamp(c.Z, 0, 1) };
        if (Transparency is { } t)
            colour = colour with { A = 1f - Math.Clamp(t, 0, 100) / 100f };
        return f with
        {
            TextureId = TextureId ?? f.TextureId,
            Colour = colour,
            Glow = Glow is { } g ? Math.Clamp(g, 0, 1) : f.Glow,
            Fullbright = Fullbright ?? f.Fullbright,
            TexGen = TexGen ?? f.TexGen,
            RepeatU = RepeatU is { } ru ? Math.Clamp(ru, -100, 100) : f.RepeatU,
            RepeatV = RepeatV is { } rv ? Math.Clamp(rv, -100, 100) : f.RepeatV,
            OffsetU = OffsetU is { } ou ? Math.Clamp(ou, -1, 1) : f.OffsetU,
            OffsetV = OffsetV is { } ov ? Math.Clamp(ov, -1, 1) : f.OffsetV,
            Rotation = RotationDegrees is { } r ? r * MathF.PI / 180f : f.Rotation,
        };
    }
}

/// <summary>One face's settings as an edit sends them.</summary>
public readonly record struct FaceEdit(int Face, FaceSettings Settings);

/// <summary>
/// The face rules of the Texture tab, after the official viewer (read as behaviour only): the texture's sides on the object for repeats
/// per metre (LLPrimitive::getTESTAxes), the planar mapping (LLFace's planar projection, the same mapping the face shader draws), and
/// Align planar faces (LLFace::calcAlignedPlanarTE: the faces take the last selected face's repeats, and the rotation and offset that
/// make its texture run on across them).
/// </summary>
public static class FaceRules
{
    /// <summary>The texture a face shows when nothing is set (the official default plywood).</summary>
    public static readonly Guid DefaultTexture = Guid.Parse("89556747-24cb-43ed-920b-47caed15465f");

    /// <summary>A face's settings as the world holds them.</summary>
    public static FaceSettings Settings(ObjectState o, int face)
    {
        var t = o.Textures?[face] ?? FaceTexture.Plain(DefaultTexture);
        var c = o.Colors?[face] ?? Rgba.White;
        return new FaceSettings(t.TextureId, c, t.RepeatU, t.RepeatV, t.OffsetU, t.OffsetV, t.Rotation, t.TexGen, t.Fullbright, t.Glow);
    }

    /// <summary>The object's sides a face's texture runs along (0 x, 1 y, 2 z): the top and the bottom x by y, faces 1 and 3 x by z,
    /// faces 2 and 4 y by z.</summary>
    public static (int S, int T) TextureAxes(int face) => face switch
    {
        0 => (0, 1),
        1 or 3 => (0, 2),
        2 or 4 => (1, 2),
        _ => (0, 1),
    };

    private static float Side(Vector3 scale, int axis) => axis switch { 0 => scale.X, 1 => scale.Y, _ => scale.Z };

    /// <summary>The repeats a face needs for <paramref name="perMetre"/> repeats per metre on an object of <paramref name="scale"/>.</summary>
    public static (float U, float V) RepeatsForPerMetre(float perMetre, int face, Vector3 scale)
    {
        var (s, t) = TextureAxes(face);
        return (perMetre * Side(scale, s), perMetre * Side(scale, t));
    }

    /// <summary>The repeats per metre a face's horizontal repeats make.</summary>
    public static float PerMetre(float repeatsU, int face, Vector3 scale)
    {
        float side = Side(scale, TextureAxes(face).S);
        return side > 1e-6f ? repeatsU / side : 0f;
    }

    /// <summary>The planar mapping's two directions on the object, for a face whose normal on the object is <paramref name="n"/>: the
    /// first along the texture's width, the second the axis it is turned from (the same choice the face shader makes).</summary>
    private static (Vector3 B, Vector3 T) PlanarAxes(Vector3 n)
    {
        n = Vector3.Normalize(n);
        Vector3 b = MathF.Abs(n.X) >= 0.5f ? (n.X < 0 ? -Vector3.UnitY : Vector3.UnitY) : (n.Y > 0 ? -Vector3.UnitX : Vector3.UnitX);
        return (b, Vector3.Cross(b, n));
    }

    /// <summary>The planar face's two texture coordinates before its own repeats, offset and rotation, at a point of the object
    /// (object-relative, in metres): twice the point along each planar direction, the second negated.</summary>
    private static Vector2 Planar(Vector3 b, Vector3 t, Vector3 local) => new(2 * Vector3.Dot(b, local), -2 * Vector3.Dot(t, local));

    /// <summary>A face's repeats, offset and rotation applied to its texture coordinates about the face's middle, as the face shader does.</summary>
    private static Vector2 Place(Vector2 st, float repeatU, float repeatV, float offsetU, float offsetV, float rotation)
    {
        float c = MathF.Cos(rotation), sn = MathF.Sin(rotation);
        return new Vector2((st.X * c + st.Y * sn) * repeatU + offsetU + 0.5f, (-st.X * sn + st.Y * c) * repeatV + offsetV + 0.5f);
    }

    /// <summary>Where a world point falls in a planar face's texture (whole repeats included).</summary>
    public static Vector2 PlanarUv(Vector3 objectPosition, Quaternion objectRotation, Vector3 localNormal, FaceSettings f, Vector3 world)
    {
        var (b, t) = PlanarAxes(localNormal);
        var local = Vector3.Transform(world - objectPosition, Quaternion.Inverse(objectRotation));
        return Place(Planar(b, t, local), f.RepeatU, f.RepeatV, f.OffsetU, f.OffsetV, f.Rotation);
    }

    /// <summary>A placed face for alignment: its object's place and turn and the face's normal on the object.</summary>
    public readonly record struct PlacedFace(Vector3 ObjectPosition, Quaternion ObjectRotation, Vector3 LocalNormal);

    /// <summary>The anchor's planar directions, and the target's carried onto the anchor's plane (a face at an angle to it is turned
    /// about their meeting line first), all in the world.</summary>
    private static (Vector3 Ab, Vector3 At, Vector3 Tb, Vector3 Tt) Frames(PlacedFace anchor, PlacedFace target)
    {
        var (ab, at) = PlanarAxes(anchor.LocalNormal);
        var (tb, tt) = PlanarAxes(target.LocalNormal);
        Vector3 W(PlacedFace f, Vector3 v) => Vector3.Transform(v, f.ObjectRotation);
        var an = Vector3.Normalize(W(anchor, anchor.LocalNormal));
        var tn = Vector3.Normalize(W(target, target.LocalNormal));
        var unfold = FromTo(tn, an);
        return (W(anchor, ab), W(anchor, at), Vector3.Transform(W(target, tb), unfold), Vector3.Transform(W(target, tt), unfold));
    }

    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        float d = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        if (d > 0.99999f)
            return Quaternion.Identity;
        var axis = Vector3.Cross(a, b);
        if (axis.LengthSquared() < 1e-10f)
        {
            axis = Vector3.Cross(a, MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
        }
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(d));
    }

    private static float Fraction(float v) => v - MathF.Truncate(v);

    /// <summary>
    /// Align planar faces for one face: the target takes the anchor's repeats, and the rotation and offset that make the anchor's
    /// texture run on across it, the offset kept between -1 and 1 as the official viewer keeps it. Null when either face is not planar.
    /// </summary>
    public static FaceSettings? AlignPlanar(PlacedFace anchor, FaceSettings anchorFace, PlacedFace target, FaceSettings targetFace)
    {
        if (anchorFace.TexGen != TexGen.Planar || targetFace.TexGen != TexGen.Planar)
            return null;
        var (ab, at, tb, tt) = Frames(anchor, target);
        // one step along the anchor's first planar direction, read in the target's planar coordinates: how far the target's are turned
        var v = new Vector2(Vector3.Dot(tb, ab), -Vector3.Dot(tt, ab));
        float rotation = anchorFace.Rotation + MathF.Atan2(v.Y, v.X);
        rotation = MathF.IEEERemainder(rotation, 2 * MathF.PI);
        // the anchor's object middle reads the anchor's offset; the target's offset is chosen so it reads the same there
        var d = anchor.ObjectPosition - target.ObjectPosition;
        var st = new Vector2(2 * Vector3.Dot(tb, d), -2 * Vector3.Dot(tt, d));
        var placed = Place(st, anchorFace.RepeatU, anchorFace.RepeatV, 0, 0, rotation) - new Vector2(0.5f, 0.5f);
        return targetFace with
        {
            RepeatU = anchorFace.RepeatU,
            RepeatV = anchorFace.RepeatV,
            Rotation = rotation,
            OffsetU = Fraction(anchorFace.OffsetU - placed.X),
            OffsetV = Fraction(anchorFace.OffsetV - placed.Y),
        };
    }

    // ---------------- PBR faces: the glTF texture transform (scale, then rotation, then offset, V down the image) ----------------

    private static Vector2 Khr(Vector2 uv, TextureTransform t)
    {
        var s = new Vector2(uv.X, 1f - uv.Y) * new Vector2(t.ScaleU, t.ScaleV);
        float c = MathF.Cos(t.Rotation), sn = MathF.Sin(t.Rotation);
        return new Vector2(c * s.X + sn * s.Y, -sn * s.X + c * s.Y) + new Vector2(t.OffsetU, t.OffsetV);
    }

    /// <summary>Where a world point falls in a planar PBR face's texture, through its glTF texture transform (as the PBR face shader draws it).</summary>
    public static Vector2 PlanarPbrUv(Vector3 objectPosition, Quaternion objectRotation, Vector3 localNormal, TextureTransform t, Vector3 world)
    {
        var (b, tt) = PlanarAxes(localNormal);
        var local = Vector3.Transform(world - objectPosition, Quaternion.Inverse(objectRotation));
        return Khr(Planar(b, tt, local) + new Vector2(0.5f, 0.5f), t);
    }

    /// <summary>Align planar faces for PBR faces (the official viewer leaves them out): the target takes the anchor's scale, and the
    /// rotation and offset of its glTF texture transform that make the anchor's texture run on across it. Exact for an even scale; an
    /// uneven scale turned by other than a quarter turn is as near as one transform can come.</summary>
    public static TextureTransform AlignPlanarPbr(PlacedFace anchor, TextureTransform anchorT, PlacedFace target)
    {
        var (ab, _, tb, tt) = Frames(anchor, target);
        // glTF reads V down the image, so the second planar direction keeps its sign here
        var v = new Vector2(Vector3.Dot(tb, ab), Vector3.Dot(tt, ab));
        float rotation = MathF.IEEERemainder(anchorT.Rotation + MathF.Atan2(v.Y, v.X), 2 * MathF.PI);
        var d = anchor.ObjectPosition - target.ObjectPosition;
        var st = new Vector2(2 * Vector3.Dot(tb, d), -2 * Vector3.Dot(tt, d)) + new Vector2(0.5f, 0.5f);
        var want = Khr(new Vector2(0.5f, 0.5f), anchorT);
        var have = Khr(st, anchorT with { Rotation = rotation, OffsetU = 0, OffsetV = 0 });
        return anchorT with { Rotation = rotation, OffsetU = Fraction(want.X - have.X), OffsetV = Fraction(want.Y - have.Y) };
    }

    /// <summary>The override a PBR face's texture transform is sent as: a glTF document whose one material sets the same
    /// KHR_texture_transform on each of its four maps (the official Texture tab moves them together unless told otherwise).</summary>
    public static string TransformOverrideJson(TextureTransform t)
    {
        string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        string info = "{\"extensions\":{\"KHR_texture_transform\":{\"offset\":[" + F(t.OffsetU) + "," + F(t.OffsetV) + "],\"scale\":[" + F(t.ScaleU) + "," + F(t.ScaleV)
            + "],\"rotation\":" + F(t.Rotation) + "}}}";
        var sb = new StringBuilder();
        sb.Append("{\"asset\":{\"version\":\"2.0\"},\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":").Append(info)
            .Append(",\"metallicRoughnessTexture\":").Append(info).Append("},\"normalTexture\":").Append(info)
            .Append(",\"emissiveTexture\":").Append(info).Append("}]}");
        return sb.ToString();
    }
}
