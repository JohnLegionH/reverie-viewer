// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using System.Xml;
using Viewer.Core.World;

namespace Viewer.Core.Avatars;

/// <summary>Which sex a visual parameter applies to (the avatar definition file's sex attribute; the official bit values 1, 2, 3).</summary>
[Flags]
public enum ShapeSex : byte { Female = 1, Male = 2, Both = 3 }

/// <summary>What one visual parameter does to one joint at weight one: added to the joint's scale, and (when
/// <paramref name="Offset"/> is not null) to its position relative to its parent.</summary>
public sealed record SkeletonDelta(string Bone, Vector3 Scale, Vector3? Offset);

/// <summary>One entry of a driver parameter: the driven parameter and the trapezoid it follows (the driven weight rises from
/// its minimum at <see cref="Min1"/> to its maximum at <see cref="Max1"/>, holds to <see cref="Max2"/>, and falls back at <see cref="Min2"/>).</summary>
public sealed record DrivenEntry(int Id, float Min1, float Max1, float Max2, float Min2);

/// <summary>A visual parameter of the avatar definition file: its range and default, the sex it applies to, the joints it
/// scales or moves, and the parameters it drives.</summary>
public sealed record ShapeParam(int Id, string Name, float Min, float Max, float Default, ShapeSex Sex,
    IReadOnlyList<SkeletonDelta> Bones, IReadOnlyList<DrivenEntry> Driven);

/// <summary>
/// The visual parameters of a character folder's avatar definition file (avatar_lad.xml), read for what shapes the skeleton:
/// every param element with its value range and default, its param_skeleton bones (scale and offset deltas), and its
/// param_driver entries. Behaviour as the official viewer's (read for behaviour: LLVisualParamInfo::parseXml,
/// LLPolySkeletalDistortionInfo::parseXml, LLDriverParamInfo::parseXml, in llcharacter, llappearance), in our own words.
/// Morph targets and colour parameters are listed (so a driver can drive them) but carry no skeleton delta.
/// </summary>
public sealed class ShapeDefinition
{
    /// <summary>The shape's "male" parameter (transmitted); the avatar is male when its weight is above one half.</summary>
    public const int MaleId = VisualParamSet.MaleParamId;

    /// <summary>The Hover parameter (hover height in metres, range -2 to 2).</summary>
    public const int HoverId = 11001;

    public const int MaxParams = 4096, MaxBytes = 16 << 20;

    private readonly Dictionary<int, ShapeParam> _params;

    private ShapeDefinition(Dictionary<int, ShapeParam> p, IReadOnlyList<MorphParam> morphs) { _params = p; Morphs = morphs; }

    /// <summary>The morph parameters listed under the level-0 body meshes, in file order (a parameter named under two meshes appears twice).</summary>
    public IReadOnlyList<MorphParam> Morphs { get; }

    public IReadOnlyDictionary<int, ShapeParam> Params => _params;

    /// <summary>The parameters in ascending id order (the order the official viewer applies them in).</summary>
    public IEnumerable<ShapeParam> InIdOrder => _params.Values.OrderBy(p => p.Id);

    public bool TryGet(int id, out ShapeParam param) => _params.TryGetValue(id, out param!);

    public static ShapeDefinition Parse(byte[] ladXml)
    {
        if (ladXml.Length > MaxBytes) throw new InvalidDataException($"avatar definition file over {MaxBytes} bytes");
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new MemoryStream(ladXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        var map = new Dictionary<int, ShapeParam>();
        foreach (var e in doc.GetElementsByTagName("param").OfType<XmlElement>())
        {
            if (!int.TryParse(e.GetAttribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id < 0) continue;
            if (map.Count >= MaxParams) throw new InvalidDataException($"avatar definition file declares more than {MaxParams} parameters");
            float min = Num(e, "value_min", 0f), max = Num(e, "value_max", 1f);
            if (max < min) (min, max) = (max, min);
            float def = e.HasAttribute("value_default") ? Math.Clamp(Num(e, "value_default", min), min, max) : Math.Clamp(0f, min, max);
            var sex = e.GetAttribute("sex") switch { "male" => ShapeSex.Male, "female" => ShapeSex.Female, _ => ShapeSex.Both };
            var bones = new List<SkeletonDelta>();
            var driven = new List<DrivenEntry>();
            foreach (var child in e.ChildNodes.OfType<XmlElement>())
            {
                if (child.Name == "param_skeleton")
                {
                    foreach (var b in child.ChildNodes.OfType<XmlElement>().Where(b => b.Name == "bone"))
                    {
                        if (b.GetAttribute("name") is not { Length: > 0 } name || !b.HasAttribute("scale")) continue;   // the official viewer skips such a bone
                        bones.Add(new SkeletonDelta(name, Vec(b.GetAttribute("scale")), b.HasAttribute("offset") ? Vec(b.GetAttribute("offset")) : null));
                    }
                }
                else if (child.Name == "param_driver")
                {
                    foreach (var d in child.ChildNodes.OfType<XmlElement>().Where(d => d.Name == "driven"))
                    {
                        if (!int.TryParse(d.GetAttribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int drivenId)) continue;
                        // max2 and min2 default to the DRIVER's maximum (the official parser sets them before it reads max1)
                        driven.Add(new DrivenEntry(drivenId, Num(d, "min1", min), Num(d, "max1", max), Num(d, "max2", max), Num(d, "min2", max)));
                    }
                }
            }
            // a parameter named twice (a layer's colour parameter shares no id with the shape's): the first one with data stays
            if (map.TryGetValue(id, out var already) && (already.Bones.Count > 0 || already.Driven.Count > 0 || (bones.Count == 0 && driven.Count == 0))) continue;
            map[id] = new ShapeParam(id, e.GetAttribute("name"), min, max, def, sex, bones, driven);
        }
        return new ShapeDefinition(map, BodyMorph.ParseMorphs(doc));
    }

    private static float Num(XmlElement e, string name, float fallback) =>
        e.HasAttribute(name) && float.TryParse(e.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : fallback;

    private static Vector3 Vec(string s)
    {
        var p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 3) throw new InvalidDataException($"expected three numbers: '{s}'");
        return new(F(p[0]), F(p[1]), F(p[2]));
        static float F(string v) => float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
