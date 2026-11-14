// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;

namespace Viewer.Shell;

/// <summary>
/// One layer's own queue of the arrivals of a source that several layers share (the meshes and materials of every region). Each layer that
/// subscribed is told of every arrival, so an arrival one layer took does not leave another layer waiting for ever.
/// </summary>
public sealed class ArrivalFeed
{
    private readonly ConcurrentQueue<Guid> _queue = new();
    private readonly Action<ArrivalFeed>? _leave;

    public ArrivalFeed(Action<ArrivalFeed>? leave = null) => _leave = leave;

    internal void Add(Guid id) => _queue.Enqueue(id);

    /// <summary>The next asset that arrived since this feed was made or last asked; false when none.</summary>
    public bool TryTake(out Guid id) => _queue.TryDequeue(out id);

    /// <summary>The layer is gone: no more arrivals are kept for it.</summary>
    public void Leave() => _leave?.Invoke(this);
}
