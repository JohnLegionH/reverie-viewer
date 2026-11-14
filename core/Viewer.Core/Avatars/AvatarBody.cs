// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Xml;

namespace Viewer.Core.Avatars;

/// <summary>
/// How a weighted body mesh binds to the skeleton. A vertex's weight w names two consecutive entries of the mesh's joint
/// list: entry floor(w) with weight 1 - frac(w) and entry floor(w) + 1 with weight frac(w). The joint list is built by
/// walking the skeleton depth first, children in file order: for each joint that is one of the mesh's skin joints, its
/// nearest base ancestor is added first unless the list already ends with that ancestor, then the joint itself. Extended
/// joints (added later between base joints) are skipped when looking for the ancestor, so the base meshes keep binding
/// the way they were weighted. Above the top joint the ancestor is the avatar's root. (Behaviour as the official viewer's
/// avatar joint mesh setup describes it.) Each entry's skin transform is its joint's current transform times the inverse
/// of the joint's default transform.
/// </summary>
public static class SkinBinding
{
    /// <summary>The avatar's root, which is not a skeleton joint.</summary>
    public const int Root = -1;

    /// <summary>The mesh's joint list as skeleton indices (<see cref="Root"/> for the avatar's root).</summary>
    public static int[] JointList(CharacterSkeleton skeleton, IReadOnlyList<string> skinJoints)
    {
        var wanted = new HashSet<int>();
        foreach (var name in skinJoints)
        {
            int i = skeleton.IndexOf(name);
            if (i < 0) throw new InvalidDataException($"skin joint '{name}' is not in the skeleton");
            wanted.Add(i);
        }
        var list = new List<int>();
        void Visit(int j)
        {
            if (wanted.Contains(j))
            {
                int ancestor = skeleton.BaseAncestor(j);
                if (list.Count == 0 || list[^1] != ancestor) list.Add(ancestor);
                list.Add(j);
            }
            foreach (int c in skeleton.Children[j]) Visit(c);
        }
        foreach (var top in skeleton.Joints.Where(x => x.Parent < 0)) Visit(top.Index);
        return list.ToArray();
    }

    /// <summary>The two joints and the second one's weight for each vertex (A == B with weight 0 for a single joint).</summary>
    public static (int A, int B, float WeightB)[] Bind(float[] weights, int[] jointList)
    {
        var r = new (int, int, float)[weights.Length];
        for (int v = 0; v < weights.Length; v++)
        {
            float w = weights[v];
            int i = (int)MathF.Floor(w);
            float f = w - i;
            if (i < 0 || i >= jointList.Length || (f > 0 && i + 1 >= jointList.Length))
                throw new InvalidDataException($"vertex {v}: weight {w} is outside the mesh's {jointList.Length} joint entries");
            r[v] = f > 0 ? (jointList[i], jointList[i + 1], f) : (jointList[i], jointList[i], 0f);
        }
        return r;
    }
}

/// <summary>One drawn part of the body: its mesh, the bake slot that textures it, and per vertex two palette entries and
/// the second one's weight. <c>Rigid</c> is the matrix an unweighted part (an eyeball) was moved by from its joint's own frame to the root frame (null for a weighted part).</summary>
public sealed record BodyPart(string Type, string BakeSlot, LlmMesh Mesh, Vector3[] Positions, Vector3[] Normals, int[] BoneA, int[] BoneB, float[] WeightB, Matrix4x4? Rigid = null);

/// <summary>
/// The default body read from a character folder: the skeleton, the attachment points and the full-detail body meshes the
/// avatar definition file names, bound to one palette of skinning joints shared by every part. Default shape: no shape
/// parameter is applied. The folder is configured by path and read in place; nothing in it is copied anywhere.
/// </summary>
public sealed class AvatarBody
{
    public const string SkeletonFile = "avatar_skeleton.xml", DefinitionFile = "avatar_lad.xml";

    /// <summary>The parts drawn by default, with their bake slot and, for unweighted parts, the joint they ride on.</summary>
    public static readonly IReadOnlyList<(string Type, string Bake, string? RigidJoint)> DefaultParts =
    [
        ("headMesh", "head", null), ("upperBodyMesh", "upper", null), ("lowerBodyMesh", "lower", null),
        ("eyelashMesh", "head", null), ("eyeBallLeftMesh", "eyes", "mEyeLeft"), ("eyeBallRightMesh", "eyes", "mEyeRight"),
    ];

    private AvatarBody(CharacterSkeleton skeleton, IReadOnlyList<BodyPart> parts, int[] palette)
    {
        Skeleton = skeleton;
        Parts = parts;
        Palette = palette;
        var inverse = new Matrix4x4[palette.Length];
        for (int i = 0; i < palette.Length; i++)
            Matrix4x4.Invert(palette[i] == SkinBinding.Root ? Matrix4x4.Identity : skeleton.DefaultWorld[palette[i]], out inverse[i]);
        InverseBind = inverse;
        float min = parts.Min(p => p.Positions.Min(v => v.Z)), max = parts.Max(p => p.Positions.Max(v => v.Z));
        FootHeight = min;
        Height = max - min;
    }

    public CharacterSkeleton Skeleton { get; }

    /// <summary>The visual parameters of the avatar definition file (what shapes the skeleton); set when the body is read from a character folder.</summary>
    public ShapeDefinition? Shape { get; private set; }
    public IReadOnlyList<BodyPart> Parts { get; }

    /// <summary>The skinning joints every part binds to, as skeleton indices (<see cref="SkinBinding.Root"/> first when used).</summary>
    public int[] Palette { get; }

    /// <summary>Per palette entry, the inverse of its joint's default transform.</summary>
    public IReadOnlyList<Matrix4x4> InverseBind { get; }

    /// <summary>The lowest point of the default body above the avatar's root (the soles), and the body's height.</summary>
    public float FootHeight { get; }
    public float Height { get; }

    public int VertexCount => Parts.Sum(p => p.Mesh.VertexCount);
    public int TriangleCount => Parts.Sum(p => p.Mesh.Indices.Length / 3);

    /// <summary>Reads a character folder (its skeleton file, its avatar definition file and the meshes that file names).</summary>
    public static AvatarBody Load(string folder, IReadOnlyList<(string Type, string Bake, string? RigidJoint)>? parts = null)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException($"character folder not found: '{folder}'");
        return LoadFrom(name => File.ReadAllBytes(Path.Combine(folder, name)), parts);
    }

    /// <summary>Reads a character the way <see cref="Load"/> does, with <paramref name="read"/> giving the bytes of each file by plain file name (the
    /// skeleton file, the definition file and the meshes the definition names).</summary>
    public static AvatarBody LoadFrom(Func<string, byte[]> read, IReadOnlyList<(string Type, string Bake, string? RigidJoint)>? parts = null)
    {
        byte[] definition = read(DefinitionFile);
        var skeleton = CharacterSkeleton.Parse(read(SkeletonFile), definition);
        var files = MeshFiles(definition);
        var built = Build(skeleton, ResolveParts(definition, parts ?? DefaultParts).Select(p =>
        {
            if (!files.TryGetValue(p.Type, out var file)) throw new InvalidDataException($"the avatar definition names no full-detail {p.Type}");
            if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException($"mesh file name '{file}' is not a plain file name");
            return (p.Type, p.Bake, p.RigidJoint, LlmMesh.Parse(read(file)));
        }).ToList());
        built.Shape = ShapeDefinition.Parse(definition);
        return built;
    }

    /// <summary>The joint each full-detail mesh element names in its optional <c>joint</c> attribute, by mesh type. A definition without the attribute
    /// gives an empty table.</summary>
    public static Dictionary<string, string> MeshJoints(byte[] definitionXml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new MemoryStream(definitionXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        var joints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in doc.GetElementsByTagName("mesh").OfType<XmlElement>())
            if (e.GetAttribute("lod") == "0" && e.GetAttribute("type") is { Length: > 0 } type && e.GetAttribute("joint") is { Length: > 0 } joint)
                joints.TryAdd(type, joint);
        return joints;
    }

    /// <summary>The parts to read, with each one's joint as the definition states it: a mesh element's <c>joint</c> attribute replaces the joint the
    /// caller's list gives that part; without the attribute the list's joint stands (for the default parts, the left eye on mEyeLeft and the right on
    /// mEyeRight, as the official viewer places them).</summary>
    public static IReadOnlyList<(string Type, string Bake, string? RigidJoint)> ResolveParts(byte[] definitionXml, IReadOnlyList<(string Type, string Bake, string? RigidJoint)> parts)
    {
        var joints = MeshJoints(definitionXml);
        return parts.Select(p => joints.TryGetValue(p.Type, out var joint) ? (p.Type, p.Bake, (string?)joint) : p).ToList();
    }

    /// <summary>The full-detail (level 0) mesh file for each mesh type the avatar definition names.</summary>
    public static Dictionary<string, string> MeshFiles(byte[] definitionXml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new MemoryStream(definitionXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in doc.GetElementsByTagName("mesh").OfType<XmlElement>())
            if (e.GetAttribute("lod") == "0" && e.GetAttribute("type") is { Length: > 0 } type && e.GetAttribute("file_name") is { Length: > 0 } file)
                files.TryAdd(type, file);
        return files;
    }

    /// <summary>Binds parsed meshes to a skeleton (no file access).</summary>
    public static AvatarBody Build(CharacterSkeleton skeleton, IReadOnlyList<(string Type, string Bake, string? RigidJoint, LlmMesh Mesh)> meshes)
    {
        var palette = new List<int>();
        int Slot(int joint)
        {
            int i = palette.IndexOf(joint);
            if (i < 0) { palette.Add(joint); i = palette.Count - 1; }
            return i;
        }
        var parts = new List<BodyPart>();
        foreach (var (type, bake, rigid, mesh) in meshes)
        {
            int n = mesh.VertexCount;
            var a = new int[n];
            var b = new int[n];
            var w = new float[n];
            var pos = mesh.Positions;
            var nrm = mesh.Normals;
            if (rigid is not null)
            {
                // an unweighted part is stored in its joint's own frame: moved to the root frame at the default pose
                int j = skeleton.IndexOf(rigid);
                if (j < 0) throw new InvalidDataException($"{type}: joint '{rigid}' is not in the skeleton");
                var m = skeleton.DefaultWorld[j];
                pos = pos.Select(p => Vector3.Transform(p, m)).ToArray();
                nrm = nrm.Select(v => Vector3.Normalize(Vector3.TransformNormal(v, m))).ToArray();
                int s = Slot(j);
                Array.Fill(a, s);
                Array.Fill(b, s);
            }
            else
            {
                if (!mesh.Weighted) throw new InvalidDataException($"{type} is not weighted and names no joint to ride on");
                var bound = SkinBinding.Bind(mesh.Weights, SkinBinding.JointList(skeleton, mesh.SkinJoints));
                for (int v = 0; v < n; v++) { a[v] = Slot(bound[v].A); b[v] = Slot(bound[v].B); w[v] = bound[v].WeightB; }
            }
            parts.Add(new BodyPart(type, bake, mesh, pos, nrm, a, b, w, rigid is null ? null : skeleton.DefaultWorld[skeleton.IndexOf(rigid)]));
        }
        return new AvatarBody(skeleton, parts, palette.ToArray());
    }
}
