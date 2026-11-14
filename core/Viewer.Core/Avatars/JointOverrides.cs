// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Meshing;

namespace Viewer.Core.Avatars;

/// <summary>
/// The joint position overrides and pelvis offset that the rigged meshes an avatar wears ask for, and which of them wins.
/// Behaviour as the official viewer's (read for behaviour: LLVOAvatar::addAttachmentOverridesForObject,
/// removeAttachmentOverrides, LLJoint::addAttachmentPosOverride / updatePos, LLVector3OverrideMap::findActiveOverride,
/// LLAvatarAppearance::addPelvisFixup / hasPelvisFixup), in our own words:
///   - A mesh carries overrides only when it lists alternate inverse bind matrices, one per joint name; otherwise all of
///     its overrides, and its pelvis offset, are ignored.
///   - The override for a joint is the translation of that joint's alternate matrix: a new position relative to the
///     joint's parent, replacing the skeleton's own. It counts only when it differs from the joint's default by more than
///     0.1 mm. A joint name the skeleton does not know is skipped.
///   - When several meshes override the same joint, the mesh with the greatest asset id wins, comparing the 16 id bytes in
///     order (the textual order of the id), not the order the meshes were worn in.
///   - When a mesh asks for it (lock-scale flag), every joint it overrides also has its scale pinned to the default.
///   - A non-zero pelvis offset raises the avatar by that many metres; again the greatest asset id wins.
///   - Removing a mesh removes only its entries; whatever remains decides, and with nothing left the joint is at its default.
/// One instance per avatar. Not thread-safe.
/// </summary>
public sealed class JointOverrides
{
    /// <summary>The official viewer's smallest difference from the default position that counts as an override (0.1 mm).</summary>
    public const float PositionThreshold = 0.0001f;

    private readonly AvatarRig _rig;
    private readonly Dictionary<int, SortedDictionary<Guid, Vector3>> _positions = [];
    private readonly Dictionary<int, HashSet<Guid>> _scaleLocks = [];
    private readonly SortedDictionary<Guid, float> _pelvis = new(new RfcOrder());
    private readonly HashSet<Guid> _meshes = [];

    public JointOverrides(AvatarRig rig) => _rig = rig;

    /// <summary>The meshes whose overrides are in effect.</summary>
    public int MeshCount => _meshes.Count;

    /// <summary>Takes a mesh's overrides. Returns true when this changed a joint's winning position, the lock set or the
    /// pelvis offset. A mesh already taken is left as it is (like the official viewer, which skips a mesh it has loaded).</summary>
    public bool Add(Guid meshId, MeshSkinBlock skin)
    {
        if (meshId == Guid.Empty || _meshes.Contains(meshId)) return false;
        if (!skin.HasJointOverrides) return false;   // no alternate matrices, or not one per joint: ignored, pelvis offset too
        _meshes.Add(meshId);
        bool changed = false;
        for (int i = 0; i < skin.JointNames.Count; i++)
        {
            int node = _rig.Find(skin.JointNames[i]);
            if (node < 0 || node >= _rig.JointCount) continue;   // unknown, the root, or an attachment point: not an overridable joint
            var pos = skin.AlternateInverseBind[i].Translation;
            if (!Finite(pos) || (pos - _rig.DefaultPosition[node]).LengthSquared() <= PositionThreshold * PositionThreshold) continue;
            var before = PositionOf(node);
            if (!_positions.TryGetValue(node, out var map)) _positions[node] = map = new(new RfcOrder());
            map[meshId] = pos;
            changed |= PositionOf(node) != before;
            if (skin.LockScaleIfJointPosition)
            {
                if (!_scaleLocks.TryGetValue(node, out var set)) _scaleLocks[node] = set = [];
                changed |= set.Add(meshId) && set.Count == 1;
            }
        }
        if (skin.PelvisOffset != 0f && float.IsFinite(skin.PelvisOffset))
        {
            float before = PelvisFixup;
            _pelvis[meshId] = skin.PelvisOffset;
            changed |= PelvisFixup != before;
        }
        return changed;
    }

    /// <summary>Drops a mesh's entries (it was detached or its attachment went away). Returns true when anything was in effect.</summary>
    public bool Remove(Guid meshId)
    {
        if (!_meshes.Remove(meshId)) return false;
        foreach (var map in _positions.Values) map.Remove(meshId);
        foreach (var set in _scaleLocks.Values) set.Remove(meshId);
        _pelvis.Remove(meshId);
        return true;
    }

    /// <summary>A copy that does not change when this one does (and the other way round): what a worker thread reads while the main thread goes on
    /// adding and removing the meshes an avatar wears.</summary>
    public JointOverrides Clone()
    {
        var copy = new JointOverrides(_rig);
        foreach (var (node, map) in _positions) copy._positions[node] = new SortedDictionary<Guid, Vector3>(map, new RfcOrder());
        foreach (var (node, set) in _scaleLocks) copy._scaleLocks[node] = [.. set];
        foreach (var (mesh, offset) in _pelvis) copy._pelvis[mesh] = offset;
        copy._meshes.UnionWith(_meshes);
        return copy;
    }

    public void Clear()
    {
        _meshes.Clear();
        _positions.Clear();
        _scaleLocks.Clear();
        _pelvis.Clear();
    }

    /// <summary>The joint's position relative to its parent: the winning override, else its default.</summary>
    public Vector3 PositionOf(int node) =>
        _positions.TryGetValue(node, out var map) && map.Count > 0 ? map.Last().Value : _rig.DefaultPosition[node];

    /// <summary>The winning override for the joint, when some worn mesh asks for one (false: the joint is at its default).</summary>
    public bool TryGet(int node, out Vector3 position)
    {
        if (_positions.TryGetValue(node, out var map) && map.Count > 0)
        {
            position = map.Last().Value;
            return true;
        }
        position = default;
        return false;
    }

    /// <summary>The nodes some worn mesh moves.</summary>
    public IEnumerable<int> OverriddenNodes => _positions.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key);

    /// <summary>The nodes whose scale some worn mesh pins.</summary>
    public IEnumerable<int> LockedNodes => _scaleLocks.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key);

    /// <summary>True when some mesh pins this joint's scale to its default.</summary>
    public bool ScaleLocked(int node) => _scaleLocks.TryGetValue(node, out var set) && set.Count > 0;

    /// <summary>The metres the avatar is raised by the winning mesh's pelvis offset; 0 when none asks.</summary>
    public float PelvisFixup => _pelvis.Count == 0 ? 0f : _pelvis.Last().Value;

    /// <summary>The mesh whose position override wins for the joint, or null when the joint has none.</summary>
    public Guid? WinnerOf(int node) => _positions.TryGetValue(node, out var map) && map.Count > 0 ? map.Last().Key : null;

    /// <summary>Writes the effective local position of every node: defaults, with the winning overrides on top.</summary>
    public void Apply(Span<Vector3> positions)
    {
        if (positions.Length < _rig.NodeCount) throw new ArgumentException("span shorter than the rig's node count");
        _rig.DefaultPosition.AsSpan().CopyTo(positions);
        foreach (var (node, map) in _positions)
            if (map.Count > 0) positions[node] = map.Last().Value;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Asset ids compared byte by byte in their textual (RFC 4122) order, as the official viewer compares them;
    /// .NET's own Guid order differs (it reads the first three groups as little-endian numbers).</summary>
    private sealed class RfcOrder : IComparer<Guid>
    {
        public int Compare(Guid a, Guid b)
        {
            Span<byte> x = stackalloc byte[16], y = stackalloc byte[16];
            a.TryWriteBytes(x, bigEndian: true, out _);
            b.TryWriteBytes(y, bigEndian: true, out _);
            return x.SequenceCompareTo(y);
        }
    }
}
