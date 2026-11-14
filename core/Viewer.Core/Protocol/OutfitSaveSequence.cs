// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Protocol;

/// <summary>One link to make in a saved outfit folder: the item it points to and the layer note the link carries.</summary>
internal sealed record SaveLink(Guid Target, string Description);

/// <summary>What a save ended with: whether every link is in the new folder, why not, and the folder's id.</summary>
internal sealed record SaveOutcome(bool Ok, string Reason, Guid Folder);

/// <summary>The grid-facing steps a saved outfit needs, so the order they run in can be tested without a grid.</summary>
internal interface IOutfitFolderOps
{
    /// <summary>Makes a folder and completes when the grid has confirmed it; <see cref="Guid.Empty"/> when it could not be made.</summary>
    Task<Guid> CreateFolderAsync(Guid parent, string name, bool isMyOutfits);

    /// <summary>Asks the grid to make the links in <paramref name="folder"/>.</summary>
    Task CreateLinksAsync(Guid folder, IReadOnlyList<SaveLink> links);

    /// <summary>Reads the folder back from the grid and returns the items its links point to.</summary>
    Task<IReadOnlySet<Guid>> ReadLinkTargetsAsync(Guid folder);
}

/// <summary>
/// The steps of saving the current look as an outfit folder: make My Outfits when the account has none, make the outfit
/// folder, make its links, read the folder back, and make again any link the grid lost. The official viewer makes an outfit's
/// links only after the grid has confirmed the new folder (LLAppearanceMgr::makeNewOutfitLinks waits for the folder-created
/// callback of LLInventoryModel::createNewCategory, then copies the links in), so nothing here waits on a timer for a folder
/// to appear.
/// </summary>
internal sealed class OutfitSaveSequence
{
    /// <summary>How many times a read-back is tried before the links that still look missing are made again.</summary>
    internal const int ReadsPerRound = 8;
    /// <summary>How many times the missing links are made again before the save fails.</summary>
    internal const int MaxRounds = 4;
    /// <summary>The spacing between two read-backs of a folder that does not yet show every link.</summary>
    internal static readonly TimeSpan ReadSpacing = TimeSpan.FromMilliseconds(250);

    private readonly IOutfitFolderOps _ops;
    private readonly Func<TimeSpan, Task> _delay;

    public OutfitSaveSequence(IOutfitFolderOps ops, Func<TimeSpan, Task>? delay = null)
    {
        _ops = ops;
        _delay = delay ?? Task.Delay;
    }

    public async Task<SaveOutcome> RunAsync(Guid rootId, Guid myOutfitsId, string name, IReadOnlyList<SaveLink> links)
    {
        var parent = myOutfitsId;
        if (parent == Guid.Empty)
        {
            parent = await _ops.CreateFolderAsync(rootId, "My Outfits", isMyOutfits: true).ConfigureAwait(false);   // the account has none yet
            if (parent == Guid.Empty)
                return new SaveOutcome(false, "failed", Guid.Empty);
        }
        // Each folder is made with a request the grid answers: the links go in only once it has.
        var folder = await _ops.CreateFolderAsync(parent, name, isMyOutfits: false).ConfigureAwait(false);
        if (folder == Guid.Empty)
            return new SaveOutcome(false, "failed", Guid.Empty);

        var missing = links.ToList();
        for (int round = 0; round < MaxRounds && missing.Count > 0; round++)
        {
            await _ops.CreateLinksAsync(folder, missing).ConfigureAwait(false);
            // The links are made with messages the grid handles in its own time: read the folder back until it shows them, and
            // make again only those that it still lacks after a fair number of reads (a slow answer is not a lost link).
            for (int read = 0; read < ReadsPerRound; read++)
            {
                var held = await _ops.ReadLinkTargetsAsync(folder).ConfigureAwait(false);
                missing = links.Where(l => !held.Contains(l.Target)).ToList();
                if (missing.Count == 0)
                    return new SaveOutcome(true, "", folder);
                await _delay(ReadSpacing).ConfigureAwait(false);
            }
        }
        return new SaveOutcome(false, "links_missing", folder);
    }
}
