// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Assets;

/// <summary>Counts a transport's whole-file texture fetches and the bytes they returned,
/// so partial and whole logins can be compared by the bytes fetched from the grid.</summary>
public sealed class FetchCounter
{
    private long _requests, _ok, _bytes;

    public long Requests => Interlocked.Read(ref _requests);
    public long Ok => Interlocked.Read(ref _ok);
    public long Bytes => Interlocked.Read(ref _bytes);

    public Func<Guid, CancellationToken, Task<AssetFetchResult>> Wrap(Func<Guid, CancellationToken, Task<AssetFetchResult>> fetch) => async (id, ct) =>
    {
        Interlocked.Increment(ref _requests);
        var r = await fetch(id, ct).ConfigureAwait(false);
        if (r.Status == AssetFetchStatus.Ok)
        {
            Interlocked.Increment(ref _ok);
            Interlocked.Add(ref _bytes, r.Bytes.Length);
        }
        return r;
    };
}
