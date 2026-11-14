// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>One <see cref="SimRef"/> per simulator, replaced when the simulator's handle is not the one it was made with.</summary>
internal sealed class SimRefMap<T> where T : class
{
    private readonly Dictionary<T, SimRef> _refs = new(ReferenceEqualityComparer.Instance);

    /// <summary>Called (outside the lock) when a simulator's reference was replaced because its handle changed: the simulator, the old
    /// reference, the new one. Not called when a reference is first made.</summary>
    public Action<T, SimRef, SimRef>? OnReplaced { get; set; }

    public SimRef Get(T sim, ulong handle, string name)
    {
        SimRef? old = null;
        SimRef made;
        lock (_refs)
        {
            if (!_refs.TryGetValue(sim, out var r) || r.Handle != handle)
            {
                old = r;
                _refs[sim] = r = new SimRef(handle, name);
            }
            made = r;
        }
        if (old is not null)
            OnReplaced?.Invoke(sim, old, made);
        return made;
    }

    /// <summary>The simulator a reference was made for, or null for a reference this map did not hand out.</summary>
    public T? Find(SimRef? sim)
    {
        if (sim is null)
            return null;
        lock (_refs)
            foreach (var pair in _refs)
                if (ReferenceEquals(pair.Value, sim))
                    return pair.Key;
        return null;
    }

    public int Count { get { lock (_refs) return _refs.Count; } }

    public void Clear() { lock (_refs) _refs.Clear(); }
}
