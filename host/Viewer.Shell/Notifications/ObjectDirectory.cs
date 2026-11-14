// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Notifications;

/// <summary>What the viewer knows of an object that spoke: its name, owner, and where it was said to be.</summary>
/// <param name="Place">The place text an instant message from the object carried (a region name and position); empty when it gave none.</param>
/// <param name="RegionPosition">Where the object spoke from in the region the user is in, when it spoke in local chat.</param>
public sealed record ObjectInfo(Guid ObjectId, Guid OwnerId, string Name, bool GroupOwned, string Place, System.Numerics.Vector3? RegionPosition = null);

/// <summary>The objects that recently spoke to the user, kept so a link on an object's name can open its details. Bounded.</summary>
public sealed class ObjectDirectory
{
    public const int Capacity = 200;

    private readonly Dictionary<Guid, ObjectInfo> _byId = new();
    private readonly LinkedList<Guid> _order = new();

    public int Count => _byId.Count;

    /// <summary>Remembers (or refreshes) an object. An object with no id is not kept. The oldest is forgotten past <see cref="Capacity"/>.</summary>
    public void Remember(ObjectInfo info)
    {
        if (info.ObjectId == Guid.Empty)
            return;
        if (_byId.ContainsKey(info.ObjectId))
            _order.Remove(info.ObjectId);
        _byId[info.ObjectId] = info;
        _order.AddLast(info.ObjectId);
        while (_byId.Count > Capacity)
        {
            _byId.Remove(_order.First!.Value);
            _order.RemoveFirst();
        }
    }

    public ObjectInfo? Find(Guid objectId) => _byId.GetValueOrDefault(objectId);
}
