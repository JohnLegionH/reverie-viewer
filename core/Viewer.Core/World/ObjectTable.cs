// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections;

namespace Viewer.Core.World;

/// <summary>
/// The region's objects by local id, with an index from a parent to its direct children kept up to date as objects are set, replaced and
/// removed, so a link set is found by walking its own children instead of scanning every object of the region. Like the dictionary it
/// replaces it is not thread-safe: <see cref="WorldState"/> uses it under its lock.
/// </summary>
internal sealed class ObjectTable : IEnumerable<KeyValuePair<uint, ObjectState>>
{
    private readonly Dictionary<uint, ObjectState> _objects = new();
    private readonly Dictionary<uint, HashSet<uint>> _children = new();   // parent local id -> children's local ids (parent 0, a root, is not indexed)

    public int Count => _objects.Count;

    public Dictionary<uint, ObjectState>.ValueCollection Values => _objects.Values;

    public bool ContainsKey(uint local) => _objects.ContainsKey(local);

    public bool TryGetValue(uint local, out ObjectState value) => _objects.TryGetValue(local, out value!);

    public ObjectState this[uint local]
    {
        get => _objects[local];
        set
        {
            if (_objects.TryGetValue(local, out var old))
            {
                if (old.ParentLocalId == value.ParentLocalId)
                {
                    _objects[local] = value;
                    return;
                }
                Unindex(local, old.ParentLocalId);
            }
            _objects[local] = value;
            Index(local, value.ParentLocalId);
        }
    }

    public bool Remove(uint local) => Remove(local, out _);

    public bool Remove(uint local, out ObjectState removed)
    {
        if (!_objects.Remove(local, out removed!))
            return false;
        Unindex(local, removed.ParentLocalId);
        return true;
    }

    public void Clear()
    {
        _objects.Clear();
        _children.Clear();
    }

    /// <summary>The objects whose parent is <paramref name="parent"/>, in no particular order.</summary>
    public IEnumerable<ObjectState> ChildrenOf(uint parent)
    {
        if (!_children.TryGetValue(parent, out var kids))
            yield break;
        foreach (uint kid in kids)
            yield return _objects[kid];
    }

    private void Index(uint local, uint parent)
    {
        if (parent == 0)
            return;
        if (!_children.TryGetValue(parent, out var kids))
            _children[parent] = kids = new HashSet<uint>();
        kids.Add(local);
    }

    private void Unindex(uint local, uint parent)
    {
        if (parent == 0 || !_children.TryGetValue(parent, out var kids))
            return;
        kids.Remove(local);
        if (kids.Count == 0)
            _children.Remove(parent);
    }

    public IEnumerator<KeyValuePair<uint, ObjectState>> GetEnumerator() => _objects.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
