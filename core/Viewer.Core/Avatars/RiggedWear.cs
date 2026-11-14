// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Meshing;

namespace Viewer.Core.Avatars;

/// <summary>
/// The rigged mesh objects one avatar wears, as far as the avatar's joints are concerned: which meshes are worn (by the object
/// that wears them), and the joint position overrides and pelvis offset they ask for with the winner among them
/// (<see cref="JointOverrides"/>). Two objects wearing the same mesh asset share its entry, which goes when the last of them is detached
/// (the official viewer keeps overrides per mesh asset). One instance per avatar. Not thread-safe.
/// </summary>
public sealed class RiggedWear
{
    private readonly Dictionary<uint, Guid> _objects = [];
    private readonly Dictionary<Guid, int> _users = [];

    public RiggedWear(AvatarRig rig) => Overrides = new JointOverrides(rig);

    /// <summary>The overrides in effect; hand it to <see cref="AvatarJoints.Overrides"/>.</summary>
    public JointOverrides Overrides { get; }

    /// <summary>The mesh asset each worn rigged object wears, by the object's local id.</summary>
    public IReadOnlyDictionary<uint, Guid> Meshes => _objects;

    /// <summary>Rigged mesh objects worn.</summary>
    public int Objects => _objects.Count;

    /// <summary>Counts up whenever the overrides or the pelvis offset in effect changed, so a caller can tell when to pose again.</summary>
    public int Version { get; private set; }

    public float PelvisFixup => Overrides.PelvisFixup;

    /// <summary>The object <paramref name="local"/> wears the mesh <paramref name="asset"/>. Returns true when this changed the overrides in effect.
    /// An object already worn with another mesh is detached from that one first.</summary>
    public bool Add(uint local, Guid asset, MeshSkinBlock skin)
    {
        bool changed = false;
        if (_objects.TryGetValue(local, out var had))
        {
            if (had == asset) return false;
            changed = Detach(local);
        }
        _objects[local] = asset;
        _users[asset] = _users.GetValueOrDefault(asset) + 1;
        if (_users[asset] == 1) changed |= Overrides.Add(asset, skin);
        if (changed) Version++;
        return changed;
    }

    /// <summary>The object is no longer worn. Returns true when it was worn.</summary>
    public bool Remove(uint local)
    {
        if (!_objects.ContainsKey(local)) return false;
        if (Detach(local)) Version++;
        return true;
    }

    private bool Detach(uint local)
    {
        var asset = _objects[local];
        _objects.Remove(local);
        if (--_users[asset] > 0) return false;
        _users.Remove(asset);
        return Overrides.Remove(asset);
    }
}
