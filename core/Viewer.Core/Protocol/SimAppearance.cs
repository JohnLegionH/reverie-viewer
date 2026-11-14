// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Protocol;

/// <summary>
/// The two announcements of a neighbour (enable-simulator, with the handle and size; establish-agent-communication, with the seed capability)
/// are handled at the same time and in no fixed order. When the second is handled first, the library connects the neighbour with no handle,
/// the data that arrives meanwhile cannot be placed and is lost, and the grid does not send it again. So the establish message waits a
/// short while for the simulator the enable message is making.
/// </summary>
internal static class SimAppearance
{
    /// <summary>Looks for something until it is found or <paramref name="limit"/> has passed, sleeping <paramref name="poll"/> between looks
    /// (the sleep is given). Null when it did not appear.</summary>
    public static T? WaitFor<T>(Func<T?> find, TimeSpan limit, TimeSpan poll, Action<TimeSpan> sleep) where T : class
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            if (find() is { } found)
                return found;
            if (waited >= limit)
                return null;
            sleep(poll);
            waited += poll;
        }
    }
}
