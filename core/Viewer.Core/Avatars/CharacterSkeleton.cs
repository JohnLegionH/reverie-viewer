// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using System.Xml;

namespace Viewer.Core.Avatars;

/// <summary>What a skeleton entry is: a bone the animations drive, or a collision volume (a scaled box under a bone that
/// shape parameters deform; it has no children).</summary>
public enum JointKind : byte { Bone, CollisionVolume }

/// <summary>
/// One joint of the avatar skeleton, in the avatar's own frame (x forward, y left, z up, metres), at its default pose.
/// <see cref="Position"/> is relative to the parent; <see cref="Rotation"/> and <see cref="Scale"/> are the joint's own.
/// </summary>
/// <param name="Base">True for the joints every avatar has had from the start ("base" support); the newer, extended
/// joints sit between some of them. The body meshes were weighted against the base joints alone.</param>
public sealed record SkeletonJoint(
    int Index, string Name, int Parent, JointKind Kind, bool Base,
    Vector3 Position, Quaternion Rotation, Vector3 Scale, IReadOnlyList<string> Aliases);

/// <summary>A place on the skeleton where an object can be worn: its joint and its offset from that joint.</summary>
public sealed record AttachmentPoint(int Id, string Name, string Joint, Vector3 Position, Quaternion Rotation);

/// <summary>
/// The avatar skeleton read from a character folder's skeleton file (an XML tree of bone and collision_volume elements,
/// each with name, pos, rot in degrees, scale and support attributes), plus the attachment points from its avatar
/// definition file. Data-driven: a different system avatar is a different folder.
/// </summary>
public sealed class CharacterSkeleton
{
    /// <summary>Upper bounds on what a skeleton file may declare: past them the file is refused.</summary>
    public const int MaxJoints = 1024, MaxBytes = 4 << 20;

    /// <summary>The avatar's root, above the top bone: attachment points may name it, the skeleton file does not.</summary>
    public const string RootName = "mRoot";

    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

    private CharacterSkeleton(List<SkeletonJoint> joints, IReadOnlyList<AttachmentPoint> points)
    {
        Joints = joints;
        AttachmentPoints = points;
        foreach (var j in joints)
        {
            _byName.TryAdd(j.Name, j.Index);
            foreach (var a in j.Aliases) _byName.TryAdd(a, j.Index);
        }
        var children = new List<int>[joints.Count];
        for (int i = 0; i < joints.Count; i++) children[i] = [];
        foreach (var j in joints) if (j.Parent >= 0) children[j.Parent].Add(j.Index);
        Children = children;
        var world = new Matrix4x4[joints.Count];
        foreach (var j in joints)   // parents come first (file order, depth first)
            world[j.Index] = Local(j) * (j.Parent >= 0 ? world[j.Parent] : Matrix4x4.Identity);
        DefaultWorld = world;
    }

    public IReadOnlyList<SkeletonJoint> Joints { get; }
    public IReadOnlyList<IReadOnlyList<int>> Children { get; }
    public IReadOnlyList<AttachmentPoint> AttachmentPoints { get; }

    /// <summary>Each joint's transform from its own frame to the avatar's root frame at the default pose
    /// (row-vector convention, System.Numerics: a point p maps to Vector3.Transform(p, m)).</summary>
    public IReadOnlyList<Matrix4x4> DefaultWorld { get; }

    public int BoneCount => Joints.Count(j => j.Kind == JointKind.Bone);
    public int CollisionVolumeCount => Joints.Count(j => j.Kind == JointKind.CollisionVolume);

    /// <summary>The joint's index by name or alias; -1 when the skeleton has no such joint.</summary>
    public int IndexOf(string name) => _byName.TryGetValue(name, out int i) ? i : -1;

    /// <summary>The nearest base joint above <paramref name="joint"/>, skipping extended ones; -1 above the top.</summary>
    public int BaseAncestor(int joint)
    {
        for (int p = Joints[joint].Parent; p >= 0; p = Joints[p].Parent)
            if (Joints[p].Base) return p;
        return -1;
    }

    /// <summary>The joint's local transform: scale, then rotation, then its offset from the parent.</summary>
    public static Matrix4x4 Local(SkeletonJoint j) =>
        Matrix4x4.CreateScale(j.Kind == JointKind.Bone ? Vector3.One : j.Scale) * Matrix4x4.CreateFromQuaternion(j.Rotation) * Matrix4x4.CreateTranslation(j.Position);

    /// <summary>Reads a skeleton file and, when given, an avatar definition file for its attachment points.</summary>
    public static CharacterSkeleton Parse(byte[] skeletonXml, byte[]? definitionXml = null)
    {
        if (skeletonXml.Length > MaxBytes) throw new InvalidDataException($"skeleton file over {MaxBytes} bytes");
        var doc = Load(skeletonXml);
        var root = doc.DocumentElement ?? throw new InvalidDataException("skeleton file has no root element");
        var joints = new List<SkeletonJoint>();
        foreach (var e in root.ChildNodes.OfType<XmlElement>()) Walk(e, -1, joints);
        if (joints.Count == 0) throw new InvalidDataException("skeleton file has no bones");
        var points = new List<AttachmentPoint>();
        if (definitionXml is not null)
        {
            if (definitionXml.Length > 16 * MaxBytes) throw new InvalidDataException("avatar definition file too large");
            foreach (var e in Load(definitionXml).GetElementsByTagName("attachment_point").OfType<XmlElement>())
                points.Add(new AttachmentPoint(int.Parse(e.GetAttribute("id"), CultureInfo.InvariantCulture), e.GetAttribute("name"), e.GetAttribute("joint"),
                    Vec(e.GetAttribute("position")), PointRotation(Vec(e.GetAttribute("rotation")))));
        }
        return new CharacterSkeleton(joints, points);
    }

    private static void Walk(XmlElement e, int parent, List<SkeletonJoint> joints)
    {
        var kind = e.Name switch { "bone" => JointKind.Bone, "collision_volume" => JointKind.CollisionVolume, _ => (JointKind?)null };
        if (kind is null) return;
        if (joints.Count >= MaxJoints) throw new InvalidDataException($"skeleton file declares more than {MaxJoints} joints");
        string name = e.GetAttribute("name");
        if (name.Length == 0) throw new InvalidDataException("a skeleton joint has no name");
        var j = new SkeletonJoint(joints.Count, name, parent, kind.Value, e.GetAttribute("support") != "extended",
            Vec(e.GetAttribute("pos")), Euler(Vec(e.GetAttribute("rot"))), e.HasAttribute("scale") ? Vec(e.GetAttribute("scale")) : Vector3.One,
            e.GetAttribute("aliases").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        joints.Add(j);
        if (kind == JointKind.CollisionVolume) return;
        foreach (var c in e.ChildNodes.OfType<XmlElement>()) Walk(c, j.Index, joints);
    }

    private static XmlDocument Load(byte[] xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        doc.Load(reader);
        return doc;
    }

    private static Vector3 Vec(string s)
    {
        if (s.Length == 0) return Vector3.Zero;
        var p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 3) throw new InvalidDataException($"expected three numbers: '{s}'");
        return new(F(p[0]), F(p[1]), F(p[2]));
        static float F(string v) => float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Degrees about x, then y, then z (the file's order), as a rotation: a point is turned about x first, the result
    /// about y, that about z. (System.Numerics multiplies quaternions the usual way, right factor applied first, so the
    /// product is written z * y * x.)</summary>
    public static Quaternion Euler(Vector3 degrees)
    {
        var r = degrees * (MathF.PI / 180f);
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, r.Z) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, r.X));
    }

    /// <summary>An attachment point's rotation attribute (degrees about x, y and z) as a rotation. STUB: not yet the
    /// official viewer's order.</summary>
    public static Quaternion PointRotation(Vector3 degrees) => Euler(degrees);
}
