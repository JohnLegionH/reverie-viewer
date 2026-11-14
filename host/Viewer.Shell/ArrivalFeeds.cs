// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The layers subscribed to a source, each with its own <see cref="ArrivalFeed"/>; an arrival is added to every one.</summary>
internal sealed class ArrivalFeeds
{
    private readonly object _lock = new();
    private ArrivalFeed[] _feeds = [];

    public ArrivalFeed Subscribe()
    {
        var feed = new ArrivalFeed(Leave);
        lock (_lock) _feeds = [.. _feeds, feed];
        return feed;
    }

    private void Leave(ArrivalFeed feed)
    {
        lock (_lock) _feeds = _feeds.Where(f => !ReferenceEquals(f, feed)).ToArray();
    }

    public void Arrived(Guid id)
    {
        foreach (var feed in Volatile.Read(ref _feeds)) feed.Add(id);
    }
}