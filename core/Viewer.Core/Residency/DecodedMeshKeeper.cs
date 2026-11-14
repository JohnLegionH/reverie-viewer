// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Residency;

/// <summary>
/// When a mesh's decoded geometry may be let go. It is
/// kept only while it is needed to build a LOD: every LOD request, preparation start and main-thread LOD build from an
/// asset's geometry marks it needed (<see cref="Needed"/>). Once it has not been needed for <see cref="Keep"/> (10 s), and
/// no preparation is in flight for it and no object waits for one of its LODs (the caller's <c>busy</c>),
/// <see cref="Collect"/> hands it back to be let go; asked for again later, it is decoded again from the stores. No drawn
/// LOD is touched. Engine-free and deterministic: the caller gives the time. Main thread only; no allocation per
/// call once its dictionary has grown.
/// </summary>
public sealed class DecodedMeshKeeper
{
    public static readonly TimeSpan DefaultKeep = TimeSpan.FromSeconds(10);

    private readonly Dictionary<Guid, TimeSpan> _lastNeeded = new();
    private readonly List<Guid> _due = new();

    public DecodedMeshKeeper(TimeSpan? keep = null)
    {
        Keep = keep ?? DefaultKeep;
        if (Keep < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(keep));
    }

    /// <summary>How long an asset's geometry is kept after it was last needed.</summary>
    public TimeSpan Keep { get; }

    /// <summary>Assets whose geometry is kept now (needed within <see cref="Keep"/>, or busy).</summary>
    public int Tracked => _lastNeeded.Count;

    /// <summary>Assets handed back to be let go since the keeper was made.</summary>
    public long LetGo { get; private set; }

    /// <summary>The asset's geometry was needed at <paramref name="now"/> (a LOD request, a preparation start or a build).</summary>
    public void Needed(Guid asset, TimeSpan now)
    {
        if (!_lastNeeded.TryGetValue(asset, out var at) || now > at)
            _lastNeeded[asset] = now;
    }

    /// <summary>
    /// Adds to <paramref name="into"/> every asset not needed for <see cref="Keep"/> or longer at <paramref name="now"/> and
    /// not <paramref name="busy"/> (a preparation in flight, an object waiting), and stops keeping them. A busy asset stays
    /// kept and is let go on the first call after it stops being busy. Returns how many were added.
    /// </summary>
    public int Collect(TimeSpan now, Func<Guid, bool> busy, List<Guid> into)
    {
        _due.Clear();
        foreach (var (asset, at) in _lastNeeded)
            if (now - at >= Keep && !busy(asset))
                _due.Add(asset);
        foreach (var asset in _due)
            _lastNeeded.Remove(asset);
        into.AddRange(_due);
        LetGo += _due.Count;
        return _due.Count;
    }

    /// <summary>Stops tracking one asset (its geometry went some other way).</summary>
    public void Forget(Guid asset) => _lastNeeded.Remove(asset);

    /// <summary>Stops tracking everything (a region change or dispose: the geometry is let go by the caller).</summary>
    public void Clear() => _lastNeeded.Clear();
}
