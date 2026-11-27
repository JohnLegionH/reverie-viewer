// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Media;

/// <summary>Asks the region for the media on an object's faces, once for each media version the object states.</summary>
internal sealed class PrimMediaFetcher<TSim>(Func<TSim, Guid, CancellationToken, Task<IReadOnlyList<FaceMediaEntry>?>> fetch,
    Action<TSim, Guid, string, IReadOnlyList<FaceMediaEntry>> done, Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable where TSim : class
{
    private const int Attempts = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();
    private readonly Dictionary<Guid, string> _wanted = [];
    private readonly SemaphoreSlim _slots = new(2);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private CancellationTokenSource _stop = new();

    public void Want(TSim sim, Guid objectId, string version)
    {
        if (version.Length == 0)
            return;
        CancellationToken ct;
        lock (_lock)
        {
            if (_wanted.GetValueOrDefault(objectId) == version)
                return;
            _wanted[objectId] = version;
            ct = _stop.Token;
        }
        _ = Task.Run(() => Run(sim, objectId, version, ct));
    }

    private bool Current(Guid objectId, string version)
    {
        lock (_lock) return _wanted.GetValueOrDefault(objectId) == version;
    }

    private async Task Run(TSim sim, Guid objectId, string version, CancellationToken ct)
    {
        try
        {
            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                IReadOnlyList<FaceMediaEntry>? faces = null;
                await _slots.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!Current(objectId, version))
                        return;   // a newer version was stated while this one waited
                    faces = await fetch(sim, objectId, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException)) { }
                finally { _slots.Release(); }
                if (ct.IsCancellationRequested || !Current(objectId, version))
                    return;
                if (faces is not null)
                {
                    done(sim, objectId, version, faces);
                    return;
                }
                await _delay(RetryDelay * attempt, ct).ConfigureAwait(false);
            }
            lock (_lock)
                if (_wanted.GetValueOrDefault(objectId) == version)
                    _wanted.Remove(objectId);   // the object's next update asks again
        }
        catch (OperationCanceledException) { }
    }

    public void CancelAll()
    {
        lock (_lock)
        {
            _stop.Cancel();
            _stop = new();
            _wanted.Clear();
        }
    }
    public void Dispose() => CancelAll();
}
