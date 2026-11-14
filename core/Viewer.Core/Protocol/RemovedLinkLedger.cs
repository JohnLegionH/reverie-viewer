// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Protocol;

/// <summary>
/// Remembers the links this viewer has just removed from the current outfit folder. The grid deletes a link some time after the
/// removal message, so an answer or a late notice that arrives meanwhile can still list it; the library keeps what such an answer
/// lists (it does not drop what the answer leaves out), so the link would then show as worn until the session ends. While a
/// removal is remembered, that link is dropped from the library's store whenever it turns up again and is not reported.
/// </summary>
internal sealed class RemovedLinkLedger
{
    /// <summary>How long a removal is remembered.</summary>
    internal static readonly TimeSpan Keep = TimeSpan.FromSeconds(60);

    private readonly Func<DateTime> _now;
    private readonly Dictionary<Guid, DateTime> _until = new();
    private readonly object _gate = new();

    public RemovedLinkLedger(Func<DateTime>? now = null) { _now = now ?? (() => DateTime.UtcNow); }

    /// <summary>Notes that a removal of the link <paramref name="id"/> was sent.</summary>
    public void Removed(Guid id)
    {
        lock (_gate)
        {
            Forget();
            _until[id] = _now() + Keep;
        }
    }

    /// <summary>True while the removal of <paramref name="id"/> is remembered.</summary>
    public bool IsRemoved(Guid id)
    {
        lock (_gate)
            return _until.TryGetValue(id, out var until) && until > _now();
    }

    /// <summary>How many removals are remembered now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                Forget();
                return _until.Count;
            }
        }
    }

    private void Forget()
    {
        var now = _now();
        foreach (var id in _until.Where(e => e.Value <= now).Select(e => e.Key).ToList())
            _until.Remove(id);
    }
}
