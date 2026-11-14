// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>
/// The avatar skeleton as a flat list of nodes a rigged mesh or an attachment can name: the skeleton's joints (bones and
/// collision volumes, in the skeleton's order) followed by the attachment points, each of which is a joint of its own in
/// the official viewer (a child of the joint it names, offset by the point's position and rotation). Parents always come
/// before their children, so one forward pass computes every world matrix (<see cref="JointWorlds"/>).
///
/// Name lookup follows the official viewer (read for behaviour: LLVOAvatar::getJoint, getJointAliases): the joint's own
/// name, any alias the skeleton file lists, an attachment point's name with its spaces replaced by underscores, or the
/// attachment point's own name; "mRoot" is the avatar root. Matching is exact (case-sensitive). A name nothing matches is
/// unknown: a joint position override naming it is skipped, and a skin binding naming it is moved to mPelvis.
/// </summary>
public sealed class AvatarRig
{
    /// <summary>What <see cref="Find"/> returns for "mRoot": the avatar root, whose world matrix is the identity.</summary>
    public const int Root = -2;

    /// <summary>What <see cref="Find"/> returns for a name that matches nothing.</summary>
    public const int Unknown = -1;

    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly int[] _parent;
    private readonly string[] _name;
    private readonly int[] _pointNode;   // attachment point id -> node, -1 for an id the skeleton does not define

    public AvatarRig(CharacterSkeleton skeleton)
    {
        Skeleton = skeleton;
        JointCount = skeleton.Joints.Count;
        var points = skeleton.AttachmentPoints;
        NodeCount = JointCount + points.Count;
        _parent = new int[NodeCount];
        _name = new string[NodeCount];
        DefaultScale = new Vector3[NodeCount];
        Array.Fill(DefaultScale, Vector3.One);
        DefaultPosition = new Vector3[NodeCount];
        DefaultRotation = new Quaternion[NodeCount];
        IsScreen = new bool[NodeCount];
        int maxId = points.Count == 0 ? 0 : points.Max(p => p.Id);
        _pointNode = new int[maxId + 1];
        Array.Fill(_pointNode, -1);

        for (int i = 0; i < JointCount; i++)
        {
            var j = skeleton.Joints[i];
            _parent[i] = j.Parent;
            _name[i] = j.Name;
            DefaultPosition[i] = j.Position;
            DefaultRotation[i] = j.Rotation;
            if (j.Kind == JointKind.CollisionVolume) DefaultScale[i] = j.Scale;
            _byName.TryAdd(j.Name, i);
        }
        for (int i = 0; i < JointCount; i++)
            foreach (var a in skeleton.Joints[i].Aliases) _byName.TryAdd(a, i);

        for (int k = 0; k < points.Count; k++)
        {
            var p = points[k];
            int node = JointCount + k;
            _parent[node] = p.Joint == CharacterSkeleton.RootName || p.Joint == "mScreen" ? -1 : skeleton.IndexOf(p.Joint);
            if (_parent[node] < 0 && p.Joint != CharacterSkeleton.RootName && p.Joint != "mScreen")
                throw new InvalidDataException($"attachment point '{p.Name}' names a joint the skeleton lacks: '{p.Joint}'");
            IsScreen[node] = p.Joint == "mScreen";
            _name[node] = p.Name;
            DefaultPosition[node] = p.Position;
            DefaultRotation[node] = p.Rotation;
            _pointNode[p.Id] = node;
            _byName.TryAdd(p.Name, node);
            _byName.TryAdd(p.Name.Replace(' ', '_'), node);
        }
        Pelvis = skeleton.IndexOf("mPelvis");
    }

    public CharacterSkeleton Skeleton { get; }

    /// <summary>The skeleton's joints come first in the node list: nodes below this are joints, the rest attachment points.</summary>
    public int JointCount { get; }
    public int NodeCount { get; }

    /// <summary>The node of mPelvis, where a skin binding to an unknown joint name goes; -1 if the skeleton has none.</summary>
    public int Pelvis { get; }

    /// <summary>Each node's rest position relative to its parent (an attachment point's offset from its joint).</summary>
    public Vector3[] DefaultPosition { get; }
    public Quaternion[] DefaultRotation { get; }

    /// <summary>Each node's rest scale: a collision volume's own, one for everything else (bones carry no scale of their own in the skeleton file).</summary>
    public Vector3[] DefaultScale { get; }

    /// <summary>True for a HUD point (it hangs off the screen, not the body).</summary>
    public bool[] IsScreen { get; }

    public string NodeName(int node) => _name[node];

    /// <summary>The parent node; -1 for a node directly under the avatar root.</summary>
    public int Parent(int node) => _parent[node];

    public bool IsAttachmentPoint(int node) => node >= JointCount;

    /// <summary>The node of an attachment point by its id; -1 for an id the skeleton does not define.</summary>
    public int PointNode(int attachmentPointId) => attachmentPointId >= 0 && attachmentPointId < _pointNode.Length ? _pointNode[attachmentPointId] : -1;

    /// <summary>The node a name refers to; <see cref="Root"/> for "mRoot"; <see cref="Unknown"/> for no match.</summary>
    public int Find(string name)
    {
        if (name == CharacterSkeleton.RootName) return Root;
        return _byName.TryGetValue(name, out int n) ? n : Unknown;
    }

    /// <summary>The node a skin binding to <paramref name="name"/> uses: the match, or mPelvis for an unknown name
    /// (the official viewer's repair for legacy meshes with invalid joint names).</summary>
    public int FindForSkinning(string name)
    {
        int n = Find(name);
        return n == Unknown ? Pelvis : n;
    }
}
