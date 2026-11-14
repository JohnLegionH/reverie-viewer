// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using Viewer.Core.Assets;

namespace Viewer.Core.World;

/// <summary>
/// The built-in skies of the Environment menu, fetched by id as settings assets and kept once loaded. A choice is loaded the way
/// the official viewer's menu loads it (LLEnvironment::setEnvironment with a settings asset id); a grid that cannot serve the
/// asset, or serves something that is not a sky, fails the load and says why. Safe to call from any thread.
/// </summary>
public sealed class FixedSkyStore(Func<Guid, CancellationToken, Task<AssetFetchResult>>? fetch)
{
    private readonly Func<Guid, CancellationToken, Task<AssetFetchResult>>? _fetch = fetch;
    private readonly ConcurrentDictionary<SkyChoice, SkyFrame> _loaded = new();

    /// <summary>Why the last load failed (empty after one that worked).</summary>
    public string LastFailure { get; private set; } = "";

    /// <summary>Loads a choice; true when it is held afterwards. The region's own environment needs no load.</summary>
    public async Task<bool> LoadAsync(SkyChoice choice, CancellationToken ct = default)
    {
        if (choice == SkyChoice.Region) return true;
        if (KnownSkies.AssetId(choice) is not { } id) { LastFailure = "no asset for " + choice; return false; }
        if (_loaded.ContainsKey(choice)) return true;
        if (_fetch is null) { LastFailure = "this session cannot fetch settings assets"; return false; }
        AssetFetchResult r;
        try { r = await _fetch(id, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            LastFailure = "fetch error: " + e.GetType().Name;
            return false;
        }
        if (r.Status != AssetFetchStatus.Ok) { LastFailure = "the grid answered " + r.Status; return false; }
        var frame = SettingsSky.Parse(r.Bytes.Span, out var why);
        if (frame is null) { LastFailure = "not a sky: " + why; return false; }
        _loaded[choice] = frame;
        LastFailure = "";
        return true;
    }

    public bool TryGet(SkyChoice choice, out SkyFrame? frame) => _loaded.TryGetValue(choice, out frame);
}
