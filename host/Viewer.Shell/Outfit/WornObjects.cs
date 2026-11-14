// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;

namespace Viewer.Shell.Outfit;

/// <summary>
/// The inventory items of the objects the own avatar wears, read from the world state (each worn root object states the item it was
/// taken from). The answer is kept for a short time so a window that asks for every row it draws does not read the world each time.
/// Main thread only.
/// </summary>
public sealed class WornObjects
{
    private readonly WorldState _world;
    private readonly Guid _agent;
    private readonly Func<long> _nowMs;
    private IReadOnlySet<Guid> _items = new HashSet<Guid>();
    private long _readAt = long.MinValue;

    public WornObjects(WorldState world, Guid agent, Func<long> nowMs)
    {
        _world = world;
        _agent = agent;
        _nowMs = nowMs;
    }

    /// <summary>How long an answer is reused, in milliseconds.</summary>
    public const long KeepMs = 250;

    public IReadOnlySet<Guid> Items()
    {
        long now = _nowMs();
        if (_readAt != long.MinValue && now - _readAt < KeepMs)
            return _items;
        _readAt = now;
        _items = _world.Snapshot().Objects.Values
            .Where(o => o.AttachmentPoint is not null && o.WearerAgentId == _agent && o.AttachItemId is { } id && id != Guid.Empty)
            .Select(o => o.AttachItemId!.Value)
            .ToHashSet();
        return _items;
    }
}
