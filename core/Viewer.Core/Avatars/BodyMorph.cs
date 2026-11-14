// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llpolymorph.cpp
 * @brief Implementation of LLPolyMesh class
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

// Adapted 2026 by Legion Builds. The way a morph parameter moves the mesh's vertices, normals and texture coordinates
// (LLPolyMorphTarget::apply, with the normal softening factor), the volume morphs that change collision volumes
// (LLPolyMorphTarget::apply, the volume part; LLPolyMorphTargetInfo::parseXml), the choice of the parameter's own weight or its
// default by sex, and the lookup of a driven parameter's morph (LLPolyMorphTarget::setInfo) follow the file named above, rewritten in C#
// for our own types. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

using System.Globalization;
using System.Numerics;
using System.Xml;

namespace Viewer.Core.Avatars;

/// <summary>What a morph parameter does to one collision volume at weight one: added to the volume's box size, and to its position.</summary>
public sealed record VolumeMorph(string Volume, Vector3 Scale, Vector3 Pos);

/// <summary>A morph parameter of one body mesh, as the avatar definition file lists it under that mesh: the mesh type, the parameter's id and
/// name (the name picks the mesh file's morph), the sex it applies to, its range and default, and the volume morphs it carries.</summary>
public sealed record MorphParam(string MeshType, int Id, string Name, ShapeSex Sex, float Min, float Max, float Default, IReadOnlyList<VolumeMorph> Volumes);

/// <summary>One body part after the shape's morphs: the positions and normals (in the same frame as <see cref="BodyPart.Positions"/>) and texture coordinates.</summary>
public sealed record MorphedPart(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs);

/// <summary>Shape morphs on the default body and on the collision volumes.</summary>
public static class BodyMorph
{
    /// <summary>How much of a morph's normal offset is added (the official viewer softens it).</summary>
    public const float NormalSoften = 0.65f;

    /// <summary>The suffix of the physics-driven parameters, whose morph is found under the name without it when the mesh file has no morph of the full name.</summary>
    public const string DrivenSuffix = "_Driven";

    /// <summary>The morph parameters under the level-0 meshes of an avatar definition file, in file order: every param element that has a
    /// param_morph child, with its volume morphs (a volume morph without a name is skipped, a missing scale or position is zero).</summary>
    internal static List<MorphParam> ParseMorphs(XmlDocument doc)
    {
        var list = new List<MorphParam>();
        foreach (var mesh in doc.GetElementsByTagName("mesh").OfType<XmlElement>())
        {
            if (mesh.GetAttribute("lod") != "0" || mesh.GetAttribute("type") is not { Length: > 0 } type) continue;
            foreach (var p in mesh.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "param"))
            {
                var morph = p.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.Name == "param_morph");
                if (morph is null || !int.TryParse(p.GetAttribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id < 0) continue;
                float min = Num(p, "value_min", 0f), max = Num(p, "value_max", 1f);
                if (max < min) (min, max) = (max, min);
                float def = p.HasAttribute("value_default") ? Math.Clamp(Num(p, "value_default", min), min, max) : Math.Clamp(0f, min, max);
                var sex = p.GetAttribute("sex") switch { "male" => ShapeSex.Male, "female" => ShapeSex.Female, _ => ShapeSex.Both };
                var volumes = new List<VolumeMorph>();
                foreach (var v in morph.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "volume_morph"))
                    if (v.GetAttribute("name") is { Length: > 0 } name)
                        volumes.Add(new VolumeMorph(name, Vec(v.GetAttribute("scale")), Vec(v.GetAttribute("pos"))));
                list.Add(new MorphParam(type, id, p.GetAttribute("name"), sex, min, max, def, volumes));
            }
        }
        return list;
    }

    private static float Num(XmlElement e, string name, float fallback) =>
        e.HasAttribute(name) && float.TryParse(e.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : fallback;

    private static Vector3 Vec(string s)
    {
        var p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 3 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ? new Vector3(x, y, z) : Vector3.Zero;
    }

    /// <summary>The weight a morph parameter works with: the avatar's own for the parameter's sex, else the parameter's default.</summary>
    public static float Effective(MorphParam p, IReadOnlyDictionary<int, float> weights, bool male)
    {
        bool applies = ((int)p.Sex & (male ? (int)ShapeSex.Male : (int)ShapeSex.Female)) != 0;
        return applies && weights.TryGetValue(p.Id, out float w) ? w : p.Default;
    }

    /// <summary>The morph the mesh file has under a parameter's name, else (for a name with the driven suffix) under the name without it.</summary>
    public static MorphTarget? Find(LlmMesh mesh, string name)
    {
        if (mesh.Morphs.TryGetValue(name, out var t)) return t;
        int at = name.IndexOf(DrivenSuffix, StringComparison.Ordinal);
        return at > 0 && mesh.Morphs.TryGetValue(name[..at], out t) ? t : null;
    }

    /// <summary>
    /// The body's parts with the avatar's morphs applied, one entry per part of <paramref name="body"/> in order (a part no morph touches
    /// shares the body's own arrays: do not change them); null when no morph changes anything. A vertex's position moves by each
    /// morph's offset times the parameter's weight, its normal by the normal offset times the weight times <see cref="NormalSoften"/>
    /// and is then renormalised, its texture coordinate by the uv offset times the weight (the official viewer applies the weight
    /// change since the last application, from zero here, so the sum is the same). Positions and normals are in the body's frame, as
    /// <see cref="BodyPart.Positions"/> are (an unweighted part is moved to its joint's place after morphing).
    /// </summary>
    public static MorphedPart[]? Compute(AvatarBody body, ShapeDefinition def, AvatarShape shape) => ComputeAll(body, def, shape);

    /// <summary>The body's parts with the avatar's morphs applied, and the weights in <paramref name="named"/> (by morph parameter name) used in place of the parameters' own;
    /// with <paramref name="onlyNamed"/> no other morph is applied (the animated hand morphs on a body that has no shape of its own).</summary>
    public static MorphedPart[]? Compute(AvatarBody body, ShapeDefinition def, AvatarShape shape, IReadOnlyDictionary<string, float>? named, bool onlyNamed) =>
        ComputeAll(body, def, shape, named, onlyNamed);

    /// <summary>The mesh types (headMesh, eyelashMesh and the like) whose morphs are named in <paramref name="names"/>: the parts a change of those weights reshapes.</summary>
    public static IReadOnlySet<string> MeshTypesOf(ShapeDefinition def, IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.Ordinal);
        return def.Morphs.Where(p => wanted.Contains(p.Name)).Select(p => p.MeshType).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>As <see cref="Compute(AvatarBody, ShapeDefinition, AvatarShape, IReadOnlyDictionary{string, float}?, bool)"/>, but only the parts whose mesh type is in
    /// <paramref name="meshTypes"/> are worked out (each as the whole computation would give it); the other entries are null. Null when no part is of those types.</summary>
    public static MorphedPart?[]? ComputeParts(AvatarBody body, ShapeDefinition def, AvatarShape shape, IReadOnlyDictionary<string, float>? named, bool onlyNamed, IReadOnlySet<string> meshTypes)
    {
        if (!body.Parts.Any(p => meshTypes.Contains(p.Type))) return null;
        var all = ComputeAll(body, def, shape, named, onlyNamed, meshTypes);
        var result = new MorphedPart?[body.Parts.Count];
        for (int i = 0; i < result.Length; i++)
            if (meshTypes.Contains(body.Parts[i].Type)) result[i] = all?[i] ?? new MorphedPart(body.Parts[i].Positions, body.Parts[i].Normals, body.Parts[i].Mesh.Uvs);
        return result;
    }

    private static MorphedPart[]? ComputeAll(AvatarBody body, ShapeDefinition def, AvatarShape shape, IReadOnlyDictionary<string, float>? named = null, bool onlyNamed = false,
        IReadOnlySet<string>? only = null)
    {
        MorphedPart[]? result = null;
        for (int pi = 0; pi < body.Parts.Count; pi++)
        {
            var part = body.Parts[pi];
            if (only is not null && !only.Contains(part.Type)) continue;   // a part of another mesh type is not worked out
            var mesh = part.Mesh;
            int n = mesh.VertexCount;
            Vector3[]? pos = null, scaled = null;
            Vector2[]? uv = null;
            bool[]? touched = null;
            foreach (var p in def.Morphs)
            {
                if (p.MeshType != part.Type) continue;
                float eff;
                if (named is not null && named.TryGetValue(p.Name, out float given)) eff = given;
                else if (onlyNamed) continue;
                else eff = Effective(p, shape.Weights, shape.Male);
                if (eff == 0f || Find(mesh, p.Name) is not { } target) continue;
                pos ??= (Vector3[])mesh.Positions.Clone();
                scaled ??= (Vector3[])mesh.Normals.Clone();
                uv ??= (Vector2[])mesh.Uvs.Clone();
                touched ??= new bool[n];
                for (int k = 0; k < target.Indices.Length; k++)
                {
                    int v = target.Indices[k];
                    if (v >= n) continue;
                    touched[v] = true;
                    pos[v] += target.Coords[k] * eff;
                    scaled[v] += target.Normals[k] * (eff * NormalSoften);
                    uv[v] += target.Uvs[k] * eff;
                }
            }
            if (pos is null) continue;
            var normals = scaled!;
            for (int v = 0; v < n; v++) if (touched![v]) normals[v] = SafeNormalize(normals[v], mesh.Normals[v]);
            if (part.Rigid is { } m)
            {
                for (int v = 0; v < n; v++)
                {
                    pos[v] = Vector3.Transform(pos[v], m);
                    normals[v] = SafeNormalize(Vector3.TransformNormal(normals[v], m), normals[v]);
                }
            }
            result ??= new MorphedPart[body.Parts.Count];
            result[pi] = new MorphedPart(pos, normals, uv!);
        }
        if (result is null) return null;
        for (int pi = 0; pi < result.Length; pi++) result[pi] ??= new MorphedPart(body.Parts[pi].Positions, body.Parts[pi].Normals, body.Parts[pi].Mesh.Uvs);
        return result;
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float l = v.Length();
        return l > 1e-12f && float.IsFinite(l) ? v / l : fallback;
    }

    /// <summary>
    /// The scale and position the volume morphs add to each collision volume (by name) for these weights (<paramref name="male"/> chooses
    /// whose weight each parameter takes): each parameter's volume morph times its weight, summed. A parameter listed under two meshes
    /// counts twice, as the official viewer makes one morph target per mesh.
    /// </summary>
    public static Dictionary<string, (Vector3 Scale, Vector3 Pos)> VolumeEffects(IReadOnlyList<MorphParam> morphs, IReadOnlyDictionary<int, float> weights, bool male)
    {
        var sum = new Dictionary<string, (Vector3 Scale, Vector3 Pos)>(StringComparer.Ordinal);
        foreach (var p in morphs)
        {
            if (p.Volumes.Count == 0) continue;
            float eff = Effective(p, weights, male);
            if (eff == 0f) continue;
            foreach (var v in p.Volumes)
            {
                var (s, q) = sum.TryGetValue(v.Volume, out var cur) ? cur : (Vector3.Zero, Vector3.Zero);
                sum[v.Volume] = (s + v.Scale * eff, q + v.Pos * eff);
            }
        }
        return sum;
    }
}
